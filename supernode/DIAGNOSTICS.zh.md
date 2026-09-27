# 可选测试诊断

> [English](DIAGNOSTICS.md) | [简体中文](DIAGNOSTICS.zh.md)

私有测试构建在每次手动连接时默认采用 **每次询问**。拒绝、按 Escape 或关闭对话框都会让日志留在本地。一次性授权覆盖该次连接和自动 edge 重启；它永远不会变成已保存的授权。设置里还提供 **始终允许**（需要单独的知情确认）和 **始终拒绝**（不弹窗、不上传）。持久授权绑定到所选节点的身份/配置、确切的 HTTPS 接收端和固定证书。编辑或切换活动节点会清除它，即使再切回来也一样；接收端或证书变化需要重新授权。它绝不会由私有配置预置。应用“询问”或“拒绝”会撤销进行中的持久上传。IPv4 和 IPv6 会话（包括失败的连接尝试）都遵循这一策略。

主窗口可以停止上传，或在无需重启客户端、edge 或连接的情况下恢复诊断记录/上传。恢复遵循所选授权模式并开始一个新的分段；被拒绝/撤销的记录绝不会重放。每次连接在本地最多保留四个 64 MiB 分段（共 256 MiB），写满时轮转最旧的分段。存储错误会暂停记录并提供可见的恢复操作。接收端中断会在这些范围内重试；丢失的未确认分段会报告为未完成投递。断开/退出时最多尝试八秒的最后刷新。后续连接绝不会上传更早的文件。

被 409（序号/内容冲突）或 410（已过期）拒绝的分段会立即放弃上传，并做三秒退避，以避免在每个新分段都被拒绝时快速轮转。每次 507 容量响应会等待 30 秒，并保留同一个不可变的待处理分块以便重试。它自身绝不会放弃或轮转分段；等待期间普通的本地配额轮转仍可能淘汰旧的积压。服务器容量恢复后会自动继续投递。手动恢复会取消等待，并在新的/适用的授权下开始一个新分段，保持“恢复前的记录绝不重放”的规则。其他网络故障会保留该不可变分块，在正常的滚动限制内做幂等重试。更新的分段可以继续；如果当前分段被拒绝，客户端会开始一个全新分段。本地文件仍受其既有配额和保留策略约束。`upload_segment_abandoned` 记录该分段、HTTP 状态和未确认字节数，界面会把投递标记为未完成。这些转换会保留授权，并且绝不会在撤销后重新启用上传。被计为放弃的字节会从后续配额淘汰总量中排除；这两个总量互不重叠。`upload_capacity_wait` 标识被保留的分段/分块和重试间隔。

接收端的过期 ID 集合保存在内存中。重启后，对已被淘汰分段的非零分块重试可能返回 409 而不是 410；这两种状态现在走相同的客户端恢复路径。此修复不需要任何服务器更新或额外的服务器状态。

记录包含 UTC、单调递增偏移、批次/会话/事件标识符、构建与 edge 哈希、网络接口地址、NAT 结果、对端传输/延迟、连接变化以及原始 edge 诊断事件。IPv6 诊断描述地址选择、候选刷新、探测发送与匹配的 PONG RTT、过期、发送失败、拒绝计数和流量总量。它们不采集数据包负载、密钥、密码、无关文件或历史日志。已知的会话凭据和本地用户配置路径会从诊断字符串字段中脱敏。这些记录仍包含个人网络元数据和昵称；授权对话框会说明这一范围。

当前客户端使用 IPv6 wire 第 3 代，带方向性尺寸发现和有界分片；参见 [IPv6 传输](../docs/IPV6-TRANSPORT.zh.md)。`pong_matched` 报告已检查的字节数、对端的接收租约和 `data_ready`；`data_path_ready` 标记 DATA 选择。`data_fragmented` 以及周期性的分片/重组计数用于区分大包处理与真正的回退。`version_mismatch` 让第 1/2 代客户端留在 IPv4。`peer_readiness_expired`、路径过期和硬发送失败记录真实的回退。扫描暂停/恢复事件解释了 IPv6 稳定时 IPv4 打洞计数停止的原因。无需更新 supernode wire；双方客户端都需要第 3 代。

私有测试会话记录 `MikuN2N latency` 事件，用于单播探测发送、ACK、十秒超时、ICMP 结果、过期样本和路由变化。节点/探测/代次字段在不含数据包内容的情况下关联结果。现有的两秒探测节奏限制了常规日志频率；会话日志限制仍然适用。路由变化会使上一代的未完成测量失效。“暂无法测量”不会停止重试，也不意味着好友离线。原生 PONG RTT 与应用隧道 RTT 保持分开。

## 接收端

在 supernode 上，复制此目录并以 root 运行：

```sh
bash install-diagnostics.sh --host YOUR_SERVER_HOST --batch ipv6-test-YOUR_BATCH
```

在主机和云服务商的防火墙中都放行 TCP 5443。接收端使用专用账户、本地生成的 TLS 证书、按批次区分的上传凭据、128 KiB 请求、原子/幂等的编号 JSONL 分块、64 MiB 分段上限和滚动 2 GiB 总量上限（先移除最旧的保留分段）。日志只能由管理员通过 SSH 访问；HTTP 服务没有下载 API。分段在首次接收 24 小时后过期，启动时和每五分钟清理一次。此服务器策略同样适用于对话框仍显示七天的旧客户端。重新安装会更新配置文件的保留字段，而不会轮换凭据或证书。包内的固定证书用于认证服务器，而无需全局禁用 TLS 验证。上传时禁用重定向和系统代理。证书在 90 天后过期。

把 `/etc/mikun2n-diagnostics/client.json` 直接放入私有构建输入；不要打印它、放入版本控制或包含进公开源码归档。添加一个 `Node` 对象，只包含 `Id`、`Name`、`Server` 和 `Community`。绝不要把已保存的客户端设置、用户的 NodeId、记住的密钥、昵称或服务器私钥复制进该输入。该输入没有授权设置。

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:TestProfilePath="ABSOLUTE_PRIVATE_PROFILE_PATH" -o artifacts/private-ipv6-test
```

私有配置只有在显式提供 `TestProfilePath` 时才会被嵌入。测试设置位于 `%LocalAppData%/MikuN2N/tests/<batch>/settings.json`；首次启动时启用 IPv6 并预置所选私有节点。既有常规构建的设置保持独立。诊断文件位于 `%LocalAppData%/MikuN2N/logs/diagnostics-*.log`，并遵循客户端的本地保留设置。不带该属性的常规发布没有任何内置节点或上传器配置。

可选的私有配置项 `Ipv6StunHost` 独立于日志上传来配置地址发现。启动器用三秒 DNS 超时解析最多两个全局 IPv6 地址；edge 从其 peer socket 向 UDP 3478 发送标准 STUN 绑定请求。观察者看到的是源地址/端口和一个随机事务标识符，而不是昵称、小组名称、会话令牌、应用负载或诊断日志。原始 supernode 通过 IPv4 交换得到的候选地址，并且仍是唯一的诊断上传目标。诊断包括映射查找/请求/回复/超时事件、本地 IPv6 接口 MTU、探测预算变化和已检查的每对端 UDP 尺寸。日志上传授权流程不变。

## 通过 SSH 读取双方客户端

```sh
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --sessions
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --contains v6diag
sudo python3 /opt/mikun2n/diagnostics/read-diagnostics.py /var/lib/mikun2n-diagnostics/YOUR_BATCH --kind management_edges
sudo journalctl -u mikun2n-diagnostics.service --since today
```

每一行都有一个稳定的 `connection`、递增的 `segment` 和连接范围内的 `sequence`。HTTP 的 `session` 和目录标识一个分段。用 `--connection CONNECTION_ID` 合并所有保留的分段，包括恢复后的日志。分段头部重复最小的会话/连接/原生身份信息以便关联。昵称、虚拟 IP 和对端 MAC 把两侧对应起来。客户端时钟可能不同：用 `received.json` 判断服务器的首次接收，用 `elapsedMs`/`sequence` 判断每个客户端内部的顺序。读取器按客户端 UTC 合并流；它无法纠正未同步的时钟。

双方客户端必须各自独立授权。目录缺失可能意味着拒绝、上传停止或端点不可达；它不能证明另一个客户端没有运行。当界面报告投递未完成时，请保留本地文件。要停止收集，禁用 `mikun2n-diagnostics.service`；移除已存日志或更换批次/凭据是单独的管理员操作。
