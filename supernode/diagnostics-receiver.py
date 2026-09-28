#!/usr/bin/env python3
"""Receive consented test-session JSONL over pinned TLS; expose no log download API.

Two ways to authorize an upload:
* a private test batch with its own bearer token (MIKUN2N_DIAGNOSTICS_BATCH/_TOKEN_SHA256);
* community mode (MIKUN2N_DIAGNOSTICS_COMMUNITY_FILE): the batch is derived from a
  community served by this supernode, and the client proves it knows that community
  name with an HMAC bound to the batch and session. Only literal community names
  from the supernode's community list are accepted.
"""
import argparse
import datetime as dt
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import re
import shutil
import ssl
import threading
import time

MAX_BODY = 128 * 1024
MAX_SESSION = 64 * 1024 * 1024
MAX_TOTAL = 2 * 1024 * 1024 * 1024
RETENTION_SECONDS = 24 * 3600
COMMUNITY_PROOF = "mikun2n-diagnostics-v1"
COMMUNITY_RELOAD_SECONDS = 30
ROUTE = re.compile(r"/v1/logs/([A-Za-z0-9-]{1,64})/([0-9a-f]{32})/([0-9]{8})\Z")


def community_batch(name):
    return "c-" + hashlib.sha256(name.encode("utf-8")).hexdigest()[:16]


class Communities:
    """Literal community names from the supernode's community list, keyed by batch."""

    def __init__(self, path):
        self.path, self.stamp, self.checked, self.by_batch = Path(path), None, 0.0, {}
        self.lock = threading.Lock()

    def lookup(self, batch):
        with self.lock:
            now = time.monotonic()
            if now - self.checked >= COMMUNITY_RELOAD_SECONDS:
                self.checked = now
                try:
                    stamp = self.path.stat().st_mtime_ns
                    if stamp != self.stamp:
                        names = []
                        for line in self.path.read_text(encoding="utf-8").splitlines():
                            name = line.strip()
                            # Skip comments, user/password entries and regular expressions.
                            if name and not name.startswith(("#", "*")) and not re.search(r"[.*+?\[\]\\\s]", name):
                                names.append(name)
                        self.by_batch = {community_batch(name): name for name in names}
                        self.stamp = stamp
                except (OSError, UnicodeError):
                    pass
            return self.by_batch.get(batch)


class Storage:
    def __init__(self, root, batch, communities=None):
        self.root = Path(root)
        self.batch = batch
        self.communities = communities
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.lock = threading.Lock()
        self.expired = set()
        self.total = 0
        self.sizes = {}
        self.prune()

    def prune(self):
        with self.lock:
            cutoff = time.time() - RETENTION_SECONDS
            for directory in self.root.glob("*/*"):
                if not directory.is_dir() or directory.is_symlink():
                    continue
                first = directory / "received.json"
                if first.is_file() and first.stat().st_mtime < cutoff:
                    self.expired.add((directory.parent.name, directory.name))
                    shutil.rmtree(directory)
            self.sizes = {str(p): sum(f.stat().st_size for f in p.glob("*.jsonl"))
                          for p in self.root.glob("*/*") if p.is_dir()}
            self.total = sum(self.sizes.values())

    def store(self, batch, session, chunk, data):
        if batch != self.batch and not (self.communities and self.communities.lookup(batch)):
            return 403
        if not data.endswith(b"\n"):
            return 400
        try:
            rows = [json.loads(line) for line in data.decode("utf-8").splitlines()]
            if not rows or any(not isinstance(row, dict) or row.get("schema") != 1 or
                               row.get("batch") != batch or row.get("session") != session for row in rows):
                return 400
        except (ValueError, UnicodeError, RecursionError):
            return 400
        with self.lock:
            if (batch, session) in self.expired:
                return 410
            directory = self.root / batch / session
            path = directory / (chunk + ".jsonl")
            # Atomic chunks make retries after a lost response idempotent.
            if path.exists():
                return 200 if path.read_bytes() == data else 409
            if int(chunk) > 99999 or (int(chunk) > 0 and not (directory / f"{int(chunk)-1:08d}.jsonl").exists()):
                return 409
            current = self.sizes.get(str(directory), 0)
            if current + len(data) > MAX_SESSION:
                return 507
            # New rolling segments replace the oldest retained segments when the
            # global quota fills. Never evict the segment being appended here.
            if self.total + len(data) > MAX_TOTAL:
                candidates = [Path(p) for p in self.sizes if Path(p) != directory
                              and Path(p).is_dir() and not Path(p).is_symlink()
                              and (Path(p) / 'received.json').is_file()]
                candidates.sort(key=lambda p: (p / 'received.json').stat().st_mtime)
                for old in candidates:
                    if self.total + len(data) <= MAX_TOTAL: break
                    self.expired.add((old.parent.name, old.name))
                    shutil.rmtree(old)
                    self.total -= self.sizes.pop(str(old), 0)
            if self.total + len(data) > MAX_TOTAL:
                return 507
            if shutil.disk_usage(self.root).free < len(data) + 256 * 1024 * 1024:
                return 507
            directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            metadata = directory / "received.json"
            if not metadata.exists():
                metadata.write_text(json.dumps({"first_received_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
                                                 "retention_hours": 24}) + "\n", encoding="utf-8")
            temporary = path.with_suffix(".tmp")
            with temporary.open("wb") as output:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, path)
            self.sizes[str(directory)] = current + len(data)
            self.total += len(data)
            return 201


class Receiver(http.server.ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 16

    def __init__(self, address, context, storage, token_hash):
        self.context, self.storage, self.token_hash = context, storage, token_hash
        self.slots = threading.BoundedSemaphore(16)
        super().__init__(address, Handler)

    def process_request(self, request, address):
        if not self.slots.acquire(blocking=False):
            request.close()
            return
        try:
            super().process_request(request, address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, address):
        stage = "tls"
        try:
            request.settimeout(10)
            with self.context.wrap_socket(request, server_side=True) as tls:
                stage = "http"
                self.finish_request(tls, address)
        except (OSError, ssl.SSLError) as error:
            print(f"Diagnostics request failed stage={stage} type={type(error).__name__} errno={error.errno}", flush=True)
        finally:
            self.shutdown_request(request)
            self.slots.release()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "MikuN2N-Diagnostics/1"
    sys_version = ""

    def log_message(self, *_):
        # Request paths and authorization headers do not belong in service logs.
        pass

    def reply(self, status):
        body = json.dumps({"status": status}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True

    def do_GET(self):
        self.reply(200 if self.path == "/health" else 404)

    def do_POST(self):
        authorization = self.headers.get("Authorization", "")
        match = ROUTE.fullmatch(self.path)
        if not match:
            return self.reply(404)
        batch, session = match.group(1), match.group(2)
        authorized = False
        if self.server.token_hash and batch == self.server.storage.batch and authorization.startswith("Bearer "):
            authorized = hmac.compare_digest(
                hashlib.sha256(authorization[7:].encode()).hexdigest(), self.server.token_hash)
        elif authorization.startswith("MikuN2N-Community ") and self.server.storage.communities:
            community = self.server.storage.communities.lookup(batch)
            if community is not None:
                expected = hmac.new(community.encode("utf-8"),
                                    f"{COMMUNITY_PROOF}\n{batch}\n{session}".encode(), hashlib.sha256).hexdigest()
                authorized = hmac.compare_digest(authorization[18:].strip().lower(), expected)
        if self.headers.get("Transfer-Encoding") or self.headers.get("Content-Encoding"):
            return self.reply(400)
        if self.headers.get_content_type() != "application/x-ndjson":
            return self.reply(415)
        lengths = self.headers.get_all("Content-Length", [])
        if len(lengths) != 1 or not lengths[0].isdigit():
            return self.reply(411)
        length = int(lengths[0])
        if not 0 < length <= MAX_BODY:
            return self.reply(413)
        data = self.rfile.read(length)
        if len(data) != length:
            return self.reply(400)
        # Consume only a bounded body before replying. Closing TLS with unread body
        # bytes can discard the 401 response and hide the actual failure from clients.
        if not authorized:
            return self.reply(401)
        try:
            status = self.server.storage.store(*match.groups(), data)
        except OSError:
            status = 507
        self.reply(status)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=5443)
    parser.add_argument("--storage", default="/var/lib/mikun2n-diagnostics")
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    args = parser.parse_args()
    batch = os.environ.get("MIKUN2N_DIAGNOSTICS_BATCH", "")
    token_hash = os.environ.get("MIKUN2N_DIAGNOSTICS_TOKEN_SHA256", "")
    community_file = os.environ.get("MIKUN2N_DIAGNOSTICS_COMMUNITY_FILE", "")
    if (batch or token_hash) and (not re.fullmatch(r"[A-Za-z0-9-]{1,64}", batch) or
                                  not re.fullmatch(r"[0-9a-f]{64}", token_hash)):
        parser.error("Invalid diagnostics configuration")
    if not token_hash and not community_file:
        parser.error("Configure a private batch token, a community file, or both")
    os.umask(0o077)
    storage = Storage(args.storage, batch or None, Communities(community_file) if community_file else None)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.cert, args.key)
    server = Receiver((args.bind, args.port), context, storage, token_hash)
    def cleanup():
        while True:
            time.sleep(300)
            try:
                storage.prune()
            except OSError:
                print("Diagnostics retention cleanup failed", flush=True)
    threading.Thread(target=cleanup, daemon=True).start()
    print("Diagnostics receiver ready; TLS; retention<=24h; cleanup=5m; rolling limit=64MiB/segment,2GiB/total", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
