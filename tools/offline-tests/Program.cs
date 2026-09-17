using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

static byte[] ReadLog(string path)
{
    using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
    using var bytes = new MemoryStream(); file.CopyTo(bytes); return bytes.ToArray();
}
static object PrivateField(TestDiagnosticsSession subject,string name) =>
    typeof(TestDiagnosticsSession).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(subject)!;
static void FillUntilRotation(TestDiagnosticsSession subject)
{
    var path = subject.LogPath;
    var payload = new string('x',48*1024);
    for(int i=0; i<1500 && path == subject.LogPath; i++) subject.Write("backlog",new { index=i,payload });
    Check(subject.LogPath != path && subject.IsRecording,"real 64 MiB Write-driven rotation");
}
static async Task WaitFor(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(!condition()) await Task.Delay(20,timeout.Token);
}
static void CheckDelivery(ScriptedReceiver receiver,string directory,string marker,int expectedMarkers)
{
    int markers=0;
    foreach(var group in receiver.Accepted.ToArray().GroupBy(chunk=>chunk.Id))
    {
        var chunks=group.ToArray();
        Check(chunks.Select(c=>c.Index).SequenceEqual(Enumerable.Range(0,chunks.Length)),"chunk indices start at zero without gaps or duplicates");
        var received=chunks.SelectMany(c=>c.Bytes).ToArray();
        var local=ReadLog(Directory.GetFiles(directory,$"diagnostics-*-{group.Key}.log").Single());
        Check(received.AsSpan().SequenceEqual(local.AsSpan(0,received.Length)),"delivered bytes equal local file prefix");
        var lines=Encoding.UTF8.GetString(received).Split('\n',StringSplitOptions.RemoveEmptyEntries);
        long previous=0;
        foreach(var line in lines)
        {
            using var row=JsonDocument.Parse(line);
            var sequence=row.RootElement.GetProperty("sequence").GetInt64();
            Check(previous==0 || sequence==previous+1,"record sequence is contiguous");
            previous=sequence;
            if(row.RootElement.GetProperty("kind").GetString()==marker) markers++;
        }
        Check(lines.Length==Encoding.UTF8.GetString(local.AsSpan(0,received.Length)).Count(c=>c=='\n'),"record count matches local prefix");
    }
    Check(markers==expectedMarkers,"every expected marker arrives exactly once");
}

foreach (var statusCode in new[] { 409, 410 })
{
    var firstRequest=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var nextSegment=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseNext=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    string? blockedId=null;
    var receiver=new ScriptedReceiver(async (chunk,token)=> {
        blockedId ??= chunk.Id;
        if(chunk.Id==blockedId) { firstRequest.TrySetResult(); await release.Task.WaitAsync(token); return statusCode; }
        nextSegment.TrySetResult(); await releaseNext.Task.WaitAsync(token); return 201;
    });
    var directory=Path.Combine(root,"recovery-"+statusCode);
    var recovery=new TestDiagnosticsSession(profile,true,"",_=>{},directory,receiver.CreateHandler,
        (delay,token)=>Task.Delay(1,token));
    await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var blockedPath=recovery.LogPath;
    if(statusCode==409) FillUntilRotation(recovery);
    release.TrySetResult();
    await nextSegment.Task.WaitAsync(TimeSpan.FromSeconds(3));
    for(int i=0;i<12;i++) recovery.Write("delivery_marker",new { index=i,payload=new string('y',48*1024) });
    recovery.Write("delivery_end",new {});
    releaseNext.TrySetResult();
    await WaitFor(()=>receiver.Contains("delivery_end"));
    Check(receiver.Requests.Count(c=>c.Id==blockedId)==1,"conflicting segment is not retried");
    Check(recovery.IsRecording && recovery.UploadAllowed && File.Exists(blockedPath), "skip preserves local logging and valid consent");
    await recovery.StopUploadAsync();
    CheckDelivery(receiver,directory,"delivery_marker",12);
    Check(receiver.Accepted.Count>2,"integrity check spans multiple chunks");
    Check((long)PrivateField(recovery,"_abandonedBytes")==new FileInfo(blockedPath).Length,"rejected bytes counted once");
    // Exercise quota removal of the already abandoned segment without another 192 MiB of fixtures.
    var gate=PrivateField(recovery,"_gate");
    lock(gate) for(int i=0;i<3;i++) typeof(TestDiagnosticsSession).GetMethod("Rotate",BindingFlags.Instance|BindingFlags.NonPublic)!
        .Invoke(recovery,["test_abandoned_retention"]);
    Check(!File.Exists(blockedPath) && (long)PrivateField(recovery,"_droppedBytes")==0,"abandoned bytes are not recounted as rotation drops");
    await recovery.DisposeAsync();
}
Console.WriteLine("PASS: real 64 MiB backlog, 409/410 recovery, exact bytes/records/chunk order, no duplicates or double-counting");

{
    bool capacityRestored=false;
    var receiver=new ScriptedReceiver((_,_)=>Task.FromResult(Volatile.Read(ref capacityRestored)?201:507));
    var delays=new ControlledDelay();
    var directory=Path.Combine(root,"capacity-recovery");
    var recovery=new TestDiagnosticsSession(profile,true,"",_=>{},directory,receiver.CreateHandler,delays.WaitAsync);
    var firstWait=await delays.NextAsync();
    recovery.Write("capacity_marker",new {});
    firstWait.Release.TrySetResult();
    var secondWait=await delays.NextAsync();
    var attempts=receiver.Requests.ToArray();
    Check(attempts.Length==2 && attempts[0].Id==attempts[1].Id && attempts[0].Index==attempts[1].Index &&
        attempts[0].Bytes.SequenceEqual(attempts[1].Bytes),"507 retries keep the exact pending URI and bytes");
    Check(Directory.GetFiles(directory,"*.log").Length==1 && (long)PrivateField(recovery,"_abandonedBytes")==0,"507 does not abandon or rotate");
    Volatile.Write(ref capacityRestored,true); secondWait.Release.TrySetResult();
    await WaitFor(()=>receiver.Contains("capacity_marker"));
    await recovery.StopUploadAsync();
    CheckDelivery(receiver,directory,"capacity_marker",1);
    await recovery.DisposeAsync();
}
Console.WriteLine("PASS: each 507 waits 30 seconds; capacity recovery delivers the retained immutable chunk and following records");

foreach(bool consent in new[] { true,false })
{
    var receiver=new ScriptedReceiver((_,_)=>Task.FromResult(507));
    var delays=new ControlledDelay();
    var directory=Path.Combine(root,"capacity-resume-"+consent);
    var recovery=new TestDiagnosticsSession(profile,true,"",_=>{},directory,receiver.CreateHandler,delays.WaitAsync);
    var wait=await delays.NextAsync();
    var oldId=receiver.Requests.Single().Id;
    if(consent)
    {
        for(int i=0;i<4;i++) FillUntilRotation(recovery);
        Check(receiver.Requests.Count==1,"heavy rotation cannot bypass the pending capacity backoff");
        wait.Release.TrySetResult(); wait=await delays.NextAsync();
        Check(receiver.Requests.Last().Id!=oldId && receiver.Requests.Count==2,"new segment still waits after 507");
    }
    recovery.Write("before_resume_marker",new {});
    var beforeResume=receiver.Requests.Count;
    await recovery.ResumeAsync(consent).WaitAsync(TimeSpan.FromSeconds(3));
    Check(wait.Cancelled.Task.IsCompleted,"resume cancels and joins the old backoff worker");
    Check(recovery.IsRecording && recovery.UploadAllowed==consent,"resume preserves the selected consent");
    if(consent)
    {
        wait=await delays.NextAsync();
        var request=receiver.Requests.Last();
        Check(receiver.Requests.Count==beforeResume+1 && request.Id!=oldId && request.Index==0,"resumed worker starts a new segment at chunk zero");
        var text=Encoding.UTF8.GetString(request.Bytes);
        Check(text.Contains("diagnostics_resumed") && !text.Contains("before_resume_marker"),"manual resume cannot replay pre-resume records");
    }
    else Check(receiver.Requests.Count==beforeResume,"resume without consent makes no request");
    Check((long)PrivateField(recovery,"_abandonedBytes")==0,"global capacity shortage never abandons a segment");
    await recovery.StopUploadAsync().WaitAsync(TimeSpan.FromSeconds(3));
    if(consent) Check(wait.Cancelled.Task.IsCompleted,"revocation cancels a capacity backoff");
    var stoppedRequests=receiver.Requests.Count;
    recovery.Write("after_revocation_marker",new {}); await Task.Delay(100);
    Check(receiver.Requests.Count==stoppedRequests,"backoff cannot resurrect a revoked upload");
    await recovery.DisposeAsync();
}
Console.WriteLine("PASS: sustained 507 with real quota rotation, ResumeAsync during backoff with/without consent, cancellation and revocation");

{
    object? gate=null;
    var statuses=new List<string>();
    var subject=new TestDiagnosticsSession(profile,false,"",text=> {
        Check(gate is null || !Monitor.IsEntered(gate),"status callback holds no recording lock"); statuses.Add(text);
    },Path.Combine(root,"status-order"));
    gate=PrivateField(subject,"_gate");
    var capture=typeof(TestDiagnosticsSession).GetMethod("CaptureStatus",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var publish=typeof(TestDiagnosticsSession).GetMethod("PublishStatus",BindingFlags.Instance|BindingFlags.NonPublic)!;
    object oldStatus,newStatus;
    lock(gate) { oldStatus=capture.Invoke(subject,["old delivery snapshot"])!; newStatus=capture.Invoke(subject,["new skipped status"])!; }
    publish.Invoke(subject,[newStatus]); publish.Invoke(subject,[oldStatus]);
    Check(statuses.Last()=="new skipped status","delayed delivery snapshot cannot overwrite a newer status");
    var file=(FileStream)PrivateField(subject,"_current").GetType().GetProperty("File")!.GetValue(PrivateField(subject,"_current"))!;
    // A file occupying the rotation directory produces a real filesystem IOException.
    file.SetLength(64L*1024*1024);
    typeof(TestDiagnosticsSession).GetField("_directory",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(subject,subject.LogPath);
    subject.Write("storage_failure",new {});
    Check(!subject.IsRecording && statuses.Last().Contains("写入已暂停"),"Write storage failure is reported outside the lock");
    await subject.DisposeAsync();
}
Console.WriteLine("PASS: stale status snapshots are suppressed and callbacks run outside the recording lock");

sealed class FakeUpload(ConcurrentQueue<string> delivered) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        delivered.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.Created);
    }
}

sealed record UploadChunk(string Id,int Index,byte[] Bytes);

sealed class ScriptedReceiver(Func<UploadChunk,CancellationToken,Task<int>> response)
{
    public readonly ConcurrentQueue<UploadChunk> Requests=new(),Accepted=new();
    public HttpMessageHandler CreateHandler()=>new Handler(this,response);
    public bool Contains(string marker)=>Accepted.Any(c=>Encoding.UTF8.GetString(c.Bytes).Contains(marker));
    private sealed class Handler(ScriptedReceiver owner,Func<UploadChunk,CancellationToken,Task<int>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var chunk=new UploadChunk(request.RequestUri!.Segments[^2].TrimEnd('/'),int.Parse(request.RequestUri.Segments[^1]),
                await request.Content!.ReadAsByteArrayAsync(token));
            owner.Requests.Enqueue(chunk);
            var code=await response(chunk,token); token.ThrowIfCancellationRequested();
            if(code==201) owner.Accepted.Enqueue(chunk);
            return new HttpResponseMessage((HttpStatusCode)code);
        }
    }
}

sealed class ControlledDelay
{
    public sealed class Waiting
    {
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Channel<Waiting> _waits=Channel.CreateUnbounded<Waiting>();
    public async Task WaitAsync(TimeSpan duration,CancellationToken token)
    {
        if(duration!=TimeSpan.FromSeconds(30)) throw new InvalidOperationException("capacity retry must wait 30 seconds");
        var waiting=new Waiting(); await _waits.Writer.WriteAsync(waiting,token);
        try { await waiting.Release.Task.WaitAsync(token); }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { waiting.Cancelled.TrySetResult(); throw; }
    }
    public async Task<Waiting> NextAsync()=>await _waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
}
