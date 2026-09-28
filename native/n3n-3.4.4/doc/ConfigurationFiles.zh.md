# 配置文件

> [English](ConfigurationFiles.md) | [简体中文](ConfigurationFiles.zh.md)

为了方便部署、更好地管理各地不同的配置，n3n 的 `n3n-edge` 和 `n3n-supernode` 都支持使用配置文件。

守护进程会根据"会话名"（sessionname）查找配置文件；edge 守护进程的会话名默认为"edge"，对应的配置文件就是位于"/etc/n3n"（Windows 上为 %USERPROFILE%\n3n 目录）中的"edge.conf"。

配置文件是纯文本文件，格式与 INI 文件非常相似。

查看当前所有选项的帮助文档：
```bash
n3n-edge help config
```

假设你创建了如下的 `/etc/n3n/testing.conf` 文件：

```
[community]
cipher = Speck
key = mysecretpass
name = mynetwork
supernode = supernode.ntop.org:7777

[daemon]
background = false

[tuntap]
address = 192.168.100.1
address_mode = static
```

可以这样加载它：

```
sudo ./n3n-edge start testing
```

如有需要，配置文件中的所有设置都可以用命令行参数覆盖。

也可以额外提供命令行参数：

```
sudo n3n-edge start testing \
    -Oconnection.description=myComputer \
    -O community.compression=lzo
```

一些最常用的选项还有简写形式，可以用下面的命令查看全部简写：

```
n3n-edge help options
```
