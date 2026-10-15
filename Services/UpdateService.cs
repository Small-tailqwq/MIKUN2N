using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MikuN2N.Services;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Failed
}

public sealed record UpdateRelease(
    string Version,
    string Notes,
    string PageUrl,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string Sha256);

/// <summary>
/// Finds, downloads and installs releases published on the project's GitHub page.
/// Everything that can fail slowly (network, hash, extraction) happens while the
/// connection is still up; only the file swap runs after n3n-edge has stopped.
/// </summary>
public sealed partial class UpdateService
{
    public const string AfterUpdateArgument = "--after-update";
    private const string ExecutableName = "MikuN2N.exe";
    private const string OldSuffix = ".update-old";
    private static readonly HttpClient Http = CreateClient();
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly string? _repository;
    private readonly string _currentVersion;
    private readonly string _runtime;
    private readonly string _updatesRoot;
    private readonly Func<DateTimeOffset> _now;
    private Task? _running;
    private DateTimeOffset _nextCheck;
    private DateTimeOffset _manifestRetry;
    private DateTimeOffset _apiRetry;
    private DateTimeOffset _downloadRetry;

    public UpdateService() : this(Http, Repository, BuildIdentity.Version, ResolveRuntime(), UpdatesRoot) { }

    // Tests inject HTTP and staging paths; production always uses the official HTTPS endpoints.
    internal UpdateService(HttpClient http, string? repository, string currentVersion, string runtime,
        string updatesRoot, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _repository = repository is not null && RepositoryName().IsMatch(repository) ? repository : null;
        _currentVersion = currentVersion;
        _runtime = runtime == "framework-dependent" ? runtime : "self-contained";
        _updatesRoot = updatesRoot;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? StateChanged;

    /// <summary>"owner/name" from the build; forks override it with -p:UpdateRepository=...</summary>
    public static string? Repository { get; } = ResolveRepository();

    public static string ReleasesPage =>
        Repository is null ? string.Empty : $"https://github.com/{Repository}/releases";

    public bool IsConfigured => _repository is not null;
    public UpdateStage Stage { get; private set; }
    public UpdateRelease? Available { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    private string? StagedDirectory { get; set; }

    private static string UpdatesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "updates");

    private static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Checks GitHub and, when a newer release exists, downloads and stages it.
    /// Concurrent callers share one run; a release that is already staged is kept.
    /// </summary>
    public Task CheckAndPrepareAsync(CancellationToken cancellationToken = default, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return _running.WaitAsync(cancellationToken);
            }
            if (!IsConfigured || Stage == UpdateStage.Ready || !force && _now() < _nextCheck)
            {
                return Task.CompletedTask;
            }
            // A caller can stop waiting without cancelling other callers' shared download.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion.Task;
            _ = CompleteSharedCheckAsync(completion);
            return _running.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteSharedCheckAsync(TaskCompletionSource completion)
    {
        try
        {
            await CheckCoreAsync();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task CheckCoreAsync()
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            if (Stage == UpdateStage.Ready)
            {
                return;
            }
            SetState(UpdateStage.Checking);
            var release = await FetchLatestAsync(cancellationToken);
            if (release is null)
            {
                Available = null;
                _nextCheck = _now().AddMinutes(15);
                SetState(UpdateStage.UpToDate);
                return;
            }
            Available = release;
            SetState(UpdateStage.Downloading);
            StagedDirectory = await StageAsync(release, cancellationToken);
            SetState(UpdateStage.Ready);
        }
        catch (Exception exception)
        {
            _nextCheck = exception is RateLimitException limited ? limited.RetryAt : _now().AddMinutes(1);
            SetState(UpdateStage.Failed, Describe(exception));
        }
    }

    /// <summary>
    /// Only a published single-file package can be replaced in place: a development
    /// build would overwrite bin/ with release output, and a protected folder cannot
    /// be written without elevation.
    /// </summary>
    public static bool CanInstallInPlace(out string reason)
    {
        // An empty location is exactly how a single-file publish is recognised.
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
#pragma warning restore IL3000
        {
            reason = "当前是开发版，不能自动替换，请前往下载页获取新版本。";
            return false;
        }
        try
        {
            var probe = Path.Combine(InstallDirectory, $".update-probe-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            reason = "程序所在的文件夹没有写入权限，请前往下载页手动更新，或把程序放到自己的文件夹里。";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Replaces the installed files with the staged release. Must run after n3n-edge
    /// has stopped. Windows allows renaming a running executable but not overwriting
    /// it, so every existing file is moved aside first; any failure moves them back.
    /// </summary>
    public void InstallStaged()
    {
        if (Stage != UpdateStage.Ready || StagedDirectory is null)
        {
            throw new InvalidOperationException("没有已准备好的更新。");
        }
        ReplaceFiles(StagedDirectory, InstallDirectory);
    }

    internal static void ReplaceFiles(string stagedDirectory, string installDirectory)
    {
        var replaced = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagedDirectory, source);
                var target = Path.Combine(installDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = FreeBackupPath(target);
                    File.Move(target, backup);
                }
                replaced.Add((target, backup));
                File.Copy(source, target);
            }
        }
        catch
        {
            for (var index = replaced.Count - 1; index >= 0; index--)
            {
                var (target, backup) = replaced[index];
                try
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    if (backup is not null)
                    {
                        File.Move(backup, target);
                    }
                }
                catch (Exception rollbackError)
                {
                    CrashLogService.Record("更新回滚", rollbackError, fatal: false);
                }
            }
            throw;
        }
    }

    /// <summary>Starts the installed executable, which waits for this process to exit first.</summary>
    public static void Relaunch()
    {
        Process.Start(new ProcessStartInfo(Path.Combine(InstallDirectory, ExecutableName))
        {
            UseShellExecute = false,
            WorkingDirectory = InstallDirectory,
            ArgumentList = { AfterUpdateArgument, Environment.ProcessId.ToString() }
        });
    }

    /// <summary>
    /// Called before the single-instance check: the previous process still holds the
    /// mutex while it shuts down.
    /// </summary>
    public static void WaitForPreviousProcess(string[] args)
    {
        var index = Array.IndexOf(args, AfterUpdateArgument);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Already exited.
            }
        }
    }

    /// <summary>
    /// Called only by the instance that owns the single-instance mutex; a second launch
    /// must not delete an update the running instance has staged.
    /// </summary>
    public static void CleanUpAfterStart()
    {
        RemoveLeftovers(InstallDirectory);
        try
        {
            if (Directory.Exists(UpdatesRoot))
            {
                Directory.Delete(UpdatesRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover file is retried on the next start.
        }
    }

    internal static void RemoveLeftovers(string installDirectory)
    {
        try
        {
            // Only inspect known package directories: the program may sit in Downloads.
            foreach (var directory in new[]
            {
                installDirectory,
                Path.Combine(installDirectory, "Runtime"),
                Path.Combine(installDirectory, "Runtime", "changes")
            })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }
                foreach (var old in Directory.EnumerateFiles(directory, "*" + OldSuffix + "*"))
                {
                    TryDelete(old);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover file is retried on the next start.
        }
    }

    private async Task<UpdateRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        Exception? manifestError = null;
        if (_now() >= _manifestRetry)
        {
            try
            {
                using var manifest = await _http.GetAsync(
                    $"https://github.com/{_repository}/releases/latest/download/update.json", cancellationToken);
                CheckRateLimit(manifest, ref _manifestRetry);
                if (manifest.StatusCode != HttpStatusCode.NotFound)
                {
                    manifest.EnsureSuccessStatusCode();
                    var json = await manifest.Content.ReadAsStringAsync(cancellationToken);
                    return ParseManifest(json);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                             JsonException or UpdateException or InvalidOperationException or
                                             KeyNotFoundException or FormatException or OverflowException)
            {
                manifestError = exception;
            }
        }
        else
        {
            manifestError = new RateLimitException(_manifestRetry);
        }
        if (_now() < _apiRetry)
        {
            throw manifestError is RateLimitException manifestLimit && manifestLimit.RetryAt < _apiRetry
                ? manifestLimit : new RateLimitException(_apiRetry);
        }
        try
        {
            return await FetchLatestFromApiAsync(cancellationToken);
        }
        catch (RateLimitException apiLimit) when (manifestError is RateLimitException manifestLimit)
        {
            // Retry when either independent endpoint becomes available again.
            throw new RateLimitException(apiLimit.RetryAt < manifestLimit.RetryAt ? apiLimit.RetryAt : manifestLimit.RetryAt);
        }
    }

    private UpdateRelease? ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1)
        {
            throw new UpdateException("更新清单格式暂不支持，请前往下载页查看。");
        }
        var latest = ParseReleaseVersion(root.GetProperty("version").GetString());
        var name = ExpectedPackageName(latest);
        JsonElement? selected = null;
        foreach (var package in root.GetProperty("packages").EnumerateArray())
        {
            if (package.GetProperty("runtime").GetString() == _runtime &&
                package.GetProperty("name").GetString() == name)
            {
                if (selected is not null)
                {
                    throw new UpdateException("更新清单包含重复程序包，请前往下载页查看。");
                }
                selected = package;
            }
        }
        if (selected is not { } found)
        {
            throw new UpdateException("更新清单缺少适合当前程序的安装包，请前往下载页查看。");
        }
        var checksum = found.GetProperty("sha256").GetString();
        var url = found.GetProperty("url").GetString();
        var page = root.GetProperty("pageUrl").GetString();
        var size = found.GetProperty("size").GetInt64();
        ValidatePackage(latest, name, url, size, checksum);
        if (!IsReleaseUrl(page, "tag/"))
        {
            throw new UpdateException("更新清单的下载页地址无效，请前往下载页查看。");
        }
        var notes = root.GetProperty("notes").GetString() ?? string.Empty;
        return IsNewer(latest) ? new UpdateRelease(latest, notes,
            page!, name, url!, size, checksum!.ToLowerInvariant()) : null;
    }

    private async Task<UpdateRelease?> FetchLatestFromApiAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"https://api.github.com/repos/{_repository}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        CheckRateLimit(response, ref _apiRetry);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var latest = ParseReleaseVersion(tag);
        if (!IsNewer(latest))
        {
            return null;
        }

        JsonElement? package = null;
        string? sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (name.Equals(ExpectedPackageName(latest), StringComparison.Ordinal))
            {
                if (package is not null)
                {
                    throw new UpdateException("发布说明包含重复程序包，请前往下载页查看。");
                }
                package = asset;
            }
            else if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sumsUrl = asset.GetProperty("browser_download_url").GetString();
            }
        }
        if (package is not { } found)
        {
            throw new UpdateException($"新版本 {latest} 没有附带 Windows 程序包，请前往下载页查看。");
        }
        var assetName = found.GetProperty("name").GetString()!;
        var sha256 = found.TryGetProperty("digest", out var digest) &&
                     digest.GetString() is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value["sha256:".Length..]
            : sumsUrl is null ? null : await ReadChecksumAsync(sumsUrl, assetName, cancellationToken);
        if (sha256 is null || !Sha256Hex().IsMatch(sha256))
        {
            throw new UpdateException($"新版本 {latest} 缺少校验信息，为安全起见没有自动下载，请前往下载页查看。");
        }
        var assetUrl = found.GetProperty("browser_download_url").GetString();
        var size = found.GetProperty("size").GetInt64();
        ValidatePackage(latest, assetName, assetUrl, size, sha256);
        var pageUrl = root.GetProperty("html_url").GetString();
        if (!IsReleaseUrl(pageUrl, "tag/"))
        {
            throw new UpdateException("发布说明的下载页地址无效，请前往下载页查看。");
        }
        return new UpdateRelease(
            latest,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            pageUrl!,
            assetName,
            assetUrl!,
            size,
            sha256.ToLowerInvariant());
    }

    private async Task<string?> ReadChecksumAsync(string url, string assetName, CancellationToken cancellationToken)
    {
        if (!IsReleaseUrl(url, "download/"))
        {
            throw new UpdateException("发布说明的校验地址无效，请前往下载页查看。");
        }
        using var response = await _http.GetAsync(url, cancellationToken);
        CheckRateLimit(response, ref _apiRetry);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }
        return null;
    }

    private static string ParseReleaseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !AppVersion.TryParse(text, out var version) || version.Build < 0)
        {
            throw new UpdateException("发布说明的版本无效，请前往下载页查看。");
        }
        return version.ToString();
    }

    private bool IsNewer(string latest) =>
        AppVersion.TryParse(latest, out var parsed) && AppVersion.TryParse(_currentVersion, out var current)
            ? parsed.CompareTo(current) > 0
            : throw new UpdateException("无法识别当前程序版本，请前往下载页查看。");

    private string ExpectedPackageName(string version) => _runtime == "framework-dependent"
        ? $"MikuN2N-{version}-win-x64-framework-dependent.zip"
        : $"MikuN2N-{version}-win-x64.zip";

    private bool TryGetReleasePath(string? text, out string path)
    {
        path = string.Empty;
        var repositoryPrefix = $"/{_repository}/";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // GitHub canonicalises owner/repository casing independently of the build metadata.
        path = uri.AbsolutePath[repositoryPrefix.Length..];
        return path.StartsWith("releases/", StringComparison.Ordinal);
    }

    private bool IsReleaseUrl(string? text, string suffix) =>
        TryGetReleasePath(text, out var path) && path.StartsWith("releases/" + suffix, StringComparison.Ordinal);

    private void ValidatePackage(string version, string name, string? url, long size, string? sha256)
    {
        if (size <= 0 || sha256 is null || !Sha256Hex().IsMatch(sha256) ||
            !TryGetReleasePath(url, out var path) ||
            !(path.Equals($"releases/download/{version}/{name}", StringComparison.Ordinal) ||
              path.Equals($"releases/download/v{version}/{name}", StringComparison.Ordinal)))
        {
            throw new UpdateException("程序包地址、大小或校验信息无效，为安全起见没有自动下载，请前往下载页查看。");
        }
    }

    private void CheckRateLimit(HttpResponseMessage response, ref DateTimeOffset retryAt)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return;
        }
        var now = _now();
        DateTimeOffset? deadline = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delay)
        {
            deadline = now.Add(delay);
        }
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var seconds))
        {
            try
            {
                var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (deadline is null || reset > deadline)
                {
                    deadline = reset;
                }
            }
            catch (ArgumentOutOfRangeException) { }
        }
        retryAt = deadline > now ? deadline.Value : now.AddMinutes(15);
        throw new RateLimitException(retryAt);
    }

    private async Task<string> StageAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(_updatesRoot, release.Version);
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        Directory.CreateDirectory(workDirectory);
        var archive = Path.Combine(workDirectory, release.AssetName);
        await DownloadAsync(release, archive, cancellationToken);
        return ExtractPackage(archive, Path.Combine(workDirectory, "files"), release.Version);
    }

    /// <summary>Unpacks a downloaded package and returns the folder that holds the program.</summary>
    internal static string ExtractPackage(string archive, string staged, string expectedVersion)
    {
        ExtractSafely(archive, staged);
        // Packages zipped with a single top-level folder are accepted as well.
        var root = File.Exists(Path.Combine(staged, ExecutableName))
            ? staged
            : Directory.GetDirectories(staged) is [var only] && File.Exists(Path.Combine(only, ExecutableName))
                ? only
                : throw new UpdateException("下载的程序包内容不完整，已放弃这次更新。");
        if (!File.Exists(Path.Combine(root, "Runtime", "n3n-edge.exe")))
        {
            throw new UpdateException("下载的程序包缺少虚拟网络组件，已放弃这次更新。");
        }
        var packaged = FileVersionInfo.GetVersionInfo(Path.Combine(root, ExecutableName)).ProductVersion;
        if (!AppVersion.TryParse(packaged ?? string.Empty, out var packagedVersion) ||
            packagedVersion.ToString() != expectedVersion)
        {
            throw new UpdateException($"程序包的版本（{packaged}）与发布说明（{expectedVersion}）不一致，已放弃这次更新。");
        }
        return root;
    }

    private async Task DownloadAsync(UpdateRelease release, string path, CancellationToken cancellationToken)
    {
        if (_now() < _downloadRetry)
        {
            throw new RateLimitException(_downloadRetry);
        }
        using var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        CheckRateLimit(response, ref _downloadRetry);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.AssetSize;
        if (total != release.AssetSize)
        {
            throw new UpdateException("下载的文件大小与发布说明不一致，已放弃这次更新。");
        }
        long received = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            var lastReport = 0.0;
            // HttpClient.Timeout stops at the response headers; a stalled body would
            // otherwise leave the download "in progress" until the program restarts.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int read;
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new UpdateException("下载长时间没有进展，已暂停。请检查网络后再试。");
                }
                if (read == 0)
                {
                    break;
                }
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (received > release.AssetSize)
                {
                    throw new UpdateException("下载的文件超出发布大小，已放弃这次更新。");
                }
                var progress = total > 0 ? Math.Min(1.0, (double)received / total) : 0;
                if (progress - lastReport >= 0.01)
                {
                    lastReport = progress;
                    Progress = progress;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (received != release.AssetSize || actual != release.Sha256)
        {
            throw new UpdateException("下载的文件校验失败（可能被网络中途损坏或篡改），已放弃这次更新。");
        }
    }

    private static void ExtractSafely(string archive, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            // Reject entries such as "../x" that would write outside the staging folder.
            if (entry.FullName.Contains(':') || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("下载的程序包包含异常路径，已放弃这次更新。");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static string FreeBackupPath(string target)
    {
        var backup = target + OldSuffix;
        if (File.Exists(backup) && !TryDelete(backup))
        {
            // Still locked by a process from an earlier update; pick another name.
            backup = $"{target}{OldSuffix}-{Guid.NewGuid():N}";
        }
        return backup;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetState(UpdateStage stage, string? error = null)
    {
        Stage = stage;
        Error = error;
        if (stage != UpdateStage.Downloading)
        {
            Progress = 0;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(Exception exception) => exception switch
    {
        UpdateException => exception.Message,
        HttpRequestException or TaskCanceledException => "暂时连不上 GitHub，请检查网络后再试。",
        IOException or UnauthorizedAccessException => $"无法保存更新文件：{exception.Message}",
        _ => $"检查更新时出错：{exception.Message}"
    };

    private static string? ResolveRepository()
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateRepository")
            ?.Value;
        return value is not null && RepositoryName().IsMatch(value) ? value : null;
    }

    private static string ResolveRuntime() => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "UpdateRuntime")?.Value == "framework-dependent"
            ? "framework-dependent" : "self-contained";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MikuN2N", BuildIdentity.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryName();

    internal class UpdateException(string message) : Exception(message);

    private sealed class RateLimitException(DateTimeOffset retryAt)
        : UpdateException($"GitHub 暂时限制了访问次数，请在 {retryAt.ToLocalTime():HH:mm} 后重试。")
    {
        public DateTimeOffset RetryAt { get; } = retryAt;
    }
}

/// <summary>Release versions such as "0.5.8-5": a three-part base plus a build number.</summary>
public readonly record struct AppVersion(Version Base, int Build) : IComparable<AppVersion>
{
    public static bool TryParse(string text, out AppVersion version)
    {
        version = default;
        var trimmed = text.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }
        var dash = trimmed.IndexOf('-');
        var basePart = dash >= 0 ? trimmed[..dash] : trimmed;
        var build = 0;
        if (!Version.TryParse(basePart, out var parsed) ||
            dash >= 0 && !int.TryParse(trimmed[(dash + 1)..], out build))
        {
            return false;
        }
        version = new AppVersion(new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)), build);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var result = Base.CompareTo(other.Base);
        return result != 0 ? result : Build.CompareTo(other.Build);
    }

    public override string ToString() => Build > 0 ? $"{Base}-{Build}" : Base.ToString();
}
