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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? StateChanged;

    /// <summary>"owner/name" from the build; forks override it with -p:UpdateRepository=...</summary>
    public static string? Repository { get; } = ResolveRepository();

    public static string ReleasesPage =>
        Repository is null ? string.Empty : $"https://github.com/{Repository}/releases";

    public bool IsConfigured => Repository is not null;
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
    public async Task CheckAndPrepareAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || !await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }
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
                SetState(UpdateStage.UpToDate);
                return;
            }
            Available = release;
            SetState(UpdateStage.Downloading);
            StagedDirectory = await StageAsync(release, cancellationToken);
            SetState(UpdateStage.Ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(UpdateStage.Idle);
        }
        catch (Exception exception)
        {
            SetState(UpdateStage.Failed, Describe(exception));
        }
        finally
        {
            _gate.Release();
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
            // Packages only hold files at the root and in Runtime/. Never recurse: the
            // program may sit in a large folder such as Downloads.
            foreach (var directory in new[] { installDirectory, Path.Combine(installDirectory, "Runtime") })
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
        using var response = await Http.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new UpdateException("GitHub 暂时限制了访问次数，请过一会儿再试。");
        }
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
        if (!AppVersion.TryParse(tag, out var latest) ||
            !AppVersion.TryParse(BuildIdentity.Version, out var current) ||
            latest.CompareTo(current) <= 0)
        {
            return null;
        }

        JsonElement? package = null;
        string? sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (PackageName().IsMatch(name))
            {
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
        return new UpdateRelease(
            latest.ToString(),
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            root.GetProperty("html_url").GetString() ?? ReleasesPage,
            assetName,
            found.GetProperty("browser_download_url").GetString()!,
            found.GetProperty("size").GetInt64(),
            sha256.ToLowerInvariant());
    }

    private static async Task<string?> ReadChecksumAsync(string url, string assetName, CancellationToken cancellationToken)
    {
        var text = await Http.GetStringAsync(url, cancellationToken);
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

    private async Task<string> StageAsync(UpdateRelease release, CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(UpdatesRoot, release.Version);
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
        using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.AssetSize;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
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
        if (actual != release.Sha256)
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
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MikuN2N", BuildIdentity.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    [GeneratedRegex(@"^MikuN2N-.+-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageName();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex RepositoryName();

    internal sealed class UpdateException(string message) : Exception(message);
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
