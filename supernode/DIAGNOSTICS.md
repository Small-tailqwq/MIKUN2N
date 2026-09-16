# Optional test diagnostics

The private IPv6 test build prompts on **every manual connection** before sending any diagnostic data. Declining, pressing Escape, or closing the dialog leaves logs local. Consent covers only that new connection and its automatic edge restarts. It is never saved in settings. The main window can stop subsequent uploads at any time. Disconnect/exit attempts a final flush for at most eight seconds; unacknowledged data stays local and is not uploaded by a later session.

Records include UTC, a monotonic offset, batch/session/event identifiers, build and edge hashes, network interface addresses, NAT results, peer transport/latency, connection changes and raw edge diagnostic events. IPv6 diagnostics describe address selection, candidate refreshes, probe sends and matched PONG RTT, expirations, send failures, rejection counters and traffic totals. They do not capture packet payloads, keys, passwords, unrelated files or historical logs. Known session credentials and the local user-profile path are redacted from diagnostic string fields. These records still contain personal network metadata and nicknames; the consent dialog describes that scope.

Readiness-aware clients use IPv6 wire version 2. `pong_matched` reports the peer's remaining receive lease and `data_ready`; `data_path_ready` marks selection for DATA. `version_mismatch` leaves older clients on IPv4, and `peer_readiness_expired` records fallback. `ipv4_scan_paused` and scan pause/resume events explain stopped punch counters while IPv6 is stable. No supernode wire update is needed. All participants must update for IPv6 testing.

Private test sessions record `MikuN2N latency` events for unicast probe sends, ACKs, ten-second timeouts, ICMP results, stale samples and route changes. Node/probe/generation fields correlate results without packet contents. The existing two-second probe cadence bounds routine log frequency; session log limits still apply. A changed route invalidates outstanding measurements from the prior generation. “暂无法测量” does not stop retries or imply that the friend is offline. Native PONG RTT and application tunnel RTT remain separate.

## Receiver

On the supernode, copy this directory and run as root:

```sh
bash install-diagnostics.sh --host YOUR_SERVER_HOST --batch ipv6-test-YOUR_BATCH
```

Allow TCP 5443 in both host and provider firewalls. The receiver uses a dedicated account, a locally generated TLS certificate, a batch-specific upload credential, 128 KiB requests, atomic/idempotent numbered JSONL chunks, a 64 MiB session limit and a 2 GiB total limit. Logs are accessible to administrators through SSH only; the HTTP service has no download API. Sessions expire 24 hours after first receipt, with cleanup at startup and every five minutes. This server policy also applies to older clients whose dialog still says seven days. Reinstalling updates the profile's retention field without rotating credentials or certificates. A pinned certificate in the package authenticates the server without disabling TLS verification globally. Redirects and system proxies are disabled for uploads. The certificate expires after 90 days.

Transfer `/etc/mikun2n-diagnostics/client.json` directly into a private build input; do not print it, put it in source control, or include it in the public source archive. Add a `Node` object containing only `Id`, `Name`, `Server` and `Community`. Never copy saved client settings, a user's NodeId, remembered keys, nicknames or a server private key into that input. The input has no consent setting.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:TestProfilePath="ABSOLUTE_PRIVATE_PROFILE_PATH" -o artifacts/private-ipv6-test
```

Private profiles are embedded only when `TestProfilePath` is explicitly supplied. Test settings live under `%LocalAppData%/MikuN2N/tests/<batch>/settings.json`; IPv6 is enabled and the selected private node is seeded on first launch. Existing normal-build settings remain independent. Diagnostic files are under `%LocalAppData%/MikuN2N/logs/diagnostics-*.log` and follow the client's local retention setting. A normal publish without the property has no built-in node or uploader configuration.

An optional private-profile `Ipv6StunHost` configures address discovery independently of log upload. The launcher resolves up to two global IPv6 addresses with a three-second DNS timeout; the edge sends standard STUN binding requests to UDP 3478 from its peer socket. The observer sees the source address/port and a random transaction identifier, not nicknames, community names, session tokens, application payloads or diagnostic logs. The original supernode exchanges the resulting candidate over IPv4 and remains the only diagnostic upload destination. Diagnostics include mapping lookup/request/reply/timeout events, local IPv6 interface MTU, probe budget changes and the checked per-peer UDP size. The log-upload consent flow is unchanged.

## Read both clients over SSH

```sh
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --sessions
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --contains v6diag
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --kind management_edges
sudo journalctl -u mikun2n-diagnostics.service --since today
```

Match the client session prefix displayed in its window with the server directory. Nicknames, virtual IPs and peer MACs link opposite sides. Client clocks can differ: use `received.json` for the first server receipt and `elapsedMs`/`sequence` for ordering within each client. The reader merges streams by client UTC; it cannot correct unsynchronized clocks.

Both clients must consent independently. A missing directory can mean refusal, a stopped upload or an unreachable endpoint; it is not evidence that the other client did not run. Keep local files when the UI reports an incomplete delivery. To stop collection, disable `mikun2n-diagnostics.service`; removing stored logs or replacing a batch/credential is a separate administrator action.
