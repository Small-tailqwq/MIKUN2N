using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MikuN2N.Models;

namespace MikuN2N.Services;

/// <summary>
/// Where diagnostics are uploaded. Either the private test profile embedded at build
/// time (pre-shared batch token), or, with the experimental setting, the active node's
/// own supernode, where uploads prove knowledge of the community instead.
/// </summary>
public sealed class TestBuildProfile
{
    public const int NodeReceiverPort = 5443;
    private const string CommunityProofLabel = "mikun2n-diagnostics-v1";

    public static TestBuildProfile? Current { get; } = Load();
    public string BatchId { get; set; } = string.Empty;
    public SupernodeNode Node { get; set; } = new();
    public string UploadUrl { get; set; } = string.Empty;
    public string CertificateSha256 { get; set; } = string.Empty;
    public string UploadToken { get; set; } = string.Empty;
    public string Ipv6StunHost { get; set; } = string.Empty;
    // Legacy private-profile field; it does not enforce a receiver retention policy.
    public int RetentionDays { get; set; } = 1;

    [JsonIgnore]
    public string UploadCommunity { get; private set; } = string.Empty;

    [JsonIgnore]
    public bool IsNodeReceiver => UploadCommunity.Length > 0;

    public AppSettings CreateSettings() => new()
    {
        Nodes = [Node],
        ActiveNodeId = Node.Id,
        ExperimentalIpv6P2p = true
    };

    /// <summary>Receiver on the node's supernode; an empty certificate means none answered.</summary>
    public static TestBuildProfile ForNode(SupernodeNode node, string host, string certificateSha256) => new()
    {
        BatchId = CommunityBatch(node.Community),
        Node = node,
        UploadUrl = $"https://{(host.Contains(':') ? $"[{host}]" : host)}:{NodeReceiverPort}/",
        CertificateSha256 = certificateSha256,
        UploadCommunity = node.Community,
        RetentionDays = 1
    };

    public static string CommunityBatch(string community) =>
        "c-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(community)))[..16].ToLowerInvariant();

    /// <summary>Per-segment upload proof; the receiver recomputes it from its community list.</summary>
    public string CommunityProof(string session) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(UploadCommunity),
            Encoding.UTF8.GetBytes($"{CommunityProofLabel}\n{BatchId}\n{session}"))).ToLowerInvariant();

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
                !Regex.IsMatch(profile.UploadToken, @"\A[0-9a-fA-F]{64}\z"))
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
