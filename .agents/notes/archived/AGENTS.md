# AGENTS.md — Archived Agent Notes

Archived notes under the class folders are frozen historical snapshots, not current authority. Never edit, reformat, translate, repair, delete, or move a sealed file; put new decisions and facts in an active note or current documentation.

The archival change may only move the note (with its counterparts and sidecar, if any), insert the `Archived: YYYY-MM-DD` line immediately below `Status: implemented` in every language file, re-record the sidecar once, and repair or delete inbound links. Do not inspect, verify, or repair links out of archived notes.

Follow the [`agent-notes` skill](../../skills/agent-notes/SKILL.md), then run `python tools/agent_notes.py seal` to append seals for the new files and `python tools/agent_notes.py check` to verify. The check rejects changed, missing, or unsealed files, seals removed relative to the baseline commit, unknown class folders, and invalid archive metadata.
