using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MikuN2N.Models;
using MikuN2N.Services;

static void Check(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
}
var node = new SupernodeNode { Id = "test", Name = "Test", Server = "node.invalid:50001", Community = "test" };
var profile = new TestBuildProfile { BatchId = "offline", UploadUrl = "https://logs.invalid:5443/", CertificateSha256 = new string('A',64) };
var settings = new AppSettings { Nodes = [node], ActiveNodeId = node.Id, DiagnosticUpload = DiagnosticUploadPreference.AlwaysAllow };
settings.DiagnosticUploadTarget = DiagnosticUploadConsent.Target(profile,node);
var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
Check(DiagnosticUploadConsent.IsAllowed(restored,profile), "saved permission persists");
foreach (var field in new[] { "Id", "Name", "Server", "Community" })
{
    var property = typeof(SupernodeNode).GetProperty(field)!;
    var original = property.GetValue(node); property.SetValue(node, "changed");
    Check(!DiagnosticUploadConsent.IsAllowed(settings,profile), "node edit revokes " + field);
    property.SetValue(node,original);
}
profile.UploadUrl = "https://different.invalid:5443/";
Check(!DiagnosticUploadConsent.IsAllowed(settings,profile), "receiver change revokes");
profile.UploadUrl = "https://logs.invalid:5443/"; profile.CertificateSha256 = new string('B',64);
Check(!DiagnosticUploadConsent.IsAllowed(settings,profile), "certificate change revokes");
profile.CertificateSha256 = new string('A',64);
DiagnosticUploadConsent.Invalidate(settings);
Check(!DiagnosticUploadConsent.IsAllowed(settings,profile) && settings.DiagnosticUpload == DiagnosticUploadPreference.Ask,
    "switching back cannot resurrect permission");
settings.DiagnosticUpload = DiagnosticUploadPreference.AlwaysDeny;
DiagnosticUploadConsent.Invalidate(settings);
Check(settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysDeny, "deny survives endpoint changes");
Console.WriteLine("PASS: persisted consent, node/receiver/certificate edits, switch-back revocation, deny");

var root = Path.Combine(Path.GetFullPath(args.Single()),Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var delivered = new ConcurrentQueue<string>();
int workers = 0;
var session = new TestDiagnosticsSession(profile, false, "offline-secret", _ => { }, root,
    () => { workers++; return new FakeUpload(delivered); });
session.Write("offline_denied_marker", new { value = "offline-secret" });
Check(workers == 0, "refusal must not create any HTTP client");
var deniedPath = session.LogPath;
using(var reader = new StreamReader(new FileStream(deniedPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)))
    Check(!reader.ReadToEnd().Contains("offline-secret"), "credentials are redacted");
await session.ResumeAsync(true);
session.Write("allowed_marker", new { value = "new" });
for (int i=0; i<30 && !string.Join("",delivered).Contains("allowed_marker"); i++) await Task.Delay(100);
Check(string.Join("",delivered).Contains("allowed_marker"), "new records uploaded to fake handler");
Check(!string.Join("",delivered).Contains("offline_denied_marker"), "refused history must never replay");
await session.StopUploadAsync();
var deliveryCount = delivered.Count;
session.Write("revoked_marker", new { });
await Task.Delay(300);
Check(delivered.Count == deliveryCount, "revocation stops HTTP delivery");
await session.ResumeAsync(true);
session.Write("second_allowed_marker", new { });
for (int i=0; i<30 && !string.Join("",delivered).Contains("second_allowed_marker"); i++) await Task.Delay(100);
Check(!string.Join("",delivered).Contains("revoked_marker"), "revoked history never replays");
await session.StopUploadAsync();

for (int i=0; i<6; i++)
{
    var current = typeof(TestDiagnosticsSession).GetField("_current", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
    var file = (FileStream)current.GetType().GetProperty("File")!.GetValue(current)!;
    file.SetLength(64L*1024*1024-1);
    var previous = session.LogPath;
    session.Write("rotation_marker", new { index=i });
    Check(session.LogPath != previous && session.IsRecording, "size limit rotates and recording continues");
    Check(Directory.GetFiles(root,"diagnostics-*.log").Length <= 4, "at most four segments");
}
await session.ResumeAsync(false);
Check(session.IsRecording && !session.UploadAllowed, "recording can resume without upload consent");
session.Write("final_marker", new { });
using(var reader = new StreamReader(new FileStream(session.LogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)))
using(var row = JsonDocument.Parse(reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Last()))
    Check(row.RootElement.GetProperty("connection").GetString() == session.SessionId, "stable connection across segments");
await session.DisposeAsync();
Console.WriteLine("PASS: no requests before consent, redaction, no refused/revoked backlog, bounded rotation, in-session resume");

foreach (var statusCode in new[] { 409, 410, 507 })
{
    var handler = new SegmentRejector(statusCode);
    var retryDelays = new ConcurrentQueue<TimeSpan>();
    var recovery = new TestDiagnosticsSession(profile, true, "", _ => { }, Path.Combine(root,"recovery-"+statusCode),
        () => handler, (delay, token) => { retryDelays.Enqueue(delay); return Task.Delay(1,token); });
    await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var blockedPath = recovery.LogPath;
    if (statusCode == 409)
    {
        // Reproduce an old pending segment with a newer segment already waiting.
        var gate = typeof(TestDiagnosticsSession).GetField("_gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(recovery)!;
        lock(gate) typeof(TestDiagnosticsSession).GetMethod("Rotate",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(recovery,["test_next_segment"]);
        recovery.Write("next_segment_marker",new { });
    }
    handler.Release.TrySetResult();
    await handler.NextSegment.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(handler.RejectedRequests == (statusCode == 507 ? 3 : 1), "bounded rejected-chunk retries");
    Check(recovery.IsRecording && recovery.UploadAllowed && File.Exists(blockedPath), "skip preserves local logging and valid consent");
    if(statusCode == 507) Check(retryDelays.Contains(TimeSpan.FromSeconds(30)),"capacity failure backs off after skipping");
    await recovery.StopUploadAsync();
    var requestsAfterStop = handler.Requests;
    recovery.Write("after_recovery_revocation",new { }); await Task.Delay(100);
    Check(handler.Requests == requestsAfterStop,"recovery cannot bypass revocation");
    await recovery.DisposeAsync();
}
Console.WriteLine("PASS: old-segment 409, current-segment 410/507 recovery, bounded retries/backoff and revocation");

sealed class FakeUpload(ConcurrentQueue<string> delivered) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        delivered.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.Created);
    }
}

sealed class SegmentRejector(int statusCode) : HttpMessageHandler
{
    public readonly TaskCompletionSource FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource NextSegment = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _blockedId;
    public int RejectedRequests, Requests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Requests++;
        var id = request.RequestUri!.Segments[^2];
        _blockedId ??= id;
        if(id == _blockedId)
        {
            RejectedRequests++;
            FirstRequest.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new HttpResponseMessage((HttpStatusCode)statusCode);
        }
        NextSegment.TrySetResult();
        return new HttpResponseMessage(HttpStatusCode.Created);
    }
}
