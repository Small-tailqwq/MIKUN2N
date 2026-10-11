# AGENTS.md — Implemented Agent Notes

These notes describe shipped decisions. Follow the [Agent Note format](../README.md#the-file-format); `python tools/agent_notes.py check` enforces the lifecycle-specific structure.

## Keep an implemented note current with what shipped

Keep paths, symbols, defaults, and mechanisms current in the same change that alters them. Rewrite stale facts in place in the present tense; do not append change history ("used to", "now", "as of this change").

When a shipped note is unlikely to guide future work, retire it through the [`agent-notes` skill](../../skills/agent-notes/SKILL.md).

## This is not a license to rewrite the decision

Update the factual realization in place. A reversal of the decision or its rationale requires a new note and a cross-link; a fully superseded note may be deleted only through the consolidation rule in the [Agent Note rules](../README.md#when-to-write-one).
