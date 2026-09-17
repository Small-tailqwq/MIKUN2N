# Optional test diagnostics

The private test build defaults to **每次询问** on each manual connection. Declining, pressing Escape, or closing the dialog leaves logs local. One-time consent covers that connection and automatic edge restarts; it never becomes saved consent. Settings also offers **始终允许** (requires a separate informed confirmation) and **始终拒绝** (no prompt or upload). Persistent permission is bound to the selected node identity/configuration, exact HTTPS receiver and pinned certificate. Editing or switching the active node clears it, even when switching back; receiver/certificate changes require fresh permission. It is never seeded by a private profile. Applying Ask or Deny revokes an active persistent upload. Both IPv4 and IPv6 sessions, including failed connection attempts, follow this policy.

The main window can stop uploading or resume diagnostic recording/uploading without restarting the client, edge, or connection. Resume follows the selected consent mode and starts a new segment; refused/revoked records are never replayed. Each connection retains at most four 64 MiB segments (256 MiB) locally, rotating its oldest segment when full. Storage errors pause recording with a visible recovery action. Receiver outages retry within those bounds; lost unacknowledged segments are reported as incomplete delivery. Disconnect/exit attempts a final flush for at most eight seconds. A later connection never uploads earlier files.

A segment rejected with 409 (sequence/content conflict) or 410 (expired) is
abandoned for upload immediately, with a three-second backoff to avoid rapid
rotation if every new segment is rejected. Every 507 capacity response waits 30
seconds and retains the same immutable pending chunk for retry. It never abandons
or rotates a segment itself; ordinary local quota rotation can still evict old
backlog while waiting. Recovery of server capacity resumes delivery automatically.
Manual resume cancels the wait and starts a new segment under fresh/applicable
consent, keeping the rule that pre-resume records are never replayed. Other network
failures retain the immutable chunk for idempotent retry within the normal rolling
limit. A newer segment can proceed; if the current segment was rejected, the
client starts a fresh segment. Local files remain subject to their existing quota
and retention. `upload_segment_abandoned` records the segment, HTTP status and
unacknowledged bytes, and the UI marks delivery incomplete. These transitions
preserve consent and never re-enable an upload after revocation. Bytes counted as
abandoned are excluded from later quota-drop totals; the two totals are disjoint.
`upload_capacity_wait` identifies the retained segment/chunk and retry interval.

The receiver's expired-ID set is in memory. After a restart, a retry of a nonzero
chunk from an already evicted segment can return 409 instead of 410; both statuses
now take the same client recovery path. No server update or extra server state is
needed for this fix.

Records include UTC, a monotonic offset, batch/session/event identifiers, build and edge hashes, network interface addresses, NAT results, peer transport/latency, connection changes and raw edge diagnostic events. IPv6 diagnostics describe address selection, candidate refreshes, probe sends and matched PONG RTT, expirations, send failures, rejection counters and traffic totals. They do not capture packet payloads, keys, passwords, unrelated files or historical logs. Known session credentials and the local user-profile path are redacted from diagnostic string fields. These records still contain personal network metadata and nicknames; the consent dialog describes that scope.

The current clients use IPv6 wire generation 3 with directional size discovery and bounded fragmentation; see [IPv6 transport](../docs/IPV6-TRANSPORT.md). `pong_matched` reports checked bytes, the peer's receive lease and `data_ready`; `data_path_ready` marks DATA selection. `data_fragmented` and periodic fragment/reassembly counters distinguish larger-packet handling from actual fallback. `version_mismatch` leaves generation 1/2 clients on IPv4. `peer_readiness_expired`, path expiry and hard send failures record real fallback. Scan pause/resume events explain stopped IPv4 punch counters while IPv6 is stable. No supernode wire update is needed; both clients need generation 3.

Private test sessions record `MikuN2N latency` events for unicast probe sends, ACKs, ten-second timeouts, ICMP results, stale samples and route changes. Node/probe/generation fields correlate results without packet contents. The existing two-second probe cadence bounds routine log frequency; session log limits still apply. A changed route invalidates outstanding measurements from the prior generation. “暂无法测量” does not stop retries or imply that the friend is offline. Native PONG RTT and application tunnel RTT remain separate.

## Receiver

On the supernode, copy this directory and run as root:

```sh
bash install-diagnostics.sh --host YOUR_SERVER_HOST --batch ipv6-test-YOUR_BATCH
```

Allow TCP 5443 in both host and provider firewalls. The receiver uses a dedicated account, a locally generated TLS certificate, a batch-specific upload credential, 128 KiB requests, atomic/idempotent numbered JSONL chunks, a 64 MiB segment limit and a rolling 2 GiB total limit (the oldest retained segments are removed first). Logs are accessible to administrators through SSH only; the HTTP service has no download API. Segments expire 24 hours after first receipt, with cleanup at startup and every five minutes. This server policy also applies to older clients whose dialog still says seven days. Reinstalling updates the profile's retention field without rotating credentials or certificates. A pinned certificate in the package authenticates the server without disabling TLS verification globally. Redirects and system proxies are disabled for uploads. The certificate expires after 90 days.

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

Each row has a stable `connection`, increasing `segment` and connection-wide `sequence`. The HTTP `session` and directory identify a segment. Use `--connection CONNECTION_ID` to combine all retained segments, including resumed logging. Segment headers repeat minimal session/connection/native identity for correlation. Nicknames, virtual IPs and peer MACs link opposite sides. Client clocks can differ: use `received.json` for the first server receipt and `elapsedMs`/`sequence` for ordering within each client. The reader merges streams by client UTC; it cannot correct unsynchronized clocks.

Both clients must consent independently. A missing directory can mean refusal, a stopped upload or an unreachable endpoint; it is not evidence that the other client did not run. Keep local files when the UI reports an incomplete delivery. To stop collection, disable `mikun2n-diagnostics.service`; removing stored logs or replacing a batch/credential is a separate administrator action.
