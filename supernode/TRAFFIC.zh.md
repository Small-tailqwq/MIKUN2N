# Supernode 出站流量统计

> [English](TRAFFIC.md) | [简体中文](TRAFFIC.zh.md)

修改版 supernode 在它原有的本地管理套接字上提供 `get_relay_stats` 方法。`relay-traffic.py` 每 60 秒采样一次，按小时汇总存进 SQLite，保留 30 天。整个过程不需要更新客户端，不开放公网端口，不抓包，也不上传客户端日志。只保存服务端自己看到的转发元数据和计数；与客户端诊断上传的授权流程互不相干。

## 通过 SSH 查看

```sh
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 72 --top 30 --hourly
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --hours 24 --json
sudo systemctl status mikun2n-traffic --no-pager
```

报告列出全部出站流量、各类别合计、每对收发方之间的转发量，以及按各发送方造成的服务端出站流量排序的列表（广播复制出的流量也算在发送方头上）。名称和虚拟 IP 取自最近一次看到的注册信息；统计时以 MAC 加小组名称作为区分依据。离线对端的名称保留 30 天。普通 n2n 客户端即使不出现在 MikuN2N 的好友界面里，也会被统计在内。没有名称时显示 MAC。MAC 更换后会被当作另一个身份；名称、MAC 和注册信息都不是经过核实的真实身份。

## 统计边界

* `pSp`：向在本 supernode 注册的目标 edge 成功发送一个单播数据包。
* `broadcast-copy`：向一个目标 edge 成功发送一份广播数据包副本。向十个接收方发送 1 KiB 会被计为发送方造成的 10 KiB 服务端出站流量。
* `federation-unicast`：经由另一台 supernode 发往已知远端目标的数据包。目标按最终 edge 的 MAC 统计。
* `federation-flood`：发往某台联邦 supernode 的一份数据包副本。目标按那台 supernode 的 MAC 统计，而不是最终的客户端，其中也包括目标未知时的单播泛洪。这两类不能理解为本地客户端之间的 pSp 中继链路。
* `all-egress`：supernode 网络发送函数 `sendto` 每次返回的正字节数之和，包括控制消息、TCP 分帧开销和只发出一部分的 TCP 发送。发送错误单独计数。按收发方统计的合计只算完整发出的数据包。
* 字节数指操作系统接收下来的 n3n 数据报或数据流字节，不含 IP/UDP/TCP/链路层头部和内核重传。发送成功不代表对方一定收到。总出站量与按收发方合计之间的差额只是近似值，因为几页统计数据的读取时刻略有先后；这部分差额包含控制消息和未发完的发送。
* 对端之间 IPv4/IPv6 直连的流量不经过服务端，因此不在统计之内。出现一条记录只说明在所选时段内用过中继，不代表两人一直是中继连接。这些数据无法还原安装之前的流量，也看不到游戏数据的内容。

每个 supernode 进程最多跟踪 8192 对收发方；超出部分的数据包不单独跟踪，但会计入 `flow-overflow`。已在跟踪的收发方继续正常计数。不会为每个数据包读写文件。计数器的检查点和每小时的增量在同一个 SQLite 事务中提交，所以重启采集器不会重复计数。识别 supernode 进程时，会综合主机的 boot ID、PID 和 supernode 启动时间。

supernode 崩溃或重启时，还没来得及采集的流量会丢失（通常不超过一个采样间隔；如果采集器当时也停着，会丢得更多）。只要 supernode 本身一直在运行，单纯的采集器停机不会丢失累计字节。采集器恢复后补上的增量，全部算在恢复时所在的那个小时，不会再分摊回中间缺失的各小时。报告会显示最近一次采样的时间、发现的重启，以及超过 180 秒的采样空档。第一次采样会把当前进程启动以来的全部流量计入。查询的时间段按 UTC 向下取整到整点；当前这个小时的数据是不完整的。

## 安装

先构建并部署配套的修改版 supernode，同时保留与之对应的源码包和上一版二进制的备份。原生补丁只新增了一个只读的管理方法，不改变通信协议、路由、节点配置或客户端行为。更新二进制需要重启一次 supernode。独立的采集器之后可以单独重启，无需重启 n3n。

```sh
sudo install -d -m 0755 /opt/mikun2n/traffic
sudo install -m 0755 supernode/relay-traffic.py /opt/mikun2n/traffic/relay-traffic.py
sudo install -m 0644 supernode/mikun2n-traffic.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mikun2n-traffic.service
```

默认套接字是 `/run/n3n/mikun2n-supernode/mgmt`。统计其他实例时，用 systemd 覆盖配置在 `ExecStart` 里加上 `--socket PATH --db PATH`；绝不要让不同的 supernode 或同时运行的多个采集器共用一个数据库。第二个实例（例如联邦测试服务）需要自己的部署与数据库。

采集器只能访问 Unix 套接字，数据库存放在 `/var/lib/mikun2n-traffic/traffic.sqlite3`（目录权限 0700，umask 0077）。无需额外的防火墙端口或 supernode 文件系统权限。过期数据清理后，SQLite 会复用腾出来的空间，但文件大小不会缩小，保持在历史最大值。要回滚，停掉采集器并换回备份的服务端二进制即可；数据库可以留着以后分析。

## 对照云计费时间窗

采集器还会把默认路由网卡的收发（RX/TX）增量记录到 `host_hourly` 表中。这些计数来自网卡本身，包含所有进程的流量以及内核层的头部和重传。可用 `--interface NAME` 指定别的物理网卡。第一次采样只用来建立基线，本次开机以来已有的流量不会被错算到安装那个小时里。boot ID 或网卡编号变化时，会重新建立基线。即使 n3n 没有运行，主机流量的采样也照常进行。

```sh
sudo python3 /opt/mikun2n/traffic/relay-traffic.py report --since 2026-09-17T00:00:00+08:00 --until 2026-09-18T00:00:00+08:00 --hourly
```

时间段包含起点、不含终点。手动指定的起止时间必须是带时区的 ISO 格式，并且是整点。中继统计和主机统计使用同一个时间段；报告会分别显示主机统计覆盖的时间和中继统计开始的时间，主机出站量（OUT）同时用十进制 GB 和二进制单位列出。每次采样的增量算在采集时所在的小时（边界上最多有一个 60 秒采样间隔的误差）。停机恢复后补上的累计字节，算在恢复时的那个小时。网卡计数可能包含服务商不计费的流量（例如内网流量），不能代替服务商的计费数据。更早缺失的采样，没法据此还原整天的账单。只升级采集器不需要重启 supernode。
