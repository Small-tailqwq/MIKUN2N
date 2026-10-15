# Agent Notes

> [English](README.md) | [简体中文](README.zh.md)

Agent Notes 保存代码、测试和现有文档尚未解释的决策理由。修改时参阅[技能](../skills/agent-notes/SKILL.md)；格式参数见 [`agent-notes.config.json`](agent-notes.config.json)。导入的工具和指导遵循 kit 的 [MIT 许可证](../agent-notes-LICENSE.txt)。

<a id="layout-and-naming"></a>
## 布局与命名

路径为 `{lifecycle}/{class}/yyyy-mm-dd-topic.md`。移动时保留首次提出的日期。使用相对链接，移动或删除时修复入站链接。

- `proposed/`：尚未交付的实质性工作。
- `implemented/`：已交付的决策；保持直接受影响的路径、符号和默认值准确。
- `rejected/`：已否决的提案，理由仍能防止可信错误时保留。
- `archived/`：已完成决策的冻结历史。

目录树就是清单，检查器不接受中央 `INDEX.md`。

<a id="classification"></a>
## 分类

使用配置中的一个类别：`feature`（能力）、`bug-fix`（缺陷）、`simplification`（移除）、`architecture`（交付结构）、`process`（工具与流程）、`testing`（测试策略）。

<a id="when-to-write-one"></a>
## 何时记录

只有代码、测试和现有文档缺少某项持久选择的理由时，才新增或更新笔记。普通修复、机械精简、局部界面调整和工作报告无需笔记。先搜索已有所有者，更新原笔记而不是复制决策。

保持事实准确，但不要把原决策改写成相反决定；反转时新增笔记并互相链接。完全被取代的笔记可合并：先将独有理由、备选方案、后果和兼容约束保留到当前所有者，并修复入站链接。部分取代保留两条笔记并互链。不要把无关改动扩展成笔记审计。

<a id="retiring-notes-delete-archive-or-keep"></a>
## 退役：删除、归档或保留

保留仍指导行为、所有权、兼容性或有实质意义的否决方案的笔记。琐碎或过时内容的有用事实已有其他所有者时可删除，并修复入站链接。否决提案移入 `rejected/` 并写明原因。已经完成且不再指导当前工作的 implemented 笔记，可因历史价值归档。

<a id="the-frozen-archive"></a>
### 冻结归档

归档时，在每种语言的 `Status: implemented` 下一行加 `Archived: YYYY-MM-DD`，刷新 sidecar，整组移入 `archived/<class>/`，修复活跃入站链接，再运行 `python tools/agent_notes.py seal`。移动时不改正文。

封存归档保持冻结，不作为当前行为的依据。[`archived/manifest.json`](archived/manifest.json) 记录 SHA-256；`check` 按 Git 基线核对。见 [archived/AGENTS.md](archived/AGENTS.md)。

<a id="the-file-format"></a>
## 文件格式

每条笔记以下列四行开头：

```markdown
# Agent Note: <title>

Status: <status>

```

两种语言均保留英文 `# Agent Note: ` 和 `Status:`。状态与生命周期一致：`proposed`、`implemented` 或 `rejected — <单行原因>`。语言链接放在头部之后。

<a id="the-body-skeleton"></a>
### 正文骨架

| 生命周期 | 必需章节，按顺序 |
|---|---|
| proposed | `Problem`、`Proposal`、`Alternatives considered`、`Acceptance criteria`、`Risks` |
| implemented | `Problem`、`Decision`、`Alternatives considered`、`Consequences` |
| rejected | `Problem`、`Proposal`，并记录备选方案 |

使用 `##` 标题，技术专节可穿插其中。implemented 用现在时描述已交付行为；检查器拒绝 `Plan` 或 `Acceptance criteria` 等提案期标题。

<a id="alternatives-considered-is-mandatory"></a>
### 备选方案必填

记录真正考虑过的选项及落选原因，不编造备选方案。早于 `formatAdopted: 2026-10-11` 且无法还原备选方案的笔记，可使用检查器接受的注释：

```markdown
<!-- agent-note-format: alternatives-not-recorded (pre-format Agent Note) -->
```

<a id="moving-between-lifecycles"></a>
### 生命周期迁移

提案交付时，将 `Proposal` 改成 `Decision`，把计划和风险合入后果，更新状态后移入 `implemented/`。否决时在 `Status:` 写明原因，保留提案正文。

<a id="language-and-counterparts"></a>
## 语言与对照文件

MikuN2N 使用英文 `foo.md`、中文 `foo.zh.md` 和一致性记录 `foo.i18n.json`。两种语言地位相同，状态与标题结构一致。中文章节为：问题／提案／决策／曾考虑的替代方案／验收标准／风险／后果。

修改两边并确认含义一致后，对改过的组运行 `python tools/agent_notes.py pair --write <相对notes的英文主文路径>`。两份契约 README 同步维护，不使用 sidecar。不重新翻译未改笔记或归档。

笔记改动后运行一次 `python tools/agent_notes.py check`（Python 3.9+ 和 Git）及 `git diff --check`。可选 `lint` 只提供文风建议，不要求逐条裁决报告，也不是发布前提。
