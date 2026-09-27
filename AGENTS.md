# AGENTS.md

MikuN2N 是一个 Windows WPF (.NET 9, C#) 桌面客户端，包在开源 n3n edge 客户端之外，让非技术玩家一键建立类局域网联机：它管理 `n3n-edge.exe` 子进程、调用其本机管理接口、通过 UDP 广播发现其他 MikuN2N 客户端，并在带托盘常驻的 WPF 界面里显示连接与好友链路状态。

项目真正要解决的问题是 n3n 家族客户端在 Windows 上缺少可用的对称 NAT 端口预测路径：对端处于对称/端口限制型 NAT（NAT4，国内 CGNAT 下很常见）时只能退回 supernode 中继（pSp），而中继的额外延迟与负载会毁掉实时联机体验。MikuN2N 的核心工程量集中在本地修改版 `n3n-edge.exe` 上——加入符合 RFC 8489 的 NAT 行为发现与多轮端口预测、探测算法，让双方建立直连 UDP 路径（RFC 5128 打洞，思路对齐 RFC 8445 ICE）。当前 NAT3↔NAT4 可稳定直连，NAT4↔NAT4 仅在端口分配可预测的子集上成功。这些算法只用于两个自愿的 MikuN2N 用户之间建立直连，不扫描、不接触任何第三方系统，也不是绕过访问控制的工具。

配套 `supernode/` 存放自建服务端的部署脚本、systemd 单元与配置模板，以及中文自建文档；服务端改动一律手工部署到用户自己的机器，仓库不做自动化。**客户端不含任何内置服务器地址**：supernode 由用户自建或朋友分享，在界面的节点管理里维护，因此任何提交都不得写回真实服务地址、内网网段或小组名。

## 构建与运行

需要 Windows 与 .NET 9 SDK。

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

构建输出在 `bin/Debug/net9.0-windows/`。没有 lint 步骤。

改动后的最小冷检查（不启动程序、不联网、不需要授权）：

| 改动范围 | 检查 |
|---|---|
| 任意 C# 代码 | `dotnet build` |
| 诊断会话、上传授权、日志轮转 | `dotnet run --project tools/offline-tests -- <一个空临时目录>`（会写出 64 MiB 分段，别指向仓库） |
| 原生 IPv6 尺寸发现、分片、重组 | patched 源码树 `make` 后运行 `tools/tests-ipv6.exe`；wire 编解码用 `tests-wire` |
| 连接、进程生命周期、TAP 网卡、UDP 发现 | 没有单元测试能覆盖，必须实机连接；这属于需要用户明确授权的验证，不要自行启动程序 |

XAML 能编译只说明语法正确：界面改动按下面「界面主题」一节在两套主题下实际运行确认。

程序是单实例的（命名互斥体 `Local\MikuN2N.SingleInstance`），第二次启动只弹一个消息框后退出。

## 代码地图

入口在根目录：`App.xaml.cs`（启动/主题/单实例/托盘/日志清理接线，并负责把旧格式设置就地升级）、`MainWindow.xaml.cs`（主界面与接线）、`SettingsWindow.xaml.cs`（常规/关于/节点三个页签）、`NodeEditDialog.xaml.cs`（单个节点的表单）、`CloseBehaviorDialog.xaml.cs`、`ConflictingProcessDialog.xaml.cs`、`LogUploadConsentDialog.xaml.cs`（诊断上传授权弹窗，只在测试包里出现）。`Models/` 是数据模型，其中 `SupernodeNode` 是一个节点的「名称 + 服务器地址 + 小组名称」。

`Services/` 一览（`Runtime/` 下是与发布包一起分发的第三方二进制与说明；`tools/` 是本地验证与实验脚本；`tools/natpunch/` 是打洞算法的独立参考实现，`tools/offline-tests/` 是客户端侧的离线回归工程，跑法见上面「构建与运行」）：

| 文件 | 职责 |
|---|---|
| `EdgeController.cs` | 全应用核心，约 2500 行，下面单列 |
| `N2nManagementClient.cs` | 管理协议：n3n 走 `N3nHttp`（JSON-RPC 2.0 over HTTP 到 `http://[::1]:{port}/v1`；n3n 在 Windows 的监听只绑 IPv6 回环，不要"修"成 `127.0.0.1`），含 `get_nat`、`get_edges`、`get_supernodes`、`set_peer_relay`；旧 `edge.exe` 退化走 `N2nUdp` 文本协议 |
| `PeerDiscoveryService.cs` | 独立于 n3n 的发现通道：在 TAP 子网内 UDP 43121 广播 hello/ack 交换昵称，并用 ICMP ping 测延迟 |
| `UpnpPortMappingService.cs` | SSDP 发现 + SOAP 在路由器上开通/续订 UDP 50001 映射，并识别 CGNAT（映射后外网 IP 仍不可全局路由） |
| `NetworkTuningService.cs` | 调整 TAP 网卡接口 metric（影响局域网房间广播走哪块网卡）并配置 Windows 防火墙规则 |
| `SettingsStore.cs` | 把 `AppSettings` 存为 `%LocalAppData%\MikuN2N\settings.json`；仅在 `RememberKey` 时用 Windows DPAPI（`ProtectedData`，当前用户作用域）单独保存加密密钥，保存后清零托管内存中的副本 |
| `LogCleanupService.cs` | 按 `AppSettings.LogRetentionDays`（7/30/90/180/永久）在启动时与每 6 小时清理 `%LocalAppData%\MikuN2N\logs` |
| `CrashLogService.cs` | 记录进程级未处理异常 |
| `ThemeManager.cs` | 运行时套用浅色/深色调色板（资源字典）并设置 DWM 沉浸式深色属性；跟随"系统"主题时轮询注册表（3s），不订阅系统事件 |
| `TrayIconService.cs` | WinForms `NotifyIcon` 与右键菜单（WPF + WinForms 混用，csproj 里的 `UseWindowsForms` 就是为它开的）；"最小化到托盘还是退出"由 `MainWindow` 按 `ClosePreference` 决定 |
| `AppIconService.cs` | 运行时用 `DrawingVisual` → `RenderTargetBitmap` 生成图标，并设置进程 AppUserModelID 以正确分组任务栏 |
| `BuildIdentity.cs` | 从程序集元数据（informational version，回退 assembly version）解析显示版本，供主界面/设置/托盘提示读取 |
| `EasterEggManager.cs` + `EasterEggVisualController.cs` | 自包含的彩蛋（设置里的老虎机 → 飞虫/蜘蛛追逐动画 → 彩虹 jackpot），与连通性逻辑无关，推理核心行为时可直接跳过 |
| `TestBuildProfile.cs` | 私有测试包内嵌的配置（批次、节点、上传地址、证书指纹、STUN 观察者）；读不到该资源时返回 null，普通构建因此不创建任何诊断会话 |
| `TestDiagnosticsSession.cs` | 诊断会话：按 64 MiB 滚动分段记录与脱敏，按 128 KiB 分块上传；不重连即可继续记录或恢复上传，状态经 `IsRecording`/`UploadAllowed` 暴露给界面 |
| `UpdateService.cs` | 在线更新：查 `UpdateRepository`（csproj 属性，写进程序集元数据）的 GitHub latest release，后台下载 `MikuN2N-<版本>-win-x64.zip`，按资产 digest 或 `SHA256SUMS` 校验、解包并核对 exe 版本后才询问；安装在 edge 停止后原地换文件（运行中的文件先改名 `.update-old`，失败整体回滚），以 `--after-update <pid>` 重启；新进程在单实例检查前等旧进程退出，拿到互斥体后才清理残留（只查根目录与 `Runtime/`，不递归） |
| `DiagnosticUploadConsent.cs` | 把「始终允许」绑定成「当前节点 + 接收地址 + 证书指纹」的 SHA256；节点编辑、删除、切换都会使其失效 |

### EdgeController 不变量

- 启动 `Runtime/n3n-edge.exe`；该文件不存在时回退到用户自备的旧 `Runtime/edge.exe`（无可复现源码，已被 gitignore，绝不能提交或打包），并据实际选中的二进制决定管理协议与配置格式。子进程配置每次会话生成，密钥只经 `N2N_KEY`/`N3N_KEY` 环境变量传入，绝不写进命令行或磁盘配置。
- 轮询管理接口得到 supernode 注册状态、在线 peer、每个 peer 的直连/中继（`pSp`）模式与 NAT 类型（`GetNatAsync` → `ConnectionSnapshot.NatType`/`NatDescription`）。
- 节点由用户在界面维护（`AppSettings.Nodes` + `ActiveNodeId`），每个节点是一组「名称 + 服务器地址 + 小组名称」；`ActiveNode` 只在没有选中项时才退回第一个节点，指向已删除节点的 id 返回 null，让调用方去提示而不是偷偷换节点。服务器地址按逗号/分号/顿号/空格拆成多端点（`SplitServers`），每个端点生成一条 `supernode=`，多端点时启用 `supernode_selection=rtt`；旧 `edge.exe` 只取第一个端点。
- 用虚拟 IP、昵称与 ARP 解析出的 MAC（`iphlpapi.dll` 的 `SendARP`）把管理接口的 peer 行与 `PeerDiscoveryService` 的结果对上，再判定 `PeerConnectionMode`：`Direct`、`LanDirect`、`Punching`、`Relayed`、`ForcedRelayed`。
- 负责异常退出恢复：`RestartDelays` 指数退避重启、从日志文本（"already in use"）识别地址冲突并经 `ConflictingProcessDialog` 提示，以及 `_wantConnected`/`_sessionId` 状态机。改动连接生命周期前先读懂这个状态机：文件里大部分复杂度都是为了在 `_stateGate`/`_lifecycleGate` 下让并发的启动/停止/重启/监视不出竞态。
- 每个会话的完整日志写在 `%LocalAppData%\MikuN2N\logs`，保留策略见 `LogCleanupService.cs`。

## 界面主题（硬性要求）

- 有完整的浅色与深色主题，可实时切换（也可跟随系统），由 `ThemeManager` 驱动。任何界面改动——新窗口、新控件、新弹窗——都必须在两种主题下验证并正确配色，不能只看编辑器当时所在的那一种。
- 不要依赖 Windows/WPF 的默认控件模板：`ComboBox`、`ComboBoxItem`、`ContextMenu`、`MenuItem`、滚动条、复选框等的系统模板是 XP 时代的扁平灰，与其余界面完全不一致。凡是默认模板不认 `DynamicResource` 画刷的控件，都要用应用的 `DynamicResource` 调色板加显式现代 `ControlTemplate`/`Style`（照抄现有窗口的写法）。
- 认为界面改动完成之前，实际运行程序看浅色与深色两套下的关闭/打开、悬停、选中、勾选、聚焦、禁用状态；XAML 能编译不算验证通过。
- 每个窗口都在 `SourceInitialized` 里调用 `ThemeManager.ApplyWindow(this)`，新窗口必须照做。

## 约定

- 所有面向用户的文案用简体中文，语气与现有一致（简洁、非技术化、重连过程中让人安心）。代码、注释、提交信息与文档用英文。
- 注释很少，只解释不显然的"为什么"（竞态、协议怪癖、平台怪癖），不复述代码在做什么。
- WPF 与 WinForms 类型会重名（`Brush`、`Color`、`Point` 等），现有文件用显式 `using X = ...` 别名解决（如 `MediaColor`、`WpfBrush`、`Forms.NotifyIcon`），照这个写法做，不要在行内写全限定名。
- 应用版本由 `MikuN2N.csproj` 的 `<BaseVersion>` 和 `<BuildNumber>` 组成，格式为 `0.5.8-1`、`0.5.8-2`，不再追加时间戳。每次交付新的测试包或发布包前将构建序号加一；同一交付的编译重试不加号。`BaseVersion` 变化时序号重置为 `1`。显示处一律经 `Services/BuildIdentity.cs` 读取。重建原生 edge 前运行 `tools/sync-native-version.ps1 -PatchedSource <源码目录>`，使原生构建标识与本次版本一致；单独替换 edge 时仍显示实际运行二进制报告的标识，不用客户端版本冒充。IPv6 兼容性按握手协议代次判断，不按构建号是否相同判断。
- 本仓库按开源发布对待：节点地址、小组名称、联机密钥与个人机器路径只允许出现在 `*.local.md`（已被 gitignore）里，不要写进代码、默认值、测试脚本或受版本控制的文档。
- `Runtime/` 分发第三方二进制（n3n edge、TAP-Windows 安装器）及其许可证（`LICENSE-n2n.txt`、`LICENSE-tap-windows.txt`、`LICENSE-dotnet.txt`）与源码归档 `n3n-3.4.4-source.zip`；本体 `LICENSE` 以 `LICENSE.txt` 随包。改动打包内容时保持许可证与源码可得性义务完整，汇总见 `Runtime/THIRD-PARTY-NOTICES.txt`，规则见 `Runtime/README.txt`。换 edge 或源码包后同步更新声明里的 SHA-256；patched 树根目录的 `MIKUN2N-MODIFICATIONS.md` 是 GPLv3 §5(a) 的修改声明，每次原生改动更新其中的版本与日期。
- 公开发布包一律用 `tools/package-release.ps1` 生成（它检查许可证、源码与本机账户路径），产物与 `SHA256SUMS` 挂到同名 tag 的 GitHub Release 上，客户端的在线更新只认这两个文件；推 tag 会由 `.github/workflows/release.yml` 建草稿 Release，核对说明后再发布。私有测试包（`TestProfilePath`）不走这个流程。
- 每交付一版就打一个提交，标题为 `0.5.8-2: <本版做了什么>`，正文写改动与理由，并用同名 tag 标记该提交（`0.5.8-1`、`0.5.8-2`）。审阅某一版改了什么，直接 `git diff <上一版>..<这一版>`，不要靠解压发布包逐文件比对。原生侧改动在 `n3n-build` 仓库用同样的粒度提交与打 tag。
- 每轮的发布包、离线测试日志、验证 JSON 与改动前快照留在 `artifacts/<版本>/`（不进版本控制）。审阅、发布核对或追查历史版本时先看那里的 `*-validation.json` 与 `*-tests*.log`；要对照上一版源码就看 `before/`。

## 边缘补丁与打洞算法

`Runtime/n3n-edge.exe` 是本地修改版而非上游原版，这份补丁是本项目的核心工程贡献，也是合规的连接/互操作工程（面向自有 P2P 会话的 RFC 对齐 NAT 行为发现与打洞调度），不是安全绕过或逆向工具。

**补丁清单的唯一权威来源是 `Runtime/README.txt`**（随发布包分发、按版本编号）。不要在本文件或其他地方再抄一份——历史上那份副本长期落后于 README，并与它关于冷却策略的表述互相矛盾。要看当前实际生效的行为读那份清单，不要依赖任何摘要。

改动补丁、重建 `n3n-edge.exe` 或调试打洞失败时，加载 skill `mikun2n-edge-patch`，其中记录了源码树与构建工具链位置、n3n 与 natpunch 两侧的重建步骤，以及打洞算法的参考实现（`tools/natpunch/`）。

## 文档索引

- 对外文档为双语：英文是规范名（`README.md`），中文版本加 `.zh.md` 后缀（`README.zh.md`），两版顶部互留语言跳转链接。下列路径均为英文版，中文版把 `.md` 换成 `.zh.md`。
- `supernode/README.md`：自建 supernode 的完整步骤（装 n3n、跑安装脚本、填进客户端、联邦、排查）。
- `docs/FEDERATION.md`：多 supernode 联邦的设计、测试部署与已知边界（当前暂缓推进）。
- `docs/TROUBLESHOOTING.md`、`docs/P2P-PUNCH-INTEGRATION.md`、`docs/NATPUNCH-V7-SUMMARY.md`：打洞与中继问题的历史排查结论与方案。
- `docs/IPV6-TRANSPORT.md`：IPv6 实验传输的分层契约（尺寸发现、分片、回退边界与已知限制）。
- `supernode/DIAGNOSTICS.md`、`supernode/TRAFFIC.md`：诊断接收端与流量统计的部署、口径和限制。
- `docs/V7*.md`、`docs/第*测试.md`、`docs/打洞测试*.md`：逐轮实测日志，只在需要查证某次现场数据时读。
