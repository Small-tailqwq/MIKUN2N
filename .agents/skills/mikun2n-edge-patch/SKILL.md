---
name: mikun2n-edge-patch
description: 修改 MikuN2N 的 n3n edge/supernode 补丁、重建 Runtime/n3n-edge.exe 或调试打洞失败时使用。涵盖 patched 源码树与 MinGW 工具链位置、n3n 与 natpunch 两侧的重建步骤、补丁清单与 GPLv3 源码归档的同步义务，以及打洞算法参考实现（tools/natpunch/）与联邦文档的入口。
---

# MikuN2N 边缘补丁与打洞算法

`Runtime/n3n-edge.exe` 是本地修改版，不是上游原版二进制。这份补丁是项目的核心工程贡献，改动它等于改动产品的关键行为，因此每次改动都要同步补丁清单与源码归档。

## 环境位置

patched 源码树与工具链都在仓库外，路径因机器而异，不要写进受版本控制的文件。本机路径记在
同目录的 `SKILL.local.md`（已被 `.gitignore` 覆盖，不随仓库分发）；没有这份文件时先问用户
要路径，或按下面的约定自行推断。

| 变量 | 含义 | 默认推断 |
|---|---|---|
| `$PatchedSource` | n3n 3.4.4 patched 源码树 | 与 MikuN2N 仓库同级的 `n3n-build\n3n-3.4.4-patched` |
| `$ToolchainBin` | MinGW-w64 的 `bin` 目录（默认不在 PATH 上） | `%USERPROFILE%\mingw64\mingw64\bin` |

`n3n-build/` 是独立 git 仓库，不在 MikuN2N 仓库内，也不随仓库分发。它按版本提交并打同名 tag
（`0.5.8-1`、`0.5.8-2`）：看原生改动用 `git diff <上一版>..<这一版>`，不要再解压
`Runtime/n3n-3.4.4-source.zip` 逐文件比对。仓库里另有 `n3n-edge.exe.original-backup` 与
`backups/`（改动前的原始二进制与快照，已被 gitignore）。

- 打洞算法参考实现：`tools/natpunch/`（Windows UDP NAT4 打洞实验工具 v7.x，C 源码 + 协调服务器 + 日志分析），edge 侧的 bank 模型、cone 逃逸、fast/volatile 判定都从它移植或对齐
- 打包产物：`Runtime/n3n-edge.exe`、`Runtime/n3n-3.4.4-source.zip`、`Runtime/README.txt`

## 打补丁并重建 n3n-edge.exe

1. 在 patched 源码树里改代码，用 `git diff` 复核改动范围。
2. 前置工具链并构建（先按上表解析 `$PatchedSource` 与 `$ToolchainBin`）：

   ```powershell
   $PatchedSource = if ($env:N3N_PATCHED_SOURCE) { $env:N3N_PATCHED_SOURCE } else { "..\n3n-build\n3n-3.4.4-patched" }
   $ToolchainBin  = if ($env:N3N_TOOLCHAIN_BIN) { $env:N3N_TOOLCHAIN_BIN } else { "$env:USERPROFILE\mingw64\mingw64\bin" }
   $env:Path = "$ToolchainBin;$env:Path"
   cd $PatchedSource
   ./scripts/hack_fakeautoconf.sh
   make -j4
   ```

   Windows 目标需要在 `config.mak` 的 `CFLAGS` 里保留 `-std=gnu17`（当前为 `-g -O2 -std=gnu17`），否则随包的旧 `src/win32/getopt.c` 在当前 GCC 的 C23 默认标准下编译失败。
   构建完先跑离线回归：`tools/tests-ipv6.exe`（方向性尺寸、短 ACK 绑定、分片乱序/重复/过期/重叠、错误会话与端点拒绝、大探测丢失后保活仍在、EMSGSIZE 与瞬态失败）与 `tests-wire`（wire 编解码）。它们通过只说明协议层正确，不代替双端实机。
3. 把新二进制拷到 `Runtime/n3n-edge.exe`，并在 `Runtime/README.txt` 的编号补丁清单末尾追加一条：版本前缀 + 问题现象（含实测数据）+ 改动点 + 影响范围。清单要写"为什么"，不要逐行复述代码。
4. 重新打包源码：把 patched 源码树的源码（不含 `.o`、`.exe` 等构建产物）打成 `Runtime/n3n-3.4.4-source.zip`，替换旧文件。GPLv3 要求分发二进制时提供对应源码，归档必须与刚发布的二进制同源——打包后用字节数或哈希核对 `src/edge_utils.c`、`src/management.c` 等关键文件在归档与源码树中一致。
5. 客户端侧的配套改动（管理方法、界面文案、设置项）在 MikuN2N 仓库里完成，并实际运行程序验证连接行为。

## 重建 natpunch 参考客户端

参考实现改动独立于 edge 补丁，但结论通常要移植回 edge：

```powershell
$env:Path = "$ToolchainBin;$env:Path"
cd tools/natpunch
gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.3.1.exe natpunch.c -lws2_32
.\natpunch-v7.3.1.exe --self-test
```

升级版本号时把旧版本逐字节归档到 `tools/natpunch/legacy/<版本>/`，并更新 `tools/natpunch/README.md` 的策略说明。

## 不要在别处复制补丁清单

补丁清单的唯一权威来源是 `Runtime/README.txt`，它随发布包分发、按版本编号。`AGENTS.md` 只保留指针，不再维护副本——历史上那份副本长期落后于 README，并与它关于冷却策略的表述互相矛盾。

## 读什么

- 某项补丁改了什么、为什么：`Runtime/README.txt`
- 打洞算法的设计与实测策略：`tools/natpunch/README.md`
- 多 supernode 联邦（含测试节点、部署方式、已知偏差）：`docs/FEDERATION.md`
- 现场故障排查结论与历代方案：`docs/故障排查.md`、`docs/P2P打洞融合方案.md`、`docs/NATPUNCH-V7阶段总结.md`
- 某轮交付包含什么、验证到什么程度：`artifacts/<版本>/`（`*-validation.json`、`*-tests*.log`、`使用说明.txt`，不进版本控制）
- 逐轮实测数据：`docs/V7*.md`、`docs/第*测试.md`、`docs/打洞测试*.md`
