---
name: mikun2n-edge-patch
description: 修改 MikuN2N 的 n3n edge/supernode 补丁、重建 Runtime/n3n-edge.exe 或调试打洞失败时使用。包含构建、对应验证和分发源码同步步骤。
---

# MikuN2N edge 补丁

源码在 `native/n3n-3.4.4/`，分发二进制在 `Runtime/n3n-edge.exe`。原生改动以 Git diff 审阅；工具链由 `N3N_TOOLCHAIN_BIN`、同目录被忽略的 `SKILL.local.md` 或 `%USERPROFILE%\mingw64\mingw64\bin` 定位。缺少工具链时报告无法构建的限制。按本次任务的授权范围操作，不自动提交、打 tag 或发布。

## 构建与验证

构建前运行 `tools/sync-native-version.ps1` 同步标识，再在原生目录执行：

```powershell
$ToolchainBin = if ($env:N3N_TOOLCHAIN_BIN) { $env:N3N_TOOLCHAIN_BIN } else { "$env:USERPROFILE\mingw64\mingw64\bin" }
$env:Path = "$ToolchainBin;$env:Path"
cd native/n3n-3.4.4
./scripts/hack_fakeautoconf.sh
make -j4
```

Windows `config.mak` 保留 `-std=gnu17`，否则旧 `src/win32/getopt.c` 无法用 GCC 的 C23 默认模式编译。

按实际改动运行对应测试：IPv6 尺寸发现、分片或重组用 `tools/tests-ipv6.exe`；wire 编解码用 `tests-wire`。连接行为在已授权的相关双端场景验证。检查通过且没有新问题时停止；不能运行的部分如实报告，不重复跑无关测试。

## 交付原生改动

需要交付二进制时，将构建产物复制到 `Runtime/n3n-edge.exe`，并同步：

1. `Runtime/changes/<版本>.md` 与 `.zh.md`：问题、改动、兼容影响和实际验证；同一交付更新同一版本日志。
2. `native/n3n-3.4.4/MIKUN2N-MODIFICATIONS.md`：版本与日期。
3. 源码归档：`python tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report artifacts/<版本>/native-source-validation.json`。
4. `Runtime/THIRD-PARTY-NOTICES.txt` 中归档的 SHA-256。修改声明后再生成归档，使它与发布二进制同源。

公开发布步骤见 `Runtime/BUILD.md`。版本日志由 `Runtime/changes/` 唯一维护，其他指引只链接；历史参数以最新契约和源码核实。

## natpunch 参考实现

`tools/natpunch/` 是打洞算法参考实现。只在修改它时构建和测试：

```powershell
$env:Path = "$ToolchainBin;$env:Path"
cd tools/natpunch
gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.3.1.exe natpunch.c -lws2_32
.\natpunch-v7.3.1.exe --self-test
```

升级参考版本时，按 `tools/natpunch/README.md` 保留旧版 `legacy/`。技术入口：`Runtime/changes/README.md`、`Runtime/BUILD.md`、`tools/natpunch/README.md`、`docs/IPV6-TRANSPORT.md`、`docs/FEDERATION.md`。逐轮实测日志按需查证。
