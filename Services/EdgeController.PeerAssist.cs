using System.Collections.Concurrent;
using System.Diagnostics;
using MikuN2N.Models;

namespace MikuN2N.Services;

public sealed partial class EdgeController
{
    public event EventHandler<PeerAssistRequest>? PeerAssistRequested;

    private PeerAssistCoordinator RequirePeerAssist()
    {
        lock (_stateGate)
        {
            if (_currentRun is not { PeerAssistSupported: true, PeerAssist: { } assist })
                throw new InvalidOperationException("好友协助尚未就绪，或当前 edge 不支持此功能。");
            return assist;
        }
    }

    public Task<IReadOnlyList<PeerAssistCandidate>> GetPeerAssistCandidatesAsync(
        string targetNodeId, CancellationToken cancellationToken) =>
        RequirePeerAssist().GetCandidatesAsync(targetNodeId, cancellationToken);

    public Task RequestPeerAssistAsync(string targetNodeId, string relayNodeId,
        CancellationToken cancellationToken) =>
        RequirePeerAssist().RequestAsync(targetNodeId, relayNodeId, cancellationToken);

    public Task RespondPeerAssistAsync(string circuitId, bool accept,
        CancellationToken cancellationToken) =>
        RequirePeerAssist().RespondAsync(circuitId, accept, cancellationToken);

    public Task StopPeerAssistAsync(string circuitId, CancellationToken cancellationToken) =>
        RequirePeerAssist().RemoveAsync(circuitId, "协助已结束。", true, cancellationToken);

    public IReadOnlyList<PeerAssistSession> GetPeerAssistSessions()
    {
        lock (_stateGate) return _currentRun?.PeerAssist?.GetSessions() ?? [];
    }

    public IReadOnlyList<PeerAssistRequest> GetPeerAssistRequests()
    {
        lock (_stateGate) return _currentRun?.PeerAssist?.GetRequests() ?? [];
    }

    // The relay owns the lease. Endpoints only renew after a relay heartbeat;
    // losing any participant therefore removes approval without reconnecting TAP.
    private sealed class PeerAssistCoordinator
    {
        private readonly EdgeController _owner;
        private readonly EdgeRun _run;
        private readonly PeerDiscoveryService _discovery;
        private readonly CancellationTokenSource _lifetime;
        private readonly ConcurrentDictionary<string, AssistCircuit> _circuits = new();
        private readonly ConcurrentDictionary<string, long> _closedCircuits = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _acks = new();
        private readonly SemaphoreSlim _mutations = new(1, 1);
        private readonly Task _maintenance;
        private string Local => _run.NodeId;
        private CancellationToken Token => _lifetime.Token;

        internal PeerAssistCoordinator(EdgeController owner, EdgeRun run, PeerDiscoveryService discovery)
        {
            _owner = owner;
            _run = run;
            _discovery = discovery;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(run.MonitorCancellation.Token);
            discovery.AssistMessageReceived += OnMessage;
            _maintenance = MaintainAsync();
        }

        internal void Stop()
        {
            _discovery.AssistMessageReceived -= OnMessage;
            _lifetime.Cancel();
            foreach (var circuit in _circuits.Values)
                circuit.Completion.TrySetCanceled();
            foreach (var ack in _acks.Values) ack.TrySetCanceled();
        }

        private PeerSnapshot? Find(string id) => _discovery.GetPeers().FirstOrDefault(p => p.NodeId == id);
        private string Name(string id) => id == Local ? "你" : Find(id)?.Nickname ?? "已离线好友";
        private static bool IsDirect(PeerConnectionMode mode) => mode is
            PeerConnectionMode.Direct or PeerConnectionMode.Ipv6Direct or PeerConnectionMode.LanDirect;
        private bool Direct(string id) => Find(id) is { } peer &&
            IsDirect(GetPeerConnectionMode(_run, peer, allowLatencyInference: false));

        internal async Task<IReadOnlyList<PeerAssistCandidate>> GetCandidatesAsync(string target,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            if (Find(target) is null) throw new InvalidOperationException("通信对象已离线。");
            var targetSupported = _discovery.SupportsPeerAssist(target);
            var candidates = await Task.WhenAll(_discovery.GetPeers().Where(p => p.NodeId != target).Select(async relay =>
            {
                var local = _discovery.GetLinkQuality(relay.NodeId);
                LinkQualitySnapshot? remote = null;
                var supported = _discovery.SupportsPeerAssist(relay.NodeId);
                if (supported && targetSupported)
                    remote = await _discovery.QueryLinkQualityAsync(relay.NodeId, target, linked.Token);
                var eligible = supported && targetSupported && Direct(relay.NodeId) &&
                    remote is not null && IsDirect(remote.Mode) && !remote.IsStale;
                var status = !targetSupported ? "通信对象需要更新客户端" :
                    !supported ? "该好友需要更新客户端" :
                    !Direct(relay.NodeId) ? "你到该好友尚未直连" :
                    remote is null || remote.IsStale ? "等待对方提供近期质量" :
                    !IsDirect(remote.Mode) ? "该好友到目标尚未直连" :
                    local is null || local.Samples < 5 || remote.Samples < 5 ? "样本较少，可申请后确认" :
                    "可申请；按探测质量排序";
                return new PeerAssistCandidate(relay, local, remote, eligible, status);
            }));
            return candidates.OrderByDescending(c => c.Eligible)
                .ThenByDescending(c => c.ToRelay?.HasEnoughSamples == true && c.RelayToTarget?.HasEnoughSamples == true)
                .ThenBy(c => c.EstLossPercent ?? double.MaxValue)
                .ThenBy(c => c.EstRttMs ?? double.MaxValue).ToArray();
        }

        internal IReadOnlyList<PeerAssistRequest> GetRequests() => _circuits.Values
            .Where(c => c.Relay == Local && c.Phase == "requested")
            .Select(c => new PeerAssistRequest(c.Id, c.Requester, Name(c.Requester),
                c.Target, Name(c.Target), c.Relay)).ToArray();

        internal IReadOnlyList<PeerAssistSession> GetSessions() => _circuits.Values.Select(c =>
            new PeerAssistSession(c.Id, c.Requester, c.Target, c.Relay,
                $"{Name(c.Requester)} ↔ {Name(c.Target)}，经 {Name(c.Relay)}：" +
                (c.Phase == "requested" ? "等待同意" : c.Phase != "committed" ? "正在建立" :
                 c.NativeActive ? "协助转发中" : "线路确认中，当前保留原路径"),
                c.Relay == Local, c.NativeActive && c.Phase == "committed")).ToArray();

        internal string? ActivePath(string target) => _circuits.Values.FirstOrDefault(c =>
            c.Relay != Local && c.Other(Local) == target && c.NativeActive && c.Phase == "committed")?.Id;

        internal string? ActiveRelayName(string target) => _circuits.Values.FirstOrDefault(c =>
            c.Relay != Local && c.Other(Local) == target && c.NativeActive && c.Phase == "committed") is { } circuit
                ? Name(circuit.Relay) : null;

        internal async Task RequestAsync(string target, string relay, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            if (target == relay || target == Local || relay == Local ||
                Find(target) is null || Find(relay) is null ||
                !_discovery.SupportsPeerAssist(target) || !_discovery.SupportsPeerAssist(relay))
                throw new InvalidOperationException("请选在线且支持协助的两位不同好友。");
            if (!Direct(relay)) throw new InvalidOperationException("你与协助者尚未建立直连。");
            var state = new AssistCircuit(Guid.NewGuid().ToString("N"), Local, target, relay);
            await _mutations.WaitAsync(linked.Token);
            try
            {
                if (Conflicts(state)) throw new InvalidOperationException("与该好友已有协助申请或会话，请先结束。");
                _circuits[state.Id] = state;
            }
            finally { _mutations.Release(); }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                while (!state.Completion.Task.IsCompleted)
                {
                    await SendAsync(state, relay, "request", timeout.Token);
                    await Task.WhenAny(state.Completion.Task, Task.Delay(2000, timeout.Token));
                    timeout.Token.ThrowIfCancellationRequested();
                }
                await state.Completion.Task;
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                await RemoveAsync(state.Id, "协助申请超时。", true, Token);
                throw new InvalidOperationException("对方未及时确认，协助申请已取消。");
            }
            catch
            {
                await RemoveAsync(state.Id, "协助申请已取消。", true, Token);
                throw;
            }
        }

        internal async Task RespondAsync(string id, bool accept, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
            if (!_circuits.TryGetValue(id, out var state) || state.Relay != Local)
                throw new InvalidOperationException("申请已过期或撤回。");
            if (!accept)
            {
                await RemoveAsync(id, "对方未接受协助申请。", true, linked.Token);
                return;
            }
            await _mutations.WaitAsync(linked.Token);
            try
            {
                if (state.Phase != "requested") return;
                if (!Direct(state.Requester) || !Direct(state.Target))
                    throw new InvalidOperationException("你需要与申请人、通信对象都建立直连，才能协助转发。");
                state.Phase = "preparing";
                await InstallAsync(state, "relay", linked.Token);
            }
            catch
            {
                state.Phase = "requested";
                if (state.NativeRole is not null)
                    await RemoveCoreAsync(state, "协助配置未能确认，申请已取消。", true, Token);
                throw;
            }
            finally { _mutations.Release(); }
            try
            {
                await Task.WhenAll(ExchangeAsync(state, state.Requester, "prepare", "prepared", linked.Token),
                    ExchangeAsync(state, state.Target, "prepare", "prepared", linked.Token));
                await Task.WhenAll(ExchangeAsync(state, state.Requester, "commit", "committed", linked.Token),
                    ExchangeAsync(state, state.Target, "commit", "committed", linked.Token));
                state.Phase = "committed";
                state.RequesterSeen = state.TargetSeen = Stopwatch.GetTimestamp();
                await SendAsync(state, state.Requester, "accepted", linked.Token);
                _owner.AppendLog(_run, $"好友协助：已同意协助 {Name(state.Requester)} 与 {Name(state.Target)} 通信。");
            }
            catch
            {
                await RemoveAsync(id, "协助未能完成建立，已保留原路径。", true, Token);
                throw;
            }
        }

        private bool Conflicts(AssistCircuit state) => _circuits.Values.Any(c => c.Id != state.Id &&
            c.Relay != Local && state.Relay != Local && c.Other(Local) == state.Other(Local));

        private async Task InstallAsync(AssistCircuit state, string role, CancellationToken token)
        {
            if (!_circuits.TryGetValue(state.Id, out var current) || !ReferenceEquals(current, state))
                throw new InvalidOperationException("协助已结束。");
            if (state.FirstMac is null || state.SecondMac is null)
            {
                state.FirstMac = ResolveMac(state.Relay == Local ? state.Requester : state.Other(Local));
                state.SecondMac = ResolveMac(state.Relay == Local ? state.Target : state.Relay);
            }
            // A timed-out HTTP response may still have installed the native lease.
            state.NativeRole = role;
            await _run.ManagementClient.SetPeerAssistAsync(state.Id, role, state.FirstMac,
                state.SecondMac, true, token);
        }

        private string ResolveMac(string id)
        {
            var peer = Find(id) ?? throw new InvalidOperationException("参与协助的好友已离线。");
            // Do not bind consent by nickname: duplicate display names are permitted.
            return ResolvePeerEdgeMac(_run, peer.VirtualIp, null)
                ?? throw new InvalidOperationException("尚未确认好友的网络身份，请稍后重试。");
        }

        private Task SendAsync(AssistCircuit state, string recipient, string kind, CancellationToken token,
            string? detail = null) => _discovery.SendAssistMessageAsync(recipient,
                new PeerAssistMessage(kind, state.Id, state.Requester, state.Target, state.Relay, Detail: detail), token);

        private async Task ExchangeAsync(AssistCircuit state, string recipient, string kind,
            string response, CancellationToken token)
        {
            var key = $"{state.Id}:{recipient}:{response}";
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_acks.TryAdd(key, completion)) throw new InvalidOperationException("协助正在建立，请稍候。");
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    await SendAsync(state, recipient, kind, token);
                    await Task.WhenAny(completion.Task, Task.Delay(750, token));
                    token.ThrowIfCancellationRequested();
                    if (completion.Task.IsCompleted) { await completion.Task; return; }
                }
                throw new InvalidOperationException("好友未确认协助线路，已取消切换。");
            }
            finally { _acks.TryRemove(key, out _); }
        }

        private void OnMessage(object? sender, PeerAssistEnvelope envelope) =>
            _ = Task.Run(() => HandleSafelyAsync(envelope));

        private async Task HandleSafelyAsync(PeerAssistEnvelope envelope)
        {
            try { await HandleAsync(envelope); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _owner.AppendLog(_run, $"好友协助：{exception.Message}");
                if (_circuits.ContainsKey(envelope.Message.CircuitId))
                    await RemoveAsync(envelope.Message.CircuitId, "协助建立失败，已保留原路径。", true, Token);
            }
        }

        private async Task HandleAsync(PeerAssistEnvelope envelope)
        {
            if (!_run.PeerAssistSupported) return;
            var msg = envelope.Message;
            if (_closedCircuits.ContainsKey(msg.CircuitId)) return;
            if (msg.Kind is "prepared" or "committed")
            {
                if (_circuits.TryGetValue(msg.CircuitId, out var ackState) && ackState.Matches(msg) &&
                    ackState.Relay == Local && (envelope.SenderNodeId == ackState.Requester || envelope.SenderNodeId == ackState.Target))
                    if (_acks.TryGetValue($"{msg.CircuitId}:{envelope.SenderNodeId}:{msg.Kind}", out var ack))
                        ack.TrySetResult(true);
                return;
            }

            await _mutations.WaitAsync(Token);
            try
            {
                _circuits.TryGetValue(msg.CircuitId, out var state);
                if (state is not null && !state.Matches(msg)) return;
                if (msg.Kind == "request")
                {
                    if (msg.RelayNodeId != Local || envelope.SenderNodeId != msg.RequesterNodeId ||
                        Find(msg.TargetNodeId) is null || !_discovery.SupportsPeerAssist(msg.TargetNodeId)) return;
                    if (state is not null)
                    {
                        if (state.Phase == "committed") await SendAsync(state, state.Requester, "accepted", Token);
                        return;
                    }
                    // Coalesce retries and prevent repeated outstanding prompts for one pair.
                    if (_circuits.Values.Any(c => c.Requester == msg.RequesterNodeId && c.Target == msg.TargetNodeId)) return;
                    state = new AssistCircuit(msg.CircuitId, msg.RequesterNodeId, msg.TargetNodeId, Local);
                    _circuits[state.Id] = state;
                    _owner.PeerAssistRequested?.Invoke(_owner, new PeerAssistRequest(state.Id,
                        state.Requester, Name(state.Requester), state.Target, Name(state.Target), Local));
                    return;
                }
                if (msg.Kind == "prepare")
                {
                    if (envelope.SenderNodeId != msg.RelayNodeId || msg.RelayNodeId == Local ||
                        (msg.RequesterNodeId != Local && msg.TargetNodeId != Local) || !Direct(msg.RelayNodeId)) return;
                    if (state is null)
                    {
                        // Only the target accepts an unsolicited prepare. The requester must
                        // still have the exact outstanding choice it made in the UI.
                        if (msg.TargetNodeId != Local || Find(msg.RequesterNodeId) is null) return;
                        state = new AssistCircuit(msg.CircuitId, msg.RequesterNodeId, msg.TargetNodeId, msg.RelayNodeId);
                        if (Conflicts(state)) return;
                        _circuits[state.Id] = state;
                    }
                    if (state.Phase != "committed")
                    {
                        await InstallAsync(state, "standby", Token);
                        state.Phase = "prepared";
                    }
                    state.RelaySeen = Stopwatch.GetTimestamp();
                    await SendAsync(state, state.Relay, "prepared", Token);
                    return;
                }
                if (state is null) return;
                if (msg.Kind == "commit" && envelope.SenderNodeId == state.Relay &&
                    state.Relay != Local && state.Phase is "prepared" or "committed")
                {
                    await InstallAsync(state, "endpoint", Token);
                    state.Phase = "committed";
                    state.RelaySeen = Stopwatch.GetTimestamp();
                    await SendAsync(state, state.Relay, "committed", Token);
                }
                else if (msg.Kind == "accepted" && envelope.SenderNodeId == state.Relay &&
                         state.Requester == Local && state.Phase == "committed")
                    state.Completion.TrySetResult(true);
                else if (msg.Kind == "heartbeat" && envelope.SenderNodeId == state.Relay &&
                         state.Relay != Local && state.Phase == "committed")
                {
                    state.RelaySeen = Stopwatch.GetTimestamp();
                    await InstallAsync(state, "endpoint", Token);
                    await SendAsync(state, state.Relay, "alive", Token);
                }
                else if (msg.Kind == "alive" && state.Relay == Local && state.Phase == "committed")
                {
                    if (envelope.SenderNodeId == state.Requester) state.RequesterSeen = Stopwatch.GetTimestamp();
                    if (envelope.SenderNodeId == state.Target) state.TargetSeen = Stopwatch.GetTimestamp();
                }
                else if (msg.Kind == "stop" && state.Contains(envelope.SenderNodeId))
                    await RemoveCoreAsync(state, msg.Detail ?? "对方已结束协助。", state.Relay == Local, Token);
            }
            finally { _mutations.Release(); }
        }

        internal async Task RemoveAsync(string id, string detail, bool notify, CancellationToken token)
        {
            // Cancellation of a dialog must still roll back an installed native route.
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await _mutations.WaitAsync(cleanup.Token);
                try
                {
                    if (_circuits.TryGetValue(id, out var state))
                        await RemoveCoreAsync(state, detail, notify, cleanup.Token);
                }
                finally { _mutations.Release(); }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
            catch (OperationCanceledException)
            { throw new InvalidOperationException("撤销尚未确认，请稍后重试；原生协助会在停止续期后 30 秒内失效。"); }
        }

        private async Task RemoveCoreAsync(AssistCircuit state, string detail, bool notify, CancellationToken token)
        {
            if (!_circuits.TryRemove(state.Id, out _)) return;
            _closedCircuits[state.Id] = Stopwatch.GetTimestamp();
            state.NativeActive = false;
            state.Completion.TrySetException(new InvalidOperationException(detail));
            // Only locally initiated requests have a waiter; observe the other states too.
            _ = state.Completion.Task.Exception;
            foreach (var entry in _acks.Where(a => a.Key.StartsWith(state.Id + ":", StringComparison.Ordinal)))
                entry.Value.TrySetException(new InvalidOperationException(detail));
            Exception? removalError = null;
            if (state.NativeRole is not null && state.FirstMac is not null && state.SecondMac is not null)
            {
                try
                {
                    await _run.ManagementClient.SetPeerAssistAsync(state.Id, state.NativeRole,
                        state.FirstMac, state.SecondMac, false, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    removalError = exception;
                    _owner.AppendLog(_run, $"好友协助：本机撤销未获确认，将由短期租约失效：{exception.Message}");
                }
            }
            if (notify)
                foreach (var recipient in new[] { state.Requester, state.Target, state.Relay }.Where(p => p != Local))
                    try { await SendAsync(state, recipient, "stop", token, detail); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { _owner.AppendLog(_run, $"好友协助：结束通知未送达：{exception.Message}"); }
            _owner.AppendLog(_run, $"好友协助：{detail}");
            if (removalError is not null)
                throw new InvalidOperationException("撤销未获原生接口确认；已停止续期，协助会在 30 秒内失效。", removalError);
        }

        private async Task MaintainAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    await Task.Delay(3000, Token);
                    foreach (var closed in _closedCircuits)
                        if (Stopwatch.GetElapsedTime(closed.Value).TotalSeconds > 120)
                            _closedCircuits.TryRemove(closed.Key, out _);
                    if (_circuits.IsEmpty) continue;
                    await _mutations.WaitAsync(Token);
                    try
                    {
                        foreach (var state in _circuits.Values)
                        {
                            if (state.Phase != "committed")
                            {
                                if (Stopwatch.GetElapsedTime(state.Created).TotalSeconds > 60)
                                    await RemoveCoreAsync(state, "协助申请已过期。", true, Token);
                                continue;
                            }
                            var expired = state.Relay == Local
                                ? Stopwatch.GetElapsedTime(state.RequesterSeen).TotalSeconds > 15 ||
                                  Stopwatch.GetElapsedTime(state.TargetSeen).TotalSeconds > 15
                                : Stopwatch.GetElapsedTime(state.RelaySeen).TotalSeconds > 15;
                            if (expired)
                            {
                                await RemoveCoreAsync(state, "协助者或参与好友已失联，已恢复原路径。", true, Token);
                                continue;
                            }
                            if (state.Relay == Local)
                            {
                                await InstallAsync(state, "relay", Token);
                                await SendAsync(state, state.Requester, "heartbeat", Token);
                                await SendAsync(state, state.Target, "heartbeat", Token);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { _owner.AppendLog(_run, $"好友协助：续期暂未完成：{exception.Message}"); }
                    finally { _mutations.Release(); }
                    try
                    {
                        var rows = await _run.ManagementClient.GetPeerAssistsAsync(Token);
                        foreach (var state in _circuits.Values)
                        {
                            state.NativeActive = rows.Any(row => row.TryGetProperty("circuit", out var id) &&
                                id.GetString() == state.Id && row.TryGetProperty("active", out var active) &&
                                active.ValueKind == System.Text.Json.JsonValueKind.True);
                        }
                        if (_owner.IsCurrentRun(_run)) _owner.PublishConnected(_run);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        foreach (var state in _circuits.Values) state.NativeActive = false;
                        _owner.AppendLog(_run, $"好友协助：尚未确认转发状态：{exception.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        }

        private sealed class AssistCircuit(string id, string requester, string target, string relay)
        {
            internal string Id { get; } = id;
            internal string Requester { get; } = requester;
            internal string Target { get; } = target;
            internal string Relay { get; } = relay;
            internal long Created { get; } = Stopwatch.GetTimestamp();
            internal long RequesterSeen = Stopwatch.GetTimestamp();
            internal long TargetSeen = Stopwatch.GetTimestamp();
            internal long RelaySeen = Stopwatch.GetTimestamp();
            internal volatile string Phase = "requested";
            internal volatile bool NativeActive;
            internal string? NativeRole;
            internal string? FirstMac;
            internal string? SecondMac;
            internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string Other(string local) => local == Requester ? Target : Requester;
            internal bool Contains(string node) => node == Requester || node == Target || node == Relay;
            internal bool Matches(PeerAssistMessage msg) => Requester == msg.RequesterNodeId &&
                Target == msg.TargetNodeId && Relay == msg.RelayNodeId;
        }
    }
}
