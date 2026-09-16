using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class TestBuildProfile
{
    public static TestBuildProfile? Current { get; } = Load();
    public string BatchId { get; set; } = string.Empty;
    public SupernodeNode Node { get; set; } = new();
    public string UploadUrl { get; set; } = string.Empty;
    public string CertificateSha256 { get; set; } = string.Empty;
    public string UploadToken { get; set; } = string.Empty;
    public string Ipv6StunHost { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 1;

    public AppSettings CreateSettings() => new()
    {
        Nodes = [Node],
        ActiveNodeId = Node.Id,
        ExperimentalIpv6P2p = true
    };

    private static TestBuildProfile? Load()
    {
        // Only an explicitly selected private publish embeds this resource.
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MikuN2N.PrivateTestProfile");
        if (stream is null)
            return null;
        try
        {
            var profile = JsonSerializer.Deserialize<TestBuildProfile>(stream);
            if (profile is null || !Regex.IsMatch(profile.BatchId, @"\A[a-zA-Z0-9-]{1,64}\z") ||
                profile.Node is null || string.IsNullOrWhiteSpace(profile.Node.Server) ||
                string.IsNullOrWhiteSpace(profile.Node.Community) ||
                !Uri.TryCreate(profile.UploadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                !Regex.IsMatch(profile.CertificateSha256, @"\A[0-9a-fA-F]{64}\z") ||
                !Regex.IsMatch(profile.UploadToken, @"\A[0-9a-fA-F]{64}\z") || profile.RetentionDays != 1)
                throw new InvalidDataException();
            return profile;
        }
        catch
        {
            // Do not include resource contents or credentials in startup diagnostics.
            throw new InvalidDataException("内置节点配置不完整，请重新获取安装包。");
        }
    }
}
