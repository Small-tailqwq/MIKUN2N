# Integrating P2P punching into MikuN2N

> [English](P2P-PUNCH-INTEGRATION.md) | [简体中文](P2P-PUNCH-INTEGRATION.zh.md)

Updated: 2026-07-24
Status: Phase A is landed; Phase B's dual-NAT4 complementary roles have entered the 0.4.1 test build

## Background and goals

In the current 0.3.1 test build, n3n punches P2P on its own for **ordinary NAT** (cone↔cone, cone↔sym, predictable sym↔sym), but it fails on **hard cases** (cloud PC = cone + port-restricted, main machine = symmetric + port-restricted) and stays on supernode relay (pSp). The `natpunch` v6/v7 series has already **proven in the standalone tool** that predictable NAT4 can establish a bidirectional direct path using "measure bank/drift + server GO sync + both sides spray the peer's predicted port window simultaneously" (v6 round 51 punched main machine ↔ cloud PC).

This plan integrates that dedicated punching capability cleanly into the product, tiered progressively with fast fallback on failure. Core constraints and the already-decided architecture:

- **Punching must happen on n3n's data socket (50001)**. NAT mappings are per-socket and per-destination; a hole punched by a separate process/socket cannot be used by n3n's data port. So the real bank prediction + synchronized spraying **lives in the patched n3n binary** (`edge_utils.c`), not on the C# side.
- **Both endpoints exchange NAT models and GO sync signals through the n3n supernode** (it is already the trusted rendezvous point and knows both public endpoints). Production no longer depends on a standalone natpunch server; natpunch is kept as a research/diagnostic tool.
- **MikuN2N (C#) is responsible for**: displaying NAT type detection, honestly presenting punching state, strategy/budget switches (delivered through the generated n3n config), and end-to-end integration.

## Tiered strategy (aligned with NATPUNCH-V7 summary §6)

- **Tier 0 — native n3n**: normal REGISTER exchange + the existing periodic punching patch. Covers most non-hard NAT combinations.
- **Tier 1 — upgraded punching (new this round)**: after a peer stays on pSp longer than a threshold of T seconds, both sides enter one round of **coordinated punching**:
  1. Each side measures its local NAT on the data socket (two bank tops + drift rate + mode).
  2. Exchange the NAT model + a synchronized GO through the supernode relay (a new edge-to-edge control message).
  3. At the GO moment both sides **simultaneously** spray n3n `REGISTER` at the peer's predicted port window (a hit establishes a real P2P path; `last_p2p` refresh → n3n automatically switches to p2p).
  4. There is a **budget** (time/send caps); if punching fails within budget, fall back to relay (no stubborn retries against fast-cycle/multi-exit CGNAT).
- **Tier 2 — graceful give-up**: hard / fast-cycle / multi-public-IP CGNAT, accept relay and present state honestly.

## Where each component lands

| Capability | Where it lands | Notes |
|---|---|---|
| NAT self-measurement (bank/drift/mode) | n3n edge, data socket | Ports are allocated per destination, so it must be measured on the same socket. Two observation angles are needed → the supernode adds a **second UDP observation port** (STUN-lite, echoing the observed source ip:port), replacing natpunch's A/B dual ports |
| Model + GO exchange | edge-to-edge control message relayed by the supernode | The supernode already forwards edge-to-edge packets within the community; piggybacking a short punching-coordination payload is a natural extension |
| Prediction + synchronized spraying | n3n edge `mikun2n_punch_peer` upgrade | Change from fixed ±900 blind spraying to "measured bank + predicted window + attempt salting + GO sync", sending with `send_register` |
| NAT type / punching state display | MikuN2N C# | Add a management call to read the NAT classification computed by edge; honestly show "punching / punching failed, on relay" |
| Strategy/budget switches | MikuN2N C# → generated n3n config | Test builds can tune thresholds/budget/switches without recompiling |

## n3n original patch baseline (before Phase B)

- The original `mikun2n_punch_peer` implementation is **fixed blind spraying** — a near window `base-8..+80` plus a sibling engine `base±[850..1020]`, based on a fixed dual-active-gateway assumption, with no measurement, no drift prediction, and no sync with the peer.
- The main loop calls it every 2 seconds for every AF_INET peer that is non-local with a stale `last_p2p` (`edge_utils.c:3256`).
- Each row of the `get_edges` management output (`management.c:582` `jsonrpc_get_edges_row`) already contains: `mode`(p2p/pSp/sn), `ip4addr`, `macaddr`, `sockaddr` (**the peer's public ip:port as observed by the supernode**), `prefered_sockaddr`, `local`, `last_p2p`, `time_alloc`, `last_seen`. C# currently reads only `mode`/`ip4addr`/`desc`/`macaddr`; `sockaddr`/`last_p2p` are already available but unused — the peer public endpoint needed for Tier 1 coordination is obtainable without any protocol change.

## natpunch assets that can be ported directly (research findings → production)

- **NAT classification** `fit_model` (`tools/natpunch/natpunch.c:344`): cone / sym / hard / volatile / fast, the two bank tops, step, spread, drift rate, fast-cycle stickiness. The logic is self-contained with self-tests and can be ported to edge C as a whole.
- **Punching scheduler** `build_targets`+`spray_attempt`+`handle_peer_packet`: tiered windows (low 1..64 / mid 65..256 / predicted / tail), control lane, fast-target/fast-sender, attempt salting, GO sync, and replying A7 from the **receive socket to the real source** on P7 (the key that makes port-restricted/symmetric work).
- **Protocol essentials**: the core is "measurement must not pollute counters → both ends spray the peer's `[bank..+N]` in increasing order at the same GO moment → index i hits the peer's i-th mapping". When porting to n3n the payload becomes n3n `REGISTER` and the rendezvous becomes the supernode relay.

## Implementation phases

### Phase A — C# foundation and honest state (landed this round, verifiable by building)

- Add `Punching` to `PeerConnectionMode`; add "punching…" to `ConnectionModeText`.
- Add `RelayedSince` to `EdgeRun` (records the moment of entering relay, keyed by virtual IP).
- `PublishConnected`: show a peer as "punching…" for the first `PunchDisplayWindow` (25s) after it enters pSp, then show "pSp relay" if still relaying after the timeout; the detail row adds "N peers punching". The window duration will later align with the Tier 1 punching budget.
- No protocol change, no n3n change, a pure display-layer improvement that reflects the real process of "n3n is punching for this peer".

### Phase B — n3n edge: measurement + adaptive spraying (requires rebuild + local sym↔sym testing)

- Port `fit_model` to edge, periodically measure the local NAT on the data socket; expose the classification via a new management method `get_nat`.
- Upgrade `mikun2n_punch_peer`: replace the fixed ±900 blind spraying with measured bank + drift-predicted window + attempt salting (an incremental improvement even without supernode sync yet).
- Run sym↔sym regression on two user-held machines both on the new version (main machine symmetric ↔ rented machine/cloud PC).

First-round test implementation (2026-07-24):

- Reuse the natpunch A/B observation ports `21001/21002` already deployed on the same host as the Supernode, and have n3n's own data socket run "A mapping → A asks B to punch back → B mapping" in order, avoiding the gap between a separate C# socket's results and the actual tunnel socket.
- `get_nat` exposes mappings, filtering, observation ports and strategy; the 0.3.1 main UI shows NAT1/2, NAT3, NAT4.
- Native n3n Tier 0 keeps 5 seconds; only if it stays on pSp does the 20-second Tier 1 trigger. During this the main loop uses 250ms time slices; the ordinary/NAT3 side fully repeats control + low 1..64 and rotates mid/far, the NAT4 local side uses a low-fan-out fixed anchor; at most 9000 packets per peer, keeping pSp on failure.
- `get_edges` adds `punch_state`, elapsed time, attempt and sent-packet count; the log keeps the start, the successful transition, and the budget-exhaustion reason.

Current boundary: this is a Phase B canary that does not change the public supernode binary, fixing the old implementation's problems of a 10-second `select()` sleep, whole-window blind spraying every two seconds and unlimited retries. It does not yet implement Phase C's mutual NAT-model exchange and strict GO sync, so NAT4↔NAT4 still cannot promise a direct path; cloud PC ↔ local machine must first undergo real-network regression with the new version on both ends before deciding whether to enter Phase C.

0.4.0 test build increments (2026-07-24):

- Standalone tool v7.3.1 verified in a cross-ISP NAT4 scenario: the A/B ports for the same server IP both reuse `59832`, but the egress to the real peer is `59849`, i.e. a cross-destination-IP offset of `+17`; the old version mistook this local stability for a real Cone and attacked only control, failing for 21 consecutive rounds.
- The n3n NAT4 sender switches to cross-address Cone escape: keep control every 250ms, and split `control±1..64` plus this round's two-way 192-port band into four slices; one second fully covers 513 candidates, and the next round keeps pushing outward.
- The Tier 1 budget is adjusted to 22 seconds / 12000 REGISTER, outputting peer control, near and rotating ranges each round; success logs use 14-bit signed offsets to accurately record hits in the `±4096` range.
- The non-NAT4 layered low/mid/far path, native n3n Tier 0 and the post-failure pSp relay all stay unchanged.

0.4.1 test build increments (2026-07-24):

- 0.4.0 two-end testing confirmed that when both ends are NAT4 they fan out high simultaneously, a single n3n data socket keeps producing new egress ports due to destination-dependent mapping, and both sides sending roughly 9000 REGISTERs still yields zero P2P hits.
- Dual-NAT4 ends derive complementary roles from a stable ordering of local/peer MAC: one end is the scanner, the other the anchor; both ends independently computing this necessarily get opposite results, so no new server-side protocol is needed.
- The scanner's phase/round switches to 250ms common wall-clock slots, aligning even when the two ends start about a second apart; the anchor only repeats the fixed `control/±1/±2/.../±256` set, reducing continuous mapping drift.
- Tier 1 explicitly excludes the current Supernode endpoint, no longer mis-spending the full budget scanning the public `3076`.
- `get_edges` and the run log add `punch_role`, `punch_band_lo/hi` for later two-end alignment analysis.

### Phase C — supernode coordination + synchronized spraying (requires supernode rebuild + redeploy to vps.example.com, ⚠ requires user confirmation)

- The supernode adds a second UDP observation port (STUN-lite echo of the source ip:port).
- Add a supernode-relayed edge-to-edge punching-coordination PDU: forward the peer's NAT model + issue a shared `attempt` and GO moment.
- On receiving GO, edge sprays synchronously on the data socket; a hit refreshes `last_p2p` and n3n automatically switches to p2p.
- **Deployment note**: the production node has real users, redeployment is an outward-facing, hard-to-rollback operation that needs consent first; canary on a bypass port/test group first.

### Phase D — C# wrap-up and end-to-end

- Done: C# reads `get_nat` to show the local NAT type and reads `punch_state` to honestly distinguish punching-in-progress from budget-exhausted relay.
- To do: strategy/budget UI; cloud PC ↔ local machine end-to-end punch verification.

## Validation

- Phase A: `dotnet build` passes; after a local connection, a relayed peer shows "punching…" for the first 25s, then switches to "pSp relay".
- Phase B/C: rebuild n3n with MinGW (`<n3n-build>\n3n-3.4.4-patched`, `./scripts/hack_fakeautoconf.sh && make -j4`, `config.mak` CFLAGS needs `-std=gnu17`), test sym↔sym on two on-hand machines; check the log for a `last_p2p` refresh and whether a bidirectional direct path appears.
- Keep the model/attempt/lane/offset/first-hit/relay-reason for every hard-case result, following the natpunch research convention.

## Related

- Research findings: `NATPUNCH-V7-SUMMARY.md`, memory `nat-p2p-breakthrough`, `natpunch-tool`.
- Patch notes: the Runtime section of `CLAUDE.md` (punching patch, build toolchain).
