> [English](README.md) | [简体中文](README.zh.md)
>
> 本文是 MikuN2N 项目为 n3n 3.4.4 文档所做的中文翻译，内容以英文原文为准。本源码树相对上游的改动见 [MIKUN2N-MODIFICATIONS.zh.md](MIKUN2N-MODIFICATIONS.zh.md)。

# n3n

n3n 是一个轻量级的点对点（P2P）VPN，用来建立虚拟网络。

开始使用 n3n 需要两类角色：

- 一个 _supernode_（超级节点）：让各个 edge 节点报到并发现彼此。它必须有一个能从互联网访问的端口。
- 若干 _edge_ 节点：加入虚拟网络的各台机器。

在 n3n 中，由多个 edge 节点共享的一个虚拟网络称为一个 _community_（小组）。一台 supernode 可以同时为多个小组中转，一台电脑也可以同时加入多个小组（运行多个 _edge_ 守护进程即可）。edge 节点可以用一个加密密钥，对本小组内的数据包加密。

只要条件允许，n3n 会尝试在 edge 节点之间直接建立基于 UDP 的点对点连接。做不到时（通常是因为某些特殊的 NAT 设备），就改由 supernode 中转数据包。

n3n 最初基于更早的 n2n 项目，并希望保持与它的协议兼容。

注意：有些发行版自带的 n2n 软件包非常旧，与 n3n 使用的协议不兼容。至少 Debian 上的 n2n 版本是 1.3.1，使用的是 2008 年的协议，多年来一直与 n2n 的各个稳定版不兼容，自然也肯定无法与 n3n 互通。

## 许可证

- 新增的、自成一体的工具或模块按 GPL-2.0-only 授权。
- 已有代码按 GPL-3-only 授权。
- 代码库中存在多个不同的版权持有人。
- 项目没有贡献者许可协议（CLA），因此不存在某一个主体能够独占代码的所有权或更改许可证。

## 快速上手

适用于 Debian、Ubuntu 或其他基于 dpkg 的系统：

- 从[最新稳定版](https://github.com/n42n/n3n/releases/latest)下载软件包。

- 安装软件包。

- 创建配置文件 `/etc/n3n/mynetwork.conf`，内容如下：
  ```
  [community]
  name=mynetwork
  key=mypassword
  supernode=supernode.ntop.org:7777
  ```

- 启动服务：`sudo systemctl start n3n-edge@mynetwork`

- 检查连接：`sudo n3nctl -s mynetwork supernodes`

- 列出发现的其他节点：`sudo n3nctl -s mynetwork edges`

**重要：** 强烈建议自己选一个小组名称（`community.name` 选项）和一个保密的加密密钥（`community.key` 选项），以防其他用户连进你的电脑。

也建议你搭建自己的 [supernode](doc/Supernode.zh.md)。

# 另见

- [从源码构建](doc/Building.zh.md)
- [安全注意事项](doc/Security.zh.md)
- [高级配置](doc/Advanced.md)（英文）
- [常见问题](doc/Faq.zh.md)（FAQ）
- 内部实现细节见[开发者指南](doc/Hacking.md)（英文）

## 参与贡献

你可以通过多种方式为 n3n 做贡献：

- 在[已有的 issue](https://github.com/n42n/n3n/issues) 中补充信息，或提交一个附有详细信息的新 issue
- 提议新功能
- 改进文档
- 提交带有改进的 pull request


---

(C) 2007-22 - ntop.org and contributors
Copyright (C) 2023-24 Hamish Coleman
