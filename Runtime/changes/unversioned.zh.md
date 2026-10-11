# Runtime Change: unversioned — 早期打洞与未标版本的协调改动

Version: unversioned

> [English](unversioned.md) | [简体中文](unversioned.zh.md)

## 问题

原记录涉及 Windows 打洞、管理可靠性、联邦、资源诊断及快速变化的 NAT 映射，未标明所属版本。

## 改动记录

<a id="patch-1"></a>
### 补丁 1

Windows 启用 register_pkt_ttl 对称 NAT 打洞路径，并忽略打洞 TTL 探测产生的 WSAENETRESET。

<a id="patch-2"></a>
### 补丁 2

从 n3n 数据 socket 向 Supernode 同机的 UDP 21001/21002 观测端点执行映射与跨端口过滤探测，通过 get_nat 管理方法报告 NAT1/2、NAT3、NAT4。

<a id="patch-3"></a>
### 补丁 3

普通 n3n 直连保留 5 秒优先窗口；仍为 pSp 时执行 25 秒、250ms 时间片、12000 包硬上限的分层喷射。NAT4 本端采用 v7.3.1 已实测的跨地址 Cone 逃逸：持续发送 control，四个时间片完整覆盖 control±1..64，并逐轮双向覆盖 192 端口带，约 22 轮推进到 control±4096；预算耗尽后保留中继。

<a id="patch-4"></a>
### 补丁 4

修复 Windows 非阻塞管理连接将 WSAEWOULDBLOCK 误判为致命错误的问题，保证 get_nat/get_edges 状态可以稳定传递给主界面。

<a id="patch-5"></a>
### 补丁 5

修复 NAT 探测回包来源校验误用 IPv4 数组地址比较的问题，确保 21001/21002 回包可被数据 socket 正确识别。

<a id="patch-34"></a>
### 补丁 34

适配多 Supernode 联邦（多节点自动选路中继）。edge 侧：打洞排除表从仅 curr_sn 扩展到
conf.supernodes 全部联邦节点（强制中继 keepalive 与 Tier 1 扫描都不再误把第二个
supernode 当 peer）；NAT 探测回包（21001/21002）来源校验放宽到任一已知联邦节点，
避免 rtt 重锚定竞态丢弃回包。supernode 侧：QUERY_PEER 经联邦转发到达对端锚定的
supernode 时（from_supernode 且本地无 source_edge），信任查询内携带的 NAT 摘要与
bank 模型字段（来源 supernode 已按本地注册表校验过），照常计算并下发同 generation
的互补角色打洞计划；双 NAT4 的 GO deadline 在跨 supernode 场景由各自锚定的
supernode 独立下发，偏差不超过一个协调间隔，由按 attempt 推进的 scanner 容忍。
配套：客户端配置生成支持多条 supernode= 并在多节点时启用 supernode_selection=rtt，
主界面显示当前锚定节点与延迟。

<a id="patch-35"></a>
### 补丁 35

Windows sendto 失败后立即保存 WSAGetLastError，避免后续日志调用污染错误码；遇到
WSAENOBUFS (10055) 时额外记录 socket、报文长度、进程句柄、内存负载与可用物理内存、
known/pending peer、活跃 NAT4 worker，以及 P2P/Supernode 累计发送计数，供长期日志
对比并区分进程资源泄漏、打洞突发流量和系统网络队列瞬时耗尽。

<a id="patch-36"></a>
### 补丁 36

跨轮速率测量 + 高动态 NAT 快速落中继：bank 拟合现在跨轮测量端口分配速率（环形
距离/时间，双 bank 取最优配对）。实测（natpunch v7.2 现场数据：对端移动 CGNAT
速率每轮 150-232/s 摆动、一次 7 秒喷射内窗口漂移数千端口，17 轮 fast-target
全部落空）表明高速单 bank 无法拟合成可追踪模型，因此速率 >=120 端口/秒时直接
判定为 HARD（不可预测）：扫描端按宽窗覆盖一轮，预算耗尽即落中继，不再烧预算
追逐相位。校准样本不足时继承上轮 VOLATILE 模型而非退化为 HARD；上一轮
VOLATILE 且未观察到 cone 时保留 volatile 分类，避免 CGNAT 负载均衡短暂塌缩为
单 bank 时扫描策略振荡。Tier 1（NAT3↔NAT4 layered / cone sweep）时间片从
250ms 提速到 100ms，±4096 全覆盖从 15.75s 缩短到约 6.3s，12000 包预算与
25 秒窗口不变；bank 喷射的 low/mid 近区从散点哈希改为按 tick 确定性推进，
预算耗尽时最近（最可能）的端口已优先扫过。

<a id="patch-37"></a>
### 补丁 37

双端同步性：校准模型在 REPORTED 等待对端期间会老化（对端晚校准数秒时，
GO 时刻模型年龄可达 7s+，SYMMETRIC 的 predicted 带覆盖不了漂移量而失败）。
edge 现在检测到模型年龄超过 5s 就原地重校准（复用 worker、nonce 轮转、
清对端模型缓存，上限 2 次），把 GO 时刻模型年龄压到 5s 内；supernode 侧
报告 TTL 从 15s 收紧到 6s，拒绝用陈旧报告生成 GO。单 supernode 的 GO
时刻对齐已由 750ms 查询周期 + 3000ms GO 延迟保证（双方 go_at 均收敛到
同一 deadline + 各自 RTT/2，偏差 <50ms），无需改动；联邦场景双 SN 各自
定 deadline 的 ~400ms 偏差由 attempt 驱动扫描容忍，未改协议。
