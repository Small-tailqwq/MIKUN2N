using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed partial class EdgeController : IAsyncDisposable
{
    private const int EdgeUdpPort = 50001;
    private static readonly string[] ConflictingProcessNames = ["n3n-edge", "n2n-edge", "edge"];
    // Edge children this process started, so an emergency shutdown can clean up exactly
    // what belongs to us instead of every n2n-family process on the machine.
    private static readonly ConcurrentDictionary<int, byte> LaunchedEdgeProcessIds = new();
    private static readonly TimeSpan PunchDisplayWindow = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RelayReconcileInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelayPolicyQuietWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    private readonly string _runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime");
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MikuN2N",
        "logs");
    private readonly string _n3nConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "n3n");
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // Node id -> virtual IP. Lives on the controller, not on EdgeRun, so it survives the
    // edge restarts that would otherwise drop the policy on this side only.
    private readonly object _relayPolicyGate = new();
    private readonly Dictionary<string, string> _forcedRelayPeers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _relayPolicyChangedAt = new(StringComparer.Ordinal);
    private EdgeRun? _currentRun;
    private ConnectionParameters? _parameters;
    private CancellationTokenSource? _connectionCancellation;
    private bool _wantConnected;
    private int _sessionId;
    private int _restartAttempt;
    private volatile bool _useRotatingMac;
    private volatile string? _tapAdapterId;
    private volatile bool _tapAdapterPinFailed;

    public event EventHandler<ConnectionSnapshot>? SnapshotChanged;
    public event EventHandler<string>? LogReceived;

    public async Task SetPeerRelayAsync(
        string virtualIp,
        bool forceRelay,
        CancellationToken cancellationToken = default)
    {
        EdgeRun run;
        lock (_stateGate)
        {
            run = _currentRun
                ?? throw new InvalidOperationException("当前没有正在运行的连接。");
        }

        var discovery = run.PeerDiscovery
            ?? throw new InvalidOperationException("好友同步通道尚未就绪，请稍后重试。");
        // Resolve the stable node id before touching any state: the policy has to be
        // remembered per friend so it can be re-applied after an edge restart, and the
        // supernode-assigned virtual IP is only stable within a session.
        var target = run.Peers.FirstOrDefault(peer =>
            string.Equals(peer.VirtualIp, virtualIp, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("尚未找到该好友的同步通道，请稍后重试。");
        var nodeId = target.NodeId;
        var edgeMac = ResolvePeerEdgeMac(run, virtualIp, target.Nickname);

        var updated = await run.ManagementClient.SetPeerRelayAsync(
            virtualIp,
            forceRelay,
            cancellationToken,
            edgeMac);
        if (updated == 0)
        {
            AppendLog(
                run,
                $"链路策略：n3n 尚未持有 {virtualIp} 的连接条目，策略已记录，将在该好友出现后生效。");
        }
        try
        {
            await discovery.SendRelayPolicyAsync(virtualIp, forceRelay, cancellationToken);
        }
        catch (Exception syncException)
        {
            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await run.ManagementClient.SetPeerRelayAsync(
                    virtualIp,
                    !forceRelay,
                    rollbackTimeout.Token,
                    edgeMac);
                AppendLog(
                    run,
                    $"链路策略：{virtualIp} 未完成双端确认，已撤销本机切换以避免单向中转。");
            }
            catch (Exception rollbackException)
            {
                AppendLog(
                    run,
                    $"链路策略：{virtualIp} 双端同步失败，且本机回滚失败：{rollbackException.Message}");
            }
            // The peer applies before it acknowledges, so a lost acknowledgement leaves it
            // forced while we roll back - exactly the one-sided relay this protocol exists
            // to prevent. Tell it to revert too instead of only undoing our own side.
            try
            {
                using var revertTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await discovery.SendRelayPolicyNoticeAsync(
                    virtualIp,
                    !forceRelay,
                    attempts: 4,
                    reassert: false,
                    revertTimeout.Token);
            }
            catch (Exception revertException)
            {
                AppendLog(
                    run,
                    $"链路策略：未能通知 {virtualIp} 一同撤销：{revertException.Message}");
            }
            throw new InvalidOperationException(syncException.Message, syncException);
        }

        RememberRelayPolicy(nodeId, virtualIp, forceRelay);
        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentRun, run))
            {
                return;
            }
        }
        AppendLog(
            run,
            forceRelay
                ? $"链路策略：双方已同步将 {virtualIp} 切换为 pSp 中继；任一方均可取消。"
                : $"链路策略：双方已同步取消 {virtualIp} 的强制中继，恢复自动 P2P 探测。");
        PublishConnected(run);
    }

    public async Task RetryPeerPunchAsync(
        string virtualIp,
        CancellationToken cancellationToken = default)
    {
        EdgeRun run;
        lock (_stateGate)
        {
            run = _currentRun
                ?? throw new InvalidOperationException("当前没有正在运行的连接。");
        }

        var discovery = run.PeerDiscovery
            ?? throw new InvalidOperationException("好友同步通道尚未就绪，请稍后重试。");
        var target = run.Peers.FirstOrDefault(peer =>
            string.Equals(peer.VirtualIp, virtualIp, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("尚未找到该好友的同步通道，请稍后重试。");
        var edgeMac = ResolvePeerEdgeMac(run, virtualIp, target.Nickname);

        await run.ManagementClient.SetPeerRelayAsync(
            virtualIp,
            false,
            cancellationToken,
            edgeMac);
        try
        {
            // Releasing relay policy also clears n3n's per-MAC terminal failure
            // budget. The peer applies the same command before acknowledging it,
            // so the new round starts from both endpoints.
            await discovery.SendRelayPolicyAsync(virtualIp, false, cancellationToken);
        }
        catch (Exception exception)
        {
            AppendLog(
                run,
                $"P2P 重试：本机已复位，但未能与 {virtualIp} 同步复位；本轮可能无法命中。");
            throw new InvalidOperationException(exception.Message, exception);
        }

        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentRun, run))
            {
                return;
            }
        }
        AppendLog(run, $"P2P 重试：双方已复位 {virtualIp} 的失败预算，开始新一轮自动打洞。");
        PublishConnected(run);
    }

    /// <summary>
    /// The peer's n3n MAC, if the management rows identify it. n3n only learns a peer's
    /// virtual IPv4 from a REGISTER, so a peer first seen through a data packet has none
    /// and an IPv4-keyed relay request silently matches nothing on this side - which is
    /// exactly the one-sided relay the two-sided protocol exists to prevent. The MAC is
    /// the one key every management row always carries.
    /// </summary>
    private static string? ResolvePeerEdgeMac(EdgeRun run, string virtualIp, string? nickname)
    {
        var rowMacs = run.PeerRowMacs;
        if (rowMacs.TryGetValue($"ip:{virtualIp}", out var mac) && mac.Length > 0)
        {
            return mac;
        }
        if (nickname is not null &&
            rowMacs.TryGetValue($"name:{nickname}", out mac) &&
            mac.Length > 0)
        {
            return mac;
        }
        lock (run.PeerModeGate)
        {
            if (run.PeerMacAddresses.TryGetValue(virtualIp, out var cached))
            {
                return cached;
            }
        }
        return TryResolveMacAddress(virtualIp);
    }

    /// <returns>True when this changed the remembered policy.</returns>
    private bool RememberRelayPolicy(string nodeId, string virtualIp, bool forceRelay)
    {
        lock (_relayPolicyGate)
        {
            var had = _forcedRelayPeers.ContainsKey(nodeId);
            if (forceRelay)
            {
                _forcedRelayPeers[nodeId] = virtualIp;
            }
            else
            {
                _forcedRelayPeers.Remove(nodeId);
            }
            if (had != forceRelay)
            {
                _relayPolicyChangedAt[nodeId] = DateTimeOffset.Now;
                return true;
            }
            return false;
        }
    }

    // A re-assertion the peer sent before it learned about our change is stale. Without
    // this window an explicit cancel could be resurrected by an in-flight keepalive and
    // the pair would latch on forever.
    private bool IsStaleRelayReassert(string nodeId) =>
        DateTimeOffset.Now - GetRelayPolicyChangedAt(nodeId) < RelayPolicyQuietWindow;

    private bool IsRelayForced(string nodeId)
    {
        lock (_relayPolicyGate)
        {
            return _forcedRelayPeers.ContainsKey(nodeId);
        }
    }

    private DateTimeOffset GetRelayPolicyChangedAt(string nodeId)
    {
        lock (_relayPolicyGate)
        {
            return _relayPolicyChangedAt.TryGetValue(nodeId, out var changedAt)
                ? changedAt
                : DateTimeOffset.MinValue;
        }
    }

    private async Task ApplyRemoteRelayPolicyAsync(EdgeRun run, RelayPolicyRequest request)
    {
        if (request.IsReassert &&
            request.ForceRelay != IsRelayForced(request.NodeId) &&
            IsStaleRelayReassert(request.NodeId))
        {
            return;
        }

        bool alreadyApplied;
        lock (run.RelayPolicyGate)
        {
            alreadyApplied = !run.AppliedRelayPolicyCommands.Add(request.CommandId);
            if (!alreadyApplied && run.AppliedRelayPolicyCommands.Count > 128)
            {
                run.AppliedRelayPolicyCommands.Clear();
                run.AppliedRelayPolicyCommands.Add(request.CommandId);
            }
        }

        // A request is retransmitted several times under one command id. Dropping the
        // duplicates silently also dropped their acknowledgements, so a single lost
        // acknowledgement burst guaranteed the sender timed out and rolled back alone.
        if (alreadyApplied)
        {
            try
            {
                using var ackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                if (run.PeerDiscovery is not null)
                {
                    await run.PeerDiscovery.SendRelayPolicyAcknowledgementAsync(
                        request,
                        ackTimeout.Token);
                }
            }
            catch
            {
                // The sender retransmits; a failed duplicate acknowledgement is not fatal.
            }
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await run.ManagementClient.SetPeerRelayAsync(
                request.VirtualIp,
                request.ForceRelay,
                timeout.Token,
                ResolvePeerEdgeMac(run, request.VirtualIp, request.Nickname));
            if (run.PeerDiscovery is not null)
            {
                await run.PeerDiscovery.SendRelayPolicyAcknowledgementAsync(
                    request,
                    timeout.Token);
            }
            // Remember the peer's policy as ours too. Both sides then re-assert it, so a
            // side that restarts is pushed back into the same state by the other one.
            var changed = RememberRelayPolicy(
                request.NodeId,
                request.VirtualIp,
                request.ForceRelay);
            lock (_stateGate)
            {
                if (!ReferenceEquals(_currentRun, run))
                {
                    return;
                }
            }
            if (!changed)
            {
                return; // Periodic re-assertion of an unchanged policy stays silent.
            }

            AppendLog(
                run,
                request.ForceRelay
                    ? $"链路策略：{request.Nickname} 请求双方使用 pSp；本机已同步切换 {request.VirtualIp}。"
                    : $"链路策略：{request.Nickname} 已取消强制 pSp；本机同步恢复 {request.VirtualIp} 的自动 P2P。");
            PublishConnected(run);
        }
        catch (Exception exception)
        {
            lock (run.RelayPolicyGate)
            {
                run.AppliedRelayPolicyCommands.Remove(request.CommandId);
            }
            AppendLog(
                run,
                $"链路策略：未能同步 {request.Nickname} 的 pSp 设置：{exception.Message}");
        }
    }

    /// <summary>
    /// Re-applies every remembered relay policy to the edge and re-announces it to the
    /// peer. The forced-relay list lives inside the n3n process, so an unexpected edge
    /// exit silently drops it on one side and restores the one-sided relay this protocol
    /// exists to prevent; the peer's own restart is covered by the re-announcement.
    /// </summary>
    private async Task ReconcileRelayPoliciesAsync(EdgeRun run, CancellationToken cancellationToken)
    {
        KeyValuePair<string, string>[] desired;
        lock (_relayPolicyGate)
        {
            desired = _forcedRelayPeers.ToArray();
        }
        if (desired.Length == 0)
        {
            run.RelayPolicyRestored = true;
            return;
        }

        var restoring = !run.RelayPolicyRestored;
        if (!restoring && DateTimeOffset.Now < run.NextRelayReconcileAt)
        {
            return;
        }
        run.NextRelayReconcileAt = DateTimeOffset.Now + RelayReconcileInterval;
        var failed = false;

        foreach (var (nodeId, storedIp) in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var peer = run.Peers.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
            var virtualIp = peer?.VirtualIp ?? storedIp;
            if (peer is not null && !string.Equals(virtualIp, storedIp, StringComparison.Ordinal))
            {
                lock (_relayPolicyGate)
                {
                    if (_forcedRelayPeers.ContainsKey(nodeId))
                    {
                        _forcedRelayPeers[nodeId] = virtualIp;
                    }
                }
            }

            var effective = peer is not null &&
                            GetPeerConnectionMode(run, peer) == PeerConnectionMode.ForcedRelayed;
            if (restoring || !effective)
            {
                try
                {
                    var applied = await run.ManagementClient.SetPeerRelayAsync(
                        virtualIp,
                        true,
                        cancellationToken,
                        ResolvePeerEdgeMac(run, virtualIp, peer?.Nickname));
                    if (restoring)
                    {
                        AppendLog(
                            run,
                            $"链路策略：连接已重建，重新对 {virtualIp} 应用手动 pSp 中继。");
                    }
                    // A policy that keeps matching no peer entry is a one-sided relay in
                    // the making: this side stays on P2P while the friend relays, which
                    // is what the latency flapping looks like. Say so instead of retrying
                    // in silence.
                    if (applied == 0 && peer is not null && !run.RelayUnmatchedWarningLogged)
                    {
                        run.RelayUnmatchedWarningLogged = true;
                        AppendLog(
                            run,
                            $"链路策略：n3n 仍未把 {virtualIp} 的连接条目与该好友对应上，"
                            + "手动 pSp 在本机尚未生效；正在持续重试。");
                    }
                    else if (applied > 0)
                    {
                        run.RelayUnmatchedWarningLogged = false;
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    if (!run.RelayReconcileWarningLogged)
                    {
                        run.RelayReconcileWarningLogged = true;
                        AppendLog(
                            run,
                            $"链路策略：暂时无法恢复 {virtualIp} 的手动 pSp，将继续后台重试：{exception.Message}");
                    }
                    continue;
                }
            }

            if (peer is not null && run.PeerDiscovery is { } discovery)
            {
                try
                {
                    await discovery.SendRelayPolicyNoticeAsync(
                        virtualIp,
                        true,
                        attempts: 1,
                        reassert: true,
                        cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Best effort; the next reconcile pass retries.
                }
            }
        }

        run.RelayPolicyRestored = true;
        if (!failed)
        {
            run.RelayReconcileWarningLogged = false;
        }
    }

    // This remains true while an unexpectedly stopped edge is being recovered.
    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _wantConnected;
            }
        }
    }

    public async Task StartAsync(
        string server,
        string community,
        string nickname,
        string nodeId,
        string encryptionKey,
        string? nodeName = null,
        bool experimentalIpv6P2p = false,
        CancellationToken cancellationToken = default,
        TestDiagnosticsSession? diagnostics = null)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var conflictingProcesses = FindConflictingEdgeProcesses();
            if (conflictingProcesses.Count > 0)
            {
                throw new InvalidOperationException(
                    "检测到另一个 n2n/n3n 客户端仍在运行。请返回主界面确认是否结束旧进程。");
            }

            var edgePath = ResolveEdgePath();
            if (!File.Exists(edgePath))
            {
                throw new FileNotFoundException("缺少 Runtime\\edge.exe，程序包不完整。", edgePath);
            }

            Directory.CreateDirectory(_runtimeDirectory);
            Directory.CreateDirectory(_logDirectory);

            var engine = string.Equals(Path.GetFileName(edgePath), "n3n-edge.exe", StringComparison.OrdinalIgnoreCase)
                ? EdgeEngine.N3n
                : EdgeEngine.LegacyN2n;
            var parameters = new ConnectionParameters(server, community, nickname, nodeId, encryptionKey, engine, nodeName, experimentalIpv6P2p, diagnostics);
            diagnostics?.Write("connection_requested", new
            {
                server, community, nickname, nodeId, engine = engine.ToString(), experimentalIpv6P2p,
                encryptionEnabled = !string.IsNullOrEmpty(encryptionKey), edgePort = EdgeUdpPort
            });
            CancellationTokenSource connectionCancellation;
            int sessionId;
            lock (_stateGate)
            {
                _wantConnected = true;
                _parameters = parameters;
                _restartAttempt = 0;
                sessionId = ++_sessionId;
                connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _connectionCancellation = connectionCancellation;
            }

            Publish(new ConnectionSnapshot(
                ConnectionState.Connecting,
                "正在连接…",
                "正在启动虚拟网络并联系服务器，请稍候。"));

            try
            {
                await StartRunAsync(sessionId, parameters, connectionCancellation.Token);
            }
            catch
            {
                lock (_stateGate)
                {
                    if (_sessionId == sessionId)
                    {
                        _wantConnected = false;
                        _parameters = null;
                        _connectionCancellation = null;
                    }
                }
                connectionCancellation.Cancel();
                connectionCancellation.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            // A user-requested disconnect ends the session, so the manual relay choices
            // end with it; an unexpected edge exit deliberately keeps them.
            lock (_relayPolicyGate)
            {
                _forcedRelayPeers.Clear();
                _relayPolicyChangedAt.Clear();
            }

            EdgeRun? run;
            CancellationTokenSource? connectionCancellation;
            lock (_stateGate)
            {
                _wantConnected = false;
                _parameters = null;
                _restartAttempt = 0;
                _sessionId++;
                run = _currentRun;
                _currentRun = null;
                connectionCancellation = _connectionCancellation;
                _connectionCancellation = null;
                if (run is not null)
                {
                    run.StopRequested = true;
                }
            }

            connectionCancellation?.Cancel();
            TryCancel(run?.MonitorCancellation);

            if (run?.Process is { HasExited: false } process)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await run.ManagementClient.StopAsync(timeout.Token);
                }
                catch
                {
                    // n3n leaves its select() loop as soon as it reads the stop request and
                    // exits without ever flushing the JSON-RPC reply, so a transport failure
                    // here is the normal path, not a reason to reach for Kill(). Killing it
                    // instead would skip n3n's own cleanup, which restores the TAP metric.
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }

            if (run is not null)
            {
                CleanupRun(run);
                run.Process.Dispose();
            }
            connectionCancellation?.Dispose();

            Publish(new ConnectionSnapshot(
                ConnectionState.Disconnected,
                "尚未连接",
                "填写昵称后点击“连接”，即可加入朋友们的虚拟局域网。"));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public static bool HasTapAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(IsTapAdapter);

    public static IReadOnlyList<ConflictingEdgeProcess> FindConflictingEdgeProcesses()
    {
        var result = new List<ConflictingEdgeProcess>();
        foreach (var processName in ConflictingProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        DateTimeOffset? startedAt = null;
                        try
                        {
                            startedAt = process.StartTime;
                        }
                        catch
                        {
                            // Process metadata may be protected; PID and name are sufficient.
                        }
                        result.Add(new ConflictingEdgeProcess(process.Id, process.ProcessName, startedAt));
                    }
                    catch
                    {
                        // The process exited between enumeration and inspection.
                    }
                }
            }
        }

        return result
            .DistinctBy(process => process.ProcessId)
            .OrderBy(process => process.StartedAt)
            .ThenBy(process => process.ProcessId)
            .ToArray();
    }

    public static async Task TerminateConflictingEdgeProcessesAsync(
        IReadOnlyCollection<int> confirmedProcessIds,
        CancellationToken cancellationToken = default)
    {
        var confirmed = confirmedProcessIds.ToHashSet();
        var failures = new List<string>();

        foreach (var processId in confirmed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!ConflictingProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    continue; // PID was reused by an unrelated process.
                }
                if (process.HasExited)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (ArgumentException)
            {
                // The process exited after the confirmation dialog.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"PID {processId}：{exception.Message}");
            }
        }

        var remaining = FindConflictingEdgeProcesses()
            .Where(process => confirmed.Contains(process.ProcessId))
            .Select(process => process.ProcessId)
            .ToArray();
        if (remaining.Length > 0)
        {
            failures.Add($"仍在运行的 PID：{string.Join("、", remaining)}");
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"无法结束部分旧进程。{string.Join("；", failures)}");
        }
    }

    private string? TapAddress()
    {
        foreach (var adapter in OrderedTapAdapters())
        {
            try
            {
                var address = adapter.GetIPProperties().UnicastAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(IsUsableTapAddress);
                if (address is not null)
                {
                    return address.ToString();
                }
            }
            catch (NetworkInformationException)
            {
                // Adapter state can change while edge is starting; retry on the next poll.
            }
        }
        return null;
    }

    /// <summary>
    /// TAP adapters, most likely one first. A machine can easily carry several - a
    /// gaming accelerator's bundled TAP driver matches the same loose name test as the
    /// adapter we ship - so the one n3n was actually told to open always wins.
    /// </summary>
    private IEnumerable<NetworkInterface> OrderedTapAdapters()
    {
        var pinned = _tapAdapterId;
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsTapAdapter)
            .OrderByDescending(adapter =>
                pinned is not null && string.Equals(adapter.Id, pinned, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(adapter => adapter.OperationalStatus == OperationalStatus.Up);
    }

    // 169.254.x is what Windows leaves on an idle TAP adapter. It is a perfectly valid
    // non-loopback IPv4, so accepting it made the virtual IP - and the subnet the peer
    // discovery broadcasts on - point at an unconfigured adapter.
    private static bool IsUsableTapAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) &&
        !address.GetAddressBytes().AsSpan(0, 2).SequenceEqual<byte>([169, 254]);

    /// <summary>
    /// The adapter n3n should bind, as a GUID for the n3n <c>[tuntap] name</c> option.
    /// The driver we ship reports "TAP-Windows Adapter V9"; vendor-rebranded TAP clones
    /// (e.g. "Netease UU TAP-Win32 Adapter") are only used when nothing else exists,
    /// because n3n otherwise just takes whichever adapter happens to open first.
    /// A GUID is also pure ASCII, unlike the localized connection name.
    /// </summary>
    [GeneratedRegex(@"set address\s+""[^""]*""\s+static\s+(\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.IgnoreCase)]
    private static partial Regex StaticAddressRegex();

    private static Regex StaticAddressPattern => StaticAddressRegex();

    /// <summary>
    /// Drops <paramref name="address"/> from any interface other than the one we bind.
    /// Verified behaviour: assigning an IPv4 that another adapter already holds makes
    /// netsh fail with "The object already exists" and the address is not applied.
    /// Only the exact address n3n just asked for is touched, and the interface is
    /// addressed by index so a localized adapter name cannot be mangled on the way.
    /// </summary>
    private async Task ReleaseConflictingVirtualAddressAsync(EdgeRun run, string address)
    {
        if (!IPAddress.TryParse(address, out var target) ||
            target.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        var pinned = _tapAdapterId;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (pinned is not null && string.Equals(adapter.Id, pinned, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int index;
            try
            {
                if (!adapter.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(target)))
                {
                    continue;
                }
                index = adapter.GetIPProperties().GetIPv4Properties().Index;
            }
            catch (Exception)
            {
                continue;
            }

            try
            {
                var startInfo = new ProcessStartInfo("netsh")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("interface");
                startInfo.ArgumentList.Add("ip");
                startInfo.ArgumentList.Add("delete");
                startInfo.ArgumentList.Add("address");
                startInfo.ArgumentList.Add($"name={index}");
                startInfo.ArgumentList.Add($"addr={address}");
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                AppendLog(
                    run,
                    process.ExitCode == 0
                        ? $"网卡清理：已从“{adapter.Name}”释放上次遗留的 {address}，即将自动重试连接。"
                        : $"网卡清理：从“{adapter.Name}”释放 {address} 未成功（netsh 返回 {process.ExitCode}）。");
            }
            catch (Exception exception)
            {
                AppendLog(run, $"网卡清理：释放 {address} 时出错：{exception.Message}");
            }
        }
    }

    private static string? ResolveTapAdapterId()
    {
        // GetAllNetworkInterfaces also returns NDIS filter pseudo-interfaces bound to each
        // adapter ("TAP-Windows Adapter V9-Npcap Packet Driver-0000" and friends), whose
        // descriptions pass the name test but which n3n cannot open. Keep only real
        // network connections, using the very registry key n3n enumerates.
        var connections = ReadNetworkConnectionIds();
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsTapAdapter)
            .Where(adapter => connections.Count == 0 || connections.Contains(adapter.Id))
            .ToArray();
        var preferred = candidates.FirstOrDefault(adapter =>
            adapter.Description.StartsWith("TAP-Windows Adapter", StringComparison.OrdinalIgnoreCase));
        return (preferred ?? candidates.FirstOrDefault())?.Id;
    }

    private static HashSet<string> ReadNetworkConnectionIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}");
            if (root is null)
            {
                return result;
            }
            foreach (var name in root.GetSubKeyNames())
            {
                using var connection = root.OpenSubKey($@"{name}\Connection");
                if (connection?.GetValue("Name") is string label && !string.IsNullOrWhiteSpace(label))
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // Without the registry view we simply do not narrow the candidate list.
            result.Clear();
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private async Task StartRunAsync(
        int sessionId,
        ConnectionParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var managementPort = parameters.Engine == EdgeEngine.N3n
            ? FindAvailableTcpPort()
            : FindAvailableUdpPort();
        var managementPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        parameters.Diagnostics?.AddSecret(managementPassword);
        var sessionName = $"mikun2n-{Guid.NewGuid():N}";
        var configPath = parameters.Engine == EdgeEngine.N3n
            ? Path.Combine(_n3nConfigDirectory, $"{sessionName}.conf")
            : Path.Combine(Path.GetTempPath(), $"{sessionName}.conf");
        // Pin the adapter n3n should open. Without this it takes whichever TAP-family
        // device opens first, which on a machine with a gaming accelerator installed is
        // the accelerator's adapter, not the one we ship.
        _tapAdapterId = _tapAdapterPinFailed ? null : ResolveTapAdapterId();
        var config = parameters.Engine == EdgeEngine.N3n
            ? BuildN3nConfiguration(
                parameters.Server,
                parameters.Community,
                parameters.Nickname,
                _useRotatingMac ? CreateSessionMac() : null,
                _tapAdapterId,
                EdgeUdpPort,
                managementPort,
                managementPassword,
                parameters.ExperimentalIpv6P2p)
            : BuildLegacyConfiguration(
                // The legacy edge knows nothing about federations; give it the first entry.
                SplitServers(parameters.Server).FirstOrDefault() ?? parameters.Server,
                parameters.Community,
                parameters.Nickname,
                managementPort,
                managementPassword);
        var edgePath = parameters.Engine == EdgeEngine.N3n
            ? Path.Combine(_runtimeDirectory, "n3n-edge.exe")
            : Path.Combine(_runtimeDirectory, "edge.exe");
        var startInfo = new ProcessStartInfo(edgePath)
        {
            WorkingDirectory = _runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (parameters.Engine == EdgeEngine.N3n)
        {
            startInfo.ArgumentList.Add("start");
            startInfo.ArgumentList.Add(sessionName);
        }
        else
        {
            startInfo.ArgumentList.Add(configPath);
        }
        if (!string.IsNullOrEmpty(parameters.EncryptionKey))
        {
            startInfo.Environment["N2N_KEY"] = parameters.EncryptionKey;
            startInfo.Environment["N3N_KEY"] = parameters.EncryptionKey;
        }
        if (parameters.Diagnostics is not null)
            startInfo.Environment["MIKUN2N_IPV6_DIAGNOSTICS"] = "1";
        if (parameters.Engine == EdgeEngine.N3n && parameters.ExperimentalIpv6P2p &&
            TestBuildProfile.Current is { Ipv6StunHost.Length: > 0 } profile)
        {
            using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lookupCancellation.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(
                    profile.Ipv6StunHost, AddressFamily.InterNetworkV6, lookupCancellation.Token);
                var observers = addresses.Where(address => (address.GetAddressBytes()[0] & 0xe0) == 0x20)
                    .Distinct().Take(2).Select(address => address.ToString()).ToArray();
                startInfo.Environment["MIKUN2N_IPV6_STUN_SERVERS"] = string.Join(';', observers);
                parameters.Diagnostics?.Write("ipv6_mapping_observers", new
                {
                    host = profile.Ipv6StunHost, port = 3478, addresses = observers
                });
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                startInfo.Environment["MIKUN2N_IPV6_STUN_SERVERS"] = string.Empty;
                parameters.Diagnostics?.Write("ipv6_mapping_lookup_failed", new
                {
                    error = exception.GetType().Name
                });
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, config, new UTF8Encoding(false), cancellationToken);
        var logPath = CreateLogPath(sessionId);
        var logWriter = new StreamWriter(logPath, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        logWriter.WriteLine(
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
            $"MikuN2N {BuildIdentity.Version}：正在准备连接会话 s{sessionId}；" +
            "若此后没有 n3n 输出，表示会话在 n3n 启动前已取消。");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        var run = new EdgeRun(
            sessionId,
            process,
            new N2nManagementClient(
                managementPort,
                managementPassword,
                parameters.Engine == EdgeEngine.N3n
                    ? ManagementProtocol.N3nHttp
                    : ManagementProtocol.N2nUdp),
            new CancellationTokenSource(),
            configPath,
            logPath,
            logWriter,
            parameters.Engine == EdgeEngine.N3n,
            parameters.NodeId,
            parameters.Nickname)
        {
            SupernodeHosts = ResolveSupernodeHosts(parameters.Server),
            ActiveNodeName = parameters.NodeName,
            ExperimentalIpv6P2p = parameters.ExperimentalIpv6P2p,
            Diagnostics = parameters.Diagnostics
        };

        if (run.Diagnostics is { } diagnostics)
        {
            try
            {
                using var binary = File.OpenRead(edgePath);
                diagnostics.Write("edge_start", new
                {
                    run = sessionId, binary = Path.GetFileName(edgePath),
                    sha256 = Convert.ToHexString(SHA256.HashData(binary)),
                    adapterPinned = _tapAdapterId is not null, rotatingMac = _useRotatingMac
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Write("edge_hash_unavailable", new { error = exception.GetType().Name });
            }
            diagnostics.CaptureNetwork();
        }

        process.OutputDataReceived += (_, args) => AppendLog(run, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog(run, args.Data);
        process.Exited += (_, _) => _ = HandleProcessExitedAsync(run, cancellationToken);

        lock (_stateGate)
        {
            if (!_wantConnected || _sessionId != sessionId)
            {
                CleanupRun(run);
                process.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }
            _currentRun = run;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 n2n edge。");
            }
            run.StartedAt = DateTimeOffset.Now;
            LaunchedEdgeProcessIds[process.Id] = 0;
            AppendLog(run, $"MikuN2N {BuildIdentity.Version}：本次完整日志保存到 {logPath}");
            if (run.ExperimentalIpv6P2p)
            {
                AppendLog(run, run.IsN3n
                    ? "IPv6 实验：已请求启用，正在等待 edge 确认。"
                    : "IPv6 实验：当前使用旧版 edge，不支持此功能，本次继续使用 IPv4。");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.EnableRaisingEvents = true;
            if (run.IsN3n)
            {
                _ = ConfigurePortMappingAsync(run, cancellationToken);
            }
            _ = MonitorAsync(run, cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch
            {
                // Preserve the original startup exception.
            }
            lock (_stateGate)
            {
                if (ReferenceEquals(_currentRun, run))
                {
                    _currentRun = null;
                }
            }
            CleanupRun(run);
            process.Dispose();
            throw;
        }
    }

    private async Task MonitorAsync(EdgeRun run, CancellationToken connectionCancellation)
    {
        var failedPolls = 0;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            run.MonitorCancellation.Token,
            connectionCancellation);
        var cancellationToken = linkedCancellation.Token;

        while (!cancellationToken.IsCancellationRequested && !run.Process.HasExited && IsCurrentRun(run))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);

                if (run.IsN3n &&
                    run.ManagementUnavailable &&
                    DateTimeOffset.Now < run.NextManagementRetryAt)
                {
                    if (run.LogConfirmedConnected && KeepRegistrationRenewal(run, SupernodeState.Waiting))
                    {
                        PublishLogConfirmedConnection(run);
                    }
                    else if (run.LogConfirmedConnected)
                    {
                        lock (_stateGate) run.RegistrationBlocked = true;
                        PublishWaitingState(run, SupernodeState.Unknown, ++failedPolls);
                    }
                    continue;
                }

                var supernodes = await run.ManagementClient.GetSupernodesAsync(cancellationToken);
                run.Diagnostics?.Write("management_supernodes", new { run = run.SessionId, rows = supernodes });
                var supernodeState = GetSupernodeState(supernodes);

                if (supernodeState != SupernodeState.Registered)
                {
                    failedPolls++;
                    if (!KeepRegistrationRenewal(run, supernodeState))
                    {
                        lock (_stateGate) run.RegistrationBlocked = true;
                        PublishWaitingState(run, supernodeState, failedPolls);
                        continue;
                    }
                }
                else
                {
                    if (run.RegistrationFailureStartedMs != 0)
                        run.Diagnostics?.Write("registration_recovered", new { run = run.SessionId,
                            elapsedMs = Environment.TickCount64 - run.RegistrationFailureStartedMs });
                    lock (_stateGate)
                    {
                        run.RegistrationFailureStartedMs = 0;
                        run.RegistrationBlocked = false;
                    }
                }

                run.AddressConflict = false;
                // Registration went through, so the stale registration that forced a
                // rotating MAC is gone; the next connect returns to the stable one.
                _useRotatingMac = false;
                if (supernodeState == SupernodeState.Registered) failedPolls = 0;
                lock (_stateGate)
                {
                    if (ReferenceEquals(_currentRun, run))
                    {
                        _restartAttempt = 0;
                    }
                }

                var anchor = GetCurrentSupernode(supernodes);
                if (!string.Equals(anchor, run.CurrentSupernode, StringComparison.Ordinal))
                {
                    run.CurrentSupernode = anchor;
                    run.SupernodeRttMs = null;
                    if (anchor is not null)
                    {
                        AppendLog(run, $"中继节点：当前锚定 {anchor}。");
                    }
                }
                if (run.CurrentSupernode is not null && DateTimeOffset.Now >= run.NextSupernodePingAt)
                {
                    run.NextSupernodePingAt = DateTimeOffset.Now + TimeSpan.FromSeconds(4);
                    _ = MeasureSupernodeRttAsync(run);
                }

                var edges = await run.ManagementClient.GetEdgesAsync(cancellationToken);
                run.PeerRuntimeRows = edges;
                if (run.IsN3n && !run.NativeIdentityRead)
                {
                    run.NativeIdentityRead = true;
                    try
                    {
                        var info = await run.ManagementClient.GetInfoAsync(cancellationToken);
                        run.NativeVersion = PeerDiscoveryService.NormalizeVersion(
                            info.TryGetProperty("mikun2n_build_version", out var build) ? build.GetString() : null);
                        run.Ipv6WireVersion = ReadWireVersion(info, "ipv6_wire_version");
                        AppendLog(run, $"本机版本：客户端 {BuildIdentity.Version}，n3n 构建 {run.NativeVersion ?? "未报告"}，IPv6 协议 {run.Ipv6WireVersion?.ToString() ?? "未报告"}。");
                        run.Diagnostics?.Write("native_identity", info);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppendLog(run, $"当前 edge 未能报告版本：{exception.Message}");
                    }
                }
                run.Diagnostics?.Write("management_edges", new { run = run.SessionId, rows = edges });
                if (run.Diagnostics is not null && DateTimeOffset.UtcNow >= run.NextDiagnosticsAt)
                {
                    run.NextDiagnosticsAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    run.Diagnostics.CaptureNetwork();
                }
                run.PeerModes = GetPeerModes(edges, out var hasPunchTelemetry, out var rowMacs, out var pathKeys);
                run.PeerRowMacs = rowMacs;
                run.PeerPathKeys = pathKeys;
                run.NativePunchTelemetry = hasPunchTelemetry;
                if (run.IsN3n && DateTimeOffset.Now >= run.NextNatPollAt)
                {
                    run.NextNatPollAt = DateTimeOffset.Now + TimeSpan.FromSeconds(4);
                    try
                    {
                        var nat = await run.ManagementClient.GetNatAsync(cancellationToken);
                        run.Diagnostics?.Write("management_nat", new { run = run.SessionId, rows = nat });
                        UpdateNatStatus(run, nat);
                        if (run.ExperimentalIpv6P2p && !run.Ipv6StatusReported && nat.Count > 0)
                        {
                            run.Ipv6StatusReported = true;
                            var status = nat[0].TryGetProperty("ipv6_enabled", out var enabled) &&
                                         enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                                ? enabled.GetBoolean()
                                    ? "edge 已确认启用；是否建立直连请查看好友链路状态。"
                                    : "edge 报告开关未启用，本次继续使用 IPv4。"
                                : "当前 edge 未报告开关状态，无法确认 IPv6 实验已生效，请使用同版本运行时。";
                            AppendLog(run, $"IPv6 实验：{status}");
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!run.NatPollWarningLogged)
                        {
                            run.NatPollWarningLogged = true;
                            AppendLog(run, $"NAT 类型检测暂不可用，将继续后台重试：{exception.Message}");
                        }
                    }
                }
                run.ManagementUnavailable = false;
                var virtualIp = TapAddress() ?? "正在获取";
                if (virtualIp != "正在获取")
                {
                    run.VirtualIp = virtualIp;
                }
                run.LogConfirmedConnected = true;
                EnsurePeerDiscovery(run);
                run.PeerDiscovery?.UpdateLocalIdentity(run.NativeVersion, run.Ipv6WireVersion);
                PublishConnected(run, virtualIp);
                try
                {
                    await ReconcileRelayPoliciesAsync(run, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!run.Process.HasExited && IsCurrentRun(run))
            {
                run.Diagnostics?.Write("management_error", new { run = run.SessionId, error = exception.GetType().Name });
                if (run.IsN3n)
                {
                    var firstFailure = !run.ManagementUnavailable;
                    run.ManagementUnavailable = true;
                    // The Windows n3n management listener (IPv6 loopback) is known to
                    // intermittently refuse/reset roughly half of all requests but
                    // typically recovers within a couple of seconds; a long backoff here
                    // just makes the peer connection-mode display ("检测中") sit stale
                    // longer than the underlying data actually is.
                    run.NextManagementRetryAt = DateTimeOffset.Now + TimeSpan.FromSeconds(2);
                    if (firstFailure)
                    {
                        AppendLog(
                            run,
                            $"n3n Windows 管理接口暂时不可用，使用运行日志并继续重试：{exception.Message}");
                    }
                    if (run.LogConfirmedConnected && KeepRegistrationRenewal(run, SupernodeState.Waiting))
                    {
                        PublishLogConfirmedConnection(run);
                    }
                    else
                    {
                        lock (_stateGate) run.RegistrationBlocked = true;
                        PublishWaitingState(run, SupernodeState.Unknown, ++failedPolls);
                    }
                    continue;
                }
                failedPolls++;
                AppendLog(run, $"状态检查：{exception.Message}");
                PublishWaitingState(run, SupernodeState.Unknown, failedPolls);
            }
        }
    }

    private async Task ConfigurePortMappingAsync(EdgeRun run, CancellationToken cancellationToken)
    {
        UpnpPortMappingService? service = null;
        try
        {
            service = await UpnpPortMappingService.CreateAsync(EdgeUdpPort, cancellationToken);
            if (!IsCurrentRun(run))
            {
                service.Dispose();
                return;
            }
            run.PortMapping = service;
            run.NetworkHint = service.Status.Message;
            AppendLog(run, $"网络开放性：{service.Status.Message}");
            if (run.LogConfirmedConnected)
            {
                PublishConnected(run);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            service?.Dispose();
        }
    }

    private static bool KeepRegistrationRenewal(EdgeRun run, SupernodeState state)
    {
        if (run.RegistrationFailureStartedMs == 0)
        {
            run.RegistrationFailureStartedMs = Environment.TickCount64;
            run.Diagnostics?.Write("registration_wait_started", new { run = run.SessionId,
                established = run.LogConfirmedConnected, state = state.ToString(), graceMs = 10000 });
        }
        return run.LogConfirmedConnected && !run.AddressConflict &&
               state == SupernodeState.Waiting &&
               Environment.TickCount64 - run.RegistrationFailureStartedMs < 10000;
    }

    private void PublishWaitingState(EdgeRun run, SupernodeState state, int failedPolls)
    {
        if (run.AddressConflict)
        {
            PublishIfCurrent(run, new ConnectionSnapshot(
                ConnectionState.Reconnecting,
                "正在恢复连接…",
                "服务器仍保留着刚才的旧连接，程序会继续自动重试，通常 1～2 分钟内恢复。",
                TapAddress() ?? "正在获取"));
            return;
        }

        if (run.LogConfirmedConnected)
        {
            PublishIfCurrent(run, new ConnectionSnapshot(
                ConnectionState.Reconnecting,
                "正在恢复连接…",
                "n2n 正在重新联系服务器，无需反复点击连接。",
                TapAddress() ?? "正在获取"));
            return;
        }

        if (failedPolls < 8)
        {
            PublishIfCurrent(run, new ConnectionSnapshot(
                ConnectionState.Connecting,
                "正在连接…",
                "程序已启动，正在等待服务器确认。",
                TapAddress() ?? "正在获取"));
            return;
        }

        PublishIfCurrent(run, new ConnectionSnapshot(
            ConnectionState.Reconnecting,
            "连接不稳定，正在重试…",
            "暂时无法确认服务器状态，程序仍在后台自动恢复。请先不要反复点击连接。",
            TapAddress() ?? "正在获取"));
    }

    private async Task HandleProcessExitedAsync(EdgeRun run, CancellationToken connectionCancellation)
    {
        try
        {
            run.Process.WaitForExit();
        }
        catch
        {
            // The process is already gone; retained output is enough for diagnosis.
        }

        TryCancel(run.MonitorCancellation);
        var exitCode = TryGetExitCode(run.Process);
        var exitCodeText = exitCode is int code
            ? $"{code} / 0x{unchecked((uint)code):X8}"
            : "未知";
        var reason = FindExitReason(run);
        AppendLog(
            run,
            $"MikuN2N：n3n 进程已退出（代码 {exitCodeText}）。{reason}");
        CleanupRun(run);

        ConnectionParameters? parameters;
        int restartAttempt;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentRun, run))
            {
                return;
            }
            _currentRun = null;
            if (run.StopRequested || !_wantConnected || _sessionId != run.SessionId)
            {
                return;
            }
            parameters = _parameters;
            restartAttempt = ++_restartAttempt;
        }

        run.Process.Dispose();

        if (parameters is null)
        {
            return;
        }

        while (!connectionCancellation.IsCancellationRequested)
        {
            var delay = RestartDelays[Math.Min(restartAttempt - 1, RestartDelays.Length - 1)];
            Publish(new ConnectionSnapshot(
                ConnectionState.Reconnecting,
                "连接意外中断，正在自动恢复…",
                $"n2n 已退出（代码 {exitCode}）。{reason} {delay.TotalSeconds:0} 秒后自动重启。"));

            try
            {
                await Task.Delay(delay, connectionCancellation);
                if (!ShouldRestart(run.SessionId))
                {
                    return;
                }
                await StartRunAsync(run.SessionId, parameters, connectionCancellation);
                return;
            }
            catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                restartAttempt++;
                lock (_stateGate)
                {
                    if (_sessionId == run.SessionId)
                    {
                        _restartAttempt = restartAttempt;
                    }
                }
                reason = $"重启失败：{exception.Message}";
            }
        }
    }

    private void AppendLog(EdgeRun run, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        run.Diagnostics?.Write("edge_log", new { run = run.SessionId, line });

        var friendlyPunchLine = GetFriendlyPunchLog(line);
        string? compatibilityLine = null;
        lock (run.LogGate)
        {
            if (!run.EncryptionMismatchLogged &&
                line.Contains("invalid transop ID", StringComparison.OrdinalIgnoreCase))
            {
                run.EncryptionMismatchLogged = true;
                run.CompatibilityHint =
                    "检测到至少一位好友的联机密钥或加密模式与本机不一致，该链路无法传输游戏数据。";
                compatibilityLine =
                    "配置错误：检测到对端的联机密钥/加密模式与本机不一致；请双方统一密钥（或都留空）后重新连接。";
            }
            run.LogLines.Add(line);
            if (friendlyPunchLine is not null)
            {
                run.LogLines.Add(friendlyPunchLine);
            }
            if (compatibilityLine is not null)
            {
                run.LogLines.Add(compatibilityLine);
            }
            if (run.LogLines.Count > 500)
            {
                run.LogLines.RemoveRange(0, run.LogLines.Count - 500);
            }
            try
            {
                run.LogWriter.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {line}");
                if (friendlyPunchLine is not null)
                {
                    run.LogWriter.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {friendlyPunchLine}");
                }
                if (compatibilityLine is not null)
                {
                    run.LogWriter.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {compatibilityLine}");
                }
            }
            catch (ObjectDisposedException)
            {
                // A final async output callback can arrive just after process cleanup.
            }
        }

        // n3n leaves its static address on the adapter when it exits, so after switching
        // adapters the old one still owns the address the supernode hands out again and
        // netsh refuses with "The object already exists". Release the stale copy; the
        // automatic restart then succeeds.
        if (!run.VirtualAddressReleased &&
            line.Contains("Unable to set IP address", StringComparison.OrdinalIgnoreCase))
        {
            var match = StaticAddressPattern.Match(line);
            if (match.Success)
            {
                run.VirtualAddressReleased = true;
                _ = ReleaseConflictingVirtualAddressAsync(run, match.Groups[1].Value);
            }
        }

        // The pinned adapter turned out to be unusable (removed, disabled, or held by
        // another process). Fall back to n3n's own "first adapter that opens" choice on
        // the next attempt rather than failing every restart from here on.
        if (!_tapAdapterPinFailed &&
            _tapAdapterId is not null &&
            line.Contains("Cannot find tap device", StringComparison.OrdinalIgnoreCase))
        {
            _tapAdapterPinFailed = true;
            AppendLog(run, "网卡选择：指定的 TAP 网卡不可用，下次连接将改回自动选择。");
        }

        if (line.Contains("authentication error", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("already in use", StringComparison.OrdinalIgnoreCase))
        {
            run.LogConfirmedConnected = false;
            run.AddressConflict = true;
            if (!_useRotatingMac)
            {
                _useRotatingMac = true;
                AppendLog(
                    run,
                    "地址冲突：服务器尚未释放上次的连接，本次改用临时随机 MAC 立即重连。");
                _ = RestartForMacRotationAsync(run);
            }
            PublishIfCurrent(run, new ConnectionSnapshot(
                ConnectionState.Reconnecting,
                "正在恢复连接…",
                "服务器仍保留着刚才的旧连接，程序会继续自动重试，通常 1～2 分钟内恢复。",
                TapAddress() ?? "正在获取"));
        }

        if (run.IsN3n &&
            line.Contains("[OK] edge", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("supernode", StringComparison.OrdinalIgnoreCase))
        {
            run.AddressConflict = false;
            run.LogConfirmedConnected = true;
            PublishLogConfirmedConnection(run);
        }

        if (line.Contains("supernode not responding", StringComparison.OrdinalIgnoreCase))
        {
            run.LogConfirmedConnected = false;
            PublishIfCurrent(run, new ConnectionSnapshot(
                ConnectionState.Reconnecting,
                "连接不稳定，正在恢复…",
                "暂时收不到服务器回应，程序仍在自动重试，无需反复点击连接。",
                TapAddress() ?? "正在获取",
                -1,
                -1));
        }

        LogReceived?.Invoke(this, line);
        if (friendlyPunchLine is not null)
        {
            LogReceived?.Invoke(this, friendlyPunchLine);
        }
        if (compatibilityLine is not null)
        {
            LogReceived?.Invoke(this, compatibilityLine);
        }
    }

    private static string? GetFriendlyPunchLog(string line)
    {
        if (line.Contains("MikuN2N NAT4 bank calibration started", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：检测到双 NAT4，正在用独立 UDP 端口组采样两端映射规律。";
        }
        if (line.Contains("MikuN2N NAT4 bank punch started", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：双 NAT4 端口组已同步，正在并行命中对端映射窗口。";
        }
        if (line.Contains("MikuN2N bank punch promoted", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：双 NAT4 端口组已命中，连接已切换为 P2P 直连。";
        }
        if (line.Contains("MikuN2N NAT4 bank punch exhausted", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：本轮双 NAT4 端口组未命中，将在冷却后重新采样；当前继续使用 pSp 中继。";
        }
        if (line.Contains("MikuN2N NAT4 bank punch abandoned", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：自动尝试已全部用完，现已稳定使用 pSp 中继；可在好友链路上右键手动重试。";
        }
        if (line.Contains("MikuN2N Tier 1 punch started", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：增强策略已启动，正在受限的时间与发包预算内扫描可能的公网端口。";
        }
        if (line.Contains("MikuN2N Tier 1 punch succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：增强策略成功，连接已切换为 P2P 直连。";
        }
        if (line.Contains("MikuN2N Tier 1 punch exhausted", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：本轮增强策略未建立直连，已停止扫描并继续使用 pSp 中继。";
        }
        if (line.Contains("MikuN2N Tier 1 punch abandoned", StringComparison.OrdinalIgnoreCase))
        {
            return "P2P 打洞：自动尝试已全部用完，现已稳定使用 pSp 中继；可在好友链路上右键手动重试。";
        }
        return null;
    }

    private void PublishLogConfirmedConnection(EdgeRun run)
    {
        EnsurePeerDiscovery(run);
        PublishConnected(run);
    }

    private void EnsurePeerDiscovery(EdgeRun run)
    {
        if (!run.IsN3n || run.PeerDiscovery is not null || run.DiscoveryUnavailable ||
            !TryGetTapNetwork(out var address, out var subnetMask))
        {
            return;
        }

        lock (run.DiscoveryGate)
        {
            if (run.PeerDiscovery is not null)
            {
                return;
            }

            try
            {
                var discovery = new PeerDiscoveryService(
                    run.NodeId,
                    run.Nickname,
                    address,
                    subnetMask,
                    message => AppendLog(run, message),
                    traceLatency: run.Diagnostics is not null);
                discovery.PeersChanged += (_, peers) =>
                {
                    run.Peers = peers;
                    PublishConnected(run);
                };
                discovery.RelayPolicyRequested += (_, request) =>
                    _ = ApplyRemoteRelayPolicyAsync(run, request);
                run.PeerDiscovery = discovery;
                run.VirtualIp = address.ToString();
                AppendLog(run, $"MikuN2N：好友发现已启动（{address}，UDP 43121）。");
                // Shells out to netsh/powershell, so it must not hold up the poll loop.
                run.TunedAdapterId = _tapAdapterId ?? ResolveTapAdapterId();
                _ = Task.Run(() => NetworkTuningService.ApplyAsync(
                    run.TunedAdapterId,
                    address,
                    subnetMask,
                    message => AppendLog(run, message)));
            }
            catch (SocketException exception)
            {
                run.DiscoveryUnavailable = true;
                AppendLog(run, $"MikuN2N：好友列表启动失败：{exception.Message}");
            }
        }
    }

    private void PublishConnected(EdgeRun run, string? virtualIp = null)
    {
        // Discovery and UPnP callbacks must not hide a sustained registration failure.
        if (run.RegistrationBlocked || run.AddressConflict || run.Process.HasExited) return;
        var now = DateTimeOffset.Now;
        var typed = run.Peers
            .Select(peer => (peer, mode: GetPeerConnectionMode(run, peer)))
            .ToArray();

        // Patched n3n reports the exact Tier 1 state. Keep the old 25-second
        // display heuristic only as compatibility fallback for an older edge
        // binary that does not expose punch_state.
        var peers = new PeerSnapshot[typed.Length];
        lock (run.PeerModeGate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < typed.Length; index++)
            {
                var (peer, mode) = typed[index];
                present.Add(peer.VirtualIp);
                if (mode == PeerConnectionMode.Relayed && !run.NativePunchTelemetry)
                {
                    if (!run.RelayedSince.TryGetValue(peer.VirtualIp, out var since))
                    {
                        since = now;
                        run.RelayedSince[peer.VirtualIp] = since;
                    }
                    if (now - since < PunchDisplayWindow)
                    {
                        mode = PeerConnectionMode.Punching;
                    }
                }
                else
                {
                    run.RelayedSince.Remove(peer.VirtualIp);
                }
                var path = GetPeerPath(run, peer);
                var measuredPeer = run.PeerDiscovery?.ApplyPeerPath(peer, path) ?? peer;
                peers[index] = AddPeerIdentity(run, measuredPeer) with { ConnectionMode = mode };
            }
            foreach (var key in run.RelayedSince.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                run.RelayedSince.Remove(key);
            }
        }
        LogPeerConnectionTransitions(run, peers);

        var directCount = peers.Count(peer =>
            peer.ConnectionMode is PeerConnectionMode.Direct or PeerConnectionMode.Ipv6Direct or PeerConnectionMode.LanDirect);
        var punchingCount = peers.Count(peer =>
            peer.ConnectionMode == PeerConnectionMode.Punching);
        var relayedCount = peers.Count(peer =>
            peer.ConnectionMode is PeerConnectionMode.Relayed or
                PeerConnectionMode.ForcedRelayed or
                PeerConnectionMode.PunchFailed);
        var failedCount = peers.Count(peer =>
            peer.ConnectionMode == PeerConnectionMode.PunchFailed);
        var detail = peers.Length == 0
            ? "隧道已连接；暂时没有发现其他已启动 MikuN2N 的朋友。"
            : punchingCount > 0
                ? $"隧道已连接；{directCount} 位 P2P 直连，{punchingCount} 位打洞中，{relayedCount} 位经 Supernode 中继。"
                : failedCount > 0
                    ? $"隧道已连接；{directCount} 位 P2P 直连，{relayedCount} 位经 Supernode 中继；其中 {failedCount} 位已停止自动打洞，可右键重试。"
                : $"隧道已连接；{directCount} 位 P2P 直连，{relayedCount} 位经 Supernode 中继。";
        if (!string.IsNullOrWhiteSpace(run.NetworkHint))
        {
            detail = $"{detail} {run.NetworkHint}";
        }
        if (!string.IsNullOrWhiteSpace(run.CompatibilityHint))
        {
            detail = $"{detail} {run.CompatibilityHint}";
        }
        var mismatched = peers.Count(peer => peer.Ipv6VersionMismatch);
        if (mismatched > 0)
        {
            detail += $" {mismatched} 位好友的 IPv6 协议与本机不同，暂用 IPv4；请更新好友客户端。";
        }
        PublishIfCurrent(run, new ConnectionSnapshot(
            ConnectionState.Connected,
            "已连接，可以开始游戏",
            detail,
            virtualIp ?? run.VirtualIp ?? TapAddress() ?? "正在获取",
            peers.Length,
            directCount,
            DateTimeOffset.Now - run.StartedAt,
            peers,
            run.NatType,
            run.NatDescription,
            FormatSupernodeText(run, run.ActiveNodeName)));
    }

    private static HashSet<string> ResolveSupernodeHosts(string server) =>
        SplitServers(server)
            .Select(entry => entry.LastIndexOf(':') is var separator && separator > 0
                ? entry[..separator]
                : entry)
            .ToHashSet(StringComparer.Ordinal);

    private static string FormatSupernodeText(EdgeRun run, string? activeNodeName)
    {
        var sockaddr = run.CurrentSupernode;
        if (string.IsNullOrEmpty(sockaddr))
        {
            return "—";
        }
        var separator = sockaddr.LastIndexOf(':');
        var host = separator > 0 ? sockaddr[..separator] : sockaddr;
        var isOwn = run.SupernodeHosts.Contains(host);
        var label = isOwn && !string.IsNullOrWhiteSpace(activeNodeName) ? activeNodeName : sockaddr;
        return run.SupernodeRttMs is { } rtt ? $"{label} · {rtt} ms" : label;
    }

    private void LogPeerConnectionTransitions(EdgeRun run, IReadOnlyList<PeerSnapshot> peers)
    {
        var lines = new List<string>();
        lock (run.PeerModeGate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var peer in peers)
            {
                var key = $"{peer.NodeId}|{peer.VirtualIp}";
                present.Add(key);
                if (peer.ConnectionMode == PeerConnectionMode.Unknown)
                {
                    continue;
                }

                run.LoggedPeerModes.TryGetValue(key, out var previous);
                if (run.LoggedPeerModes.ContainsKey(key) && previous == peer.ConnectionMode)
                {
                    continue;
                }
                run.LoggedPeerModes[key] = peer.ConnectionMode;

                var peerLabel = $"{peer.Nickname}（{peer.VirtualIp}）";
                switch (peer.ConnectionMode)
                {
                    case PeerConnectionMode.Punching:
                        lines.Add(
                            $"P2P：正在与 {peerLabel} 尝试直连；" +
                            "先执行 n3n 原生打洞，必要时自动进入增强打洞。");
                        break;
                    case PeerConnectionMode.Direct:
                        lines.Add($"P2P：已与 {peerLabel} 建立 IPv4 P2P 直连。");
                        break;
                    case PeerConnectionMode.Ipv6Direct:
                        lines.Add($"P2P：已与 {peerLabel} 建立 IPv6 P2P 直连。");
                        break;
                    case PeerConnectionMode.LanDirect:
                        lines.Add($"P2P：已与 {peerLabel} 建立本地直连。");
                        break;
                    case PeerConnectionMode.Relayed when previous == PeerConnectionMode.Punching:
                        lines.Add(
                            $"P2P：与 {peerLabel} 的本轮打洞未成功，继续使用 Supernode 中继。");
                        break;
                    case PeerConnectionMode.Relayed:
                        lines.Add($"P2P：{peerLabel} 当前使用 Supernode 中继。");
                        break;
                    case PeerConnectionMode.ForcedRelayed:
                        lines.Add($"P2P：{peerLabel} 已按用户设置强制使用 Supernode 中继。");
                        break;
                    case PeerConnectionMode.PunchFailed:
                        lines.Add(
                            $"P2P：与 {peerLabel} 的自动打洞已达到上限，现稳定使用 Supernode 中继；可右键手动重试。");
                        break;
                }
            }

            foreach (var key in run.LoggedPeerModes.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                run.LoggedPeerModes.Remove(key);
            }
        }

        foreach (var line in lines)
        {
            AppendLog(run, line);
        }
    }

    private static void UpdateNatStatus(EdgeRun run, IReadOnlyList<JsonElement> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var row = rows[0];
        var type = row.TryGetProperty("type", out var typeValue)
            ? typeValue.GetString()
            : null;
        var mapping = row.TryGetProperty("mapping", out var mappingValue)
            ? mappingValue.GetString()
            : null;
        var filtering = row.TryGetProperty("filtering", out var filteringValue)
            ? filteringValue.GetString()
            : null;
        var strategy = row.TryGetProperty("strategy", out var strategyValue)
            ? strategyValue.GetString()
            : null;
        var publicIp = row.TryGetProperty("public_ip", out var ipValue)
            ? ipValue.GetString()
            : null;
        var publicPort = row.TryGetProperty("public_port", out var portValue) &&
                         portValue.TryGetInt32(out var parsedPort)
            ? parsedPort
            : 0;
        var available = !row.TryGetProperty("available", out var availableValue) ||
                        availableValue.ValueKind != JsonValueKind.False;
        var complete = row.TryGetProperty("complete", out var completeValue) &&
                       completeValue.ValueKind == JsonValueKind.True;
        var probeRound = row.TryGetProperty("probe_round", out var roundValue) &&
                         roundValue.TryGetInt32(out var parsedRound)
            ? parsedRound
            : 0;

        if (!complete)
        {
            run.NatType = available ? "检测中" : "检测不可用";
            run.NatDescription = available
                ? $"正在通过 n3n 数据端口检测 NAT 行为，已发送 {probeRound} 轮探测。"
                : $"n3n 数据端口连续 {probeRound} 轮未收到探测回包，后台将在 60 秒后重试。";
            run.NatPollWarningLogged = false;
            return;
        }

        run.NatType = type switch
        {
            "NAT1/2" => "NAT1/2",
            "NAT3" => "NAT3",
            "NAT4" => "NAT4",
            _ => "未知"
        };

        var mappingText = mapping switch
        {
            "endpoint-independent" => "端点无关映射",
            "address-and-port-dependent" => "地址和端口相关映射",
            _ => "映射待确认"
        };
        var filteringText = filtering switch
        {
            "endpoint-or-address-independent" => "开放或地址相关过滤",
            "address-dependent" => "地址相关过滤",
            "address-and-port-dependent" => "地址和端口相关过滤",
            _ => "过滤待确认"
        };
        var strategyText = strategy switch
        {
            "tier1-cone-escape" => "NAT4 跨地址端口窗口",
            "tier1-low-fanout" => "NAT4 低扇出兜底",
            "tier1-layered-scan" => "分层窗口兜底",
            _ => "n3n 原生优先"
        };
        var endpoint = !string.IsNullOrWhiteSpace(publicIp) && publicPort > 0
            ? $"，公网端点 {publicIp}:{publicPort}"
            : string.Empty;
        run.NatDescription = $"{mappingText}，{filteringText}；{strategyText}{endpoint}。";
        run.NatPollWarningLogged = false;
    }

    private static string FindExitReason(EdgeRun run)
    {
        lock (run.LogGate)
        {
            var reason = run.LogLines.LastOrDefault(line =>
                line.Contains("recvfrom() failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WSAGetLastError", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("authentication error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase));
            return reason is null
                ? $"n3n 未产生诊断输出，可能是本地运行时崩溃、文件损坏或权限不足；完整日志：{run.LogPath}"
                : $"原因线索：{reason}；完整日志：{run.LogPath}";
        }
    }

    private string CreateLogPath(int sessionId)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(_logDirectory, $"n2n-{stamp}-s{sessionId}.log");
    }

    private string ResolveEdgePath()
    {
        var n3nPath = Path.Combine(_runtimeDirectory, "n3n-edge.exe");
        return File.Exists(n3nPath)
            ? n3nPath
            : Path.Combine(_runtimeDirectory, "edge.exe");
    }

    private bool IsCurrentRun(EdgeRun run)
    {
        lock (_stateGate)
        {
            return _wantConnected && ReferenceEquals(_currentRun, run) && _sessionId == run.SessionId;
        }
    }

    private bool ShouldRestart(int sessionId)
    {
        lock (_stateGate)
        {
            return _wantConnected && _sessionId == sessionId && _currentRun is null;
        }
    }

    private void PublishIfCurrent(EdgeRun run, ConnectionSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (!_wantConnected || !ReferenceEquals(_currentRun, run)) return;
            if (snapshot.State == ConnectionState.Connected &&
                (run.RegistrationBlocked || run.AddressConflict || run.Process.HasExited)) return;
            Publish(snapshot);
        }
    }

    private static string? GetCurrentSupernode(IReadOnlyList<JsonElement> rows)
    {
        foreach (var row in rows)
        {
            if (row.TryGetProperty("current", out var current) &&
                current.ValueKind == JsonValueKind.Number &&
                current.TryGetInt32(out var value) &&
                value == 1 &&
                row.TryGetProperty("sockaddr", out var sockaddr) &&
                sockaddr.ValueKind == JsonValueKind.String)
            {
                return sockaddr.GetString();
            }
        }
        return null;
    }

    private static async Task MeasureSupernodeRttAsync(EdgeRun run)
    {
        var sockaddr = run.CurrentSupernode;
        if (sockaddr is null)
        {
            return;
        }
        var separator = sockaddr.LastIndexOf(':');
        var host = separator > 0 ? sockaddr[..separator] : sockaddr;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1000);
            if (string.Equals(run.CurrentSupernode, sockaddr, StringComparison.Ordinal))
            {
                run.SupernodeRttMs = reply.Status == IPStatus.Success
                    ? reply.RoundtripTime
                    : null;
            }
        }
        catch
        {
            // ICMP being filtered somewhere on the path only costs the display value.
        }
    }

    private static SupernodeState GetSupernodeState(IReadOnlyList<JsonElement> rows)
    {
        var foundWaiting = false;
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("current", out var value))
            {
                continue;
            }

            var current = value.ValueKind switch
            {
                JsonValueKind.True => 1,
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
                JsonValueKind.String when string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) => 1,
                _ => 0
            };

            if (current == 1)
            {
                return SupernodeState.Registered;
            }
            if (current == 2)
            {
                foundWaiting = true;
            }
        }
        return foundWaiting ? SupernodeState.Waiting : SupernodeState.Unknown;
    }

    private static IReadOnlyDictionary<string, PeerConnectionMode> GetPeerModes(
        IReadOnlyList<JsonElement> rows,
        out bool hasPunchTelemetry,
        out IReadOnlyDictionary<string, string> rowMacs,
        out IReadOnlyDictionary<string, string> pathKeys)
    {
        var result = new Dictionary<string, PeerConnectionMode>(StringComparer.Ordinal);
        var macs = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        rowMacs = macs;
        pathKeys = paths;
        hasPunchTelemetry = false;
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("mode", out var modeValue))
            {
                continue;
            }
            var punchState = row.TryGetProperty("punch_state", out var punchValue)
                ? punchValue.GetString()
                : null;
            hasPunchTelemetry |= punchState is not null;
            var ipv6 = row.TryGetProperty("transport", out var transport) &&
                       transport.GetString() == "ipv6";
            var mode = modeValue.GetString() switch
            {
                var value when string.Equals(value, "pSp", StringComparison.OrdinalIgnoreCase) &&
                               punchState == "forced_relay" =>
                    PeerConnectionMode.ForcedRelayed,
                var value when string.Equals(value, "p2p", StringComparison.OrdinalIgnoreCase) =>
                    ipv6 ? PeerConnectionMode.Ipv6Direct : PeerConnectionMode.Direct,
                var value when string.Equals(value, "pSp", StringComparison.OrdinalIgnoreCase) &&
                               punchState == "failed" =>
                    PeerConnectionMode.PunchFailed,
                var value when string.Equals(value, "pSp", StringComparison.OrdinalIgnoreCase) &&
                               punchState is "native" or "punching" =>
                    PeerConnectionMode.Punching,
                var value when string.Equals(value, "pSp", StringComparison.OrdinalIgnoreCase) =>
                    PeerConnectionMode.Relayed,
                _ => PeerConnectionMode.Unknown
            };
            var rowMac = row.TryGetProperty("macaddr", out var macValue)
                ? NormalizeMacAddress(macValue.GetString())
                : null;
            var path = mode == PeerConnectionMode.Ipv6Direct
                ? "ipv6:" + (row.TryGetProperty("ipv6_path", out var v6Path) ? v6Path.GetString() : "")
                : mode == PeerConnectionMode.Direct
                    ? "ipv4:" + (row.TryGetProperty("sockaddr", out var v4Path) ? v4Path.GetString() : "")
                    : "relay";
            if (row.TryGetProperty("ip4addr", out var addressValue))
            {
                var address = addressValue.GetString()?.Split('/', 2)[0];
                AddPeerMode(result, paths, $"ip:{address}", mode, path);
                AddRowMac(macs, $"ip:{address}", rowMac);
            }
            if (row.TryGetProperty("desc", out var descriptionValue))
            {
                AddPeerMode(result, paths, $"name:{descriptionValue.GetString()}", mode, path);
                AddRowMac(macs, $"name:{descriptionValue.GetString()}", rowMac);
            }
            if (rowMac is not null)
            {
                AddPeerMode(result, paths, $"mac:{rowMac}", mode, path);
            }
        }
        return result;
    }

    private static string? GetPeerPath(EdgeRun run, PeerSnapshot peer)
    {
        var paths = run.PeerPathKeys;
        if (!paths.TryGetValue($"ip:{peer.VirtualIp}", out var path) &&
            !paths.TryGetValue($"name:{peer.Nickname}", out path))
        {
            run.PeerMacAddresses.TryGetValue(peer.VirtualIp, out var mac);
            if (mac is null || !paths.TryGetValue($"mac:{mac}", out path))
            {
                return null;
            }
        }
        return path == "relay" ? $"relay:{run.CurrentSupernode}" : path;
    }

    private static int? ReadWireVersion(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var version) && version is > 0 and <= 255 ? version : null;

    private static PeerSnapshot AddPeerIdentity(EdgeRun run, PeerSnapshot peer)
    {
        run.PeerMacAddresses.TryGetValue(peer.VirtualIp, out var mac);
        JsonElement? best = null;
        var priority = 0;
        foreach (var row in run.PeerRuntimeRows)
        {
            var match = mac is not null && row.TryGetProperty("macaddr", out var rowMac) &&
                        NormalizeMacAddress(rowMac.GetString()) == mac ? 3 :
                row.TryGetProperty("ip4addr", out var ip) && ip.GetString()?.Split('/', 2)[0] == peer.VirtualIp ? 2 :
                row.TryGetProperty("desc", out var name) && name.GetString() == peer.Nickname ? 1 : 0;
            if (match > priority)
            {
                priority = match;
                best = row;
            }
        }
        var nativeVersion = peer.NativeVersion;
        var wireVersion = peer.Ipv6WireVersion;
        if (best is { } identity)
        {
            nativeVersion = identity.TryGetProperty("version", out var version)
                ? PeerDiscoveryService.NormalizeVersion(version.GetString()) ?? nativeVersion : nativeVersion;
            wireVersion = ReadWireVersion(identity, "peer_ipv6_wire_version") ?? wireVersion;
        }
        return peer with { NativeVersion = nativeVersion, Ipv6WireVersion = wireVersion,
            LocalIpv6WireVersion = run.ExperimentalIpv6P2p ? run.Ipv6WireVersion : null };
    }

    private static PeerConnectionMode GetPeerConnectionMode(EdgeRun run, PeerSnapshot peer)
    {
        if (run.PeerModes.TryGetValue($"ip:{peer.VirtualIp}", out var mode) ||
            run.PeerModes.TryGetValue($"name:{peer.Nickname}", out mode))
        {
            return mode;
        }

        string? macAddress;
        lock (run.PeerModeGate)
        {
            run.PeerMacAddresses.TryGetValue(peer.VirtualIp, out macAddress);
        }
        if (macAddress is null)
        {
            macAddress = TryResolveMacAddress(peer.VirtualIp);
            if (macAddress is not null)
            {
                lock (run.PeerModeGate)
                {
                    run.PeerMacAddresses[peer.VirtualIp] = macAddress;
                }
            }
        }
        if (macAddress is not null && run.PeerModes.TryGetValue($"mac:{macAddress}", out mode))
        {
            return mode;
        }

        // A peer answering ping in <=2 ms cannot be relayed through the public
        // supernode; the traffic is on the local network even when the n3n
        // management rows fail to correlate (common for multicast-discovered
        // LAN peers whose row keys never match the discovery data).
        return peer.LatencyMs is >= 0 and <= 2
            ? PeerConnectionMode.LanDirect
            : PeerConnectionMode.Unknown;
    }

    private static string? TryResolveMacAddress(string virtualIp)
    {
        if (!IPAddress.TryParse(virtualIp, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        var bytes = address.GetAddressBytes();
        var destination = BitConverter.ToUInt32(bytes);
        var mac = new byte[6];
        var length = mac.Length;
        return SendARP(destination, 0, mac, ref length) == 0 && length > 0
            ? string.Join(':', mac.Take(length).Select(value => value.ToString("X2")))
            : null;
    }

    private static string? NormalizeMacAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)))
            : null;
    }

    private static void AddPeerMode(
        IDictionary<string, PeerConnectionMode> modes,
        IDictionary<string, string> paths,
        string key,
        PeerConnectionMode mode,
        string path)
    {
        if (key.EndsWith(':') || mode == PeerConnectionMode.Unknown)
        {
            return;
        }
        if (!modes.TryGetValue(key, out var existing) ||
            PeerModePriority(mode) > PeerModePriority(existing))
        {
            modes[key] = mode;
            paths[key] = path;
        }
    }

    private static int PeerModePriority(PeerConnectionMode mode) => mode switch
    {
        PeerConnectionMode.LanDirect => 7,
        PeerConnectionMode.Ipv6Direct => 6,
        PeerConnectionMode.Direct => 5,
        PeerConnectionMode.ForcedRelayed => 4,
        PeerConnectionMode.PunchFailed => 3,
        PeerConnectionMode.Punching => 2,
        PeerConnectionMode.Relayed => 1,
        _ => 0
    };

    // Two management rows can share a description (peers keep the default machine
    // name), and addressing the wrong one would force the wrong friend onto the
    // relay - so an ambiguous key is dropped rather than resolved arbitrarily.
    private static void AddRowMac(IDictionary<string, string> macs, string key, string? mac)
    {
        if (key.EndsWith(':') || mac is null)
        {
            return;
        }
        macs[key] = macs.TryGetValue(key, out var existing) && existing != mac
            ? string.Empty
            : mac;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(
        uint destinationIp,
        uint sourceIp,
        byte[] macAddress,
        ref int physicalAddressLength);

    private static string BuildLegacyConfiguration(
        string server,
        string community,
        string nickname,
        int managementPort,
        string managementPassword) =>
        string.Join('\n',
        [
            "# Generated by MikuN2N. The encryption key is passed through N2N_KEY.",
            $"-c={community}",
            $"-l={server}",
            $"-I={nickname}",
            "-E",
            "-x=1",
            $"-t={managementPort}",
            $"--management-password={managementPassword}",
            string.Empty
        ]);

    /// <param name="edgeMac">
    /// Null in the normal case, which leaves the TAP adapter completely untouched.
    /// n3n reads the adapter's real MAC back through TAP_IOCTL_GET_MAC and uses that
    /// for the n2n protocol, and the adapter's own MAC is already stable across runs,
    /// so configuring one buys nothing while forcing n3n to rewrite the adapter
    /// registry entry and disable/enable the adapter on every connect. A value is only
    /// passed to rotate away from a supernode registration that is still held.
    /// </param>
    private static string BuildN3nConfiguration(
        string server,
        string community,
        string nickname,
        string? edgeMac,
        string? adapterId,
        int edgePort,
        int managementPort,
        string managementPassword,
        bool experimentalIpv6P2p) =>
        string.Join('\n',
        ((string?[])
        [
            "# Generated by MikuN2N. The encryption key is passed through N3N_KEY.",
            "[community]",
            $"name={community}",
            .. SplitServers(server).Select(entry => $"supernode={entry}"),
            string.Empty,
            "[connection]",
            $"description={nickname}",
            // With several federated supernodes, anchor to the lowest-RTT one so
            // relayed traffic enters the backbone at the nearest node.
            SplitServers(server).Count > 1 ? "supernode_selection=rtt" : null,
            $"bind={edgePort}",
            "register_pkt_ttl=3",
            "mikun2n_punch=true",
            experimentalIpv6P2p ? "mikun2n_ipv6=true" : null,
            "mikun2n_punch_grace=5",
            "mikun2n_punch_budget=25",
            "mikun2n_punch_max_packets=12000",
            string.Empty,
            "[filter]",
            "allow_multicast=true",
            string.Empty,
            "[tuntap]",
            "address_mode=auto",
            adapterId is null ? null : $"name={adapterId}",
            edgeMac is null ? null : $"macaddr={edgeMac}",
            "metric=1",
            string.Empty,
            "[management]",
            $"port={managementPort}",
            $"password={managementPassword}",
            string.Empty,
            "[logging]",
            "verbose=2",
            string.Empty
        ]).Where(line => line is not null));

    /// <summary>
    /// The server box accepts several supernode endpoints in one string so a
    /// federation can be listed; separators follow whatever users may type.
    /// </summary>
    public static IReadOnlyList<string> SplitServers(string value) =>
        value.Split(
                [',', ';', '、', '，', '；', ' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string CreateSessionMac()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] & 0xFC) | 0x02);
        return FormatMac(bytes);
    }

    private static string FormatMac(byte[] bytes) =>
        string.Join(':', bytes.Select(value => value.ToString("X2")));

    /// <summary>
    /// Restarts the edge so the next run picks a rotating MAC. The supernode still holds
    /// our previous registration ("MAC or IP address already in use or not released yet"),
    /// and n3n keeps retrying with the same MAC, so waiting it out would stall for up to
    /// the full registration timeout.
    /// </summary>
    private async Task RestartForMacRotationAsync(EdgeRun run)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (!IsCurrentRun(run) || run.Process.HasExited)
            {
                return;
            }
            run.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The run was torn down concurrently; the normal restart path takes over.
        }
    }

    private bool TryGetTapNetwork(out IPAddress address, out IPAddress subnetMask)
    {
        foreach (var adapter in OrderedTapAdapters())
        {
            try
            {
                var item = adapter.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(candidate => IsUsableTapAddress(candidate.Address));
                if (item?.IPv4Mask is not null)
                {
                    address = item.Address;
                    subnetMask = item.IPv4Mask;
                    return true;
                }
            }
            catch (NetworkInformationException)
            {
            }
        }
        address = IPAddress.None;
        subnetMask = IPAddress.None;
        return false;
    }

    private static int FindAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTapAdapter(NetworkInterface adapter)
    {
        var description = $"{adapter.Name} {adapter.Description}";
        return description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("n2n", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kills any edge child this process started that is somehow still alive. Only
    /// PIDs we launched ourselves are touched, so a second n2n-family client running
    /// on the machine is never affected.
    /// </summary>
    public static void KillLaunchedEdgeProcesses()
    {
        foreach (var processId in LaunchedEdgeProcessIds.Keys)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone, or no longer ours - either way there is nothing to clean.
            }
            LaunchedEdgeProcessIds.TryRemove(processId, out _);
        }
    }

    private static void CleanupRun(EdgeRun run)
    {
        if (Interlocked.Exchange(ref run.CleanedUp, 1) != 0)
        {
            return;
        }

        try
        {
            LaunchedEdgeProcessIds.TryRemove(run.Process.Id, out _);
        }
        catch (InvalidOperationException)
        {
            // The process was never started, so it has no id to forget.
        }

        try
        {
            File.Delete(run.ConfigPath);
        }
        catch
        {
            // Best-effort cleanup; the file never contains the encryption key.
        }

        // n3n restores the interface metric itself, but only on a clean exit - and it
        // gets killed or crashes often enough that a stale metric=1 would otherwise
        // outrank the user's real network card while MikuN2N is not even running.
        if (run.TunedAdapterId is { } tunedAdapter)
        {
            _ = NetworkTuningService.RestoreAsync(tunedAdapter, _ => { });
        }

        TryCancel(run.MonitorCancellation);
        run.PeerDiscovery?.Stop();
        run.PortMapping?.Dispose();
        run.MonitorCancellation.Dispose();
        run.ManagementClient.Dispose();
        lock (run.LogGate)
        {
            run.LogWriter.Dispose();
        }
    }

    private void Publish(ConnectionSnapshot snapshot)
    {
        _parameters?.Diagnostics?.Write("connection_snapshot", snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A process exit and a user-requested stop can finish concurrently.
        }
    }

    private sealed record ConnectionParameters(
        string Server,
        string Community,
        string Nickname,
        string NodeId,
        string EncryptionKey,
        EdgeEngine Engine,
        string? NodeName,
        bool ExperimentalIpv6P2p,
        TestDiagnosticsSession? Diagnostics);

    private enum EdgeEngine
    {
        LegacyN2n,
        N3n
    }

    private sealed class EdgeRun(
        int sessionId,
        Process process,
        N2nManagementClient managementClient,
        CancellationTokenSource monitorCancellation,
        string configPath,
        string logPath,
        StreamWriter logWriter,
        bool isN3n,
        string nodeId,
        string nickname)
    {
        public int SessionId { get; } = sessionId;
        public Process Process { get; } = process;
        public N2nManagementClient ManagementClient { get; } = managementClient;
        public CancellationTokenSource MonitorCancellation { get; } = monitorCancellation;
        public string ConfigPath { get; } = configPath;
        public string LogPath { get; } = logPath;
        public StreamWriter LogWriter { get; } = logWriter;
        public bool IsN3n { get; } = isN3n;
        public string NodeId { get; } = nodeId;
        public string Nickname { get; } = nickname;
        /// <summary>Hosts of the node this run connects to; anything else is another federation member.</summary>
        public IReadOnlySet<string> SupernodeHosts { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>User-visible name of the selected node, shown instead of its raw address.</summary>
        public string? ActiveNodeName { get; init; }
        public object LogGate { get; } = new();
        public object DiscoveryGate { get; } = new();
        public object PeerModeGate { get; } = new();
        public object RelayPolicyGate { get; } = new();
        public List<string> LogLines { get; } = [];
        public IReadOnlyList<PeerSnapshot> Peers { get; set; } = [];
        public IReadOnlyList<JsonElement> PeerRuntimeRows { get; set; } = [];
        public bool NativeIdentityRead { get; set; }
        public string? NativeVersion { get; set; }
        public int? Ipv6WireVersion { get; set; }
        public IReadOnlyDictionary<string, PeerConnectionMode> PeerModes { get; set; } =
            new Dictionary<string, PeerConnectionMode>();
        public IReadOnlyDictionary<string, string> PeerPathKeys { get; set; } =
            new Dictionary<string, string>();
        /// <summary>Edge MAC per management-row key ("ip:x" / "name:x"), used to address a peer whose virtual IPv4 n3n never learned.</summary>
        public IReadOnlyDictionary<string, string> PeerRowMacs { get; set; } =
            new Dictionary<string, string>();
        public Dictionary<string, string> PeerMacAddresses { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> RelayedSince { get; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, PeerConnectionMode> LoggedPeerModes { get; } =
            new(StringComparer.Ordinal);
        public HashSet<string> AppliedRelayPolicyCommands { get; } =
            new(StringComparer.Ordinal);
        /// <summary>Adapter whose metric this run changed, so teardown can hand it back.</summary>
        public string? TunedAdapterId { get; set; }
        public PeerDiscoveryService? PeerDiscovery { get; set; }
        public UpnpPortMappingService? PortMapping { get; set; }
        public string? NetworkHint { get; set; }
        public string? CompatibilityHint { get; set; }
        public string NatType { get; set; } = "检测中";
        public string NatDescription { get; set; } = "正在通过 n3n 数据端口检测 NAT 行为。";
        public string? VirtualIp { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public bool StopRequested { get; set; }
        public volatile bool AddressConflict;
        public volatile bool LogConfirmedConnected;
        public volatile bool ManagementUnavailable;
        public volatile bool RegistrationBlocked;
        public long RegistrationFailureStartedMs;
        public DateTimeOffset NextManagementRetryAt { get; set; }
        public DateTimeOffset NextNatPollAt { get; set; }
        public bool NatPollWarningLogged { get; set; }
        public bool ExperimentalIpv6P2p { get; init; }
        public TestDiagnosticsSession? Diagnostics { get; init; }
        public DateTimeOffset NextDiagnosticsAt { get; set; }
        public bool Ipv6StatusReported { get; set; }
        /// <summary>Sockaddr of the supernode the edge is currently anchored to (get_supernodes current=1).</summary>
        public string? CurrentSupernode { get; set; }
        public long? SupernodeRttMs { get; set; }
        public DateTimeOffset NextSupernodePingAt { get; set; }
        public DateTimeOffset NextRelayReconcileAt { get; set; }
        public bool RelayPolicyRestored { get; set; }
        public bool RelayReconcileWarningLogged { get; set; }
        public bool RelayUnmatchedWarningLogged { get; set; }
        public bool EncryptionMismatchLogged { get; set; }
        public volatile bool VirtualAddressReleased;
        public volatile bool DiscoveryUnavailable;
        public volatile bool NativePunchTelemetry;
        public int CleanedUp;
    }

    private enum SupernodeState
    {
        Unknown,
        Waiting,
        Registered
    }
}

public sealed record ConflictingEdgeProcess(
    int ProcessId,
    string ProcessName,
    DateTimeOffset? StartedAt);
