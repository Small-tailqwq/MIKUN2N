#!/usr/bin/env python3
"""Attribute outbound public traffic to remote endpoints and local processes.

Reads only IP and transport headers on the external interface: no payload, no
content, no new listening port. Snapshots `ss` and `conntrack` while capturing so
each remote endpoint can be tied to a local process or container address. Runs for
a bounded window and appends one report per run, so it can be scheduled around the
hour where unexplained egress shows up.
"""
import argparse
import datetime as dt
import json
import re
import signal
import socket
import struct
import subprocess
import time
from collections import defaultdict
from pathlib import Path

DEFAULT_LOG = "/var/log/mikun2n-ghost/ghost.log"
DEFAULT_JSON = "/var/log/mikun2n-ghost/ghost.jsonl"
# Ranges the cloud bill does not meter; 224/4 is link-local multicast.
NON_PUBLIC_V4 = ((0x00000000, 8), (0x0A000000, 8), (0x64400000, 10), (0x7F000000, 8),
                 (0xA9FE0000, 16), (0xAC100000, 12), (0xC0A80000, 16), (0xE0000000, 4))
SS_RE = re.compile(r'users:\(\("([^"]+)",pid=(\d+)')
CONN_RE = re.compile(r'src=(\S+) dst=(\S+) sport=(\d+) dport=(\d+)')
PROTO = {1: "icmp", 6: "tcp", 17: "udp", 58: "icmp6"}


def external_interface():
    for row in Path("/proc/net/route").read_text().splitlines()[1:]:
        fields = row.split()
        if len(fields) >= 4 and fields[1] == "00000000":
            return fields[0]
    return "eth0"


def size(value):
    for unit in ("B", "KiB", "MiB", "GiB"):
        if abs(value) < 1024 or unit == "GiB":
            return f"{value:.2f} {unit}"
        value /= 1024


def ipv4_public(packed):
    value = struct.unpack("!I", packed)[0]
    for net, bits in NON_PUBLIC_V4:
        if value & (0xFFFFFFFF << (32 - bits) & 0xFFFFFFFF) == net:
            return False
    return True


def ipv6_public(packed):
    if packed[:12] == b"\x00" * 10 + b"\xff\xff":
        return ipv4_public(packed[12:])
    return not (packed[0] == 0xFE and packed[1] & 0xC0 == 0x80) \
        and not (packed[0] & 0xFE == 0xFC) and packed != b"\x00" * 15 + b"\x01"


def text(address):
    return socket.inet_ntop(socket.AF_INET if len(address) == 4 else socket.AF_INET6, address)


def parse(packet):
    """(proto, src, dst, sport, dport, ip_length, public_src, public_dst) or None."""
    if not packet:
        return None
    version = packet[0] >> 4
    if version == 4 and len(packet) >= 20:
        ihl = (packet[0] & 0x0F) * 4
        proto, src, dst = packet[9], packet[12:16], packet[16:20]
        length = struct.unpack("!H", packet[2:4])[0]
        public_src, public_dst = ipv4_public(src), ipv4_public(dst)
    elif version == 6 and len(packet) >= 40:
        ihl = 40
        proto, src, dst = packet[6], packet[8:24], packet[24:40]
        length = 40 + struct.unpack("!H", packet[4:6])[0]
        public_src, public_dst = ipv6_public(src), ipv6_public(dst)
    else:
        return None
    sport = dport = None
    if proto in (6, 17) and len(packet) >= ihl + 4:
        sport, dport = struct.unpack("!HH", packet[ihl:ihl + 4])
    return (PROTO.get(proto, str(proto)), src, dst, sport, dport, length, public_src, public_dst)


def ss_map():
    """{(proto, peer_ip, peer_port): who} and {(proto, local_port): who}."""
    peers, locals_ = {}, {}
    try:
        out = subprocess.run(["ss", "-tunap"], capture_output=True, text=True, timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return peers, locals_
    for line in out.splitlines()[1:]:
        fields = line.split()
        match = SS_RE.search(line)
        if len(fields) < 6 or not match:
            continue
        proto, local, peer = fields[0], fields[4], fields[5]
        who = f"{match.group(1)}/{match.group(2)}"
        if ":" in local:
            locals_[(proto, local.rsplit(":", 1)[1])] = who
        if peer != "*:*" and ":" in peer:
            host, port = peer.rsplit(":", 1)
            peers[(proto, host.strip("[]"), port)] = who
    return peers, locals_


def conntrack_map():
    """{(proto, remote_ip, remote_port): original source address} for NATed flows."""
    mapping = {}
    try:
        out = subprocess.run(["conntrack", "-L"], capture_output=True, text=True, timeout=15).stdout
    except (OSError, subprocess.SubprocessError):
        return mapping
    for line in out.splitlines():
        fields = CONN_RE.findall(line)
        if len(fields) < 2:
            continue
        proto, original, reply = line.split()[0], fields[0], fields[1]
        mapping[(proto, original[1], original[3])] = original[0]
        mapping[(proto, reply[1], reply[3])] = original[0]
    return mapping


def netdev(interface):
    for row in Path("/proc/net/dev").read_text().splitlines()[2:]:
        name, _, rest = row.partition(":")
        if name.strip() == interface:
            values = rest.split()
            return int(values[0]), int(values[8])
    return 0, 0


def rdns(address):
    try:
        socket.setdefaulttimeout(2)
        return socket.gethostbyaddr(address)[0]
    except OSError:
        return ""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--interface", default=external_interface())
    parser.add_argument("--seconds", type=int, default=1200)
    parser.add_argument("--sample", type=int, default=30, help="ss/conntrack sampling interval")
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--log", default=DEFAULT_LOG)
    parser.add_argument("--json-log", default=DEFAULT_JSON)
    parser.add_argument("--no-rdns", action="store_true")
    args = parser.parse_args()

    Path(args.log).parent.mkdir(parents=True, exist_ok=True, mode=0o750)
    if Path(args.log).exists() and Path(args.log).stat().st_size > 2_000_000:
        Path(args.log).replace(args.log + ".1")

    flows = defaultdict(lambda: [0, 0, None])
    local_ports = defaultdict(set)
    private = defaultdict(int)
    ingress = defaultdict(int)
    egress_bytes = private_bytes = ingress_bytes = 0
    timeline = []
    peers, locals_, conntrack = {}, {}, {}

    sock = socket.socket(socket.AF_PACKET, socket.SOCK_DGRAM, socket.htons(3))
    sock.bind((args.interface, 0))
    sock.settimeout(1.0)

    started = time.time()
    stop = {"flag": False}
    signal.signal(signal.SIGTERM, lambda *_: stop.__setitem__("flag", True))
    signal.signal(signal.SIGINT, lambda *_: stop.__setitem__("flag", True))

    def sample():
        nonlocal peers, locals_, conntrack
        peers, locals_ = ss_map()
        conntrack = conntrack_map()
        rx, tx = netdev(args.interface)
        timeline.append((int(time.time()), tx, rx))

    sample()
    first_tx, first_rx = timeline[0][1], timeline[0][2]
    next_sample = time.time() + args.sample

    while not stop["flag"] and time.time() - started < args.seconds:
        try:
            packet = sock.recv(65535)
        except socket.timeout:
            packet = None
        except OSError:
            break
        if packet:
            parsed = parse(packet)
            if parsed:
                proto, src, dst, sport, dport, length, public_src, public_dst = parsed
                if public_dst:
                    key = (proto, text(dst), dport)
                    entry = flows[key]
                    entry[0] += length
                    entry[1] += 1
                    if entry[2] is None:
                        entry[2] = sport
                    if sport is not None:
                        local_ports[key].add(sport)
                    egress_bytes += length
                elif public_src:
                    ingress[text(src)] += length
                    ingress_bytes += length
                else:
                    private[(proto, text(dst), dport)] += length
                    private_bytes += length
        if time.time() >= next_sample:
            sample()
            next_sample = time.time() + args.sample

    sock.close()
    ended = time.time()
    final_rx, final_tx = netdev(args.interface)

    def owner(key):
        proto, dst, dport = key
        who = peers.get((proto, dst, str(dport)))
        if not who:
            for port in local_ports[key]:
                who = locals_.get((proto, str(port)))
                if who:
                    break
        source = conntrack.get((proto, dst, str(dport)))
        if source and not source.startswith(("172.31.", "10.251.")):
            who = f"{who or 'conntrack-only'} via {source}"
        return who or "-"

    top = sorted(flows.items(), key=lambda kv: kv[1][0], reverse=True)[:args.top]
    samples = [(stamp, timeline[i + 1][1] - timeline[i][1])
               for i, (stamp, _, _) in enumerate(timeline[:-1])]
    busiest = sorted(samples, key=lambda item: -item[1])[:5]
    lines = [f"===== {dt.datetime.fromtimestamp(started):%Y-%m-%d %H:%M:%S} .. "
             f"{dt.datetime.fromtimestamp(ended):%H:%M:%S} ({int(ended - started)}s, {args.interface}) =====",
             f"sampled {len(timeline)} ss/conntrack snapshots",
             f"egress public {size(egress_bytes)} in {sum(v[1] for v in flows.values()):,} pkts | "
             f"egress private/VPC {size(private_bytes)} | ingress public {size(ingress_bytes)}",
             f"eth0 counters: tx {size(final_tx - first_tx)}, rx {size(final_rx - first_rx)} "
             f"(tx minus captured public egress = {size(max(0, final_tx - first_tx - egress_bytes))}, "
             f"includes VPC traffic and L2 framing)",
             "busiest sampling intervals (host tx): "
             + ", ".join(f"{dt.datetime.fromtimestamp(stamp):%H:%M:%S} {size(value)}"
                         for stamp, value in busiest),
             "top public egress endpoints:"]
    for (proto, dst, dport), (bytes_, packets, _) in top:
        name = "" if args.no_rdns else rdns(dst)
        lines.append(f"  {size(bytes_):>10} {packets:>8} pkts  {proto:<5} {dst}:{dport}  "
                     f"{owner((proto, dst, dport))}" + (f"  ({name})" if name else ""))
    if private_bytes:
        lines.append("top private/VPC egress endpoints:")
        for (proto, dst, dport), bytes_ in sorted(private.items(), key=lambda kv: -kv[1])[:5]:
            lines.append(f"  {size(bytes_):>10}  {proto:<5} {dst}:{dport}")
    if ingress_bytes:
        lines.append("top public ingress sources:")
        for dst, bytes_ in sorted(ingress.items(), key=lambda kv: -kv[1])[:5]:
            lines.append(f"  {size(bytes_):>10}  {dst}")
    report = "\n".join(lines)
    print(report)
    with open(args.log, "a", encoding="utf-8") as handle:
        handle.write(report + "\n")
    with open(args.json_log, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(dict(
            start=dt.datetime.fromtimestamp(started).isoformat(timespec="seconds"),
            end=dt.datetime.fromtimestamp(ended).isoformat(timespec="seconds"),
            seconds=int(ended - started), interface=args.interface, egress_public=egress_bytes,
            egress_private=private_bytes, ingress_public=ingress_bytes,
            host_tx=final_tx - first_tx, host_rx=final_rx - first_rx,
            intervals=[[stamp, value] for stamp, value in samples],
            endpoints=[dict(proto=proto, dst=dst, port=dport, bytes=bytes_, packets=packets,
                            process=owner((proto, dst, dport)))
                       for (proto, dst, dport), (bytes_, packets, _) in top],
        ), ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
