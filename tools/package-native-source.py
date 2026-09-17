"""Export tracked native sources and verify every archived byte against the worktree."""
import argparse
import hashlib
import json
import re
import subprocess
import zipfile
from pathlib import Path


GENERATED = {
    "config.mak", "config.log", "config.status", "config.rpath", "configure",
    "include/config.h", "include/config.h.in", "scripts/config.guess", "scripts/config.sub",
    "src/management_index.html.h", "src/management_script.js.h",
}
PERSONAL_PATH = re.compile(
    rb"(?:[a-z]:[\\/]+users[\\/]+|" rb"/home" rb"/[^/\s]+/|" rb"/Users" rb"/[^/\s]+/)", re.I)


def private_path(data):
    return bool(PERSONAL_PATH.search(data) or PERSONAL_PATH.search(data.replace(b"\0", b"")))


def source_files(source):
    names = subprocess.check_output(["git", "-C", str(source), "ls-files", "-z", "--", "."]).decode().split("\0")
    return sorted(name for name in names if name and name not in GENERATED
                  and Path(name).suffix not in {".o", ".a", ".exe", ".gz"}
                  and ".local." not in name and "autom4te.cache" not in Path(name).parts)


def validate(source, archive, expected):
    mismatches, unexpected, personal_paths = [], [], []
    manifest = []
    with zipfile.ZipFile(archive) as bundle:
        names = bundle.namelist()
        for name in names:
            data = bundle.read(name)
            if private_path(data):
                personal_paths.append(name)
            if name not in expected:
                unexpected.append(name)
                continue
            match = data == (source / name).read_bytes()
            if not match:
                mismatches.append(name)
            manifest.append({"path": name, "bytes": len(data),
                             "sha256": hashlib.sha256(data).hexdigest(), "matches_source": match})
        missing = sorted(set(expected) - set(names))
        duplicates = sorted(name for name in set(names) if names.count(name) != 1)
        corrupt = bundle.testzip()
    return {
        "native_source_entries": len(names), "matched_entries": sum(row["matches_source"] for row in manifest),
        "all_source_bytes_match": not (mismatches or unexpected or missing or duplicates or corrupt),
        "mismatched_entries": mismatches, "unexpected_entries": unexpected,
        "missing_entries": missing, "duplicate_entries": duplicates, "corrupt_entry": corrupt,
        "personal_paths_absent": not personal_paths, "personal_path_entries": personal_paths,
        "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "files": manifest,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--validate-only", action="store_true")
    args = parser.parse_args()
    source = args.source.resolve()
    if not (source / "src/mikun2n_ipv6.c").is_file():
        parser.error("source must be the patched native source root")
    expected = source_files(source)
    if not expected:
        parser.error("no tracked source files")
    if not args.validate_only:
        args.archive.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
            for name in expected:
                path = source / name
                if not path.resolve().is_relative_to(source) or path.is_symlink():
                    raise ValueError("Source entry escapes its root: " + name)
                bundle.write(path, name)
    report = validate(source, args.archive, expected)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "files"}, indent=2))
    if not report["all_source_bytes_match"] or not report["personal_paths_absent"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
