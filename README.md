# MikuN2N

> [English](README.md) | [简体中文](README.zh.md)

A Windows client for playing LAN games with friends over a self-hosted virtual Ethernet network. MikuN2N manages a patched n3n edge, TAP-Windows, peer discovery, reconnects and a light/dark WPF interface.

**The client ships with no server addresses.** Build your own server using [the server deployment guide](supernode/README.md), or add a server shared by a friend in the node manager. Participants need the same community and encryption key. A node may contain multiple federated endpoints.

## Connectivity

- Native n3n IPv4 P2P, followed by bounded NAT-aware port prediction and worker-bank probing when necessary. NAT4-to-NAT4 direct connections depend on the predictability of both networks; supernode relay remains available.
- Per-peer IPv4 P2P, IPv6 P2P, local direct, punching and relay status, with tunnel latency and online presence. The friend list shows client versions; its version tooltip includes the running n3n build and IPv6 wire version. Unreported legacy versions remain unknown. IPv6 wire mismatches are explained while IPv4 remains available; differing build numbers alone do not prevent IPv6.
- Optional IPv6 UDP peer transport, disabled by default. Enable “尝试 IPv6 P2P 直连” in Settings → General on both clients, then reconnect.
- The IPv6 experiment uses the matching patched **IPv4 supernode** to exchange candidates. It validates a sized UDP round trip and the peer's receive readiness before preferring IPv6, checks liveness and falls back to IPv4 on expiry or send failure. The current wire generation requires matching updated clients; older peers keep IPv4 connectivity. The virtual Ethernet network and game traffic remain IPv4.
- IPv6 selects one local address, preferring global IPv6 and allowing a routed ULA for NAT66. Matching test builds can configure an IPv6 STUN observer to discover the peer socket's public mapping and exchange it through the NAT66-capable supernode, allowing both peers to initiate checks. Without an observer, a ULA peer still depends on its first probe reaching the public peer. Base keepalives stay independent of larger size probes, so repeated larger-probe loss lowers the send size without dropping the path; DATA above the confirmed size is fragmented inside the tunnel instead of falling back to IPv4 per packet. Endpoint-dependent mappings or filtered UDP may still prevent direct connectivity, and NAT66-to-NAT66 remains unvalidated. Validated IPv6 is preferred over Internet IPv4, while local direct links retain priority. IPv6 RTT is recorded without comparison against IPv4 RTT. IPv6-only supernodes and IPv6 relay remain outside this experiment.

The numbered native patch list is maintained only in [Runtime/README.txt](Runtime/README.txt). Use the server and client source from the same release: an upstream-only server does not provide MikuN2N's extended coordination or IPv6 candidate exchange.

## Desktop features

- Node management, tray controls, single-instance protection and recovery after unexpected edge exits.
- TAP-Windows installation, adapter selection, interface priority and scoped firewall setup.
- UDP 50001 for the edge, optional UPnP mapping and CGNAT detection.
- System, light and dark themes; configurable close behavior and log retention.
- Peer discovery and latency measurement inside the virtual network. Missing or stale samples show “暂无法测量” after ten seconds; a route change clears the old RTT while probes continue. Native IPv6 probe RTT does not substitute for tunnel measurements.
- Encryption keys passed through process environment variables, never command-line arguments or generated configuration files. Optional key storage uses Windows DPAPI for the current user.
- Settings and logs stored under `%LocalAppData%/MikuN2N`; settings replacement preserves the previous file until the new file has been written.
- In-app updates from GitHub Releases (see below), checked daily unless turned off in Settings → General.

## Build

Requires Windows and the .NET 9 SDK:

```powershell
dotnet build
```

Output: `bin/Debug/net9.0-windows/`. `BaseVersion` and `BuildNumber` in `MikuN2N.csproj` produce versions such as `0.5.8-1`. Increment the number for each newly delivered package; retain it for rebuilds of that delivery and reset it to 1 when the base version changes. Record the version from the UI or `build-identity.txt` when reporting a problem.

Each delivery gets one commit plus a matching tag (`git diff 0.5.8-1..0.5.8-2` is the whole version's change); this covers the patched native edge too, which lives in `native/n3n-3.4.4/` on top of an unmodified upstream 3.4.4 baseline commit, so `git log -- native/` lists every native change and diffing against that baseline gives the complete patch set. Client-side offline regression tests live in `tools/offline-tests` and run with `dotnet run --project tools/offline-tests -- <empty temp directory>`; the native checks are `tests-wire` and `tools/tests-ipv6.c` in `native/n3n-3.4.4`. These cover protocol and session logic only — not a live connection.

The patched native source is `native/n3n-3.4.4/`, and release packages carry it as `Runtime/n3n-3.4.4-source.zip`. Before a new native release build, run `tools/sync-native-version.ps1` to generate its build identity from the project version. Then run `sh scripts/build-mikun2n-windows.sh` from the native source root in Git Bash with MinGW-w64 GCC and make on PATH. Rebuilding the supplied archive directly retains its included identity. This uses GNU C17 and maps build paths in debug information. Copy `apps/n3n-edge.exe` to `Runtime/n3n-edge.exe`, then regenerate the source archive with `tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>` and update the component hashes together. `tools/package-release.ps1` refuses to package an archive that does not match `native/` byte for byte.

## Publish

Framework-dependent package (requires the .NET 9 Desktop Runtime):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/MikuN2N
```

Self-contained package:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

Both commands write `build-identity.txt` and include the explicitly listed runtime files: the patched edge, corresponding source, licenses, component notices and TAP installer. Use a fresh output directory for each release. Legacy edge binaries and backups are excluded.

## Releases and in-app updates

`tools/package-release.ps1` publishes the self-contained build, verifies that it carries the patched edge, its corresponding source and every bundled license (and none of the build machine's account paths), and writes `dist/MikuN2N-<version>-win-x64.zip` plus `dist/SHA256SUMS`. Pushing a tag such as `0.5.8-6` runs the same script in [the release workflow](.github/workflows/release.yml) and attaches both files to a **draft** GitHub release; clients only see it after it is published as a regular (non-prerelease) release.

The client asks `api.github.com` for the latest release of the repository named by `UpdateRepository` in `MikuN2N.csproj`. Forks set their own repository with `-p:UpdateRepository=owner/name`; `-p:UpdateRepository=none` builds a client that never checks. A newer package is downloaded in the background, verified against the asset's SHA-256 (GitHub's asset digest or `SHA256SUMS`), unpacked, and its `MikuN2N.exe` version must match the tag. Only then is the player asked. Installing stops the connection, swaps the files in place with rollback on failure and restarts the new version. Development builds and folders without write access are pointed to the release page instead.

The source archive supports both the Windows edge and the Linux supernode. Server build and install scripts are in [supernode/](supernode/README.md). Deployment is manual; no server or credentials are supplied by this project.

IPv6 remains experimental. Compilation does not establish real-world connectivity, game compatibility or light/dark visual acceptance. Release validation needs two consenting clients, their own server and tests of connection, fallback, reconnect and MTU behavior.

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

Keep deployment addresses, communities, keys and local settings out of public source and history; put them only in git-ignored `*.local.md` files. The repository history was rewritten before publication to remove them, so it can be pushed as is.
