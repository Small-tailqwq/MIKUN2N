# Agent Notes

> [English](README.md) | [简体中文](README.zh.md)

An **Agent Note** records a decision or proposal that affects this codebase: the *why* and *what we gave up*, the parts code and other docs cannot carry. Agent Notes are RFCs written and maintained mostly by coding agents. This file defines where they live, when to write one, how they are retired, and [the in-file format](#the-file-format). [`agent-notes.config.json`](agent-notes.config.json) holds the project's settings. Run `python tools/agent_notes.py check` from the repository root with Python 3.9+ and Git; the gate checks structure, format, bilingual records, and archive seals. Review still checks rationale, translation quality, and links.

MikuN2N uses English primary notes with Chinese counterparts, the six classes below, and `formatAdopted: 2026-10-11`. The contract README is bilingual; agent instructions and skill documents remain in their existing language, without translated counterparts. The existing release script and workflow run the gate; pull requests use their target commit as the archive baseline. Existing technical contracts, analyses, and raw test logs remain at their documented paths. See the [adoption note](implemented/process/2026-10-11-adopt-agent-notes.md) for scope, integration, and the pinned upstream revision. The kit's [MIT license](../agent-notes-LICENSE.txt) covers the imported gate and guidance.

## Layout and naming

Every Agent Note has two axes, both encoded in its **path**: `{lifecycle}/{class}/yyyy-mm-dd-topic-title.md`.

- **Lifecycle** (the top-level folder) is the note's status. A note moves between folders as its status changes:
  - **`proposed/`**: reviewed before implementation; not yet built, or only partly built.
  - **`implemented/`**: the decision shipped. The note records what was decided and what was rejected, and is **kept current with what actually shipped**: when code later moves a file, renames a module, or changes a key or default, the same change updates the note's facts (paths, names, structure), never its decision. See [implemented/AGENTS.md](implemented/AGENTS.md).
  - **`rejected/`**: considered and declined. Keep it only while its rationale prevents a tempting, meaningful mistake; otherwise delete it.
- **Class** (the nested folder) is the kind of decision; see [Classification](#classification).

The filename date is when the topic was **first proposed** (per git history), and it never changes when the note moves. Cross-references between notes use relative Markdown links, never bare prose or numbers. Repair those links when a note moves.

The lifecycle tree is the inventory: browse its folders or search the repository. Do not add a central `INDEX.md`; it duplicates path metadata and makes unrelated changes conflict on one file. The gate rejects it.

## Classification

Each note belongs to exactly one class from the closed `classes` list in the config; the gate rejects any other folder. Adding a class means editing the config, this table, and the matching `archived/<class>/` folder in one change, and recording why in a `process` note.

| Class | What it covers |
|---|---|
| `feature` | A new user- or model-facing capability. |
| `bug-fix` | Corrects a defect or closes a gap an incident surfaced. |
| `simplification` | Removes code, behavior, or surface area without adding a capability. |
| `architecture` | A structural decision about the **shipped source**: how modules relate, what the runtime vocabulary is. |
| `process` | Tooling, policy, or workflow **around** the code: gates, CI, package management, agent workflows. |
| `testing` | Test infrastructure and strategy. |

**architecture** is about the source that ships; **process** is the tooling and workflow around it. There is no `refactor` class: it overlaps `simplification`, whose question "does observable behavior change?" already covers it.

## When to write one

Add or update an Agent Note in the same change only for **lasting decision rationale that code, tests, and existing documentation do not explain**. A proposal for substantial future work starts in `proposed/`; a decision already made starts in `implemented/`.

- **Exempt:** mechanical or local edits, including local UI presentation and interaction tweaks, routine bug fixes whose cause and fix are evident from the diff, and dependency bumps without a policy choice.
- **One owner per decision:** updating the note that already owns a decision satisfies the rule; never create a duplicate.
- **Never edit a note into a different decision.** Supersede it with a new note and cross-link both, unless the old note is later consolidated under the rule below.
- **Supersession is checked at write time.** Every new note triggers a scoped search of active notes covering the same decision, mechanism, or rejected alternative, and the same change classifies each match with the [`agent-notes` skill](../skills/agent-notes/SKILL.md).

A fully superseded implemented note may be **consolidated** into the note that now owns the decision and then deleted. Before deletion, the owner must preserve every unique rationale, alternative, consequence, required verification, and named coverage gap, and every inbound link must be repaired. Partial supersession does not qualify: keep both notes cross-linked and keep every surviving fact current. Consolidation never rewrites the old note into its opposite and never leaves git history as the only copy of rationale.

A feature-addition note may be consolidated into the later removal note only when the feature is gone from production code, configuration, schemas, persisted or wire formats, migrations, and compatibility handling; no current documentation presents it as available; and no test exercises it as supported behavior. The removal note then keeps the original motivation, why it no longer justified the feature, the alternatives to full removal, the capability given up, the conditions for reintroduction, and how complete absence is verified. Removing one transport, default, implementation, or presentation is partial supersession.

## Retiring notes: delete, archive, or keep

Judge every note by its **future decision value**, never by word count, age, or a quota. The [`agent-notes` skill](../skills/agent-notes/SKILL.md) carries the calibrated workflow.

- **Implemented, delete:** a note that only describes small UI adjustments or purely mechanical changes. A small diff that carries a lasting naming, compatibility, ownership, or behavior rule does not qualify.
- **Implemented, keep active:** its alternatives, ownership boundary, negative guarantee, persisted or wire semantics, security rule, or reintroduction condition can still guide a future change.
- **Implemented, archive:** the decision is complete and unlikely to guide future work, but its rationale still has historical value.
- **Proposed:** never archived. An obsolete proposal moves to `rejected/` with an honest reason.
- **Rejected:** kept only while it prevents a plausible mistake; otherwise deleted.

Every deletion repairs or removes inbound links in the same change.

### The frozen archive

The archive path is `archived/{class}/yyyy-mm-dd-topic-title.md`; `implemented` is absent because only implemented notes enter it. Archiving moves the note (with its counterparts and sidecar, if bilingual), keeps `Status: implemented`, inserts `Archived: YYYY-MM-DD` on the line immediately below it in every language file, re-records the sidecar once, and repairs or deletes inbound links. Nothing else in the note may change during archival.

Once sealed, an archived note is **permanently frozen**: never edit, translate, reformat, update, move, or delete it, and never treat it as authority for current behavior. `python tools/agent_notes.py seal` appends a SHA-256 seal per file to [`archived/manifest.json`](archived/manifest.json); `python tools/agent_notes.py check` rejects any changed, missing, or unsealed archived file and any seal removed relative to the baseline commit. Other documentation checks skip archived files, including their outbound links; active prose may still link into the archive when it cites history on purpose. See [archived/AGENTS.md](archived/AGENTS.md).

## The file format

Every active note follows one in-file format, enforced by `python tools/agent_notes.py check`. Archived notes keep the format they had when sealed, plus the archive-date line.

### The header block

The first lines of every note are exactly:

```markdown
# Agent Note: <title>

Status: <status>

```

The `# Agent Note: ` prefix and the `Status:` line are machine tokens and stay in English in every language. The status must agree with the lifecycle folder:

- `Status: proposed`
- `Status: implemented`
- `Status: rejected — <why, in one line>`

The status carries no dates and no parentheticals: the filename holds the first-proposed date and git holds the rest. The rejection reason is the one status with content, because the verdict is what readers of a rejected note come for. `Status:` appears only once outside code fences.

### The body skeleton

Every note opens its body with `## Problem`: the motivation, written so it stands without the solution. Required sections use the canonical names below and nothing else; genuinely bespoke technical sections (module topology, wire formats, schemas) are free-form between them. Headings inside fenced code blocks are ignored.

`proposed/`:

```markdown
## Problem
## Proposal
…bespoke sections…
## Alternatives considered
## Acceptance criteria
## Risks
```

`## Proposal` may speak in the future tense: plans, migration steps, and open questions belong here while the work is unbuilt. `## Acceptance criteria` says which observable state means done. `## Risks` covers what could go wrong and what the change knowingly gives up.

`implemented/`:

```markdown
## Problem
## Decision
…bespoke sections…
## Alternatives considered
## Consequences
```

`## Decision` describes shipped reality in the present tense. `## Consequences` records what the trade-off cost **and** bought. Proposal-era headings (`## Proposal`, `## Plan`, `## Migration plan`, `## Acceptance criteria`, and their `: …` variants) are rejected here; `## Testing`, `## Deferred`, or `## Related` are fine where they state present facts.

`rejected/`: the proposal, frozen. It keeps whatever proposal-time sections it had, and the verdict lives on the `Status:` line. Only the header block, the `## Problem` opener, a `## Proposal` section, and the alternatives rule apply.

### Alternatives considered is mandatory

Every note has `## Alternatives considered`: each genuine alternative and why it lost, as one bold-led paragraph per alternative or a `### Why not <X>?` subsection for a contested one. A decision recorded without what it beat invites re-litigation, which is the failure Agent Notes exist to prevent.

Alternatives are recorded, never invented. A note dated before the config's `formatAdopted` date whose alternatives cannot be reconstructed carries this exact comment instead, and the gate accepts it only for such notes:

```markdown
<!-- agent-note-format: alternatives-not-recorded (pre-format Agent Note) -->
```

### Moving between lifecycles

A move updates the `Status:` line and satisfies the new folder's skeleton in the same change. `proposed/` → `implemented/` rewrites `## Proposal` into a present-tense `## Decision`, folds `## Acceptance criteria` and `## Risks` into `## Consequences` (or a present-tense `## Testing` section), and replaces plans with what shipped. `proposed/` → `rejected/` only adds the reason to the `Status:` line and freezes the file.

## Language and counterparts

`primaryLanguage` in the config names the language of each `foo.md`; the configured headings are written in that language. With an empty `counterpartLanguages` list, every note is a single file.

MikuN2N configures `primaryLanguage: "en"` and `counterpartLanguages: ["zh"]`. Every active note is a group of sibling files: `foo.md`, `foo.zh.md`, and a consistency sidecar `foo.i18n.json`. Both languages carry equal authority; a note may be drafted in either and translated into the other. A counterpart keeps the English machine tokens, repeats the primary `Status:` line verbatim, and mirrors the heading structure one to one. English primary headings use the canonical names above; Chinese counterparts translate them as 问题／提案／决策／曾考虑的替代方案／验收标准／风险／后果. Place mutual language links below the header block, preserving the first four lines.

The sidecar records a hash of every section on every side. Editing a note without updating its record fails the gate and names the section. After confirming that both sides say the same thing, run `python tools/agent_notes.py pair --write implemented/process/2026-10-11-adopt-agent-notes.md` (substitute the primary path relative to `.agents/notes/`), then `python tools/agent_notes.py check`. Reviewing the sidecar diff is where a person or agent confirms that both sides say the same thing. Keep both contract READMEs synchronized too; they are reviewed together and are outside note sidecar validation.

Switching modes never rewrites the archive: archived groups are validated only against the files they contain.
