using System.Text.Json;
using MikuN2N.Services;

internal static class DiagnosticPolicyTests
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use an empty fixture directory.");
        Directory.CreateDirectory(root);
        var transportCreated = false;
        var profile = new TestBuildProfile { BatchId = "policy-fixture", UploadUrl = "https://receiver.invalid/", CertificateSha256 = new string('A', 64) };
        string path;
        await using (var session = new TestDiagnosticsSession(profile, false, "fixture-key", _ => { }, root,
            () => { transportCreated = true; throw new InvalidOperationException("No upload allowed in this fixture"); }))
        {
            path = session.LogPath;
            session.Write("policy_fixture", new { value = "fixture-key" });
        }
        var text = File.ReadAllText(path);
        if (transportCreated || text.Contains("fixture-key") || text.Contains("retentionHours"))
            throw new InvalidOperationException("No transport/secret/global retention claim expected.");
        if (!text.Contains("receiverRetentionPolicy") || !text.Contains("provider-defined"))
            throw new InvalidOperationException("Receiver retention policy must be provider-defined.");
        File.WriteAllText(Path.Combine(root, "diagnostic-policy-validation.json"), JsonSerializer.Serialize(new {
            passed = true, assertions = 5, transportCreated, realNetwork = false, realSettings = false,
            productionRecorder = true, fixedServerRetentionPromise = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 5 diagnostic policy assertions; production recorder, consent=false, temporary files only.");
    }
}
