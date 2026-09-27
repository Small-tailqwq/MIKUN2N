此目录在构建和发布时会原样复制到程序目录。

必须文件：
- n3n-edge.exe：n3n 3.4.4 Windows 客户端（新版优先使用）。当前为本地修改版，非官方原版二进制：
  1. Windows 启用 register_pkt_ttl 对称 NAT 打洞路径，并忽略打洞 TTL 探测产生的 WSAENETRESET。
  2. 从 n3n 数据 socket 向 Supernode 同机的 UDP 21001/21002 观测端点执行映射与跨端口过滤探测，通过 get_nat 管理方法报告 NAT1/2、NAT3、NAT4。
  3. 普通 n3n 直连保留 5 秒优先窗口；仍为 pSp 时执行 25 秒、250ms 时间片、12000 包硬上限的分层喷射。NAT4 本端采用 v7.3.1 已实测的跨地址 Cone 逃逸：持续发送 control，四个时间片完整覆盖 control±1..64，并逐轮双向覆盖 192 端口带，约 22 轮推进到 control±4096；预算耗尽后保留中继。
  4. 修复 Windows 非阻塞管理连接将 WSAEWOULDBLOCK 误判为致命错误的问题，保证 get_nat/get_edges 状态可以稳定传递给主界面。
  5. 修复 NAT 探测回包来源校验误用 IPv4 数组地址比较的问题，确保 21001/21002 回包可被数据 socket 正确识别。
  6. 0.4.1 在双 NAT4 时按双方 MAC 确定互补的 scanner/anchor 角色：scanner 使用共同墙钟时间片扫描，anchor 重复固定目标以稳定映射；同时排除误入 pending_peers 的 Supernode 端点。get_edges 额外报告 punch_role 和当前 band。
  7. 0.4.2 将 NAT3/锥形端的 layered 策略升级为连续宽域扫描：用不跳号的 63 批次对称完整覆盖 peer control±1..4096，同时重复 control 保持稳定映射，重点支持 NAT3↔双 bank NAT4。
  8. 0.4.2 增加 set_peer_relay 管理方法：可按虚拟 IPv4 强制该 peer 的发送路径使用 Supernode、暂停增强打洞，并可取消后重置打洞预算。get_edges 通过 forced_relay/force_relay 报告状态。
  9. 0.4.3 将手动 pSp 升级为双端会话策略：任一方切换或取消时通过好友同步通道通知对端，收到对端应用确认后才完成操作；同步失败会撤销本机切换，避免单向中转。
 10. 0.4.3 增加日志保留时间设置（7/30/90/180 天或永久），启动后立即检查并每 6 小时清理一次过期日志。
 11. 0.4.4 让 set_peer_relay 返回真实命中的 peer 条目数（原先无论是否匹配都返回 1），使主程序能区分"策略已记录"与"策略已生效"。
 12. 0.4.9 修复 MAC 指定在标准 TAP 驱动上完全失效的问题：原版只写非标准的注册表值 MAC，而
     OpenVPN 官方 tap-windows 驱动读的是标准 NDIS 值 NetworkAddress，导致配置的 MAC 被静默忽略、
     edge 始终以网卡内置 MAC 注册。现在两个值都写。（实测验证：修复前配置 MAC 不生效，修复后生效，
     且"固定 MAC 快速重连被 supernode 拒绝 -> 换随机 MAC 立即恢复"这条路径随之成立。）
 13. 0.4.9 注册表 MAC 与目标一致时跳过整个禁用/启用网卡循环，避免每次连接都重置网卡与
     Windows 网络位置；连接名为空的网卡不再被选中；重新打开网卡失败后追加一次 enable 重试，
     不把网卡留在禁用状态。
 14. 0.4.9 将 stdout 设为无缓冲：edge 作为子进程运行时 stdout 是管道、默认全缓冲，进程异常
     终止会丢掉全部网卡配置诊断信息。
 15. 0.4.9 把 TAP_IOCTL_SET_MEDIA_STATUS（将网卡置为已连接）提前到配置 IP 之前执行，
     使 netsh 与 GetAdaptersInfo 面对的是一块已就绪的网卡。
 16. 0.5.0 修复手动 pSp 在一端静默失效的问题。n3n 只从 REGISTER 学到对端的虚拟 IPv4，
     先从数据包（PACKET）建立的 peer 条目 dev_addr 恒为 0，按 IPv4 下发的强制中继就匹配 0 条，
     形成本机 P2P、对端 pSp 的单向中继（表现为延迟在两个值之间横跳）。改动三处：
     set_peer_relay 增加可选的第三个参数 MAC，策略同时按 IPv4 与 MAC 两个键保存和匹配；
     已存在的 peer 条目在任何带 dev_addr 的报文到达时补学该地址；socket 变化重建条目时
     沿用原有 dev_addr，不再丢失。（实测验证：用错误 IPv4+正确 MAC 下发，旧版匹配 0 条、
     新版匹配 1 条并在 get_edges 中反映为 forced_relay。）
 17. 0.5.1 修复 metric 配置项在 Windows 上被静默忽略的问题：原版设置
     MIB_IPINTERFACE_ROW.Metric 时没有清除 UseAutomaticMetric，Windows 会继续按链路速率
     计算跃点，配置值从不生效。这一点直接影响局域网联机——224.0.0.0/4 与 255.255.255.255/32
     这两条路由在每块网卡上都存在且 RouteMetric 相同，平局由接口跃点决定，默认物理网卡更低，
     游戏的房间广播就从物理网卡发出、进不了隧道。关闭隧道时同时把 UseAutomaticMetric 交还
     Windows。（实测验证：修复前 metric=1 无效、跃点仍为 25；修复后跃点变 1，
     255.255.255.255 的首选出口从物理网卡切换到 TAP。）
 18. 0.5.2 把 NAT4 scanner 的 round/phase 从墙钟改回按 punch_attempt 计数推进。墙钟版
     看似能让两端同步，实际不能：实测两端系统时钟相差 3.65 秒（14 个 tick），双方算出的
     band 本就不同；而主循环实测只有约 3.1 tick/s（标称 4），墙钟版每丢一个 tick 就在扫描
     范围里留下一个永久空洞（实测轮转带覆盖率仅 78%~81%，且起手落在最远的低概率带）。
     改回按 attempt 计数后，band 连续、由近及远，预算耗尽只会丢掉最远最不可能的尾部。
 19. 0.5.2 让打洞主循环按固定 deadline 唤醒：原先 select 固定等 250ms、醒来后再把
     last_punch_ms 重置为"当前时刻"，250ms 睡眠加上约 65ms 的处理耗时会累加进下一个周期。
     现改为按整 tick 递推、并把 select 超时设为距下次打洞到期的剩余时间，只有真正卡顿超过
     一个 tick 才重新对齐。
 20. 0.5.2 NAT 类型改为多轮采样（5 轮）取多数判定，不再由单次 A/B 差异一锤定音。原版只测
     一轮就把结果永久锁定（complete 置位后不再复测），一次瞬时重绑定就会把实际是 NAT3
     的对端永久判成 NAT4，进而选到错误的打洞策略。判定分歧较大时标记为 uncertain 并优先
     使用 layered 策略（63 tick 即可完整覆盖，且不依赖角色划分正确）。
 21. 0.5.2 打洞预算耗尽后不再永久闩死。原版 punch_exhausted 置位后只有手动中继开关会复位，
     每个 peer 一生只有一次机会。现改为冷却后重试（最多 8 轮）。0.5.3 起双方使用相同冷却，
     由 supernode 的同代计划重新对齐，不再依赖 MAC 抖动碰运气。
 22. 0.5.3 把 5 轮 NAT 采样从 1 秒级墙钟调度改为 250ms 单调时钟调度。第一轮为保持过滤
     判定可靠，先在 750ms 窗口内重试三次跨端口探测，再访问 B；其余轮次按 A/B 各 250ms
     推进。正常情况下约 3 秒完成，不再晚于 5 秒直连优先窗口。
 23. 0.5.3 在 NAT 分类 complete/unavailable 之前禁止冻结 Tier 1 角色；每轮策略一旦确定便锁定。
     layered 必须完成 63 个 attempt，scanner/anchor 必须完成 88 个 attempt，另保留 25 秒和
     12000 包硬上限。这样不会因五轮分类尚未结束而把两端都错误固定成 scanner，也不会再因
     主循环轻微变慢只扫描到 69/71 个 tick。
 24. 0.5.3 通过现有 QUERY_PEER/PEER_INFO 的可选 aflags 扩展向 supernode 上报 NAT 摘要与
     edge-lifetime nonce。supernode 只有拿到双方摘要后才下发同一 generation、互补角色和
     1500ms 延迟 GO；旧 edge 会忽略扩展，连接旧 supernode 时新版 edge 在 4 秒后回退到
     锁定的本地策略。supernode 会校验上报 MAC、来源 socket、样本范围与 nonce，拒绝伪造摘要。
 25. 0.5.4 为 NAT4↔NAT4 增加独立 UDP worker bank。每端建立 25 个临时 socket，分别向
     Supernode 同机的 21001/21002 观测服务采样公网映射，识别锥形复用、单/双 bank、方向与
     端口跨度；摘要经 QUERY_PEER 上报，Supernode 只在双方报告互相指向、generation 与 nonce
     一致且仍在有效期内时下发同一单调时钟 GO。双方随后以 100ms tick 从所有 worker 并行发送
     合法 n3n REGISTER，命中的 worker 被提升为该 peer 的后续数据 socket，其余 socket 关闭。
     一轮未命中会冷却后重新校准，避免把已经过期的端口模型反复扫描。
 26. 0.5.4 的 worker bank 只在双方最终 NAT 摘要均为 APDM/NAT4 时启用；NAT3↔NAT4、
     分类不确定以及旧版 Supernode 仍走 0.5.3 已验证的 layered/本地回退路径。这样双 NAT4
     获得接近 natpunch 的多 socket 映射采样与并行命中能力，同时不改变本轮表现良好的混合
     NAT 场景。
 27. 0.5.5 修复自动本地地址探测结果没有随 REGISTER_SUPER 上报的问题。双方现在既会通过
     Supernode 交换各自真实的局域网 IP:端口候选，也继续保留组播发现；同一局域网不再因组播
     路由、防火墙或启动顺序而过早进入 NAT4 worker bank。任何增强打洞校准也必须等完 5 秒
     原生/局域网直连优先窗口，避免直连已经在途时抢先分配 25 个临时 socket。
 28. 0.5.5 将打洞轮数从易被 peer 清理重建抹掉的临时字段，提升为 edge 会话级、按对端 MAC
     保存的失败预算。自动打洞最多执行 3 轮；达到上限后明确报告 failed 并稳定使用 pSp，
     不再因条目重建反复回到第 1 轮。bank 协调超时只允许本轮回退 legacy，下一轮会重新校准，
     不再永久卡在 fallback。管理接口取消中继时会显式清除此记录，供双方手动重试。
 29. 0.5.5 主界面增加“pSp 中继（打洞失败）”终态及右键双端重新打洞；同时识别 n3n 的
     invalid transop ID，把联机密钥/加密模式不一致明确提示为配置错误，而不是继续伪装成
     网络打洞失败。
 30. 0.5.5 在线好友表改为稳定的增量集合，不再每次轮询重建 ItemsSource；隐藏水平/垂直
     滚动条但保留滚轮浏览，长昵称以省略号和完整悬停提示显示。虚拟 IP 可右键复制；链路状态
     变化时自动关闭旧右键菜单，避免对过期的 P2P 状态重复下发操作。
 31. 0.5.5 bank 校准拟合新增 volatile 判定（对应 natpunch v7.3.1 的 volatile 模式）：采样
     端口宽散布（跨度 ≥192）且没有双 bank 结构、或同 socket 两次映射跳变（>96）的 worker
     占比 ≥1/4 时，判定为 MIKUN2N_BANK_MODE_VOLATILE——NAT 在一个窗口内随机分配端口。
     此前这类 NAT 会落进 symmetric 分支并把 banks 收缩成最新采样的单点（实测
     banks=4393/4393 spread=15，而真实分配窗口跨度约 500，必然打不中）。volatile 模型
     把 bank1/bank2 设为观测窗口下沿与中点（车道自下而上平铺整个窗口），spread 携带实测
     跨度供对端 predicted 车道使用；对端目标生成侧本就把 VOLATILE 按 hard 处理并追加
     tail 车道，无需协议改动，supernode 校验范围 [CONE,FAST] 也已覆盖该值。
 32. 0.5.6 强制 pSp 中继期间对直连路径做 keepalive。此前 force_relay 的 peer 完全不再有
     流量经过直连 socket：NAT 针孔逐渐失效、last_p2p 持续变旧，取消强制中继后第一次发包
     即命中 timeout/2 空闲检查，known 条目被删除（P2P_EXPIRED）、退回 pending 并要求完整
     重新打洞（实测：切换 4 秒后取消可瞬间恢复直连，24 秒后取消则降级 pSp 重打洞）。现在
     每 5 秒经承载直连会话的 socket（优先 promoted worker socket）向对端发一个 REGISTER，
     往返即刷新双方 last_p2p/last_seen 与 NAT 映射；另外已知 peer 回来的 REGISTER_ACK
     现在也会刷新本端条目（此前只有 pending 提升路径会刷新），因此取消强制中继后直连
     立即恢复，无需重新打洞。
 33. 0.5.6 好友链路右键菜单在普通 pSp 中继状态（打洞冷却/轮间等待）也提供“重新尝试
     P2P 打洞”，不再只有 failed 终态才显示；配合管理接口的 set_peer_relay(false) 双端
     复位失败预算。
 34. 适配多 Supernode 联邦（多节点自动选路中继）。edge 侧：打洞排除表从仅 curr_sn 扩展到
     conf.supernodes 全部联邦节点（强制中继 keepalive 与 Tier 1 扫描都不再误把第二个
     supernode 当 peer）；NAT 探测回包（21001/21002）来源校验放宽到任一已知联邦节点，
     避免 rtt 重锚定竞态丢弃回包。supernode 侧：QUERY_PEER 经联邦转发到达对端锚定的
     supernode 时（from_supernode 且本地无 source_edge），信任查询内携带的 NAT 摘要与
     bank 模型字段（来源 supernode 已按本地注册表校验过），照常计算并下发同 generation
     的互补角色打洞计划；双 NAT4 的 GO deadline 在跨 supernode 场景由各自锚定的
     supernode 独立下发，偏差不超过一个协调间隔，由按 attempt 推进的 scanner 容忍。
     配套：客户端配置生成支持多条 supernode= 并在多节点时启用 supernode_selection=rtt，
     主界面显示当前锚定节点与延迟。
 35. Windows sendto 失败后立即保存 WSAGetLastError，避免后续日志调用污染错误码；遇到
     WSAENOBUFS (10055) 时额外记录 socket、报文长度、进程句柄、内存负载与可用物理内存、
     known/pending peer、活跃 NAT4 worker，以及 P2P/Supernode 累计发送计数，供长期日志
     对比并区分进程资源泄漏、打洞突发流量和系统网络队列瞬时耗尽。
 36. 跨轮速率测量 + 高动态 NAT 快速落中继：bank 拟合现在跨轮测量端口分配速率（环形
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
 37. 双端同步性：校准模型在 REPORTED 等待对端期间会老化（对端晚校准数秒时，
     GO 时刻模型年龄可达 7s+，SYMMETRIC 的 predicted 带覆盖不了漂移量而失败）。
     edge 现在检测到模型年龄超过 5s 就原地重校准（复用 worker、nonce 轮转、
     清对端模型缓存，上限 2 次），把 GO 时刻模型年龄压到 5s 内；supernode 侧
     报告 TTL 从 15s 收紧到 6s，拒绝用陈旧报告生成 GO。单 supernode 的 GO
     时刻对齐已由 750ms 查询周期 + 3000ms GO 延迟保证（双方 go_at 均收敛到
     同一 deadline + 各自 RTT/2，偏差 <50ms），无需改动；联邦场景双 SN 各自
     定 deadline 的 ~400ms 偏差由 attempt 驱动扫描容忍，未改协议。

 38. 0.5.7: experimental IPv6 peer transport (disabled by default). IPv4-only
     rendezvous could not use a reachable IPv6 path between game clients. A separate
     IPv6-only UDP socket now advertises a global candidate and session token through
     the matching patched supernode. MTU-sized challenge/response probes confirm the
     path; periodic probes expire it after 6.5 seconds without confirmation. Address
     changes invalidate prior paths, and send failures or oversized packets use the
     existing IPv4 route. Existing IPv4 NAT4 worker sockets, local direct priority and
     forced-relay policy remain in use. Candidate refreshes preserve established IPv4
     destinations. On IPv6 fallback, the normal last_p2p/timeout check removes an
     expired IPv4 path and selects the supernode; a recently confirmed IPv4 path
     remains eligible until that check expires it. get_edges exposes transport and
     IPv6 RTT. TAP/game addressing stays
     IPv4; this does not add an IPv6 supernode listener or relay. One global candidate
     is selected per client; validated IPv6 is preferred without comparative IPv4 RTT
     selection. Windows compilation passed; live IPv6 and Linux deployment are unverified.
 39. 0.5.7: IPv6 probe response tracking and runtime status. A single challenge could
     be replaced by a retry before its response arrived. Keep up to eight independent
     probes for the 6.5-second path window, preserving the existing sending cadence
     and matching RTT to the actual probe. Log exhausted response windows. A failed
     probe send invalidates the IPv6 path, reports the socket error and waits 10 seconds
     before retrying; local address refresh remains independent. get_nat reports
     ipv6_enabled so the client can distinguish a request from runtime confirmation.
 40. 0.5.7: Opt-in IPv6 test diagnostics. MIKUN2N_IPV6_DIAGNOSTICS=1 records local
     candidate selection and bind errors, peer candidate/query state, probe slots and
     matched RTT, path expiry/fallback reasons, and ten-second traffic/rejection
     counters. Session tokens, challenges and packet payloads are not logged. The
     private test client captures these events with UTC and monotonic timestamps;
     uploading requires a separate, explicit per-connection consent dialog.

 41. 0.5.7: NAT66-to-public IPv6 compatibility. Client logs showed a routed ULA-only
     host never opened its IPv6 socket despite a public candidate on its peer. A
     routed ULA can now bind the socket and advertise its session via the matching
     supernode; global local addresses remain preferred. ULA reports are identities,
     not Internet probe destinations. A session-bound incoming PING triggers a
     rate-limited check of its public source. Only an MTU-sized PONG matching our
     outstanding challenge, destination address/port and 6.5-second window selects
     that endpoint. Advertised candidates and checked paths are separate, so report
     refreshes preserve NAT mappings; session/address changes invalidate them.
     DATA remains restricted to the checked endpoint. Diagnostics expose NAT66
     capability, reported/selected addresses, source checks and path selection.
     Both clients and the supernode need this update for NAT66; the public peer
     must receive the first UDP probe. NAT66-to-NAT66 and IPv6 mapping discovery at
     the supernode are not implemented. Live NAT66 pairing remains to be validated.

 42. 0.5.7: Preserve IPv6 reachability on an oversized data packet. A test session
     confirmed a 63 ms IPv6 path, then disabled it 37 ms later when a 1593-byte
     wrapped packet exceeded the 1418-byte checked size. The persistent size block
     stopped outgoing probes and rejected incoming DATA while still answering PING,
     leaving the remote peer using a path the local peer had disabled. Oversized
     packets now individually fall through to the existing IPv4 route without
     changing the checked endpoint, expiry or probe window. Normal IPv6 DATA and
     keepalives continue; real expiry and send failures still invalidate the path.
     Oversize diagnostics record n3n/wrapped sizes and path preservation at most
     once per peer per ten seconds, with total fallback counts in the summary.
     This does not establish IPv6 delivery for packets above the checked size.
     The wire protocol is unchanged; the NAT66-capable supernode remains compatible.

 43. 0.5.7: Attribute supernode outbound DATA to directed community/MAC pairs.
     Existing packet counters could not identify which relay users caused a
     traffic spike. Successful unicast sends, per-recipient broadcast copies,
     federation unicast and federation flooding now have separate cumulative byte
     and packet counters. An overall positive-send byte counter includes control
     traffic and framing; errors and flow-table overflow remain visible. The
     read-only get_relay_stats method is paginated, uses 64-bit counters and caps
     tracked keys at 8192 per process. No per-packet file writes, payload capture,
     wire changes or client routing changes are introduced. The optional local
     server collector checkpoints every minute and retains hourly totals for
     30 days; see supernode/TRAFFIC.md for accounting and restart boundaries.

 44. 0.5.7: Adapt IPv6 checks to smaller paths and discover NAT66 mappings. A
     hotspot client repeatedly failed to send the fixed 1418-byte UDP probe with
     WSAEMSGSIZE (10040). An oversized send or an unanswered probe flight now
     lowers the budget to 1232 bytes (the IPv6 minimum MTU less IPv6/UDP headers).
     Each challenge records its exact size; only a matching size, source endpoint,
     transaction and lifetime can establish the per-peer checked DATA budget.
     Larger DATA packets individually use IPv4 while smaller IPv6 traffic continues.
     Optional IPv6 STUN observers use the same bound UDP socket as peer traffic.
     Source/transaction-checked RFC 8489 XOR-MAPPED-ADDRESS replies are advertised
     through the existing IPv4 rendezvous channel for 30 seconds and refreshed
     every 10 seconds. This lets both peers initiate checks instead of requiring
     a NAT66 peer's first unsolicited probe to traverse the public peer's firewall.
     A mapping is only a candidate: peer PONG validation still gates DATA, forced
     relay and LAN policy still apply, and IPv4 worker destinations are unchanged.
     The launcher resolves an optional private-profile Ipv6StunHost asynchronously;
     native MIKUN2N_IPV6_STUN_SERVERS accepts up to two semicolon-separated literal
     global IPv6 addresses on UDP 3478, with no bundled public observer default.
     Diagnostics add mapping requests/results/timeouts, interface MTU, probe
     downshift reasons and ipv6_checked_udp_bytes. Tokens and packet payloads are
     not logged. Endpoint-dependent mappings or filtered UDP can still prevent
     direct connectivity. Use matching clients; no supernode wire change is needed.

 45. 0.5.7: Require mutual receive readiness before selecting IPv6 DATA. The
     mixed-client hotspot test had one endpoint accepting 1232-byte PONGs while
     the other rejected DATA; matching clients still rejected 57/21 startup
     packets before their probe budgets converged. Wire version 2 PONGs advertise
     a bounded remaining receive lease after our own source-bound challenge has
     succeeded. DATA sends require both that lease and the local checked path;
     receives retain the validated source/session checks. Old wire versions stay
     on IPv4. A smaller incoming probe immediately lowers our own check budget,
     removing the initial full-size timeout on asymmetric MTU paths. Expiring
     readiness falls back to IPv4 and diagnostics distinguish checked paths from
     DATA readiness. Stable IPv6 pauses extra IPv4 scanning after five seconds,
     releasing exploratory workers while retaining the winning IPv4 socket and
     destinations. Loss of IPv6 resumes bounded scanning with consumed budgets
     retained. Management adds ipv6_wire_version and ipv4_scan_paused. The IPv4
     candidate exchange and existing supernode remain compatible; upgrade all
     participating clients. Build/wire checks do not establish live stability.

 46. 0.5.8-1: Expose the running native build and IPv6 wire generation. The
     mixed-version retest repeatedly reported remote=1/local=2, but friend rows
     could not identify the incompatible endpoint. An optional bounded identity
     suffix on QUERY_PEER reports the compiled build label and wire generation;
     the supernode stores it only for the matching registered sender and returns
     it in PEER_INFO to capable clients. Existing fields and old readers retain
     their layout. Version text is length/character checked, absent metadata stays
     unknown, and observed IPv6 control packets also report the peer generation.
     get_info exposes mikun2n_build_version/ipv6_wire_version; get_edges exposes
     the peer build in version and its generation in peer_ipv6_wire_version.
     This adds metadata without changing IPv6 DATA wire version 2: different
     package builds can communicate when their wire protocol is compatible.
     The client displays its own release number separately from the actual edge
     build, including after a manual Runtime replacement. Native release identity
     is generated from the client BaseVersion/BuildNumber before compilation.

 47. 0.5.8-2: Separate directional IPv6 packet-size discovery from liveness and
     add bounded tunnel fragmentation (IPv6 wire generation 3). In 0.5.8-1,
     incoming 1232-byte peer probes repeatedly erased a confirmed 1418-byte path,
     sending 1383-byte wrapped DATA through IPv4/pSp. Base keepalives now remain
     independent of larger searches; short acknowledgements bind the exact probe
     size to its challenge, session and endpoint. Three larger-probe losses lower
     packetization to 1232 and back off larger searches without invalidating the
     base path. Oversized n3n datagrams use up to four bounded assembly slots per
     peer, fixed two-second expiry, range/overlap checks and duplicate suppression.
     Transient send queue pressure preserves the path; partial sends never replay
     over IPv4. Candidate refreshes preserve same-session checked mappings.
     Generation 1/2 peers use IPv4 compatibility. Native diagnostics and management
     expose directional limits and fragment counters. Offline tests cover sizes,
     reordering, duplicates, expiry, wrong sessions/endpoints and socket errors.

 48. 0.5.8-3: Correct diagnostics and bound duplicate delivery under load.
     Remove obsolete oversize_fallback counters, the duplicate ipv6_tx_udp_bytes
     alias and generation-derived ipv6_fragmentation flag. Keep the checked
     outbound size and peer-advertised receive ceiling for API/raw diagnostic
     consumers; the latter is not a reverse-path measurement. Replace the
     64-entry/time-based completed-ID cache with a 1024-ID sliding replay window:
     aged-out IDs stay rejected during an active fragment stream, so sustained
     traffic cannot reopen duplicate delivery. The two-second idle expiry remains
     compatible with older generation-3 senders. New sender IDs survive peer
     recreation within the edge session. Reassembly storage is allocated only when an edge first
     accepts a valid fragment and freed on session/peer teardown; supernodes do
     not allocate these buffers. IPV6_DONTFRAG failures emit ordinary warnings.
     Native regressions are built by make and cover high-rate ID reuse, bounded
     reordering, fragmentation, session changes and lazy allocation lifecycle.
     IPv6 wire generation remains 3, compatible with 0.5.8-2.

 49. 0.5.8-4: Preserve admitted fragments when later traffic advances the replay
     window. Allocate datagram IDs only for fragmented sends, including DATA
     converted after EMSGSIZE; 1200 unfragmented sends no longer consume IDs.
     An assembly admitted before a global ID jump retains its fixed two-second
     deadline; completed/expired IDs still cannot reopen a slot. Allocation
     failure emits a rate-limited warning. Generation 3 remains unchanged.
     Export corresponding source directly from tracked native worktree files,
     excluding generated configuration/build outputs. Compare every archive
     entry against its source bytes and scan decompressed contents for personal
     paths; the validation report records actual mismatches and per-file hashes.

 50. 0.5.8-5: Remove the waste and fragility found in a 15-hour client log.
     IPv6: a fixed 2-second keepalive against the 6.5-second lease (itself chained
     through the peer's probes) tolerated about two consecutive losses; with ~5%
     probe loss readiness expired 65 times while the path stayed usable, each time
     sending DATA through the supernode and restarting IPv4 calibration. Keepalives
     are now scheduled for the loss case and repeated every 500 ms near the end of
     either lease; a PONG restores the 2-second cadence. IPv4 scanning resumes only
     after IPv6 stays unavailable for 15 seconds, and a working IPv4 path is kept
     warm while IPv6 carries DATA so a brief gap falls back to it, not the relay.
     IPv4: an established path whose receive side goes quiet keeps its entry and
     winning worker socket for 20 seconds, relaying meanwhile and probing the old
     endpoint every second; closing that socket had made even a short interruption
     need a full re-punch. Pending entries expire on last_seen regardless of the
     upstream 16-entry threshold, and identities that neither send nor are answered
     for by the supernode for 30 seconds stop being punched and are queried less
     often (one evening accumulated 15 departed identities of one host, each with a
     full budget). Group MACs outside IPv4/IPv6 multicast (LLDP, STP) are dropped
     at the TAP instead of becoming a permanent pending peer. A late bank plan is
     accepted only by a live calibration, and a spray without workers is never
     charged. A missed APDM coordination recalibrates twice (10 s apart, no round
     charged) before this round falls back to the single-socket scanner, which
     produced no connection in 39 rounds of about 11,600 packets. Only one heavy
     scan runs at a time. Punch outcome lines name the peer MAC.
     Supernode: optional mikun2n_relay_kbit and mikun2n_broadcast_pps police relayed
     DATA only. Control messages are never limited; under contention each sending
     edge gets an equal share and may borrow while the link is idle. Both default to
     0 (unlimited); get_relay_stats reports the limits and drop/borrow counters.
     No wire change: IPv6 generation stays 3 and mixed versions interoperate.
     Offline regressions cover keepalive scheduling, departed-identity detection and
     the relay policy; live pairing and server deployment are not yet verified.

构建要求：Windows 目标需要在 CFLAGS 中带上 `-std=gnu17`（随包的旧 src/win32/getopt.c 在本
工具链的 C23 默认标准下编译不过），并带上 `-ffile-prefix-map=<构建路径>=<占位路径>`，把调试
信息里的绝对路径映射掉。曾经发布的二进制内嵌了构建机的用户名与目录结构（`Users/<name>/.../
n3n-build/...` 共 13 条），加映射后重编已清零。重编后请重新核对二进制里不再出现本机路径。

- n3n-3.4.4-source.zip：与当前 n3n-edge.exe 对应的修改版源码（含上述改动），供 GPLv3 合规使用。
  压缩包根目录的 MIKUN2N-MODIFICATIONS.md 是 GPLv3 第 5(a) 条要求的修改声明（修改者、日期与改动范围）。
  官方原版源码见 https://github.com/n42n/n3n （tag 3.4.4）
- LICENSE-n2n.txt：n3n 的 GPLv3 许可证全文。

旧版 n2n edge.exe 无法提供对应源码，已从仓库与所有发布包中移除；本机若自行放置
Runtime/edge.exe，程序仍会在缺少 n3n-edge.exe 时回退使用它，但它不得再随包分发。

可选文件：
- tap-windows-installer.exe：OpenVPN 官方 TAP-Windows 9.24.7 驱动安装器（未修改的官方签名安装器）
- LICENSE-tap-windows.txt：从该安装器内提取的原始许可证（GPLv2 + WDK 系统库例外）

.NET 运行时：
- LICENSE-dotnet.txt、THIRD-PARTY-NOTICES-dotnet.txt：自包含发布嵌入的 .NET 9 / WPF / WinForms 运行时
  （MIT）许可证与其第三方声明，取自 Microsoft.NETCore.App.Runtime.win-x64 运行时包。

注意：分发 n2n/n3n 二进制时必须同时遵守 GPLv3 许可证并提供对应源代码；分发 TAP 安装器时须附带
其许可证并按 THIRD-PARTY-NOTICES.txt 的书面承诺提供源码。汇总说明见 THIRD-PARTY-NOTICES.txt。
Windows source build: run sh scripts/build-mikun2n-windows.sh from the extracted source root in Git Bash with MinGW-w64 on PATH.
Source packaging: tools/package-native-source.py --source <patched-root> --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>. Generated config.mak/configure/headers are excluded and recreated by the build scripts.
Linux server build: use supernode/build-supernode.sh from the MikuN2N source release; it runs autogen.sh before configure.
