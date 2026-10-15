---
name: agent-notes
description: Use for lasting decision rationale and for writing, updating, reviewing, or retiring Agent Notes under .agents/notes.
---

# Agent Notes

Use notes for rationale that code, tests, and current docs cannot explain and a future contributor could otherwise undo or re-investigate. Routine fixes, mechanical cleanup, local UI edits, and validation reports belong in the change description. Follow the [format](../../notes/README.md#the-file-format) when a note is needed.

## Find or update the owner

Search active notes by the decision or mechanism. Update an existing owner rather than making a duplicate. Keep paths, names, and defaults current when they change. A reversed decision gets a new note linked to the old one; do not rewrite history into its opposite. Do not create a note solely to document that this process was followed.

## Write and maintain

- Use `{lifecycle}/{class}/yyyy-mm-dd-topic.md`; retain the first-proposed date when moving it.
- State the problem, concrete decision or proposal, real alternatives and their reasons, and consequences. Never invent alternatives to fill a heading.
- Keep durable facts and relative links. Put commands run, machine details, and review history in the work report. The optional [prose guide](references/prose.md) helps trim repetition.
- For bilingual notes, edit both languages, confirm their meaning, then run `python tools/agent_notes.py pair --write <primary-path-relative-to-notes>` for only those notes.
- When a proposal ships, change `Proposal` to present-tense `Decision`, fold plans and risks into consequences, and move it to `implemented/`. A declined proposal moves to `rejected/` with its reason on `Status:`.

## Retire only what the task calls for

Keep rationale that still guides behavior, ownership, compatibility, or a plausible rejected alternative. Delete trivial or obsolete notes when their remaining facts are already owned elsewhere; repair inbound links. Consolidate a fully superseded note only after preserving its unique rationale in the current owner. Partial supersession keeps both linked.

Archive a completed implemented note only when its history is useful but its rationale no longer guides current work. Add `Archived: YYYY-MM-DD` immediately below `Status: implemented` in both languages, refresh its sidecar, and move the group under `archived/<class>/`. Repair active inbound links and run `python tools/agent_notes.py seal`. Existing archives remain frozen.

## Validate once

After editing notes, run `python tools/agent_notes.py check` and `git diff --check`. `lint` is an optional writing aid; its candidates do not require a separate verdict list. Report substantive decisions and any failed or unavailable checks. Stop after the relevant checks pass unless a new issue appears.
