# Peer-assisted transport

> [English](PEER-ASSIST.md) | [简体中文](PEER-ASSIST.zh.md)

The first implementation in 0.5.8-8 lets a requester T choose one consenting
friend R to forward its unicast traffic to a specific peer L. Each T/L pair has
its own choice. R can accept separate requests from different friends.

## Using assisted forwarding

Open **Choose assisted forwarding** from the target friend's context menu (the
Chinese UI uses **选择协助转发…**). Candidate queries run while this window is open.
R receives a pending-request button, without a modal dialog or focus change.
Closing the window cancels an outstanding outgoing application, but does not
stop an already established session. Any participant can end a session. Consent
is limited to the current connection; it is not persisted across reconnects.
Quality, applications and active sessions appear in a secondary window. The main
window shows the assisted connection mode and pending requests.

## Quality reports

All current clients answer same-group queries for their recent quality to one
named peer. Reports are unicast and on demand. T ranks candidates locally, using
the existing supernode without an upgrade.

The existing two-second UDP hello/ack cadence feeds a 60-second window. Reports
contain completed sample and timeout counts, mean and 95th-percentile RTT, mean
absolute change between successive successful RTTs, route mode and sample age.
A probe is counted as unanswered only after its ten-second deadline; pending
probes and ICMP fallback measurements are excluded. Changing the measured route
or peer generation clears the window. Ages use local monotonic time; a received
report conservatively includes the query's round-trip time.

**Loss means unanswered probe round trips, not one-way loss or game-packet loss.**
Reports older than 15 seconds are stale;
fewer than five completed samples are identified as insufficient evidence.
Eligible candidates require native direct-path information for both legs. Among
eligible candidates, sufficient evidence is preferred, then estimated combined
probe loss and summed RTT. The loss estimate assumes independent leg losses and
is only a ranking aid. The user makes the actual choice.

## Consent and switching

Control messages use the existing virtual-network discovery channel, bound to
known senders and the T/R/L tuple. R explicitly approves each application.
R installs a relay circuit, prepares receive-only circuits on T and L, and waits
for both acknowledgements before committing their send routes. Retries are
idempotent. Failed or cancelled setup removes the circuit; recently closed IDs
are retained briefly to reject delayed setup messages.

Native support is advertised through `get_info.peer_assist_version = 1`.
`set_peer_assist` takes `[circuit, role, first_mac, second_mac, enabled]`, where
`circuit` is a 32-digit hexadecimal ID. For `relay`, the MACs identify T and L;
for `standby` or `endpoint`, they identify the remote endpoint and R. `standby`
accepts and probes the route but never selects it for outgoing game packets.
`get_peer_assists` reports the installed role, actual readiness and packet counts.

Native configuration expires after 30 seconds without renewal. R sends control
heartbeats every three seconds and requires both endpoint replies. Endpoints
renew only after R's heartbeat. A 15-second control silence ends the session.
Independent native end-to-end probes through R maintain a six-second data-path
readiness lease. An unready route falls back to existing transport selection;
the chosen helper is not silently replaced with another player.

## Data path

The native edge wraps the original n3n packet in a 44-byte assistance envelope.
Each hop uses an existing checked IPv6 or established IPv4 direct path, including
the winning NAT worker socket. R never falls back through a supernode or another
assistance circuit, and never writes transit packets to its TAP device. Source,
destination, circuit, immediate sender and hop flag are checked. A bounded
sequence window suppresses duplicate assisted datagrams. Endpoints retain normal
n3n community, header, timestamp and payload decoding checks, and never learn R's
socket as the original sender's direct address.

IPv6 legs reuse the existing size discovery and fragmentation machinery, with
the envelope included in the size calculation. IPv4 legs retain the existing
IPv4 packetization behavior. Inner packets too large for the native packet buffer
plus the envelope use the normal path. Each datagram uses one selected path,
without transport-level retransmission. A working original IPv4 path is kept warm.
Manual forced-supernode policy takes precedence. Broadcast discovery remains on
the existing path, and the virtual IP, MAC and game sockets do not change.

R forwards both directions, consuming upload as well as download capacity. The
first version deliberately has no user bandwidth limits or automatic capacity
reservation. R can decline or end individual sessions. Existing shared-community
encryption allows a helper who knows the group key to decrypt the traffic.

## Diagnosing a failed assisted path

The native edge writes `MikuN2N assistdiag` and `MikuN2N v6peer` summaries to
the ordinary local log, without requiring the optional detailed IPv6 trace.
Changed counters are sampled at roughly ten-second intervals; lifecycle records
preserve counters before circuit removal or peer/path resets. No game payloads or
session secrets are logged. The existing diagnostic recording and upload consent
also cover these records.

`assistdiag` identifies the local MAC, circuit, role, creation time, monotonic
sample time, and original source/destination MACs. Both directions use the same
source/destination identities at T, R and L. DATA counters are separate from
PING/PONG counters. The stages are send attempt, UDP submission, envelope receive,
envelope validation, inner n3n acceptance, and TAP write. `tx_consumed_drop`
identifies an existing transport outcome that consumes a packet even though not
all UDP fragments were submitted. Rejection counters distinguish endpoint/hop,
readiness, sequence, inner-header/community/identity, transform, filter and TAP
failures. An endpoint's `tx_failed` can fall back to the ordinary route; a relay's
failed forwarding does not have that fallback. The payload counter covers
unsupported compression types.

`get_peer_assists` exposes the same cumulative directional counters under
`diagnostics_version = 1`. While local assistance circuits exist, the client queries
the management API and records `management_peer_assists` snapshots with
circuit-to-participant mappings about every 10–12 seconds and on observed state
changes. Failed queries follow the same recording cadence, with `success=false`,
an error type and null rows, rather than an empty successful table. Maintenance
iterations that start with no local circuits skip the query and snapshot.
Snapshots are written through the session's diagnostic recorder when available.
Size-limited snapshots explicitly
mark truncation. `peer_assist_control` / `MikuN2N assistctl` records connect setup,
native installation, readiness changes, expiry and removal with the same circuit.

IPv6 counters belong to the immediate outer peer and cover all traffic on that
leg, including R's own traffic. They distinguish datagrams, UDP fragments,
successful submission, incomplete submission, completed reassembly, slot
occupancy/high-water marks, slot-full drops, expired assemblies and missing bytes,
duplicates, conflicting overlaps and retired/window rejection. The four-slot,
two-second policy is unchanged. Expiry remains lazy: an expired slot can still be
allocated until the next fragment triggers cleanup; summaries distinguish this
from unexpired occupancy. Resets record unfinished assemblies before clearing them.
IPv6 `gen`, `path_gen` and `seq` are local identities; fragment IDs belong to the
sender's session across all its peers. Unfragmented IPv6 DATA has no fragment ID,
and gaps in the per-peer ID range do not establish loss. `delivered` means IPv6
unwrap returned a complete datagram, before assistance/n3n checks and TAP writes.
Identical duplicates, accepted overlaps and waiting for another fragment are not
themselves drops. Short/unknown-circuit packets cannot be assigned to an assist
session, and malformed IPv6 headers are assigned only when a known endpoint can
be identified; unassigned traffic is not included in per-session totals.

To diagnose a timeout, retain the complete T/R/L logs from before assistance
setup through the timeout and shutdown, plus the MC server's disconnect reason.
Compare one circuit and one direction at a time:

1. Compare T's submitted DATA with R's received/validated DATA, then R's submitted
   DATA with L's received/validated DATA. Repeat in the reverse direction.
2. At a missing receive stage, compare the immediate peers' IPv6 fragment counters
   and reassembly rejection reasons. Increasing slot-full drops are direct
   evidence of local slot exhaustion.
3. If the endpoint validated the envelope, follow inner acceptance and TAP writes
   to distinguish a native processing drop from a gap before the envelope arrived.

UDP submission counts datagrams accepted by the local socket; check the next
host's receive counters to follow delivery. Use game logs to investigate what
happens after TAP writes. Compare complete, overlapping windows by circuit,
direction and sequence because clocks across hosts are not synchronized.
In-flight data, duplicates, resets, omitted logs and concurrent counter updates
affect counter deltas; compare the stages alongside their rejection reasons.
Sequence high-water marks identify the highest observed sequence; individual
gaps require packet-level records.

## Live validation

Game acceptance uses three participating machines with matching clients. Test
world loading, route switching, helper departure/revocation and recovery,
including R's own game and both UI themes. Record actual paths, sustained
interruptions and server-relay traffic. These runs also assess bandwidth and
large-datagram behavior beyond the small quality probes.
