using System.Diagnostics;
using System.Text.Json.Serialization;

namespace MikuN2N.Models;

// Loss counts unanswered UDP probe round trips after the full timeout, not game
// packets or directional loss. Pending probes and ICMP fallbacks are excluded.
public sealed record LinkQualitySnapshot(
    string PeerNodeId,
    int Samples,
    int Lost,
    double? RttMs,
    double? JitterMs,
    double? LossPercent,
    double AgeSeconds,
    PeerConnectionMode Mode,
    double? P95RttMs = null,
    int WindowSeconds = 60)
{
    [JsonIgnore]
    internal long CapturedAt { get; init; } = Stopwatch.GetTimestamp();

    [JsonIgnore]
    public double CurrentAgeSeconds => AgeSeconds + Stopwatch.GetElapsedTime(CapturedAt).TotalSeconds;

    [JsonIgnore]
    public bool IsStale => CurrentAgeSeconds > 15;

    [JsonIgnore]
    public bool HasEnoughSamples => Samples >= 5 && !IsStale;

    [JsonIgnore]
    public bool IsDirect => Mode is PeerConnectionMode.Direct or PeerConnectionMode.Ipv6Direct or
        PeerConnectionMode.LanDirect;
}

public sealed record PeerAssistMessage(
    string Kind,
    string CircuitId,
    string RequesterNodeId,
    string TargetNodeId,
    string RelayNodeId,
    string? ReplyTo = null,
    string? Detail = null);

public sealed record PeerAssistEnvelope(
    string SenderNodeId,
    string SenderNickname,
    string SenderVirtualIp,
    PeerAssistMessage Message);

public sealed record PeerAssistCandidate(
    PeerSnapshot Relay,
    LinkQualitySnapshot? ToRelay,
    LinkQualitySnapshot? RelayToTarget,
    bool Eligible,
    string Status)
{
    public double? EstRttMs => ToRelay?.RttMs is { } first && RelayToTarget?.RttMs is { } second
        ? first + second : null;

    // This is only an estimate: loss on the two legs may be correlated.
    public double? EstLossPercent => ToRelay?.LossPercent is { } first && RelayToTarget?.LossPercent is { } second
        ? 100 * (1 - (1 - first / 100) * (1 - second / 100)) : null;
}

public sealed record PeerAssistRequest(
    string CircuitId,
    string RequesterNodeId,
    string RequesterNickname,
    string TargetNodeId,
    string TargetNickname,
    string RelayNodeId);

public sealed record PeerAssistSession(
    string CircuitId,
    string RequesterNodeId,
    string TargetNodeId,
    string RelayNodeId,
    string Description,
    bool IsRelay,
    bool Active);
