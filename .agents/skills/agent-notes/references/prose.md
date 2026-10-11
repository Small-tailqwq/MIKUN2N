# Prose checks for Agent Notes

Agent-written notes fail in predictable ways: they leak the writing session, pad facts with defensive qualifications, and state facts too loosely to verify. Apply one test to every suspect passage: **could a reader at the current commit, with no access to any chat transcript, PR thread, or uncommitted draft, resolve every reference and verify every claim?** If not, restate the surviving facts from the repository's point of view and delete the rest. Never delete a passage that still carries a fact; restate the fact first.

## Leakage to remove

1. **Dead session citations:** `(decision 7)`, `(audit C2)`, `plan §4`, phase labels, "as discussed above" pointing at a chat. Cite a committed owner by path, or restate the fact.
2. **Change and stack vantage:** "this PR adds", "a later PR", "the previous commit", "used to", "no longer", "now", "this cut", "本次接入", "这次改动". State the present mechanism. A fixed regression becomes a counterfactual: "without X, Y happens".
3. **Review choreography:** "rejected in review", "the reviewer confirmed", "v3 of this note", "the maintainer explicitly asked for", "维护者明确要求". Keep the decision and its reason, stated as the project's own rule; drop who said it when.
4. **Reviewer-addressed justification:** "this is safe because…" written to win an argument. State the invariant that makes it safe.
5. **Derivation transcripts:** "first we X, then Y", walkthroughs of tests or obvious branches. Keep only the resulting contract.
6. **Hedges and residue:** "probably fine for now", "should be enough", deferrals without an owner. Promote to a `TODO` with an issue, or state the actual bound.
7. **Mixed working language:** fragments of the authoring language left in prose written in another language.
8. **Author-machine facts:** tool versions, operating system, or paths of the machine that wrote the note ("installed with Python 3.12.10", "tested locally on Windows"). A reader at the current commit cannot verify them. State the requirement the repository enforces (a minimum version in a config file, a CI matrix) or delete the sentence.
9. **Draft history:** "the original 55 entries", "legacy anchors", "the inventory originally chosen here", "原 55 项", "旧锚点", "最初选择的". These describe the state before the uncommitted change, which no reader at the commit can see. Supersession between notes that land in the same change is the same leak: the replaced decision never existed in the repository. Describe the resulting layout and its stable contract ("`patch-N` anchors stay stable for citations"), and link the other note as related, not superseded.
10. **Process reports:** "this note owns …", "no older note needed superseding", "本笔记负责……", "没有需要取代的旧笔记", lists of what the author checked. They report on the writing session. Put them in the change description or the report to the user.

## Not leakage

- Issue and merged-PR references (`#123`) as evidence.
- "Without X, Y happens" regression explanations and measured bounds ("measured: 512 items ≈ 0.15 s").
- Runtime old/new states ("the old connection drains before the new one accepts").
- The `## Alternatives considered` section itself, and "we" as the project voice.

## Patterns that read as machine-written

These add words without adding facts. Each has a test; rewrite only when the test fails.

- **Scope disclaimers:** "X does not prove / guarantee / mean Y" / "X 不能证明 / 不保证 / 不代表 / 不等于 Y". Keep it only when a reader would plausibly over-read X, for example a latency probe that readers might take as a bandwidth measurement. When the sentence before it already says what X checks, delete the disclaimer.
- **Unprompted negatives:** "this setup does not configure branch protection" / "不配置远端分支保护" when nothing in the note or the repository suggests it would. Delete it, or, when the absence is a deliberate decision, state the reason as a fact.
- **Contrast against a straw man:** "X is not A, but B" / "不是 A，而是 B" / "rather than A". Delete the A half and reread: keep it only if a reader or agent would now plausibly do A, as with a prohibition on something agents really do. A real alternative that the surrounding text already names does not pass; state B alone.
- **Escalation pairs:** "not only A, but also B" / "不仅 A，更 B" / "既 A，也 B". Keep it only when B is surprising given A; otherwise list both plainly.
- **Announced importance:** "it is worth noting", "importantly", "值得注意的是", "关键在于", "本质上". Delete the announcement; if the clause matters, put it first or make it its own sentence.
- **Colon reveals:** "The answer is simple: …", "原因只有一个：…". State the answer.
- **Reflexive triads:** three adjectives or three parallel clauses where one or two carry the fact. Keep only the items that each add a fact.
- **Closing summaries:** a final sentence restating the paragraph ("In short, …", "总之，…", "这样一来，…"). Delete it unless the paragraph is long enough that a reader needs the conclusion repeated.
- **Dash and bold density:** several em dashes or bold phrases per paragraph. Use a sentence break or a list; keep bold for the one clause that changes behavior.

## Facts stated too loosely to verify

A note is only as useful as the facts a reader can act on. These fail the opening test even when every word is true.

- **Vague references:** "the relevant files", "as needed", "etc.", "相关理由", "必要时", "等等". Test: could the reader list exactly what is meant? Name the files, conditions, or items; when the list is long, link the file that owns it.
- **Ambiguous scope:** "agent instructions and the skill each keep one copy" / "各维护一份" (one copy each, or one language each?). Test: can the sentence be read two ways? Write the reading you mean ("each exists only in its current language").
- **Unanchored claims:** a behavior, default, limit, or measured number with no path, command, configuration key, or source. Test: could a reader find where it is enforced or measured? Add the anchor or drop the claim.
- **Restated owner facts:** a sentence repeating what a linked contract already says ("the gate detects stale section hashes"). Delete it and keep the link.
- **Broken sentences:** reread every sentence in the note's own language. Mixed constructions ("属于……之外" for "不在……范围内"), subjects that change mid-sentence, and counterparts that keep the other language's word order fail. Fix the grammar before judging anything else; a broken sentence hides which fact it states.

## Recall probes

The probes find candidates, not violations. Judge every hit with the tests above, rewrite what fails, keep the justified ones, and report each hit with its keep or rewrite verdict. Also read the densest paragraphs without a pattern in hand: probes miss phrasings they do not list, and ambiguous scope, unanchored claims, and broken sentences have no reliable pattern at all.

`python tools/agent_notes.py lint` runs these probes on every active note changed against `AGENT_NOTES_BASE_REF` (default `HEAD`) or untracked, in every language, and flags a supersession link to a note absent from that baseline. `NOTE...` or `--all` widens the scope; it never fails. With `"proseLint": true` in the config, `python tools/agent_notes.py check` also prints these candidates after a clean run. For other files, run the probes by hand (replace `FILES`).

```sh
# Scope disclaimers and unprompted negatives
grep -niE "(does not|doesn't|cannot|can't|won't) (prove|guarantee|mean|imply|ensure|verify|judge)|不能(证明|保证|判断|代表|说明)|不(意味着|代表|等于|保证)|并不|只(能)?说明|不配置|不提供|不涉及" FILES
# Straw-man contrasts and escalation pairs
grep -niE "not [^.]{1,40}, but|rather than|instead of|不是.*而是|而非|并非|不仅|既.*，也" FILES
# Announcements and closing summaries
grep -niE "worth noting|importantly|essentially|in short|值得注意|关键在于|本质上|总之|这样一来" FILES
# Session vantage, process reports, and author-machine facts
grep -niE "(^|[^A-Za-z])this (PR|change|note|session)([^A-Za-z]|$)|本次|这次|此次|本笔记|维护者(的)?(明确)?要求|用户要求|locally|本机|(Python|Node|node|python) ?v?[0-9]+\.[0-9]+\.[0-9]+" FILES
# Draft history
grep -niE "originally|previously|formerly|no longer|used to|the old |legacy|[0-9]+ original|最初|原先|原本|原来|此前|曾经|不再|改为|原 ?[0-9]|旧 ?\`" FILES
# Vague references
grep -niE "(^|[^A-Za-z])(etc\.|and so on|as needed|as appropriate|various|relevant (files|docs|code|parts|settings|rationale|changes))|相关(文件|文档|代码|配置|逻辑|内容|部分|理由)|相应的?(文件|文档|代码|配置|修改|处理)|某些|若干|一些|适当|必要时|等等|之类" FILES
```

## Style for implemented notes

- Present tense about what exists; no "should", migration plans, or acceptance checklists.
- One home per fact: link to code, docs, or another note instead of restating them.
- Concrete names over metaphors: name the file, function, key, or command.
- Emphasis only on the clause that changes behavior.
