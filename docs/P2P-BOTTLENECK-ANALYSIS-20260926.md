# P2P bottleneck analysis and 0.5.8-5 changes: September 26, 2026

> [English](P2P-BOTTLENECK-ANALYSIS-20260926.md) | [简体中文](P2P-BOTTLENECK-ANALYSIS-20260926.zh.md)

This note follows the [September 18 IPv4 analysis](IPV4-PUNCH-ANALYSIS-20260918.md)
and the [September 19 relay-pair analysis](RELAY-PAIR-ANALYSIS-20260919.md). It is
based on this machine's native logs from September 9-20, mainly one 15-hour 0.5.8-4
session (September 19 19:27 to September 20 10:34), read against the native source
at tag `0.5.8-4`. Peers are described by role; addresses, MACs and node names stay
in local files. No client was connected and no server was changed for this analysis.

## What the logs show

Headline numbers for the 15-hour session:

| Observation | Value |
|---|---|
| IPv6 readiness expiries toward the main IPv6 peer | 65 (median outage 0.49 s, p90 1.8 s, max 3.5 s) |
| That peer's base keepalive probes without a PONG within 2 s | 4.8% |
| Median time since the last PONG when readiness expired | 3.5 s |
| IPv4 scan resumptions caused by IPv6 readiness gaps | 65 |
| Legacy single-socket Tier 1 rounds / packets / connections | 42 / about 455,000 / 0 |
| Coordinated bank rounds with workers / connections | 5 / 3 |
| Distinct MAC identities of one remote host (one public endpoint) | 15 |
| QUERY_PEER messages sent by the IPv6 refresh loop | 128,694 (2.4/s) |
| ...of which for the LLDP group address `01:80:C2:00:00:0E` | about 17,800 |
| Bank rounds started with zero workers (F1) | 1 (still present in 0.5.8-4) |
| Established IPv4 paths lost after a receive gap and re-punched | 2 toward one peer |

Across all local logs since September 13, 26 Tier 1 successes were recorded. 19 came
through coordinated banks. None came from this machine acting as the legacy scanner.

### IPv6: the lease, not the path, was failing

`mikun2n_ipv6_active` requires two leases: our own checked path (6.5 s after our last
matched PONG) and the peer's advertised receive readiness. The peer computes that
readiness from *its* own last PONG. Probes were sent every 2 s regardless of loss.
Losing about two consecutive exchanges on either side therefore expired readiness,
although the path itself remained usable. PONGs immediately before an expiry often
advertised only 0.5-1.8 s of remaining peer readiness, which confirms the chained
dependency.

A simple model reproduces the order of magnitude: two endpoints with independent
per-exchange loss of 4.8% expire readiness about 225 times per 15 hours between them
with the old schedule. The log shows 65 on one end.

Each expiry had three effects:

- DATA was relayed through the supernode.
- IPv4 worker calibration restarted.
- The friend list flickered between "trying to connect" and "IPv6 direct".

### IPv4: most packets went to peers that could not answer

- **Departed identities.** Every client reconnect registers a fresh MAC. Upstream
  `purge_peer_list` skips tables with fewer than 16 entries, so pending entries for
  departed identities were never purged. The edge punched each of them with a full
  three-round budget and queried the supernode for them every 3 s. Fifteen identities
  of one host accumulated in a single evening. Concurrent rounds reached about
  3,500 packets/s and six sends failed with `WSAENOBUFS`.
- **Futile fallback.** When bank coordination timed out, APDM-to-APDM pairs fell back
  to the single-socket scanner (about 11,600 packets per round), which never
  connected. Coordinated banks connected 3 of 5 rounds that actually had workers.
- **Established paths.** After `timeout/2` (10 s) without a direct receipt,
  `find_peer_destination` freed the known entry, and `peer_info_free` closed the
  winning worker socket. The peer's filter admits only that socket's mapping, so a
  short interruption became a full re-punch. This matches the pair that carried half
  of the relayed bytes in the September 19 accounting.
- **F1 is still present.** A bank plan arriving after local fallback armed a spray
  with no sockets and charged a round.

**Unproven correlation.** Three of 20 path-loss bursts fell inside the 12 minutes of
densest local legacy scanning, about eleven times the time-share expectation. That
is suggestive of NAT session pressure from our own scans, but the sample is small and
not proof.

### Supernode bandwidth

The dominant server cost remains relayed unicast game traffic. Control traffic is
small, but on a 4 Mbit/s host that is shaped by the provider it is dropped together
with relay traffic once the link is full. That makes registration and punch
coordination fail exactly when direct paths are most needed.

## Options considered

| Option | Decision |
|---|---|
| Lengthen the IPv6 path TTL | Rejected: PONG receivers reject `ready_ms > 6500`, so it breaks generation-3 peers. |
| Probe faster only near lease expiry | Adopted: no wire change, and the steady-state rate is unchanged. |
| Treat received IPv6 DATA as liveness | Deferred: needs a wire-level signal to stay symmetric with older peers. |
| Lengthen the IPv4 `timeout/2` deletion | Rejected: it keeps blackholing traffic on a dead path. Relay-while-revalidating gives the same benefit without the loss. |
| Remove the legacy scanner entirely | Rejected: kept as a last resort after two free coordination retries, for peers that cannot coordinate. |
| Remember failures per public endpoint across MAC changes | Deferred: silent-identity gating removes most waste, and a real reconnect should still get a fresh try. |
| Queue relayed DATA on the supernode | Rejected: queuing adds latency; policing below the link rate keeps control traffic flowing. |
| Shape with Linux `tc` on the server | Not chosen: needs DSCP marking per packet type. The in-process policy is deterministic and testable offline. |

## Changes in 0.5.8-5

The authoritative native list is item 50 in [Runtime/README.txt](../Runtime/README.txt).
In summary:

- **IPv6 keepalive.** Probes are scheduled for the loss case and repeated every 500 ms
  near the end of either lease; a PONG restores the 2 s cadence. With the model's
  4.8% independent loss, expiries drop from about 225 to 0 per 15 hours. A real path
  failure still expires on time.
- **IPv4 scan resumption.** IPv4 scanning resumes only after IPv6 stays unavailable
  for 15 s.
- **IPv4 standby.** A working IPv4 path is kept alive while IPv6 carries DATA, so an
  IPv6 gap falls back to it instead of the relay.
- **Path recovery.** An established IPv4 path keeps its entry and winning socket for
  20 s after a receive gap. DATA is relayed meanwhile and the old endpoint is probed
  every second.
- **Departed identities.**
  - Pending entries expire on `last_seen` at any table size.
  - A peer is treated as departed after 30 s with neither packets nor a supernode
    answer: its round is suspended without a charge and it is queried less often.
  - Group MACs outside IPv4/IPv6 multicast are dropped at the TAP.
- **Bank coordination.**
  - A late bank plan is accepted only by a live calibration, and a spray without
    workers is never charged.
  - A missed APDM coordination recalibrates twice, 10 s apart, before legacy.
  - Only one heavy scan runs at a time.
- **Supernode relay policy.** The optional `mikun2n_relay_kbit` and
  `mikun2n_broadcast_pps` settings police relayed DATA only. Both are off by default.
  See [supernode/README.md](../supernode/README.md).
- **Client.**
  - Brief loss of a direct path keeps the direct label for 5 s.
  - A session that never reached the server explains likely misconfiguration instead
    of asking the player to wait.
  - An orderly edge exit (code 0 with packet statistics) is no longer described as a
    crash.

## Validation and remaining gaps

- **Native regressions.** `tests-ipv6` passes 17 checks, 4 of them new: keepalive
  rescheduling, loss cadence, fast probing only after granted readiness, and
  departed-identity detection. `tests-relay` passes 6 new checks: disabled mode,
  single source, a protected light source beside a heavy one, equal split, broadcast
  storms, and idle release. `tests-wire` passes.
- **Client.** The offline regression suite and `dotnet build` pass.
- **Not yet verified.**
  - Live pairing on IPv4, IPv6 and mixed-version pairs.
  - Server deployment and a live test of the relay policy.
  - The UI in both themes.

The next useful evidence is a paired session between two 0.5.8-5 clients. It would
show:

- whether readiness expiries disappear;
- how often `IPv4 path recovered` appears compared with `not recovered`;
- how many `bank coordination missed` events end in a bank connection.

For NAT4-to-NAT4 pairs that still fail after coordinated banks, per-worker mapping
records from both sides remain the missing evidence noted on September 18.
