# AGENTS.md

MikuN2N 是 Windows WPF（.NET 9、C#）客户端，管理本地修改版 n3n edge 子进程、节点、UDP 好友发现与托盘界面。`native/n3n-3.4.4/` 是原生源码，`Runtime/` 是分发内容，`supernode/` 是自建服务端部署工具。

## 工作范围

- 以用户本次要求为准；已有授权覆盖的构建、运行与验证不重复询问。连接真实节点、修改机器网络配置或部署服务器，应在用户授权的范围内进行。
- 提交、版本号、tag、push、打包和发布按本次交付范围执行，不因完成一次编辑自动触发。
- 先读将修改的代码及其直接契约；历史实测日志只在查证现场数据时读。不要为例行清理新增长篇笔记或复制代码地图。

## 最低验证与停止条件

需要 Windows 与 .NET 9 SDK。按改动选择一次最小有效检查：

| 改动 | 检查 |
|---|---|
| C# 或 XAML | `dotnet build` |
| 诊断会话、上传授权、日志轮转 | `dotnet run --project tools/offline-tests -- <空临时目录>`（会写出 64 MiB 分段） |
| 原生协议 | 用下述 edge skill 的构建与对应协议测试 |
| 连接、进程生命周期、TAP、UDP 发现 | 授权范围内的相关实机场景 |
| 仅文档 | 检查 diff、链接和 `git diff --check` |

界面改动只查看受影响画面和交互；涉及主题配色时各看一次浅色与深色。沿用 `DynamicResource` 调色板、现有控件样式及窗口 `SourceInitialized` 中的 `ThemeManager.ApplyWindow(this)`。

检查通过且没有新故障或未决疑点时停止扩展验证。缺少工具、交互环境或外部服务时，报告具体未验证项及限制，不把编译结果当作运行结果。

## 实现约定

- 面向用户的文案用简体中文，简洁、非技术化；代码、注释、提交信息与对外文档用英文。注释只解释不明显的原因。
- 遵循 `.gitattributes` 行尾规则。WPF/WinForms 同名类型沿用显式 `using` 别名。
- 节点地址、小组名称、密钥与个人机器路径只放在被忽略的 `*.local.md`，不提交到代码、默认值或测试数据。
- `EdgeController` 的启动/停止/重启由 `_wantConnected`、`_sessionId`、`_stateGate`、`_lifecycleGate` 协调；修改生命周期前先读对应路径。密钥仅经 `N2N_KEY`/`N3N_KEY` 环境变量传给子进程。
- `Runtime/n3n-edge.exe` 缺失时的用户自备 `edge.exe` 回退、旧管理协议及持久设置兼容不能只凭搜索无引用删除；旧 `edge.exe` 不提交或打包。
- n3n Windows 管理接口使用 IPv6 回环 `http://[::1]:{port}/v1`。显示版本经 `Services/BuildIdentity.cs` 读取，版本源为 csproj 的 `BaseVersion` 与 `BuildNumber`。

## 原生与分发

修改原生补丁、重建 edge 或调试打洞时，读 [mikun2n-edge-patch](.agents/skills/mikun2n-edge-patch/SKILL.md)。改动记录由 `Runtime/changes/` 分版本双语日志维护；当前行为以源码和最新直接契约为准。

日常修改、构建和测试不改正式版本。只有用户验收并明确决定发布后，才统一版本和 tag；不把测试包交付视为发布批准。测试产物用提交、时间或测试标识区分，使用独立目录，不覆盖既有产物。升级测试需要更高版本时，只在临时构建参数或夹具中设置。`tools/package-release.ps1` 默认产出带测试标识的包，正式包须显式传入已批准的 `-ExpectedVersion`。保留许可证、第三方声明、校验文件及对应源码，细节见 [docs/UPDATES.md](docs/UPDATES.md)。原生变更仍同步修改声明、源码归档及 SHA-256。

## 文档与决策记录

对外文档通常为英文 `.md` 与中文 `.zh.md`，互留语言链接。代理指导、原始实测日志、法律文本及上游原文保持现有语言；不要为本次清理批量翻译或改写。上游译文变化也会影响源码归档。

只有代码、测试、现有文档无法解释且未来仍需使用的决策理由才记入 `.agents/notes/`；先找已有所有者，按 [agent-notes](.agents/skills/agent-notes/SKILL.md) 更新。普通修复、机械精简及本地验证写在交付说明即可。归档冻结，历史记录不作为当前行为依据。

按需入口：自建服务端见 `supernode/README.md`；IPv6 契约见 `docs/IPV6-TRANSPORT.md`；诊断与流量部署见 `supernode/DIAGNOSTICS.md`、`supernode/TRAFFIC.md`；打洞参考实现见 `tools/natpunch/README.md`。
