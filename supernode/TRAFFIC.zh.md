# Supernode 出站流量统计

> [English](TRAFFIC.md) | [简体中文](TRAFFIC.zh.md)

打过补丁的 supernode 在其现有的本地管理 socket 上暴露了 `get_relay_stats`。`relay-traffic.py` 每 60 秒采样一次，并以 SQLite 按小时保留 30 天的总计。整个过程不涉及客户端更新、公网监听、抓包或客户端日志上传。仅存储服务端观测到的投递元数据与计数器；客户端诊断上传的授权流程保持独立。

## 通过 SSH 查看

```sh
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 72 --top 30 --hourly
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 24 --json
sudo systemctl status mikun2n-traffic --no-pager
```

报告展示全部出站流量、分类总计、定向投递，以及按所引发的服务端出站流量排名的发送方，包括广播扇出。名称与虚拟 IP 是最近观测到的注册标签；MAC 加 community 是统计键。离线 peer 的标签保留 30 天。普通 n2n 客户端即使不出现在 MikuN2N 的好友发现界面中也会被计入。缺失的标签回退到 MAC。MAC 轮换会形成独立身份；名称、MAC 与注册标签都不是经过验证的真实个人身份。

## 统计边界

* `pSp`：向本地已注册的目标 edge 成功发送一次单播 DATA。
* `broadcast-copy`：向一个目标 edge 成功投递一份 DATA 副本。向十个接收方发送 1 KiB 会被计为发送方造成的 10 KiB 服务端出站流量。
* `federation-unicast`：经另一个 supernode 发往已知远端目标的 DATA。目标键是最终 edge 的 MAC。
* `federation-flood`：发往某个联邦 supernode 的一份 DATA 副本。目标键是该 supernode 的 MAC，而非最终客户端。这包含未知单播泛洪。这些分类不得被解读为本地客户端的 pSp 链路。
* `all-egress`：supernode 网络 `sendto` 辅助函数返回的正向字节数，包括控制流量、TCP 分帧与不完整的 TCP 发送。发送错误单独计数。配对总计只统计完整的 DATA 发送。
* 字节数是操作系统接受的 n3n 数据报/流字节，不含 IP/UDP/TCP/链路层头部与内核重传。成功不代表远端确已收到。相对配对总计的残差是近似的，因为分页是在略有不同的时刻读取的。它包含控制流量与不完整的发送。
* 直连的 IPv4/IPv6 peer 流量绕过服务端，因此不被计量。一条记录只证明所选时段内使用了中继，而非永久性的连接模式。此数据无法还原安装前的流量，也不会揭示游戏载荷内容。

每个 supernode 进程最多有 8192 个定向流键；超出未跟踪的 DATA 仍会在 `flow-overflow` 中可见。已有的键继续计数。每个数据包都没有文件 I/O。计数器检查点与按小时增量在同一个 SQLite 事务中提交，因此重启采集器不会重复计数。进程身份包含主机 boot ID、PID 与 supernode 启动时间。

supernode 崩溃/重启时未采集的流量会丢失（通常最多一个采样间隔，采集器停机期间会更长）。只要同一 supernode 持续运行，仅采集器停机不会丢失累计字节。恢复的增量被归入采集所在的小时，不会跨缺失的小时重建。报告会显示采样新鲜度、观测到的重启以及超过 180 秒的缺口。首个样本包含当前进程的整个生命周期。请求的时间段向下取整到 UTC 整点边界；当前小时是不完整的。

## 安装

首先构建并部署配套的、打过补丁的 supernode，同时保留其对应的源码归档和上一版二进制的备份。原生补丁只新增一个只读的管理方法；它不改变线路协议、路由、节点配置或客户端行为。更新二进制需要重启一次 supernode。独立的采集器之后可以单独重启，无需重启 n3n。

```sh
sudo install -d -m 0755 /opt/mikun2n/traffic
sudo install -m 0755 supernode/relay-traffic.py /opt/mikun2n/traffic/relay-traffic.py
sudo install -m 0644 supernode/mikun2n-traffic.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mikun2n-traffic.service
```

默认 socket 是 `/run/n3n/mikun2n-supernode/mgmt`。对于其他实例，在 `ExecStart` 中使用带 `--socket PATH --db PATH` 的服务覆盖；绝不要在不同的 supernode 或并行的采集器之间共享数据库。第二个实例（例如联邦测试服务）需要自己的部署与数据库。

采集器仅限使用 Unix socket，并把数据库存放在 `/var/lib/mikun2n-traffic/traffic.sqlite3`（目录权限 0700，umask 0077）。无需额外的防火墙端口或 supernode 文件系统权限。SQLite 在保留期清理后会复用已释放的页面；文件大小保持在之前的最高水位。回滚时停止采集器并恢复保存的服务端二进制即可；数据库可保留供后续分析。

## 对照云计费时间窗

采集器还会在 `host_hourly` 中持久化默认路由接口的 RX/TX 增量。这些计数器包含接口暴露的所有进程、内核头部与重传。可用 `--interface NAME` 指定不同的物理接口。首个样本建立基线；此前整个开机周期内的字节不会被错误地计入安装所在的小时。boot ID 与接口索引的变化会建立新的基线。即使 n3n 不可用，主机采样仍会继续。

```sh
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --since 2026-09-17T00:00:00+08:00 --until 2026-09-18T00:00:00+08:00 --hourly
```

起始时间包含在内，结束时间不包含在内。显式边界需要带时区的 ISO 时间戳，且必须是整点。中继表与主机表使用同一个时间窗；报告会把主机覆盖范围与中继安装时间分开显示，并以十进制的 GB 以及二进制单位打印主机 OUT。采样增量被归入采集所在的小时（最多一个常规 60 秒间隔的边界误差）。停机之后，恢复的累计字节属于恢复所在的小时。接口计数器可能包含服务商不计费的内网流量等；它们不能替代服务商的计量表。较早缺失的样本无法重建整天的账单。仅升级此采集器无需重启 supernode。
