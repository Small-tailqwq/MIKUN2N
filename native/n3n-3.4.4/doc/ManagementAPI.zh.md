Copyright (C) 2023-24 Hamish Coleman
SPDX-License-Identifier: GPL-3.0-only

# 管理接口

> [English](ManagementAPI.md) | [简体中文](ManagementAPI.zh.md)

edge 和 supernode 都提供基于 JsonRPC 的管理接口。

一个快速上手的查询示例：
```
curl --unix-socket /run/n3n/edge/mgmt http://x/v1 -d '{"jsonrpc": "2.0", "method": "get_edges", "id": 1}' |jq
```
或者
```
n3nctl edges
```

（supernode 目前仍沿用旧的默认设置，监听 TCP/5645，但很快会改为默认使用 Unix 域套接字。）

除了主要的 JsonRPC 接口外，还有少量简单的 HTTP 页面。它们不用于传递复杂数据，主要供人直接查看，或与其他系统对接。

获取 HTTP 页面列表的示例：
```
curl --unix-socket /run/n3n/edge/mgmt http://x/help
```

## 监听的套接字

守护进程启动时，要么被指定一个会话名，要么使用默认值（edge 默认为"edge"，supernode 默认为"supernode"）。

会话名用来确定 Unix 域套接字的路径：

- `/run/n3n/$sessionname/mgmt`

这个目录不存在时会自动创建，属主和属组与守护进程运行时的用户和组相同。管理员可以根据需要调整权限和组成员。

退出时，守护进程会尝试删除自己的套接字和会话目录，因此可以借此简单地查看有哪些会话正在运行。

注意：由于 Windows 不支持 Unix 域套接字，在 Windows 上默认监听 TCP/5644（并在 %USERPROFILE%\n3n 中创建一个空的会话目录）。

> 译注：实际上 n3n 3.4.x 在 Windows 上的这个 TCP 监听只绑定 IPv6 回环地址（`::1`），所以 MikuN2N 客户端通过 `http://[::1]:{端口}/v1` 访问，用 `127.0.0.1` 是连不上的。

## 列出 HTTP 端点

请求 `/help` 即可得到 HTTP 端点列表。

例如：
```
curl --unix-socket /run/n3n/edge/mgmt http://x/help
```

## 列出 JsonRPC 方法

调用"help"方法，会返回所有已知方法的列表及简短说明。

例如：
```
curl --unix-socket /run/n3n/edge/mgmt http://x/v1 -d '{"jsonrpc": "2.0", "method": "help", "id": 1}' |jq
```
或者
```
n3nctl help
```

## 事件流

事件流位于"/events/$topic"这个 URL。请求该端点后，这条连接会切换为持续推送 JSON 数据包，格式遵循 RFC7464。

连接建立后，该主题上发布的所有事件都会转发给客户端。

每个事件主题同时只能有一个客户端订阅，新的订阅会取代旧的订阅。

特殊主题"debug"会收到所有已发布事件的副本。注意，这是专门用来调试事件的！

JsonRPC 方法"help.events"会返回所有事件主题的列表。

## 认证

有些 API 请求会对正在运行的守护进程做全局性的修改，可能影响 n3n 网络的可用性。对于这类请求，守护进程会检查请求中是否带有标准的 HTTP Authorization 头。

认证方式是由客户端提供一个简单的密码。密码默认为"n3n"，可以通过配置选项 `management.password` 修改。

## 分页

如果某次 API 调用的结果会超出内部缓冲区的大小，响应会返回 507 状态码和一个 JsonRPC 错误对象，表示发生了"溢出"。

返回溢出时，错误对象中可能会包含溢出发生前能放下的条目数。可以带上分页参数重新请求，以避免溢出。（注意，这也意味着在分页请求的过程中，内部数据可能发生变化。）

分页时，在 param 字典中加入 offset 和 limit 两个值。

n3nctl 工具的 JsonRPC.get() 方法实现了这一用法，可以作为示例参考。
