# MikuN2N

> [English](README.md) | [简体中文](README.zh.md)

一款 Windows 客户端，用于通过自建的虚拟以太网与好友联机局域网游戏。MikuN2N 管理一个打过补丁的 n3n edge、TAP-Windows、peer 发现、断线重连，以及一套浅色/深色 WPF 界面。

**客户端不内置任何服务器地址。** 按[服务器部署指南](supernode/README.zh.md)自建服务器，或在节点管理里添加朋友分享的服务器。参与者需要使用相同的小组名称与联机密钥。一个节点可以包含多个联邦端点。

## 连通性

- 原生 n3n IPv4 P2P，必要时辅以有界的 NAT 感知端口预测与 worker-bank 探测。NAT4↔NAT4 直连取决于双方网络的可预测性；supernode 中继仍然可用。
- 每个 peer 的 IPv4 P2P、IPv6 P2P、本地直连、打洞与中继状态，附带隧道延迟与在线状态。好友列表显示客户端版本；版本悬浮提示包含正在运行的 n3n 构建号与 IPv6 wire 版本。未上报的旧版本保持未知。IPv6 wire 不匹配时会说明，同时 IPv4 仍可用；仅构建号不同不会阻止 IPv6。
- 可选的 IPv6 UDP peer 传输，默认关闭。在双方的「设置 → 常规」里开启「尝试 IPv6 P2P 直连」，然后重连。
- IPv6 实验使用配套打过补丁的 **IPv4 supernode** 来交换候选。它在优先选择 IPv6 之前，先验证一次指定大小的 UDP 往返与 peer 的接收就绪状态，检查存活，并在超时或发送失败时回退到 IPv4。当前 wire 代次要求客户端配套更新；旧 peer 保持 IPv4 连通。虚拟以太网与游戏流量仍为 IPv4。
- IPv6 会选择一个本机地址，优先全局 IPv6，并允许为 NAT66 使用一个可路由的 ULA。配套的测试构建可配置一个 IPv6 STUN 观察者，用来发现 peer socket 的公网映射并经支持 NAT66 的 supernode 交换，使双方都能发起检查。没有观察者时，ULA peer 仍依赖其第一个探测包到达公网 peer。基础 keepalive 独立于更大的尺寸探测，因此反复丢失更大的探测只会降低发送尺寸而不会丢弃路径；超过已确认尺寸的 DATA 在隧道内分片，而不是逐包回退到 IPv4。端点相关的映射或过滤 UDP 仍可能阻止直连，NAT66↔NAT66 仍未验证。已验证的 IPv6 优先于公网 IPv4，而本地直连保持更高优先级。IPv6 RTT 只做记录，不与 IPv4 RTT 比较。仅 IPv6 的 supernode 与 IPv6 中继不在本实验范围内。

带编号的原生补丁清单只维护在 [Runtime/README.txt](Runtime/README.txt)。请使用同一版本的服务端与客户端源码：仅使用上游原版的服务端不提供 MikuN2N 的扩展协调或 IPv6 候选交换。

## 桌面功能

- 节点管理、托盘控制、单实例保护，以及 edge 异常退出后的恢复。
- TAP-Windows 安装、适配器选择、接口优先级与作用域防火墙配置。
- edge 使用 UDP 50001，可选的 UPnP 映射与 CGNAT 检测。
- 跟随系统、浅色与深色主题；可配置的关闭行为与日志保留。
- 虚拟网络内的 peer 发现与延迟测量。样本缺失或过期时，十秒后显示「暂无法测量」；路由变化会清掉旧的 RTT，同时探测继续。原生 IPv6 探测的 RTT 不能替代隧道测量。
- 加密密钥通过进程环境变量传入，绝不写进命令行参数或生成的配置文件。可选的密钥存储使用 Windows DPAPI，作用域为当前用户。
- 设置与日志存放在 `%LocalAppData%/MikuN2N`；设置替换会保留旧文件，直到新文件写入完成。
- 来自 GitHub Releases 的应用内更新（见下文），默认每天检查，除非在「设置 → 常规」里关闭。

## 构建

需要 Windows 与 .NET 9 SDK：

```powershell
dotnet build
```

输出：`bin/Debug/net9.0-windows/`。`MikuN2N.csproj` 里的 `BaseVersion` 与 `BuildNumber` 生成诸如 `0.5.8-1` 的版本号。每交付一个新包就把序号加一；同一交付的重建保持该序号不变，`BaseVersion` 变化时重置为 1。报告问题时，从界面或 `build-identity.txt` 记录版本号。

每次交付对应一次提交加一个同名 tag（`git diff 0.5.8-1..0.5.8-2` 即该版本的全部改动），这也包括打过补丁的原生 edge：它位于 `native/n3n-3.4.4/`，建立在一个未修改的上游 3.4.4 基线提交之上，因此 `git log -- native/` 列出每一次原生改动，与该基线做 diff 即得到完整补丁集。客户端离线回归测试位于 `tools/offline-tests`，用 `dotnet run --project tools/offline-tests -- <空临时目录>` 运行；原生检查是 `native/n3n-3.4.4` 里的 `tests-wire` 与 `tools/tests-ipv6.c`。这些只覆盖协议与会话逻辑——不是真实连接。

打过补丁的原生源码位于 `native/n3n-3.4.4/`，发布包以 `Runtime/n3n-3.4.4-source.zip` 随附。在新的原生发布构建之前，运行 `tools/sync-native-version.ps1` 从项目版本生成其构建标识。然后在 Git Bash 里、MinGW-w64 GCC 与 make 位于 PATH 的前提下，从原生源码根目录运行 `sh scripts/build-mikun2n-windows.sh`。直接重建随附的源码包会保留其内置标识。这使用 GNU C17，并在调试信息里映射构建路径。把 `apps/n3n-edge.exe` 复制到 `Runtime/n3n-edge.exe`，然后用 `tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>` 重新生成源码包，并一起更新各组件哈希。源码包与 `native/` 不逐字节一致时，`tools/package-release.ps1` 会拒绝打包。

## 发布

框架依赖包（需要 .NET 9 Desktop Runtime）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/MikuN2N
```

自包含包：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

两条命令都会写入 `build-identity.txt`，并包含明确列出的运行时文件：打过补丁的 edge、对应的源码、许可证、组件声明与 TAP 安装器。每个版本请使用全新的输出目录。旧的 edge 二进制与备份会被排除。

## 发布与应用内更新

`tools/package-release.ps1` 发布自包含构建，校验它带有打过补丁的 edge、对应源码与全部随附许可证（且不含构建机器上的任何账户路径），并写出 `dist/MikuN2N-<version>-win-x64.zip` 与 `dist/SHA256SUMS`。推送诸如 `0.5.8-6` 的 tag 会在[发布工作流](.github/workflows/release.yml)中运行同一脚本，并把两个文件挂到 **draft** GitHub release 上；只有发布为正式（非预发布）release 后客户端才看得到。

客户端向 `api.github.com` 查询 `MikuN2N.csproj` 里 `UpdateRepository` 指定的仓库的最新 release。fork 用 `-p:UpdateRepository=owner/name` 设置自己的仓库；`-p:UpdateRepository=none` 会构建一个从不检查更新的客户端。新包在后台下载，先按资产的 SHA-256（GitHub 的资产 digest 或 `SHA256SUMS`）校验，解包后其 `MikuN2N.exe` 版本必须与 tag 一致，之后才会询问玩家。安装会停止连接，原地换文件，失败时回滚，然后重启新版本。开发构建与无写权限的目录会改为指向 release 页面。

源码包同时支持 Windows edge 与 Linux supernode。服务端构建与安装脚本在 [supernode/](supernode/README.zh.md)。部署是手动的；本项目不提供任何服务器或凭据。

IPv6 仍是实验性的。编译通过不代表实际连通性、游戏兼容性或浅色/深色视觉验收合格。发布验证需要两个自愿的客户端、他们自己的服务器，以及对连接、回退、重连与 MTU 行为的测试。

## 研究

[NATPUNCH v7 总结](docs/NATPUNCH-V7-SUMMARY.zh.md) 与[联邦说明](docs/FEDERATION.zh.md)记录了过往实验及其边界。

## 许可证

MikuN2N 自身的代码，包括原生补丁与 `tools/natpunch`，按 [GPL-3.0-only](LICENSE) 许可，并在每个包里以 `LICENSE.txt` 随附。随附组件保留各自的许可证：

| 组件 | 许可证 | 义务履行方式 |
|---|---|---|
| 打过补丁的 n3n 3.4.4 edge | GPL-3.0-only（含 LGPL-2.1-only `connslot`）| `Runtime/n3n-3.4.4-source.zip`（含 `MIKUN2N-MODIFICATIONS.md`）、`Runtime/LICENSE-n2n.txt` |
| TAP-Windows 9.24.7 安装器（未修改）| GPL-2.0（含 WDK 系统库例外）| `Runtime/LICENSE-tap-windows.txt`、上游源码链接与书面源码要约 |
| .NET 9 运行时、WPF、Windows Forms | MIT | `Runtime/LICENSE-dotnet.txt`、`Runtime/THIRD-PARTY-NOTICES-dotnet.txt` |

详情见 [Runtime/THIRD-PARTY-NOTICES.txt](Runtime/THIRD-PARTY-NOTICES.txt)。每个原生二进制都必须随附对应的原生源码包。旧版 n2n `edge.exe` 无可复现源码，既不被跟踪也不被打包。

请勿把部署地址、小组名称、密钥与本地设置写进公开源码与历史记录；只把它们放进被 git 忽略的 `*.local.md` 文件。仓库历史在公开前已重写过以移除这些内容，因此可以原样推送。
