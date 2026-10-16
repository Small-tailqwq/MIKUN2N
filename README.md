# MikuN2N

> [English](README.md) | [简体中文](README.zh.md)

A Windows client for playing LAN games with friends over a self-hosted virtual Ethernet network. MikuN2N manages a patched n3n edge, TAP-Windows, peer discovery, reconnects and a light/dark WPF interface.

**The client ships with no server addresses.** Build your own server using [the server deployment guide](supernode/README.md), or add a server shared by a friend in the node manager. Participants need the same community and encryption key. A node may contain multiple federated endpoints.

## Download and install

Choose a published Windows x64 package from [GitHub Releases](https://github.com/Small-tailqwq/MIKUN2N/releases):

| Package | Requirement |
|---|---|
| `MikuN2N-<version>-win-x64.zip` | Includes .NET; recommended for most players. |
| `MikuN2N-<version>-win-x64-framework-dependent.zip` | Smaller download; install the [.NET 9 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) first. |

Extract the entire ZIP and run `MikuN2N.exe`. Keep its `Runtime/` directory, source archive and licenses together. If you do not have a TAP adapter, use the client's network-component installer, then add your node and connection details. The current interface is in Simplified Chinese.

## Connectivity

- Native n3n IPv4 P2P, followed by bounded NAT-aware port prediction and worker-bank probing when necessary. NAT4-to-NAT4 direct connections depend on the predictability of both networks; supernode relay remains available.
- Per-peer connection mode — direct over IPv4, direct over IPv6, same LAN, attempting a direct connection or relayed through the server — with tunnel latency and online presence. The friend list shows client versions; its version tooltip includes the running n3n build and IPv6 wire version. Unreported legacy versions remain unknown. IPv6 wire mismatches are explained while IPv4 remains available; differing build numbers alone do not prevent IPv6.
- Optional IPv6 UDP peer transport, disabled by default. Enable the experimental IPv6 P2P option in Settings → General on both clients, then reconnect.
- The IPv6 experiment uses the matching patched **IPv4 supernode** to exchange candidates. It validates a sized UDP round trip and the peer's receive readiness before preferring IPv6, checks liveness and falls back to IPv4 on expiry or send failure. The current wire generation requires matching updated clients; older peers keep IPv4 connectivity. The virtual Ethernet network and game traffic remain IPv4.
- IPv6 selects one local address, preferring global IPv6 and allowing a routed ULA for NAT66. Behind NAT66 the edge discovers its peer socket's public mapping from a STUN observer and exchanges it through the supernode, so both peers can initiate checks: the supernode host's own observer when it runs one (it answers only edges registered there), otherwise Cloudflare's public STUN service. Public STUN requests contain no MikuN2N identifier or game payload, but the observer sees the source IP. Base keepalives stay independent of larger size probes, so repeated larger-probe loss lowers the send size without dropping the path; DATA above the confirmed size is fragmented inside the tunnel instead of falling back to IPv4 per packet. Endpoint-dependent mappings or filtered UDP may still prevent direct connectivity, and NAT66-to-NAT66 remains unvalidated. Validated IPv6 is preferred over Internet IPv4, while local direct links retain priority. IPv6 RTT is recorded without comparison against IPv4 RTT. IPv6-only supernodes and IPv6 relay remain outside this experiment.

Native change records are maintained as [bilingual version logs](Runtime/changes/README.md), reached from the [runtime overview](Runtime/README.md). Use the server and client source from the same release: an upstream-only server does not provide MikuN2N's extended coordination or IPv6 candidate exchange.

## Desktop features

- Node management, tray controls, single-instance protection and recovery after unexpected edge exits.
- TAP-Windows installation and adapter selection. Automatic firewall, private-network/metric and UPnP setup is enabled by default and can be disabled in Settings before connecting; firewall rules are scoped by ports/subnet, not by executable path.
- UDP 50001 for the edge, optional UPnP mapping and CGNAT detection.
- System, light and dark themes; configurable close behavior and log retention.
- Peer discovery and latency measurement inside the virtual network. Missing or stale samples are shown as unavailable after ten seconds; a route change clears the old RTT while probes continue. Native IPv6 probe RTT does not substitute for tunnel measurements.
- Encryption keys passed through process environment variables, never command-line arguments or generated configuration files. Optional key storage uses Windows DPAPI for the current user.
- Settings and logs stored under `%LocalAppData%/MikuN2N`; settings replacement preserves the previous file until the new file has been written.
- In-app updates from GitHub Releases (see below), checked daily unless turned off in Settings → General.
- Optional diagnostics upload to the active node's own supernode (experimental setting, off by default): the node must run the receiver, and upload requires authorization for the receiving node, address and certificate. A saved permission can be revoked, and retention is determined by that provider.

## Build

Requires Windows and the .NET 9 SDK:

```powershell
dotnet build
```

Output: `bin/Debug/net9.0-windows/`. `BaseVersion` and `BuildNumber` in `MikuN2N.csproj` identify the agreed version baseline. Keep them unchanged during everyday edits and tests; choose the next release version only after user acceptance and an explicit release decision. Test packages add a commit/time/test identity. Report that identity from the UI or `build-identity.txt` with any problem.

An approved release gets a matching source tag (`git diff 0.5.8-1..0.5.8-2` shows its changes); local test delivery does not create a tag. The patched native edge lives in `native/n3n-3.4.4/` on top of an unmodified upstream 3.4.4 baseline commit, so `git log -- native/` lists every native change. Client-side offline regression tests live in `tools/offline-tests`; the native checks are `tests-wire` and `tools/tests-ipv6.c` in `native/n3n-3.4.4`.

The patched native source is `native/n3n-3.4.4/`, and release packages carry it as `Runtime/n3n-3.4.4-source.zip`. Before a new native release build, run `tools/sync-native-version.ps1` to generate its build identity from the project version. Then run `sh scripts/build-mikun2n-windows.sh` from the native source root in Git Bash with MinGW-w64 GCC and make on PATH. Rebuilding the supplied archive directly retains its included identity. This uses GNU C17 and maps build paths in debug information. Copy `apps/n3n-edge.exe` to `Runtime/n3n-edge.exe`, then regenerate the source archive with `tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>` and update the component hashes together. `tools/package-release.ps1` refuses to package an archive that does not match `native/` byte for byte.

## Packaging and releases

For a local release build, use PowerShell 7, Python 3.9+ and the .NET 9 SDK:

```powershell
./tools/package-release.ps1 -ExpectedVersion '<approved-version>' -Output dist
```

The script creates both ZIPs, `SHA256SUMS` and `update.json`, retaining corresponding source, licenses, notices and the TAP installer. Without `-ExpectedVersion`, it creates uniquely identified test packages with real update checks disabled. See [packages and updates](docs/UPDATES.md).

Pushing a tag that matches the project version starts [Release package](.github/workflows/release.yml). CI builds both packages, renders Chinese and English notes from the [shared template](.github/release-notes-template.md) and version-specific entries, and attaches the four assets to a **draft** Release for review. Manual runs upload workflow artifacts only: leave `release_version` blank for test packages, or set it and `source_commit` to preview a specific release build. A successful preview's existing artifacts can be attached to a draft without rebuilding; use the exact source commit as the draft target. Publish a reviewed regular release to make it discoverable by clients.

## In-app updates

Checks run daily unless disabled in Settings → General. The client tries the static `update.json` on GitHub Releases first and uses the API as a fallback. It keeps the current package flavor and checks the download size, SHA-256 and executable version. After a download is ready, you choose when to install; downloading or receiving a notification does not stop an active connection. Installation disconnects the virtual network, replaces files with an attempted rollback on failure and restarts the client. Reconnect afterward. Hashes establish consistency with release metadata, rather than an independent publisher signature.

The previously shipped `0.5.8-10` updater only uses the API; it needs a published, higher version to offer an upgrade. Drafts and the same version are not upgrades. See [update compatibility](docs/UPDATES.md#compatibility-with-058-10).

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

## Privacy and removal

The project does not automatically report personal profiles, usage statistics or diagnostic logs to its developers. Diagnostic upload is optional, off by default and consent-based, for development and troubleshooting. The receiving supernode provider controls retention and deletion; the client cannot guarantee 24-hour deletion. Functional network requests still occur: see [Privacy](PRIVACY.md) and [network changes and removal](UNINSTALL.md). Both documents accompany the ZIP. About contains a link to the privacy policy on the repository's default branch so the policy can be updated with the project documentation.

## Code signing policy

[Code signing policy](CODE_SIGNING.md): a SignPath Foundation application is planned, not approved or enabled. The proposed scope is only our own `MikuN2N.exe`; upstream binaries are not re-signed.
