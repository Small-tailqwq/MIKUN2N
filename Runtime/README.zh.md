# MikuN2N 运行时组件

> [English](README.md) | [简体中文](README.zh.md)

本目录包含 MikuN2N 的运行时组件，其中 edge 基于 n3n 3.4.4 修改。构建和发布时，这些文件会复制到程序的 `Runtime/` 目录。

## 按需阅读

| 要了解什么 | 入口 |
|---|---|
| 某版本改了什么、为什么、验证到什么程度？ | [双语改动日志](changes/README.zh.md) |
| 如何重建原生二进制并打包对应源码？ | [构建与源码说明](BUILD.zh.md) |
| 许可证、源码提供承诺和二进制哈希是什么？ | [第三方声明](THIRD-PARTY-NOTICES.txt) |

## 随包组件

| 文件 | 用途 |
|---|---|
| `n3n-edge.exe` | 优先使用的 Windows 修改版 edge。 |
| `n3n-3.4.4-source.zip` | 对应的修改版源码，含 GPLv3 §5(a) 要求的修改声明 `MIKUN2N-MODIFICATIONS.md`。 |
| `LICENSE-n2n.txt` | n3n 的完整 GPLv3 许可证。 |
| `tap-windows-installer.exe` | 未修改、带官方签名的 OpenVPN TAP-Windows 9.24.7 安装器。 |
| `LICENSE-tap-windows.txt` | 从该安装器提取的许可证：GPLv2 与 WDK 系统库例外。 |
| `LICENSE-dotnet.txt`、`THIRD-PARTY-NOTICES-dotnet.txt` | 自包含包内嵌的 .NET 9／WPF／WinForms 运行时许可证与第三方声明，取自 Microsoft.NETCore.App.Runtime.win-x64 包。 |
| `README.txt`、`README*.md`、`BUILD*.md`、`changes/*.md` | 阅读入口、构建说明与双语版本日志。 |

已有配置好的网卡时，使用程序不必再运行 TAP 安装器；公开发布打包脚本会包含安装器及许可证。

自包含包和框架依赖包都完整保留本目录，包括原生源码归档与全部声明。框架依赖版需要另外安装 .NET 9 桌面运行时 x64；程序仍附带 Microsoft apphost，因此也保留 .NET 许可证文件。包名与更新兼容性见源码仓库的 [UPDATES.zh.md](https://github.com/Small-tailqwq/MikuN2N/blob/master/docs/UPDATES.zh.md)。

旧版 n2n `edge.exe` 无法提供可复现的对应源码，不得分发。用户自行放置的 `Runtime/edge.exe` 仍可在缺少 `n3n-edge.exe` 时作为本地回退。

分发 n3n 必须提供对应源码及 GPLv3 许可证；分发 TAP 安装器必须附带许可证，并履行[第三方声明](THIRD-PARTY-NOTICES.txt)中的源码提供承诺。该声明也列出了各组件的哈希。
