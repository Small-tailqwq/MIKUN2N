# TODO

## Pending follow-up (2026-09-18)

- [ ] Show the complete Chinese link-state text in the friends table's Link tooltip.
  - Reported symptom: hover shows English text instead of the full displayed status. `MainWindow.xaml` already binds the inner text to `ConnectionModeText`, but its rich tooltip inherits the global string `ContentTemplate` in `App.xaml`. That template can stringify the containing `StackPanel`; the other rich tooltip explicitly clears `ContentTemplate`. Preserve the full Chinese description and inspect this template interaction, including truncated or collapsed cells.
  - Acceptance: IPv4/IPv6 direct, LAN direct, punching, relay and forced-relay states have matching full Chinese hover text; check truncated cells in both themes.

- [ ] Replace first-peer-only quota exceptions with adaptive, event-prioritized diagnostics and independent local detail control.
  - Cover initial/new-peer connections, disconnects, reconnects, manual and automatic retries, direct-to-relay transitions, recovery, mapping/MTU changes, coordination failures, process exits and upload failures. A previously exhausted routine allowance must not suppress these events or the available pre-event context; reserve delivery capacity for their metadata and bounded detail windows.
  - Stable operation uses meaningful state changes and periodic counter summaries. A consented rolling context buffer supplies the preceding two minutes when an incident occurs, followed by a proposed five-minute detail window; overlapping windows merge without repeatedly copying the same context. Preserve peer/session/attempt identity and explicit gaps, and keep unfamiliar native warnings observable for future investigations. Do not claim complete packet-level observability.
  - Decouple capture, the upload outbox and user-retained local logs. Add a local-only detailed-log setting: switching to basic logs retains essential lifecycle/error/transport records and must not change upload detail, eligibility, endpoint consent or transport behavior. Bound any temporary upload spool independently and describe it separately from retained user logs.
  - Next-release update notice: explain the changed local granularity, current retention period and measured size estimate; offer direct access to the detail and retention settings. Remember the user's choice and notice version. Preserve the current 30-day default unless the user changes it; the notice is not upload consent.
  - Reduce upload volume with semantic snapshot deltas, periodic checkpoints, compressed batches and bounded HTTP connection reuse. Prioritize terminal/failure events over routine backlog, preserve idempotent retries and reconstruction order, and pace all bytes including retries. Proposed targets: 1 KiB/s average compressed routine payload per client and an 8 KiB/s incident/catch-up ceiling; validate before adopting these as defaults.
  - Capacity assessment on September 19 supports a proposed increase from 2 GiB to 4 GiB of actual receiver storage, with compressed storage, existing 24-hour expiry and at least 5 GiB filesystem headroom. These are planned settings, not deployed changes. Do not increase retention or lift quotas without considering the measured growth and compression boundaries in [the design note](docs/DIAGNOSTICS-NEXT.md).
  - Quota exceptions bypass only routine logging/upload allowances. They do not override refusal, revocation, endpoint/certificate changes, a rejected batch namespace, bandwidth ceilings or actual disk/network failures. Never upload pre-consent/refused records or earlier sessions automatically. Keep receiver support for older clients explicit; compressed requests currently require a server protocol change.
  - Acceptance for the implementation: paired connect/disconnect/retry/fallback incidents still arrive after routine quota exhaustion; stable-state compaction and decompression retain known diagnostic evidence; incident metadata bypasses ordinary backlog; local detail toggles leave the upload event stream equivalent; pacing, resume/507 behavior, consent revocation, storage pressure and short exit flushing remain bounded and observable. Validate the update notice and settings in both themes when live UI verification is authorized.

IPv4 investigation is recorded in [the September 18 analysis](docs/IPV4-PUNCH-ANALYSIS-20260918.md), including an observed zero-worker round, mismatched retry budgets, and the limits of the cone candidate model. Implementation is deferred until the next iteration, as requested; these notes do not mark either task above complete.

The [September 19 relay-pair analysis](docs/RELAY-PAIR-ANALYSIS-20260919.md) adds counterpart traces and byte-ranked priorities: established IPv4 path recovery after a receive gap, exhausted retries, and the separate older-client rollback period. Keep these findings available for the next algorithm iteration.

- [ ] Compare natpunch v7.3.1 with the integrated IPv4 path before the next algorithm iteration.
  - The supplied successful log identifies v7.3.1: 25 retained workers, a local same-server CONE observation, a symmetric peer model with two banks, and success on the first GO after 375 ms. The 180-second overall allowance does not explain this first-round success. Source reference: `tools/natpunch/natpunch.c`; private evidence is under `artifacts/natpunch-v7.3.1-reference-20260919/`.
  - Compare calibration provenance, control and bank candidate construction, per-worker send order/fan-out, GO coordination, and retaining the winning socket. The reported real endpoint falls inside a bank-relative candidate band rather than matching the control endpoint; exact winning lane/offset still requires the structured `peer_packet` record.
  - Source comparison confirms material integration differences: native workers are assigned one bank by parity while the demo rotates each worker across bank candidates; native uses sequential low/mid scheduling, fixed calibration waits, a longer coordinated GO delay, serialized pending worker attempts and a three-round failure budget. Record candidate-order differences separately from measured connectivity: an earlier scheduled probe does not prove it caused the historical success.
  - The supplied sample concerns a different pair from the current relay-heavy failure. Use paired logs from the same endpoints and comparable mapping conditions/budgets before attributing a success-rate advantage to the integration or generalizing the result. Keep short handshake success separate from sustained tunnel traffic.

## Implemented in 0.5.8-2; live acceptance pending

- [x] Replace coupled IPv6 probe-size fallback with directional size discovery and bounded tunnel fragmentation.
  - Evidence from 0.5.8-1 on 2026-09-17: a 1418-byte probe timeout reduced the limit to 1232; later successful 1418-byte checks were undone by incoming 1232-byte peer probes. Encapsulated 1383-byte DATA fell through to IPv4 relay while smaller DATA remained direct.
  - Separate directional receive capability, confirmed outbound datagram size, liveness and larger-size searches using RFC 8899 principles. Keep safe DATA delivery during larger-probe loss, validate endpoint/session/challenge, and fragment/reassemble oversized tunnel packets with strict bounds.
  - Acceptance: asymmetric sizes, lost probes, duplicates, reordering, fragment expiry, real path loss and protocol-version mismatch remain safe; both public IPv6 and NAT66 need live acceptance on the delivered build.

- [x] Add persistent host-interface egress accounting alongside per-peer relay counters, with matching report windows and explicit coverage boundaries for cloud-bill comparison.

- [x] Fix the brief reconnect indication during supernode registration renewal in an established session.
  - Evidence from 0.5.8-1 on 2026-09-16: a pending registration acknowledgement (`get_supernodes.current=2`) produced a Reconnecting snapshot with an empty peer list for approximately 885 ms while IPv6 P2P probes and application UDP replies continued.
  - Before this fix, `Services/EdgeController.cs` mapped Waiting directly to Reconnecting and skips peer polling. Distinguish initial registration, short renewal waits, and sustained failures; retain live peers and uptime during renewal and continue evaluating actual path health. Keep management polling and discovery callbacks consistent so a discovery update cannot overwrite a real failure.
  - Acceptance: routine renewal does not flash Reconnecting or clear friends on IPv4 or IPv6; actual registration loss, edge exit, and peer-path expiry still produce accurate status. Live acceptance remains pending for the next release.

- [x] Add a persistent diagnostic-upload preference to Settings: `每次询问`, `始终允许`, and `始终拒绝`.
  - Default new and migrated settings to `每次询问`; keep the current per-manual-connection prompt in that mode, including refusal when the dialog is dismissed. One-time consent must not silently become persistent permission.
  - `始终允许` must be explicitly selected by the user after the upload destination and collected data are explained; subsequent connections may upload without repeated prompts. Never preconfigure persistent consent in a distributed private profile.
  - `始终拒绝` skips the prompt and prevents uploads while retaining local logging and normal connectivity. Applying this preference during an active upload must stop further uploads. Preserve the existing stop-upload control and allow the preference to be changed later.
  - Preserve collection for both IPv4 and IPv6, including failed connection attempts. Upload authorization and transport selection remain independent; neither IPv6 availability nor successful supernode registration is a prerequisite for the upload worker.
  - Bind persistent permission to the current node, receiver URL and pinned certificate; edits and switches invalidate it, including a switch back. Keep the receiver's 24-hour retention and update the consent dialog, status text, and `supernode/DIAGNOSTICS.md` to match the selected mode. Continue excluding payload contents, credentials, unrelated files, and historical sessions.
  - Acceptance: all three modes persist across application restarts; IPv4-only and IPv6 sessions follow the same policy; refusal, revocation, and unreachable-receiver behavior remain correct; Settings and consent UI work in both themes. Live acceptance remains pending for the next release.

- [x] Support continuing and resuming diagnostics without disconnecting peers or restarting the application/edge.
  - In 0.5.8-1, reaching the 64 MiB diagnostic file limit permanently sets `TestDiagnosticsSession._storageFailed` for that object. The UI only supports stopping uploads; a new diagnostic object is created on a manual connection. Server-side cleanup or increasing a receiver limit cannot resume client-side recording.
  - Evidence from the 2026-09-16 investigation: a host's detailed recording stopped at the size limit before its peer began uploading, preventing a simultaneous trace while the game session continued.
  - Use bounded rolling diagnostic segments and coordinated receiver limits so a long connection can continue recording/uploading after a segment fills. Preserve connection identity and event ordering across segments for analysis, the receiver's 24-hour retention, and bounded disk usage during receiver outages.
  - Provide an in-session resume/start action for recoverable recording/upload stops, with an accurate visible recording/delivery status. Keep the existing edge process, virtual adapter, and peer paths running throughout.
  - Apply the selected upload-consent mode and any explicit revocation. Automatic rotation may preserve valid consent for the current connection; it must not re-enable uploads after refusal or revocation, upload historical sessions, or introduce silent consent in packaged defaults.
  - Acceptance: filling a segment and resuming after a recoverable stop do not reset peers or connection uptime; records remain correlatable without duplicate acknowledged segments; disk/receiver failures are visible and bounded; upload denial/revocation and 24-hour server retention still hold. Live acceptance remains pending for the next release.

## Current diagnostic coverage

The private build creates `TestDiagnosticsSession` before calling `EdgeController.StartAsync`, independently of `ExperimentalIpv6P2p`. With consent, the HTTPS upload worker starts before connection success. Ordinary public builds without a private upload profile do not automatically upload logs.

Uploaded session records include raw edge output, connection snapshots, network interfaces, management errors, supernode state, peer rows, and NAT results. IPv4 peer rows expose punch state, attempts, elapsed time, and packet counts; native logs include NAT mapping/filtering, coordination, bank calibration, and punch-budget outcomes. This supports comparison of both endpoints when diagnosing IPv4-only P2P failures.

Coverage is not a packet capture: peer/NAT management polling requires initial registration and continues through a bounded ten-second renewal grace period, only emitted native log levels are recorded, and upload delivery still requires consent and receiver reachability. A missing upload is not proof that a client did not run. Failed deliveries stay local and are not automatically replayed by a later session.

## Validation status

The checked items above refer to implementation and cold validation, not completed
live acceptance. The native wire and generation-3 regression checks, client build,
offline consent/rotation checks and collector/receiver checks passed. They cover
asymmetric size limits, larger-probe loss, fragment reorder/duplicates/expiry,
session/endpoint rejection, send errors, permission persistence/revocation,
no historical upload, bounded rotation, accounting windows and receiver retention.

September 18 consented logs provide an initial public-IPv6/NAT66 pair observation
with both clients on 0.5.8-4: the initiating client reports a ready IPv6 path about
one second after session start, and both sides exchange data. The observation ends
with the peer's normal process exit about five and a half minutes later. One
39 ms readiness-expiry/recovery interval remains visible. No fragmented DATA is
recorded in this sample; it does not establish sustained game or MTU acceptance.

Still pending: broader public IPv6/NAT66 pair coverage, sustained game traffic,
registration renewal/real failure in the live UI, both WPF themes and billing-window
comparison after sufficient host samples accumulate. Historical unrecorded traffic
cannot be recovered. See docs/IPV6-TRANSPORT.md for the packetization contract.
