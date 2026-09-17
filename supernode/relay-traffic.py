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
        CREATE TABLE IF NOT EXISTS host_previous (
            interface TEXT PRIMARY KEY, generation TEXT, rx INTEGER, tx INTEGER);
        CREATE TABLE IF NOT EXISTS host_hourly (
            hour INTEGER, interface TEXT, rx INTEGER, tx INTEGER,
            PRIMARY KEY(hour,interface));
    """)
    return db


def collect_host(db, interface=None):
    names = {interface} if interface else {
        row.split()[0] for row in Path('/proc/net/route').read_text().splitlines()[1:]
        if len(row.split()) >= 4 and row.split()[1] == '00000000'}
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    sampled = int(time.time())
    values = []
    for name in sorted(names):
        if not name or '/' in name or name in ('.', '..', 'lo'):
            raise ValueError('invalid external interface')
        root = Path('/sys/class/net') / name
        generation = boot + ':' + (root / 'ifindex').read_text().strip()
        values.append((name, generation, int((root / 'statistics/rx_bytes').read_text()),
                       int((root / 'statistics/tx_bytes').read_text())))
    with db:
        for name, generation, rx, tx in values:
            old = db.execute('SELECT generation,rx,tx FROM host_previous WHERE interface=?', (name,)).fetchone()
            if old and old[0] == generation and rx >= old[1] and tx >= old[2]:
                db.execute('''INSERT INTO host_hourly VALUES (?,?,?,?)
                    ON CONFLICT(hour,interface) DO UPDATE SET rx=rx+excluded.rx,tx=tx+excluded.tx''',
                           (sampled // 3600 * 3600, name, rx-old[1], tx-old[2]))
            db.execute('INSERT OR REPLACE INTO host_previous VALUES (?,?,?,?)', (name,generation,rx,tx))
        if values:
            db.execute("INSERT OR IGNORE INTO state VALUES ('host_first_sample',?)", (str(sampled),))
            db.execute("INSERT OR REPLACE INTO state VALUES ('host_last_sample',?)", (str(sampled),))
        db.execute('DELETE FROM host_hourly WHERE hour < ?', (sampled // 3600 * 3600 - 30*86400,))


def collect(db, path, interface=None):
    # Host totals continue advancing even while n3n's management socket is down.
    collect_host(db, interface)
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
        until = args.until or int(time.time()) + 1
        since = args.since or (until - args.hours * 3600) // 3600 * 3600
        if since >= until: raise ValueError('since must precede until')
        rows = [dict(zip(("community", "src", "dst", "kind", "bytes", "packets"), row)) for row in
                db.execute("""SELECT community,src,dst,kind,SUM(bytes),SUM(packets)
                    FROM hourly WHERE hour>=? AND hour<? GROUP BY community,src,dst,kind ORDER BY SUM(bytes) DESC""", (since,until))]
        labels = {(c, m): (n, ip) for c, m, n, ip in db.execute("SELECT community,mac,name,ip FROM peers")}
        hours = [dict(hour=utc(h), bytes=b, sends=p) for h, b, p in db.execute(
            "SELECT hour,SUM(bytes),SUM(packets) FROM hourly WHERE hour>=? AND hour<? AND kind=0 GROUP BY hour", (since,until))]
        host = [dict(interface=n, rx_bytes=rx, tx_bytes=tx) for n, rx, tx in db.execute(
            'SELECT interface,SUM(rx),SUM(tx) FROM host_hourly WHERE hour>=? AND hour<? GROUP BY interface', (since,until))]
    db.close()
    for row in rows:
        row["category"] = KINDS[row["kind"]]
        for end in ("src", "dst"):
            row[end + "_name"], row[end + "_ip"] = labels.get((row["community"], row[end]), ("", ""))
    data = dict(since_utc=utc(since), last_sample_utc=utc(state.get("last_sample", 0)),
                until_utc=utc(until), state=state, flows=rows, hourly=hours, host=host)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return
    print(f"Period: {data['since_utc']} .. {data['until_utc']} (hour buckets, UTC)")
    print(f"Collection began: {utc(state.get('first_sample', 0))}; retained: 30 days")
    print(f"Last sample age: {max(0, int(time.time()) - int(state.get('last_sample', 0)))}s; "
          f"observed restarts: {state.get('restarts', 0)}; sampling gaps >180s: {state.get('sampling_gaps', 0)}")
    totals = {kind: sum(r["bytes"] for r in rows if r["kind"] == kind) for kind in KINDS}
    for kind in range(6):
        print(f"{KINDS[kind]:22} {size(totals[kind]):>14}")
    print(f"Control / framing / partial sends (approx): {size(max(0, totals[0] - sum(totals[k] for k in range(1, 6))))}")
    print(f"Failed sends: {sum(r['packets'] for r in rows if r['kind'] == -1)}; "
          f"tracked flows: {state.get('flow_count', 0)}/{state.get('flow_limit', 0)}")
    print("\nHost interface totals (all processes; not a substitute for the cloud bill):")
    if 'host_first_sample' in state:
        print(f"Host coverage: {utc(state['host_first_sample'])} .. {utc(state['host_last_sample'])}; earlier traffic unavailable")
    for item in host:
        print(f"{item['interface']}: OUT {size(item['tx_bytes'])} ({item['tx_bytes']/1e9:.6f} GB), IN {size(item['rx_bytes'])}")

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


def hour_timestamp(value):
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None or parsed.minute or parsed.second or parsed.microsecond:
        raise argparse.ArgumentTypeError('use an ISO timestamp at a whole hour with timezone, e.g. 2026-09-17T14:00:00+08:00')
    return int(parsed.timestamp())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("collect", "report"))
    parser.add_argument("--db", default=DEFAULT_DB)
    parser.add_argument("--socket", default=DEFAULT_SOCKET)
    parser.add_argument("--interface", help="External interface; default: interfaces with an IPv4 default route")
    parser.add_argument("--since", type=hour_timestamp, help="Inclusive hour boundary with timezone")
    parser.add_argument("--until", type=hour_timestamp, help="Exclusive hour boundary with timezone")
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
                collect(db, args.socket, args.interface)
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
