using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MikuN2N.Services;

public sealed class TestDiagnosticsSession : IAsyncDisposable
{
    private const long MaxLogBytes = 64 * 1024 * 1024;
    private const int ChunkBytes = 128 * 1024;
    private readonly object _gate = new();
    private readonly TestBuildProfile _profile;
    private readonly FileStream _file;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _secrets = [];
    private readonly CancellationTokenSource _uploadCancellation = new();
    private readonly HttpClient? _client;
    private readonly Task _worker;
    private readonly Action<string> _status;
    private long _sequence;
    private long _acknowledgedBytes;
    private int _chunk;
    private bool _disposed;
    private bool _storageFailed;
    private volatile bool _finishing;
    private volatile bool _uploadAllowed;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string LogPath { get; }
    public bool UploadAllowed => _uploadAllowed;

    public TestDiagnosticsSession(TestBuildProfile profile, bool consent, string encryptionKey, Action<string> status)
    {
        _profile = profile;
        _status = status;
        _uploadAllowed = consent;
        AddSecret(encryptionKey);
        AddSecret(profile.UploadToken);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "logs");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, $"diagnostics-{DateTime.UtcNow:yyyyMMddTHHmmss}-{SessionId}.log");
        _file = new FileStream(LogPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        Write("session_start", new
        {
            version = BuildIdentity.Version,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            utcOffsetMinutes = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
            uploadConsent = consent,
            retentionDays = profile.RetentionDays
        });

        // No HTTP client, DNS lookup, request or historical-log scan exists before consent.
        if (consent)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                    profile.CertificateSha256, StringComparison.OrdinalIgnoreCase);
            _client = new HttpClient(handler) { BaseAddress = new Uri(profile.UploadUrl), Timeout = TimeSpan.FromSeconds(8) };
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", profile.UploadToken);
            _worker = Task.Run(UploadLoopAsync);
            _status($"已同意上传本次日志 · 会话 {SessionId[..8]} · 等待服务器接收");
        }
        else
        {
            _worker = Task.CompletedTask;
            _status($"仅保存在本机 · 会话 {SessionId[..8]}");
        }
    }

    public void AddSecret(string value)
    {
        if (!string.IsNullOrEmpty(value))
            lock (_gate) _secrets.Add(value);
    }

    private string Redact(string value)
    {
        foreach (var secret in _secrets)
            value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? value : value.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private JsonNode? RedactNode(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(Redact(text));
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
                obj[key] = RedactNode(obj[key]?.DeepClone());
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                array[i] = RedactNode(array[i]?.DeepClone());
        }
        return node;
    }

    public void Write(string kind, object? data)
    {
        lock (_gate)
        {
            if (_disposed || _storageFailed) return;
            try
            {
                var detail = RedactNode(JsonSerializer.SerializeToNode(data));
                var line = JsonSerializer.Serialize(new
                {
                    schema = 1, batch = _profile.BatchId, session = SessionId,
                    sequence = ++_sequence, utc = DateTimeOffset.UtcNow,
                    elapsedMs = _clock.ElapsedMilliseconds, kind, data = detail
                });
                // Keep each record within a chunk; never write a partial JSON record.
                if (Encoding.UTF8.GetByteCount(line) > ChunkBytes / 2)
                    line = JsonSerializer.Serialize(new { schema = 1, batch = _profile.BatchId,
                        session = SessionId, sequence = _sequence, utc = DateTimeOffset.UtcNow,
                        elapsedMs = _clock.ElapsedMilliseconds, kind = "record_truncated", originalKind = kind });
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (_file.Length + bytes.Length > MaxLogBytes)
                    throw new IOException("Diagnostic session size limit reached.");
                _file.Seek(0, SeekOrigin.End);
                _file.Write(bytes);
                _file.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _storageFailed = true;
                _status("本次详细日志已停止写入（磁盘不可用或达到 64 MB 上限），联机仍可继续。");
            }
        }
    }

    public void CaptureNetwork()
    {
        try
        {
            Write("network_interfaces", NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
            {
                var ip = adapter.GetIPProperties();
                return new
                {
                    type = adapter.NetworkInterfaceType.ToString(),
                    status = adapter.OperationalStatus.ToString(), adapter.Description,
                    ipv6Mtu = adapter.Supports(NetworkInterfaceComponent.IPv6) ? ip.GetIPv6Properties()?.Mtu : null,
                    addresses = ip.UnicastAddresses.Select(address => new
                    {
                        address = address.Address.ToString(), address.PrefixLength,
                        dad = address.DuplicateAddressDetectionState.ToString(),
                        address.AddressPreferredLifetime
                    }).ToArray(),
                    gateways = ip.GatewayAddresses.Select(address => address.Address.ToString()).ToArray()
                };
            }).ToArray());
        }
        catch (Exception exception)
        {
            Write("network_interfaces_error", new { error = exception.GetType().Name });
        }
    }

    private byte[] ReadChunk()
    {
        lock (_gate)
        {
            var size = (int)Math.Min(ChunkBytes, _file.Length - _acknowledgedBytes);
            if (size <= 0) return [];
            var data = new byte[size];
            _file.Seek(_acknowledgedBytes, SeekOrigin.Begin);
            _file.ReadExactly(data);
            var end = Array.LastIndexOf(data, (byte)'\n');
            return end < 0 ? [] : data[..(end + 1)];
        }
    }

    private async Task UploadLoopAsync()
    {
        var cancellationToken = _uploadCancellation.Token;
        var failures = 0;
        var reportAt = 0L;
        byte[]? pending = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // A response can be lost after the receiver commits a chunk. Retries
                // must keep its original bytes even while more records arrive locally.
                var data = pending ??= ReadChunk();
                if (data.Length == 0)
                {
                    pending = null;
                    if (_finishing) return;
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }
                try
                {
                    using var body = new ByteArrayContent(data);
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
                    using var response = await _client!.PostAsync(
                        $"v1/logs/{_profile.BatchId}/{SessionId}/{_chunk:D8}", body, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("Upload rejected", null, response.StatusCode);
                    _acknowledgedBytes += data.Length;
                    _chunk++;
                    pending = null;
                    if (failures > 0 || _clock.ElapsedMilliseconds >= reportAt)
                    {
                        _status($"服务器已接收 {_acknowledgedBytes / 1024.0:F1} KB · 会话 {SessionId[..8]}");
                        reportAt = _clock.ElapsedMilliseconds + 30000;
                    }
                    failures = 0;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                    exception is HttpRequestException or TaskCanceledException)
                {
                    failures++;
                    if (failures == 1)
                    {
                        var code = exception is HttpRequestException http ? (int?)http.StatusCode : null;
                        Write("upload_failure", new { error = exception.GetType().Name, httpStatus = code });
                        _status("日志暂未送达，正在重试；完整记录仍保存在本机。");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * failures)), cancellationToken);
                }
                if (!_finishing)
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Write("upload_worker_stopped", new { error = exception.GetType().Name });
            _status("日志上传已停止，记录仍保存在本机。");
        }
    }

    public async Task StopUploadAsync()
    {
        _uploadAllowed = false;
        _uploadCancellation.Cancel();
        await _worker;
        Write("upload_revoked", new { acknowledgedBytes = _acknowledgedBytes });
        _status("已停止上传 · 后续日志仅保存在本机");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        var uploadWasAllowed = _uploadAllowed;
        Write("session_end", new { acknowledgedBytes = _acknowledgedBytes });
        _finishing = true;
        // A failed endpoint must not hold up disconnect/exit indefinitely.
        if (await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(8))) != _worker)
            _uploadCancellation.Cancel();
        await _worker;
        _uploadAllowed = false;
        lock (_gate)
        {
            _disposed = true;
            if (_storageFailed)
                _status("本次详细日志不完整（磁盘不可用或达到上限），请保留本机日志。");
            else if (uploadWasAllowed)
                _status(_acknowledgedBytes == _file.Length
                    ? $"本次日志已全部送达 · 会话 {SessionId[..8]}"
                    : $"本次日志部分未送达，请保留本机日志 · 会话 {SessionId[..8]}");
            _file.Dispose();
            _secrets.Clear();
        }
        _client?.Dispose();
        _uploadCancellation.Dispose();
    }
}
