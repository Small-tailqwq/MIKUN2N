# Privacy and network activity

MikuN2N does not automatically collect or report personal profiles, usage statistics,
or diagnostic logs to its developers. There is no developer-operated analytics or
mandatory log-upload endpoint in normal packages. This is a statement about the
program's behavior, not a consequence of its open-source license: connecting,
checking for updates, and discovering network addresses still require network traffic.

## Local information

Settings and connection logs are stored under `%LocalAppData%/MikuN2N`.
Settings include your chosen nodes, community names, nickname and preferences.
The initial nickname comes from the device name; you can change it before connecting.
A remembered connection key is protected with Windows DPAPI for the current user.
Logs are retained locally according to Settings and diagnostic capacity limits.
They can contain addresses, peer names and network details: review them before sharing.
Opening a local log folder or saving settings does not upload them.

## Optional diagnostic upload

Diagnostic upload is optional and off by default in normal builds. It is intended
for development and connection troubleshooting, not usage tracking. Enabling the
experimental option still requires your authorization before upload. The dialog
identifies the destination and certificate; a persistent authorization is bound to
that node, receiver address and certificate. A private test build may name a receiver
in its explicit test profile; it still uses the authorization flow.

Authorized logs may include nickname, connection/session identifiers, IP/MAC addresses,
adapter and NAT status, operating-system/runtime versions, probe timings, path changes
and errors. They do not intentionally capture game payloads, connection keys, passwords
or unrelated files. Sensitive values known to the diagnostic recorder are redacted;
this is not a guarantee that every address or identifying detail is anonymous.

The recipient is the administrator of the supernode/receiver you authorize. That
administrator may be a service provider rather than a MikuN2N developer. Upload can be
refused or stopped without preventing normal play. Only newly authorized records are
eligible; stopping upload does not recall copies already received.

**Actual server retention, deletion and backups are controlled by the receiving
supernode service provider. Ask that provider for its policy or request deletion
from it. The client cannot guarantee deletion after 24 hours or any other period.**
The supplied example receiver currently uses a 24-hour expiry default; another
deployment, modified receiver or backup policy can differ. Local log retention is
a separate setting and does not control a remote server.

## Functional network requests

| Function | Destination and information visible to it | Control |
|---|---|---|
| Virtual LAN connection, NAT traversal and relay | Your configured supernode and participating peers receive protocol identifiers, addresses and connection traffic needed for the selected virtual network. Peer discovery announces your nickname, client/version and virtual-network identifiers. Relays carry forwarded traffic. | Starts when you connect; ends when you disconnect. Choose trusted nodes and communities. |
| Update checking and downloads | GitHub and its download infrastructure see normal connection metadata, including source IP and the client version in the User-Agent. The client does not attach diagnostic logs, saved node lists or connection keys to update requests. | Automatic checks are on by default in normal releases and can be disabled in Settings; manual checking is a separate request. Test packages may disable the updater entirely. |
| Experimental IPv6 address discovery | The node's observer, with Cloudflare STUN as a fallback, sees source IP and STUN requests. Public fallback requests do not carry the MikuN2N tag or game payload; they are not anonymous with respect to the source IP. DNS resolution uses the machine's configured resolver. | The IPv6 experiment is off by default. |
| Automatic network adaptation | The local router may receive SSDP/UPnP discovery and a temporary UDP 50001 mapping request. The client also configures Windows firewall and virtual-adapter properties. | On by default; disable automatic adaptation in Settings before the next connection. See [network changes and removal](UNINSTALL.md). |

GitHub's handling of its service data is described in the
[GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
Cloudflare describes its handling in the
[Cloudflare Privacy Policy](https://www.cloudflare.com/privacypolicy/).
Those services and your chosen supernode provider apply their own policies; MikuN2N
does not set their retention periods.

Use the project's issue tracker for questions about client behavior. Do not post
private logs, keys, personal node details or receiver credentials in a public issue.
