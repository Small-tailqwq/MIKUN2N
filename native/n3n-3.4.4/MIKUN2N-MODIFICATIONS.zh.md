# n3n 3.4.4 的修改版

> [English](MIKUN2N-MODIFICATIONS.md) | [简体中文](MIKUN2N-MODIFICATIONS.zh.md)
>
> 本文是英文声明的中文翻译；GPLv3 第 5(a) 条所要求的修改声明以英文原文为准。

本源码树是 n3n 3.4.4（https://github.com/n42n/n3n，tag `3.4.4`）的修改版。修改由 MikuN2N 项目在 2026 年 7 月至 9 月间完成；最近一次修改属于 MikuN2N 0.5.8-5 版本（2026-09-27）。它是该版本随附的 `n3n-edge.exe` 的对应源码，并按与 n3n 相同的许可证 GPL-3.0-only 分发（见 `LICENSES/preferred/GPL-3.0`）。上游的版权声明均原样保留。

相对上游改动的部分：

- Windows 上针对对称型 NAT 的打洞：借助配套的观测服务判断 NAT 行为、多轮端口预测、worker 套接字组，以及由 supernode 协调的打洞时间表（`src/edge_utils.c`、`src/sn_utils.c`、`src/peer_info.[ch]`、`src/wire.c`、`include/n2n_typedefs.h`）。
- 管理接口新增的方法和字段，例如 `get_nat`、`set_peer_relay` 以及 `get_edges` 的附加字段（`src/management.c`）。
- Windows TAP 网卡在 MAC 设置、接口跃点数和媒体状态方面的修复（`src/win32/wintap.c`）。
- 实验性的 IPv6 传输（含尺寸发现和有上限的隧道内分片，`src/mikun2n_ipv6.*`、`src/mikun2n_ipv6_frag.h`）、中继限速（`src/mikun2n_relay*.h`）、配置选项（`src/conffile_defs.c`）以及构建标识（`src/mikun2n_build_version.h`）。
- 构建和测试工具（`Makefile`、`tools/Makefile`、`tools/tests-*.c`、`scripts/build-mikun2n-windows.sh`）。
- README、本声明以及 `doc/` 下部分文档的简体中文译文（`*.zh.md`，2026-09-28 新增）。英文原文未作改动，内容以英文原文为准。

文件名以 `mikun2n_` 开头的都是新增文件。各版本的改动记录见 MikuN2N 发布包中的 `Runtime/README.txt`；要查看确切差异，请把本源码树与上游 `3.4.4` tag 进行比较。
