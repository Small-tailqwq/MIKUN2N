# AGENTS.md — Agent Notes

Agent Notes are RFCs written by agents: durable proposals and decision records that preserve rationale, alternatives, consequences, and required verification. Follow the [Agent Note rules](README.md) and the [`agent-notes` skill](../skills/agent-notes/SKILL.md).

**Every new Agent Note triggers a supersession check.** Search the active tree for older notes covering the same decision or mechanism, classify each full or partial supersession with the skill, and archive, reject, or delete every qualifying match in the same change. Keep partial supersessions active and cross-linked.

Files under [`archived/`](archived/AGENTS.md) are frozen historical snapshots: never edit them or treat them as current authority.

Run `python tools/agent_notes.py check` after touching anything under this directory, and `python tools/agent_notes.py lint` after writing note prose; report a keep or rewrite verdict for every lint hit.
