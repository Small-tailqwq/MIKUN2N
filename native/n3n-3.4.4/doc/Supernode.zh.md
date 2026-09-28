# 搭建自己的 Supernode

> [English](Supernode.md) | [简体中文](Supernode.zh.md)

为了保护你所发送数据的隐私，也为了减轻 `supernode.ntop.org` 的负担、不依赖它，建议你搭建自己的 supernode。

你可以在一台公网服务器（例如 VPS）上搭建 supernode，建立自己的基础设施。只需要在防火墙（通常是 `iptables`）上开放一个端口（下例中为 1234）。

1. 安装 n3n 软件包。
2. 编辑 `/etc/n3n/supernode.conf`，加入以下内容：
   ```
   [connection]
   bind=1234
   ```
3. 用 `sudo systemctl start n3n-supernode` 启动 supernode 服务。
4. 可选：让 supernode 开机自启：`sudo systemctl enable n3n-supernode`

现在 supernode 服务应该已经在 1234 端口上运行了。在各个 edge 节点上指定 `-l your_supernode_ip:1234` 即可使用它。所有 edge 节点必须使用同一台 supernode（或者属于同一个 [supernode 联邦](Federation.zh.md)）。
