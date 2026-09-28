# MikuN2N

> [English](README.md) | [简体中文](README.zh.md)

MikuN2N 是一个 Windows 客户端，让你和朋友通过自建的虚拟以太网一起玩局域网游戏。它负责管理修改版 n3n edge 和 TAP-Windows 网卡、发现对端、断线重连，并提供支持浅色/深色主题的 WPF 界面。

**客户端不内置任何服务器地址。** 你可以按[服务器部署指南](supernode/README.zh.md)自建服务器，也可以在节点管理里添加朋友分享的服务器。一起联机的人需要填写相同的小组名称和联机密钥。一个节点可以包含多个联邦端点。

## 连通性

- 优先使用 n3n 原生的 IPv4 P2P；不通时再做有次数上限、按 NAT 类型调整的端口预测和多端口探测。NAT4 与 NAT4 之间能否直连，取决于双方网络的端口分配是否可预测；直连不成时仍可走 supernode 中继。
- 按对端分别显示 IPv4 P2P、IPv6 P2P、局域网直连、打洞中和中继几种状态，以及隧道延迟和在线情况。好友列表显示对方的客户端版本，版本的悬浮提示里还有正在运行的 n3n 构建号和 IPv6 协议代次。没有上报版本的旧客户端显示为未知。IPv6 协议代次不一致时会给出说明，IPv4 仍可正常使用；只是构建号不同并不妨碍 IPv6。
- 可选的 IPv6 UDP 对端传输，默认关闭。需要双方都在「设置 → 常规」里打开「尝试 IPv6 P2P 直连」，然后重新连接。
- IPv6 实验借助配套修改版的 **IPv4 supernode** 交换连接候选。启用 IPv6 之前，客户端会先确认一次指定大小的 UDP 往返成功、对端也已准备好接收；之后持续检查连通性，一旦超时或发送失败就退回 IPv4。当前的协议代次要求双方客户端都更新到配套版本，旧版对端仍走 IPv4。虚拟以太网本身和游戏流量始终是 IPv4。
- IPv6 只选用一个本机地址，优先全局 IPv6，也允许使用可路由的 ULA 来穿过 NAT66。配套的测试版可以配置 IPv6 STUN 观察者，用来发现对端套接字的公网映射，再通过支持 NAT66 的 supernode 交换，这样双方都能主动发起检查。没有观察者时，使用 ULA 的一方仍要靠自己的第一个探测包先到达公网一方。基础保活包与更大的尺寸探测互不影响，因此大探测反复丢失只会调低发送尺寸，不会断开路径；超过已确认尺寸的数据包在隧道内分片发送，而不是逐包退回 IPv4。映射随目标端点变化的 NAT 或被过滤的 UDP 仍可能导致无法直连，NAT66 对 NAT66 也尚未验证。验证通过的 IPv6 优先于经公网的 IPv4，但局域网直连的优先级更高。IPv6 的 RTT 只做记录，不与 IPv4 的 RTT 比较。仅支持 IPv6 的 supernode 和 IPv6 中继不在这次实验的范围内。

带编号的原生补丁清单只维护在 [Runtime/README.txt](Runtime/README.txt) 一处。服务端和客户端请使用同一版本的源码：只装上游原版的服务端，用不了 MikuN2N 扩展的打洞协调和 IPv6 候选交换。

## 桌面功能

- 节点管理、托盘操作、防止重复启动，以及 edge 意外退出后自动恢复。
- 安装 TAP-Windows、选择网卡、调整网卡优先级，并配置只针对本程序的防火墙规则。
- edge 使用 UDP 50001 端口，可选 UPnP 端口映射和 CGNAT 检测。
- 跟随系统、浅色和深色三种主题；可设置关闭窗口时的行为和日志保留时长。
- 在虚拟网络内发现对端并测量延迟。十秒内没有新的测量结果时显示「暂无法测量」；线路切换后会清掉旧的 RTT，探测照常继续。原生 IPv6 探测得到的 RTT 不会顶替隧道延迟。
- 联机密钥通过进程环境变量传给 edge，绝不出现在命令行参数或生成的配置文件里。选择记住密钥时，用 Windows DPAPI 按当前用户加密保存。
- 设置和日志保存在 `%LocalAppData%/MikuN2N`；写入新设置时，旧文件会一直保留到新文件写完为止。
- 通过 GitHub Releases 在程序内更新（见下文），默认每天检查一次，可在「设置 → 常规」里关闭。

## 构建

需要 Windows 和 .NET 9 SDK：

```powershell
dotnet build
```

输出目录：`bin/Debug/net9.0-windows/`。版本号由 `MikuN2N.csproj` 里的 `BaseVersion` 和 `BuildNumber` 组成，形如 `0.5.8-1`。每交付一个新包，序号加一；同一次交付重新编译时序号不变；`BaseVersion` 变化时序号重置为 1。反馈问题时，请附上界面里或 `build-identity.txt` 中的版本号。

每次交付对应一个提交和一个同名 tag（`git diff 0.5.8-1..0.5.8-2` 就是这一版的全部改动）。修改版原生 edge 也包括在内：它位于 `native/n3n-3.4.4/`，建立在一个未经修改的上游 3.4.4 基线提交之上，所以 `git log -- native/` 能列出每一次原生改动，与基线提交比较就得到完整的补丁集。客户端的离线回归测试在 `tools/offline-tests`，用 `dotnet run --project tools/offline-tests -- <空的临时目录>` 运行；原生侧的检查是 `native/n3n-3.4.4` 里的 `tests-wire` 和 `tools/tests-ipv6.c`。这些测试只覆盖协议和会话逻辑，不能代替真实联机。

修改版原生源码位于 `native/n3n-3.4.4/`，发布包里以 `Runtime/n3n-3.4.4-source.zip` 的形式附带。重新发布原生构建之前，先运行 `tools/sync-native-version.ps1`，按项目版本生成原生构建标识。然后在 Git Bash 中、确保 MinGW-w64 GCC 和 make 在 PATH 上，于原生源码根目录运行 `sh scripts/build-mikun2n-windows.sh`。直接用随包附带的源码包重新编译，会保留包内原有的构建标识。构建使用 GNU C17，并在调试信息中替换掉构建机上的路径。把生成的 `apps/n3n-edge.exe` 复制为 `Runtime/n3n-edge.exe`，再用 `tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>` 重新生成源码包，并同时更新各组件的哈希。源码包与 `native/` 不能逐字节对上时，`tools/package-release.ps1` 会拒绝打包。

## 打包

框架依赖包（需要另装 .NET 9 桌面运行时）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/MikuN2N
```

自包含包：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

两条命令都会写出 `build-identity.txt`，并带上显式列出的运行时文件：修改版 edge、对应源码、各许可证、组件声明和 TAP 安装程序。每次发布请使用全新的输出目录。旧版 edge 二进制和各种备份不会被打包。

## 发布与程序内更新

`tools/package-release.ps1` 生成自包含构建，检查其中带有修改版 edge、对应源码和全部许可证（并且没有夹带构建机上的账户路径），然后写出 `dist/MikuN2N-<版本>-win-x64.zip` 和 `dist/SHA256SUMS`。推送 `0.5.8-6` 这样的 tag，会在[发布工作流](.github/workflows/release.yml)中运行同一个脚本，并把这两个文件挂到一个**草稿** GitHub Release 上；只有正式发布（不是预发布）之后，客户端才能看到它。

客户端向 `api.github.com` 查询 `MikuN2N.csproj` 中 `UpdateRepository` 所指仓库的最新 Release。fork 可以用 `-p:UpdateRepository=owner/name` 指向自己的仓库；`-p:UpdateRepository=none` 则构建出永不检查更新的客户端。发现新版本后，客户端在后台下载，用该文件的 SHA-256（GitHub 提供的文件摘要或 `SHA256SUMS`）校验，解压后还要确认其中 `MikuN2N.exe` 的版本与 tag 一致，这些都通过后才会询问玩家。安装时先断开连接，再原地替换文件，失败则整体回滚，最后重启到新版本。开发版以及所在目录没有写权限时，会改为引导玩家打开 Release 页面。

源码包既能构建 Windows 版 edge，也能构建 Linux 版 supernode。服务端的构建和安装脚本在 [supernode/](supernode/README.zh.md)。服务端需要手动部署；本项目不提供任何服务器或账号凭据。

IPv6 仍处于实验阶段。编译通过并不代表实际能连通、游戏能兼容，也不代表浅色/深色界面已经验收。发布前的验证需要两位自愿参与的玩家、他们自己的服务器，并实际测试连接、回退、重连和 MTU 行为。

## 研究记录

[NATPUNCH v7 阶段总结](docs/NATPUNCH-V7-SUMMARY.zh.md)和[联邦说明](docs/FEDERATION.zh.md)记录了以往的实验及其局限。

## 许可证

MikuN2N 自己的代码（包括原生补丁和 `tools/natpunch`）按 [GPL-3.0-only](LICENSE) 授权，每个发布包里都以 `LICENSE.txt` 附带。随包分发的组件保留各自的许可证：

| 组件 | 许可证 | 如何履行义务 |
|---|---|---|
| 修改版 n3n 3.4.4 edge | GPL-3.0-only（其中 `connslot` 为 LGPL-2.1-only）| `Runtime/n3n-3.4.4-source.zip`（内含 `MIKUN2N-MODIFICATIONS.md`）、`Runtime/LICENSE-n2n.txt` |
| TAP-Windows 9.24.7 安装程序（未修改）| GPL-2.0，附 WDK 系统库例外 | `Runtime/LICENSE-tap-windows.txt`、上游源码链接和书面源码提供承诺 |
| .NET 9 运行时、WPF、Windows Forms | MIT | `Runtime/LICENSE-dotnet.txt`、`Runtime/THIRD-PARTY-NOTICES-dotnet.txt` |

详见 [Runtime/THIRD-PARTY-NOTICES.txt](Runtime/THIRD-PARTY-NOTICES.txt)。分发任何原生二进制时，都要一并提供对应的原生源码包。旧版 n2n `edge.exe` 没有可复现的源码，既不纳入版本控制，也不打包。

部署地址、小组名称、密钥和本地设置不要写进公开的源码和提交历史，只放在被 git 忽略的 `*.local.md` 文件里。仓库历史在公开前已经重写、清除了这类内容，可以直接推送。
