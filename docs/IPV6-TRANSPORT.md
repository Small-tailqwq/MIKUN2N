# IPv6 transport, generation 3

> [English](IPV6-TRANSPORT.md) | [简体中文](IPV6-TRANSPORT.zh.md)

The 0.5.8-4 test build uses a bounded, two-level packetization-layer discovery
profile using [RFC 8899](https://www.rfc-editor.org/rfc/rfc8899.html) principles.
It separates each direction's confirmed UDP size from peer receive capacity,
path liveness and larger probes.

## Local address selection and interface recovery

The edge rescans local IPv6 addresses every ten seconds. Initial Windows selection
uses an up interface with an IPv6 gateway, a preferred address with sufficient
lifetime, address type and interface metric. A selected source is bound to one
IPv6 socket; STUN and peer checks determine whether to retain the binding.

The edge retains a still-eligible current binding unless existing connectivity
checks keep failing. A validated STUN reply or matching peer PONG clears that
failure window. Only unanswered/hard-failed STUN requests and eligible peers'
base checks count against the binding; larger MTU probes, transient queue errors,
absent observers, and known incompatible/offline peers do not. A new binding has
30 seconds to settle. Retrying another interface requires at least three failures
spanning 20 seconds, with a failure in the preceding 15 seconds.

On retry, the whole failed interface is temporarily excluded for 60 seconds so
its other temporary addresses cannot hide a usable Wi-Fi interface. A bounded
history tracks up to 16 recently failed interfaces. A current address that leaves
the eligible list can be replaced without waiting for the failure window. With
no eligible alternative, an existing candidate remains available for recovery;
if no local candidate remains, IPv4 continues. Enumeration failure preserves the
existing binding. A replacement socket must be configured and bound successfully
before the old socket is closed; failed replacements enter the same cooldown.

Rebinding clears old local mapping transactions, peer path/readiness proof,
outstanding probes and fragment assemblies, then refreshes peer candidates and
checks them with a new local session token. IPv4 paths remain available while
IPv6 is re-established. Ordinary logs record selection, interface and failure
reasons. A working replacement is retained even if the failed interface later
has a lower metric. Users manage network connections in Windows. Recovery selects
one address on another interface and leaves system routing unchanged.

## Size discovery

- The base UDP payload is 1232 bytes (1280 minus IPv6/UDP headers). Initial
  probes and keepalives use that size. `IPV6_DONTFRAG` must succeed before the
  transport starts; probes cannot silently pass through IP fragmentation. Failure
  disables the IPv6 transport and emits a warning even without detailed diagnostics.
  The Windows test build supports this socket option; other targets must provide it.
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

An edge allocates four assembly slots on the first valid fragment from a checked
peer, each bounded by the n3n packet buffer (2048 bytes), with a fixed two-second
deadline. The allocation is freed on session reset or peer destruction; the shared
peer structure contains only a pointer, so supernodes allocate no assembly buffers.
Allocation failures emit a rate-limited warning. Identical overlapping bytes are ignored;
conflicting overlap or inconsistent total length discards that assembly. A 1024-ID
sliding bitmap rejects completed/expired/conflicting assemblies. IDs older than the
window remain rejected even after bitmap reuse while fragments keep arriving;
high rates cannot reopen duplicate delivery. After two seconds without accepted
fragments the history expires, retaining generation-3 compatibility with older
senders that recreate a peer record and reset its IDs. Duplicates delayed beyond
that idle expiry can be delivered, as with the previous two-second cache. New
senders allocate IDs only for fragmented datagrams across the edge session, so
recreating a peer cannot reuse an ID and unfragmented traffic consumes no IDs.
An already admitted assembly keeps its original two-second deadline even when
later IDs advance the window. A first fragment arriving outside the 1024-ID window
cannot start an assembly; the ID distance is shared across all peers sent to by
the source edge. Invalid ranges,
unknown sessions, wrong endpoints and excess concurrent assemblies are rejected.
Fragment loss drops the original UDP datagram; there is no reliability/retry layer.
A partially sent datagram is never replayed simultaneously through IPv4. With
independent fragment loss probability p, a datagram split into two fragments
succeeds with probability (1-p)^2; correlated loss can differ.

Native summaries expose sent/received fragments, completed assemblies and transient
drops. Individual events report path expiry and send errors. Obsolete `oversize_fallback_*` counters were
removed in 0.5.8-3; inspect fragment counts and actual path/send-failure events.

`get_edges` retains `ipv6_checked_udp_bytes` (the confirmed outbound UDP limit,
zero while inactive) and `ipv6_peer_rx_udp_bytes` (the peer's advertised receive
ceiling). The redundant `ipv6_tx_udp_bytes` alias and generation-derived
`ipv6_fragmentation` flag were removed. The receive ceiling is not a measurement
of the reverse path. Obtain the reverse outbound limit from the other endpoint's
row. These fields are intended for local API consumers and consented raw
`management_edges` diagnostic records; the friend UI does not display them.

## Compatibility and validation

Both clients need generation 3 for IPv6 testing. Generation 1/2 and ordinary n2n
clients retain IPv4 compatibility; a build-number mismatch alone is allowed.
The existing supernode forwards candidates/identity and needs no wire change.
IPv4 reachability to the supernode is still required. NAT66 discovery remains
enabled in the private profile.

`tools/tests-ipv6.c` in the corresponding native source intercepts all sends and
checks asymmetric sizes, loss, reorder, duplicates, expiry, bounded buffers,
session/endpoint rejection and local send errors without opening sockets. Build
the native tree first; `make` now builds this check alongside `tests-wire`. Run
both executables. The source archive contains both checks.

Client/receiver offline checks:

```powershell
dotnet run --project tools/offline-tests/OfflineTests.csproj -- "$env:TEMP/MikuN2N-offline-check"
python tools/offline-tests/test_server.py artifacts/offline-server
```

The 0.5.8-4 checks also cover 1200 unfragmented sends without ID consumption,
an admitted assembly surviving an ID jump until its deadline, real 64 MiB log
rotation, exact uploaded bytes/record/chunk sequences, retained chunks after 507,
and manual resume/revocation during capacity backoff.

Live validation covers public IPv6/NAT66, Windows adapter selection during
router failure or Wi-Fi switching, both WPF themes, and sustained
game traffic. Record interruptions and recovery during interface switches.

## Read-only local network status

Local maintenance builds based on 0.5.8-10 add optional `get_nat` fields:

| Field | Values |
| --- | --- |
| `ipv6_status` | `disabled`, `checking`, `unverified`, `available`, `unavailable` |
| `ipv6_address_kind` | `none`, `global`, `ula` |
| `ipv6_mapping` | `unknown`, `untranslated`, `port-translated`, `address-translated` |

The existing `ipv6_nat66_supported` flag is a capability, never evidence of
translation on this network. `available` requires a successful source-bound
round trip within 30 seconds. Mapping classification additionally requires a
fresh accepted observer reply on the same bound socket. It compares the
reported endpoint against the bound endpoint; address translation may be
NAT66 or NPTv6, which these observations cannot distinguish. This does not
prove unsolicited inbound reachability or connectivity to every peer.

These getters neither probe nor enumerate interfaces. The client reads them
through the existing four-second NAT polling and discards reports older than
12 seconds. No addresses are added to this response. Missing fields from older
runtimes remain unverified/unsupported; they are not a network failure.
