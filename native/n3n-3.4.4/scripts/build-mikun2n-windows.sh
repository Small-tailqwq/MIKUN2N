#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-only
# Run from the source root in Git Bash with MinGW-w64 on PATH.
set -eu
build_root=$(pwd -W)
export CFLAGS="${CFLAGS:-} -std=gnu17 -ffile-prefix-map=$build_root=/usr/src/mikun2n/n3n"
sh scripts/hack_fakeautoconf.sh
make -B -j4
strip --strip-debug apps/n3n-edge.exe
