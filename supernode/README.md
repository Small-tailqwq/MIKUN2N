# Self-hosting a MikuN2N supernode

MikuN2N supplies server code and deployment templates, not a hosted server. Use a Linux machine with a public IPv4 address and systemd. Participants add its address in the client and use the same community and encryption key.

For private IPv6 test builds with explicit per-connection log-upload consent, see [test diagnostics](DIAGNOSTICS.md). The optional HTTPS receiver stores both clients' logs for inspection over SSH.

## Build the matching server

Use `Runtime/n3n-3.4.4-source.zip` from the **same MikuN2N release** as the clients. It contains the modified supernode coordination code and IPv6 candidate extension. Installing an upstream n3n package alone does not reproduce the enhanced NAT4 behavior.

For Debian/Ubuntu, install the build dependencies:

```bash
sudo apt update
sudo apt install build-essential autoconf automake unzip python3 pkg-config libcap-dev libzstd-dev
```

From the MikuN2N source root:

```bash
bash supernode/build-supernode.sh
```

The build script extracts the source into a temporary directory, regenerates the Linux configure script with `autogen.sh`, builds n3n and places these files in `artifacts/supernode/`:

- `n3n-supernode`: Linux executable.
- `n3n-3.4.4-source.zip`: its corresponding patched source.
- `SHA256SUMS`: checksums using portable filenames.

`--source-archive FILE` and `--output DIRECTORY` select another release archive or output directory. Keep the source archive with any server binary you redistribute. Windows builds of the native sources do not verify a Linux build or deployment.

## Required ports

Allow all of the following in both the host firewall and the provider security group:

| UDP port | Purpose |
|---|---|
| 3076, or your chosen main port | Registration, rendezvous and relay |
| 21001 | NAT observation endpoint A |
| 21002 | NAT observation endpoint B |

The two observation endpoints **must use the same public IPv4 address as the supernode**. NAT classification compares replies from that host's different ports. The installer starts the existing Python observation service alongside n3n; it requires Python 3 and uses fixed ports 21001/21002. Deploy one such pair per host/address. Changing the service name does not create another independent pair of observation ports.

An IPv6 address is not required on the server for this IPv6 peer experiment. Both clients still need working IPv4 connectivity to the supernode for registration and rendezvous; a native IPv6-only client without an IPv4 path cannot connect. IPv6 supports global and routed-ULA/NAT66 candidates. Matching test clients can use a separately configured IPv6 STUN observer on UDP 3478 to discover the public mapping of their peer socket, then exchange it through the existing NAT66-capable supernode. This enables both peers to initiate checks, including when a public peer filters unsolicited inbound UDP. Without an observer, a ULA peer still depends on its first outbound probe reaching the public peer. A reported mapping alone never establishes a data path; peer challenge/response validation remains required. Endpoint-dependent mappings, blocked UDP and unavailable observers can still prevent IPv6 P2P. NAT66-to-NAT66 is not field-validated. Smaller paths use a 1232-byte UDP probe budget after a size error or probe timeout; data above the checked budget falls back individually to IPv4. The existing client firewall rule allows inbound UDP 50001 on all profiles but does not configure routers.

IPv6 wire version 2 also requires the peer to advertise a still-valid receive path before DATA switches to IPv6. Older client wire versions retain IPv4 connectivity. Upgrade every client for IPv6 tests; this handshake update does not require another supernode deployment. A smaller incoming probe triggers a matching size check immediately. Stable IPv6 pauses extra IPv4 scans after five seconds, preserving existing IPv4 destinations and relay fallback.

The current server also accepts optional native build/wire identity reports from registered clients and returns them to capable peers. New clients show this information in the friend list and explain IPv6 protocol mismatches. Existing clients and servers keep their prior packet layouts and IPv4 connectivity; metadata requires this server update, while direct discovery between new desktop clients can still exchange version information independently.

## Install

From the same source checkout:

```bash
sudo bash supernode/install-supernode.sh \
  --binary "$(pwd)/artifacts/supernode/n3n-supernode" \
  --community mygroup --cidr 10.42.0.0/24
```

`mygroup` and `10.42.0.0/24` are examples, not a supplied deployment. Choose a virtual subnet that does not overlap participants' physical networks. Omit `--cidr` to use automatic address allocation. Without `--community`, the generated configuration accepts arbitrary community names; edit the allowlist if you want to limit this.

The installer copies the binary and probe script under `/opt/mikun2n/n3n`, writes configuration under `/etc/n3n`, installs two systemd units and starts them. Use `--port PORT`, `--service-name NAME` or `--prefix /opt/another-directory` when needed. Keep the prefix outside home directories because the services use `ProtectHome=true`.

Existing `.conf`, community list and `.env` files are preserved on reinstall. New `--community`, `--cidr` and `--port` arguments do not overwrite existing configuration. Edit those files explicitly, then restart. Reinstallation updates service units and executables and restarts the deployment.

Default files and service names:

```text
/etc/n3n/mikun2n-supernode.conf
/etc/n3n/mikun2n-supernode-community.list
/etc/n3n/mikun2n-supernode.env
mikun2n-supernode.service
mikun2n-supernode-probe.service
```

The matching templates are always loaded from this directory, including when a custom service name is used. `community.list.template` documents the allowlist syntax.

## Connect and inspect

Add `your-server.example.com:3076` in the client's node manager. All participants need matching communities and encryption keys. The application contains no built-in address or key.

```bash
sudo systemctl status mikun2n-supernode mikun2n-supernode-probe
sudo journalctl -u mikun2n-supernode -u mikun2n-supernode-probe -n 100 --no-pager
sudo ss -lunp
```

If registration works but NAT discovery fails, check UDP 21001/21002, both firewalls and the probe unit. If registration fails, check the main UDP port and community configuration. NAT4-to-NAT4 success depends on the observed mapping behavior; relay is an expected outcome for unpredictable networks.

To try IPv6, install a server built from this release and enable the experimental setting on both clients before connecting. The peer label changes to `IPv6 P2P 直连` only while the local IPv6 path is validated. Broadcast discovery and relay still use the existing IPv4 virtual network. IPv6-only server addresses are not supported by this experiment.

## Relay traffic statistics

For server-only outbound accounting by sender and receiver, including broadcast
fan-out and legacy n2n clients, see [traffic accounting](TRAFFIC.md). The optional
local collector keeps hourly totals for 30 days and exposes no additional port.

## Federation

Multiple server endpoints can be stored in one client node. Federation requires matching configuration on the servers; see [the federation notes](../docs/FEDERATION.md) for the current limits. The installer preserves an existing federation environment file. Each public server address still needs its own observation endpoints.

## Remove

```bash
sudo bash supernode/uninstall-supernode.sh
```

This stops and removes the two service units while retaining configuration, binaries and source. Add `--service-name NAME` for a custom deployment. `--purge` additionally deletes that deployment's configuration, environment file and community list.
