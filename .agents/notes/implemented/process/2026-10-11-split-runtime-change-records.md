# Agent Note: Disclose runtime changes through bilingual version records

Status: implemented

> [English](2026-10-11-split-runtime-change-records.md) | [简体中文](2026-10-11-split-runtime-change-records.zh.md)

## Problem

Readers need to find bundled components and build instructions quickly, while contributors need the reasons and test results behind native changes. Keeping the full change history in the runtime README makes basic information harder to find as versions accumulate.

## Decision

The [runtime overview](../../../../Runtime/README.md) lists components and links to [build instructions](../../../../Runtime/BUILD.md) and a [topic guide](../../../../Runtime/changes/README.md). `README.txt` is a short bilingual entry point for existing references in the source archive and component notices.

Implementation history lives in `Runtime/changes/<version>.md` with a `.zh.md` counterpart. Each record describes the problem and changes, keeping test results beside the relevant implementation. These logs ship with the runtime and describe behavior at that version. Agent Notes under `.agents/notes/` carry lasting decisions and follow their own lifecycle and gate.

The records contain 55 numbered changes in 22 bilingual groups. Entries with explicit versions are grouped by those labels; entries 1–5 and 34–37 have unknown versions and are collected in `unversioned.md`. The `patch-N` anchors stay stable for citations. Each numbered entry describes its implementation and evidence in both languages. The topic guide provides links by mechanism; individual versions are found by filename.

The [project content list](../../../../MikuN2N.csproj) copies both language overviews, build instructions and all change-record Markdown files into build and publish output. The [release packaging script](../../../../tools/package-release.ps1) checks that they are present. The [updater](../../../../Services/UpdateService.cs) installs subdirectories and includes `Runtime/changes/` in its nonrecursive cleanup of replaced `.update-old` files.

The [Agent Notes adoption record](2026-10-11-adopt-agent-notes.md) explains the language, lifecycle, gate and agent-integration choices for decision notes. Technical documents cite runtime changes through the matching version record and language.

## Alternatives considered

**Continue appending to the runtime README.** This preserves one-file reading, but every component lookup pays for the complete history and the file grows on every release.

**Move the complete list into one changelog file.** This shortens the overview while leaving the same unbounded detailed document behind its link. Per-version records bound each reading unit.

**Create one file per numbered patch.** It gives finer retrieval, but divides a single delivery across many files and makes paired translation maintenance more fragmented. Version groups retain stable per-patch anchors for precise references.

**Store all implementation history as active Agent Notes.** Their maintenance rules require updating facts to match current code, while version logs need to preserve historical behavior. Shipping them would also bring internal process guidance into runtime packages.

## Consequences

Readers can reach the relevant version through the overview and topic guide. Adding a release changes one bilingual record and any relevant topic links, keeping the overview short. Both languages are available in the distributed runtime.

Runtime logs are outside `agent_notes.py`'s scope, so their paired content, links and packaged files need manual review. Readers looking for current behavior need the latest relevant record and technical documentation.
