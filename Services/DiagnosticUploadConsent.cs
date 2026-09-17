using System.Security.Cryptography;
using System.Text.Json;
using MikuN2N.Models;

namespace MikuN2N.Services;

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
}
