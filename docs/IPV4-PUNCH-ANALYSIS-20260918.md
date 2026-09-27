# IPv4 punch analysis: September 18, 2026

Analysis completed on September 19 from existing client logs, consented server
uploads, and release-tagged source. No client was launched, no probe was initiated,
and no algorithm, binary, configuration, or server service was changed.

## Result and scope

The logs establish a native state-machine defect that can consume a retry without
sending any bank probes. They also establish unequal retry budgets between the
two endpoints. The cone candidate model has a separate, concrete coverage limit,
but the evidence does not establish it as the unique cause of unsuccessful IPv4
connections. Wait for more paired 0.5.8-4 traces before choosing the next change.

The [September 19 relay-pair follow-up](RELAY-PAIR-ANALYSIS-20260919.md) ranks the
largest flows, incorporates counterpart logs, and distinguishes loss of an
established IPv4 path from the separate old-client rollback and IPv6 MTU cases.

Endpoint A denotes the initiating client; B is the owner of the supplied archive;
C is another older client. Identities, addresses, actual mapping ports, and raw
logs remain in ignored local artifacts. Times below are client wall-clock times
in UTC+8 unless stated otherwise. Approximately two seconds of clock difference
is visible between A and B in the September 17 exchange; do not infer a GO
scheduling error from their absolute timestamps alone.

Evidence retained locally under `artifacts/analysis-20260918-ipv4/`:

- The supplied archive contains 12 native logs, six diagnostic logs, and one crash
  log, covering September 9 through September 18. It ends before B's 0.5.8-4
  session; those newer records were obtained separately from the receiver.
- Four diagnostic files end near the old 64 MiB recording limit. Native logs
  continue into the evening, filling the previously missing September 17 trace.
- `inventory.local.md` and `native-events.local.md` contain file, line, timestamp,
  and parsed event references. `analysis.local.md` contains the private pairing
  and source references; `updated-pair.local.md` contains selected newer records.
- The prior A-side snapshot is retained in the 0.5.8-4 IPv4 investigation folder.
  Older receiver records may already have expired under its retention policy.

These are matched log events, not a connection-success-rate denominator:

| Event | Entire archive | B's three 0.5.8-1 native logs |
|---|---:|---:|
| Worker calibration reports | 124 | 90 |
| Reports selecting model 1 / CONE | 124 | 90 |
| Bank punch starts | 78 | 41 |
| Starts with zero local workers | 2 | 1 |
| Bank coordination timeouts | 28 | 14 |
| Tier 1 success reports | 16 | 8 |

Several peers, retries, native versions, legacy attempts, and IPv6 pauses coexist
in these logs. Dividing successes by starts would produce a misleading overall
IPv4 success rate. A queued/sent packet counter does not establish remote receipt.

## F1: a late bank plan starts an empty round after fallback

**Confirmed from runtime events and the owning source.** On September 17, B runs
0.5.8-1 and attempts IPv4 P2P to C:

| B time | Event |
|---|---|
| 18:33:40.661 | Bank coordination times out; workers are closed and legacy punching starts. |
| 18:33:44.695 | A bank plan arrives, advertising 25 workers on the other endpoint. |
| 18:33:45.162 | Local bank punching starts with `workers=0`, budget 7000 ms. |
| 18:33:52.171 | The round ends after 64 ticks and **0 packets**, consumes round 1/3, and waits 30 seconds. |
| 18:34:33.128 | A later attempt succeeds after creating a new worker pool. |

The same empty-round pattern appears on September 15 in the older build. These
are two occurrences, not an estimate of prevalence.

Native `src/edge_utils.c` ownership at native tag `0.5.8-4`:

- `mikun2n_close_bank_workers`, lines 3827-3837, closes sockets and zeros the count.
- `mikun2n_bank_update`, lines 4442-4451, enters FALLBACK after coordination timeout.
- PEER_INFO bank-plan handling, lines 3579-3607, accepts a changed peer bank nonce
  and assigns ARMED without requiring live local workers or an eligible local
  bank phase. This can overwrite FALLBACK.
- Lines 4454-4498 start the empty round, reset its counters, and consume the retry
  budget on expiry. The spray loop has no sockets to send through.

The earlier legacy packets are separate from the new round's zero counter.
The defect interrupts useful fallback, spends seven seconds doing no bank work,
and charges a failure. A future fix should reject or safely reconcile plans for
an expired local calibration and should never charge an empty bank round. Do not
solve this by removing the bounded retry policy.

## F2: independent retry histories leave one endpoint waiting alone

**Confirmed scheduling limitation; not proof of why the first two rounds missed.**
A starts a new 0.5.8-4 session on September 17 while B remains on 0.5.8-1. Matching
pair generation and advertised bank values identify the same A-B exchange:

| Stage | A time and result | B time and result |
|---|---|---|
| First paired bank attempt | 20:47:48.160 start; 20:47:55.167 round **1/3** fails, 4,650 packets | 20:47:50.151 start; 20:47:57.152 round **2/3** fails, 5,500 packets |
| Second paired bank attempt | 20:48:29.964 start; 20:48:36.952 round **2/3** fails, 5,250 packets | 20:48:31.947 start; 20:48:38.949 round **3/3** is abandoned, 6,875 packets |
| A's remaining attempt | Recalibrates twice; 20:49:28.269 coordination timeout; legacy attempt ends 20:49:37.971, 3,696 packets | Already abandoned until a manual retry/reset |

A sends 13,596 counted probe packets across these three attempts. This does not
mean B received them. The original event that consumed B's earlier allowance is
not uniquely identifiable from the terse peer-less failure messages.

`mikun2n_apply_punch_history` (lines 226-239) restores history keyed by peer MAC.
The generation-change handler resets counters and then reapplies that history
(lines 3554-3571). `mikun2n_bank_update` returns immediately for an abandoned peer
(lines 4342-4343). GO coordination does not give A a matching remaining-round
budget or a usable terminal response for this situation.

History retention intentionally prevents candidate refreshes from restarting an
unlimited probe loop. A future design should coordinate attempt eligibility and
completion between the pair while preserving this bound. Clearing history on
every refresh would reintroduce continuous probing.

## F3: same-address worker reuse is a limited candidate hint

**The coverage limit is confirmed; its contribution to a particular missed path
is still a hypothesis.** All 124 archived worker calibrations select CONE, while
the main-socket NAT probe reports NAT4 with zero matching A/B samples in its
completed runs. These are different sockets and observations; the discrepancy
alone is not proof that the calibration result is corrupt.

The current data flow explains the risk:

1. `mikun2n_send_bank_probe` (lines 3891-3923) sends from each worker to two ports
   on the same supernode IP. It does not test mapping reuse across destination IPs.
2. `mikun2n_fit_bank_model` (lines 4003-4010) selects CONE when at least 80% of valid
   A samples have matching B mappings. Its candidate banks then use the main
   socket's public port instead of preserving individual worker endpoints.
3. Native `src/sn_utils.c`, lines 2740-2745, replaces CONE banks with the source
   edge's registration port. A difference between B's local bank log and A's
   received bank is therefore expected behavior, not evidence of corruption.
4. `mikun2n_bank_spray_tick` (lines 4276-4291) targets the peer control port, a
   near band of +/-1..64, and a rotating 192-port band. With the three-round limit,
   ordinary bank rounds reach offsets 65..256, 257..448, then 449..640. They do
   not reach the reference tool's much later bands automatically. Actual coverage
   depends on ticks and successful sends; this is not an exhaustive-scan claim.

The reference [natpunch notes](../tools/natpunch/README.md) already acknowledge the
same-IP limitation and describe a longer sequence of bands. The integrated edge's
three-round budget makes the difference material. Legacy fallback has a different
schedule; the +/-640 observation applies specifically to these bank cone rounds.

Mapping reuse is defined for the **same internal IP and port** when changing
external destinations; reuse across two ports on one external IP cannot establish
endpoint-independent mapping to another IP. Mapping and filtering are separate
behaviors. See [RFC 4787, section 4.1](https://www.rfc-editor.org/rfc/rfc4787.html#section-4.1).

Existing logs do not retain every worker's mapped A/B endpoints, peer-observed
source endpoints, and matching receive/ACK rejection outcomes. Consequently they
cannot establish whether the missed mapping was outside the bands, packets were
filtered or lost, or an endpoint/coordination state changed. Preserve these
distinctions before selecting a port-prediction change. More packets alone are
not an evidence-based remedy.

## Multiple-peer scheduling and version boundaries

`mikun2n_bank_pool_busy` permits only one active bank worker pool per client.
The server stores one current bank target per edge. Local peer ordering can delay
one partner while the other recalibrates; this is a possible contributor to the
28 coordination timeouts, not a proven explanation for all of them.

`git diff 0.5.8-1..0.5.8-4` is empty for the native `src/edge_utils.c` and
`src/sn_utils.c` files containing the IPv4 logic above. These findings should not
be assumed fixed by the IPv6 updates. September 17 IPv6 wire-generation 2/3
incompatibility explains why IPv6 could not substitute then; it does not by
itself explain unsuccessful IPv4 punching.

## What the September 18 updated pair establishes

Both uploaded sessions identify client and native version 0.5.8-4 and IPv6 wire
generation 3. On A's own clock, session start at 23:21:28.514 is followed by
`data_path_ready` at 23:21:29.554, about **1.04 seconds** later. Both endpoints'
management records then show `p2p / ipv6`; native summaries show transmitted and
received DATA. This supports the reported immediate public-IPv6/NAT66 connection
for this pair; topology identification includes the operator's description.

The overlap lasts roughly five and a half minutes. B has one readiness expiry at
23:21:48.561 followed by readiness recovery at 23:21:48.600, about 39 ms later.
Management polling does not sample that short interval; uninterrupted IPv6 DATA
cannot be claimed from the polling rows alone. The logs do not establish that
application data was actually relayed during the interval.

B exits with code 0 and records `session_end` at 23:27:03.922. A's subsequent
expiry and relay state around 23:27:10 occur after that exit; this is not evidence
of a spontaneous sustained IPv6 fallback while both processes remain connected.
The overlapping sample contains no fragmented DATA. It is not a sustained gaming,
fragmentation, or all-network acceptance test, and does not validate IPv4 success.

## Deferred iteration and evidence gaps

Keep the two requested product follow-ups in [TODO.md](../TODO.md): the complete
Chinese link tooltip and a fresh consented diagnostic window for a newly joining
peer despite exhausted earlier allowances. The old archive's 64 MiB cutoffs make
the latter evidence gap concrete. Current rolling segments and receiver quotas
must be distinguished from a hard stop in the older client.

For a later authorized IPv4 iteration, prioritize the empty-round state transition
and coordinated retry eligibility. Evaluate candidate changes against paired
logs with peer identity, both session/attempt identifiers, current local phase,
remaining budget, worker-local port, both observed mappings, actual probe target,
send result, receive/ACK result, and explicit fallback reason. Existing 0.5.8-4
logs can improve pair timing and outcomes but cannot retroactively supply missing
per-worker fields. Synchronized captures or targeted additional instrumentation
may still be required to attribute mapping versus filtering/loss.

This analysis changes documentation only. No builds, tests, release, or deployment
were performed; source/event comparison and document checks are the validation.
