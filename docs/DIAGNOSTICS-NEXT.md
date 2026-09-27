# Adaptive diagnostics: capacity assessment and next-release plan

> [English](DIAGNOSTICS-NEXT.md) | [简体中文](DIAGNOSTICS-NEXT.zh.md)

Status: proposed on September 19, 2026. This document and the corresponding TODO
describe future work. No logging policy, receiver limit, upload protocol, retention
setting or running service was changed during this assessment.

## Measured capacity

A read-only snapshot of the receiver filesystem around 00:39 UTC+8 gives:

| Measure | Observed value |
|---|---:|
| Filesystem total | 39.01 GiB |
| Available space | 9.37 GiB |
| Retained diagnostic file contents | 0.379 GiB |
| Diagnostic allocated disk space | 0.459 GiB |
| Diagnostic files / numbered upload chunks | 40,311 / 40,302 |
| Free filesystem inodes | About 1.19 million |
| Active receiver limits | 64 MiB per segment; 2 GiB total; 24-hour expiry |

The retained nine upload segments belong to four client nicknames. The traffic
database has ten historical registration labels, including a server-side edge;
labels are neither a current concurrent-user count nor verified people. Old-client
size caps also truncate the available histories. Consequently, 0.379 GiB
is a storage snapshot, not an unconstrained daily demand estimate.

Observed raw diagnostic generation ranges from about 23 to 51 MiB per client
hour across these segments; an earlier supplied capped session reaches about
53 MiB/hour. Several are short sessions rather than full-day steady workloads.
Use 25-55 MiB/hour for the following planning scenarios, not a guaranteed bound.

| Scenario | Raw diagnostic data/day | Compressed data/day at a conservative 15% ratio |
|---|---:|---:|
| 4 clients, 8 hours each | 0.78-1.72 GiB | 0.12-0.26 GiB |
| 10 clients, 8 hours each | 1.95-4.30 GiB | 0.29-0.64 GiB |
| 10 clients, continuously online | 5.86-12.89 GiB | 0.88-1.93 GiB |

Formula: clients * hours * MiB/hour / 1024. Compression projections assume that
the receiver stores the compressed representation; wire compression alone does
not reduce its on-disk NDJSON. The table excludes filesystem/index overhead,
other services' growth, retransmissions and additional future instrumentation.

A bounded sample of about 16.83 MiB, taken after the first five minutes of each
available segment, compresses to about 1.83 MiB using gzip level 3 in independent
32 KiB blocks: **10.89% of the original bytes**. Per-segment samples range from
about 9.7% to 11.8%. This is an offline size measurement of existing records,
not a measured production network saving or a new-client throughput benchmark.
The 15% scenario allows some variation but is not a worst-case compression bound.

At the current filesystem occupancy, a **4 GiB actual allocated-storage budget**
for diagnostics would leave about 5.83 GiB available if filled, assuming other
usage stays unchanged. A separate **5 GiB minimum free-space threshold** should
take precedence as other services grow. Retain the current 24-hour expiry; do not
promise complete 24-hour coverage if eviction or missing uploads occur. Current
expiry is measured from a segment's first receipt, not separately from each row.

This supports a moderate quota increase together with compression and batching.
Uncompressed logging by ten continuously active clients can exceed current free
space in a day; unlimited uploads/storage are not supported by these estimates.
No quota increase has been deployed.

## Current sources of overhead

Byte accounting across the retained NDJSON gives:

| Kind | Share of bytes |
|---|---:|
| Connection snapshots | 47.26% |
| Native peer-table snapshots | 25.52% |
| Raw edge/latency output | 18.83% |
| Network interface inventories | 3.88% |
| Supernode snapshots | 3.05% |
| NAT snapshots | 1.44% |

Snapshot records contain changing timestamps, ages, counters and latency values;
ordinary byte-for-byte deduplication will not remove their repetition. Preserve
meaningful changes and summarize counters rather than comparing complete JSON.
The byte shares identify an optimization target, not a guaranteed deletion ratio.

`EdgeController` logs peer management rows every approximately 1.5 seconds, NAT
rows about every four seconds, interface inventories every 30 seconds, and a full
connection snapshot whenever it publishes state. `AppendLog` also copies native
text into both the session log and diagnostic JSONL. Detailed native probe text
and application latency text account for most `edge_log` volume.

`TestDiagnosticsSession` posts currently available complete lines, up to 128 KiB,
then usually waits 250 ms; it does not wait to fill a target batch. Newer observed
sessions average roughly 5-8 KiB per request. About 37% of retained chunks are
smaller than 4 KiB. Physical allocation is about 21% above file contents in this
snapshot; reducing tiny chunks will help disk space, inodes and request overhead.

The receiver explicitly sends `Connection: close` and handles a TLS connection
per request. It currently rejects `Content-Encoding`. Compression and bounded
connection reuse therefore require coordinated receiver/client changes. They
cannot be enabled by changing the client header alone.

Current raw body generation is roughly 6.5-14.5 KiB/s per client, before request,
TLS and retry overhead. The existing 250 ms delay is not a byte-rate limiter:
large backlogs can send much faster than steady generation, and final flushing
skips that delay. The upload body is server ingress; responses and TLS exchanges
consume server egress. Do not treat uploaded body bytes as cloud-billed egress.

## Capture according to connection state

Capture diagnostic events once, then give separate projections to the retained
local log and the upload outbox. Neither selection should block the packet path
or wait for disk flushes or HTTP responses. Bounded queues need explicit overflow
counters and a preserved critical-event reserve.

Stable operation should emit semantic changes plus a proposed 30-second health
summary. Keep counters/deltas, successful and failed probe totals, RTT summaries,
maximum receive gaps, last receive age, packet-size/fragment outcomes, path and
NAT identity, registration health, and data/relay selection. Send periodic full
checkpoints, for example every five minutes, so state can be reconstructed even
if an earlier segment is unavailable. Inventory changes emit immediately; an
unchanged inventory need not be repeated every 30 seconds.

Open a detailed incident window for new-peer attempts, actual peer disconnects,
reconnection, manual/automatic retry, direct-to-relay changes, path recovery,
mapping/MTU changes, coordination expiry, empty rounds, rejected packets, process
failure, and upload failure. Include a proposed two minutes of preceding consented
context and five minutes after the trigger. Merge overlapping windows, reference
already emitted context and assign one incident ID across the related retry
sequence. Continuous faults retain transitions and periodic detailed samples
without allowing each repeated warning to recreate the same entire window.

Preserve both sides' connection/run/peer/attempt identifiers, monotonic timing,
local worker endpoint and observed mappings, target endpoint, send outcome,
receive/ACK outcome, retry eligibility and explicit expiry/fallback reason. This
addresses the lost candidate, mismatched rounds, zero-worker attempt and IPv6
size/readiness cases already found. Keep unfamiliar warnings and bounded raw
context for issues the current schema does not anticipate; a known-error-only
whitelist would undermine future investigation.

Detailed tracing cannot guarantee complete packet-level attribution of every
future fault. A hard process crash may prevent its final record; periodic progress
checkpoints and explicit missing-tail indicators must distinguish that uncertainty
from a clean disconnect. No packet contents or credentials are added to the data.

## Upload priority, limits and recovery

Event exemption means that exhausting a **routine allowance** does not stop a
connect/disconnect/retry/fallback record or its bounded incident context. It does
not mean ignoring physical disk exhaustion or sending unlimited bytes. Keep a
reserved critical queue; routine backlog must not head-of-line block terminal
or failure metadata. Retain logical event sequence, original event time and
incident linkage even if urgent delivery overtakes an older routine batch.

Proposed tuning points to verify before release:

- Stable payload averages at most 1 KiB/s per client after compaction/compression.
  Incident detail/catch-up is paced to at most 8 KiB/s, with a bounded small burst
  allowance. Retries share the same limiter. These are payload targets, not a
  guarantee of whole-connection bandwidth or game latency on every network.
- Batch stable data up to about 64 KiB decoded or 15 seconds, whichever comes
  first. Urgent incident headers should become eligible within about one second;
  detail follows within the bandwidth budget. Preserve the bounded exit flush.
- Compress batches and store them compressed. Retain a strict decoded-size limit,
  chunk identity, record boundaries and content verification. Adjust the existing
  SSH reader to read compressed and old plain chunks transparently.
- Reuse HTTP/TLS connections with bounded idle times and receiver concurrency.
  Check protocol support explicitly and preserve the legacy uncompressed route;
  fallback must still respect the byte budget and current authorization.
- When storage approaches the proposed 4 GiB allocation cap or 5 GiB free-space
  floor, evict/compress expendable old routine detail before incident context,
  account for missing ranges, and report pressure. A single noisy endpoint must
  not consume every endpoint's incident reserve.

The current 64 MiB boundary should remain a manageable rolling-segment boundary,
not a connection-lifetime stop. `BatchId` remains the receiver namespace, not a
numeric quota to bypass. Preserve idempotence, conflict/expiry recovery, the
507 backoff, revocation during a pending request, and bounded temporary storage.
Automatic incident recording must not repeatedly invoke manual resume, which
currently starts a new consent-scoped segment and can discard prior pending work.

Endpoint-specific consent still gates all uploading. Pre-event upload context
must contain only records collected within the current valid authorization and
connection; do not include previously refused/revoked records or automatically
replay earlier sessions. The update notice below is not permission to upload.

## Local detail setting and update notice

Provide a local-only setting such as `保留本地详细日志`. Preserve the current test
build's detailed behavior on upgrade, explain it in the next-release notice, and
let the user switch to a basic retained log. Basic logs keep lifecycle, transport
transitions, failures, retry outcomes, summaries and correlation IDs, while
omitting repetitive detailed samples. Either selection uses the same upload
event policy and the same consent checks.

The temporary bounded upload outbox is separate from user-retained local logs.
Describe its purpose and limit explicitly: disabling retained detailed logs does
not imply that authorized pending delivery never uses temporary disk storage.
Do not make upload availability depend on whether a detailed local archive exists.

Current local defaults are 30 days, with choices of 7/30/90/180 days or permanent.
There is also a four-segment, 256 MiB cap per diagnostic connection; that cap is
not a global cap over all old sessions or ordinary native logs. The supplied
native text logs add approximately 2-10 MiB/hour in active longer sessions.

For context, retaining uncompressed detail at 25-55 MiB/hour plus 2-10 MiB/hour
of native text for eight hours/day would produce roughly 0.21-0.51 GiB/day,
or **6.3-15.2 GiB over 30 days** without intervening size eviction. This is an
uncapped planning scenario, not a claim about current bounded diagnostic storage.
Compressing closed local files can reduce this, but the UI should estimate size
from the user's actual recent rate rather than promise a fixed number.

Show the update explanation once per diagnostic-policy revision. Include what
changed, the distinction between local detail and upload, current retention, a
size estimate, and direct access to those settings. Do not silently alter the
retention period or reset an existing choice. An information notice does not
replace the existing upload consent dialog; materially expanded collection scope
would need a fresh explanation and applicable permission.

## Implementation acceptance

The pending task in [TODO.md](../TODO.md) owns this work. Before release, use the
existing incident traces to check that compaction retains the evidence needed for
previous IPv4/IPv6 failures, and compare upload projections with local detail on
and off. Check quota-exhausted new-peer/disconnect/retry windows, priority under
backlog, compression/idempotence, 507 waits, revocation, disk pressure, restart
boundaries, rate limits and incomplete final flushes. Cold checks cannot establish
real gameplay latency; any live/network/UI checks remain separately authorized.

Current assessment artifacts are the ignored `diagnostic-capacity.local.md` and
`diagnostic-capacity-report.local.md` under the September 18 analysis directory.
Only file-content sizes and existing logs were measured; no load test was run.
