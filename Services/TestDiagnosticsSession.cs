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
    private const long SegmentBytes = 64 * 1024 * 1024;
    private const int MaxSegments = 4;
    private const int ChunkBytes = 128 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TestBuildProfile _profile;
    private readonly string _directory;
    private readonly Func<HttpMessageHandler>? _uploadHandlerFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _secrets = [];
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<string, JsonNode?> _context = [];
    // Status sinks enqueue UI work; they must not synchronously wait for a session
    // operation. Write-error notifications may originate while _gate is held.
    private readonly Action<string> _status;
    private CancellationTokenSource? _uploadCancellation;
    private HttpClient? _client;
    private Task _worker = Task.CompletedTask;
    private Segment? _current;
    private long _sequence, _acknowledgedBytes, _droppedBytes, _abandonedBytes;
    private int _segmentNumber;
    private bool _disposed, _storageFailed;
    private volatile bool _finishing, _uploadAllowed;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string LogPath => _current?.Path ?? string.Empty;
    public bool UploadAllowed => _uploadAllowed;
    public bool IsRecording { get { lock (_gate) return !_disposed && !_storageFailed; } }

    private sealed class Segment(string id, string path, FileStream file, bool allowed)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public FileStream File { get; } = file;
        public long Ack;
        public int Chunk;
        public bool Allowed = allowed;
        public bool Removed;
    }
    private sealed record Pending(Segment Segment, int Chunk, byte[] Bytes);

    public TestDiagnosticsSession(TestBuildProfile profile, bool consent, string encryptionKey, Action<string> status)
        : this(profile, consent, encryptionKey, status,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikuN2N", "logs")) { }

    internal TestDiagnosticsSession(TestBuildProfile profile, bool consent, string encryptionKey, Action<string> status,
        string directory, Func<HttpMessageHandler>? uploadHandlerFactory = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        _directory = directory; _uploadHandlerFactory = uploadHandlerFactory;
        _retryDelay = retryDelay ?? Task.Delay;
        _profile = profile; _status = status; _uploadAllowed = consent;
        AddSecret(encryptionKey); AddSecret(profile.UploadToken);
        try { lock (_gate) Rotate("connection_start"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _storageFailed = true; }
        Write("session_start", new { version = BuildIdentity.Version, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
            utcOffsetMinutes = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
            uploadConsent = consent, retentionHours = 24 });
        if (consent) StartWorker();
        _status(_storageFailed ? "详细日志写入已暂停；可点击恢复日志。" :
            consent ? "详细日志正在记录 · 等待服务器接收" : "详细日志正在记录 · 仅保存在本机");
    }

    private void Rotate(string reason)
    {
        // Only files created by this connection are rotated. Unsent outage backlog
        // is bounded too; removed segments can never be replayed after resumption.
        while (_segments.Count >= MaxSegments)
        {
            var oldest = _segments[0];
            var unacknowledged = oldest.File.Length - oldest.Ack;
            File.Delete(oldest.Path);
            oldest.File.Dispose();
            oldest.Removed = true; _segments.RemoveAt(0);
            _droppedBytes += unacknowledged;
        }
        Directory.CreateDirectory(_directory);
        var id = _segmentNumber == 0 ? SessionId : Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, $"diagnostics-{DateTime.UtcNow:yyyyMMddTHHmmss}-{id}.log");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        _current = new Segment(id, path, stream, _uploadAllowed);
        _segments.Add(_current);
        _segmentNumber++;
        _storageFailed = false;
        Append("segment_start", JsonSerializer.SerializeToNode(new { reason, connection = SessionId,
            segment = _segmentNumber, droppedUnacknowledgedBytes = _droppedBytes, uploadConsent = _uploadAllowed }));
        foreach (var (kind, value) in _context) Append(kind, value);
    }

    private void Append(string kind, JsonNode? data)
    {
        var line = JsonSerializer.Serialize(new { schema = 1, batch = _profile.BatchId, session = _current!.Id,
            connection = SessionId, segment = _segmentNumber, sequence = ++_sequence,
            utc = DateTimeOffset.UtcNow, elapsedMs = _clock.ElapsedMilliseconds, kind, data });
        if (Encoding.UTF8.GetByteCount(line) > ChunkBytes / 2)
            line = JsonSerializer.Serialize(new { schema = 1, batch = _profile.BatchId, session = _current.Id,
                connection = SessionId, segment = _segmentNumber, sequence = _sequence, utc = DateTimeOffset.UtcNow,
                elapsedMs = _clock.ElapsedMilliseconds, kind = "record_truncated", originalKind = kind });
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        if (_current.File.Length + bytes.Length > SegmentBytes) throw new IOException("Diagnostic context exceeds segment.");
        _current.File.Seek(0, SeekOrigin.End);
        _current.File.Write(bytes); _current.File.Flush();
    }

    public void Write(string kind, object? data)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var detail = RedactNode(JsonSerializer.SerializeToNode(data));
                if (kind is "connection_requested" or "native_identity" or "session_start") _context[kind] = detail?.DeepClone();
                if (_storageFailed) return;
                // Reserve room for the next complete record before choosing its segment.
                if (_current!.File.Length > SegmentBytes - ChunkBytes) Rotate("size_limit");
                Append(kind, detail);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _storageFailed = true;
                _status("详细日志写入已暂停；可点击恢复日志，联机不受影响。");
            }
        }
    }

    private Pending? ReadChunk()
    {
        lock (_gate)
        {
            foreach (var segment in _segments)
            {
                if (!segment.Allowed || segment.Removed) continue;
                var size = (int)Math.Min(ChunkBytes, segment.File.Length - segment.Ack);
                if (size <= 0) continue;
                var data = new byte[size]; segment.File.Seek(segment.Ack, SeekOrigin.Begin); segment.File.ReadExactly(data);
                var end = Array.LastIndexOf(data, (byte)'\n');
                if (end >= 0) return new Pending(segment, segment.Chunk, data[..(end + 1)]);
            }
            return null;
        }
    }

    private void StartWorker()
    {
        // Called only after explicit consent or a deliberately saved Allow preference.
        var handler = _uploadHandlerFactory?.Invoke() ?? CreatePinnedHandler();
        _client = new HttpClient(handler) { BaseAddress = new Uri(_profile.UploadUrl), Timeout = TimeSpan.FromSeconds(8) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _profile.UploadToken);
        _uploadCancellation = new CancellationTokenSource();
        _worker = Task.Run(() => UploadLoopAsync(_client, _uploadCancellation.Token));
    }

    private HttpClientHandler CreatePinnedHandler()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
            string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), _profile.CertificateSha256, StringComparison.OrdinalIgnoreCase);
        return handler;
    }

    private async Task UploadLoopAsync(HttpClient client, CancellationToken token)
    {
        Pending? pending = null;
        int failures = 0, capacityFailures = 0; long reportAt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (_gate)
                    if (pending is not null && (pending.Segment.Removed || !pending.Segment.Allowed))
                    { pending = null; failures = capacityFailures = 0; }
                pending ??= ReadChunk();
                if (pending is null)
                {
                    if (_finishing) return;
                    await Task.Delay(1000, token); continue;
                }
                try
                {
                    using var body = new ByteArrayContent(pending.Bytes);
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
                    using var response = await client.PostAsync($"v1/logs/{_profile.BatchId}/{pending.Segment.Id}/{pending.Chunk:D8}", body, token);
                    var code = (int)response.StatusCode;
                    capacityFailures = code == 507 ? capacityFailures + 1 : 0;
                    // Conflicts cannot be repaired by replaying the same immutable
                    // chunk. A repeated capacity refusal must not pin the oldest segment.
                    if (code is 409 or 410 || capacityFailures >= 3)
                    {
                        AbandonSegment(pending.Segment, code);
                        pending = null; failures = capacityFailures = 0;
                        // A receiver rejecting every fresh segment must not cause
                        // unbounded rotation or a tight retry loop.
                        await _retryDelay(TimeSpan.FromSeconds(code == 507 ? 30 : 3), token);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("Upload rejected", null, response.StatusCode);
                    lock (_gate)
                    {
                        pending.Segment.Ack += pending.Bytes.Length; pending.Segment.Chunk++;
                        _acknowledgedBytes += pending.Bytes.Length;
                    }
                    pending = null;
                    if (failures > 0 || _clock.ElapsedMilliseconds >= reportAt)
                    {
                        ReportDelivery(); reportAt = _clock.ElapsedMilliseconds + 30000;
                    }
                    failures = capacityFailures = 0;
                }
                catch (Exception exception) when (!token.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException)
                {
                    if (++failures == 1)
                    {
                        Write("upload_failure", new { error = exception.GetType().Name,
                            httpStatus = exception is HttpRequestException http ? (int?)http.StatusCode : null });
                        _status("日志暂未送达，正在重试；本机最多保留本次连接最近 256 MiB 详细记录。");
                    }
                    await _retryDelay(TimeSpan.FromSeconds(Math.Min(30, failures * 3)), token);
                }
                if (!_finishing) await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Write("upload_worker_stopped", new { error = exception.GetType().Name });
            _uploadAllowed = false;
            _status("上传已暂停；可点击恢复日志，联机不受影响。");
        }
    }

    private void AbandonSegment(Segment segment, int httpStatus)
    {
        long bytes;
        lock (_gate)
        {
            if (segment.Removed || !segment.Allowed) return;
            segment.Allowed = false;
            bytes = segment.File.Length - segment.Ack;
            _abandonedBytes += bytes;
            if (ReferenceEquals(segment, _current) && !_finishing) Rotate("receiver_rejected");
        }
        Write("upload_segment_abandoned", new { segment = segment.Id, httpStatus, unacknowledgedBytes = bytes });
        _status("一段日志未能送达，已跳过并继续后续上传；该段仍按本机容量与保留策略保存。");
    }

    private void ReportDelivery()
    {
        string status;
        lock (_gate)
            status = $"{(_storageFailed ? "记录已暂停" : "正在记录")} · 服务器已接收 {_acknowledgedBytes / 1048576.0:F1} MiB · 分段 {_segmentNumber}" +
                     (_droppedBytes > 0 ? $" · 已轮转 {_droppedBytes / 1048576.0:F1} MiB 未确认记录" : "") +
                     (_abandonedBytes > 0 ? $" · 已跳过 {_abandonedBytes / 1048576.0:F1} MiB 未送达记录" : "");
        _status(status);
    }

    private async Task StopWorkerAsync()
    {
        _uploadAllowed = false;
        _uploadCancellation?.Cancel();
        await _worker;
        _client?.Dispose(); _client = null;
        _uploadCancellation?.Dispose(); _uploadCancellation = null;
        lock (_gate) foreach (var segment in _segments) segment.Allowed = false;
    }

    public async Task StopUploadAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopWorkerAsync();
            Write("upload_revoked", new { acknowledgedBytes = _acknowledgedBytes });
            _status("已停止上传 · 后续日志仅保存在本机");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ResumeAsync(bool consent)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            await StopWorkerAsync();
            _uploadAllowed = consent;
            lock (_gate) Rotate("manual_resume");
            Write("diagnostics_resumed", new { uploadConsent = consent });
            if (consent) StartWorker();
            _status(consent ? "详细日志已恢复 · 仅上传恢复后的新记录" : "详细日志已恢复 · 仅保存在本机");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _storageFailed = true; _uploadAllowed = false;
            _status("详细日志仍无法写入，请检查磁盘后再次恢复。");
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            Write("session_end", new { acknowledgedBytes = _acknowledgedBytes, droppedBytes = _droppedBytes,
                abandonedBytes = _abandonedBytes });
            _finishing = true;
            if (await Task.WhenAny(_worker, Task.Delay(8000)) != _worker) _uploadCancellation?.Cancel();
            await StopWorkerAsync();
            string status;
            lock (_gate)
            {
                _disposed = true;
                foreach (var segment in _segments) segment.File.Dispose();
                _secrets.Clear(); _context.Clear();
                status = _storageFailed || _droppedBytes > 0 || _abandonedBytes > 0
                    ? "本次诊断记录不完整，请保留本机日志。" : "本次诊断记录已结束，未送达部分保留在本机。";
            }
            _status(status);
        }
        finally { _lifecycle.Release(); }
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

}
