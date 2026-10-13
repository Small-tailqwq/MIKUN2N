# natpunch v7.3.1

> [English](README.md) | [简体中文](README.zh.md)

A UDP NAT4 (address- and port-dependent mapping) punching experiment tool for
Windows. The source is kept permanently in the MikuN2N repository so it is not
again left behind as only a temporary directory or binary.

## v7.3.1 minimal fix

v7.3.1 fixes the single-probe-IP blind spot exposed by cross-ISP testing:

- The A/B probes still sit on the same server IP, so `cone` only means the port
  mapping is stable for that same destination IP; it does not prove the port
  stays unchanged when reaching another peer's IP.
- When the peer reports `cone`, it no longer tries only the control port; every
  round keeps covering `control ±1..64` and rotates across a 192-port band on
  both sides.
- Round 1 covers up to `±256` and later rounds advance from there; 21 rounds
  explore at most about `±4096`.
- The existing prediction and scheduling for `sym`, `volatile` and `fast` stays
  unchanged, and the server protocol is unchanged.
- The GO log adds the `跨IP不确定性回退`, `near` and `rotating` ranges, and the
  JSONL adds `cone_escape`, `band_lo` and `band_hi` so it is easy to confirm
  whether that path was hit.

## v7.3 strategy

v7.3 is the final focused round of experiments for fast-cycle mobile CGNAT:

- `fast-target` runs on the regular end: it keeps densely scanning the 385-port
  static prediction band at GO time, covering it completely about once every
  0.6 seconds, while keeping the moving lane that advances with time.
- `fast-sender` is enabled only when the local end is itself fast; it keeps the
  control, fixed anchor and phased moving low-fan-out targets.
- The anchor offset, dual-bank selection and static/moving arrangement all
  include the attempt salt; a failed retry explores new relative ports instead
  of repeating v7.2's fixed blind spots.

The following model capabilities already implemented in v7.2 are kept:

v7.2 keeps v7.1's validated layered scanning for regular NAT and adds an
independent `fast` path for the fast increment, rapid cycling and intermittent
packet loss common in mobile traffic:

- A single bank enters `fast-cycle` after its sustained rate reaches
  120 ports/second.
- When probes run short it keeps using the last trusted bank, ring direction
  and rate instead of falling back to the distorted control model; once
  sampling resumes it uses the ring difference across a full time interval.
- `fast` punching uses a 500ms phase-shifted prediction band whose window
  advances with `rate × elapsed`, covering up to an 8192 offset.
- Each worker socket always reuses the control, one low-offset anchor and one
  phased moving target, reducing the mapping fan-out of destination-dependent
  NAT and the risk of carrier UDP rate limiting.

Regular NAT still uses the original v7.1 strategy:

- The old `PROBE/JOIN/BANKS` server protocol is kept, so old v6 clients still
  work.
- v7 creates and keeps one set of UDP sockets per round; measurement, punching
  and confirmation all use the same set.
- The same socket probes the server's A/B ports separately to tell stable
  mappings apart from destination-port-dependent mappings.
- It fits a single bank or dual banks to the sampled ports and recognizes the
  `volatile` model formed by large bank drift, single/dual-bank switching and
  abrupt bank-gap changes.
- Rate only sets candidate priority and no longer replaces fixed coverage;
  from GO on, each worker socket covers in parallel the control, low offsets
  1–64, mid offsets 65–256, and the prediction band / volatile tail band
  257–384.
- Borrowing from EasyTier: both ends finish calibration and reporting first,
  then the server synchronizes GO; all sockets first concentrate on
  high-probability ports, then scan candidate windows in slices.
- As soon as any socket receives a valid `P7`, it replies `A7` from that same
  socket to the packet's real source endpoint and locks the socket without
  recreating the mapping.
- P7/A7 carry the sending worker, destination port, lane, offset and tick, so
  a first hit directly reveals which candidate class took effect; duplicate
  peer models and duplicate A7s no longer pollute the log.
- The punching window is 7 seconds, and only about 3 seconds of confirmation
  traffic is kept after success.
- Each run writes both a human-readable `.log` and a structured `.jsonl`.

## Files

- `natpunch.c`: Windows v7 client.
- `natpunch-server.py`: v7 coordination server compatible with v6.
- `analyze_logs.py`: summarizes or compares one to two v7 JSONL files.
- `legacy/v7.1/`: v7.1 client and server source kept byte-for-byte before the
  upgrade.
- `legacy/v7.2/`: v7.2 client and server source kept byte-for-byte before the
  upgrade.
- `legacy/v7.3/`: v7.3 client, server and build notes kept byte-for-byte before
  the upgrade.
- `legacy/v7.0/`: v7.0 client and server source.
- `legacy/natpunch-v6.c`: the v6 client baseline recovered from a Claude
  temporary directory.

SHA-256 of the recovered v6 original file:
`BF0DFB50B1CD2E61B16D87BDD9364184601EB2642A016489A23E2EC1076CEB8A`.

## Build

```powershell
$env:Path = "$env:USERPROFILE\mingw64\mingw64\bin;$env:Path"
gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.3.1.exe natpunch.c -lws2_32
```

Model self-test:

```powershell
.\natpunch-v7.3.1.exe --self-test
```

## Run

Server:

```bash
python3 natpunch-server.py server --bind 0.0.0.0 --port-a 21001 --port-b 21002
```

Both ends use the same room number; set the server address to the machine
running the server above:

```powershell
.\natpunch-v7.3.1.exe client vps.example.com 房间号
.\打洞测试.bat vps.example.com 房间号
```

Optional arguments:

- `--sockets N`: number of worker sockets, default 25, range 4–48.
- `--duration SEC`: total experiment time, default 180 seconds.
- `--log-dir PATH`: log directory, default
  `%LocalAppData%\MikuN2N\logs\natpunch`.
- `--no-pause`: do not wait for Enter at the end, suitable for automated tests.

## Logs

Each run produces:

- `natpunch-v7.3.1-时间-PID.log`: full copy of the console.
- `natpunch-v7.3-时间-PID.jsonl`: one JSON event per line.

Key events include:

- `session_start`: version, client ID, arguments and local port.
- `calibration`: A/B reply counts, mapping reuse ratio, RTT, bank, step,
  dispersion, cross-round rate and confidence.
- `peer_model`: the peer's reported model.
- `go`: attempt ID, GO delay and actual start deviation.
- `spray_progress` / `spray_summary`: per-second and whole-round packet counts,
  error counts and one-way hits.
- `peer_packet`: the local socket, real source, lane, offset, destination port
  and tick of the first hit.
- `success` / `attempt_failed`: success latency or the failed round summary.

`.jsonl` does not record the room number itself, only its FNV-1a tag.

Analyze one or both ends' logs:

```powershell
python .\analyze_logs.py .\A.jsonl
python .\analyze_logs.py .\A.jsonl .\B.jsonl
```

Two-end analysis reports directly whether the GO attempts are aligned, and
highlights dual banks, fast increment, probe loss, multiple public IPs, one-way
hits and the final winning socket.

## Limits

v7 improves the success probability of predictable, partially predictable and
dual-bank NAT4. When both ends use truly random or destination-hashed mappings,
there is no reliable pure-UDP guaranteed-success method, and production systems
should still keep relay.
