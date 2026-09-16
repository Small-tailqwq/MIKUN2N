# MikuN2N 联邦机制（Multi-Supernode Federation）

> 状态：**暂缓推进**（2026-08-01 决定）。本文固化已实现的设计、部署方式和已知边界，
> 供后续决定是否继续时直接恢复，不需要重新考古。

## 背景与目标

n2n 家族的单 supernode 架构里，中继（pSp）流量总是经由**本端注册的同一个
supernode**，而客户端默认锚定的节点与对端可能相隔很远。联邦机制让一个网络
（同一 community）由**多个 supernode 组成骨干**，每个 edge 自动锚定到
**延迟最低**的节点，中继路径变成：

```
用户 A --最近节点 SN1--(supernode 骨干网)--SN2-- 用户 B
```

对跨地域好友（如广州 ↔ 上海）而言，A 到 B 的理论延迟低于 A 直连
"距离对端更近但离自己远"的单一节点。

## 已实现内容（全部在工作区，尚未提交）

### edge 侧（n3n-edge.exe 补丁 #34）

- 打洞排除表从仅 `curr_sn` 扩展到 `conf.supernodes` **全部联邦节点**：强制中继
  keepalive 与 Tier 1 扫描不再把第二个 supernode 误当 peer。
- NAT 探测回包（21001/21002）来源校验放宽到任一已知联邦节点，避免 rtt 重锚定
  竞态丢弃回包。
- 客户端配置生成支持多条 `supernode=`，多节点时追加 `supernode_selection=rtt`
  （n3n 3.4.x 原生选项：锚定最低 RTT 节点，逐节点维持 REGISTER）。

### supernode 侧（C 补丁，位于 n3n-build patched 源码）

- QUERY_PEER 经联邦转发到达对端锚定的 supernode 时（`from_supernode` 且本地无
  `source_edge`），信任查询内携带的 NAT 摘要与 bank 模型字段（来源 supernode 已
  按本地注册表校验过），照常计算并下发同 generation 的互补角色打洞计划。
- 双 NAT4 的 GO deadline 在跨 supernode 场景由**各自锚定的 supernode 独立下发**，
  偏差不超过一个协调间隔（750ms 查询周期），由按 attempt 推进的 scanner 容忍。

### 客户端（C#）

- 服务器设置接受多个端点（逗号/分号/顿号/空格分隔）。
- 配置生成：每端点一条 `supernode=` + 多端点时 `supernode_selection=rtt`。
- 主界面显示当前锚定节点（`get_supernodes` `current=1`）与 ICMP RTT。
- `EdgeController.FormatSupernodeText` 对当前节点的地址显示用户自己起的节点名
  （按主机名匹配 `AppSettings.Nodes`），联邦里的其他成员显示原始地址。

## 部署方式（测试联邦）

每个联邦成员 supernode 主机必须运行 natpunch 应答器（UDP 21001/21002）：

- 参考实现：`tools/natpunch/natpunch-server.py`（v7 协议，`server --bind 0.0.0.0 --port-a 21001 --port-b 21002`）
- 部署到 systemd 时自建一个最小单元指向该脚本即可，服务名与路径随部署环境而定

测试联邦与生产节点分离部署：

| 节点 | 端点 | 说明 |
|---|---|---|
| A | `vps.example.com:3077` | 独立 federation 名（`/etc/n3n/*-fed.env`） |
| B | `vps2.example.com:3076` | `/etc/n3n/mikun2n-supernode.env` |

生产 `vps.example.com:3076` 不受影响（独立 federation 名、未加入联邦）。

客户端测试用法：在一个节点的「服务器地址」里填
`vps.example.com:3077, vps2.example.com:3076`，小组名称与联机密钥保持不变。

## 已知边界与风险

- **GO 同步偏差**：跨 supernode 的双 NAT4 打洞 GO 由双方 SN 独立定时，
  skew ≈ 时钟差 + 查询相位差（平均约 400ms），损失约 7s 窗口的 6%；
  attempt 驱动扫描可容忍。修复需协议字段（破坏新旧兼容），暂不实施。
- **模型新鲜度**：联邦转发路径上对端 bank 模型依赖 QUERY_PEER 携带，上报 TTL
  已收紧至 6s，edge 侧 5s 年龄自动重校准（见 Runtime/README.txt #37）。
- **信任模型**：跨节点打洞计划信任来源 supernode 已校验的 NAT 摘要——联邦内
  supernode 之间互信是前提，不应跨不可信管理员部署。
- **兼容性**：patch #34 的 edge/supernode 需配套部署；旧 edge 连接多端点时只取
  第一个端点（`SplitServers().First()` 回退），等效于普通单节点行为。

## 决定与后续（恢复此功能时的清单）

1. 提交当前工作区改动（edge 二进制 + 源码 zip + 客户端 C# + 文档）。
2. 服务器侧：部署 `natpunch.py` 应答器到新节点，准备独立 federation 配置文件。
3. 端到端验证：跨地域双 NAT4 打洞成功率、rtt 锚定切换、断链重锚定。
4. 若继续：优先做 GO 同步字段（联邦 skew 是最大已知短板）。
