此目录在构建和发布时会原样复制到程序目录。

必须文件：
- n3n-edge.exe：n3n 3.4.4 Windows 客户端（新版优先使用）。当前为本地修改版，非官方原版二进制：
  1. Windows 启用 register_pkt_ttl 对称 NAT 打洞路径，并忽略打洞 TTL 探测产生的 WSAENETRESET。
  2. 从 n3n 数据 socket 向 Supernode 同机的 UDP 21001/21002 观测端点执行映射与跨端口过滤探测，通过 get_nat 管理方法报告 NAT1/2、NAT3、NAT4。
  3. 普通 n3n 直连保留 5 秒优先窗口；仍为 pSp 时执行 22 秒、250ms 时间片、12000 包硬上限的分层喷射。NAT4 本端采用 v7.3.1 已实测的跨地址 Cone 逃逸：持续发送 control，四个时间片完整覆盖 control±1..64，并逐轮双向覆盖 192 端口带，约 21 轮推进到 control±4096；预算耗尽后保留中继。
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

- n3n-3.4.4-source.zip：与当前 n3n-edge.exe 对应的修改版源码（含上述改动），供 GPLv3 合规使用。官方原版源码见 https://github.com/n42n/n3n （tag 3.4.4）

源码目录中的 edge.exe 仅保留作历史排查参考，不会复制到正式构建或发布包。

可选文件：
- tap-windows-installer.exe：OpenVPN 官方 TAP-Windows 9.24.7 驱动安装器

注意：分发 n2n/n3n 二进制时必须同时遵守 GPLv3 许可证并提供对应源代码。
