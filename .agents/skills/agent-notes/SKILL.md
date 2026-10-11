---
name: agent-notes
description: Use when a change involves a lasting decision (choosing between designs, adopting or removing a capability, setting a policy, accepting a trade-off), when writing, updating, moving, or reviewing an Agent Note under .agents/notes, and when auditing, pruning, archiving, or deleting Agent Notes. Decides whether a note is needed, checks supersession, applies the lifecycle format, and retires notes by future decision value.
---

# Agent Notes

An Agent Note keeps the rationale a future agent needs so it does not re-litigate or silently undo a decision. Read [the rules](../../notes/README.md) before writing; this skill is the judgment and procedure on top of them. `python tools/agent_notes.py check` enforces the mechanical parts, so spend attention on what it cannot check: whether the note is needed, whether it is true, and whether it is the only owner of its decision.

## 1. Decide whether a note is needed

Write or update a note only when **all** hold:

1. The change makes or reverses a choice between real alternatives, or records why something is deliberately absent.
2. A future contributor, human or agent, could plausibly undo it or redo the analysis without the note.
3. Code, tests, comments, and current docs do not already carry the rationale.

Skip it for mechanical edits, local UI tweaks, evident bug fixes, and dependency bumps without a policy choice. When several agents work in the same repository, prefer one note per decision over one note per session; the note is shared memory, not a work log.

## 2. Find the owner and check supersession

Before drafting, search the active tree (`proposed/`, `implemented/`, `rejected/`) for notes covering the same decision, mechanism, or rejected alternative. Search by concept, not only by file name.

- An active note already owns the decision: **update it**; do not create a second owner.
- The new decision reverses or narrows an old one: write a new note, cross-link both, then classify the old note with section 5 in the same change.
- The new note absorbs every unique proposition of an old implemented note: apply the consolidation rule in the README and delete the old note in the same change.

Do not defer a known match to a later cleanup; the author of the replacement has the freshest evidence.

## 3. Write it

1. Path: `{lifecycle}/{class}/yyyy-mm-dd-topic.md`. A decision already made goes in `implemented/`; substantial unbuilt work goes in `proposed/`. Use the first-proposed date and a short kebab-case topic.
2. Header: `# Agent Note: <title>`, a blank line, the `Status:` line, a blank line.
3. `## Problem` stands alone: someone who rejects the solution should still agree with the problem statement.
4. `## Decision` (or `## Proposal`) states the mechanism concretely: names, paths, defaults, invariants, failure behavior. An implemented note speaks in the present tense about what exists.
5. `## Alternatives considered` lists only alternatives that were really weighed, each with the reason it lost. Never invent alternatives to fill the section.
6. `## Consequences` states what the decision costs and what it buys, including what is now impossible or harder.
7. Link related notes and the code or docs that implement the decision with relative links.
8. Run `python tools/agent_notes.py lint` (the recall probes in the [prose checks](references/prose.md), applied to every note you changed), judge each hit with the test it lists, and fix what fails. Then reread the note for the loose facts no probe finds: ambiguous scope, unanchored claims, and broken sentences. A hit you neither rewrite nor justify leaves the note unfinished. Session facts such as supersession results, the commands you ran, or your machine's tool versions go in your report, not in the note.

## 4. Move between lifecycles

- **Proposed → implemented** in the change that ships the decision: rewrite `## Proposal` as a present-tense `## Decision`, fold `## Acceptance criteria` and `## Risks` into `## Consequences` or a present-tense `## Testing`, and replace plans with what shipped. Keep the filename date.
- **Proposed → rejected:** add the one-line reason to the `Status:` line; leave the body as it was.
- **Implemented, decision reversed:** never edit it into the new decision. Write a new note and classify the old one.

## 5. Retire notes by future value

Judge each note semantically; word count and age are discovery aids, never criteria. Use current code, configuration, docs, newer notes, and inbound links to decide whether the rationale still constrains anything.

- **Implemented, delete:** only small UI adjustments or purely mechanical changes. Apply this test first. Local bug fixes, performance changes, new capabilities, and behavior or ownership decisions do not qualify just because they shipped.
- **Implemented, keep active:** the rationale, alternatives, negative guarantee, persisted or wire semantics, ownership boundary, security rule, or reintroduction condition is likely to guide a future change. Classify the decision, not the size of its diff: a mechanical rename can still carry a lasting naming or compatibility rule.
- **Implemented, archive:** complete, unlikely to guide future work, but the historical rationale is still worth preserving.
- **Proposed:** never archive. If no longer worth pursuing, move it to `rejected/` with an honest reason.
- **Rejected, keep:** only while the losing idea remains a tempting, meaningful mistake and the note explains why it loses.
- **Rejected, delete:** the idea is obsolete, superseded, implausible, or unlikely to prevent re-litigation.

Do not retire toward a quota. Inspect every note in scope, classify similar notes under one principle, and report genuinely borderline cases.

### Calibration

These examples come from the corpus this kit was extracted from (deepseek-harness: about 300 active and 900 archived notes); word counts show that size is not the test. Replace them with examples from this repository once it has a few retirement decisions of its own.

- Delete: a collapsed sidebar control (533 words; closed, minor UI behavior); a module ownership move (relocation and import rewiring, behavior unchanged).
- Keep: event-sourced sessions (248 words; foundational durability boundary); a single configuration-root resolver (596 words; cross-product ownership rule); parallel pre-push checks (400 words; borderline, still guides scheduling); a dropped content block (334 words; states the condition for reintroducing it).
- Rejected, keep: merging two packages that were split on purpose (the temptation recurs). Rejected, delete: streaming progress through a protocol that was later removed (premise obsolete).

## 6. Archive one implemented note

1. While the note is still under `implemented/`, insert `Archived: YYYY-MM-DD` (today) immediately below `Status: implemented` in every language file. Change nothing else: no fact updates, link fixes, reformatting, or translation.
2. If bilingual, run `python tools/agent_notes.py pair --write implemented/<class>/foo.md` once so the sidecar includes the archive-date lines. The command accepts active primary paths relative to `.agents/notes/`.
3. Move `implemented/<class>/foo.md` to `archived/<class>/foo.md`, together with `foo.<lang>.md` and `foo.i18n.json` if the note is bilingual.
4. Find inbound links from active files. Point them at the current authority, retarget them to the archive only when the history is cited on purpose, or delete them.
5. Run `python tools/agent_notes.py seal`, then `python tools/agent_notes.py check`. The seal appends hashes; it never rewrites existing ones.

## 7. Bilingual groups

When the config lists counterpart languages, edit both sides in the same change: patch the counterpart minimally against the diff of the edited side instead of re-translating the whole file, keep the heading structure and code blocks identical, and keep the English machine tokens. Then run `python tools/agent_notes.py pair --write <note.md>` for exactly the notes you confirmed and review the sidecar diff. `--all` is for deliberate corpus-wide confirmation only.

## 8. Validate and report

Run `python tools/agent_notes.py check`, `python tools/agent_notes.py lint`, and `git diff --check`. Report notes added or updated, the supersession check and its result, notes kept, archived, or deleted with a one-line reason each, inbound links repaired, every lint hit with its keep or rewrite verdict (and, for a kept hit, the test it passes), and every borderline call. Do not claim archived outbound links are valid; nothing checks them, by design.
