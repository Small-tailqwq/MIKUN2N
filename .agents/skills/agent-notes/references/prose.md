# Prose guide for Agent Notes

A reader at the current commit should be able to find the mechanism and understand why it was chosen without the chat or review thread.

- State concrete names, paths, defaults, and failure behavior. Link to the owner instead of copying its explanation.
- Keep reasons, real alternatives, trade-offs, and compatibility constraints. Avoid walkthroughs that repeat the code.
- Put session history, reviewer discussion, commands run, and author-machine details in the work report.
- Remove repeated summaries, vague references, defensive qualifications, and unnecessary contrasts. Keep a limitation when a reader could otherwise draw the wrong conclusion.
- Describe implemented behavior in the present tense. Keep both languages aligned when editing a bilingual note.

`python tools/agent_notes.py lint` can suggest passages to review. Its output is advisory; use judgment and do not expand the change merely to clear stylistic matches in untouched notes.
