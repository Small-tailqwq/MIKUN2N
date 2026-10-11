# Agent Note: Adopt bilingual Agent Notes for MikuN2N

Status: implemented

> [English](2026-10-11-adopt-agent-notes.md) | [简体中文](2026-10-11-adopt-agent-notes.zh.md)

## Problem

MikuN2N has shared agent instructions, a native-patch skill, bilingual technical documents, and historical test reports. These explain existing behavior and investigations, but provide no common lifecycle or format for a new decision whose rationale is absent from those sources. Repeated agent sessions need to distinguish current decisions, proposals, and historical evidence without creating competing owners of the same rule.

## Decision

Agent Notes are based on [agent-notes-kit](https://github.com/Small-tailqwq/agent-notes-kit) at commit `6484330d6435f329384a0e4da6e355e6d7883d05`, following its [ADAPTING.md](https://github.com/Small-tailqwq/agent-notes-kit/blob/6484330d6435f329384a0e4da6e355e6d7883d05/ADAPTING.md). The [contract](../../README.md), [configuration](../../agent-notes.config.json), and [shared skill](../../../skills/agent-notes/SKILL.md) define the format and maintenance workflow. The skill's archive procedure is localized: it inserts the archive date and runs `pair --write` while the note is still under `implemented/`, because `pair` accepts active paths only. The imported gate and guidance retain the upstream [MIT license](../../../agent-notes-LICENSE.txt).

- **Gate:** `python tools/agent_notes.py`, from the repository root, using Python 3.9+ and Git. The [Python implementation](../../../../tools/agent_notes.py) is copied unchanged. `proseLint: true` makes `check` list the [prose-check](../../../skills/agent-notes/references/prose.md) candidates of changed notes after a clean run, so the release script and the CI notes job show them; the exit status ignores them. The gate reuses the dependency already used for [native source packaging](../../../../tools/package-native-source.py).
- **Language:** `primaryLanguage: "en"`, `counterpartLanguages: ["zh"]`, and the upstream English canonical headings. Each active note has an English file, a Chinese `.zh.md` counterpart, and an `.i18n.json` record. The contract README is bilingual, following the public documentation convention. Agent instructions and skill documents remain in their existing language, without translated counterparts.
- **Classification:** the six upstream classes, `feature`, `bug-fix`, `simplification`, `architecture`, `process`, and `testing`, cover transport, client, and tooling decisions. `formatAdopted` is `2026-10-11`; `forbiddenRoots` is empty because existing documentation keeps its paths.
- **Agent entry points:** root [AGENTS.md](../../../../AGENTS.md) links the contract and skill. [CLAUDE.md](../../../../CLAUDE.md) imports that file. The [Claude skill adapter](../../../../.claude/skills/agent-notes/SKILL.md) contains discovery metadata and a reference to the shared skill. It is a regular file so Windows checkouts work with symlinks disabled. The ignore rules admit this adapter under `.claude/` and ignore local settings.
- **Checks:** the [release script](../../../../tools/package-release.ps1) runs the gate before packaging. The [release workflow](../../../../.github/workflows/release.yml) runs it for PRs, version-tag pushes, and manual runs. The job fetches full Git history and sets `AGENT_NOTES_BASE_REF` to the PR target SHA or `HEAD~1`. PRs run the notes job only; packaging and draft releases run on tag/manual events.
- **Existing documentation:** technical contracts, analyses, proposals, and raw test logs retain their paths under `docs/`. Runtime history is organized by version, as described in the [version-record decision](2026-10-11-split-runtime-change-records.md). New notes link to existing explanations of a decision.

## Alternatives considered

**Use the Node gate.** It adds a release prerequisite for the same structural checks available in the standard-library Python gate. The repository already uses Python.

**Leave `proseLint` off and run `lint` by hand.** Most notes here are written by models prone to defensive qualification and loosely stated facts. A manual step depends on the author remembering it; listing candidates on every `check` puts them in front of the author and in CI logs.

**Single-language notes.** They reduce translation and sidecar maintenance, but limit access for contributors who read the other language. Bilingual notes follow the repository's public documentation convention.

**Copy the skill into each agent directory or require symlinks.** Copies can drift. Symlinks depend on Windows checkout settings and privileges. A small regular-file adapter keeps the procedure in one place while retaining a discoverable Claude skill.

**Rewrite all existing design and analysis documents as notes.** Those documents mix public explanations, measurements, deployment steps, and pending work, and already carry relevant rationale. Moving them wholesale would disturb established bilingual links and historical evidence. They retain their existing ownership; the note requirement applies to lasting rationale not explained there.

## Consequences

Lasting decisions gain searchable lifecycle paths, recorded alternatives, and sealed history. Routine local fixes need no note when the diff and existing documentation already explain them.

Every note change requires synchronizing two texts, reviewing the translation, and running `pair --write` for the affected primary paths before `check`. Both contract READMEs are reviewed together without a sidecar. Packaging requires Python, and CI runs a separate notes job.

When updating from the kit, compare against the pinned revision and preserve project localization. Changes to classes, headings, or language mode update the config and both contract READMEs together.
