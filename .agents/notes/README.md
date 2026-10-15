# Agent Notes

> [English](README.md) | [简体中文](README.zh.md)

Agent Notes preserve decision rationale that code, tests, and current documentation do not explain. Use the [skill](../skills/agent-notes/SKILL.md) for edits; project format settings live in [`agent-notes.config.json`](agent-notes.config.json). The imported tooling and guidance use the kit's [MIT license](../agent-notes-LICENSE.txt).

## Layout and naming

Path: `{lifecycle}/{class}/yyyy-mm-dd-topic.md`. Keep the first-proposed date when moving a note. Use relative links and repair inbound links when moving or deleting it.

- `proposed/`: substantial work not yet shipped.
- `implemented/`: shipped decisions; keep directly affected paths, symbols, and defaults current.
- `rejected/`: declined proposals, kept while their rationale prevents a plausible mistake.
- `archived/`: completed implemented decisions retained as frozen history.

The directory tree is the inventory. The checker does not accept a central `INDEX.md`.

## Classification

Use one configured class: `feature` (capability), `bug-fix` (defect), `simplification` (removal), `architecture` (shipped structure), `process` (tooling and workflow), or `testing` (test strategy).

## When to write one

Write or update a note only for a lasting choice whose reason is missing from code, tests, and existing docs. Ordinary fixes, mechanical cleanup, local UI edits, and work reports do not need one. Search for an existing owner and update it instead of duplicating the decision.

Keep facts current, but do not rewrite a decision into its opposite. A reversal gets a new linked note. A fully superseded note may be consolidated after preserving its unique rationale, alternatives, consequences, and compatibility constraints in the current owner and repairing inbound links. Partial supersession keeps both notes linked. Do not expand an unrelated change into a note audit.

## Retiring notes: delete, archive, or keep

Keep notes that still guide behavior, ownership, compatibility, or a meaningful rejected alternative. Delete trivial or obsolete material whose useful facts are already owned elsewhere; repair inbound links. A declined proposal moves to `rejected/` with its reason. A completed implemented note may be archived when its history is useful but it no longer guides current work.

### The frozen archive

To archive, add `Archived: YYYY-MM-DD` immediately after `Status: implemented` in each language, refresh the sidecar, move the group to `archived/<class>/`, repair active inbound links, and run `python tools/agent_notes.py seal`. Leave the body unchanged during the move.

Sealed archives remain frozen and are not authority for current behavior. [`archived/manifest.json`](archived/manifest.json) records their SHA-256 hashes; `check` verifies them against the Git baseline. See [archived/AGENTS.md](archived/AGENTS.md).

## The file format

Each note starts with the following four lines:

```markdown
# Agent Note: <title>

Status: <status>

```

Keep `# Agent Note: ` and `Status:` in English in both languages. Status matches the lifecycle: `proposed`, `implemented`, or `rejected — <one-line reason>`. Language links follow this header.

### The body skeleton

| Lifecycle | Required headings, in order |
|---|---|
| proposed | `Problem`, `Proposal`, `Alternatives considered`, `Acceptance criteria`, `Risks` |
| implemented | `Problem`, `Decision`, `Alternatives considered`, `Consequences` |
| rejected | `Problem`, `Proposal`, with recorded alternatives |

Use `##` headings. Technical sections may appear between them. Implemented notes describe shipped reality in the present tense; proposal-era headings such as `Plan` or `Acceptance criteria` are rejected by the checker.

### Alternatives considered is mandatory

Record real alternatives and why they lost; never invent them. For a note predating `formatAdopted: 2026-10-11` whose alternatives cannot be recovered, the checker accepts:

```markdown
<!-- agent-note-format: alternatives-not-recorded (pre-format Agent Note) -->
```

### Moving between lifecycles

When a proposal ships, change `Proposal` to `Decision`, fold plans and risks into consequences, update the status, and move it to `implemented/`. A rejection records the reason on `Status:` and keeps the proposal body.

## Language and counterparts

MikuN2N uses an English `foo.md`, Chinese `foo.zh.md`, and consistency record `foo.i18n.json`. Both languages have equal authority, matching status and heading structure. Chinese section names are 问题／提案／决策／曾考虑的替代方案／验收标准／风险／后果.

After editing both sides and confirming their meaning, run `python tools/agent_notes.py pair --write <primary-path-relative-to-notes>` for the changed groups. Keep these contract READMEs aligned; they have no sidecar. Do not retranslate untouched notes or archives.

Run `python tools/agent_notes.py check` once after note changes (Python 3.9+ and Git), plus `git diff --check`. The optional `lint` command suggests prose edits; it is not a required verdict report or release prerequisite.
