# Supernode outbound traffic accounting

The patched supernode exposes `get_relay_stats` on its existing local management
socket. `relay-traffic.py` samples it every 60 seconds and retains hourly SQLite
totals for 30 days. No client update, public listener, packet capture, or client
log upload is involved. Only server-observed delivery metadata and counters are
stored; the client diagnostic-upload consent flow remains independent.

## Read over SSH

```sh
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 72 --top 30 --hourly
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 24 --json
sudo systemctl status mikun2n-traffic --no-pager
```

The report shows all egress, category totals, directed deliveries, and senders
ranked by the server egress they caused, including broadcast fan-out. Names and
virtual IPs are the latest observed registration labels; MAC plus community is
the accounting key. Offline peers retain labels for 30 days. Plain n2n clients
are included even when they do not appear in the MikuN2N friend-discovery UI.
An absent label falls back to the MAC. MAC rotation creates a separate identity;
names, MACs, and registration labels are not verified person identities.

## Accounting boundaries

* `pSp`: a successful unicast DATA send to a locally registered destination edge.
* `broadcast-copy`: one successful DATA copy to one destination edge. Sending
  1 KiB to ten recipients counts as 10 KiB of server egress caused by the sender.
* `federation-unicast`: DATA sent toward a known remote destination via another
  supernode. The destination key is the final edge MAC.
* `federation-flood`: one DATA copy sent to a federation supernode. The destination
  key is that supernode's MAC, not a final client. This includes unknown-unicast
  flooding. These categories must not be interpreted as local client pSp links.
* `all-egress`: positive return bytes from the supernode's network `sendto`
  helper, including control traffic, TCP framing, and partial TCP sends.
  Send errors are counted separately. Pair totals count complete DATA sends.
* Bytes are n3n datagram/stream bytes accepted by the OS, excluding IP/UDP/TCP/link
  headers and kernel retransmissions. Success does not prove remote delivery.
  The residual against pair totals is approximate because pages are read at
  slightly different times. It includes control traffic and incomplete sends.
* Direct IPv4/IPv6 peer traffic bypasses the server and is not measured. An entry
  proves relay usage during the selected period, not a permanent connection mode.
  This data does not recover traffic before installation or reveal game payloads.

There are at most 8192 directed flow keys per supernode process; additional
untracked DATA remains visible in `flow-overflow`. Existing keys keep counting.
There is no file I/O per packet. Counter checkpoints and hourly deltas commit in
one SQLite transaction, so restarting the collector does not double-count.
Process identity includes the host boot ID, PID, and supernode start time.

Uncollected traffic at a supernode crash/restart is lost (normally up to one
sampling interval, longer during collector downtime). Collector downtime alone
does not lose cumulative bytes while the same supernode continues running.
Recovered deltas are assigned to the collection hour, not reconstructed across
the missing hours. Reports display sampling freshness, observed restarts, and
gaps over 180 seconds. The first sample includes the current process lifetime.
Requested periods round down to UTC hour boundaries; the current hour is partial.

## Installation

First build and deploy the matching patched supernode, keeping its corresponding
source archive and a backup of the previous binary. The native patch adds a
read-only management method; it does not change the wire protocol, routing, node
configuration, or client behavior. Updating the binary requires one supernode
restart. The separate collector can subsequently restart without restarting n3n.

```sh
sudo install -d -m 0755 /opt/mikun2n/traffic
sudo install -m 0755 supernode/relay-traffic.py /opt/mikun2n/traffic/relay-traffic.py
sudo install -m 0644 supernode/mikun2n-traffic.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mikun2n-traffic.service
```

The default socket is `/run/n3n/mikun2n-supernode/mgmt`. For another instance use
a service override with `--socket PATH --db PATH` in `ExecStart`; never share a
database between different supernodes or simultaneous collectors. A second
instance such as a federation test service needs its own deployment and database.

The collector is restricted to Unix sockets and stores the database under
`/var/lib/mikun2n-traffic/traffic.sqlite3` (directory mode 0700, umask 0077).
No additional firewall port or supernode filesystem permission is required.
SQLite reuses freed pages after retention cleanup; file size follows its previous
high-water mark. Stop the collector and restore the saved server binary to roll
back; preserve the database for later analysis.
