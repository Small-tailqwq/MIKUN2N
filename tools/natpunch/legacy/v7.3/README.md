# natpunch v7.3

> [English](README.md) | [简体中文](README.zh.md)

A UDP NAT4 (address- and port-dependent mapping) hole-punching experiment tool for
Windows. The source is kept permanently in the MikuN2N repository, so it no longer
ends up only as a temp directory or a bare binary.

## v7.3 Strategy

v7.3 is the final focused round of experiments for fast-cycle mobile CGNAT:

- `fast-target` runs on the regular side: it continuously dense-scans the 385-port
  static prediction band at GO time, covering the whole band about once every 0.6
  seconds, while also keeping the moving lane that advances over time.
- `fast-sender` is enabled only when the local side is itself fast, and keeps
  maintaining the three low-fan-out target classes: control, a fixed anchor, and a
  phased moving target.
- Anchor offset, dual-bank selection, and the static/moving arrangement are all
  salted with the attempt value; failed retries explore new relative ports instead
  of repeating v7.2's fixed blind spots.

The following model capabilities implemented in v7.2 are retained:

v7.2 retains v7.1's validated layered scanning for regular NAT and adds a separate
`fast` path for the high-speed increments, fast cycling, and intermittent packet
loss common to mobile traffic:

- A single bank enters `fast-cycle` once its sustained rate reaches 120 ports/second.
- When probes are insufficient, it keeps using the last trusted bank, ring
  direction, and rate instead of falling back to the distorted control model; after
  sampling resumes, it uses ring differences across complete time intervals.
- The `fast` punch uses a 500 ms phase-shifted prediction band; the window advances
  with `rate × elapsed`, covering up to 8192 offset.
- Each worker socket keeps reusing a control, one low-offset anchor, and one phased
  moving target, lowering the mapping fan-out for target-dependent NAT and the risk
  of carrier UDP rate limiting.

Regular NAT still uses the original v7.1 strategy:

- The legacy server `PROBE/JOIN/BANKS` protocol is retained, so old v6 clients
  continue to work.
- v7 creates and keeps one set of UDP sockets per round; measurement, punching, and
  confirmation all use the same socket set.
- The same socket probes the server's A/B ports separately, telling a stable
  mapping apart from a target-port-dependent mapping.
- It fits single-bank/dual-bank models to the sampled ports and recognizes the
  `volatile` model produced by large bank drift, single/dual-bank switching, and
  abrupt bank-spacing changes.
- Rate is used only for candidate priority, no longer as a substitute for fixed
  coverage; each worker socket covers in parallel, from GO onward, the control, low
  offset 1-64, mid offset 65-256, and the prediction/volatile tail band 257-384.
- Borrowed from EasyTier: both sides finish calibration and reporting first, then
  the server synchronizes GO; all sockets first concentrate fire on the
  high-probability ports, then scan the candidate windows in slices.
- When any socket receives a valid `P7`, it immediately replies `A7` from that same
  socket to the packet's real source endpoint and locks the socket, without
  recreating the mapping.
- P7/A7 carry the sending worker, target port, lane, offset, and tick, so the first
  hit directly reveals which candidate class worked; duplicate peer models and
  duplicate A7 replies no longer pollute the logs.
- The punching window is 7 seconds, and after success only about 3 seconds of
  confirmation traffic is kept.
- Each run writes both a human-readable `.log` and a structured `.jsonl`.

## Files

- `natpunch.c`: Windows v7 client.
- `natpunch-server.py`: v7 coordination server, compatible with v6.
- `analyze_logs.py`: summarizes or compares one or two v7 JSONL files.
- `legacy/v7.1/`: v7.1 client and server sources preserved byte-for-byte before the
  upgrade.
- `legacy/v7.2/`: v7.2 client and server sources preserved byte-for-byte before the
  upgrade.
- `legacy/v7.0/`: v7.0 client and server sources.
- `legacy/natpunch-v6.c`: the v6 client baseline recovered from a Claude temp
  directory.

SHA-256 of the recovered v6 original file:
`BF0DFB50B1CD2E61B16D87BDD9364184601EB2642A016489A23E2EC1076CEB8A`.

## Building

```powershell
$env:Path = "$env:USERPROFILE\mingw64\mingw64\bin;$env:Path"
gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.3.exe natpunch.c -lws2_32
```

Model self-test:

```powershell
.\natpunch-v7.3.exe --self-test
```

## Running

Server:

```bash
python3 natpunch-server.py server --bind 0.0.0.0 --port-a 21001 --port-b 21002
```

Both ends use the same room name:

```powershell
.\natpunch-v7.3.exe client vps.example.com 房间号
```

Optional arguments:

- `--sockets N`: number of worker sockets, default 25, range 4-48.
- `--duration SEC`: total experiment time, default 180 seconds.
- `--log-dir PATH`: log directory. Defaults to
  `%LocalAppData%\MikuN2N\logs\natpunch`.
- `--no-pause`: does not wait for Enter at the end; suitable for automated testing.

## Logs

Each run produces:

- `natpunch-v7.3-time-PID.log`: a complete copy of the console.
- `natpunch-v7.3-time-PID.jsonl`: one JSON event per line.

Key events include:

- `session_start`: version, client ID, arguments, and local port.
- `calibration`: A/B reply counts, mapping reuse ratio, RTT, bank, step, dispersion,
  cross-round rate, and confidence.
- `peer_model`: the peer's reported model.
- `go`: attempt ID, GO delay, and actual start deviation.
- `spray_progress` / `spray_summary`: per-second and per-round packet counts, error
  counts, and one-way hits.
- `peer_packet`: the first-hit local socket, real source, lane, offset, target port,
  and tick.
- `success` / `attempt_failed`: success latency or a failed-round summary.

`.jsonl` does not record the room name itself, only its FNV-1a tag.

Analyze one-sided or two-sided logs:

```powershell
python .\analyze_logs.py .\A.jsonl
python .\analyze_logs.py .\A.jsonl .\B.jsonl
```

Two-sided analysis directly reports whether the GO attempts are aligned, and
highlights dual banks, fast increments, lost probes, multiple public IPs, one-way
hits, and the final winning socket.

## Limitations

v7 improves the success probability for predictable, partially predictable, and
dual-bank NAT4. When both sides use truly random or target-hash mapping, there is no
stable pure-UDP method that is guaranteed to succeed, and production systems should
still keep relaying.
