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
    string SupernodeText = "—",
    string? NetworkHint = null);

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
    // Evaluated when the poll creates the row, so a peer going quiet changes the record
    // (and the displayed row) instead of staying "在线" until another field differs.
    public string StatusText { get; init; } = DateTimeOffset.Now - LastSeen < TimeSpan.FromSeconds(6)
        ? "在线"
        : "连接波动";
    public string ConnectionModeText => ConnectionMode switch
    {
        PeerConnectionMode.Direct => "直连 · IPv4",
        PeerConnectionMode.Ipv6Direct => "直连 · IPv6",
        PeerConnectionMode.LanDirect => "局域网直连",
        PeerConnectionMode.Punching => "正在尝试直连…",
        PeerConnectionMode.PunchFailed => "服务器中转（直连未成功）",
        PeerConnectionMode.Relayed => "服务器中转",
        PeerConnectionMode.ForcedRelayed => "服务器中转（手动）",
        _ => "检测中"
    };
    public string ConnectionModeDescription => ConnectionMode switch
    {
        PeerConnectionMode.Direct => "你们之间直接传输数据，延迟最低。",
        PeerConnectionMode.Ipv6Direct => "通过 IPv6 直接传输数据，延迟最低。",
        PeerConnectionMode.LanDirect => "你们在同一个局域网内，直接相连。",
        PeerConnectionMode.Punching => "正在尝试建立直连，期间数据经节点服务器中转。",
        PeerConnectionMode.PunchFailed => "自动直连尝试已用完，当前经节点服务器中转，延迟可能略高。可右键重新尝试。",
        PeerConnectionMode.Relayed => "暂时无法直连，数据经节点服务器中转，延迟可能略高。程序会继续自动尝试。",
        PeerConnectionMode.ForcedRelayed => "已手动设为经服务器中转，可右键恢复自动直连。",
        _ => "正在确认连接方式…"
    };
}
