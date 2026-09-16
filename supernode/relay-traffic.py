#!/usr/bin/env python3
"""Collect local supernode counters and report retained hourly outbound traffic."""
import argparse
import datetime as dt
import http.client
import json
import os
from pathlib import Path
import signal
import socket
import sqlite3
import struct
import time

KINDS = {0: "all-egress", 1: "pSp", 2: "broadcast-copy", 3: "federation-unicast",
         4: "federation-flood", 5: "flow-overflow", -1: "send-errors"}
DEFAULT_DB = "/var/lib/mikun2n-traffic/traffic.sqlite3"
DEFAULT_SOCKET = "/run/n3n/mikun2n-supernode/mgmt"


def rpc(path, method, offset=0):
    conn = http.client.HTTPConnection("localhost", timeout=5)
    conn.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    conn.sock.settimeout(5)
    try:
        conn.sock.connect(path)
        pid, _, _ = struct.unpack("3i", conn.sock.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        conn.request("POST", "/v1", json.dumps({"jsonrpc": "2.0", "id": 1,
                     "method": method, "params": {"limit": 8, "offset": offset}}),
                     {"Content-Type": "application/json"})
        response = conn.getresponse()
        body = response.read(131073)
        if response.status != 200 or len(body) > 131072:
            raise RuntimeError("management response failed or exceeded size limit")
        result = json.loads(body)
        if "error" in result:
            raise RuntimeError("management returned a JSON-RPC error")
        return pid, result["result"]
    finally:
        conn.close()


def snapshot(path):
    pid, first = rpc(path, "get_relay_stats")
    if first["schema"] != 1:
        raise RuntimeError("unsupported relay counter schema")
    rows = list(first["flows"])
    target = first["flow_count"]
    while len(rows) < target:
        page_pid, page = rpc(path, "get_relay_stats", len(rows))
        if page_pid != pid or page["started_at"] != first["started_at"] or not page["flows"]:
            raise RuntimeError("supernode restarted during counter snapshot")
        rows.extend(page["flows"])
    # The final read detects a restart after a one-page snapshot as well.
    final_pid, final = rpc(path, "get_relay_stats")
    if final_pid != pid or final["started_at"] != first["started_at"]:
        raise RuntimeError("supernode restarted during counter snapshot")
    peers = []
    try:
        for offset in range(0, 8192, 8):
            peer_pid, page = rpc(path, "get_edges", offset)
            if peer_pid != pid:
                raise RuntimeError("supernode restarted during peer snapshot")
            peers.extend(p for p in page if p.get("mode") == "sn")
            if len(page) < 8:
                break
    except (OSError, ValueError, KeyError, RuntimeError, http.client.HTTPException) as exc:
        # A malformed legacy nickname must not discard valid traffic counters.
        print(f"peer labels unavailable: {type(exc).__name__}", flush=True)
    boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
    return f"{boot}:{pid}:{first['started_at']}", first, rows[:target], peers


def open_db(path):
    os.umask(0o077)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path, timeout=20)
    db.execute("PRAGMA journal_mode=WAL")
    db.executescript("""
        CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS previous (
            key TEXT PRIMARY KEY, bytes INTEGER NOT NULL, packets INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS hourly (
            hour INTEGER, community TEXT, src TEXT, dst TEXT, kind INTEGER,
            bytes INTEGER NOT NULL, packets INTEGER NOT NULL,
            PRIMARY KEY(hour, community, src, dst, kind));
        CREATE TABLE IF NOT EXISTS peers (
            community TEXT, mac TEXT, name TEXT, ip TEXT, seen INTEGER,
            PRIMARY KEY(community, mac));
    """)
    return db


def collect(db, path):
    generation, meta, flows, peers = snapshot(path)
    sampled = meta["sampled_at"]
    hour = sampled // 3600 * 3600
    flows += [dict(community_hex="", src="", dst="", kind=kind, bytes=b, packets=p)
              for kind, b, p in [(0, meta["out_bytes"], meta["out_sends"]),
                                 (5, meta["overflow_bytes"], meta["overflow_packets"]),
                                 (-1, 0, meta["send_errors"])]]
    with db:
        state = dict(db.execute("SELECT key,value FROM state"))
        changed = state.get("generation") != generation
        if changed:
            db.execute("DELETE FROM previous")
            if "generation" in state:
                print("supernode restarted; uncollected tail of previous process cannot be recovered", flush=True)
        previous = {} if changed else {
            k: (b, p) for k, b, p in db.execute("SELECT key,bytes,packets FROM previous")}
        for flow in flows:
            dims = (flow["community_hex"], flow["src"].upper(), flow["dst"].upper(), flow["kind"])
            key = json.dumps(dims, separators=(",", ":"))
            old_b, old_p = previous.get(key, (0, 0))
            b, p = int(flow["bytes"]), int(flow["packets"])
            if b < old_b or p < old_p:
                raise RuntimeError("counter decreased within one supernode process")
            if b != old_b or p != old_p:
                db.execute("""INSERT INTO hourly VALUES (?,?,?,?,?,?,?)
                    ON CONFLICT(hour,community,src,dst,kind) DO UPDATE SET
                    bytes=bytes+excluded.bytes, packets=packets+excluded.packets""",
                           (hour, *dims, b - old_b, p - old_p))
            db.execute("INSERT OR REPLACE INTO previous VALUES (?,?,?)", (key, b, p))
        for peer in peers:
            community = peer.get("community", "").encode("utf-8")[:20].ljust(20, b"\0").hex()
            db.execute("INSERT OR REPLACE INTO peers VALUES (?,?,?,?,?)",
                       (community, peer.get("macaddr", "").upper(), peer.get("desc", ""),
                        peer.get("ip4addr", ""), sampled))
        restart_count = int(state.get("restarts", "0")) + int(changed and "generation" in state)
        gap = sampled - int(state.get("last_sample", str(sampled)))
        gap_count = int(state.get("sampling_gaps", "0")) + int(gap > 180)
        for key, value in {"generation": generation, "last_sample": sampled,
                           "first_sample": state.get("first_sample", sampled),
                           "first_started_at": state.get("first_started_at", meta["started_at"]),
                           "restarts": restart_count, "sampling_gaps": gap_count,
                           "flow_count": meta["flow_count"], "flow_limit": meta["flow_limit"]}.items():
            db.execute("INSERT OR REPLACE INTO state VALUES (?,?)", (key, str(value)))
        cutoff = hour - 30 * 86400
        db.execute("DELETE FROM hourly WHERE hour < ?", (cutoff,))
        db.execute("DELETE FROM peers WHERE seen < ?", (cutoff,))


def utc(timestamp):
    return dt.datetime.fromtimestamp(int(timestamp), dt.timezone.utc).isoformat(timespec="seconds")


def size(value):
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if abs(value) < 1024 or unit == "TiB":
            return f"{value:.2f} {unit}"
        value /= 1024


def report(args):
    db = sqlite3.connect(Path(args.db).resolve().as_uri() + "?mode=ro", uri=True)
    with db:
        state = dict(db.execute("SELECT key,value FROM state"))
        since = int(time.time() - args.hours * 3600) // 3600 * 3600
        rows = [dict(zip(("community", "src", "dst", "kind", "bytes", "packets"), row)) for row in
                db.execute("""SELECT community,src,dst,kind,SUM(bytes),SUM(packets)
                    FROM hourly WHERE hour>=? GROUP BY community,src,dst,kind ORDER BY SUM(bytes) DESC""", (since,))]
        labels = {(c, m): (n, ip) for c, m, n, ip in db.execute("SELECT community,mac,name,ip FROM peers")}
        hours = [dict(hour=utc(h), bytes=b, sends=p) for h, b, p in db.execute(
            "SELECT hour,SUM(bytes),SUM(packets) FROM hourly WHERE hour>=? AND kind=0 GROUP BY hour", (since,))]
    db.close()
    for row in rows:
        row["category"] = KINDS[row["kind"]]
        for end in ("src", "dst"):
            row[end + "_name"], row[end + "_ip"] = labels.get((row["community"], row[end]), ("", ""))
    data = dict(since_utc=utc(since), last_sample_utc=utc(state.get("last_sample", 0)),
                state=state, flows=rows, hourly=hours)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return
    print(f"Period: {data['since_utc']} .. {data['last_sample_utc']} (hour buckets, UTC)")
    print(f"Collection began: {utc(state.get('first_sample', 0))}; retained: 30 days")
    print(f"Last sample age: {max(0, int(time.time()) - int(state.get('last_sample', 0)))}s; "
          f"observed restarts: {state.get('restarts', 0)}; sampling gaps >180s: {state.get('sampling_gaps', 0)}")
    totals = {kind: sum(r["bytes"] for r in rows if r["kind"] == kind) for kind in KINDS}
    for kind in range(6):
        print(f"{KINDS[kind]:22} {size(totals[kind]):>14}")
    print(f"Control / framing / partial sends (approx): {size(max(0, totals[0] - sum(totals[k] for k in range(1, 6))))}")
    print(f"Failed sends: {sum(r['packets'] for r in rows if r['kind'] == -1)}; "
          f"tracked flows: {state.get('flow_count', 0)}/{state.get('flow_limit', 0)}")

    def identity(row, end):
        text = " ".join(filter(None, (row[end + "_name"], row[end + "_ip"], row[end])))
        return "".join(c if c.isprintable() else "?" for c in text)

    print("\nTop directed deliveries (broadcast bytes are charged once per recipient):")
    for row in [r for r in rows if 1 <= r["kind"] <= 4][:args.top]:
        group = row["community"][:8]
        print(f"{size(row['bytes']):>14} {row['packets']:>10} sends  {row['category']:20} "
              f"[{group}] {identity(row, 'src')} -> {identity(row, 'dst')}")
    talkers = {}
    for row in rows:
        if 1 <= row["kind"] <= 4:
            key = (row["community"], row["src"])
            entry = talkers.setdefault(key, [0, identity(row, "src")])
            entry[0] += row["bytes"]
    print("\nTop senders by induced server egress (includes fan-out):")
    for b, name in sorted(talkers.values(), reverse=True)[:args.top]:
        print(f"{size(b):>14}  {name}")
    if args.hourly:
        print("\nHourly server egress:")
        for hour in hours:
            print(f"{hour['hour']} {size(hour['bytes']):>14} {hour['sends']} sends")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("collect", "report"))
    parser.add_argument("--db", default=DEFAULT_DB)
    parser.add_argument("--socket", default=DEFAULT_SOCKET)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--hours", type=int, default=24, choices=range(1, 721), metavar="1..720")
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--hourly", action="store_true")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    if args.command == "report":
        report(args)
        return
    stop = False

    def shutdown(_signum, _frame):
        nonlocal stop
        stop = True

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)
    db = open_db(args.db)
    try:
        while not stop:
            start = time.monotonic()
            try:
                collect(db, args.socket)
                if args.once:
                    break
            except (OSError, ValueError, KeyError, RuntimeError, sqlite3.Error, http.client.HTTPException) as exc:
                if args.once:
                    raise
                print(f"collection failed: {type(exc).__name__}; retry in 60s", flush=True)
            while not stop and time.monotonic() - start < 60:
                time.sleep(1)
    finally:
        db.close()


if __name__ == "__main__":
    main()
