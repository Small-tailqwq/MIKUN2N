# MikuN2N

面向非技术用户的 Windows n2n 联机助手。默认连接：

- Supernode：`vps.example.com:3076`（独立 n3n 网络）
- Community：`mygroup`
- 虚拟网段：`192.0.2.0/24`

## 当前功能

- 一键启动和停止 n2n edge
- 结合 n3n 注册日志和 TAP 地址确认是否真正加入网络，不依赖 Windows 上不稳定的管理接口
- 通过隧道内 UDP 心跳显示好友昵称、虚拟 IP、往返延迟和在线状态
- 按好友显示 `P2P 直连` 或 `pSp 中继`，并统计当前 P2P 直连数量
- 使用 n3n 数据 socket 探测 NAT 映射/过滤行为，在主界面显示 NAT1/2、NAT3 或 NAT4
- n3n 原生直连失败 5 秒后触发 22 秒有预算的分层打洞；NAT4 使用跨地址 control 近窗与轮转窗口，失败后保留可靠 Supernode 中继
- 固定 n3n UDP `50001` 并自动检测 UPnP；公网 IPv4 环境下自动续租端口映射
- 当路由器 WAN 位于私网或运营商 CGNAT 后方时给出明确提示
- 支持跟随系统、亮色和暗色三种界面主题
- 默认常驻系统托盘，可从托盘连接、断开、打开设置或退出
- 首次关闭窗口会询问是退出还是最小化到托盘，选择可在设置中修改
- 设置中的“关于”页面集中展示版本、上游项目、许可证及随包许可证文件
- 意外掉线后自动重启；平时完全不改动 TAP 网卡（沿用网卡自带的固定 MAC），仅当
  服务器尚未释放旧连接时才临时下发随机 MAC 立即重连，避免每次连接都重置网卡与
  Windows 网络位置
- 单实例与旧 edge 进程检查，避免两个客户端同时抢占 TAP
- 每次 edge 运行的完整日志保存到 `%LocalAppData%\MikuN2N\logs`
- 可在设置中选择日志保留 7/30/90/180 天或永久保留，程序每 6 小时自动清理过期日志
- 使用官方 n3n 3.4.4 Windows edge；发布包不再携带来源不可复现的旧 dirty n2n edge
- 连接时自动完成三项网卡适配，让虚拟网卡像真实局域网网卡一样工作：
  - 按虚拟网段 `192.0.2.0/24` 添加一条入站放行规则（规则名“MikuN2N 虚拟局域网”），
    使好友之间的联机、房间发现和 ping 不再被 Windows 默认入站拦截挡掉；作用域限定在
    虚拟网段内，不影响真实网络
  - 将虚拟网卡的网络位置设为“专用网络”，避免公用网络配置文件下的局域网发现限制
  - 把虚拟网卡的接口跃点降到 1。`224.0.0.0/4` 与 `255.255.255.255/32` 在每块网卡上
    都存在且路由跃点相同，平局由接口跃点决定；默认物理网卡跃点更低，游戏的房间广播会从
    物理网卡发出而进不了隧道。断开连接（含异常退出）后跃点交还 Windows 自动计算
- n2n 密钥不写入命令行和临时配置
- 可选使用 Windows DPAPI 保存密钥，仅当前 Windows 用户可以解密
- 全部应用配置和日志按 Unicode 处理
- 自动检测 TAP-Windows，并提供官方签名驱动的安装入口
- 明确指定 n3n 使用随包的 TAP-Windows 网卡（按 GUID），不会被加速器等第三方
  TAP 网卡抢走；指定网卡不可用时自动回退为自动选择

## 技术研究记录

- [NATPUNCH v7 阶段总结](NATPUNCH-V7阶段总结.md)：NAT4 探测、穿梭版本、
  实测边界、失败结论及后续接入建议。

## 开发构建

需要 Windows 和 .NET 9 SDK：

```powershell
dotnet build
```

构建产物位于 `bin/Debug/net9.0-windows/`。测试构建以 `0.4.1-test.b<UTC 时间戳>z`
作为完整版本号，每次执行构建都会自动生成新的构建身份。完整版本固定显示在主界面
右上角，并写入产物中的 `build-identity.txt`；测试反馈和分发包不得只记录基础版本
`0.4.1`。

## 发布

发给测试同伴的包用**框架依赖**构建（约 2 MB，对方需自行安装 .NET 9 Desktop
Runtime，未安装时双击会提示下载）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/MikuN2N-<完整版本号>
```

只有面向不懂电脑的用户的**正式发布**才用自包含构建。它把整个 .NET 运行时打进
程序，产物约 170 MB、解压后占盘约 173 MB（WPF 不支持 `PublishTrimmed`，无法裁剪），
不要拿它打日常测试包：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

两种方式都需要把 `bin/Release/net9.0-windows/win-x64/build-identity.txt` 复制到产物
目录，`Runtime/` 会自动随程序输出。

`Runtime/n3n-edge.exe` 和对应的 `n3n-3.4.4-source.zip` 会随程序发布。发布给朋友前还需要：

1. 在干净的 Windows 10/11 电脑上验证 TAP-Windows 安装流程。
2. 对程序和安装包进行代码签名。
3. 保留随包提供的 GPLv3 许可证和 n3n 3.4.4 对应源码归档。
4. 在至少两台 Windows 电脑上测试 Minecraft 与 Left 4 Dead 2。

## 独立服务端

服务器模板位于 `server/`。当前部署使用源码构建的 n3n 3.4.4、systemd 服务
`mikun2n-supernode.service`、公网 `3076/UDP` 和 `192.0.2.0/24`。旧 `3075`
n2n 服务保持不变。supernode 管理接口仅使用服务器本机 Unix socket，不向公网开放。
