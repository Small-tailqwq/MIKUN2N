# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MikuN2N is a Windows WPF (.NET 9, C#) desktop client built on top of the open-source [n3n](https://github.com/n42n/n3n) edge VPN client (itself a fork of n2n), with additional open-source tooling, to give non-technical players one-click LAN-like gaming over a virtual network. It manages the `n3n-edge.exe` child process, talks to its local management API, discovers other MikuN2N peers over UDP broadcast, and shows connection/peer status in a WPF UI with a system tray presence.

**The problem this project exists to solve**: stock n3n, like most n2n-family edge clients, falls back to relaying all traffic through the supernode (pSp mode) whenever a peer sits behind a symmetric/port-restricted NAT (NAT4) — it has no working port-prediction path for that case on Windows, which is common under Chinese carrier-grade NAT (CGNAT). Relayed traffic adds latency and load that hurts real-time gaming. MikuN2N's core engineering effort is a locally patched `n3n-edge.exe` (see below) that adds standards-based NAT behavior discovery (RFC 8489 STUN-style mapping/filtering classification) and several purpose-built port-prediction and multi-round probing algorithms to establish a direct UDP path (RFC 5128 UDP hole punching, in the spirit of RFC 8445 ICE) instead of relaying. This currently establishes direct peer-to-peer connectivity for NAT3↔NAT4 pairs reliably, and for a subset of NAT4↔NAT4 (symmetric↔symmetric) pairs whose NAT port allocation is predictable enough to target. These algorithms exist solely to establish a successful direct peer connection between two consenting MikuN2N users on their own network path — they do not touch, scan, or interact with any third party's systems, and are not reverse-engineering or access-control-bypass tooling.

A companion `server/` directory holds the systemd unit and config template for the dedicated n3n supernode this client connects to by default (`vps.example.com:3076`, community `mygroup`, subnet `192.0.2.0/24`). Server-side changes are deployed manually to that box; nothing in this repo automates that deployment.

## Build and run

Requires Windows and the .NET 9 SDK.

```powershell
dotnet build
```

Build output: `bin/Debug/net9.0-windows/`.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

There is no test project and no lint step configured — verification is manual (build + run on Windows). When changing connection/process/networking logic, actually launch the app and connect, since this is a Windows-only WPF app that can't be meaningfully unit-tested for its core behavior (process lifecycle, TAP adapter state, UDP discovery).

The app is single-instance (named mutex `Local\MikuN2N.SingleInstance`); a second launch just shows a message box and exits.

## Architecture

**Entry points**: `App.xaml.cs` (startup/theme/single-instance/tray/log-cleanup wiring), `MainWindow.xaml.cs` (main UI + wiring), `SettingsWindow.xaml.cs` / `CloseBehaviorDialog.xaml.cs` / `ConflictingProcessDialog.xaml.cs` (secondary windows).

**`Services/EdgeController.cs`** is the core of the app — everything else is secondary. It:
- Launches `Runtime/n3n-edge.exe` (or the legacy `Runtime/edge.exe`, kept only for historical fallback — excluded from publish) as a child process with a generated per-session config file and MAC address.
- Polls the edge's local management API (`N2nManagementClient`) to detect supernode registration state, connected peers, P2P-vs-relayed (`pSp`) mode per peer, and NAT type/behavior (`GetNatAsync`, surfaced to the UI as `ConnectionSnapshot.NatType`/`NatDescription`).
- Cross-references management-API peer rows against `PeerDiscoveryService` results using virtual IP, nickname, and ARP-resolved MAC address (`SendARP` via `iphlpapi.dll`) to classify each peer's `PeerConnectionMode` as `Direct`, `LanDirect`, `Punching`, `Relayed`, or `ForcedRelayed`.
- Owns unexpected-exit recovery: exponential backoff restarts (`RestartDelays`), address-conflict detection from log text ("already in use") surfaced via `ConflictingProcessDialog`, and a `_wantConnected`/`_sessionId` state machine that must stay consistent across concurrent start/stop/restart/monitor tasks — read this state machine carefully before touching connection lifecycle code, since most of the file's complexity exists to keep it race-free under `_stateGate`/`_lifecycleGate`.
- Writes full per-run logs to `%LocalAppData%\MikuN2N\logs`; `Services/LogCleanupService.cs` prunes them on a 6-hour timer per the user's configured retention (`AppSettings.LogRetentionDays`: 7/30/90/180/forever).
- Never puts the encryption key on the command line or in the on-disk config; it's passed via the `N2N_KEY`/`N3N_KEY` process environment variables only.

**`Services/N2nManagementClient.cs`** speaks two different management protocols depending on which edge binary is running:
- `N3nHttp`: JSON-RPC 2.0 over HTTP to `http://[::1]:{port}/v1` (n3n's Windows management listener is IPv6-loopback-only — don't "fix" this to `127.0.0.1`). Includes `get_nat` (NAT type/mapping/filtering report from the patched edge) and `set_peer_relay` (force a given peer to stay on the supernode relay path, i.e. `ForcedRelayed`).
- `N2nUdp`: the legacy n2n UDP text protocol (kept for the legacy `edge.exe` fallback path).

**`Services/PeerDiscoveryService.cs`** is a separate, independent-of-n2n mechanism: it broadcasts UDP hello/ack packets on port `43121` within the TAP subnet so MikuN2N clients can find each other's nicknames and measure latency via ICMP ping, regardless of what the n3n management API reports. `EdgeController` merges this peer list with n3n's own edge/mode/NAT data (see cross-referencing above) rather than trusting either source alone.

**`Services/UpnpPortMappingService.cs`** does SSDP discovery + SOAP calls to open/renew a UDP port mapping on the router (fixed n3n UDP port `50001`) and detect CGNAT (non-globally-routable external IP after mapping). This determines whether the app can hint at "public IPv4" P2P conditions.

**`Services/SettingsStore.cs`** persists `AppSettings` as JSON under `%LocalAppData%\MikuN2N\settings.json`. The encryption key is optionally persisted separately via Windows DPAPI (`ProtectedData`, current-user scope) — only when `RememberKey` is set — and is zeroed from managed memory after protecting it.

**`Services/ThemeManager.cs`** applies a resource-dictionary color palette (light/dark) at runtime and sets the DWM immersive-dark-mode window attribute; it polls the registry every 3s when following "System" theme rather than subscribing to a system event. Every window (`MainWindow`, `SettingsWindow`, `CloseBehaviorDialog`, `ConflictingProcessDialog`) calls `ThemeManager.ApplyWindow(this)` on `SourceInitialized` — new windows must do the same.

**`Services/TrayIconService.cs`** owns the WinForms `NotifyIcon` (this app mixes WPF + WinForms — `UseWindowsForms` is enabled in the csproj specifically for this) and its context menu; `MainWindow` owns the "minimize to tray vs. exit" decision via `ClosePreference`.

**`Services/AppIconService.cs`** generates the app icon at runtime (`DrawingVisual` → `RenderTargetBitmap`) rather than shipping an `.ico`, and sets the process's explicit AppUserModelID for correct taskbar grouping.

**`Services/BuildIdentity.cs`** resolves the display version string from assembly metadata (informational version, falling back to the assembly version) — read by `MainWindow`/`SettingsWindow`/tray tooltip instead of a hardcoded string.

**`Services/EasterEggManager.cs` + `EasterEggVisualController.cs`** are a self-contained, non-functional Easter egg (slot machine in Settings → flies/spiders chase animation overlay → rainbow jackpot mode). Safe to ignore/skip when reasoning about core connectivity behavior; don't let it block understanding of the real logic in `EdgeController`.

## UI theming — hard requirement

- The app has a full **light theme and dark theme**, switchable live (and a "follow system" mode) via `ThemeManager`. **Any UI change — new window, new control, new popup/dialog — must be verified and styled correctly in both themes**, not just whichever one the editor happens to be in at the time.
- **Never rely on default Windows/WPF system control styles/templates.** The stock templates for `ComboBox`, `ComboBoxItem`, `ContextMenu`, `MenuItem`, scrollbars, checkboxes, etc. render like legacy Windows-XP-era chrome — flat gray, unstyled, and visually inconsistent with the rest of the app. Every interactive control must use the app's `DynamicResource` theme palette and an explicit modern `ControlTemplate`/`Style` (see existing windows for the pattern) whenever the default template doesn't already honor `DynamicResource` brushes.
- Before considering any UI change done, visually verify both light and dark themes, including closed/open, hover, selected, checked, focused, and disabled states where applicable; a successful XAML build is not sufficient — actually run the app and look at it.

## Conventions worth knowing

- All user-facing strings are Simplified Chinese — keep new UI text and error messages consistent with the existing tone (concise, non-technical, reassuring during reconnects).
- Comments in this codebase are rare and only explain non-obvious *why* (race conditions, protocol quirks, platform quirks). Match that style rather than narrating what code does.
- The `Runtime/` folder ships third-party binaries (n3n edge, TAP-Windows installer) and their required GPLv3 license/source archive (`n3n-3.4.4-source.zip`). If you change what's bundled, keep the license/source-availability obligations intact (see `Runtime/README.txt`).
- `Runtime/n3n-edge.exe` is currently a **locally patched build**, not the stock upstream binary — this patch is the project's central technical contribution and is legitimate connectivity/interoperability engineering (RFC-aligned NAT behavior discovery and hole-punch scheduling for the client's own peer-to-peer sessions), not a security bypass or reverse-engineering tool. Current patch contents (see `Runtime/README.txt` for the authoritative, versioned list):
  1. Enables n3n's existing symmetric-NAT port-prediction hole-punch path (`register_pkt_ttl`, originally n2n PR ntop/n2n#115) on Windows — upstream wraps it in `#ifndef _WIN32` so it's dead code there — and treats `WSAENETRESET` as a non-fatal UDP receive result, since Windows reports TTL-expired probes with that code.
  2. Adds a STUN-style (RFC 8489) NAT behavior discovery probe from the n3n data socket against UDP 21001/21002 on the supernode host, classifying mapping and filtering behavior and exposing it to the management API as `get_nat` (reported to the UI as NAT1/2, NAT3, NAT4).
  3. When a direct n3n connection isn't established within a 5s priority window, runs a time-sliced multi-round port-scan burst (22s window, 250ms slices, ~12000-packet budget) targeting the peer's observed control port ± an expanding range, per NAT4-specific strategies (`tier1-cone-escape`, `tier1-low-fanout`, `tier1-layered-scan` — see `EdgeController.UpdateNatStatus` for the Chinese strings shown in the UI); falls back to supernode relay once the budget is exhausted.
  4. Fixes a Windows-specific bug where a non-blocking management-socket `WSAEWOULDBLOCK` was misclassified as fatal, which was breaking reliable delivery of `get_nat`/`get_edges` state to the UI.
  5. Fixes NAT-probe reply-source validation that was comparing raw IPv4 array addresses incorrectly, so 21001/21002 probe replies are now correctly attributed on the data socket.
  6. For NAT4↔NAT4 pairs, assigns complementary scanner/anchor roles by comparing MAC addresses (scanner sweeps a shared wall-clock time slice; anchor repeats a fixed target to stabilize its own mapping); also excludes the supernode's own endpoint from `pending_peers`. `get_edges` reports `punch_role` and current band.
  7. Upgrades the NAT3/cone-side layered strategy to a continuous wide-range sweep covering the peer's control port ±4096 in 63 non-skipping batches, while repeating the control port to keep the mapping stable — aimed at NAT3↔dual-bank-NAT4 pairs.
  8. Adds a `set_peer_relay` management method to force a given peer (by virtual IPv4) onto the supernode relay path, pausing hole-punch attempts for it (resettable). `get_edges` reports this via `forced_relay`/`force_relay`.
  9. Upgrades manual relay toggling to a two-sided session protocol: either side switching or canceling notifies the peer over the friend-sync channel and waits for an acknowledged apply before completing, rolling back the local switch on sync failure to avoid a one-sided relay.
  10. Adds configurable log retention (7/30/90/180 days or forever); checked on startup and swept every 6 hours (`LogCleanupService`).

  Patched source tree lives at `<n3n-build>\n3n-3.4.4-patched` (sibling to this repo, not checked in); original stock binary backed up at `<n3n-build>\n3n-edge.exe.original-backup`. Build toolchain (MinGW-w64, WinLibs POSIX/UCRT, gcc 16.1.0) is installed at `%UserProfile%\mingw64\mingw64\bin` (not on PATH by default — prepend it manually). Rebuild with `./scripts/hack_fakeautoconf.sh && make -j4` from the patched source dir (Windows target needs `-std=gnu17` added to `config.mak`'s `CFLAGS` or the bundled legacy `src/win32/getopt.c` fails to compile under this GCC's C23 default). If the bundled binary is ever refreshed from a stock upstream release, re-apply this patch and update `n3n-3.4.4-source.zip`/`Runtime/README.txt` to match (GPLv3 source-availability obligation).
- WPF and WinForms types collide (`Brush`, `Color`, `Point`, etc.) — existing files resolve this with explicit `using X = ...` aliases (e.g. `MediaColor`, `WpfBrush`, `Forms.NotifyIcon`). Follow the same aliasing pattern rather than fully-qualifying inline.
- App version lives in `MikuN2N.csproj` (`<Version>`); several places (`MainWindow`, `SettingsWindow`, tray tooltip) read it via `Services/BuildIdentity.cs` rather than a hardcoded string — update the csproj, not call sites.
