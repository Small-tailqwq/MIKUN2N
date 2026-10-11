# Agent Notes

> [English](README.md) | [简体中文](README.zh.md)

**Agent Note** 记录影响本仓库的决策或提案，保存代码和其他文档无法承载的理由与取舍。它主要由编码代理编写和维护。本文件规定存放位置、记录时机、退役方式和[文件格式](#the-file-format)。项目参数保存在 [`agent-notes.config.json`](agent-notes.config.json)。在仓库根目录用 Python 3.9+ 和 Git 运行 `python tools/agent_notes.py check`，检查结构、格式、双语记录及归档封存；理由、翻译质量和链接仍需审阅。

MikuN2N 使用英文主文和中文对照、下列六个类别，以及 `formatAdopted: 2026-10-11`。契约 README 保持双语，代理指令与技能文档保持现有语言，不另建翻译版。现有发布脚本与工作流运行门禁，PR 以目标提交为归档基线。现有技术契约、分析和原始测试日志保留在原有路径。范围、接入方式和固定的上游版本见[采纳笔记](implemented/process/2026-10-11-adopt-agent-notes.zh.md)。导入的门禁和指引遵循 kit 的 [MIT 许可证](../agent-notes-LICENSE.txt)。

<a id="layout-and-naming"></a>
## 布局与命名

每条笔记有两个由路径表达的维度：`{lifecycle}/{class}/yyyy-mm-dd-topic-title.md`。

- 生命周期由顶层目录表达，状态改变时移动笔记：
  - `proposed/`：实施前审阅，尚未实现或只实现了一部分。
  - `implemented/`：决策已经落地。记录选择及被否决的方案；代码移动文件、重命名模块或修改键名、默认值时，同一次改动更新笔记中的事实，不改写决策本身。见 [implemented/AGENTS.md](implemented/AGENTS.md)。
  - `rejected/`：已经考虑并否决。只有理由仍能防止有吸引力且有实质影响的错误时才保留，否则删除。
- 类别由下一层目录表达，见[分类](#classification)。

文件名日期取主题首次提出的日期，以 Git 历史为依据，移动时不变。笔记之间使用相对 Markdown 链接，不用裸文本或编号代替引用；移动笔记时修复这些链接。

生命周期目录树就是清单，可以浏览目录或搜索仓库。不要创建中央 `INDEX.md`：它重复路径元数据，会让无关改动在同一个文件上冲突。门禁拒绝此文件。

<a id="classification"></a>
## 分类

每条笔记恰好属于配置 `classes` 中的一个类别，门禁拒绝其他目录。增加类别时，同一次改动更新配置、本表和 `archived/<class>/` 目录，并用 `process` 笔记记录原因。

| 类别 | 范围 |
|---|---|
| `feature` | 新增面向用户或模型的能力。 |
| `bug-fix` | 修复缺陷或事件暴露的缺口。 |
| `simplification` | 移除代码、行为或接口范围，不新增能力。 |
| `architecture` | 已交付源码的结构决策：模块关系、运行时术语。 |
| `process` | 围绕代码的工具、规则与流程：门禁、CI、包管理、代理工作流。 |
| `testing` | 测试基础设施与策略。 |

`architecture` 描述交付的源码，`process` 描述外围工具和流程。不设 `refactor` 类别：它与 `simplification` 重叠，后者对可观察行为是否变化的判断已能覆盖。

<a id="when-to-write-one"></a>
## 何时记录

只有改动包含代码、测试和现有文档尚未解释的持久决策理由时，才在同一次改动新增或更新笔记。尚未实施的大型提案放在 `proposed/`，已经作出的决策放在 `implemented/`。

- 机械或局部改动豁免，包括局部界面显示与交互调整、原因和修复在 diff 中已经清楚的普通缺陷，以及没有策略选择的依赖升级。
- 每项决策只有一个所有者；更新已有笔记即可，不创建副本。
- 不得把笔记改写成另一项决策。用新笔记取代并互相链接；只有符合下述合并规则时才可删除旧笔记。
- 写新笔记时立即检查取代关系：按决策、机制和被否决的方案搜索活跃笔记，在同一次改动中按 [agent-notes 技能](../skills/agent-notes/SKILL.md) 对匹配项分类。

完全被取代的 implemented 笔记可合并到当前决策所有者后删除。删除前，新所有者必须保留所有独有理由、备选方案、后果、必要验证和明确的覆盖缺口，并修复所有入站链接。部分取代不满足条件：两条笔记都保持活跃、互相链接，并更新仍有效的事实。合并不能把旧笔记改成相反决策，也不能让 Git 历史成为理由的唯一载体。

新增功能的笔记只有在功能已从生产代码、配置、schema、持久化或线格式、迁移与兼容处理全部移除，当前文档不再将其作为可用能力，且测试不再将其作为受支持行为时，才可合并到移除笔记。移除笔记须保留原始动机、动机为何不再足以保留功能、完全移除的备选方案、放弃的能力、重新引入条件及确认彻底移除的方法。只移除一种传输、默认值、实现或展示方式属于部分取代。

<a id="retiring-notes-delete-archive-or-keep"></a>
## 退役：删除、归档或保留

以未来决策价值判断，不能按字数、年龄或配额处理。[agent-notes 技能](../skills/agent-notes/SKILL.md) 提供流程和校准样例。

- implemented，删除：只描述小界面调整或纯机械变化。即便 diff 很小，若仍承载命名、兼容、所有权或行为规则，就不满足此条件。
- implemented，保持活跃：备选方案、所有权边界、明确不提供的保证、持久化或线协议语义、安全规则、重新引入条件仍可指导未来改动。
- implemented，归档：决策已完成，不太可能再指导后续工作，但理由仍有历史价值。
- proposed：不归档，过时提案带真实原因移入 `rejected/`。
- rejected：只有仍能防止可信错误时才保留，否则删除。

每次删除都在同一次改动中修复或移除入站链接。

<a id="the-frozen-archive"></a>
### 冻结归档

归档路径为 `archived/{class}/yyyy-mm-dd-topic-title.md`，只有 implemented 笔记可进入，因此不再带 `implemented` 层。归档时连同译文和 sidecar 一起移动，保留 `Status: implemented`，在每种语言的下一行插入 `Archived: YYYY-MM-DD`，重新记录一次 sidecar，并修复或删除入站链接。笔记其他内容不变。

封存后永久冻结：不编辑、翻译、格式化、更新、移动或删除，也不把归档作为当前行为的权威。`python tools/agent_notes.py seal` 向 [`archived/manifest.json`](archived/manifest.json) 追加每个文件的 SHA-256；`check` 拒绝已改变、缺失、未封存的归档文件，以及相对基线删除的封存记录。其他文档检查跳过归档及其出站链接；活跃文档可在明确引用历史时链接到归档。见 [archived/AGENTS.md](archived/AGENTS.md)。

<a id="the-file-format"></a>
## 文件格式

活跃笔记遵循统一格式，由 `python tools/agent_notes.py check` 检查。归档保留封存时的格式和归档日期行。

<a id="the-header-block"></a>
### 头部

每条笔记前四行固定为：

```markdown
# Agent Note: <title>

Status: <status>

```

`# Agent Note: ` 和 `Status:` 是机器标记，所有语言都保留英文。状态必须与生命周期目录一致：

- `Status: proposed`
- `Status: implemented`
- `Status: rejected — <单行否决原因>`

状态不带日期或括注：文件名记录首次提出日期，其他时间在 Git 中。只有否决状态附带内容，便于直接读取结论。代码围栏外只出现一次 `Status:`。

<a id="the-body-skeleton"></a>
### 正文骨架

英文主文的第一节为 `## Problem`，动机应能独立于方案成立。必需章节使用下列与配置一致的英文规范名；中文对照按后面的语言映射翻译。拓扑、线格式、schema 等专有技术章节可穿插其中。门禁忽略代码围栏内的标题。

`proposed/`：

```markdown
## Problem
## Proposal
…专有技术章节…
## Alternatives considered
## Acceptance criteria
## Risks
```

`## Proposal` 可用将来时，未实现的计划、迁移步骤和问题放在这里。`## Acceptance criteria` 写明什么可观察状态代表完成，`## Risks` 记录风险与主动放弃的能力。

`implemented/`：

```markdown
## Problem
## Decision
…专有技术章节…
## Alternatives considered
## Consequences
```

`## Decision` 用现在时描述实际落地状态。`## Consequences` 同时记录代价与收益。禁止提案期标题 `## Proposal`、`## Plan`、`## Migration plan`、`## Acceptance criteria` 及其带冒号后缀的变体。描述现有事实的 `## Testing`、`## Deferred`、`## Related` 可以保留。

`rejected/`：冻结提案正文，保留提案时期的章节，否决结论写在 `Status:` 行。只要求头部、以 `## Problem` 开头、包含 `## Proposal` 并遵守备选方案规则。

<a id="alternatives-considered-is-mandatory"></a>
### 备选方案必填

每条笔记都有 `## Alternatives considered`，每个真正考虑过的选项以加粗开头的独立段落说明为何落选；争议较大的选项可用 `### Why not <X>?` 子节。不记选择胜过了什么，会使决策被反复论证。

备选方案只能记录，不能编造。仅文件名日期早于配置 `formatAdopted`，且无法还原备选方案的旧笔记，可用下列原样注释代替此节：

```markdown
<!-- agent-note-format: alternatives-not-recorded (pre-format Agent Note) -->
```

<a id="moving-between-lifecycles"></a>
### 生命周期迁移

移动时在同一次改动更新 `Status:`，并满足目标目录骨架。`proposed/` → `implemented/` 将 `## Proposal` 改为现在时的 `## Decision`，把 `## Acceptance criteria` 和 `## Risks` 合入 `## Consequences` 或描述事实的 `## Testing`，以实际交付替换计划。`proposed/` → `rejected/` 只在状态行加原因并冻结正文。

<a id="language-and-counterparts"></a>
## 语言与对照文件

`primaryLanguage` 指定 `foo.md` 的语言，配置中的标题使用该语言。`counterpartLanguages` 为空时，每条笔记只有一个文件。

MikuN2N 配置为 `primaryLanguage: "en"` 和 `counterpartLanguages: ["zh"]`。每条活跃笔记由同目录的 `foo.md`、`foo.zh.md` 和 `foo.i18n.json` 组成。两种语言地位相同，可先写任意一种再翻译。译文保留英文机器标记，逐字重复主文的 `Status:` 行，并一一对应标题层级。英文主文使用上述规范标题；中文依次为：问题／提案／决策／曾考虑的替代方案／验收标准／风险／后果。语言跳转链接放在头部之后，保持前四行不变。

sidecar 记录两边每一节的哈希。改正文而不更新记录会使门禁报错，并指出变化章节。确认两边含义一致后，运行 `python tools/agent_notes.py pair --write implemented/process/2026-10-11-adopt-agent-notes.md`（替换为相对 `.agents/notes/` 的英文主文路径），再运行 `python tools/agent_notes.py check`。审阅 sidecar diff 就是确认两边含义一致的环节。两份契约 README 同样需要同步审阅，但不纳入笔记 sidecar 校验。

切换语言模式不重写归档，归档按组内实际包含的文件校验。
