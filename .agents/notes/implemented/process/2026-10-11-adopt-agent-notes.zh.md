# Agent Note: 在 MikuN2N 采纳双语 Agent Notes

Status: implemented

> [English](2026-10-11-adopt-agent-notes.md) | [简体中文](2026-10-11-adopt-agent-notes.zh.md)

## 问题

MikuN2N 已有共享代理指令、原生补丁技能、双语技术文档和历史测试报告。这些内容说明现有行为与调查结论，但对于理由尚未被这些来源记录的新决策，没有统一的生命周期和格式。反复参与项目的代理需要区分当前决策、提案和历史证据，同时避免为同一规则建立多个所有者。

## 决策

Agent Notes 基于 [agent-notes-kit](https://github.com/Small-tailqwq/agent-notes-kit) 的提交 `6484330d6435f329384a0e4da6e355e6d7883d05`，遵循其 [ADAPTING.md](https://github.com/Small-tailqwq/agent-notes-kit/blob/6484330d6435f329384a0e4da6e355e6d7883d05/ADAPTING.md)。[契约](../../README.zh.md)、[配置](../../agent-notes.config.json)和[共享技能](../../../skills/agent-notes/SKILL.md)规定格式与维护流程。技能的归档步骤做了本地化：笔记仍在 `implemented/` 下时先写入归档日期并运行 `pair --write`，因为 `pair` 只接受活跃路径。导入的门禁和指引保留上游 [MIT 许可证](../../../agent-notes-LICENSE.txt)。

- **门禁：**在仓库根目录运行 `python tools/agent_notes.py`，依赖 Python 3.9+ 和 Git。[Python 实现](../../../../tools/agent_notes.py)原样复制。`proseLint: true` 使 `check` 通过后列出改动笔记中的[写作检查](../../../skills/agent-notes/references/prose.md)候选，发布脚本和 CI 笔记任务的输出里都能看到；候选不影响退出码。门禁复用[原生源码打包](../../../../tools/package-native-source.py)已有的 Python 依赖。
- **语言：**`primaryLanguage: "en"`、`counterpartLanguages: ["zh"]`，保留上游英文规范标题。每条活跃笔记包含英文文件、中文 `.zh.md` 对照和 `.i18n.json` 记录。契约 README 遵循对外文档的双语约定。代理指令与技能文档保持现有语言，不另建翻译版。
- **分类：**沿用六个类别 `feature`、`bug-fix`、`simplification`、`architecture`、`process`、`testing`，覆盖传输、客户端和工具决策。`formatAdopted` 为 `2026-10-11`；现有文档保留路径，因此 `forbiddenRoots` 为空。
- **代理入口：**根 [AGENTS.md](../../../../AGENTS.md)链接契约与技能，[CLAUDE.md](../../../../CLAUDE.md)导入该文件。[Claude 技能适配文件](../../../../.claude/skills/agent-notes/SKILL.md)包含发现元数据和共享技能引用。它使用普通文件，以支持禁用符号链接的 Windows 检出。忽略规则放行 `.claude/` 下这一适配文件，并忽略本地设置。
- **检查：**[发布脚本](../../../../tools/package-release.ps1)在打包前运行门禁；[发布工作流](../../../../.github/workflows/release.yml)在 PR、版本 tag 推送及手动运行时执行检查。检查任务拉取完整 Git 历史，`AGENT_NOTES_BASE_REF` 使用 PR 目标 SHA，否则使用 `HEAD~1`。PR 只运行笔记检查，打包和草稿发布在 tag／手动事件中运行。
- **现有文档：**技术契约、分析、提案和原始测试日志保留在 `docs/` 下的原有路径。运行时历史按版本组织，理由见[分版本日志决策](2026-10-11-split-runtime-change-records.zh.md)。新笔记链接已有的决策说明。

## 曾考虑的替代方案

**使用 Node 门禁。**它增加了发布依赖，而只用标准库的 Python 门禁能完成相同的结构检查，仓库也已使用 Python。

**关闭 `proseLint`，手动运行 `lint`。**这里的笔记主要由容易写出防御性限定和含糊事实的模型撰写。手动步骤依赖作者记得执行；每次 `check` 都列出候选，作者和 CI 日志都能看到。

**单语笔记。**能减少翻译与 sidecar 维护成本，但会限制使用另一种语言的贡献者阅读。双语笔记延续仓库对外文档的约定。

**为每个代理复制技能，或要求符号链接。**副本会漂移，符号链接依赖 Windows 检出设置与权限。简短的普通文件适配入口让 Claude 能发现共享技能，流程只维护一份。

**把全部设计与分析文档改写成笔记。**这些文档混合了公开说明、测量数据、部署步骤和待办工作，也已经记录相关理由。整体迁移会扰动已有双语链接与历史证据。因此保留其所有权，笔记要求适用于这些来源尚未解释的持久理由。

## 后果

持久决策具有可搜索的生命周期路径、备选方案记录和封存历史。普通局部修复若已能从 diff 与现有文档理解，就无需笔记。

每次修改笔记都需要同步两份文本、审阅翻译，并在 `check` 前对受影响的英文路径运行 `pair --write`。两份契约 README 一起审阅，不带 sidecar。打包依赖 Python，CI 单独运行笔记检查任务。

从 kit 更新时，以固定提交为比较基线，保留项目本地化。改变类别、标题或语言模式时，在同一次改动更新配置和两份契约 README。
