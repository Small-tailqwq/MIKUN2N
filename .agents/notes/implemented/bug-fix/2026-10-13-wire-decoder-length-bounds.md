# Agent Note: Bound wire-supplied lengths in the n3n wire decoders

Status: implemented

> [English](2026-10-13-wire-decoder-length-bounds.md) | [简体中文](2026-10-13-wire-decoder-length-bounds.zh.md)

## Problem

The n3n wire decoders used a length taken from the packet as a copy size without
consulting the destination. `decode_buf` checked only that the source still had
enough bytes, so `auth.token_size`, a wire-supplied `uint16`, filled the fixed
48-byte `auth.token` field. One unauthenticated UDP datagram could overwrite
roughly two kilobytes of the `process_udp` stack; the deployed supernode was
killed by exactly that input, and glibc's stack-smashing check stopped the
process; without that check the same write would have corrupted the stack
silently.

Checking lengths inside the decoder leaves two paths open. `process_udp`
ignores the decoder's return value on these paths, so a rejected datagram still
reaches the forwarding code, and the encoder reads `auth.token_size` bytes back
out of the same 48-byte array.

## Decision

`src/wire.c` validates every length that arrives from the network before using it
as a copy size for a fixed-size destination.

- `decode_REGISTER_SUPER`, `decode_UNREGISTER_SUPER`, `decode_REGISTER_SUPER_ACK`
  and `decode_REGISTER_SUPER_NAK` reject `auth.token_size` above
  `N2N_AUTH_MAX_TOKEN_SIZE`.
- `decode_REGISTER_SUPER_ACK` rejects a backup-supernode count whose byte size
  exceeds `REG_SUPER_ACK_PAYLOAD_SPACE`.
- A rejection clears the offending field before returning `-1`, so a caller that
  ignores the return value cannot re-encode the rejected length.
- `process_udp` drops the datagram when `decode_REGISTER_SUPER` or
  `decode_UNREGISTER_SUPER` returns a negative value.

Copies with compile-time sizes, such as MAC, community, socket, descriptor and
version, keep using `decode_buf` unchanged.

## Alternatives considered

**Give `decode_buf` a destination-size argument.** It would centralise the
bound, but it touches a helper used by every decoder and every fixed-size copy
for a hazard that only exists where the length is wire-supplied. The call-site
checks keep the changed surface small. Revisit this if more wire-supplied
lengths appear.

**Clamp the value in the encoders.** This guards the re-encode path but leaves
the decoder writing past the field first, so the overflow remains.

**Trust the decoder return value alone.** `process_udp` ignores the return value
at its other decode sites, such as `decode_PACKET` and `decode_QUERY_PEER`, and
the forwarded copy of a registration is encoded outside the decoder, so the
second read would stay unbounded.

## Consequences

Oversized token lengths and oversized backup-supernode arrays are dropped before
any copy happens, and the encoders read only within the token array. Valid
packets are unaffected: `token_size` values from 0 to 48 and empty backup lists
still decode, and the decoders report the same byte counts as before.

The checks encode a relationship between one field and one array, so a new field
with a wire-supplied length needs its own bound. The negative return from the two
registration decoders is now load-bearing: new decode sites in `process_udp`
must keep handling it.
