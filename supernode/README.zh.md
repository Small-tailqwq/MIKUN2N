# 自建 MikuN2N supernode

> [English](README.md) | [简体中文](README.zh.md)

MikuN2N 提供的是服务端代码和部署模板，而不是托管好的服务器。使用一台具有公网 IPv4 地址、安装了 systemd 的 Linux 机器。参与者在客户端里填入它的地址，并使用相同的小组名称与联机密钥。

对于带逐连接日志上传明确授权的私有 IPv6 测试构建，参见[测试诊断](DIAGNOSTICS.zh.md)。可选的 HTTPS 接收端会保存双方的日志，便于通过 SSH 查看。

## 构建匹配的服务端

使用与客户端**同一个 MikuN2N 发布版本**的 `Runtime/n3n-3.4.4-source.zip`。它包含修改过的 supernode 协调代码与 IPv6 候选扩展。只安装上游 n3n 软件包无法复现增强的 NAT4 行为。

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
- `n3n-3.4.4-source.zip`：对应的补丁源码。
- `SHA256SUMS`：使用可移植文件名的校验和。

`--source-archive FILE` 和 `--output DIRECTORY` 用于选择另一个发布归档或输出目录。转发任何服务端二进制时都要保留源码归档。原生源码的 Windows 构建无法验证 Linux 构建或部署。

## 必需的端口

在主机防火墙和云服务商的安全组中都放行以下全部端口：

| UDP 端口 | 用途 |
|---|---|
| 3076，或你选择的主端口 | 注册、会合与中继 |
| 21001 | NAT 观测端点 A |
| 21002 | NAT 观测端点 B |

两个观测端点**必须使用与 supernode 相同的公网 IPv4 地址**。NAT 分类会比较来自该主机不同端口的回包。安装脚本会在 n3n 旁启动现有的 Python 观测服务；它需要 Python 3，并使用固定端口 21001/21002。每台主机/地址部署一对这样的端口。修改服务名不会创建另一对独立的观测端口。

服务器端不需要 IPv6 地址即可进行这个 IPv6 对等实验。双方客户端仍需与 supernode 之间有可用的 IPv4 连通性来完成注册与会合；没有 IPv4 通路的纯 IPv6 客户端无法连接。IPv6 支持全局地址和可路由的 ULA/NAT66 候选。匹配的测试客户端可以使用单独配置在 UDP 3478 上的 IPv6 STUN 观测器，发现其对等 socket 的公网映射，再通过现有的具备 NAT66 能力的 supernode 交换。这让双方都能发起检查，包括在公网对端过滤未经请求的入站 UDP 时。没有观测器时，ULA 对端仍依赖其首个出站探测到达公网对端。仅凭上报的映射永远无法建立数据通路；对端 challenge/response 校验仍是必需的。端点相关的映射、被封禁的 UDP 和不可用的观测器仍可能阻碍 IPv6 P2P。NAT66 到 NAT66 尚未经过实机验证。较小的通路在出现尺寸错误或探测超时后使用 1232 字节的 UDP 探测预算；超过已检查预算的数据逐条回退到 IPv4。现有客户端防火墙规则在所有配置下都允许入站 UDP 50001，但不会配置路由器。

IPv6 线格式版本 2 还要求对端在 DATA 切换到 IPv6 前通告一条仍然有效的接收通路。旧客户端线格式版本保持 IPv4 连通性。进行 IPv6 测试前要升级每一个客户端；这次握手更新不需要重新部署 supernode。更小的入站探测会立即触发匹配的尺寸检查。稳定的 IPv6 会在五秒后暂停额外的 IPv4 扫描，保留现有 IPv4 目标和中继回退。

当前服务端还会接收已注册客户端可选上报的原生构建/线格式身份信息，并返回给具备相应能力的对端。新客户端会在好友列表中显示这些信息，并解释 IPv6 协议不匹配的情况。现有客户端和服务端保持原有的数据包布局与 IPv4 连通性；元数据需要这次服务端更新，而新桌面客户端之间的直接发现仍可独立交换版本信息。

## 安装

在同一份源码检出目录下：

```bash
sudo bash supernode/install-supernode.sh \
  --binary "$(pwd)/artifacts/supernode/n3n-supernode" \
  --community mygroup --cidr 10.42.0.0/24
```

`mygroup` 和 `10.42.0.0/24` 只是示例，不是现成的部署。选择一个不与参与者物理网络重叠的虚拟子网。省略 `--cidr` 则使用自动地址分配。不加 `--community` 时，生成的配置接受任意小组名称；如果想加以限制，请编辑允许列表。

安装脚本把二进制和探测脚本复制到 `/opt/mikun2n/n3n`，把配置写到 `/etc/n3n`，安装两个 systemd 单元并启动它们。需要时使用 `--port PORT`、`--service-name NAME` 或 `--prefix /opt/another-directory`。前缀要放在用户主目录之外，因为服务使用了 `ProtectHome=true`。

重新安装时会保留已有的 `.conf`、小组列表和 `.env` 文件。新的 `--community`、`--cidr` 和 `--port` 参数不会覆盖已有配置。请显式编辑这些文件，然后重启。重新安装会更新服务单元和可执行文件，并重启部署。

默认文件与服务名：

```text
/etc/n3n/mikun2n-supernode.conf
/etc/n3n/mikun2n-supernode-community.list
/etc/n3n/mikun2n-supernode.env
mikun2n-supernode.service
mikun2n-supernode-probe.service
```

匹配的模板始终从这个目录加载，包括使用自定义服务名时。`community.list.template` 说明了允许列表的语法。

## 连接与检查

在客户端的节点管理里添加 `your-server.example.com:3076`。所有参与者需要相同的小组名称与联机密钥。应用本身不含任何内置地址或密钥。

```bash
sudo systemctl status mikun2n-supernode mikun2n-supernode-probe
sudo journalctl -u mikun2n-supernode -u mikun2n-supernode-probe -n 100 --no-pager
sudo ss -lunp
```

如果注册正常但 NAT 发现失败，检查 UDP 21001/21002、双方防火墙和探测单元。如果注册失败，检查主 UDP 端口和小组配置。NAT4 到 NAT4 能否成功取决于观测到的映射行为；对于不可预测的网络，中继是预期结果。

要尝试 IPv6，安装用本发布版本构建的服务端，并在连接前于双方客户端启用实验性设置。只有在本地 IPv6 通路通过验证时，对端标签才会变为 `直连 · IPv6`。广播发现和中继仍使用现有的 IPv4 虚拟网络。此实验不支持纯 IPv6 服务端地址。

## 中继流量统计

关于仅服务端侧、按发送方和接收方统计的出站计量（包括广播扇出和旧版 n2n 客户端），参见[流量统计](TRAFFIC.zh.md)。可选的本地采集器会保留 30 天的小时级总量，且不额外开放任何端口。

## 中继带宽限制

小型云主机会被服务商限速：一旦被中继的游戏流量占满出站链路，注册续期和打洞协调也会随之被丢弃，从而使直连更难建立。从 0.5.8-5 起的构建接受两个可选的 `[supernode]` 设置，默认都关闭：

- `mikun2n_relay_kbit`：被中继 DATA 的上限，包括广播副本。把它设为主机出站带宽的约 85%（例如在 4 Mbit/s 主机上设 `3400`）。控制消息从不被限制。发生争用时，每个发送 edge 获得均等的份额；只有当链路还有富余容量时，某个来源才可能超过它。
- `mikun2n_broadcast_pps`：每个 edge 每秒被中继的广播数。每条广播会被复制给小组里的每个成员；局域网发现通常每秒只需几条。

超出的 DATA 会被直接丢弃而不是排队，游戏对丢弃的容忍度高于延迟。`get_relay_stats` 会报告配置的上限以及 `policy_*` 丢弃和借用计数器；服务端日志最多每分钟汇总一次丢弃情况。在依赖这些数值之前，请根据你自己的流量进行验证。

## 联邦

一个客户端节点可以保存多个服务端端点。联邦需要服务端之间配置匹配；当前限制参见[联邦说明](../docs/FEDERATION.zh.md)。安装脚本会保留已有的联邦环境文件。每个公网服务端地址仍需要各自的观测端点。

## 卸载

```bash
sudo bash supernode/uninstall-supernode.sh
```

这会停止并移除这两个服务单元，同时保留配置、二进制和源码。自定义部署时加上 `--service-name NAME`。`--purge` 还会额外删除该部署的配置、环境文件和小组列表。
