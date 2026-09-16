#!/usr/bin/env bash
# Build the Linux server from the same patched source shipped with the edge.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARCHIVE="$SCRIPT_DIR/../Runtime/n3n-3.4.4-source.zip"
OUTPUT="$SCRIPT_DIR/../artifacts/supernode"
while [ $# -gt 0 ]; do
    case "$1" in
        --source-archive|--output)
            [ $# -ge 2 ] || { echo "Missing value for $1" >&2; exit 1; }
            case "$1" in --source-archive) ARCHIVE="$2" ;; --output) OUTPUT="$2" ;; esac
            shift 2 ;;
        -h|--help) echo "Usage: bash $0 [--source-archive FILE] [--output DIRECTORY]"; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done
[ "$(uname -s)" = Linux ] || { echo "Build this target on Linux" >&2; exit 1; }
for required in gcc make autoreconf unzip python3; do
    command -v "$required" >/dev/null || { echo "Missing build dependency: $required" >&2; exit 1; }
done
ARCHIVE="$(realpath "$ARCHIVE")"
mkdir -p "$OUTPUT"
OUTPUT="$(realpath "$OUTPUT")"
BUILD_DIR="$(mktemp -d -t mikun2n-build.XXXXXXXX)"
trap 'rm -rf -- "$BUILD_DIR"' EXIT
unzip -q "$ARCHIVE" -d "$BUILD_DIR"
cd "$BUILD_DIR"
# Zip exports do not preserve executable permissions; autogen replaces Windows-generated configure files.
chmod +x autogen.sh scripts/* libs/connslot/file2strbufc
./autogen.sh
./configure CFLAGS="-O2 -std=gnu17 -ffile-prefix-map=$BUILD_DIR=/usr/src/mikun2n/n3n"
make -j"$(getconf _NPROCESSORS_ONLN)"
install -m 0755 apps/n3n-supernode "$OUTPUT/n3n-supernode"
cp "$ARCHIVE" "$OUTPUT/n3n-3.4.4-source.zip"
(cd "$OUTPUT" && sha256sum n3n-supernode n3n-3.4.4-source.zip > SHA256SUMS)
echo "Built server and corresponding source: $OUTPUT"
