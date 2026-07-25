#!/usr/bin/env python3
"""Summarize and compare natpunch v7 JSONL traces."""

from __future__ import annotations

import argparse
import json
import pathlib
from collections import Counter


def load(path: pathlib.Path) -> list[dict]:
    events = []
    with path.open(encoding="utf-8") as stream:
        for number, line in enumerate(stream, 1):
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise SystemExit(f"{path}:{number}: JSON 无效: {exc}") from exc
    return events


def by_name(events: list[dict], name: str) -> list[dict]:
    return [event for event in events if event.get("event") == name]


def summarize(path: pathlib.Path, events: list[dict]) -> dict:
    starts = by_name(events, "session_start")
    calibrations = by_name(events, "calibration")
    gos = by_name(events, "go")
    samples = by_name(events, "probe_sample")
    successes = by_name(events, "success")
    summaries = by_name(events, "spray_summary")
    client = starts[0].get("client_id", "?") if starts else "?"
    print(f"\n{path.name}  client={client}")
    print(f"  校准={len(calibrations)}  GO={len(gos)}  成功={len(successes)}")
    for item in calibrations:
        display = dict(item)
        display["confidence"] = item.get("confidence", 0) / 10
        display["inherited"] = item.get("inherited", False)
        print(
            "  gen={generation} mode={mode} conf={confidence:.1f}% "
            "banks=[{bank1},{bank2}] step={step} spread={spread} "
            "rate={rate}/s rtt={median_rtt}ms valid={valid_a}/{valid_b} "
            "ip_changes={ip_changes} inherited={inherited}".format(**display)
        )
    warnings = []
    if any(item.get("mode") == "hard" for item in calibrations):
        warnings.append("出现 hard/random 或多公网 IP 模型")
    if any(item.get("mode") == "volatile" or item.get("volatile") for item in calibrations):
        warnings.append("检测到 bank 漂移/切换，已按 volatile 模型扩大固定覆盖")
    if any(item.get("bank1") != item.get("bank2") for item in calibrations):
        warnings.append("检测到双 bank")
    if any(item.get("rate", 0) >= 50 for item in calibrations):
        warnings.append("检测到快速递增（>=50 端口/秒）")
    if any(item.get("mode") == "fast" or item.get("fast_cycle") for item in calibrations):
        warnings.append("启用了 fast-cycle 历史保持与低扇出移动扫描")
    inherited = [item.get("generation") for item in calibrations if item.get("inherited")]
    if inherited:
        warnings.append(f"探针不足时沿用了最后可信模型：generations={inherited}")
    if any(
        item.get("valid_a", 0) < starts[0].get("workers", 0)
        or item.get("valid_b", 0) < starts[0].get("workers", 0)
        for item in calibrations
    ) if starts else False:
        warnings.append("部分校准探针丢失")
    peer_packets = by_name(events, "peer_packet")
    source_hits = Counter(
        (event.get("worker"), event.get("local_port"))
        for event in peer_packets
    )
    if source_hits:
        print(f"  命中 socket: {dict(source_hits)}")
        first = peer_packets[0]
        print(
            f"  首命中: kind={first.get('kind')} lane={first.get('lane', 'legacy')} "
            f"offset={first.get('offset', '?')} target={first.get('target_port', '?')} "
            f"peer_worker={first.get('peer_worker', first.get('acked_worker', '?'))}"
        )
    if successes:
        item = successes[0]
        print(
            f"  成功: attempt={item.get('attempt')} worker={item.get('worker')} "
            f"source={item.get('source')} latency={item.get('latency_ms')}ms"
        )
    elif summaries:
        one_way = [item.get("attempt") for item in summaries if item.get("one_way")]
        print(f"  未成功；单向可达 attempts={one_way or '无'}")
    fast_gos = [
        item for item in gos
        if item.get("strategy") in {"fast-target", "fast-sender"}
    ]
    if fast_gos:
        horizons = [item.get("range_hi") for item in fast_gos]
        static_widths = [item.get("static_width", 0) for item in fast_gos]
        strategies = Counter(item.get("strategy") for item in fast_gos)
        print(
            f"  fast-cycle GO={len(fast_gos)}，strategy={dict(strategies)}，"
            f"horizon={horizons}，static_width={static_widths}"
        )
    for warning in warnings:
        print(f"  警告: {warning}")
    return {
        "client": client,
        "attempts": [item.get("attempt") for item in gos],
        "success": bool(successes),
        "samples": len(samples),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="分析 natpunch v7 JSONL")
    parser.add_argument("logs", type=pathlib.Path, nargs="+")
    args = parser.parse_args()
    if len(args.logs) > 2:
        parser.error("一次最多比较两份日志")
    reports = [summarize(path, load(path)) for path in args.logs]
    if len(reports) == 2:
        left, right = reports
        print("\n双端协调检查")
        print(f"  A attempts={left['attempts']}")
        print(f"  B attempts={right['attempts']}")
        if left["attempts"] == right["attempts"]:
            print("  GO 序列一致")
        else:
            missing_left = sorted(set(right["attempts"]) - set(left["attempts"]))
            missing_right = sorted(set(left["attempts"]) - set(right["attempts"]))
            print(f"  GO 不一致：A 缺 {missing_left}，B 缺 {missing_right}")
        if not left["success"] and not right["success"]:
            print("  双端均未确认；优先检查 peer_packet 是否出现单向命中")


if __name__ == "__main__":
    main()
