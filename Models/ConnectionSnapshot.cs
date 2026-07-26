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
    string NatDescription = "NAT 行为尚未检测。");

public enum PeerConnectionMode
{
    Unknown,
    Direct,
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
    PeerConnectionMode ConnectionMode = PeerConnectionMode.Unknown)
{
    public string LatencyText => LatencyMs switch
    {
        null => "测量中",
        < 0 => $"{LatencyMs} ms",
        <= 1 => "≤1 ms",
        var latency => $"{latency} ms"
    };
    public string StatusText => DateTimeOffset.Now - LastSeen < TimeSpan.FromSeconds(6)
        ? "在线"
        : "连接波动";
    public string ConnectionModeText => ConnectionMode switch
    {
        PeerConnectionMode.Direct => "P2P 直连",
        PeerConnectionMode.LanDirect => "本地直连",
        PeerConnectionMode.Punching => "打洞中…",
        PeerConnectionMode.PunchFailed => "pSp 中继（打洞失败）",
        PeerConnectionMode.Relayed => "pSp 中继",
        PeerConnectionMode.ForcedRelayed => "pSp 中继（手动）",
        _ => "检测中"
    };
}
