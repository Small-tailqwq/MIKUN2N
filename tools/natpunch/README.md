# natpunch v7.3.1

面向 Windows 的 UDP NAT4（地址和端口相关映射）打洞实验工具。源码固定保存在
MikuN2N 仓库中，避免再次只留下临时目录或二进制。

## v7.3.1 最小修复

v7.3.1 修复跨 ISP 测试暴露出的单探测 IP 盲点：

- A/B 探针仍位于同一服务器 IP，因而 `cone` 仅代表同一目的 IP 下端口映射
  稳定，不能证明访问另一 Peer IP 时端口不变。
- 对端报告 `cone` 时不再只尝试 control 一个端口；每轮持续覆盖
  `control ±1..64`，并轮转覆盖一个双向 192 端口带。
- 第 1 轮覆盖到 `±256`，后续轮次依次推进；21 轮最多探索到约 `±4096`。
- `sym`、`volatile` 和 `fast` 的既有预测与调度逻辑保持不变，服务端协议不变。
- GO 日志新增 `跨IP不确定性回退`、`near` 和 `rotating` 范围，JSONL 新增
  `cone_escape`、`band_lo`、`band_hi`，便于确认该路径是否命中。

## v7.3 策略

v7.3 是针对 fast-cycle 手机 CGNAT 的最后一轮集中实验：

- `fast-target` 由规律端执行：持续密扫 GO 时刻的 385 端口静态预测带，
  约每 0.6 秒完整覆盖一次，同时保留随时间前进的 moving lane。
- `fast-sender` 只在本端自身为 fast 时启用，继续维持 control、固定 anchor
  和阶段 moving 三类低扇出目标。
- anchor 偏移、双 bank 选择、static/moving 排列都加入 attempt 盐值；失败重试
  会探索新的相对端口，不再重复 v7.2 的固定盲区。

以下保留 v7.2 已实现的模型能力：

v7.2 保留 v7.1 已验证的规律 NAT 分层扫描，并为手机流量常见的高速递增、
快速循环和间歇丢包增加独立的 `fast` 路径：

- 单 bank 连续速率达到 120 端口/秒后进入 `fast-cycle`。
- 探针不足时沿用最后一次可信 bank、环形方向和速率，不再退回失真的 control
  模型；恢复采样后使用跨完整时间间隔的环形差值。
- `fast` 打洞采用 500ms 相位移动预测带，窗口随 `rate × elapsed` 前进，
  最高覆盖 8192 偏移。
- 每个工作 socket 固定复用 control、一个低偏移 anchor 和一个阶段 moving
  目标，降低目标相关 NAT 的映射扇出和运营商 UDP 限速风险。

规律 NAT 仍使用原 v7.1 策略：

- 保留服务端 `PROBE/JOIN/BANKS` 旧协议，旧 v6 客户端仍可继续使用。
- v7 每轮创建并保留一组 UDP socket；测量、打洞、确认均使用同一组 socket。
- 同一 socket 分别探测服务端 A/B 端口，区分稳定映射和目标端口相关映射。
- 对采样端口拟合单 bank/双 bank，并识别 bank 大幅漂移、单双 bank 切换和
  bank 间距突变形成的 `volatile` 模型。
- 速率只用于候选优先级，不再代替固定覆盖；每个工作 socket 从 GO 起并行覆盖
  control、低偏移 1～64、中偏移 65～256，以及预测带/易变尾带 257～384。
- 借鉴 EasyTier：双方先完成校准和上报，再由服务端同步 GO；所有 socket 先集中
  攻击高概率端口，随后分片扫描候选窗口。
- 任一 socket 收到有效 `P7` 后，立即从原 socket 向报文真实源端点回复 `A7`，
  并锁定该 socket，不重新创建映射。
- P7/A7 携带发送 worker、目标端口、lane、offset 和 tick，首命中可直接还原
  哪一类候选生效；重复对端模型和重复 A7 不再污染日志。
- 打洞窗口为 7 秒，成功后只保留约 3 秒确认流量。
- 每次运行同时写人类可读 `.log` 和结构化 `.jsonl`。

## 文件

- `natpunch.c`：Windows v7 客户端。
- `natpunch-server.py`：兼容 v6 的 v7 协调服务器。
- `analyze_logs.py`：汇总或比对一至两份 v7 JSONL。
- `legacy/v7.1/`：升级前逐字节保留的 v7.1 客户端和服务端源码。
- `legacy/v7.2/`：升级前逐字节保留的 v7.2 客户端和服务端源码。
- `legacy/v7.3/`：升级前逐字节保留的 v7.3 客户端、服务端及构建说明。
- `legacy/v7.0/`：v7.0 客户端和服务端源码。
- `legacy/natpunch-v6.c`：从 Claude 临时目录找回的 v6 客户端基线。

找回的 v6 原文件 SHA-256：
`BF0DFB50B1CD2E61B16D87BDD9364184601EB2642A016489A23E2EC1076CEB8A`。

## 构建

```powershell
$env:Path = "%UserProfile%\mingw64\mingw64\bin;$env:Path"
gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.3.1.exe natpunch.c -lws2_32
```

模型自测：

```powershell
.\natpunch-v7.3.1.exe --self-test
```

## 运行

服务端：

```bash
python3 natpunch-server.py server --bind 0.0.0.0 --port-a 21001 --port-b 21002
```

两端使用相同房间号：

```powershell
.\natpunch-v7.3.1.exe client vps.example.com 房间号
```

可选参数：

- `--sockets N`：工作 socket 数，默认 25，范围 4～48。
- `--duration SEC`：总实验时间，默认 180 秒。
- `--log-dir PATH`：日志目录。默认
  `%LocalAppData%\MikuN2N\logs\natpunch`。
- `--no-pause`：结束时不等待回车，适合自动化测试。

## 日志

每次运行产生：

- `natpunch-v7.3.1-时间-PID.log`：控制台的完整副本。
- `natpunch-v7.3-时间-PID.jsonl`：每行一个 JSON 事件。

重点事件包括：

- `session_start`：版本、客户端 ID、参数和本地端口。
- `calibration`：A/B 回包数、映射复用比例、RTT、bank、步长、离散度、
  跨轮速率和置信度。
- `peer_model`：对端上报模型。
- `go`：attempt ID、GO 延迟和实际启动偏差。
- `spray_progress` / `spray_summary`：逐秒和整轮的发包量、错误数和单向命中。
- `peer_packet`：首命中的本地 socket、真实源、lane、offset、目标端口和 tick。
- `success` / `attempt_failed`：成功时延或失败轮次摘要。

`.jsonl` 不记录房间号原文，只记录其 FNV-1a 标签。

分析单端或双端日志：

```powershell
python .\analyze_logs.py .\A.jsonl
python .\analyze_logs.py .\A.jsonl .\B.jsonl
```

双端分析会直接报告 GO attempt 是否对齐，并突出双 bank、快速递增、探针丢失、
多公网 IP、单向命中和最终获胜 socket。

## 边界

v7 提高的是可预测、部分可预测和双 bank NAT4 的成功概率。双方都采用真正随机
或目标哈希映射时，没有稳定的纯 UDP 必成功方法，生产系统仍应保留中继。
