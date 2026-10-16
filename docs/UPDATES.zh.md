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
./tools/package-release.ps1
```

日常测试不修改 `BaseVersion` 和 `BuildNumber`。默认命令在 `artifacts/test-packages/<测试标识>/` 生成带独立标识的包，程序集信息版本为 `<基线>+test.<测试标识>`，另带 `SHA256SUMS` 和 `test-build.json`。默认标识包含 UTC 时间、提交、未提交状态与短随机后缀，也可显式传 `-TestId`。测试包关闭真实更新检查，不生成 `update.json` 或公开下载地址。已有产物会被拒绝覆盖；`-PlanOnly` 可只查看标识与路径，不构建。

只有用户验收并明确批准发布后，才统一商定的源码版本并运行：

```powershell
./tools/package-release.ps1 -ExpectedVersion '<已批准的版本>' -Output dist -Notes '更新说明'
```

这个显式发布模式会核对项目版本，将两版构建到新的运行目录，验证对应原生源码与许可证，然后生成：

- 上表中的两个 ZIP。
- `SHA256SUMS`，每个 ZIP 对应一行 SHA-256。
- `update.json`，包含下文的静态更新信息。

体积与源码核对记录存入 `artifacts/package-checks/<运行标识>/`，不放进 ZIP。Release 构建保留简体中文卫星资源与默认英文回退，不分发外部 PDB，ZIP 使用 `SmallestSize`。不启用 WPF 裁剪或 AOT；内部程序集压缩因实测 ZIP 略大而保持关闭。参见 Microsoft 的[单文件压缩说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#compress-assemblies-in-single-file-apps)。

[Actions 工作流](../.github/workflows/release.yml)仅在推送版本 tag 时选择发布模式，核对项目版本后将四个文件附到草稿 Release。审阅并正式发布后，自动更新才能发现它。手动运行工作流默认使用测试模式；可用 `source_commit` 固定源码提交，用 `release_version` 在核对项目版本后预览正式命名的包及 `update.json`。手动运行只将包和验证报告上传为工作流产物，不创建 tag 或 Release。构建仅有仓库只读权限，创建草稿 Release 的写权限仅授予独立的 tag 触发作业。本地构建不会创建 tag 或发布 Release。

Release 说明使用[中英双语模板](../.github/release-notes-template.md)，每个版本的中英文改动条目放在 `.github/release-notes/<版本>.json`，发布下一版时同步更新两份条目。只读构建作业生成 `dist/release-notes.md` 并上传为工作流产物；草稿作业使用该文件，只附四个分发资产。模板取自工作流提交，因此手动选择较旧源码时也能沿用当前格式。无需编译即可生成说明：

```powershell
python tools/render-release-notes.py --version '<已批准的版本>' --repository 'owner/repo' --source-ref '<确切源码提交>' --run-url '<成功的CI运行地址>' --output release-notes.md
```

手动预览通过后，直接复用其 ZIP、校验文件与清单，附到指向确切打包源码提交的草稿。仅修改说明不必重编译或替换文件；说明描述那次源码，后续文档提交不会改变既有二进制。以后启用签名时，同步更新模板中的未签名状态。

更新器比较版本时会忽略 `+test...` 元数据。需要测试更高版本时，仅对隔离夹具构建传入临时 `BuildNumber`，包也放在公开发布目录之外。原始 `-10` 源码回放夹具在自己的临时工程里使用明显虚构的夹具版本，不作为项目发行版本；`Test-LegacyUpdater.ps1 -VersionOnly` 只核对版本比较与夹具 EXE 的实际版本，不进行 HTTP、替换或 UI 操作。这两种方式都不修改正式版本或既有产物。

两版都保留 `LICENSE.txt`、全部 `Runtime/LICENSE-*.txt` 与声明、`Runtime/n3n-3.4.4-source.zip` 内的原生修改声明，以及现有 TAP 源码提供承诺；分发二进制时应一同保留。公开发布还通过对应 tag 的源码树提供 MikuN2N 本体的对应源码。参见[第三方声明](../Runtime/THIRD-PARTY-NOTICES.txt)与[原生构建说明](../Runtime/BUILD.zh.md)。

## 静态发现与校验

客户端首先请求 `https://github.com/<owner>/<repo>/releases/latest/download/update.json`，通过 GitHub 发布下载端点获取信息，无需 API 令牌。可用的清单包含：

| 字段 | 内容 |
|---|---|
| `schema` | 整数 `1`。 |
| `version` | 与项目版本及 Release tag 一致，例如 `0.5.8-10`。 |
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
