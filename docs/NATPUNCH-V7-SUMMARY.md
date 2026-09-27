# NATPUNCH v7 summary

> [English](NATPUNCH-V7-SUMMARY.md) | [简体中文](NATPUNCH-V7-SUMMARY.zh.md)

Updated: 2026-07-24

## 1. Conclusions

The natpunch v7 series has verified:

- For predictable NAT4 ↔ NAT4, synchronized GO, a kept socket, direct control
  punching, single/dual-bank fitting and low-offset fixed coverage can
  successfully establish a bidirectional UDP path.
- For the mobile-traffic CGNAT that "increments fast for the same target, maps
  unpredictably across targets, and filters strictly", v7.1, v7.2 and v7.3 all
  failed to obtain a one-way P7. Port observation from a single probe server
  alone cannot derive the complete external 4-tuple used when sending to another
  public IP:port.
- v7.3 has ruled out the main software scheduling issues: insufficient packet
  volume, sparse static-band sampling, repeated fixed blind spots and obvious
  network throttling. Expanding pure UDP brute-force scanning further yields
  little, at the cost of more mapping state, carrier throttling risk and longer
  connection times.

We therefore pause the NAT4-focused experiments, keep all versions, logs and
conclusions, and shift to reworking the MikuN2N main application.

## 2. Verified successful scenarios

Regular NAT4 ↔ regular NAT4 completed three valid tests, all successful:

| Test | Successful attempt | Successful round time | Key target |
|---|---:|---:|---|
| v7.1 first | 2 | ~1.94 s | bank +2, other side control |
| v7.1 second | 5 | ~1.94 s | bank +1, other side control |
| v7.1 third | 8 | ~3.0 s | bank +28, other side control |

Common patterns:

- One side's actual mapping stays stably within the low-offset range of the
  observed bank.
- The other side reuses or approximates the control mapping when sending to the
  peer.
- `low 1..64` and the control lane are the direct cause of the three successes.
- Once any socket receives P7, replying A7 from the same socket to the packet's
  real source completes bidirectional confirmation.

Current evidence supports v7.1's tiered scanning as a valid implementation for
"predictable NAT4".

## 3. Mobile CGNAT observations

Valid cross-public-IP tests:

- Regular-side public IP: `198.51.100.139`
- Mobile-traffic public IP: `198.51.100.115` then `198.51.100.64` were observed.

The mobile side behaves toward the probe server as follows:

- Single bank dominates.
- Ports increment fast.
- Rate is usually about 150–380 ports per second.
- It can wrap past 65535 and cycle quickly back to low ports.
- Not uniform random within the same probe target.

However, the bank, rate and wrap phase observed by the server never produce a P7
the peer can receive. This suggests mobile CGNAT most likely also exhibits one or
more of:

- mapping depends on the destination public IP;
- mapping depends on the destination port;
- different destinations use independent banks or hash salts;
- address-and-port-dependent filtering requires both sides to guess the complete
  4-tuple at once;
- the mapping toward the probe server has no usable stable relationship with the
  mapping toward the real peer.

## 4. Per-version experiment conclusions

### v7.0 / v7.1

- Established the calibration, punching and confirmation lifecycle for one set of
  sockets.
- Supports control, single bank, dual bank, volatile, and low/medium-offset
  fixed coverage.
- Added P7/A7 hit metadata and structured logging.
- Three regular NAT4 ↔ regular NAT4 successes.
- Fast port wrap, probe loss and large amounts of mapping state appeared in
  mobile high-speed CGNAT tests.

### v7.2

- Added the `fast-cycle` model.
- Keeps the last trusted bank, rate and ring-time baseline when probes are
  insufficient.
- The mobile side uses the low-fanout `fast-sender`.
- The regular side uses the moving-prediction `fast-target`.

Results:

- The mobile side's per-round packet volume dropped.
- Probes improved from frequent `0/25` to mostly `23–25/25`.
- Observed rate dropped from a peak of 300+ to mostly 150–200/s, proving the
  scanning itself had caused significant mapping inflation.
- 18 attempts with no one-way P7.
- Later analysis found the static band sampled only a few ports and repeated the
  same ordering every round.

### v7.3

- The regular side fully covers the 385-port initial static prediction band
  about every 0.6 seconds.
- Keeps a fast-moving lane that moves over time at the same time.
- anchor, bank selection, and static/moving ordering are salted per attempt.
- The mobile side keeps low fanout while the regular side carries the dense
  scanning.

Final test:

- 18 mutually aligned GO attempts.
- The regular side sent about 8190–8568 packets per round.
- The mobile side sent about 4864–5320 packets per round.
- The mobile model stayed fast from attempt 2 on, at about 184–230/s.
- Mobile calibration was mostly 23–25/25; the severe v7.1 probe loss did not
  reappear.
- Every round had `one_way=no`: no P7, no A7, no bidirectional confirmation.
- Attempt 19 timed out on coordination because the other end's total time budget
  expired; it does not affect the conclusions of the first 18 rounds.

v7.3 proves that, under the current single-probe-server information, the failure
is not incomplete 385 static-band coverage, nor a fixed relative-port blind
spot. The real peer mapping most likely lives in another destination-dependent
space.

## 5. Current source and binaries

Directory: `MikuN2N/tools/natpunch`

- `natpunch-v7.1.exe`
  - SHA-256: `4C36CFD498157A83F3033A582D02A5277CB80F34D91F3364206433E1F8C38852`
- `natpunch-v7.2.exe`
  - SHA-256: `097655CEABA079B16CA4B374645B5761FD7CD283BCAFDF800B63575914336C1E`
- `natpunch-v7.3.exe`
  - SHA-256: `2F5478748B46B91B6E036EC712437FFAE19D0E2FAB65A65E2B1E9FACD131BD12`

Source snapshots:

- `legacy/v7.0/`
- `legacy/v7.1/`
- `legacy/v7.2/`
- The current `natpunch.c` is v7.3.

The server keeps supporting the v6/v7 protocol; existing deployments need no
separate upgrade for v7.3.

## 6. MikuN2N integration recommendations

We do not recommend having the production client scan a fast CGNAT for 180
seconds continuously. We recommend tiering by capability:

1. Try IPv6, port mapping, UPnP/PCP, or existing direct-connection candidates
   first.
2. For predictable NAT4, use v7.1's control + bank low-offset tiered punching.
3. When fast-cycle is detected, run only one short, budgeted v7.3 experiment.
4. When no one-way P7 appears, fall back to supernode/relay promptly to avoid
   generating more CGNAT state.
5. Save the model, attempt, lane, offset, first hit and relay reason for future
   study.

The production goal should be "direct connection whenever possible with fast,
reliable fallback", not making every NAT4 combination succeed through pure UDP
exhaustion.

## 7. Requirements for future research

If the focused research resumes later, prioritize adding new observation
capability over sending even more packets:

- Use at least two probe nodes with different public IPs to measure
  destination-address-dependent mapping.
- Add multiple destination ports to separate destination-address hashing from
  destination-port hashing.
- Capture the external mapping toward the real peer on a carrier-side or
  controlled NAT to verify the hash/independent-bank hypothesis.
- Study birthday-paradox multi-socket algorithms, but always set mapping-count
  and bandwidth caps.
- Keep reliable relay as the final path for strict address-and-port-dependent
  mapping/filtering.

## 8. Test material

- `V7第一次测试.md`
- `V7第二次测试.md`
- `V7.1测试合集.md`
- `V7.2测试合集.md`
- `V7.3测试日志.md`

These files, together with each version's source, form the complete reproduction
material for this stage.
