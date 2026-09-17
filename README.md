# MikuN2N

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

## Build

Requires Windows and the .NET 9 SDK:

```powershell
dotnet build
```

Output: `bin/Debug/net9.0-windows/`. `BaseVersion` and `BuildNumber` in `MikuN2N.csproj` produce versions such as `0.5.8-1`. Increment the number for each newly delivered package; retain it for rebuilds of that delivery and reset it to 1 when the base version changes. Record the version from the UI or `build-identity.txt` when reporting a problem.

Each delivery gets one commit plus a matching tag (`git diff 0.5.8-1..0.5.8-2` is the whole version's change); the patched native tree is versioned the same way in the separate `n3n-build` repository. Client-side offline regression tests live in `tools/offline-tests` and run with `dotnet run --project tools/offline-tests -- <empty temp directory>`; the native checks are `tests-wire` and `tools/tests-ipv6.c` in the patched source tree. These cover protocol and session logic only — not a live connection.

The patched native source is included in `Runtime/n3n-3.4.4-source.zip`. Before a new native release build, run `tools/sync-native-version.ps1 -PatchedSource <source-root>` to generate its build identity from the project version. Then run `sh scripts/build-mikun2n-windows.sh` from the native source root in Git Bash with MinGW-w64 GCC and make on PATH. Rebuilding the supplied archive directly retains its included identity. This uses GNU C17 and maps build paths in debug information. Copy `apps/n3n-edge.exe` to `Runtime/n3n-edge.exe`, then update the source archive and component hashes together.

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

The source archive supports both the Windows edge and the Linux supernode. Server build and install scripts are in [supernode/](supernode/README.md). Deployment is manual; no server or credentials are supplied by this project.

IPv6 remains experimental. Compilation does not establish real-world connectivity, game compatibility or light/dark visual acceptance. Release validation needs two consenting clients, their own server and tests of connection, fallback, reconnect and MTU behavior.

## Research

[NATPUNCH v7 summary](docs/NATPUNCH-V7阶段总结.md) and [federation notes](docs/FEDERATION.md) document past experiments and their limits.

## License

MikuN2N's own code is licensed under [GPL-3.0-only](LICENSE). Third-party files retain their individual licenses; see [Runtime/THIRD-PARTY-NOTICES.txt](Runtime/THIRD-PARTY-NOTICES.txt). Distribute the matching native source archive with the native binary.

Keep deployment addresses, communities, keys and local settings out of public source and history. For a new public repository, import the cleaned source export without the private repository's `.git` directory.
