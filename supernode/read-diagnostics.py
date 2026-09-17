#!/usr/bin/env python3
"""Read both clients' retained JSONL chunks over SSH, ordered by client UTC time."""
import argparse
import heapq
import json
from pathlib import Path


def records(directory):
    for path in sorted(directory.glob("[0-9]*.jsonl")):
        with path.open(encoding="utf-8") as source:
            for line in source:
                try:
                    row = json.loads(line)
                    yield row
                except (ValueError, TypeError):
                    continue


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Batch directory under /var/lib/mikun2n-diagnostics")
    parser.add_argument("--kind", help="Optional event kind, e.g. edge_log or management_edges")
    parser.add_argument("--contains", default="", help="Only events containing this text")
    parser.add_argument("--connection", help="Stable connection ID across rolling segments")
    parser.add_argument("--sessions", action="store_true", help="Show session identities and first receiver timestamp")
    args = parser.parse_args()
    sessions = [p for p in sorted(args.directory.iterdir()) if p.is_dir()]
    if args.sessions:
        for session in sessions:
            identity = {}
            connection = session.name
            for row in records(session):
                connection = row.get('connection', connection)
                if row.get("kind") == "connection_requested":
                    identity = row.get("data", {})
                    break
            received = session / "received.json"
            if args.connection and connection != args.connection: continue
            print(json.dumps({"session": session.name, "connection": connection, "identity": identity,
                              "received": json.loads(received.read_text()) if received.exists() else None}, ensure_ascii=False))
        return
    # Each stream keeps its original event sequence; elapsedMs remains authoritative
    # within a session if a client's wall clock moves or is skewed.
    for row in heapq.merge(*(records(session) for session in sessions), key=lambda r: r.get("utc", "")):
        if args.connection and row.get('connection', row.get('session')) != args.connection:
            continue
        if args.kind and row.get("kind") != args.kind:
            continue
        text = json.dumps(row, ensure_ascii=False)
        if args.contains in text:
            print(text)


if __name__ == "__main__":
    main()
