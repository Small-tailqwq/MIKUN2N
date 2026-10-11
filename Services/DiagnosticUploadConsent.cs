using System.Security.Cryptography;
using System.Text.Json;
using MikuN2N.Models;

namespace MikuN2N.Services;

/// <summary>What a settings save does to the diagnostics of a connection that is already running.</summary>
public enum ActiveDiagnosticsChange
{
    None,
    StopUpload,
    /// <summary>Run the receiver check and consent for the existing recording; upload new records only.</summary>
    Authorize,
    /// <summary>Upload became available after connecting: create and attach a recording first.</summary>
    Start
}

public static class DiagnosticUploadConsent
{
    // Bind consent to both the connection being diagnosed and the pinned receiver.
    // Length-delimited JSON avoids ambiguous concatenations; no credential is stored.
    public static string Target(TestBuildProfile profile, SupernodeNode? node) => node is null ? string.Empty :
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "diagnostic-consent-v2", new Uri(profile.UploadUrl).AbsoluteUri,
            profile.CertificateSha256.ToUpperInvariant(), node.Id, node.Name, node.Server, node.Community
        })));

    public static bool IsAllowed(AppSettings settings, TestBuildProfile profile) =>
        settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow &&
        settings.ActiveNode is not null && settings.DiagnosticUploadTarget == Target(profile, settings.ActiveNode);

    public static void Invalidate(AppSettings settings)
    {
        if (settings.DiagnosticUpload == DiagnosticUploadPreference.AlwaysAllow)
            settings.DiagnosticUpload = DiagnosticUploadPreference.Ask;
        settings.DiagnosticUploadTarget = string.Empty;
    }

    /// <summary>
    /// Settings saved during a connection used to reach only an upload that was already
    /// running: choosing "always allow" or enabling node uploads mid-connection showed no
    /// prompt and uploaded nothing until the next connection.
    /// </summary>
    public static ActiveDiagnosticsChange PlanSettingsChange(
        AppSettings settings,
        DiagnosticUploadPreference previousUpload,
        string previousTarget,
        bool previousNodeUpload,
        bool privateProfile,
        bool hasRecording,
        bool uploading,
        bool recordingUsesNodeReceiver)
    {
        var upload = settings.DiagnosticUpload;
        if (!hasRecording)
            return !privateProfile && settings.ExperimentalDiagnosticUpload && !previousNodeUpload
                ? ActiveDiagnosticsChange.Start
                : ActiveDiagnosticsChange.None;
        // Leaving "always allow" for "ask" withdraws what the saved permission granted.
        if (upload == DiagnosticUploadPreference.AlwaysDeny ||
            (recordingUsesNodeReceiver && !settings.ExperimentalDiagnosticUpload) ||
            (upload == DiagnosticUploadPreference.Ask && previousUpload == DiagnosticUploadPreference.AlwaysAllow))
            return uploading ? ActiveDiagnosticsChange.StopUpload : ActiveDiagnosticsChange.None;
        // A node receiver's certificate is unknown in Settings, so "always allow" chosen
        // there arrives unbound; the running connection knows the receiver and binds it now.
        var newlyAllowed = upload == DiagnosticUploadPreference.AlwaysAllow &&
                           (previousUpload != DiagnosticUploadPreference.AlwaysAllow ||
                            previousTarget != settings.DiagnosticUploadTarget);
        if (uploading)
            return newlyAllowed ? ActiveDiagnosticsChange.Authorize : ActiveDiagnosticsChange.None;
        // Leaving "always deny", or enabling node uploads again, means this connection
        // was never asked under the current choice.
        return newlyAllowed || previousUpload == DiagnosticUploadPreference.AlwaysDeny ||
               (recordingUsesNodeReceiver && !previousNodeUpload)
            ? ActiveDiagnosticsChange.Authorize
            : ActiveDiagnosticsChange.None;
    }
}
