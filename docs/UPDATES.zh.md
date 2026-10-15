# 打包与更新

> [English](UPDATES.md) | [简体中文](UPDATES.zh.md)

## 选择包

| Windows x64 包 | 运行要求 |
|---|---|
| `MikuN2N-<版本>-win-x64.zip` | 自包含：内含 .NET 运行时。 |
| `MikuN2N-<版本>-win-x64-framework-dependent.zip` | 需要 [.NET 9 桌面运行时 x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)。 |

两版都带有单文件 `MikuN2N.exe`、构建标识、修改版 edge、对应原生源码、TAP 安装器、许可证、声明和运行时文档，解压后请保留完整目录。程序集元数据 `UpdateRuntime` 标识运行时类型，缺失或未知时按 `self-contained` 处理；更新保持当前类型。显式传 `--self-contained false` 时默认选择框架依赖元数据，除非另行覆盖；发布脚本会分别显式传入 `-p:UpdateRuntime=...`。

## 构建与分发

在 Windows 上准备 PowerShell 7、Python 3.9+ 和 .NET 9 SDK：

```powershell
./tools/package-release.ps1 -Output dist -Notes '更新说明'
```

版本来自 `MikuN2N.csproj` 的 `BaseVersion` 和 `BuildNumber`。脚本将两版发布到各自的干净目录，逐字节核对源码归档与 `native/n3n-3.4.4/`，检查包内容白名单与组件字节，排除个人构建路径，然后生成：

- 上表中的两个 ZIP。
- `SHA256SUMS`，每个 ZIP 对应一行 SHA-256。
- `update.json`，包含下文的静态更新信息。

体积与各文件压缩后大小另存于本地 `artifacts/<版本>/package-size-report.json`，不放进发布 ZIP。Release 构建保留简体中文卫星资源及默认英文回退，不分发外部 PDB，ZIP 使用 `SmallestSize`。不启用 WPF 裁剪或 AOT。单文件内部程序集压缩保持关闭：实测虽然 EXE 变小，最终 ZIP 却略大，还会增加启动时的解压工作。参见 Microsoft 的[单文件压缩说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#compress-assemblies-in-single-file-apps)。

[Actions 工作流](../.github/workflows/release.yml)在推送版本 tag 时调用相同脚本，构建前检查 tag 与项目版本一致，上传上述四个文件，并创建草稿 Release。检查草稿后正式发布，自动更新才能发现它。手动运行工作流只上传工作流产物。本地构建不会创建 tag 或发布 Release。

两版都保留 `LICENSE.txt`、全部 `Runtime/LICENSE-*.txt` 与声明、`Runtime/n3n-3.4.4-source.zip` 内的原生修改声明，以及现有 TAP 源码提供承诺；分发二进制时应一同保留。公开发布还通过对应 tag 的源码树提供 MikuN2N 本体的对应源码。参见[第三方声明](../Runtime/THIRD-PARTY-NOTICES.txt)与[原生构建说明](../Runtime/BUILD.zh.md)。

## 静态发现与校验

客户端首先请求 `https://github.com/<owner>/<repo>/releases/latest/download/update.json`，通过 GitHub 发布下载端点获取信息，无需 API 令牌。可用的清单包含：

| 字段 | 内容 |
|---|---|
| `schema` | 整数 `1`。 |
| `version` | 与项目版本及 Release tag 一致，例如 `0.5.8-11`。 |
| `notes` | 更新说明字符串，可为空。 |
| `pageUrl` | 同一仓库、对应 tag 的 HTTPS GitHub Release 页面。 |
| `packages` | `self-contained` 与 `framework-dependent` 两项；每项含 `runtime`、规范 ZIP 文件名 `name`、版本化官方 GitHub 下载地址 `url`、字节数 `size` 与十六进制 `sha256`。 |

包地址使用 `https://github.com/<owner>/<repo>/releases/download/<version>/<name>`，不使用指向最新包的可变地址。客户端选取当前运行时类型；清单缺失、不可达或不可用时，回退到 GitHub 最新 Release REST API，仍精确匹配相同文件名，并使用 GitHub 的 SHA-256 文件摘要；没有摘要时使用 `SHA256SUMS`。

完整有效的清单可以直接判定没有更新，无需再查询 API。客户端遵守各来源的限流时间；手动检查也不会越过 GitHub 要求的重试时间。请求使用正常 TLS 验证，提供安装前还会核对包哈希、大小及解压后程序的版本。安装器会尝试替换与回滚，但进程突然中断时不保证能够自动恢复。

SHA-256 用来确认下载内容与取得的发布元数据一致，不是独立的发布者签名：能同时替换 ZIP 和清单的人也能替换哈希。更新流程信任该仓库的 GitHub Release 渠道与 HTTPS。

项目的 `UpdateRepository` 控制仓库。fork 直接构建时可传 `-p:UpdateRepository=owner/name`；用发布脚本打包前应修改项目属性，使清单与程序一致。直接构建传 `-p:UpdateRepository=none` 会禁用更新检查，这种配置不适用于公开打包脚本。

## 与 0.5.8-10 的兼容

已发出的 `0.5.8-10` 客户端只查询 GitHub API。新的静态清单无法消除旧客户端的 API 限流，也无法使它发现尚未正式发布的版本。为保留升级入口，需要继续提供准确命名的自包含包 `MikuN2N-<版本>-win-x64.zip`、与 EXE 版本相同的正式 Release tag，以及文件摘要或 `SHA256SUMS`。框架依赖包的后缀有意避开旧客户端的 ZIP 匹配规则。

旧客户端取得并安装新版后，后续检查才能使用静态发现。这些兼容措施保留了发现与包校验能力，不代表运行中的旧版 EXE 在所有 Windows 环境下都能原地安装成功。API 不可用或替换失败时，可从 Release 页面手动下载所需包，关闭程序后再替换文件。
