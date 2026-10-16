# Network changes and removal

## What automatic network adaptation does

The Settings option `自动适配联机网络（推荐）` is on by default. When connecting it:

- Allows inbound UDP 50001 on all Windows firewall profiles for tunnel traffic.
- Allows inbound UDP 43121 from the virtual subnet for peer discovery.
- Allows inbound traffic from the virtual subnet for LAN game traffic and ping.
- Attempts to set the selected virtual adapter's network category to Private and
  its IPv4 interface metric to 1, so game broadcasts use the tunnel.
- For n3n, attempts a UPnP UDP 50001 mapping on a supported local router. The
  requested lease is 600 seconds and is renewed while the connection is active.

The firewall rules are scoped by port and/or virtual subnet, **not by executable
path**. Other programs listening on an allowed port or receiving traffic from that
subnet can be affected. The rule names are `MikuN2N 隧道传输`, `MikuN2N 好友发现`
and `MikuN2N 虚拟局域网`.

You can disable adaptation in Settings before connecting. Saving this option changes
the next connection, not the running session, and does not disconnect you. The next
session skips these supplemental firewall, Private-category, priority and UPnP
operations, and passes metric 0 to n3n (leave the metric unchanged). Normal virtual
IP/subnet assignment, TAP use and connection-conflict recovery remain part of making
the virtual LAN work. A user-supplied legacy edge can have behavior outside this
client's control.

Disabling adaptation does not undo earlier firewall or adapter changes. If disabled,
Windows or router filtering and broadcast routing can prevent play or room discovery;
configure those facilities yourself or re-enable adaptation for a later connection.
The option does not install a TAP driver: that installation requires the separate
`安装网络组件` button and the original OpenVPN installer.

## Disconnecting and removing MikuN2N

Perform cleanup only after you choose to disconnect and exit **all** MikuN2N instances
that use these facilities. Do not remove a shared adapter or rules while another
game client, VPN or connection relies on them.

1. Disconnect and exit MikuN2N. The client/edge attempts to return a tuned adapter
   to automatic metric on shutdown; this is best effort, especially after a crash.
   It does not automatically delete the firewall rules or restore the former network
   category. UPnP renewal stops; a router honoring the requested lease should expire
   the mapping within about ten minutes. The client does not send an immediate
   DeletePortMapping request.
2. The client is portable: remove its extracted application directory when it is no
   longer running. No client uninstaller or startup service is installed by that ZIP.
3. If you want to remove saved settings and local logs too, optionally delete
   `%LocalAppData%/MikuN2N` in File Explorer. This includes remembered credentials and
   private test settings. Deleting local files does not delete uploaded server copies;
   request those from the supernode provider.
4. To remove the three firewall rules, open **Windows Defender Firewall with Advanced
   Security**, inspect Inbound Rules and remove the exact names listed above. These
   names are shared across MikuN2N versions. Do not remove unrelated rules.
5. If needed, inspect the correct virtual adapter's IPv4 advanced properties and
   restore **Automatic metric**. Restore its previous Public/Private category only
   if you know that previous value; the client does not keep a full original-state
   snapshot. Do not change the physical Ethernet/Wi-Fi adapter by mistake.
6. If no other application needs the TAP adapter/driver, uninstall **TAP-Windows**
   using its original entry in Windows Installed Apps (or Programs and Features).
   Do not delete `.sys` files or change driver-signing settings. If a router retains
   the UPnP entry after lease expiry, review that router's own mapping controls.

This document describes actions for the person operating the computer. Reading it,
opening Settings or building the project does not execute these cleanup steps.
