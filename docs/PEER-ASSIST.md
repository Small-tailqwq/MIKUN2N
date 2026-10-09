# Peer-assisted transport

> [English](PEER-ASSIST.md) | [简体中文](PEER-ASSIST.zh.md)

The first implementation in 0.5.8-8 lets a requester T choose one consenting
friend R to forward its unicast traffic to a specific peer L. Each T/L pair has
its own choice. R can accept separate requests from different friends. There is
no group-wide relay election and no bandwidth-quota setting.

## Implementation plan and boundaries

1. Reuse existing low-frequency discovery probes for recent link quality.
2. Let T query R's quality to L and rank candidates locally.
3. Require R's approval for each T/L session, then prepare all three participants
   before enabling the endpoints' send routes.
4. Keep quality, applications and active sessions in a secondary window; expose
   only the actual assisted connection mode and a pending-request indicator in
   the main window.
5. Build the client and native edge, run the existing applicable offline checks,
   and distribute the matching native source archive. Theme interaction and
   three-machine game acceptance remain separate live checks.

Open **Choose assisted forwarding** from the target friend's context menu (the
Chinese UI uses **选择协助转发…**). Candidate queries run while this window is open.
R receives a pending-request button, without a modal dialog or focus change.
Closing the window cancels an outstanding outgoing application, but does not
stop an already established session. Any participant can end a session. Consent
is limited to the current connection; it is not persisted across reconnects.

## Quality reports

All current clients answer same-group queries for their recent quality to one
named peer. Reports are unicast and on demand; there is no broadcast quality
matrix or server-side election. No supernode upgrade is needed for this feature.

The existing two-second UDP hello/ack cadence feeds a 60-second window. Reports
contain completed sample and timeout counts, mean and 95th-percentile RTT, mean
absolute change between successive successful RTTs, route mode and sample age.
A probe is counted as unanswered only after its ten-second deadline; pending
probes and ICMP fallback measurements are excluded. Changing the measured route
or peer generation clears the window. Ages use local monotonic time; a received
report conservatively includes the query's round-trip time.

**Loss means unanswered probe round trips, not one-way loss or game-packet loss.**
The probes do not establish available bandwidth, large-datagram reliability or
performance under world-loading traffic. Reports older than 15 seconds are stale;
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
plus the envelope use the normal path. No reliability/retransmission layer or
simultaneous packet striping is added. A working original IPv4 path is kept warm.
Manual forced-supernode policy takes precedence. Broadcast discovery remains on
the existing path, and the virtual IP, MAC and game sockets do not change.

R forwards both directions, consuming upload as well as download capacity. The
first version deliberately has no user bandwidth limits or automatic capacity
reservation. R can decline or end individual sessions. Existing shared-community
encryption does not provide secrecy from a helper who knows the group key.

## Acceptance boundary

Compilation and existing offline regressions cannot establish that T's ISP path
improves, that Minecraft stays connected during a switch, or that R's own game is
unaffected. Live acceptance needs three consenting machines, matching clients,
both UI themes, world loading, helper departure/revocation, and recovery. Record
the actual paths, sustained interruptions and server-relay traffic, not just the
candidate RTT estimates. No live acceptance is claimed by this document.
