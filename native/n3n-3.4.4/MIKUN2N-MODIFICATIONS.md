# Modified version of n3n 3.4.4

This source tree is a modified version of n3n 3.4.4
(https://github.com/n42n/n3n, tag `3.4.4`). It was modified by the MikuN2N project
between July and September 2026; the latest modification belongs to MikuN2N release
0.5.8-5 (2026-09-27). It is the corresponding source of the `n3n-edge.exe` shipped with
that release and is distributed under the same license as n3n: GPL-3.0-only (see
`LICENSES/preferred/GPL-3.0`). The upstream copyright notices are retained unchanged.

Areas changed relative to upstream:

- Windows symmetric-NAT hole punching: NAT behaviour discovery against a companion
  observation service, multi-round port prediction, worker socket banks and a
  supernode-coordinated punch schedule (`src/edge_utils.c`, `src/sn_utils.c`,
  `src/peer_info.[ch]`, `src/wire.c`, `include/n2n_typedefs.h`).
- Management API additions such as `get_nat`, `set_peer_relay` and extra `get_edges`
  fields (`src/management.c`).
- Windows TAP fixes for MAC assignment, interface metric and media status
  (`src/win32/wintap.c`).
- Experimental IPv6 transport with size discovery and bounded fragmentation
  (`src/mikun2n_ipv6.*`, `src/mikun2n_ipv6_frag.h`), relay policing
  (`src/mikun2n_relay*.h`), configuration options (`src/conffile_defs.c`) and build
  identity (`src/mikun2n_build_version.h`).
- Build and test tooling (`Makefile`, `tools/Makefile`, `tools/tests-*.c`,
  `scripts/build-mikun2n-windows.sh`).
- Simplified Chinese translations of the README, this notice and selected documents
  under `doc/` (`*.zh.md`, added 2026-09-28). The English originals are unchanged
  and remain authoritative.

Files whose name starts with `mikun2n_` are new. The per-release change log is
`Runtime/README.txt` in the MikuN2N package; for the exact difference, compare this
tree with the upstream `3.4.4` tag.
