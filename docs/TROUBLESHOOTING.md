> [English](TROUBLESHOOTING.md) | [简体中文](TROUBLESHOOTING.zh.md)

260725pm0939
A failure: could not hole-punch with the NAT4 peer, needs investigation.
The natpunch-v7.3.1.exe test works fine, but N2N cannot punch through successfully:
natpunch log
Our side:
```
<repo>\tools\natpunch>.\natpunch-v7.3.1.exe client vps.example.com 1919810
[2026-07-25T21:35:51.155 +0ms] === natpunch v7.3.1 / 自适应 NAT4↔NAT4 ===
[2026-07-25T21:35:51.157 +0ms] client=366612a509dcc831 room_tag=6fcf9f52 control_local=51779 workers=25 duration=180s
[2026-07-25T21:35:51.159 +0ms] 日志: %LocalAppData%\MikuN2N\logs\natpunch\natpunch-v7.3-20260725-213551-13184.log
[2026-07-25T21:35:51.162 +0ms] 结构化日志: %LocalAppData%\MikuN2N\logs\natpunch\natpunch-v7.3-20260725-213551-13184.jsonl
[2026-07-25T21:36:12.849 +21687ms] [配对] peer=27aeee64da0e34df 198.51.100.101:35843，我方 control 映射=26807
[2026-07-25T21:36:12.855 +21703ms] [校准#1] 开始，保持 25 个 socket 到本轮结束
[2026-07-25T21:36:12.905 +21750ms] [校准#1] mode=sym conf=82.0% A=25/25 B=25/25 reuse=0 IP切换=0 volatile=0 fast=0 inherited=0 banks=[26091,26856] step=1 spread=15 rate=0/s rtt=23ms
[2026-07-25T21:36:12.942 +21781ms] [对端模型] attempt=1 mode=cone conf=100.0% rate=0/s banks=[35843,35843] spread=4 step=1 workers=25
[2026-07-25T21:36:13.757 +22594ms] [GO#1] mode=cone 候选=513，peer control=35843，跨IP不确定性回退=on near=±1..64 rotating=±65..256
[2026-07-25T21:36:14.110 +22953ms] [命中] 首个 P7，local_socket=worker#19 local_port=53331，真实源=198.51.100.101:35886，peer_worker=17 target=27089 lane=mid offset=233 tick=3
[2026-07-25T21:36:14.200 +23047ms] [成功] 双向确认，获胜 socket=worker#19，真实端点=198.51.100.101:35886，耗时=438ms
[2026-07-25T21:36:17.223 +26062ms] [轮次汇总] attempted=1325 sent=1325 errors=0 one_way=yes success=yes
[2026-07-25T21:36:17.229 +26062ms] ============================================
[2026-07-25T21:36:17.231 +26078ms] 结果: 打洞成功 [OK]
[2026-07-25T21:36:17.232 +26078ms] ============================================
```
Peer side:
```
%UserProfile%\Desktop\natpunch-test>.\natpunch-v7.3.1.exe client vps.example.com 1919810
[2026-07-25T21:36:16.478 +0ms] === natpunch v7.3.1 / 自适应 NAT4↔NAT4 ===
[2026-07-25T21:36:16.480 +15ms] client=27aeee64da0e34df room_tag=6fcf9f52 control_local=50605 workers=25 duration=180s
[2026-07-25T21:36:16.482 +15ms] 日志: %LocalAppData%\MikuN2N\logs\natpunch\natpunch-v7.3-20260725-213616-8308.log
[2026-07-25T21:36:16.484 +15ms] 结构化日志: %LocalAppData%\MikuN2N\logs\natpunch\natpunch-v7.3-20260725-213616-8308.jsonl
[2026-07-25T21:36:16.500 +31ms] [配对] peer=366612a509dcc831 198.51.100.139:26807，我方 control 映射=35843
[2026-07-25T21:36:16.507 +31ms] [校准#1] 开始，保持 25 个 socket 到本轮结束
[2026-07-25T21:36:16.581 +109ms] [校准#1] mode=cone conf=100.0% A=25/25 B=25/25 reuse=25 IP切换=0 volatile=0 fast=0 inherited=0 banks=[35843,35843] step=1 spread=4 rate=0/s rtt=23ms
[2026-07-25T21:36:16.593 +125ms] [对端模型] attempt=1 mode=sym conf=82.0% rate=0/s banks=[26091,26856] spread=15 step=1 workers=25
[2026-07-25T21:36:17.409 +937ms] [GO#1] mode=sym 候选=513，peer control=26807，banks=[26091,26856]，固定覆盖=1..256 low=128 mid=384 predicted=222 tail=0
[2026-07-25T21:36:17.778 +1312ms] [成功] 双向确认，获胜 socket=worker#17，真实端点=198.51.100.139:27089，耗时=375ms
[2026-07-25T21:36:20.806 +4328ms] [轮次汇总] attempted=612 sent=612 errors=0 one_way=yes success=yes
[2026-07-25T21:36:20.808 +4343ms] ============================================
[2026-07-25T21:36:20.810 +4343ms] 结果: 打洞成功 [OK]
[2026-07-25T21:36:20.811 +4343ms] ============================================

(按回车退出)
```

n2n log:
Our side: "%LocalAppData%\MikuN2N\logs\n2n-20260725-204936-840-s1.log"
Peer side: "%UserProfile%\Downloads\n2n-20260725-213206-799-s1.log"

---

## Diagnosis conclusion (26-07-25)

### Fact comparison

| | Our side (192.0.2.45) | Peer ADMIN (192.0.2.163) |
|---|---|---|
| n3n NAT probe | NAT4, mapping/filtering both addr-and-port-dependent, external `198.51.100.139:26796/25859` (probed at 20:49:40) | NAT4, mapping/filtering both addr-and-port-dependent, external `198.51.100.101:34848/34856` (probed at 21:32:10) |
| natpunch measured | mode=sym conf=82%, banks=[26091,26856], control mapping 26807 | **mode=cone conf=100%, reuse=25/25, banks=[35843,35843]** |
| n3n role | anchor (base=34848, 21 fixed targets ±256) | scanner (base=27096, near window ±1..64 + rotating band ±65..4288) |
| n3n result | 22s / 2898 packets exhausted, pSp | 22s / 9108 packets exhausted, pSp |
| natpunch result | 438ms punched through, real endpoint `198.51.100.101:35886` | 375ms punched through, real endpoint `198.51.100.139:27089` |

The natpunch hit details are the key to all the clues: the peer hit `27089` at **+233 above our bank2=26856** (`predicted=222`, fixed coverage `banks+1..256`); and our natpunch control mapping is 26807. In other words, **the port that actually punches through has no fixed relationship with the "control port observed by the supernode" — it depends on the NAT counter (bank) value at that exact moment.**

### Root cause (ordered by confidence)

**1. n3n picked the wrong search base, and this information has no exchange mechanism at all (primary cause)**

In `mikun2n_punch_peer()`, `base = pp->sock.port` — i.e. the peer's control port observed by the supernode — and it searches over `base ± offset`. For **symmetric NAT** this base is wrong:

- The supernode mapping is allocated **at the moment the session is established** (ours at 20:49:37), while the hole-punch mapping is newly allocated **at the moment of punching** (21:32:08), with 43 minutes of counter drift in between;
- More fundamentally, an addr-and-port-dependent mapping allocates one port per destination, so the difference between the supernode mapping (→3076) and the hole-punch mapping (→ peer IP) is **unbounded**. In the log, for three destinations 21001/21002/3076 our side got three unrelated values 26796/25859/27096 (two of which differ by 937 = the two-bank interval of a dual-active gateway).

natpunch wins in 400ms because, since v6, it exchanges `BANKS room sp cb1 cb2` through the server: **the hole-punch socket's fixed port + the two current counter banks**, and the peer directly sprays `bank+1..256`. The n3n patch has **no such exchange at all** — I grepped `src/` and no bank/counter field enters REGISTER or supernode forwarding. `get_nat` clearly measures both banks (26796/25859), but only reports them to the local UI and never tells the peer.

**This means n3n currently sits on the natpunch v5 design**, and v5 was already judged a deadlock in memory (see the v5→v6 section of memory `nat-p2p-breakthrough`).

**2. Port-restricted filtering × 21 anchor mappings = only 1 target is effective**

Our filtering is address-and-port-dependent. The anchor sends packets to 21 destination ports `34848 ± {0,±1,±2,±4,…,±256}`, and the symmetric NAT opens one **different external port** mapping for each of these 21 destinations. But the peer (cone) replies with only one source port (34848). So of our 21 mappings, **only the offset=0 one** (built for 34848) will admit the peer's packets; the other 20 are pure waste, and building them in the first tick additionally pushed our own counter up.

For the peer to succeed it must hit exactly this **single** port, and it does not even know which range that port is in (see root cause 1).

**3. Scanning the full range once still misses: single-shot coverage, no retries**

> Correction (after re-check): the first draft's claim that "the two sides started 4 seconds apart with only 18 seconds of overlap" is **wrong** — that was an artifact of clock skew. Aligning by natpunch's pairing events (the server sends PAIR at the same instant, rtt=23ms): we recorded `21:36:12.849`, the peer recorded `21:36:16.500` → **the peer's system clock is about 3.65 seconds fast**. Converting back to n3n: the peer's 21:32:12.241 (its clock) = 21:32:08.59 on our clock, and we started at 21:32:08.260 — **in real time only 0.3 seconds apart, the 22-second budget almost fully overlaps**.

Reconciling the peer scanner's send volume: per tick `4(control) + 32(near window) + 96(band) + 1(drift) = 133` packets, 4 ticks/s, 22s theoretical ~11700; actual 9108, exactly equal to the candidate total `64×2 + 4224×2 = 8576` plus control resends. **In other words it scanned `27096 ± 1..4288` completely, each candidate exactly once, and then exhausted.**

So the real problem is not "didn't scan it", but:

- **Each candidate gets only one shot; a lost packet permanently burns that candidate**, with no retry at all. natpunch is the opposite — each GO round re-sprays the highest-probability range (`banks+1..256` + `predicted`).
- Sending ~532 pps to over a thousand ports on a single IP has the same behavioral signature as a port scan, and rate-limiting/dropping by both CGNATs is quite likely (this item is a hypothesis, not provable from the log).

**Moreover, a full-range scan missing entirely in turn reinforces root cause 1**: our mapping port toward the peer's IP very likely is not within `27096 ± 4288` at all — and n3n has no way to know where it is, nor ever tells the peer its own value.

**3b. The "shared wall-clock slot" synchronization is silently broken by clock skew (potential bug)**

`round = (now_ms/250/4) % 22`, `phase = common_slot % 4` are all taken from each side's own OS wall clock; the source comment explicitly says it relies on this to work "even when peers enter Tier 1 at different times". In practice the peer's clock skews by **3.65 seconds = 14.6 250ms slots / 3.65 bands** — the band and phase the two sides compute are simply not the same.

This run did not blow up only because we happened to be the anchor (which does not use round). Switch to scanner↔scanner or layered roles and it would be directly misaligned. **Synchronization cannot rely on end-side wall clocks; it must be timestamped by the supernode.**

**4. The peer's NAT was misjudged as NAT4, forcing the hardest NAT4↔NAT4 strategy**

The two tools actually run **the same test** (the same server IP `vps.example.com`'s two ports 21001/21002, checking whether the same socket's external port is identical), just with 25× different sample sizes:

| | Samples | Observation | Verdict |
|---|---|---|---|
| natpunch (`natpunch.c:403`) | 25 sockets × 2 destination ports = 50 | all `mapped_a == mapped_b` → `reuse=25/25` | cone, conf=100% |
| n3n | 1 socket (data socket) × 2 destination ports = 2 | 34848 / **34856** (diff 8) | addr-and-port-dependent |

n3n also missed a third observation point it already had: the supernode saw the peer's registered port was likewise **34848** (our log `punch started for peer port 34848`). Same socket → three destination ports (3076/21001/21002) → `34848 / 34848 / 34856`, **two-thirds identical**.

The exact cause of `+8` cannot be pinned down from the log (multi-engine CGNAT on the peer? a single anomalous probe?), but the cause does not matter — **the problem is that the classifier is zero-tolerance: as soon as A≠B it falls straight to the worst class**, and one anomalous sample flips cone to symmetric. Two samples are simply not enough to make this judgment.

Verdict boundary (to state honestly): both tools use only **one server IP**, so strictly what they measure is only "mapping is independent of **destination port**", **not "independent of destination IP"**; the peer could still theoretically be address-dependent. But from the punch result, our spray of `±1..64` around the peer's control port 35843 hit its worker#17 at 35886, so in practice treating it as cone holds.

**ADMIN is actually cone**. If n3n knew this, this would be an already-verified NAT3↔NAT4 scenario (the "orange dream" class in memory), and the correct play is for us to build **exactly one** stable mapping to its single fixed port instead of spraying 21. Right now both sides report NAT4 → they split scanner/anchor by MAC → and use the hardest strategy against a combination that was never hard.

Also, NAT probing is done only once at startup (20:49); the 21:32 hole punch used a verdict that was 43 minutes stale, never re-probed in between.

### Second sample (玩家B, 26-07-25 22:07) — independent confirmation

The correct pairing is the **s3 session** (`n2n-20260725-213641-646-s3.log`, 0.5.1) rather than the s5 shown in the doc (0.4.1, started 22:10, not simultaneous with the peer):

| | Punch window | Packets | Role |
|---|---|---|---|
| Our s3 (0.5.1) | 22:07:12.007 → 22:07:34.112 | 2814 | anchor |
| 玩家B (0.5.1) | 22:07:14.086 → 22:07:36.090 | 9372 | scanner |

**The two windows overlap by about 20 seconds, so they punched simultaneously**, and the failure mode is identical to ADMIN:

- `9372 ÷ 132 = 71.0` ticks → the rotating band only sent `71 × 96 = 6816` / 8448 = **81%, missing 1632 candidates**;
- the scanner started with `shared_round=21` (±3905..4096, **the farthest band**), the wall-clock phase once again beginning from the lowest-probability region;
- in the log the phase-0 lines missing rounds **3, 4, 6, 12, 14** — direct visible evidence of **holes** caused by dropped ticks, showing truncation happens in the middle (digging holes) rather than at the end;
- 玩家B was likewise misjudged: natpunch measured `reuse=25/25, cone, banks=[58491,58491]`, n3n judged NAT4 from `61846/61853` (diff 7).

Along the way, confirm a version fact: **0.4.1 already had the anchor/scanner role split** (s5 log `role=anchor`, version `0.4.1-test.b...`), and 0.4.1 could punch through for the user's NAT4↔NAT4 sibling machines. So **the role split itself is not a regression**. The `n3n-build/pre-0.4.1-*` snapshot is **0.4.0** (the archive from before the 0.4.1 changes, csproj BaseVersion=0.4.0), and the role split was added in 0.4.1. The 0.4.1 source was not archived, so the specific 0.4.1→0.5.1 diff cannot be obtained directly; the wall-clock scheduling problem is inferred from comparing the 0.4.0 snapshot + the runtime log.

### Suggested fixes (by cost-effectiveness)

1. **Bring the bank information into n3n** (corresponding to natpunch v6's breakthrough). `get_nat` already has the measurement capability; what's needed is: re-measure both banks **right before punching**, forward `(sp, cb1, cb2)` to the peer via the supernode (a REGISTER_SUPER extension field or a new management message), have the peer swap `base` from `pp->sock.port` to `cb1/cb2`, and spray **only upward `+1..256`** (counters increase monotonically), not `±`. This is the only change that truly resolves root cause 1; everything else is mitigation.
2. **Exchange NAT types and pick strategy by combination**. When the peer is cone: our side builds only one mapping to its single port (the anchor degenerates to 1 target), giving the whole send budget to "keepalive + let the peer scan"; the scanner side then enjoys "only one stable target port". Along the way, fix the prober: two samples are not enough to judge symmetric, sample at least multiple destinations/sockets, or directly trust the `reuse` semantics.
3. **Move synchronization to supernode timestamps, and abandon "scan once" style coverage**. The supernode is the natural rendezvous point; have it hand down a common fire time and round number, replacing the `common_slot` now taken from each side's OS wall clock (measured 3.65s skew, see 3b). At the same time, change the equal-probability rotating band to "re-spray the high-probability range every round", and stop giving each candidate only one shot.
4. **Stop the anchor from spraying multiple targets**. Right now it both pollutes its own counter and creates 20 mappings that will never be used.
5. **Fix the NAT classifier**. At minimum, sample multiple sockets/destinations and introduce majority voting, so a single anomalous sample cannot flip cone to symmetric; also include the supernode-observed registered port as one sample. If possible, add a second IP to the probe server to truly distinguish port-dependent from address-dependent.
