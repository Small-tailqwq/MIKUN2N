namespace MikuN2N.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Reconnecting,
    Connected,
    Error
}

public sealed record ConnectionSnapshot(
    ConnectionState State,
    string Summary,
    string Detail,
    string VirtualIp = "—",
    int PeerCount = 0,
    int DirectPeerCount = 0,
    TimeSpan? Uptime = null,
    IReadOnlyList<PeerSnapshot>? Peers = null,
    string NatType = "—",
    string NatDescription = "NAT 行为尚未检测。",
    string SupernodeText = "—");

public enum PeerConnectionMode
{
    Unknown,
    Direct,
    Ipv6Direct,
    Relayed,
    ForcedRelayed,
    LanDirect,
    Punching,
    PunchFailed
}

public sealed record PeerSnapshot(
    string NodeId,
    string Nickname,
    string VirtualIp,
    int? LatencyMs,
    DateTimeOffset LastSeen,
    PeerConnectionMode ConnectionMode = PeerConnectionMode.Unknown,
    bool LatencyUnavailable = false,
    string? ClientVersion = null,
    string? NativeVersion = null,
    int? Ipv6WireVersion = null,
    int? LocalIpv6WireVersion = null)
{
    public string VersionText => ClientVersion ?? NativeVersion ?? "未报告";
    public bool Ipv6VersionMismatch => Ipv6WireVersion is > 0 && LocalIpv6WireVersion is > 0 &&
                                       Ipv6WireVersion != LocalIpv6WireVersion;
    public string VersionDetail => $"客户端：{ClientVersion ?? "未报告"}\nn3n 构建：{NativeVersion ?? "未报告"}\nIPv6 协议：{(Ipv6WireVersion is > 0 ? $"v{Ipv6WireVersion}" : "未报告")}\n" +
        (Ipv6VersionMismatch ? $"与本机 v{LocalIpv6WireVersion} 不兼容，当前保留 IPv4；请更新好友客户端。" :
            "不同构建号可以互通；IPv6 兼容性取决于协议代次。旧客户端可能不报告版本。");
    public string LatencyText => LatencyMs switch
    {
        null => LatencyUnavailable ? "暂无法测量" : "测量中",
        < 0 => $"{LatencyMs} ms",
        <= 1 => "≤1 ms",
        var latency => $"{latency} ms"
    };
    public string StatusText => DateTimeOffset.Now - LastSeen < TimeSpan.FromSeconds(6)
        ? "在线"
        : "连接波动";
    public string ConnectionModeText => ConnectionMode switch
    {
        PeerConnectionMode.Direct => "IPv4 P2P 直连",
        PeerConnectionMode.Ipv6Direct => "IPv6 P2P 直连",
        PeerConnectionMode.LanDirect => "本地直连",
        PeerConnectionMode.Punching => "打洞中…",
        PeerConnectionMode.PunchFailed => "pSp 中继（打洞失败）",
        PeerConnectionMode.Relayed => "pSp 中继",
        PeerConnectionMode.ForcedRelayed => "pSp 中继（手动）",
        _ => "检测中"
    };
}
