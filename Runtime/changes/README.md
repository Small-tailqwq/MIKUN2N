# Runtime change records

> [English](README.md) | [简体中文](README.zh.md) | [Runtime overview](../README.md)

Find a version by its `<version>.md` filename, or start with a topic below. Each record describes the problem, changes and available evidence, with a Chinese counterpart in `<version>.zh.md`.

| Topic | Starting record |
|---|---|
| Supernode input bounds, relay identity, federation allowlist and wire-decoder length checks | [0.5.8-10, patches 56–57](0.5.8-10.md#patch-56) |
| NAT verdicts and cone/NAT4 coordination | [0.5.8-9, patch 53](0.5.8-9.md#patch-53) |
| Assisted-path diagnostics and IPv6 interface recovery | [0.5.8-9, patches 54–55](0.5.8-9.md#patch-54) |
| Peer assistance and per-pair consent | [0.5.8-8](0.5.8-8.md) |
| NAT66 mapping discovery in normal builds | [0.5.8-7](0.5.8-7.md) |
| Lease recovery and bounded background traffic | [0.5.8-5](0.5.8-5.md) |
| IPv6 size discovery, fragments and replay handling | [0.5.8-2](0.5.8-2.md), [0.5.8-3](0.5.8-3.md), [0.5.8-4](0.5.8-4.md) |
| Legacy Windows fixes, NAT punching and federation | Version-named files; [unversioned records](unversioned.md) contain entries without an original release label. |

## Reading the records

The records describe behavior and test results for their named versions. For current behavior, consult the latest relevant record and technical documentation.

Numbered changes 1–57 have stable `patch-N` anchors for citations. Entries 1–5 and 34–37 have unknown versions and are collected in [unversioned.md](unversioned.md). Each numbered entry describes its implementation and test results in both languages.

## Maintaining records

Add an English/Chinese pair for each new version, and update it while that delivery is in progress. Describe the problem, changes, compatibility effects and test results; include outstanding checks where relevant. Link earlier records when changing a previous design. Keep language links and heading structure paired.

Keep change details in the version files and update topic links here as needed. Build and publish include these Markdown files, and the release script checks that they are present.

Review both languages, local links and packaged content when changing these records. Source packaging instructions are in [BUILD.md](../BUILD.md).
