# P2P 打洞技术融合进 MikuN2N 方案

> [English](P2P-PUNCH-INTEGRATION.md) | [简体中文](P2P-PUNCH-INTEGRATION.zh.md)

更新时间：2026-07-24
状态：Phase A 已落地；Phase B 双 NAT4 互补角色已进入 0.4.1 测试版

## 背景与目标

现有 0.3.1 测试构建里，n3n 对**普通 NAT**（cone↔cone、cone↔sym、可预测 sym↔sym）
能自己打通 P2P；但对**难例**（云电脑=锥形+端口受限、主力机=对称+端口受限）打不通，
一直停在 supernode 中继（pSp）。`natpunch` v6/v7 系列已在独立工具里**实测证明**：
对可预测 NAT4，用「测量 bank/漂移 + 服务器 GO 同步 + 双方同时向对方预测端口窗口喷 」
能建立双向直连（v6 第 51 轮打通 主力机↔云电脑）。

本方案把这套专业打洞能力优雅地融合进产品，分层递进、失败快速回退。核心约束与已定架构：

- **打洞必须打在 n3n 的数据 socket（50001）上**。NAT 映射是「按 socket + 按目的地」的，
  用独立进程/独立 socket 打出来的洞不能给 n3n 数据口用。所以真正的 bank 预测 + 同步喷射
  **落在 n3n 补丁二进制里**（`edge_utils.c`），不在 C# 侧。
- **两端交换 NAT 模型与 GO 同步信号走 n3n supernode**（它本就是可信集合点、已知道两端公网端点）。
  生产环境不再依赖独立的 natpunch 服务器；natpunch 保留为研究/诊断工具。
- **MikuN2N（C#）负责**：NAT 类型检测的展示、打洞状态的诚实呈现、策略/预算开关（通过生成的
  n3n 配置下发）、以及端到端联调。

## 分层策略（与 NATPUNCH-V7阶段总结 §6 一致）

- **Tier 0 — n3n 原生**：正常 REGISTER 交换 + 现有周期性打洞补丁。覆盖大多数非难例 NAT 组合。
- **Tier 1 — 升级打洞（本次新增）**：某 peer 停在 pSp 超过阈值 T 秒后，双方进入一轮**协调打洞**：
  1. 各自在数据 socket 上测量本端 NAT（两个 bank 顶部 + 漂移速率 + 模式）。
  2. 通过 supernode 中继交换 NAT 模型 + 一个同步 GO（新的边到边控制消息）。
  3. 双方在 GO 时刻**同时**向对方预测端口窗口喷 n3n `REGISTER`（命中即建立真实 P2P 路径，
     `last_p2p` 刷新 → n3n 自动切 p2p）。
  4. 有**预算**（时间/发包上限）；预算内没打通就退回中继（对 fast-cycle/多出口 CGNAT 不死磕）。
- **Tier 2 — 优雅放弃**：hard / fast-cycle / 多公网 IP CGNAT，接受中继，诚实展示状态。

## 各组件落点

| 能力 | 落点 | 说明 |
|---|---|---|
| NAT 自测量（bank/漂移/模式） | n3n edge，数据 socket | 端口按目的地分配，必须同 socket 测。需两个观测视角 → supernode 加**第二个 UDP 观测端口**（STUN-lite，只回显观测到的源 ip:port），替代 natpunch 的 A/B 双端口 |
| 模型 + GO 交换 | supernode 中继的边到边控制消息 | supernode 已在社区内转发边到边包，捎带一小段打洞协调载荷是自然扩展 |
| 预测 + 同步喷射 | n3n edge `mikun2n_punch_peer` 升级 | 从固定 ±900 盲喷改为「测量 bank + 预测窗口 + attempt 加盐 + GO 同步」，用 `send_register` 发包 |
| NAT 类型 / 打洞状态展示 | MikuN2N C# | 新增管理调用读取 edge 算出的 NAT 分类；诚实展示「打洞中 / 打洞失败走中继」 |
| 策略/预算开关 | MikuN2N C# → 生成 n3n 配置 | 测试构建可不重编即调阈值/预算/开关 |

## n3n 原补丁基线（Phase B 之前）

- `mikun2n_punch_peer` 原实现是**固定盲喷** —— 近窗 `base-8..+80` + 兄弟引擎
  `base±[850..1020]`，基于双活网关的固定假设，不测量、不预测漂移、不与对端同步。
- 主循环里每 2 秒对所有非 local、`last_p2p` 陈旧的 AF_INET peer 调一次（`edge_utils.c:3256`）。
- `get_edges` 管理输出（`management.c:582` `jsonrpc_get_edges_row`）每行已含：`mode`(p2p/pSp/sn)、
  `ip4addr`、`macaddr`、`sockaddr`（**对端 supernode 观测到的公网 ip:port**）、`prefered_sockaddr`、
  `local`、`last_p2p`、`time_alloc`、`last_seen`。C# 目前只读了 `mode`/`ip4addr`/`desc`/`macaddr`，
  `sockaddr`/`last_p2p` 已到手但未用 —— Tier 1 协调所需的对端公网端点无需改协议即可拿到。

## natpunch 可直接移植的资产（研究结论 → 生产）

- **NAT 分类** `fit_model`（`tools/natpunch/natpunch.c:344`）：cone / sym / hard / volatile / fast，
  两个 bank 顶部、step、spread、drift rate、fast-cycle 粘滞。逻辑自包含、带自测，可整体移植到 edge C。
- **打洞调度** `build_targets`+`spray_attempt`+`handle_peer_packet`：分层窗口（low 1..64 / mid 65..256 /
  predicted / tail）、control lane、fast-target/fast-sender、attempt 加盐、GO 同步、收到 P7 从**收包
  socket 向真实源**回 A7（这是端口受限/对称能通的关键）。
- **协议要点**：核心是「测量不能污染计数器 → 两端 GO 同刻按序向对方 `[bank..+N]` 递增喷 → index i
  命中对方第 i 个映射」。移植到 n3n 时载荷换成 n3n `REGISTER`，集合换成 supernode 中继。

## 实施阶段

### Phase A — C# 基础与诚实状态（本次已落地，可编译验证）

- `PeerConnectionMode` 增加 `Punching`；`ConnectionModeText` 增加「打洞中…」。
- `EdgeRun` 增加 `RelayedSince`（按虚拟 IP 记录进入中继的时刻）。
- `PublishConnected`：peer 刚进入 pSp 的前 `PunchDisplayWindow`（25s）内展示为「打洞中…」，
  超时仍中继则展示「pSp 中继」；详情行增加「N 位打洞中」。窗口时长后续与 Tier 1 打洞预算对齐。
- 不改协议、不改 n3n，纯显示层改进，反映「n3n 正在为该 peer 打洞」的真实过程。

### Phase B — n3n edge：测量 + 自适应喷射（需重编 + 本地 sym↔sym 实测）

- 移植 `fit_model` 到 edge，在数据 socket 上周期测量本端 NAT；通过新管理方法 `get_nat` 暴露分类。
- 升级 `mikun2n_punch_peer`：用测得的 bank + 漂移预测窗口 + attempt 加盐，替换固定 ±900 盲喷
  （即便还没有 supernode 同步，也是增量改进）。
- 用两台在用户手里、都挂新版的机器（主力机对称 ↔ 租用机/云电脑）做 sym↔sym 回归。

首轮测试实现（2026-07-24）：

- 复用已部署在 Supernode 同机的 natpunch A/B 观测端口 `21001/21002`，由 n3n 自己的
  数据 socket 顺序执行「A 映射 → A 请求 B 主动回打 → B 映射」，避免独立 C# socket
  的结果与实际隧道 socket 脱节。
- `get_nat` 暴露映射、过滤、观测端口和策略；0.3.1 主界面显示 NAT1/2、NAT3、NAT4。
- n3n 原生 Tier 0 保留 5 秒；仍停在 pSp 才触发 20 秒 Tier 1。主循环在此期间使用
  250ms 时间片，普通/NAT3 端完整重复 control + low 1..64 并轮换 mid/far，NAT4
  本端使用低扇出固定 anchor；每 peer 最多 9000 包，失败后保留 pSp。
- `get_edges` 增加 `punch_state`、耗时、attempt 和发包数，日志保留开始、成功转换和
  预算耗尽原因。

当前边界：这是不改公网 supernode 二进制的 Phase B 灰度方案，解决了旧实现受
10 秒 `select()` 休眠影响、每两秒整段盲喷且无限重试的问题。它尚未实现 Phase C
的双方 NAT 模型交换和严格 GO 同步，因此 NAT4↔NAT4 仍不能承诺直连；云电脑↔本机
必须用两端新版进行真实网络回归后再决定是否进入 Phase C。

0.4.0 测试版增量（2026-07-24）：

- 独立工具 v7.3.1 在跨 ISP NAT4 场景验证：同一服务器 IP 的 A/B 端口均复用
  `59832`，但访问真实 Peer 时出口为 `59849`，即跨目的 IP 偏移 `+17`；旧版把
  这种局部稳定误当成真实 Cone，只攻击 control，连续 21 轮失败。
- n3n NAT4 发送端改用跨地址 Cone 逃逸：每 250ms 保留 control，并把
  `control±1..64` 与本轮双向 192 端口带分成四片；1 秒完整覆盖 513 个候选，
  下一轮继续向外推进。
- Tier 1 预算调整为 22 秒/12000 REGISTER，每轮输出 peer control、near 与
  rotating 范围；成功日志使用 14 位带符号偏移，可准确记录 `±4096` 范围命中。
- 非 NAT4 的 layered low/mid/far 路径、n3n 原生 Tier 0 和失败后的 pSp 中继均保持不变。

0.4.1 测试版增量（2026-07-24）：

- 0.4.0 双端实测确认双方均为 NAT4 时会同时高扇出，单一 n3n 数据 socket 因
  目的相关映射持续产生新出口，双方发送约 9000 个 REGISTER 仍然零 P2P 命中。
- 双 NAT4 端依据本机/对端 MAC 的稳定排序得到互补角色：一端为 scanner，另一端
  为 anchor；两端独立计算时必然得到相反结果，不需要新增服务端协议。
- scanner 的 phase/round 改用 250ms 公共墙钟槽，即使两端启动时间相差约一秒也能
  对齐；anchor 只重复固定的 `control/±1/±2/.../±256` 集合，降低映射持续漂移。
- Tier 1 明确排除当前 Supernode endpoint，不再对公网 `3076` 误扫完整预算。
- `get_edges` 和运行日志新增 `punch_role`、`punch_band_lo/hi`，用于后续双端对齐分析。

### Phase C — supernode 协调 + 同步喷射（需重编 supernode + 重新部署到 vps.example.com，⚠需用户确认）

- supernode 增加第二个 UDP 观测端口（STUN-lite 回显源 ip:port）。
- 新增 supernode 中继的边到边打洞协调 PDU：转发对端 NAT 模型 + 下发共同 `attempt` 与 GO 时刻。
- edge 收到 GO 后在数据 socket 上同步喷射；命中即 `last_p2p` 刷新、n3n 自动切 p2p。
- **部署注意**：正式节点上有真实用户，重部署属对外、不易回滚操作，需先取得同意；先在旁路端口/测试小组灰度。

### Phase D — C# 收口与端到端

- 已完成：C# 读 `get_nat` 展示本端 NAT 类型，读取 `punch_state` 诚实区分打洞中与预算耗尽中继。
- 待完成：策略/预算 UI；云电脑↔本机端到端打通验证。

## 验证方式

- Phase A：`dotnet build` 通过；本机连接后，一个中继 peer 前 25s 显示「打洞中…」，之后转「pSp 中继」。
- Phase B/C：MinGW 重编 n3n（`native/n3n-3.4.4`，
  `./scripts/hack_fakeautoconf.sh && make -j4`，`config.mak` 的 CFLAGS 需 `-std=gnu17`），
  两台在手机器实测 sym↔sym；日志核对 `last_p2p` 是否刷新、是否出现双向直连。
- 每次难例结果保留模型/attempt/lane/offset/首命中/中继原因，沿用 natpunch 研究口径。

## 关联

- 研究结论：`NATPUNCH-V7-SUMMARY.zh.md`、记忆 `nat-p2p-breakthrough`、`natpunch-tool`。
- 补丁说明：`CLAUDE.md` 的 Runtime 段（打洞补丁、构建工具链）。
