using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private const int DiscoveryPort = 43121;
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeRetention = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);
    private readonly UdpClient _udp;
    private readonly string _nodeId;
    private readonly string _nickname;
    private readonly IPAddress _localAddress;
    private readonly uint _tapNetwork;
    private readonly uint _tapMask;
    private readonly IPEndPoint _broadcastEndpoint;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, PeerState> _peers = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingRelayPolicies = new();
    private readonly ConcurrentDictionary<string, long> _pendingProbes = new();
    private readonly Task _receiveTask;
    private readonly Task _broadcastTask;
    private readonly Task _publishTask;
    private volatile bool _publishRequested;
    private int _stopped;

    public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    public event EventHandler<RelayPolicyRequest>? RelayPolicyRequested;

    public PeerDiscoveryService(string nodeId, string nickname, IPAddress localAddress, IPAddress subnetMask)
    {
        _nodeId = nodeId;
        _nickname = nickname;
        _localAddress = localAddress;
        _tapMask = ToUInt32(subnetMask);
        _tapNetwork = ToUInt32(localAddress) & _tapMask;
        _broadcastEndpoint = new IPEndPoint(CalculateBroadcast(localAddress, subnetMask), DiscoveryPort);
        _udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
            ExclusiveAddressUse = false
        };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
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
        _cancellation.Dispose();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _cancellation.Cancel();
        _udp.Dispose();
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
                await SendProbeAsync(peer.Value.Address, cancellationToken);
            }

            RemoveExpiredProbes();
            RemoveExpiredPeers();
            await Task.Delay(BroadcastInterval, cancellationToken);
        }
    }

    private async Task SendProbeAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var probeId = Guid.NewGuid().ToString("N");
        // Serialize first so only the wire time is inside the measured window.
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryPacket("hello", _nodeId, _nickname, probeId, null));
        var endpoint = new IPEndPoint(address, DiscoveryPort);
        _pendingProbes[probeId] = Stopwatch.GetTimestamp();
        await _udp.SendAsync(payload, endpoint, cancellationToken);
    }

    private int? MeasureAck(DiscoveryPacket packet, long receivedAt)
    {
        if (packet.Type != "ack" || packet.ReplyTo is null ||
            !_pendingProbes.TryRemove(packet.ReplyTo, out var sentAt))
        {
            return null;
        }

        var elapsed = (receivedAt - sentAt) * 1000.0 / Stopwatch.Frequency;
        return (int)Math.Round(Math.Clamp(elapsed, 0, 60000));
    }

    private void RemoveExpiredProbes()
    {
        var cutoff = Stopwatch.GetTimestamp() - (long)(ProbeRetention.TotalSeconds * Stopwatch.Frequency);
        foreach (var probe in _pendingProbes)
        {
            if (probe.Value < cutoff)
            {
                _pendingProbes.TryRemove(probe.Key, out _);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellationToken);
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            // Stamp before any parsing or dispatch so deserialization and the
            // PeersChanged handlers cannot inflate the reported RTT.
            var receivedAt = Stopwatch.GetTimestamp();

            // The socket is bound to Any:43121, so a MikuN2N instance reachable over
            // the physical LAN would otherwise be recorded under its physical address.
            // Everything downstream (notably set_peer_relay) treats the stored address
            // as the peer's virtual IP, so anything outside the TAP subnet is not us.
            if (result.RemoteEndPoint.Address.Equals(_localAddress) ||
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

            if (packet is null || packet.NodeId == _nodeId ||
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
            var measured = MeasureAck(packet, receivedAt);
            var now = DateTimeOffset.Now;
            _peers.AddOrUpdate(
                packet.NodeId,
                _ => new PeerState(packet.Nickname.Trim(), result.RemoteEndPoint.Address, measured, now),
                (_, previous) => new PeerState(
                    packet.Nickname.Trim(),
                    result.RemoteEndPoint.Address,
                    measured ?? previous.LatencyMs,
                    now));
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
        }
    }

    private async Task SendAsync(DiscoveryPacket packet, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet);
        await _udp.SendAsync(payload, endpoint, cancellationToken);
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
            if (!_publishRequested)
            {
                continue;
            }
            _publishRequested = false;

            var snapshots = _peers
                .Select(peer => new PeerSnapshot(
                    peer.Key,
                    peer.Value.Nickname,
                    peer.Value.Address.ToString(),
                    peer.Value.LatencyMs,
                    peer.Value.LastSeen))
                .OrderBy(peer => peer.Nickname, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            PeersChanged?.Invoke(this, snapshots);
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
        bool? Reassert = null);

    private sealed record PeerState(
        string Nickname,
        IPAddress Address,
        int? LatencyMs,
        DateTimeOffset LastSeen);
}

public sealed record RelayPolicyRequest(
    string CommandId,
    string NodeId,
    string Nickname,
    string VirtualIp,
    bool ForceRelay,
    bool IsReassert = false);
