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
    PunchFailed,
    PeerAssisted
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
    int? LocalIpv6WireVersion = null,
    string? AssistRelayName = null)
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
        PeerConnectionMode.PunchFailed => "中转 · 直连未成功",
        PeerConnectionMode.Relayed => "服务器中转",
        PeerConnectionMode.ForcedRelayed => "中转 · 手动选择",
        PeerConnectionMode.PeerAssisted => "好友协助转发",
        _ => "检测中"
    };
    public string ConnectionModeDescription => ConnectionMode switch
    {
        PeerConnectionMode.Direct => "与这位朋友直接连接，数据无需服务器中转。",
        PeerConnectionMode.Ipv6Direct => "通过 IPv6 与这位朋友直接通信。",
        PeerConnectionMode.LanDirect => "通过本地局域网直接与这位朋友通信。",
        PeerConnectionMode.Punching => "目前通过服务器中转，同时尝试建立直连；成功后会自动切换。",
        PeerConnectionMode.PunchFailed => "本轮直连尝试已结束，目前通过服务器中转。可右键重新尝试，或查看好友协助选项。",
        PeerConnectionMode.Relayed => "通过节点服务器转发，程序会继续尝试直连。",
        PeerConnectionMode.ForcedRelayed => "你已选择通过服务器中转。可右键恢复自动选择线路。",
        PeerConnectionMode.PeerAssisted => $"由 {AssistRelayName ?? "好友"} 帮你们转发数据。可在协助窗口结束，之后回到原来的线路。",
        _ => "正在确认连接方式…"
    };
}
