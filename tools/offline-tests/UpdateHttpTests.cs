using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MikuN2N.Services;

internal static class UpdateHttpTests
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.Combine(Path.GetFullPath(directory), "updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("UPDATE TEST ROOT: " + root);
        const string repository = "fixture/updater";
        const string version = "0.0.1-1";
        var page = $"https://github.com/{repository}/releases/tag/{version}";
        var download = $"https://github.com/{repository}/releases/download/{version}/";
        var sc = $"MikuN2N-{version}-win-x64.zip";
        var fdd = $"MikuN2N-{version}-win-x64-framework-dependent.zip";
        var archive = Path.Combine(root, "fixture.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Environment.ProcessPath!, "MikuN2N.exe");
            using var writer = new StreamWriter(zip.CreateEntry("Runtime/n3n-edge.exe").Open());
            writer.Write("fixture-only; never executable");
        }
        var bytes = File.ReadAllBytes(archive);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        object Package(string runtime, string name, string? hash = null, string? url = null, long? size = null) =>
            new { runtime, name, url = url ?? download + name, size = size ?? bytes.Length, sha256 = hash ?? sha };
        string Manifest(params object[] packages) => JsonSerializer.Serialize(new
        {
            schema = 1, version, notes = "离线测试", pageUrl = page, packages
        });
        string Api(bool digest = true, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
        {
            tag_name = version, body = "离线测试", html_url = page, draft, prerelease,
            // FDD deliberately follows SC: -10's regex must never choose FDD.
            assets = new object[]
            {
                new { name = sc, browser_download_url = download + sc, size = bytes.Length, digest = digest ? "sha256:" + sha : null },
                new { name = fdd, browser_download_url = download + fdd, size = bytes.Length, digest = digest ? "sha256:" + sha : null },
                new { name = "SHA256SUMS", browser_download_url = download + "SHA256SUMS", size = 0, digest = (string?)null }
            }
        });
        HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        HttpResponseMessage Data(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        UpdateService Service(FakeHttp handler, string runtime = "self-contained") =>
            new(new HttpClient(handler), repository, "0.0.0-1", runtime,
                Path.Combine(root, Guid.NewGuid().ToString("N")), () => now);
        FakeHttp Transport(Func<HttpRequestMessage, HttpResponseMessage> metadata) => new(request =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(".zip") ? Data(bytes) : metadata(request)));

        foreach (var runtime in new[] { "self-contained", "framework-dependent", "unknown" })
        {
            var handler = Transport(_ => Json(Manifest(Package("self-contained", sc), Package("framework-dependent", fdd))));
            var service = Service(handler, runtime);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.CheckAndPrepareAsync()));
            Check(service.Stage == UpdateStage.Ready && service.Available!.AssetName == (runtime == "framework-dependent" ? fdd : sc),
                runtime + " uses the matching manifest package");
            Check(handler.Paths.Count == 2 && handler.Paths.All(path => !path.Contains("api.github.com")), "manifest avoids API and single-flights staging");
            await service.CheckAndPrepareAsync(force: true);
            Check(handler.Paths.Count == 2, "ready package is retained");
        }
        Pass("SC/FDD/unknown-runtime selection, manifest-only staging, shared checks, ready retention");

        foreach (var digest in new[] { true, false })
        {
            var handler = Transport(request => request.RequestUri!.Host == "api.github.com" ? Json(Api(digest)) :
                request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS") ? new(HttpStatusCode.OK)
                { Content = new StringContent($"{sha}  {sc}\n{sha} *{fdd}\n") } : new(HttpStatusCode.NotFound));
            var service = Service(handler, "framework-dependent");
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Ready && service.Available!.AssetName == fdd, "REST fallback selects exact runtime and checksum");
        }
        Pass("404 manifest REST fallback with digest and SHA256SUMS");

        foreach (var manifestFirst in new[] { true, false })
        {
            var handler = Transport(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                    return Json(Api(digest: false).Replace("fixture/updater", "Fixture/UPDATER", StringComparison.Ordinal));
                if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS"))
                    return new(HttpStatusCode.OK) { Content = new StringContent($"{sha}  {sc}") };
                return manifestFirst
                    ? Json(Manifest(Package("self-contained", sc)).Replace("fixture/updater", "Fixture/UPDATER", StringComparison.Ordinal))
                    : new(HttpStatusCode.NotFound);
            });
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Ready, "canonical owner/repository casing accepted by manifest/API/page/checksum/download");
        }
        foreach (var invalidApi in new[]
        {
            Api().Replace(sc, "unrelated.zip", StringComparison.Ordinal),
            Api().Replace("sha256:" + sha, "sha256:bad", StringComparison.Ordinal),
            Api().Replace(download, "https://github.com/other/repository/releases/download/" + version + "/", StringComparison.Ordinal),
            Api().Replace(download, "http://github.com/fixture/updater/releases/download/" + version + "/", StringComparison.Ordinal),
            Api().Replace(sc, sc.ToUpperInvariant(), StringComparison.Ordinal)
        })
        {
            var handler = Transport(request => request.RequestUri!.Host == "api.github.com" ? Json(invalidApi) : new(HttpStatusCode.NotFound));
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Failed && handler.Paths.All(path => !path.EndsWith(".zip")), "invalid REST package fails without downloading");
        }
        foreach (var status in new[] { "404", "draft", "prerelease" })
        {
            var handler = Transport(request => request.RequestUri!.Host == "api.github.com" && status != "404"
                ? Json(Api(draft: status == "draft", prerelease: status == "prerelease")) : new(HttpStatusCode.NotFound));
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.UpToDate && handler.Paths.Count == 2, "no formal release is distinct from failed assets");
        }
        Pass("canonical repository casing, exact asset name, official HTTPS, invalid REST packages and absent/draft/prerelease distinctions");

        foreach (var bad in new[]
        {
            "{}", "not json", Manifest(Package("self-contained", sc, hash: "bad")),
            Manifest(Package("self-contained", sc, url: "http://github.com/fixture/updater/evil.zip")),
            Manifest(Package("self-contained", sc, url: "https://untrusted.invalid/evil.zip")),
            Manifest(Package("self-contained", sc), Package("self-contained", sc)),
            Manifest(Package("self-contained", sc, size: 0)),
            Manifest(Package("framework-dependent", fdd))
        })
        {
            var handler = Transport(request => Json(request.RequestUri!.Host == "api.github.com" ? Api() : bad));
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Ready && handler.Paths.Any(path => path.Contains("api.github.com")), "invalid manifest falls back");
        }
        Pass("malformed/missing/duplicate/HTTP/external/zero-size manifest rejects and falls back");

        var upToDateJson = Manifest(Package("self-contained", sc));
        var cacheHandler = Transport(_ => Json(upToDateJson));
        var cached = new UpdateService(new HttpClient(cacheHandler), repository, version, "self-contained",
            Path.Combine(root, "cache"), () => now);
        await cached.CheckAndPrepareAsync();
        await cached.CheckAndPrepareAsync();
        Check(cached.Stage == UpdateStage.UpToDate && cacheHandler.Paths.Count == 1, "success TTL suppresses repeat checks");
        await cached.CheckAndPrepareAsync(force: true);
        Check(cacheHandler.Paths.Count == 2, "manual force bypasses success TTL");
        now = now.AddMinutes(16);
        await cached.CheckAndPrepareAsync();
        Check(cacheHandler.Paths.Count == 3, "TTL expires");
        var brokenCurrent = Transport(request => request.RequestUri!.Host == "api.github.com" ? Json(Api()) : Json("{\"schema\":1,\"version\":\"0.0.1-1\"}"));
        var brokenCurrentService = new UpdateService(new HttpClient(brokenCurrent), repository, version, "self-contained",
            Path.Combine(root, "bad-current"), () => now);
        await brokenCurrentService.CheckAndPrepareAsync();
        Check(brokenCurrent.Paths.Count == 2, "unhealthy current-version manifest cannot suppress fallback");
        Pass("healthy no-update TTL, manual force, TTL expiry, unhealthy same-version fallback");

        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            var deadline = now.AddMinutes(20);
            var handler = Transport(_ =>
            {
                var response = new HttpResponseMessage(code);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                response.Headers.Add("X-RateLimit-Reset", deadline.ToUnixTimeSeconds().ToString());
                return response;
            });
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Failed && service.Error!.Contains("限制"), "rate limit is distinguished");
            await service.CheckAndPrepareAsync(force: true);
            Check(handler.Paths.Count == 2, "manual force respects each source's cooldown");
            now = now.AddMinutes(6);
            await service.CheckAndPrepareAsync(force: true);
            Check(handler.Paths.Count == 2, "later reset wins over Retry-After");
            now = deadline.AddSeconds(1);
            await service.CheckAndPrepareAsync();
            Check(handler.Paths.Count == 4, "rate-limit expiry retries");
        }
        var networkHandler = new FakeHttp(_ => throw new HttpRequestException("offline fixture"));
        var network = Service(networkHandler);
        await network.CheckAndPrepareAsync();
        Check(network.Stage == UpdateStage.Failed && network.Error!.Contains("检查网络"), "network failure is not rate limit");
        await network.CheckAndPrepareAsync();
        Check(networkHandler.Paths.Count == 2, "network failure has short TTL");
        now = now.AddMinutes(2);
        await network.CheckAndPrepareAsync();
        Check(networkHandler.Paths.Count == 4, "network retry TTL expires");
        Pass("403/429 Retry-After and reset cooldowns, force safety, network-error distinction and retry TTL");

        var staticLimited = Transport(request => request.RequestUri!.Host == "api.github.com" ? Json(Api()) : new(HttpStatusCode.TooManyRequests));
        var staticLimitedService = Service(staticLimited);
        await staticLimitedService.CheckAndPrepareAsync();
        Check(staticLimitedService.Stage == UpdateStage.Ready, "API fallback remains usable when static manifest alone is limited");
        var metadataCalls = 0;
        var apiLimited = Transport(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddHours(1));
                return response;
            }
            return ++metadataCalls == 1 ? new(HttpStatusCode.NotFound) : Json(upToDateJson);
        });
        var apiLimitedService = Service(apiLimited);
        await apiLimitedService.CheckAndPrepareAsync();
        Check(apiLimitedService.Stage == UpdateStage.Failed, "API-only cooldown begins");
        await apiLimitedService.CheckAndPrepareAsync(force: true);
        Check(apiLimitedService.Stage == UpdateStage.Ready && apiLimited.Paths.Count(path => path.Contains("api.github.com")) == 1,
            "static manifest succeeds while API cools down");
        var downloadCalls = 0;
        var downloadLimited = new FakeHttp(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(".zip")) return Task.FromResult(Json(upToDateJson));
            downloadCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        var downloadLimitedService = Service(downloadLimited);
        await downloadLimitedService.CheckAndPrepareAsync();
        await downloadLimitedService.CheckAndPrepareAsync(force: true);
        Check(downloadLimitedService.Stage == UpdateStage.Failed && downloadCalls == 1, "manual force respects asset cooldown");
        Pass("independent static/API cooldowns, Retry-After date and download cooldown under force");

        foreach (var kind in new[] { "sha", "size", "version", "zip-slip", "missing-edge", "ads" })
        {
            var damaged = bytes;
            var declaredSize = bytes.LongLength;
            var hash = sha;
            if (kind == "sha") hash = new string('0', 64);
            if (kind == "size") declaredSize++;
            if (kind is "version" or "zip-slip" or "missing-edge" or "ads")
            {
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (kind == "version")
                    {
                        using var writer = new StreamWriter(zip.CreateEntry("MikuN2N.exe").Open());
                        writer.Write("invalid executable");
                    }
                    else zip.CreateEntryFromFile(Environment.ProcessPath!, "MikuN2N.exe");
                    if (kind != "missing-edge") zip.CreateEntry("Runtime/n3n-edge.exe");
                    if (kind == "zip-slip") zip.CreateEntry("../escaped.txt");
                    if (kind == "ads") zip.CreateEntry("MikuN2N.exe:hidden");
                }
                damaged = memory.ToArray();
                declaredSize = damaged.LongLength;
                hash = Convert.ToHexString(SHA256.HashData(damaged)).ToLowerInvariant();
            }
            var handler = new FakeHttp(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(".zip") ? Data(damaged) :
                Json(Manifest(Package("self-contained", sc, hash, size: declaredSize)))));
            var service = Service(handler);
            await service.CheckAndPrepareAsync();
            Check(service.Stage == UpdateStage.Failed, kind + " must not stage");
        }
        Pass("hash, size, executable-version, zip-slip, missing-edge and NTFS-stream refusals");

        var wrongVersionHandler = Transport(_ => Json(upToDateJson.Replace(version, "0.0.2-1", StringComparison.Ordinal)));
        var wrongVersionService = Service(wrongVersionHandler);
        await wrongVersionService.CheckAndPrepareAsync();
        Check(wrongVersionService.Stage == UpdateStage.Failed && wrongVersionService.Error!.Contains("不一致"),
            "valid executable with a different ProductVersion is rejected");
        Pass("genuine EXE ProductVersion mismatch is refused after successful SHA256 verification");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flightHandler = new FakeHttp(async request =>
        {
            entered.TrySetResult();
            await unblock.Task;
            return request.RequestUri!.AbsolutePath.EndsWith(".zip") ? Data(bytes) : Json(upToDateJson);
        });
        var flight = Service(flightHandler);
        Task? reentrant = null;
        flight.StateChanged += (_, _) =>
        {
            if (flight.Stage == UpdateStage.Checking) reentrant = flight.CheckAndPrepareAsync();
        };
        using var cancel = new CancellationTokenSource();
        var first = flight.CheckAndPrepareAsync(cancel.Token);
        await entered.Task;
        var second = flight.CheckAndPrepareAsync();
        cancel.Cancel();
        try { await first; throw new Exception("cancelled caller unexpectedly completed"); }
        catch (OperationCanceledException) { }
        Check(!second.IsCompleted, "concurrent caller awaits actual shared completion");
        unblock.SetResult();
        await second;
        await reentrant!;
        Check(flight.Stage == UpdateStage.Ready && flightHandler.Paths.Count == 2, "cancelling waiter and reentrant event do not duplicate shared work");
        Pass("single-flight waiters share completion and cancellation is isolated");

        await VerifyReplacementAsync(root);
        Console.WriteLine("UPDATE TEST ROOT: " + root);
    }

    internal static async Task VerifyReplacementAsync(string root)
    {
        var install = Path.Combine(root, "running-install");
        var staged = Path.Combine(root, "swap");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staged);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(install, Path.GetFileName(file)));
        var executable = Path.Combine(install, "MikuN2N.exe");
        File.Copy(Environment.ProcessPath!, executable, overwrite: true);
        File.WriteAllText(Path.Combine(staged, "MikuN2N.exe"), "replacement fixture");
        var ready = Path.Combine(install, "ready");
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "--update-hold", ready }
        })!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready))
            {
                Check(!child.HasExited, "isolated console fixture starts");
                await Task.Delay(25, timeout.Token);
            }
            UpdateService.ReplaceFiles(staged, install);
            Check(!child.HasExited && File.ReadAllText(executable) == "replacement fixture" &&
                File.Exists(executable + ".update-old"), "running console EXE renamed and replaced");
            UpdateService.RemoveLeftovers(install);
            Check(File.Exists(executable + ".update-old"), "locked backup survives cleanup");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
        // Windows may release the image section shortly after process-exit notification.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            do
            {
                UpdateService.RemoveLeftovers(install);
                if (!File.Exists(executable + ".update-old")) break;
                await Task.Delay(25, timeout.Token);
            } while (true);
        }
        Check(!File.Exists(executable + ".update-old"), "unlocked backup cleaned");
        var failedStage = Path.Combine(root, "failed-stage");
        Directory.CreateDirectory(failedStage);
        File.WriteAllText(Path.Combine(failedStage, "first.txt"), "new");
        File.WriteAllText(Path.Combine(failedStage, "blocked.txt"), "new");
        File.WriteAllText(Path.Combine(install, "first.txt"), "old");
        Directory.CreateDirectory(Path.Combine(install, "blocked.txt"));
        var failed = false;
        try { UpdateService.ReplaceFiles(failedStage, install); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed && File.ReadAllText(Path.Combine(install, "first.txt")) == "old", "mid-swap failure restores earlier file");
        Pass("isolated running MikuN2N.exe console fixture rename/copy, locked cleanup and process-local rollback");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Pass(string message) => Console.WriteLine("PASS: " + message);

    private sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.ToString());
            Check(request.Headers.Authorization is null, "no token is sent");
            return respond(request);
        }
    }
}
