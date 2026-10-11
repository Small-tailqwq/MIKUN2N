using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private const int DiscoveryPort = 43121;
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeRetention = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxDiscoveryPacketBytes = 4096;
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _socketRecoveryGate = new(1, 1);
    private UdpClient _udp;
    private readonly Action<string>? _diagnosticLog;
    private volatile bool _traceLatency;
    private readonly string _nodeId;
    private readonly string _nickname;
    private readonly IPAddress _localAddress;
    private readonly uint _tapNetwork;
    private readonly uint _tapMask;
    private readonly IPEndPoint _broadcastEndpoint;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, PeerState> _peers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingRelayPolicies = new();
    private readonly ConcurrentDictionary<string, PendingProbe> _pendingProbes = new();
    private readonly ConcurrentDictionary<string, PendingQualityQuery> _pendingQualityQueries = new();
    private readonly SemaphoreSlim _qualityQuerySlots = new(16, 16);
    private long _measurementGeneration;
    private NativeIdentity _localIdentity = new(null, null);
    private volatile bool _supportsPeerAssist;
    private readonly Task _receiveTask;
    private readonly Task _broadcastTask;
    private readonly Task _publishTask;
    private volatile bool _publishRequested;
    private int _stopped;

    public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    public event EventHandler<RelayPolicyRequest>? RelayPolicyRequested;
    public event EventHandler<PeerAssistEnvelope>? AssistMessageReceived;

    public string LocalNodeId => _nodeId;
    public string LocalVirtualIp => _localAddress.ToString();

    public void UpdateLocalAssistCapability(bool supported) => _supportsPeerAssist = supported;

    public bool SupportsPeerAssist(string nodeId) => _peers.TryGetValue(nodeId, out var peer) &&
        peer.SupportsPeerAssist && DateTimeOffset.Now - peer.LastSeen < PeerExpiry;

    public IReadOnlyList<PeerSnapshot> GetPeers() => _peers
        .Select(peer => new PeerSnapshot(peer.Key, peer.Value.Nickname, peer.Value.Address.ToString(),
            peer.Value.LatencyMs, peer.Value.LastSeen, peer.Value.Mode,
            LatencyUnavailable: peer.Value.LatencyUnavailable,
            ClientVersion: peer.Value.ClientVersion, NativeVersion: peer.Value.NativeVersion,
            Ipv6WireVersion: peer.Value.Ipv6WireVersion))
        .OrderBy(peer => peer.Nickname, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void UpdatePeerModes(IReadOnlyDictionary<string, PeerConnectionMode> modes)
    {
        foreach (var nodeId in _peers.Keys)
        {
            var mode = modes.GetValueOrDefault(nodeId, PeerConnectionMode.Unknown);
            while (_peers.TryGetValue(nodeId, out var current) && current.Mode != mode)
            {
                var updated = ResetMeasurement(current) with { Mode = mode };
                if (_peers.TryUpdate(nodeId, updated, current))
                {
                    PublishPeers();
                    break;
                }
            }
        }
    }

    public LinkQualitySnapshot? GetLinkQuality(string peerNodeId) =>
        _peers.TryGetValue(peerNodeId, out var peer) && DateTimeOffset.Now - peer.LastSeen < PeerExpiry
            ? peer.Quality.Snapshot(peerNodeId, peer.Mode) : null;

    public async Task<LinkQualitySnapshot?> QueryLinkQualityAsync(
        string reporterNodeId, string targetNodeId, CancellationToken cancellationToken)
    {
        if (targetNodeId == _nodeId || reporterNodeId == targetNodeId ||
            !_peers.ContainsKey(targetNodeId) || !_peers.TryGetValue(reporterNodeId, out var reporter) ||
            reporter.QualityVersion != 1)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        await _qualityQuerySlots.WaitAsync(linked.Token);
        var commandId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<LinkQualitySnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new PendingQualityQuery(reporterNodeId, reporter.Address, targetNodeId,
            Stopwatch.GetTimestamp(), completion);
        _pendingQualityQueries[commandId] = query;
        try
        {
            await SendAsync(new DiscoveryPacket("quality-request", _nodeId, _nickname, commandId, null,
                    TargetNodeId: reporterNodeId, QualityTargetNodeId: targetNodeId),
                new IPEndPoint(reporter.Address, DiscoveryPort), linked.Token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), linked.Token);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pendingQualityQueries.TryRemove(commandId, out _);
            _qualityQuerySlots.Release();
        }
    }

    public async Task SendAssistMessageAsync(
        string recipientNodeId, PeerAssistMessage message, CancellationToken cancellationToken)
    {
        if (!_supportsPeerAssist || !SupportsPeerAssist(recipientNodeId) ||
            !IsValidAssistMessage(message, _nodeId, recipientNodeId) ||
            !_peers.TryGetValue(recipientNodeId, out var recipient))
            throw new InvalidOperationException("好友协助通道尚未就绪，请稍后重试。");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        await SendAsync(new DiscoveryPacket("assist", _nodeId, _nickname, Guid.NewGuid().ToString("N"), null,
                TargetNodeId: recipientNodeId, AssistMessage: message),
            new IPEndPoint(recipient.Address, DiscoveryPort), linked.Token);
    }

    public void UpdateLocalIdentity(string? version, int? wireVersion) =>
        _localIdentity = new(NormalizeVersion(version), NormalizeWireVersion(wireVersion));

    private DiscoveryPacket StampIdentity(DiscoveryPacket packet)
    {
        var identity = _localIdentity;
        return packet with { ClientVersion = BuildIdentity.Version, NativeVersion = identity.Version,
            Ipv6WireVersion = identity.WireVersion, QualityVersion = 1, SupportsPeerAssist = _supportsPeerAssist };
    }

    internal static string? NormalizeVersion(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9._+\-]{1,64}\z") ? value : null;

    private static int? NormalizeWireVersion(int? value) => value is > 0 and <= 255 ? value : null;

    public PeerDiscoveryService(
        string nodeId,
        string nickname,
        IPAddress localAddress,
        IPAddress subnetMask,
        Action<string>? diagnosticLog = null,
        bool traceLatency = false)
    {
        _nodeId = nodeId;
        _nickname = nickname;
        _diagnosticLog = diagnosticLog;
        _traceLatency = traceLatency;
        _localAddress = localAddress;
        _tapMask = ToUInt32(subnetMask);
        _tapNetwork = ToUInt32(localAddress) & _tapMask;
        _broadcastEndpoint = new IPEndPoint(CalculateBroadcast(localAddress, subnetMask), DiscoveryPort);
        _udp = CreateUdpClient();
        _receiveTask = ReceiveLoopAsync(_cancellation.Token);
        _broadcastTask = BroadcastLoopAsync(_cancellation.Token);
        _publishTask = PublishLoopAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Task.WhenAll(_receiveTask, _broadcastTask, _publishTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        _socketRecoveryGate.Dispose();
        _cancellation.Dispose();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        lock (_socketGate)
        {
            _udp.Dispose();
        }
    }

    public async Task SendRelayPolicyAsync(
        string virtualIp,
        bool forceRelay,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(virtualIp, out var address))
        {
            throw new ArgumentException("好友虚拟地址无效。", nameof(virtualIp));
        }

        if (!TryResolveNodeId(address, out var targetNodeId))
        {
            throw new InvalidOperationException("尚未找到该好友的同步通道，请稍后重试。");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRelayPolicies.TryAdd(commandId, completion))
        {
            throw new InvalidOperationException("无法创建链路同步请求。");
        }

        var packet = new DiscoveryPacket(
            "relay-policy",
            _nodeId,
            _nickname,
            commandId,
            null,
            forceRelay,
            targetNodeId);
        var endpoint = new IPEndPoint(address, DiscoveryPort);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await SendAsync(packet, endpoint, cancellationToken);
                if (attempt < 3)
                {
                    await Task.Delay(120, cancellationToken);
                }
            }

            var acknowledgedState = await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (acknowledgedState != forceRelay)
            {
                throw new InvalidOperationException("好友返回的链路状态与请求不一致。");
            }
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("好友未确认链路切换；可能仍在使用旧版本。");
        }
        finally
        {
            _pendingRelayPolicies.TryRemove(commandId, out _);
        }
    }

    /// <summary>
    /// Announces a relay policy without waiting for an acknowledgement. Used to revert a
    /// peer that may have applied a request whose acknowledgement was lost, and to
    /// periodically re-assert a policy so a peer that restarted converges back.
    /// </summary>
    public async Task SendRelayPolicyNoticeAsync(
        string virtualIp,
        bool forceRelay,
        int attempts,
        bool reassert,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(virtualIp, out var address) ||
            !TryResolveNodeId(address, out var targetNodeId))
        {
            return;
        }

        var endpoint = new IPEndPoint(address, DiscoveryPort);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var packet = new DiscoveryPacket(
                "relay-policy",
                _nodeId,
                _nickname,
                Guid.NewGuid().ToString("N"),
                null,
                forceRelay,
                targetNodeId,
                reassert ? true : null);
            await SendAsync(packet, endpoint, cancellationToken);
            if (attempt < attempts - 1)
            {
                await Task.Delay(120, cancellationToken);
            }
        }
    }

    public async Task SendRelayPolicyAcknowledgementAsync(
        RelayPolicyRequest request,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(request.VirtualIp, out var address))
        {
            return;
        }

        var packet = new DiscoveryPacket(
            "relay-policy-ack",
            _nodeId,
            _nickname,
            Guid.NewGuid().ToString("N"),
            request.CommandId,
            request.ForceRelay,
            request.NodeId);
        var endpoint = new IPEndPoint(address, DiscoveryPort);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendAsync(packet, endpoint, cancellationToken);
            if (attempt < 2)
            {
                await Task.Delay(80, cancellationToken);
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Discovery only, never timed: n3n routes every broadcast through the
                // supernode (is_multi_broadcast in find_peer_destination), so timing this
                // would measure a relayed outbound leg plus a direct return leg.
                await SendAsync(
                    new DiscoveryPacket("hello", _nodeId, _nickname, Guid.NewGuid().ToString("N"), null),
                    _broadcastEndpoint,
                    cancellationToken);

                // RTT is measured with unicast probes, which take the same path as real
                // traffic - direct for a P2P peer, relayed for a peer on the supernode.
                foreach (var peer in _peers)
                {
                    await SendProbeAsync(peer.Key, peer.Value, cancellationToken);
                }
                await MeasureMissingLatenciesWithIcmpAsync(cancellationToken);

                RemoveExpiredProbes();
                RemoveExpiredPeers();
                await Task.Delay(BroadcastInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                await DelayAfterSocketFailureAsync(cancellationToken);
            }
            catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
            {
                await DelayAfterSocketFailureAsync(cancellationToken);
            }
        }
    }

    private async Task SendProbeAsync(string nodeId, PeerState peer, CancellationToken cancellationToken)
    {
        var probeId = Guid.NewGuid().ToString("N");
        // Serialize first so only the wire time is inside the measured window.
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            StampIdentity(new DiscoveryPacket("hello", _nodeId, _nickname, probeId, null)));
        var endpoint = new IPEndPoint(peer.Address, DiscoveryPort);
        _pendingProbes[probeId] = new PendingProbe(nodeId, peer.Address, Stopwatch.GetTimestamp(), peer.Generation);
        try
        {
            await SendPayloadAsync(payload, endpoint, cancellationToken);
            // Keep diagnostic disk/UI work outside the measured send window.
            TraceLatency($"probe_sent node={nodeId} ip={peer.Address} probe={probeId} generation={peer.Generation}");
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            _pendingProbes.TryRemove(probeId, out _);
            var error = exception is SocketException socket ? $"WSA{socket.NativeErrorCode}" : exception.GetType().Name;
            TraceLatency($"probe_send_failed node={nodeId} probe={probeId} error={error}");
            throw;
        }
    }

    private async Task MeasureMissingLatenciesWithIcmpAsync(CancellationToken cancellationToken)
    {
        var targets = _peers
            .Where(peer => peer.Value.LatencyMs is null)
            .Select(peer => MeasureIcmpLatencyAsync(
                peer.Key,
                peer.Value,
                cancellationToken))
            .ToArray();
        await Task.WhenAll(targets);
    }

    private async Task MeasureIcmpLatencyAsync(
        string nodeId,
        PeerState peer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(peer.Address, 1000).WaitAsync(cancellationToken);
            TraceLatency($"icmp_result node={nodeId} ip={peer.Address} generation={peer.Generation} status={reply.Status} rtt_ms={reply.RoundtripTime}");
            if (reply.Status != IPStatus.Success)
            {
                return;
            }

            ApplyLatency(nodeId, peer.Address, peer.Generation,
                (int)Math.Clamp(reply.RoundtripTime, 0, 60000), Stopwatch.GetTimestamp(), "icmp");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (PingException exception)
        {
            // UDP hello/ack remains the primary measurement path when ICMP is blocked.
            TraceLatency($"icmp_failed node={nodeId} ip={peer.Address} error={exception.InnerException?.GetType().Name ?? exception.GetType().Name}");
        }
    }

    private void MeasureAck(DiscoveryPacket packet, IPAddress address, long receivedAt)
    {
        if (packet.Type != "ack" || packet.ReplyTo is null ||
            !_pendingProbes.TryGetValue(packet.ReplyTo, out var probe) ||
            probe.NodeId != packet.NodeId || !probe.Address.Equals(address))
        {
            return;
        }
        if (!_pendingProbes.TryRemove(packet.ReplyTo, out _))
            return;
        var elapsed = Stopwatch.GetElapsedTime(probe.SentAt, receivedAt);
        TraceLatency($"probe_ack node={packet.NodeId} probe={packet.ReplyTo} age_ms={elapsed.TotalMilliseconds:F1} generation={probe.Generation}");
        RecordProbeQuality(probe, elapsed <= ProbeRetention ? elapsed.TotalMilliseconds : null);
        if (elapsed <= ProbeRetention)
        {
            ApplyLatency(packet.NodeId, address, probe.Generation,
                (int)Math.Round(Math.Clamp(elapsed.TotalMilliseconds, 0, 60000)), receivedAt, "udp");
        }
    }

    private void RecordProbeQuality(PendingProbe probe, double? rttMs)
    {
        if (_peers.TryGetValue(probe.NodeId, out var current) && current.Generation == probe.Generation &&
            current.Address.Equals(probe.Address))
            current.Quality.Record(probe.SentAt, rttMs);
    }

    private PeerState ResetMeasurement(PeerState peer) => peer with
    {
        LatencyMs = null,
        LatencyAt = 0,
        MeasurementStartedAt = Stopwatch.GetTimestamp(),
        LatencyUnavailable = false,
        Generation = Interlocked.Increment(ref _measurementGeneration),
        Quality = new PeerLinkQuality()
    };

    private void ApplyLatency(string nodeId, IPAddress address, long generation, int latency, long receivedAt, string source)
    {
        while (_peers.TryGetValue(nodeId, out var current) && current.Address.Equals(address) &&
               current.Generation == generation && (source != "icmp" || current.LatencyMs is null))
        {
            var measured = current with { LatencyMs = latency, LatencyAt = receivedAt, LatencyUnavailable = false };
            if (_peers.TryUpdate(nodeId, measured, current))
            {
                TraceLatency($"measurement node={nodeId} ip={address} source={source} rtt_ms={latency} generation={generation}");
                PublishPeers();
                return;
            }
        }
        TraceLatency($"measurement_discarded node={nodeId} source={source} generation={generation} reason=changed_peer_path_or_newer_sample");
    }

    public PeerSnapshot ApplyPeerPath(PeerSnapshot snapshot, string? path)
    {
        while (_peers.TryGetValue(snapshot.NodeId, out var current))
        {
            if (path is not null && current.PathKey != path)
            {
                var changed = current.PathKey is not null;
                var updated = changed
                    ? ResetMeasurement(current) with { PathKey = path }
                    : current with { PathKey = path };
                if (!_peers.TryUpdate(snapshot.NodeId, updated, current))
                {
                    continue;
                }
                if (changed)
                {
                    TraceLatency($"path_changed node={snapshot.NodeId} old={current.PathKey} new={path} generation={updated.Generation}; latency reset");
                    PublishPeers();
                }
                current = updated;
            }
            return snapshot with { LatencyMs = current.LatencyMs, LatencyUnavailable = current.LatencyUnavailable };
        }
        return snapshot;
    }

    private void ExpireLatencies()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var peer in _peers)
        {
            var current = peer.Value;
            var lastSample = current.LatencyMs is not null ? current.LatencyAt : current.MeasurementStartedAt;
            if (!current.LatencyUnavailable && Stopwatch.GetElapsedTime(lastSample, now) > ProbeRetention &&
                _peers.TryUpdate(peer.Key, current with { LatencyMs = null, LatencyUnavailable = true }, current))
            {
                TraceLatency($"measurement_expired node={peer.Key} generation={current.Generation} had_sample={current.LatencyMs is not null}; retry continues");
                PublishPeers();
            }
        }
    }

    /// <summary>Diagnostics authorized after the connection started also want the latency trace.</summary>
    public void EnableLatencyTrace() => _traceLatency = true;

    private void TraceLatency(string message)
    {
        if (_traceLatency)
        {
            LogDiagnostic($"MikuN2N latency: {message}");
        }
    }

    private void RemoveExpiredProbes()
    {
        var cutoff = Stopwatch.GetTimestamp() - (long)(ProbeRetention.TotalSeconds * Stopwatch.Frequency);
        foreach (var probe in _pendingProbes)
        {
            if (probe.Value.SentAt < cutoff && _pendingProbes.TryRemove(probe.Key, out _))
            {
                RecordProbeQuality(probe.Value, null);
                TraceLatency($"probe_timeout node={probe.Value.NodeId} ip={probe.Value.Address} probe={probe.Key} generation={probe.Value.Generation}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await ReceivePayloadAsync(cancellationToken);
                // Stamp before any parsing or dispatch so deserialization and the
                // PeersChanged handlers cannot inflate the reported RTT.
                var receivedAt = Stopwatch.GetTimestamp();

                // The socket is bound to Any:43121, so a MikuN2N instance reachable over
                // the physical LAN would otherwise be recorded under its physical address.
                // Everything downstream (notably set_peer_relay) treats the stored address
                // as the peer's virtual IP, so anything outside the TAP subnet is not us.
                if (result.Buffer.Length > MaxDiscoveryPacketBytes ||
                    result.RemoteEndPoint.Address.Equals(_localAddress) ||
                    !IsInTapSubnet(result.RemoteEndPoint.Address))
                {
                    continue;
                }

                DiscoveryPacket? packet;
                try
                {
                    packet = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (packet is null || string.IsNullOrEmpty(packet.NodeId) || packet.NodeId == _nodeId ||
                    packet.NodeId.Length is < 8 or > 64 ||
                    string.IsNullOrWhiteSpace(packet.Nickname) || packet.Nickname.Length > 31)
                {
                    continue;
                }

                // Reply before updating the model/UI. Otherwise the remote side measures
                // our network-interface enumeration and WPF scheduling as network RTT.
                if (packet.Type == "hello")
                {
                    await SendAsync(
                        new DiscoveryPacket("ack", _nodeId, _nickname, Guid.NewGuid().ToString("N"), packet.ProbeId),
                        result.RemoteEndPoint,
                        cancellationToken);
                }

                var knownSender = _peers.TryGetValue(packet.NodeId, out var knownPeer) &&
                                  knownPeer.Address.Equals(result.RemoteEndPoint.Address);
                if (packet.Type is "quality-request" or "quality-response" or "assist")
                {
                    if (knownSender && result.RemoteEndPoint.Port == DiscoveryPort &&
                        packet.TargetNodeId == _nodeId && Guid.TryParseExact(packet.ProbeId, "N", out _))
                        await ReceivePeerControlAsync(packet, knownPeer!, result.RemoteEndPoint, cancellationToken);
                    continue;
                }
                var now = DateTimeOffset.Now;
                _peers.AddOrUpdate(
                    packet.NodeId,
                    _ => NewPeer(packet, result.RemoteEndPoint.Address, now),
                    (_, previous) => previous.Address.Equals(result.RemoteEndPoint.Address)
                        ? previous with { Nickname = packet.Nickname.Trim(), LastSeen = now,
                            ClientVersion = NormalizeVersion(packet.ClientVersion),
                            NativeVersion = NormalizeVersion(packet.NativeVersion),
                            Ipv6WireVersion = NormalizeWireVersion(packet.Ipv6WireVersion),
                            QualityVersion = packet.QualityVersion == 1 ? 1 : 0,
                            SupportsPeerAssist = packet.SupportsPeerAssist == true }
                        : NewPeer(packet, result.RemoteEndPoint.Address, now));
                MeasureAck(packet, result.RemoteEndPoint.Address, receivedAt);
                PublishPeers();

                if (packet.Type == "relay-policy-ack" &&
                    packet.TargetNodeId == _nodeId &&
                    packet.ForceRelay is not null &&
                    packet.ReplyTo is not null &&
                    knownSender &&
                    _pendingRelayPolicies.TryGetValue(packet.ReplyTo, out var completion))
                {
                    completion.TrySetResult(packet.ForceRelay.Value);
                }

                if (packet.Type == "relay-policy" &&
                    packet.TargetNodeId == _nodeId &&
                    packet.ForceRelay is not null &&
                    Guid.TryParseExact(packet.ProbeId, "N", out _) &&
                    knownSender &&
                    _peers.TryGetValue(packet.NodeId, out var sender) &&
                    sender.Address.Equals(result.RemoteEndPoint.Address))
                {
                    try
                    {
                        RelayPolicyRequested?.Invoke(
                            this,
                            new RelayPolicyRequest(
                                packet.ProbeId,
                                packet.NodeId,
                                packet.Nickname.Trim(),
                                result.RemoteEndPoint.Address.ToString(),
                                packet.ForceRelay.Value,
                                packet.Reassert == true));
                    }
                    catch (Exception exception)
                    {
                        LogDiagnostic(
                            $"MikuN2N：好友链路策略回调异常，后台发现仍继续运行：{exception.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                await DelayAfterSocketFailureAsync(cancellationToken);
            }
            catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
            {
                await DelayAfterSocketFailureAsync(cancellationToken);
            }
        }
    }

    private async Task ReceivePeerControlAsync(
        DiscoveryPacket packet, PeerState sender, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (packet.Type == "quality-request" && IsValidNodeId(packet.QualityTargetNodeId) &&
            packet.QualityTargetNodeId != _nodeId && TryAllowQualityResponse(packet.NodeId))
        {
            var quality = GetLinkQuality(packet.QualityTargetNodeId!);
            await SendAsync(new DiscoveryPacket("quality-response", _nodeId, _nickname,
                    Guid.NewGuid().ToString("N"), packet.ProbeId, TargetNodeId: packet.NodeId,
                    QualityTargetNodeId: packet.QualityTargetNodeId, Quality: quality),
                endpoint, cancellationToken);
        }
        else if (packet.Type == "quality-response" && packet.ReplyTo is not null &&
            _pendingQualityQueries.TryGetValue(packet.ReplyTo, out var query) &&
            query.ReporterNodeId == packet.NodeId && query.ReporterAddress.Equals(endpoint.Address) &&
            query.TargetNodeId == packet.QualityTargetNodeId &&
            (packet.Quality is null || IsValidQuality(packet.Quality, query.TargetNodeId)))
        {
            // Include the query RTT conservatively; remote clocks are never compared.
            var quality = packet.Quality is null ? null : packet.Quality with
            {
                AgeSeconds = packet.Quality.AgeSeconds + Stopwatch.GetElapsedTime(query.SentAt).TotalSeconds,
                CapturedAt = Stopwatch.GetTimestamp()
            };
            query.Completion.TrySetResult(quality);
        }
        else if (packet.Type == "assist" && _supportsPeerAssist && sender.SupportsPeerAssist &&
            packet.AssistMessage is { } message && IsValidAssistMessage(message, packet.NodeId, _nodeId))
        {
            try
            {
                AssistMessageReceived?.Invoke(this, new PeerAssistEnvelope(
                    packet.NodeId, sender.Nickname, endpoint.Address.ToString(), message));
            }
            catch (Exception exception)
            {
                LogDiagnostic($"MikuN2N：好友协助消息回调异常：{exception.Message}");
            }
        }
    }

    private bool TryAllowQualityResponse(string nodeId)
    {
        while (_peers.TryGetValue(nodeId, out var current))
        {
            var now = Stopwatch.GetTimestamp();
            if (current.LastQualityResponseAt != 0 &&
                Stopwatch.GetElapsedTime(current.LastQualityResponseAt, now).TotalMilliseconds < 500)
                return false;
            if (_peers.TryUpdate(nodeId, current with { LastQualityResponseAt = now }, current))
                return true;
        }
        return false;
    }

    private static bool IsValidNodeId(string? value) => value is { Length: >= 8 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsValidAssistMessage(PeerAssistMessage message, string sender, string recipient) =>
        message.Kind is { Length: >= 1 and <= 32 } &&
        message.Kind.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
        Guid.TryParseExact(message.CircuitId, "N", out _) &&
        (message.ReplyTo is null || Guid.TryParseExact(message.ReplyTo, "N", out _)) &&
        (message.Detail is null || message.Detail.Length <= 256) &&
        IsValidNodeId(message.RequesterNodeId) && IsValidNodeId(message.TargetNodeId) && IsValidNodeId(message.RelayNodeId) &&
        message.RequesterNodeId != message.TargetNodeId && message.RequesterNodeId != message.RelayNodeId &&
        message.TargetNodeId != message.RelayNodeId &&
        (sender == message.RequesterNodeId || sender == message.TargetNodeId || sender == message.RelayNodeId) &&
        (recipient == message.RequesterNodeId || recipient == message.TargetNodeId || recipient == message.RelayNodeId);

    private static bool IsValidQuality(LinkQualitySnapshot quality, string targetNodeId) =>
        quality.PeerNodeId == targetNodeId && quality.WindowSeconds == 60 &&
        quality.Samples is >= 0 and <= 64 && quality.Lost >= 0 && quality.Lost <= quality.Samples &&
        double.IsFinite(quality.AgeSeconds) && quality.AgeSeconds is >= 0 and <= 3600 &&
        Enum.IsDefined(quality.Mode) && ValidMetric(quality.RttMs) && ValidMetric(quality.JitterMs) &&
        ValidMetric(quality.P95RttMs) &&
        ((quality.Samples == quality.Lost && quality.RttMs is null && quality.P95RttMs is null) ||
         (quality.Samples > quality.Lost && quality.RttMs is not null && quality.P95RttMs is not null)) &&
        (quality.Samples == 0 ? quality.LossPercent is null :
            quality.LossPercent is { } loss && double.IsFinite(loss) &&
            Math.Abs(loss - 100.0 * quality.Lost / quality.Samples) < 0.001);

    private static bool ValidMetric(double? metric) => metric is null ||
        double.IsFinite(metric.Value) && metric.Value is >= 0 and <= 10000;

    private async Task SendAsync(DiscoveryPacket packet, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(StampIdentity(packet));
        if (payload.Length > MaxDiscoveryPacketBytes)
            throw new InvalidOperationException("好友消息过长。");
        await SendPayloadAsync(payload, endpoint, cancellationToken);
    }

    private async Task SendPayloadAsync(
        byte[] payload,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        UdpClient udp;
        lock (_socketGate)
        {
            udp = _udp;
        }
        try
        {
            await udp.SendAsync(payload, endpoint, cancellationToken);
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RecoverSocketAsync(udp, "发送", exception, cancellationToken);
            throw;
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            await RecoverSocketAsync(udp, "发送时发现 socket 已关闭", null, cancellationToken);
            throw;
        }
    }

    private async Task<UdpReceiveResult> ReceivePayloadAsync(CancellationToken cancellationToken)
    {
        UdpClient udp;
        lock (_socketGate)
        {
            udp = _udp;
        }
        try
        {
            return await udp.ReceiveAsync(cancellationToken);
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RecoverSocketAsync(udp, "接收", exception, cancellationToken);
            throw;
        }
        catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
        {
            await RecoverSocketAsync(udp, "接收时发现 socket 已关闭", null, cancellationToken);
            throw;
        }
    }

    private async Task RecoverSocketAsync(
        UdpClient failedSocket,
        string operation,
        SocketException? exception,
        CancellationToken cancellationToken)
    {
        await _socketRecoveryGate.WaitAsync(cancellationToken);
        try
        {
            lock (_socketGate)
            {
                if (!ReferenceEquals(_udp, failedSocket))
                {
                    return;
                }
                failedSocket.Dispose();
            }

            LogSocketFailure(operation, exception);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var replacement = CreateUdpClient();
                    lock (_socketGate)
                    {
                        if (Volatile.Read(ref _stopped) != 0 ||
                            cancellationToken.IsCancellationRequested)
                        {
                            replacement.Dispose();
                            return;
                        }
                        _udp = replacement;
                    }
                    LogDiagnostic("MikuN2N：好友发现 UDP socket 已重建，收发循环继续运行。");
                    return;
                }
                catch (SocketException retryException)
                {
                    LogSocketFailure("重建", retryException);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        finally
        {
            _socketRecoveryGate.Release();
        }
    }

    private static UdpClient CreateUdpClient()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
            ExclusiveAddressUse = false
        };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        return udp;
    }

    private static async Task DelayAfterSocketFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LogSocketFailure(string operation, SocketException? exception)
    {
        var detail = exception is null
            ? string.Empty
            : $"（WSA {exception.NativeErrorCode}：{exception.Message}）";
        LogDiagnostic($"MikuN2N：好友发现 UDP {operation}异常{detail}，正在恢复。");
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            _diagnosticLog?.Invoke(message);
        }
        catch
        {
        }
    }

    private void RemoveExpiredPeers()
    {
        var cutoff = DateTimeOffset.Now - PeerExpiry;
        var changed = false;
        foreach (var peer in _peers)
        {
            if (peer.Value.LastSeen < cutoff && _peers.TryRemove(peer.Key, out _))
            {
                changed = true;
            }
        }
        if (changed)
        {
            PublishPeers();
        }
    }

    // Only flags work to do. PeersChanged runs EdgeController's peer cross-referencing,
    // which blocks on SendARP; invoking it inline stalled the receive loop and that delay
    // landed straight in the next probe's measured RTT.
    private void PublishPeers() => _publishRequested = true;

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PublishInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            ExpireLatencies();
            if (!_publishRequested)
            {
                continue;
            }
            _publishRequested = false;

            var snapshots = GetPeers();
            try
            {
                PeersChanged?.Invoke(this, snapshots);
            }
            catch (Exception exception)
            {
                LogDiagnostic($"MikuN2N：好友列表发布回调异常，后台发现仍继续运行：{exception.Message}");
            }
        }
    }

    private bool TryResolveNodeId(IPAddress address, out string nodeId)
    {
        foreach (var peer in _peers)
        {
            if (peer.Value.Address.Equals(address))
            {
                nodeId = peer.Key;
                return true;
            }
        }
        nodeId = string.Empty;
        return false;
    }

    private bool IsInTapSubnet(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        (ToUInt32(address) & _tapMask) == _tapNetwork;

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3]
            : 0;
    }

    private static IPAddress CalculateBroadcast(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var result = new byte[addressBytes.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        }
        return new IPAddress(result);
    }

    private sealed record DiscoveryPacket(
        string Type,
        string NodeId,
        string Nickname,
        string ProbeId,
        string? ReplyTo,
        bool? ForceRelay = null,
        string? TargetNodeId = null,
        bool? Reassert = null,
        string? ClientVersion = null,
        string? NativeVersion = null,
        int? Ipv6WireVersion = null,
        int? QualityVersion = null,
        bool? SupportsPeerAssist = null,
        string? QualityTargetNodeId = null,
        LinkQualitySnapshot? Quality = null,
        PeerAssistMessage? AssistMessage = null);

    private PeerState NewPeer(DiscoveryPacket packet, IPAddress address, DateTimeOffset now) =>
        new(packet.Nickname.Trim(), address, null, now, Stopwatch.GetTimestamp(),
            Interlocked.Increment(ref _measurementGeneration),
            new PeerLinkQuality(),
            ClientVersion: NormalizeVersion(packet.ClientVersion),
            NativeVersion: NormalizeVersion(packet.NativeVersion),
            Ipv6WireVersion: NormalizeWireVersion(packet.Ipv6WireVersion),
            QualityVersion: packet.QualityVersion == 1 ? 1 : 0,
            SupportsPeerAssist: packet.SupportsPeerAssist == true);

    private sealed record NativeIdentity(string? Version, int? WireVersion);

    private sealed record PendingProbe(string NodeId, IPAddress Address, long SentAt, long Generation);

    private sealed record PendingQualityQuery(string ReporterNodeId, IPAddress ReporterAddress,
        string TargetNodeId, long SentAt, TaskCompletionSource<LinkQualitySnapshot?> Completion);

    private sealed record PeerState(
        string Nickname,
        IPAddress Address,
        int? LatencyMs,
        DateTimeOffset LastSeen,
        long MeasurementStartedAt,
        long Generation,
        PeerLinkQuality Quality,
        long LatencyAt = 0,
        string? PathKey = null,
        bool LatencyUnavailable = false,
        string? ClientVersion = null,
        string? NativeVersion = null,
        int? Ipv6WireVersion = null,
        int QualityVersion = 0,
        bool SupportsPeerAssist = false,
        PeerConnectionMode Mode = PeerConnectionMode.Unknown,
        long LastQualityResponseAt = 0);
}

public sealed record RelayPolicyRequest(
    string CommandId,
    string NodeId,
    string Nickname,
    string VirtualIp,
    bool ForceRelay,
    bool IsReassert = false);
