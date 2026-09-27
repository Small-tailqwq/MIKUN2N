# Relay-heavy pair analysis: September 19, 2026

This extends the [IPv4 punch investigation](IPV4-PUNCH-ANALYSIS-20260918.md)
with server byte accounting and the available counterpart logs. Analysis only:
no runtime code, clients, or server configuration were changed.

Aliases are consistent with that investigation: A is the initiating client,
B the supplied archive's owner, and C the older IPv4-only peer. D is the peer
whose operator confirmed a rollback to an older n2n client after IPv6 problems;
E is another desktop peer. The private report maps these aliases to nicknames.

## Accounting and evidence coverage

The query covers September 16-18 in UTC+8, ending exclusively at September 19
00:00. Actual collection begins September 16 at 17:21:59, so that day is partial.
The current main-supernode database reports no sampling gaps over 180 seconds.
The separate federation instance has no equivalent collected history here.

Main-supernode egress totals 14.757 decimal GB: 14.402 GB unicast pSp, 0.312 GB
broadcast copies, and about 0.043 GB residual control/framing traffic. These are
server-accepted n3n bytes, not cloud-billed bytes or game payload measurements.
Direct IPv4/IPv6 traffic bypasses this accounting. Hour buckets cannot establish
the exact second a flow changed mode.

| Pair | Unicast pSp GB | Share of all unicast pSp | September 17 GB | September 18 GB |
|---|---:|---:|---:|---:|
| B-C | 7.270 | 50.48% | 4.386 | 1.209 |
| B-D | 4.402 | 30.57% | 2.680 | 1.590 |
| A-B | 2.319 | 16.10% | 2.314 | 0.000883 |
| B-E | 0.379 | 2.63% | 0.000103 | 0.379 |

These four pairs account for 99.78% of unicast pSp. A-C accounts for only
0.001889 GB over the same window; a peer label alone is not the unit of diagnosis.
C's next largest pair after B is another peer at 0.011407 GB. Broadcast is excluded
from these comparisons and must not be mistaken for failed direct connections.

New read-only receiver analysis covers C's 0.5.8-1 upload from September 18
21:14:00-23:26:16 and E's 0.5.8-1 upload from 13:00:35-15:00:31. Both stop near
64 MiB. B's native archive continues after its detailed recorder fills. Existing
September 16 extracts provide earlier C/A/B comparisons. The receiver inventory
contains no retained D-owned diagnostic session at this inspection; D's later
behavior is reconstructed from B's logs, accounting identities, and operator
confirmation, not a claimed analysis of an unavailable D-side trace.

## B-C: direct connection lost, then bounded recovery fails

This pair can establish IPv4 P2P. It was observed direct on September 16 around
20:19, September 17 around 18:34 and 21:14, and September 18 around 21:14. Native
events, endpoint changes and the available management rows support these events;
they do not establish uninterrupted direct operation between them.

The September 18 record provides a detailed failure sequence on C's own clock:

| Time | Evidence |
|---|---|
| 21:14:01 | C already reports B as IPv4 p2p. |
| 21:39:25-21:39:47 | `last_p2p` and `last_seen` keep advancing; sample ages are about one second or less. |
| 21:39:48-21:39:56 | Receive freshness stops advancing; its age grows to 9.6 seconds. |
| 21:39:58 | B becomes pSp with a newly allocated pending entry and the server-observed control endpoint. |
| 21:40:14-21:41:44 | Three coordinated bank rounds fail: 4,575 + 4,500 + 4,650 packets from C. |
| Through 23:26:16 | Later C samples retain failed/pSp status toward B. |

B independently shows application UDP acknowledgements from C until 21:39:46,
then unanswered probes, followed by recovery of the application exchange through
relay around 21:40:07. B's corresponding bank rounds send 5,250 + 4,725 + 4,725
counted packets and also exhaust round 3. The client clocks are not perfectly
synchronized; align transitions and pair generation rather than subtracting
cross-host timestamps as network latency.

Native `src/edge_utils.c:2652` deletes a known peer after no direct receipt for
`timeout / 2`; this peer reports `timeout=20`, so the observed roughly ten-second
cutoff matches the implementation. This is a real receive gap preceding expiry,
not evidence that the UI alone invented a disconnection. No manual forced relay
is set in the reviewed rows.

The previously successful B endpoint is 2,336 ports away from the control endpoint
used by C's replacement attempts. Both sides then select CONE. The three bank
rounds target control/near offsets and rotating bands only out to +/-640. Thus
the prior successful endpoint is not covered by these bank attempts. The existing
entry and its endpoint are discarded instead of being retained as a separately
validated recovery candidate. Whether that old mapping would still have worked
after the receive gap is **not** established by the logs.

The practical failure chain is therefore: receive interruption, destruction of the
known path, unsuccessful new probing, then persistent relay after the bounded
retry budget. The specific router/ISP/socket cause of the original interruption
remains unknown. An active adapter snapshot for C has no usable global IPv6;
IPv6 cannot currently provide an alternate direct path for that endpoint.

C-B unicast relay in September 18's 22:00 hour is 0.925 GB. Over the full accounting
window the pair has about 48.27 million relayed datagrams, averaging about 151
counted bytes each. The bulk cannot reasonably be described solely as an IPv6
oversize-packet symptom; this pair's prolonged IPv4 relay is central.

The empty-worker round from the earlier report is specifically observed on B-C
on September 17 at 18:33. The following round succeeds. That defect wastes an
attempt but does not, by itself, explain every later hour of relay traffic.

## B-D: separate IPv6 failures from the later old-client period

The operator confirms D returned to an older n2n client after the 0.5.8-1 IPv6
MTU/fallback problem. B's logs and the server's per-MAC accounting distinguish
the two behaviors:

- Earlier D identities have real IPv6 activity. One September 17 identity has
  454 readiness expiries, 90 path expiries, and 305 `data_oversize` log events in
  the archive. An event reports a 1,383-byte encapsulated packet exceeding a
  1,232-byte path budget, with `path_preserved=1` and per-packet IPv4 fallback.
  The event log is throttled; its count is neither lost packets nor relayed bytes.
- New identities first appear around September 17 21:27 and 23:19, and September
  18 21:57. For these three identities, B repeatedly receives an unusable IPv6
  candidate with no token and emits no IPv6 PING/PONG events for them.
- The three identities account for 1.920 + 0.364 + 1.568 = **3.852 GB**, or about
  **87.5% of B-D unicast relay**. This is consistent with the confirmed rollback.
  Exact executable versions for individual MAC identities are not independently
  established by a retained D-side log.

Consequently most B-D relay belongs to a period without the experimental IPv6
path being offered, while the older IPv6 implementation also has genuine earlier
fallback evidence. These must not be combined into one MTU-failure count. Native
NAT4 coordination additionally needs support from both ends; do not assume the
older n2n client implements the patched bank protocol. A socket/control candidate
and successful server registration alone do not establish direct reachability.

## B-E: a second direct-then-relay pattern with a missing counterpart window

E's September 18 trace initially exhausts attempts, then establishes IPv4 direct
to B at approximately 13:03:32. B corroborates success around 13:03:36. E keeps
reporting direct until its diagnostic file reaches the limit at 15:00:31.

B's continuing native log shows renewed punching at 15:19:55, followed by failures
and final abandonment at 15:21:34. The 15:00 hourly bucket contains 0.37757 GB of
B-E unicast relay, almost all of this pair's 0.37946 GB in the wider window.

The traffic and B-side failure sequence align, but E's recorder stopped before
the important event. There is no fresh counterpart evidence to identify the
original receive interruption. This is a concrete example for the requested
new-peer diagnostic-window/allowance follow-up; missing uploads must not be
treated as an offline client or as proof that the other endpoint caused failure.

## A-C and priorities for the next iteration

C's September 18 log shows successful IPv4 P2P to A around 23:06:45, with a worker
promoted after about five seconds. A's later new session around 23:21 produces
three failed attempts instead. This pair can succeed and later fail under a new
mapping; its accumulated relay bytes are nevertheless small compared with B-C.

Prioritize recovery of established IPv4 paths in the next authorized iteration:
capture the receive-gap cause, preserve previously working candidates for bounded
revalidation, coordinate the pair's remaining attempts, and reject empty rounds.
Revalidate port-model/coverage changes against actual worker mappings instead of
simply extending probing indefinitely. For D, distinguish updated-client IPv6
acceptance from interoperability with the older n2n client. Continue improving
paired diagnostic coverage under valid endpoint-specific consent.

These are recorded findings and proposals, not implemented fixes. Details and
source references are preserved in the ignored `heavy-pairs.local.md` report and
its adjacent JSONL extracts under `artifacts/analysis-20260918-ipv4/`. Raw addresses,
MACs, upload session identifiers, and private configuration are excluded here.
