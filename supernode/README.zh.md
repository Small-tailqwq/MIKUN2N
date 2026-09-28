# 自建 MikuN2N supernode

> [English](README.md) | [简体中文](README.zh.md)

MikuN2N 提供的是服务端代码和部署模板，不提供现成的服务器。你需要一台有公网 IPv4 地址、使用 systemd 的 Linux 机器。一起联机的人在客户端里填上它的地址，并填写相同的小组名称和联机密钥。

私有 IPv6 测试版会在每次连接时单独征得玩家同意后上传日志，相关部署见[测试诊断](DIAGNOSTICS.zh.md)。可选的 HTTPS 接收端会保存双方的日志，供你通过 SSH 查看。

## 构建配套的服务端

使用与客户端**同一个 MikuN2N 发布版本**的 `Runtime/n3n-3.4.4-source.zip`。其中包含修改过的 supernode 打洞协调代码和 IPv6 候选扩展。只安装上游的 n3n 软件包，得不到增强后的 NAT4 打洞效果。

对于 Debian/Ubuntu，安装构建依赖：

```bash
sudo apt update
sudo apt install build-essential autoconf automake unzip python3 pkg-config libcap-dev libzstd-dev
```

在 MikuN2N 源码根目录下：

```bash
bash supernode/build-supernode.sh
```

构建脚本把源码解压到临时目录，用 `autogen.sh` 重新生成 Linux configure 脚本，构建 n3n，并把以下文件放到 `artifacts/supernode/`：

- `n3n-supernode`：Linux 可执行文件。
- `n3n-3.4.4-source.zip`：与之对应的修改版源码。
- `SHA256SUMS`：校验和文件，其中的文件名不带路径，换个目录也能直接校验。

`--source-archive FILE` 和 `--output DIRECTORY` 可以指定别的发布源码包或输出目录。再分发任何服务端二进制时，都要连同源码包一起提供。原生源码在 Windows 上编译通过，并不能说明 Linux 构建或部署没有问题。

## 必需的端口

在主机防火墙和云服务商的安全组中都放行以下全部端口：

| UDP 端口 | 用途 |
|---|---|
| 3076，或你选择的主端口 | 注册、会合与中继 |
| 21001 | NAT 观测端点 A |
| 21002 | NAT 观测端点 B |

两个观测端点**必须使用与 supernode 相同的公网 IPv4 地址**。判断 NAT 类型时，要比较这台主机不同端口发回的应答。安装脚本会在 n3n 之外再启动现有的 Python 观测服务；它需要 Python 3，固定使用 21001/21002 端口。每台主机（每个地址）只部署一对观测端口；换一个服务名并不会多出一对独立的观测端口。

做这项对端 IPv6 直连实验，服务器本身不需要 IPv6 地址。但双方客户端仍必须能通过 IPv4 连上 supernode 来注册和汇合；只有 IPv6、没有 IPv4 通路的客户端连不上。IPv6 支持全局地址和可路由的 ULA/NAT66 候选。配套的测试版客户端可以使用另行部署在 UDP 3478 上的 IPv6 STUN 观察者，查出自己对端套接字的公网映射，再通过现有的、支持 NAT66 的 supernode 交换。这样双方都能主动发起检查，即使公网一侧的对端会过滤主动发来的入站 UDP 也不受影响。没有观察者时，使用 ULA 的一方仍要靠自己的第一个出站探测包先到达公网一方。仅凭上报的映射永远不会直接建立数据通路，仍必须通过对端的挑战/应答校验。映射随目标端点变化的 NAT、被拦截的 UDP 以及观察者不可用，都仍可能导致 IPv6 P2P 失败。NAT66 对 NAT66 还没有经过实地验证。路径较小时，一旦出现尺寸错误或探测超时，就改用 1232 字节的 UDP 探测尺寸；超过已验证尺寸的数据逐包退回 IPv4。客户端现有的防火墙规则在所有网络配置文件下都放行入站 UDP 50001，但不会去配置路由器。

IPv6 协议代次 2 还要求对端先通告一条仍然有效的接收路径，数据包才会切换到 IPv6。协议代次较旧的客户端仍可用 IPv4 互通。做 IPv6 测试前请升级所有客户端；这次握手协议的更新不需要重新部署 supernode。收到尺寸更小的入站探测时，会立即对该尺寸做一次对应的检查。IPv6 稳定五秒后，会暂停额外的 IPv4 扫描，但保留已有的 IPv4 目标和中继兜底。

当前服务端还会接收已注册客户端选择性上报的原生构建号和协议代次，并转发给支持这项功能的对端。新客户端会在好友列表里显示这些信息，并在 IPv6 协议代次不一致时给出说明。旧的客户端和服务端沿用原来的数据包格式，IPv4 互通不受影响；要显示这些版本信息需要更新服务端，不过新版桌面客户端之间通过局域网发现也能各自交换版本信息。

## 安装

在同一份源码检出目录下：

```bash
sudo bash supernode/install-supernode.sh \
  --binary "$(pwd)/artifacts/supernode/n3n-supernode" \
  --community mygroup --cidr 10.42.0.0/24
```

`mygroup` 和 `10.42.0.0/24` 只是示例，并不是提供给你的现成部署。虚拟子网要选一个不和任何参与者的实际网络重叠的网段。省略 `--cidr` 则自动分配地址。不加 `--community` 时，生成的配置接受任意小组名称；想限制的话，请编辑允许列表。

安装脚本把二进制和探测脚本复制到 `/opt/mikun2n/n3n`，把配置写到 `/etc/n3n`，安装两个 systemd 单元并启动它们。需要时使用 `--port PORT`、`--service-name NAME` 或 `--prefix /opt/another-directory`。前缀要放在用户主目录之外，因为服务使用了 `ProtectHome=true`。

重新安装时会保留已有的 `.conf`、小组列表和 `.env` 文件。新的 `--community`、`--cidr` 和 `--port` 参数不会覆盖已有配置。需要改的话请直接编辑这些文件，然后重启服务。重新安装会更新服务单元和可执行文件，并重启服务。

默认文件与服务名：

```text
/etc/n3n/mikun2n-supernode.conf
/etc/n3n/mikun2n-supernode-community.list
/etc/n3n/mikun2n-supernode.env
mikun2n-supernode.service
mikun2n-supernode-probe.service
```

对应的模板始终从本目录加载，使用自定义服务名时也一样。`community.list.template` 说明了允许列表的语法。

## 连接与检查

在客户端的节点管理里添加 `your-server.example.com:3076`。所有参与者需要相同的小组名称与联机密钥。应用本身不含任何内置地址或密钥。

```bash
sudo systemctl status mikun2n-supernode mikun2n-supernode-probe
sudo journalctl -u mikun2n-supernode -u mikun2n-supernode-probe -n 100 --no-pager
sudo ss -lunp
```

如果能注册但 NAT 检测失败，请检查 UDP 21001/21002、主机防火墙和安全组，以及探测服务。如果注册失败，请检查主 UDP 端口和小组配置。NAT4 与 NAT4 之间能否直连，取决于实测到的端口映射规律；网络的端口分配不可预测时，走中继是正常结果。

想试 IPv6，请安装用本版本源码构建的服务端，并在连接前让双方客户端都打开这项实验设置。只有本机的 IPv6 路径验证通过时，对端标签才会显示为 `直连 · IPv6`。广播发现和中继仍走现有的 IPv4 虚拟网络。这项实验不支持只有 IPv6 地址的服务端。

## 中继流量统计

如果想在服务端按发送方和接收方统计出站流量（包括广播复制出的流量和旧版 n2n 客户端），参见[流量统计](TRAFFIC.zh.md)。可选的本地采集器按小时汇总并保留 30 天，不额外开放任何端口。

## 中继带宽限制

小型云主机的带宽由服务商限定：一旦中继的游戏流量占满出站带宽，注册续期和打洞协调消息也会跟着被丢掉，直连反而更难建立。从 0.5.8-5 起的构建接受两个可选的 `[supernode]` 设置，默认都关闭：

- `mikun2n_relay_kbit`：中继数据包（包括广播副本）的总速率上限。建议设为主机出站带宽的 85% 左右（例如 4 Mbit/s 的主机设为 `3400`）。控制消息永远不受限制。带宽紧张时，每个发送方 edge 平分额度；只有链路还有空闲时，个别发送方才能超出自己的份额。
- `mikun2n_broadcast_pps`：每个 edge 每秒最多中继多少条广播。每条广播都会复制给小组里的每个成员；局域网游戏发现房间通常每秒只需要几条。

超出上限的数据包直接丢弃而不排队，因为游戏对丢包的容忍度比对延迟高。`get_relay_stats` 会返回配置的上限，以及 `policy_*` 开头的丢弃计数和超额借用计数；服务端日志最多每分钟汇总一次丢弃情况。正式依赖这些数值之前，请先用你自己的实际流量验证。

## 联邦

一个客户端节点可以保存多个服务端端点。联邦需要各台服务端做相应配置；当前限制参见[联邦说明](../docs/FEDERATION.zh.md)。安装脚本会保留已有的联邦环境文件。每个公网服务端地址仍需要各自的观测端点。

## 卸载

```bash
sudo bash supernode/uninstall-supernode.sh
```

这会停止并删除两个服务单元，但保留配置、二进制和源码。自定义过服务名的部署要加上 `--service-name NAME`。加 `--purge` 还会一并删除该部署的配置、环境文件和小组列表。
