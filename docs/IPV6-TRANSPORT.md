# IPv6 transport, generation 3

The 0.5.8-2 test build uses a bounded, two-level packetization-layer discovery
profile based on [RFC 8899](https://www.rfc-editor.org/rfc/rfc8899.html). This is
not a full general-purpose DPLPMTUD state machine. It separates each direction's
confirmed UDP size from peer receive capacity, path liveness and larger probes.

## Size discovery

- The base UDP payload is 1232 bytes (1280 minus IPv6/UDP headers). Initial
  probes and keepalives use that size. `IPV6_DONTFRAG` must succeed before the
  transport starts; probes cannot silently pass through IP fragmentation.
- A 47-byte PONG acknowledges the exact probe size, challenge and source
  endpoint, session and wire generation. Its short size avoids imposing the
  reverse direction's MTU on the measured forward direction. Maximum accepted
  response age is 6500 ms. Peers advertise a 1452-byte receive ceiling.
- Once the base path is checked, the edge probes a larger size every ten
  seconds: `min(1452, TAP MTU + 128, peer receive ceiling)`. With the default
  1290-byte TAP MTU this is 1418 bytes. An incoming small PING or a successful
  base PONG never erases a previously confirmed larger outbound size.
- Three unanswered larger probes or a local message-too-large error return
  packetization to 1232 bytes and postpone the next larger search for 60 seconds.
  Base keepalives continue independently; the usable IPv6 path remains valid.
- Both endpoints must report receive readiness before DATA selection. A genuine
  readiness/path expiry or hard socket failure still uses the IPv4 path. Temporary
  local queue pressure is ordinary UDP loss and does not switch the transport.
- A changed rendezvous candidate in the same session preserves a checked mapping.
  Expiry triggers candidate rechecking; a new session token clears all path proof.
  A newly selected mapping also clears outstanding old-mapping probes/reassembly.

This fixes the observed 0.5.8-1 pattern where an incoming 1232-byte probe undid a
1418-byte proof and 1383-byte wrapped DATA repeatedly fell through to IPv4 relay.
Size discovery is independent in A-to-B and B-to-A; their limits may differ.

## Tunnel fragmentation

DATA that exceeds the proven UDP limit is split inside the IPv6 tunnel. Each
fragment uses the existing session header plus a 64-bit datagram ID, 16-bit total
length and 16-bit offset (45-byte header). Reassembly precedes normal n3n decoding;
the inner encrypted n3n bytes are unchanged. No application payload is logged.

Each peer has four assembly slots, each bounded by the n3n packet buffer (2048
bytes), with a fixed two-second deadline. Identical overlapping bytes are ignored;
conflicting overlap or inconsistent total length discards that assembly. The most
recent 64 completed IDs are suppressed for up to two seconds. Invalid ranges,
unknown sessions, wrong endpoints and excess concurrent assemblies are rejected.
Fragment loss drops the original UDP datagram; there is no reliability/retry layer.
A partially sent datagram is never replayed simultaneously through IPv4.

Native summaries expose sent/received fragments, completed assemblies, transient
drops, path expiry and send errors. `get_edges` exposes `ipv6_tx_udp_bytes`,
`ipv6_peer_rx_udp_bytes` and `ipv6_fragmentation` alongside the native build and
wire generation. These are transport limits, not an instruction to change a
router's IPv6 MTU or the peer's TAP setting.

## Compatibility and validation

Both clients need generation 3 for IPv6 testing. Generation 1/2 and ordinary n2n
clients retain IPv4 compatibility; a build-number mismatch alone is allowed.
The existing supernode forwards candidates/identity and needs no wire change.
IPv4 reachability to the supernode is still required. NAT66 discovery remains
enabled in the private profile.

`tools/tests-ipv6.c` in the corresponding native source intercepts all sends and
checks asymmetric sizes, loss, reorder, duplicates, expiry, bounded buffers,
session/endpoint rejection and local send errors without opening sockets. Build
the native tree first, then compile it with the same includes and static libraries
as `tests-wire` (on Windows add `-lnetapi32 -lws2_32 -liphlpapi`). Run the native
wire check too. The source archive contains both checks.

Client/receiver offline checks:

```powershell
dotnet run --project tools/offline-tests/OfflineTests.csproj -- artifacts/offline-logs
python tools/offline-tests/test_server.py artifacts/offline-server
```

These checks passed for 0.5.8-2. They do not establish real public-IPv6/NAT66
behavior, both WPF themes, sustained game traffic or a reduction in the cloud bill.
