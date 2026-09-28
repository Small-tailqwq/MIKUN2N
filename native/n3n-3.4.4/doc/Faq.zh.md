# n3n 常见问题

> [English](Faq.md) | [简体中文](Faq.zh.md)


## 发布版本

### 哪里可以找到 Windows 版的二进制文件？

我们没有 Windows 安装包，但适用于较新 Windows 的 EXE 文件包含在最新的发布版本中。

如果想在 Windows XP 上使用（例如玩怀旧游戏），标准发布包里没有对应的二进制文件。自动测试工作流会生成它们：在 _Actions_ 页签中找到 _Testing_ 工作流，选择最新的一次运行，向下滚动到 _Artifacts_ 部分，其中 _binaries_ 文件的 `i686-w64-mingw32/usr/local/sbin/` 目录里就是 Windows 二进制文件。

## Supernode


### 我想搭一个只有自己能用的 supernode，最好还能设密码？

可以把小组名称当作密码：启动 supernode 时，用 `supernode.community_file` 选项指向一个简单的文本文件，里面只写一行你的秘密小组名称。这样它就是唯一允许的小组，只有属于这个小组的 edge 节点才能加入（在 edge 上用 `-c <小组名称>` 指定）。

如果还想避免秘密小组名称在网络上明文传输，**所有** edge 节点都应设置 `community.header_encryption=true`，启用头部加密。

另外，n3n 附带的 `community.list` 文件中介绍了这个文件的高级用法。

除了这道准入门槛之外，你可能还希望在各 edge 上启用数据加密（使用 `community.cipher` 选项）。只有 edge 能解密数据内容，supernode 不能。这样即使有人突破了 supernode 的准入限制，数据内容仍受加密保护，详见[这份文档](Crypto.md)（英文）。


### 能从 supernode 获取已连接的 edge 节点列表，以及它们的小组和来源 IP 地址吗？

获取方法见[管理接口](ManagementAPI.zh.md)文档。

如果启用了该功能（设置了 `management.port` 选项），用任何网页浏览器都能直接查看：

例如：
- 设置 `-Omanagement.port=5645`
- 打开 http://localhost:5645

### 支持多台 supernode 吗？

支持。请[阅读](Federation.zh.md)多台 supernode 如何组成联邦，以提高网络的可靠性。


### 一台 supernode 能同时监听多个端口吗？

supernode 本身只能监听一个端口。不过，你的防火墙也许可以把额外的 UDP 端口映射到 supernode 的常规端口上：

`sudo iptables -t nat -A PREROUTING -i <network interface name> -d <supernode's ip address> -p udp --dport <additional port number> -j REDIRECT --to-ports <regular supernode port number>`

这条命令可以作为额外的 `ExecStartPost=` 行（去掉 `sudo`）写进 supernode 的 `.service` 文件中；如有需要，可以写多行。


### 出现错误信息"process_udp dropped a packet with seemingly encrypted header for which no matching community which uses encrypted headers was found"怎么办？

这条错误信息的意思是：supernode 无法把某个数据包识别为未加密的包。supernode 会检查数据包格式是否正常；如果检查不通过，就假定头部是加密的（所以叫"_看起来_加密的头部"），然后逐个尝试所有可能对应密钥的小组（有些小组明确是不加密的，已经排除在外）。如果找不到匹配的小组，就会报这个错。

如果所有 edge 的 `community.header_encryption` 设置都一致（要么都开、要么都关），重启 supernode 也没用，那么最可能的原因是某个组件（某个 edge 或 supernode）版本过旧，使用了不同的数据包格式——数据包格式有时会在很短时间内发生大量变化，尤其是在分支上，或在尚未发布的 main 版本中。

所以，请确保所有 edge **以及** supernode 都是完全相同的构建版本。


## Edge


### 怎样知道点对点连接是否已经成功建立？

获取方法见[管理接口](ManagementAPI.zh.md)文档。

`n3nctl edges`

`n3nctl` 工具需要 Python，所以不一定总能用上。也可以用带 `--unix-socket` 选项的 `curl` 命令来查询。

另外，也可以在启动 edge 时用 `management.port` 配置选项指定一个 TCP 端口，然后用任意网页浏览器查看状态（仅限本机访问）。


### edge 反复报错"Authentication error. MAC or IP address already in use or not released yet by supernode"，是哪里出了问题？

edge 触发了 n3n 的防伪造保护。这个保护机制防止在原 edge 仍然在线时，另一个 edge 冒用它的身份（MAC 和 IP 地址），详见[这里](Authentication.zh.md)。通常有两种情况会触发它：

如果你使用的 MAC 或 IP 地址已经被占用，换一个就行。

如果 edge 是非正常退出的，比如用 `kill -9 ...` 或 `kill -SIGKILL ...` 强行结束，它就没有机会向 supernode 注销，supernode 仍然认为它在线。此时用相同的 MAC 或 IP 地址重新注册就会失败。大约两分钟后 supernode 会忘掉它，之后就能用同样的参数重新注册了。所以，要么等两分钟，要么换用不同的参数重启。

另外，原则上请始终用以下方式结束 edge：按 `CTRL` + `C`，或者用 `kill -SIGTERM ...` 或 `kill -SIGINT ...` 发送 SIGTERM 或 SIGINT 信号！不带 `-9` 的普通 `kill ...` 也可以。最后，向管理端口发送 `stop` 命令同样可以让 edge 正常退出。
