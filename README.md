# MikuN2N

> [English](README.md) | [简体中文](README.zh.md)

A Windows client for playing LAN games with friends over a self-hosted virtual Ethernet network. MikuN2N manages a patched n3n edge, TAP-Windows, peer discovery, reconnects and a light/dark WPF interface.

**The client ships with no server addresses.** Build your own server using [the server deployment guide](supernode/README.md), or add a server shared by a friend in the node manager. Participants need the same community and encryption key. A node may contain multiple federated endpoints.

## Connectivity

- Native n3n IPv4 P2P, followed by bounded NAT-aware port prediction and worker-bank probing when necessary. NAT4-to-NAT4 direct connections depend on the predictability of both networks; supernode relay remains available.
- Per-peer connection mode — direct over IPv4 (“直连 · IPv4”), direct over IPv6 (“直连 · IPv6”), same LAN (“局域网直连”), trying to connect directly (“正在尝试直连…”) or relayed through the server (“服务器中转”) — with tunnel latency and online presence. The friend list shows client versions; its version tooltip includes the running n3n build and IPv6 wire version. Unreported legacy versions remain unknown. IPv6 wire mismatches are explained while IPv4 remains available; differing build numbers alone do not prevent IPv6.
- Optional IPv6 UDP peer transport, disabled by default. Enable “尝试 IPv6 P2P 直连” in Settings → General on both clients, then reconnect.
- The IPv6 experiment uses the matching patched **IPv4 supernode** to exchange candidates. It validates a sized UDP round trip and the peer's receive readiness before preferring IPv6, checks liveness and falls back to IPv4 on expiry or send failure. The current wire generation requires matching updated clients; older peers keep IPv4 connectivity. The virtual Ethernet network and game traffic remain IPv4.
- IPv6 selects one local address, preferring global IPv6 and allowing a routed ULA for NAT66. Behind NAT66 the edge discovers its peer socket's public mapping from a STUN observer and exchanges it through the supernode, so both peers can initiate checks: the supernode host's own observer when it runs one (it answers only edges registered there), otherwise Cloudflare's public STUN service, which receives only an anonymous request. Base keepalives stay independent of larger size probes, so repeated larger-probe loss lowers the send size without dropping the path; DATA above the confirmed size is fragmented inside the tunnel instead of falling back to IPv4 per packet. Endpoint-dependent mappings or filtered UDP may still prevent direct connectivity, and NAT66-to-NAT66 remains unvalidated. Validated IPv6 is preferred over Internet IPv4, while local direct links retain priority. IPv6 RTT is recorded without comparison against IPv4 RTT. IPv6-only supernodes and IPv6 relay remain outside this experiment.

Native change records are maintained as [bilingual version logs](Runtime/changes/README.md), reached from the [runtime overview](Runtime/README.md). Use the server and client source from the same release: an upstream-only server does not provide MikuN2N's extended coordination or IPv6 candidate exchange.

## Desktop features

- Node management, tray controls, single-instance protection and recovery after unexpected edge exits.
- TAP-Windows installation, adapter selection, interface priority and scoped firewall setup.
- UDP 50001 for the edge, optional UPnP mapping and CGNAT detection.
- System, light and dark themes; configurable close behavior and log retention.
- Peer discovery and latency measurement inside the virtual network. Missing or stale samples show “暂无法测量” after ten seconds; a route change clears the old RTT while probes continue. Native IPv6 probe RTT does not substitute for tunnel measurements.
- Encryption keys passed through process environment variables, never command-line arguments or generated configuration files. Optional key storage uses Windows DPAPI for the current user.
- Settings and logs stored under `%LocalAppData%/MikuN2N`; settings replacement preserves the previous file until the new file has been written.
- In-app updates from GitHub Releases (see below), checked daily unless turned off in Settings → General.
- Optional diagnostics upload to the active node's own supernode (experimental setting, off by default): the node must run the receiver, and every upload still asks for consent and shows the receiver's certificate fingerprint.

## Build

Requires Windows and the .NET 9 SDK:

```powershell
dotnet build
```

Output: `bin/Debug/net9.0-windows/`. `BaseVersion` and `BuildNumber` in `MikuN2N.csproj` identify the agreed version baseline. Keep them unchanged during everyday edits and tests; choose the next release version only after user acceptance and an explicit release decision. Test packages add a commit/time/test identity. Report that identity from the UI or `build-identity.txt` with any problem.

An approved release gets a matching source tag (`git diff 0.5.8-1..0.5.8-2` shows its changes); local test delivery does not create a tag. The patched native edge lives in `native/n3n-3.4.4/` on top of an unmodified upstream 3.4.4 baseline commit, so `git log -- native/` lists every native change. Client-side offline regression tests live in `tools/offline-tests`; the native checks are `tests-wire` and `tools/tests-ipv6.c` in `native/n3n-3.4.4`.

The patched native source is `native/n3n-3.4.4/`, and release packages carry it as `Runtime/n3n-3.4.4-source.zip`. Before a new native release build, run `tools/sync-native-version.ps1` to generate its build identity from the project version. Then run `sh scripts/build-mikun2n-windows.sh` from the native source root in Git Bash with MinGW-w64 GCC and make on PATH. Rebuilding the supplied archive directly retains its included identity. This uses GNU C17 and maps build paths in debug information. Copy `apps/n3n-edge.exe` to `Runtime/n3n-edge.exe`, then regenerate the source archive with `tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>` and update the component hashes together. `tools/package-release.ps1` refuses to package an archive that does not match `native/` byte for byte.

## Publish

After user acceptance and release approval, set the agreed project version and build both Windows x64 packages with PowerShell 7, Python 3.9+ and the .NET 9 SDK:

```powershell
./tools/package-release.ps1 -ExpectedVersion '<approved-version>' -Output dist
```

The self-contained `MikuN2N-<version>-win-x64.zip` includes .NET. The smaller `MikuN2N-<version>-win-x64-framework-dependent.zip` requires the **.NET 9 Desktop Runtime x64** on the player's PC. Both use a single-file application and carry the same patched edge, corresponding source, licenses, notices and TAP installer. The script also writes `SHA256SUMS` and `update.json`; optional `-Notes '...'` adds update notes. See [package and update details](docs/UPDATES.md).

## Releases and in-app updates

Pushing a tag matching the project version runs the same script in [the release workflow](.github/workflows/release.yml) and attaches both ZIPs, `SHA256SUMS` and `update.json` to a **draft** GitHub Release. Review it before publishing as a regular release. The script checks package contents, component bytes, native source consistency and accidental build-machine account paths.

Update discovery first downloads the static `update.json` from GitHub's latest release, with the GitHub API as a compatibility fallback. The client keeps its runtime flavor, verifies the package SHA-256 and executable version, and offers installation after the download is ready. Installing stops the connection, swaps files with rollback on failure and restarts. Hashes verify integrity against release metadata; they are not an independent publisher signature. Fork and legacy-client behavior is explained in [UPDATES.md](docs/UPDATES.md).

The source archive supports both the Windows edge and the Linux supernode. Server build and install scripts are in [supernode/](supernode/README.md). Deployment is manual; no server or credentials are supplied by this project.

IPv6 remains experimental. Before release, test connection, fallback, reconnect, MTU behavior and both UI themes with two participating players and their server.

## Research

[NATPUNCH v7 summary](docs/NATPUNCH-V7-SUMMARY.md) and [federation notes](docs/FEDERATION.md) document past experiments and their limits.

## License

MikuN2N's own code, including the native patches and `tools/natpunch`, is licensed under [GPL-3.0-only](LICENSE) and ships as `LICENSE.txt` in every package. Bundled components keep their own licenses:

| Component | License | Obligation met by |
|---|---|---|
| Patched n3n 3.4.4 edge | GPL-3.0-only (with LGPL-2.1-only `connslot`) | `Runtime/n3n-3.4.4-source.zip` with `MIKUN2N-MODIFICATIONS.md`, `Runtime/LICENSE-n2n.txt` |
| TAP-Windows 9.24.7 installer (unmodified) | GPL-2.0 with WDK system-library exception | `Runtime/LICENSE-tap-windows.txt`, upstream source link and written source offer |
| .NET 9 runtime, WPF, Windows Forms | MIT | `Runtime/LICENSE-dotnet.txt`, `Runtime/THIRD-PARTY-NOTICES-dotnet.txt` |

Details are in [Runtime/THIRD-PARTY-NOTICES.txt](Runtime/THIRD-PARTY-NOTICES.txt). Distribute the matching native source archive with every native binary. The legacy n2n `edge.exe` has no reproducible source and is neither tracked nor packaged.

Keep deployment addresses, communities, keys and local settings out of public source and history; put them only in git-ignored `*.local.md` files.
