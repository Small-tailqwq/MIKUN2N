# Runtime Change: unversioned — Early punching and later unversioned coordination changes

Version: unversioned

> [English](unversioned.md) | [简体中文](unversioned.zh.md)

## Problem

The original entries cover Windows punching, management reliability, federation, resource diagnostics and rapidly changing NAT mappings without assigning release labels.

## Recorded changes

<a id="patch-1"></a>
### Patch 1

Enable the register_pkt_ttl symmetric-NAT punching path on Windows, and ignore WSAENETRESET caused by punching TTL probes.

<a id="patch-2"></a>
### Patch 2

Use the n3n data socket to probe mappings and cross-port filtering through UDP observers on ports 21001/21002 on the supernode host. Report NAT1/2, NAT3 and NAT4 through the get_nat management method.

<a id="patch-3"></a>
### Patch 3

Give ordinary n3n direct connectivity a five-second priority window. If the path remains pSp, use layered spraying with a 25-second window, 250 ms slices and a hard 12,000-packet cap. A local NAT4 endpoint uses the cross-address cone escape validated in v7.3.1: keep sending to control, cover control±1..64 in four slices, and sweep a 192-port band in both directions each round, reaching control±4096 in about 22 rounds. Retain relay after the budget is exhausted.

<a id="patch-4"></a>
### Patch 4

Fix Windows nonblocking management connections treating WSAEWOULDBLOCK as fatal, so get_nat/get_edges state can reliably reach the main window.

<a id="patch-5"></a>
### Patch 5

Fix NAT-probe response-source validation comparing IPv4 array addresses instead of their contents, so the data socket recognizes replies from ports 21001/21002.

<a id="patch-34"></a>
### Patch 34

Support multi-supernode federation with automatic node selection for relay. On edges, expand punching exclusions from curr_sn to every federation node in conf.supernodes so forced-relay keepalives and Tier 1 scans do not mistake a second supernode for a peer. Accept NAT-probe responses from ports 21001/21002 on any known federation node to avoid drops during RTT re-anchoring races. When QUERY_PEER is forwarded through the federation to the peer's anchored supernode (from_supernode with no local source_edge), trust its NAT summary and bank-model fields, already validated against the originating node's local registry, and calculate complementary-role plans of the same generation. Each anchored supernode independently issues dual-NAT4 GO deadlines across federation; skew is bounded by one coordination interval and tolerated by attempt-driven scanning. The client generates multiple supernode= lines, enables supernode_selection=rtt for multiple nodes, and displays the current anchor and latency.

<a id="patch-35"></a>
### Patch 35

Save WSAGetLastError immediately after a Windows sendto failure so later logging calls cannot overwrite it. On WSAENOBUFS (10055), also log socket, packet length, process handles, memory load and available physical memory, known/pending peers, active NAT4 workers, and cumulative P2P/supernode send counts. Long-term comparison can then distinguish process-resource leaks, punching bursts and transient system network-queue exhaustion.

<a id="patch-36"></a>
### Patch 36

Measure allocation rate across rounds and fall back quickly for highly dynamic NATs. Bank fitting measures circular port distance over time across rounds, taking the best pairing for dual banks. Field data from natpunch v7.2 showed mobile CGNAT allocation rates varying between 150 and 232 ports/s per round, windows drifting by thousands of ports during a seven-second spray, and all 17 fast-target rounds missing. A fast single bank cannot be fitted as a trackable model, so rates >=120 ports/s classify as HARD/unpredictable: the scanner covers a wide window once and relays after exhaustion instead of chasing phase. Insufficient calibration samples inherit the previous VOLATILE model instead of degrading to HARD. Retain a previous VOLATILE classification when no cone is observed, preventing strategy oscillation when CGNAT load balancing briefly collapses into one bank. Tier 1 NAT3-to-NAT4 layered/cone slices shrink from 250 to 100 ms, reducing full ±4096 coverage from 15.75 to about 6.3 seconds; the 12,000-packet budget and 25-second window remain. Bank low/mid near lanes advance deterministically by tick instead of sparse hashing, prioritizing the nearest, most likely ports before exhaustion.

<a id="patch-37"></a>
### Patch 37

Keep the endpoints synchronized while calibration reports wait. A model ages in REPORTED while waiting for a peer; a peer calibrating several seconds later can leave it more than seven seconds old at GO, outside the SYMMETRIC predicted band's drift coverage. Once model age exceeds five seconds, the edge recalibrates in place, reusing workers, rotating the nonce and clearing the peer-model cache, at most twice, keeping GO model age within five seconds. The supernode report TTL shrinks from 15 to six seconds and stale reports cannot generate GO. Single-supernode alignment already uses a 750 ms query interval plus a 3000 ms GO delay: both go_at values converge on one deadline plus each side's RTT/2, with under 50 ms skew, and is unchanged. Federation's roughly 400 ms skew from independently chosen deadlines is tolerated by attempt-driven scanning without a protocol change.
