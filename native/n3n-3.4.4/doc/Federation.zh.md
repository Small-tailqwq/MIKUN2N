# Supernode 联邦

> [English](Federation.md) | [简体中文](Federation.zh.md)

## 设计思路

为了提高容灾和故障切换能力，也为了分担负载，多台 supernode 可以方便地互相连接，组成一个特殊的小组，称为**联邦**（federation）。


## 使用多台 Supernode

### 组建联邦

要组成联邦，各台 supernode 需要知道彼此的存在。为了让它们连上，需要在 supernode 上额外设置 `supernode.peer` 选项。

这个选项的值是另一台已知 supernode 的 IP 地址（或域名）和 UDP 端口，例如 `192.168.1.1:1234`。联邦中的 supernode 越多，就越适合把这个选项写进配置文件。

### 使用联邦

联邦中的各台 supernode 会自行把自己所知道的其他 supernode 信息传播给其余所有 supernode 以及各个 edge。

所以一开始，edge 只需要通过 `community.supernode` 选项连接其中一台 supernode（称为锚点 supernode）即可。这台 supernode 在 edge 启动时必须在线。

也可以用多个 `community.supernode` 选项，为 edge 提供同一联邦中的多台锚点 supernode。这样在无法确保某一台 supernode 启动时一定可用的情况下更稳妥。

## 工作原理

supernode 之间应当能像普通 edge 那样互相通信。为此引入了一个称为联邦的特殊小组。联邦功能提供了一些机制，把网络中的 supernode 互相连接起来，在不改变任何可见行为的前提下，增强备份、故障切换和负载分担能力。

联邦的默认名称是 `Federation`。在内部，名称前面会强制加上一个特殊字符（`*`），这样任何普通小组即使与联邦同名，也不会发生冲突。用户也可以自选一个联邦名称（所有 supernode 上必须相同），通过 `supernode.federation` 选项提供给 supernode。此外，也可以通过环境变量 `N3N_FEDERATION` 传入联邦名称。

联邦中的 supernode 使用 REGISTER_SUPER 消息互相注册。应答消息 REGISTER_SUPER_ACK 中携带了网络中其他 supernode 的信息。

edge 向 supernode 注册时也使用同样的机制，因此 edge 也能得知其他 supernode 的存在。

edge 收到这些信息后，由它自己决定连接哪一台 supernode。每个 edge 会不时 ping 各台 supernode，并从应答中获取它们的信息。我们选择实现按负载选择的策略，因为这更符合"让 supernode 保持低负载"的初衷。而且这样一来，整个网络的负载会均匀分布到所有可用的 supernode 上。

edge 会连接负载最低的 supernode，并在每次重新注册时重新评估。我们用一个"黏性"系数来避免 edge 在 supernode 之间频繁跳来跳去。

有了这项功能，n3n 能够应对针对 supernode 的 DoS 等攻击，并能把整个网络的负载公平地重新分配到所有 supernode 上。

如果希望 edge 按往返时间（RTT）选择 supernode，也就是选"最近"的那台，可以在 edge 上设置 `connection.supernode_selection=rtt`。注意，这时 supernode 之间的负载分配可能就没那么均衡了。

另外，设置 `connection.supernode_selection=mac` 会改为按 MAC 地址选择：选择在线的 supernode 中 MAC 地址最小的那台。
