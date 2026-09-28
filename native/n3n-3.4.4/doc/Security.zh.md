# 安全注意事项

> [English](Security.md) | [简体中文](Security.zh.md)

启用数据加密后（用 `community.key` 提供密钥），supernode 无法解密两个 edge 节点之间交换的流量，但它仍然知道 edge A 正在和 edge B 通信。

可选的加密方式有好几种。[加密说明](Crypto.md)（英文）中有一张简明的对比表，可以帮助你选择。n3n 的 edge 节点默认使用 AES 加密，也可以用 `community.cipher` 选项选择其他算法。

从源码编译时，可以用 `tools/n3n-benchmark` 对各种加密方式做性能测试。

数据包头部包含一些元数据，例如 edge 节点的虚拟 MAC 地址、IP 地址、真实主机名以及小组名称。可以在各 edge 上设置 `community.header_encryption=true`，选择对头部也进行加密。
