#!/usr/bin/env python3
"""natpunch v7 coordination server, with v6 text-protocol compatibility."""

from __future__ import annotations

import argparse
import json
import logging
import os
import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Callable


DEFAULT_PORT_A = 21001
DEFAULT_PORT_B = 21002
MAX_ROOM = 64
MAX_CLIENT_ID = 32
MEMBER_TTL = 120.0


@dataclass
class Member:
    client_id: str
    addr: tuple[str, int]
    seen: float
    report: tuple[int, list[str]] | None = None


@dataclass
class Room:
    members: dict[str, Member] = field(default_factory=dict)
    issued: tuple[str, int, str, int] | None = None
    consumed: dict[str, int] = field(default_factory=dict)
    attempt: int = 0


class Coordinator:
    def __init__(self, event_log: str | None = None):
        self.rooms: dict[str, Room] = {}
        self.legacy_rooms: dict[str, list[tuple[tuple[str, int], float]]] = {}
        self.legacy_banks: dict[str, dict[tuple[str, int], tuple[int, int, int]]] = {}
        self.lock = threading.Lock()
        self.event_log = event_log
        self.rates: dict[str, tuple[float, float]] = {}

    def allowed(self, source_ip: str, now: float) -> bool:
        tokens, updated = self.rates.get(source_ip, (200.0, now))
        tokens = min(200.0, tokens + (now - updated) * 100.0)
        if tokens < 1.0:
            self.rates[source_ip] = (tokens, now)
            return False
        self.rates[source_ip] = (tokens - 1.0, now)
        return True

    def event(self, name: str, **fields: object) -> None:
        record = {"ts": time.time(), "event": name, **fields}
        logging.info("%s %s", name, fields)
        if self.event_log:
            with open(self.event_log, "a", encoding="utf-8") as stream:
                stream.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")

    @staticmethod
    def valid_token(value: str, maximum: int) -> bool:
        return 0 < len(value) <= maximum and all(c.isalnum() or c in "._-" for c in value)

    def cleanup(self, now: float) -> None:
        for room_name in list(self.rooms):
            room = self.rooms[room_name]
            room.members = {
                cid: member
                for cid, member in room.members.items()
                if now - member.seen < MEMBER_TTL
            }
            if not room.members:
                del self.rooms[room_name]
        for room_name in list(self.legacy_rooms):
            members = [(a, t) for a, t in self.legacy_rooms[room_name] if now - t < MEMBER_TTL]
            if members:
                self.legacy_rooms[room_name] = members
            else:
                self.legacy_rooms.pop(room_name, None)
                self.legacy_banks.pop(room_name, None)

    def handle(
        self,
        data: bytes,
        addr: tuple[str, int],
        label: str,
        send_here: Callable[[str, tuple[str, int]], None],
        send_a: Callable[[str, tuple[str, int]], None],
        send_b: Callable[[str, tuple[str, int]], None],
    ) -> None:
        try:
            text = data.decode("utf-8", "ignore").strip()
        except Exception:
            return
        if not text or len(text) > 1800:
            return
        parts = text.split()
        cmd = parts[0]
        now = time.monotonic()
        if not self.allowed(addr[0], now):
            return

        if cmd == "PROBE":
            send_here(f"PROBED {addr[0]} {addr[1]} {label}", addr)
            return
        if cmd == "XPROBE" and len(parts) >= 3:
            try:
                if parts[1] == addr[0] and int(parts[2]) == addr[1]:
                    send_b("XREPLY", addr)
            except (ValueError, OSError):
                pass
            return
        if cmd in {"JOIN", "BANKS"}:
            self.handle_legacy(parts, addr, now, send_a)
            return
        if cmd == "PROBE7" and len(parts) == 3:
            cid, seq = parts[1], parts[2]
            if self.valid_token(cid, MAX_CLIENT_ID) and seq.isdigit():
                send_here(
                    f"PROBED7 {cid} {seq} {addr[0]} {addr[1]} {label} "
                    f"{time.monotonic_ns() // 1_000_000}",
                    addr,
                )
            return
        if cmd == "JOIN7" and len(parts) == 3:
            room_name, cid = parts[1], parts[2]
            if not self.valid_token(room_name, MAX_ROOM) or not self.valid_token(cid, MAX_CLIENT_ID):
                send_a("ERR7 invalid-token", addr)
                return
            with self.lock:
                self.cleanup(now)
                room = self.rooms.setdefault(room_name, Room())
                room.members[cid] = Member(cid, addr, now, room.members.get(cid, Member(cid, addr, now)).report)
                send_a(f"JOIN7ACK {cid} {addr[0]} {addr[1]}", addr)
                self.send_peer_info(room, cid, send_a)
                self.event("join7", room=room_name, client=cid, source=f"{addr[0]}:{addr[1]}")
            return
        if cmd == "REPORT7" and len(parts) == 13:
            self.handle_report7(parts, addr, now, send_a)

    def send_peer_info(self, room: Room, cid: str, send: Callable[[str, tuple[str, int]], None]) -> None:
        if cid not in room.members:
            return
        peers = [member for other, member in room.members.items() if other != cid]
        if not peers:
            return
        peer = max(peers, key=lambda item: item.seen)
        me = room.members[cid]
        send(f"PEER7 {peer.client_id} {peer.addr[0]} {peer.addr[1]}", me.addr)
        send(f"PEER7 {me.client_id} {me.addr[0]} {me.addr[1]}", peer.addr)

    def handle_report7(
        self,
        parts: list[str],
        addr: tuple[str, int],
        now: float,
        send: Callable[[str, tuple[str, int]], None],
    ) -> None:
        room_name, cid = parts[1], parts[2]
        if not self.valid_token(room_name, MAX_ROOM) or not self.valid_token(cid, MAX_CLIENT_ID):
            return
        try:
            generation = int(parts[3])
            numeric = [int(value) for value in parts[6:13]]
        except ValueError:
            return
        if generation < 1 or any(abs(value) > 10_000_000 for value in numeric):
            return
        with self.lock:
            self.cleanup(now)
            room = self.rooms.setdefault(room_name, Room())
            member = room.members.get(cid)
            if member is None:
                member = Member(cid, addr, now)
                room.members[cid] = member
            member.addr = addr
            member.seen = now
            member.report = (generation, parts[4:13])
            self.send_peer_info(room, cid, send)
            ready = [m for m in room.members.values() if m.report is not None]
            if len(ready) < 2:
                return
            ready = sorted(ready, key=lambda item: item.seen, reverse=True)[:2]
            a, b = sorted(ready, key=lambda item: item.client_id)
            if (
                a.report[0] <= room.consumed.get(a.client_id, 0)
                or b.report[0] <= room.consumed.get(b.client_id, 0)
            ):
                return
            key = (a.client_id, a.report[0], b.client_id, b.report[0])
            if room.issued == key:
                return
            room.issued = key
            room.consumed[a.client_id] = a.report[0]
            room.consumed[b.client_id] = b.report[0]
            room.attempt += 1
            attempt = room.attempt
            for me, peer in ((a, b), (b, a)):
                fields = " ".join(peer.report[1])
                for _ in range(3):
                    send(
                        f"PEERREPORT7 {attempt} {peer.addr[0]} {peer.addr[1]} {fields}",
                        me.addr,
                    )
            for member_to_go in (a, b):
                for _ in range(3):
                    send(f"GO7 {attempt} 800", member_to_go.addr)
            self.event(
                "go7",
                room=room_name,
                attempt=attempt,
                a=a.client_id,
                b=b.client_id,
                gen_a=a.report[0],
                gen_b=b.report[0],
            )

    def handle_legacy(
        self,
        parts: list[str],
        addr: tuple[str, int],
        now: float,
        send: Callable[[str, tuple[str, int]], None],
    ) -> None:
        cmd = parts[0]
        if cmd == "JOIN" and len(parts) >= 2:
            room_name = parts[1]
            send("JOINACK", addr)
            with self.lock:
                members = [
                    (a, t)
                    for a, t in self.legacy_rooms.setdefault(room_name, [])
                    if now - t < MEMBER_TTL and a != addr
                ]
                members.append((addr, now))
                self.legacy_rooms[room_name] = members
                if len(members) >= 2:
                    a, b = members[-1][0], members[-2][0]
                    send(f"PEER {b[0]} {b[1]}", a)
                    send(f"PEER {a[0]} {a[1]}", b)
            return
        if cmd == "BANKS" and len(parts) >= 5:
            room_name = parts[1]
            try:
                values = tuple(int(value) for value in parts[2:5])
            except ValueError:
                return
            with self.lock:
                banks = self.legacy_banks.setdefault(room_name, {})
                banks[addr] = values
                members = [a for a, _ in self.legacy_rooms.get(room_name, [])]
                for peer in (a for a in members if a != addr):
                    send(f"PEERBANKS {addr[0]} {values[0]} {values[1]} {values[2]}", peer)
                active = members[-2:]
                if len(active) == 2 and all(member in banks for member in active):
                    for member in active:
                        send("GO", member)


def run_server(bind: str, port_a: int, port_b: int, event_log: str | None) -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    coordinator = Coordinator(event_log)
    sock_a = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock_b = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock_a.bind((bind, port_a))
    sock_b.bind((bind, port_b))

    def sender(sock: socket.socket) -> Callable[[str, tuple[str, int]], None]:
        def send(text: str, addr: tuple[str, int]) -> None:
            try:
                sock.sendto(text.encode("utf-8"), addr)
            except OSError:
                logging.exception("send failed to %s", addr)
        return send

    send_a, send_b = sender(sock_a), sender(sock_b)

    def loop(sock: socket.socket, label: str) -> None:
        send_here = sender(sock)
        while True:
            try:
                data, addr = sock.recvfrom(2048)
                coordinator.handle(data, addr, label, send_here, send_a, send_b)
            except OSError:
                logging.exception("receive loop failed on %s", label)

    threading.Thread(target=loop, args=(sock_a, "A"), daemon=True).start()
    threading.Thread(target=loop, args=(sock_b, "B"), daemon=True).start()
    logging.info("natpunch v7 server listening UDP %d/%d", port_a, port_b)
    while True:
        time.sleep(3600)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("role", choices=["server"])
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port-a", type=int, default=DEFAULT_PORT_A)
    parser.add_argument("--port-b", type=int, default=DEFAULT_PORT_B)
    parser.add_argument("--event-log", default=os.environ.get("NATPUNCH_EVENT_LOG"))
    args = parser.parse_args()
    run_server(args.bind, args.port_a, args.port_b, args.event_log)


if __name__ == "__main__":
    main()
