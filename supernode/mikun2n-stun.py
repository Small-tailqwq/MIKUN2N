#!/usr/bin/env python3
"""IPv6 mapping observer for MikuN2N edges registered on this supernode.

Answers only tagged STUN Binding requests (RFC 8489 framing, SOFTWARE attribute
"MikuN2N/1 <mac>"), so it is not an open STUN service:

* Over IPv6, the MAC must belong to an edge currently registered on the local
  supernode (read from its management socket). The answer is the requester's
  XOR-MAPPED-ADDRESS, which lets an edge behind NAT66 advertise its public mapping.
* Over IPv4, a "MikuN2N/1 discover" request lets the client learn, before it starts
  the edge, whether this supernode offers the observer and at which IPv6 address.
  The answer carries OTHER-ADDRESS with that IPv6 address.

Every answer is at most as large as its request, so a spoofed source gains no
amplification, and each source prefix and MAC is rate limited.
"""
import argparse
import ipaddress
import json
import re
import socket
import struct
import sys
import time

COOKIE = 0x2112A442
BINDING_REQUEST, BINDING_SUCCESS = 0x0001, 0x0101
SOFTWARE, XOR_MAPPED, OTHER_ADDRESS = 0x8022, 0x0020, 0x802C
TAG = re.compile(rb"MikuN2N/1 (?:([0-9a-f]{12})|discover) *\Z")
BUCKET_SIZE, BUCKET_REFILL_S = 6, 5.0
GLOBAL_PER_SECOND = 200
EDGE_REFRESH_S, EDGE_MISS_REFRESH_S = 15.0, 2.0


class Registry:
    """MACs of edges registered on the local supernode, refreshed on demand."""

    def __init__(self, path):
        self.path, self.macs, self.loaded = path, set(), 0.0

    def query(self):
        body = json.dumps({"jsonrpc": "2.0", "method": "get_edges", "id": 1}).encode()
        request = (b"POST /v1 HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n"
                   b"Connection: close\r\nContent-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
            client.settimeout(2)
            client.connect(self.path)
            client.sendall(request)
            # n3n keeps the connection open after answering, so read by Content-Length.
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = client.recv(65536)
                if not chunk:
                    raise ValueError("truncated management response")
                data += chunk
            head, payload = data.split(b"\r\n\r\n", 1)
            length = re.search(rb"(?im)^content-length:\s*(\d+)\s*$", head)
            if not length:
                raise ValueError("management response without Content-Length")
            while len(payload) < int(length.group(1)):
                chunk = client.recv(65536)
                if not chunk:
                    raise ValueError("truncated management response")
                payload += chunk
        rows = json.loads(payload[:int(length.group(1))]).get("result", [])
        return {row["macaddr"].replace(":", "").lower() for row in rows if row.get("macaddr")}

    def contains(self, mac):
        now = time.monotonic()
        stale = now - self.loaded > EDGE_REFRESH_S
        if stale or (mac not in self.macs and now - self.loaded > EDGE_MISS_REFRESH_S):
            try:
                self.macs = self.query()
            except (OSError, ValueError, KeyError, IndexError):
                # Keep the last known set; an unreachable supernode must not open the service.
                pass
            self.loaded = now
        return mac in self.macs


class Limiter:
    def __init__(self):
        self.buckets, self.second, self.count = {}, 0, 0

    def allow(self, keys):
        now = time.monotonic()
        if int(now) != self.second:
            self.second, self.count = int(now), 0
        if self.count >= GLOBAL_PER_SECOND:
            return False
        if len(self.buckets) > 65536:
            self.buckets = {k: v for k, v in self.buckets.items() if now - v[1] < 600}
        for key in keys:
            tokens, stamp = self.buckets.get(key, (BUCKET_SIZE, now))
            if min(BUCKET_SIZE, tokens + (now - stamp) / BUCKET_REFILL_S) < 1:
                return False
        for key in keys:
            tokens, stamp = self.buckets.get(key, (BUCKET_SIZE, now))
            self.buckets[key] = (min(BUCKET_SIZE, tokens + (now - stamp) / BUCKET_REFILL_S) - 1, now)
        self.count += 1
        return True


def parse(data):
    """Return (transaction, mac or None for discover) for a well-formed tagged request."""
    if not 20 <= len(data) <= 256:
        return None
    kind, length, cookie = struct.unpack_from("!HHI", data)
    if kind != BINDING_REQUEST or cookie != COOKIE or length + 20 != len(data) or length & 3:
        return None
    tag, offset = None, 20
    while offset < len(data):
        if offset + 4 > len(data):
            return None
        attribute, size = struct.unpack_from("!HH", data, offset)
        end = offset + 4 + ((size + 3) & ~3)
        if end > len(data):
            return None
        if attribute == SOFTWARE:
            tag = data[offset + 4:offset + 4 + size]
        elif attribute < 0x8000:
            return None
        offset = end
    match = TAG.fullmatch(tag or b"")
    if not match:
        return None
    return data[8:20], (match.group(1).decode() if match.group(1) else None)


def address_attribute(kind, address, port, transaction, xor):
    ip = ipaddress.ip_address(address)
    raw = ip.packed
    if xor:
        port ^= COOKIE >> 16
        mask = struct.pack("!I", COOKIE) + transaction
        raw = bytes(b ^ m for b, m in zip(raw, mask))
    family = 1 if ip.version == 4 else 2
    value = struct.pack("!BBH", 0, family, port) + raw
    return struct.pack("!HH", kind, len(value)) + value


def answer(transaction, attributes):
    body = b"".join(attributes)
    return struct.pack("!HHI", BINDING_SUCCESS, len(body), COOKIE) + transaction + body


def main():
    parser = argparse.ArgumentParser(description="MikuN2N IPv6 mapping observer")
    parser.add_argument("--ipv6", required=True, help="global IPv6 address of this host")
    parser.add_argument("--port", type=int, default=3478)
    parser.add_argument("--mgmt", default="/run/n3n/mikun2n-supernode/mgmt")
    args = parser.parse_args()
    advertised = ipaddress.IPv6Address(args.ipv6)
    if not advertised.is_global:
        parser.error("--ipv6 must be a global address")

    registry, limiter = Registry(args.mgmt), Limiter()
    server = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
    server.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
    server.bind(("::", args.port))
    print(f"MikuN2N IPv6 observer ready on UDP {args.port}; advertising {advertised}", flush=True)
    answered = rejected = 0
    report = time.monotonic()
    while True:
        data, source = server.recvfrom(512)
        host, port = source[0], source[1]
        ip = ipaddress.ip_address(host)
        mapped_v4 = ip.version == 6 and ip.ipv4_mapped
        parsed = parse(data)
        reply = None
        if parsed:
            transaction, mac = parsed
            if mapped_v4 and mac is None:
                prefix = str(mapped_v4)
                if limiter.allow([prefix]):
                    reply = answer(transaction, [
                        address_attribute(XOR_MAPPED, mapped_v4, port, transaction, True),
                        address_attribute(OTHER_ADDRESS, advertised, args.port, transaction, False)])
            elif not mapped_v4 and mac is not None and ip.is_global:
                prefix = str(ipaddress.IPv6Network(f"{host}/64", strict=False))
                if registry.contains(mac) and limiter.allow([prefix, mac]):
                    reply = answer(transaction, [address_attribute(XOR_MAPPED, ip, port, transaction, True)])
        # Never answer with more bytes than were received.
        if reply and len(reply) <= len(data):
            server.sendto(reply, source)
            answered += 1
        else:
            rejected += 1
        if time.monotonic() - report >= 600:
            print(f"observer answered={answered} ignored={rejected} registered_edges={len(registry.macs)}", flush=True)
            answered = rejected = 0
            report = time.monotonic()


if __name__ == "__main__":
    sys.exit(main())
