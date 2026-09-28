# MikuN2N federation (multi-supernode)

> [English](FEDERATION.md) | [简体中文](FEDERATION.zh.md)

> Status: **on hold** (decided 2026-08-01). This document pins down the implemented design, deployment and known limits so a later decision on whether to continue can resume directly without re-archaeology.

## Background and goals

In n2n's single-supernode architecture, relay (pSp) traffic always flows through **the same supernode the local side registered with**, while the node a client anchors to by default may be far from the peer. Federation makes one network (a single community) form its backbone from **multiple supernodes**; each edge automatically anchors to the **lowest-latency** node, and the relay path becomes:

```
用户 A --最近节点 SN1--(supernode 骨干网)--SN2-- 用户 B
```

For friends in different regions (e.g. Guangzhou ↔ Shanghai), the theoretical latency from A to B is lower than A connecting directly to a single node that is "closer to the peer but far from you".

## Implemented so far (all in the working tree, not yet committed)

### edge side (n3n-edge.exe patch #34)

- The punch exclusion table expands from just `curr_sn` to **all federation nodes** in `conf.supernodes`: forced-relay keepalives and Tier 1 scans no longer mistake a second supernode for a peer.
- NAT probe replies (21001/21002) relax source validation to any known federation node, so an rtt re-anchor race no longer drops the reply.
- Client configuration generation supports multiple `supernode=` lines, appending `supernode_selection=rtt` when there are multiple nodes (native n3n 3.4.x option: anchor to the lowest-RTT node, keeping REGISTER per node).

### supernode side (C patch, in `native/n3n-3.4.4`)

- When a QUERY_PEER forwarded through the federation arrives at the peer's anchored supernode (`from_supernode` and no local `source_edge`), it trusts the NAT summary and bank-model fields carried in the query (the source supernode already validated them against its local registry), computes as usual, and issues the complementary-role punch plan of the same generation.
- In the cross-supernode case, the GO deadlines for dual NAT4 are issued **independently by each side's anchored supernode**; the skew stays within one coordination interval (750ms query period) and is tolerated by the attempt-driven scanner.

### client (C#)

- The server setting accepts multiple endpoints (comma/semicolon/ideographic comma/space separated).
- Configuration generation: one `supernode=` per endpoint, plus `supernode_selection=rtt` when there are multiple endpoints.
- The main window shows the currently anchored node (`get_supernodes` `current=1`) and ICMP RTT.
- `EdgeController.FormatSupernodeText` shows the user-chosen node name for the current node's address (matched against `AppSettings.Nodes` by hostname); other federation members show the raw address.

## Deployment (test federation)

Every federation member supernode host must run the natpunch responder (UDP 21001/21002):

- Reference implementation: `tools/natpunch/natpunch-server.py` (v7 protocol, `server --bind 0.0.0.0 --port-a 21001 --port-b 21002`)
- For systemd deployment, create a minimal unit that points at this script; the service name and path depend on the deployment environment.

The test federation is deployed separately from production nodes:

| Node | Endpoint | Notes |
|---|---|---|
| A | `vps.example.com:3077` | independent federation name (`/etc/n3n/*-fed.env`) |
| B | `vps2.example.com:3076` | `/etc/n3n/mikun2n-supernode.env` |

Production `vps.example.com:3076` is unaffected (separate federation name, not part of the federation).

Client test usage: enter `vps.example.com:3077, vps2.example.com:3076` in one node's `服务器地址` field, keeping the community name and encryption key unchanged.

## Known limits and risks

- **GO synchronization skew**: in cross-supernode dual NAT4 punching, each side's SN times its GO independently; skew ≈ clock difference + query phase difference (about 400ms on average), losing about 6% of the 7s window; the attempt-driven scan tolerates it. Fixing it needs a protocol field (breaking old/new compatibility), so it is not implemented for now.
- **Model freshness**: on the federation forwarding path, the peer's bank model relies on being carried by QUERY_PEER; the reported TTL is tightened to 6s and the edge auto-recalibrates at 5s age (see Runtime/README.txt #37).
- **Trust model**: cross-node punch plans trust the NAT summary the source supernode already validated — mutual trust between the federation's supernodes is a prerequisite, so it should not be deployed across untrusted administrators.
- **Compatibility**: patch #34's edge/supernode must be deployed together; an old edge connecting to multiple endpoints takes only the first (`SplitServers().First()` fallback), equivalent to ordinary single-node behavior.

## Decision and follow-up (checklist for resuming this feature)

1. Commit the current working-tree changes (edge binary + source zip + client C# + docs).
2. Server side: deploy the `natpunch.py` responder to the new node and prepare an independent federation configuration file.
3. End-to-end validation: cross-region dual NAT4 punch success rate, rtt anchor switching, and re-anchoring after link loss.
4. If continuing: prioritize the GO synchronization field (federation skew is the largest known shortcoming).
