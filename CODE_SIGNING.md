# Code signing policy

## Status: planned application, not approved or enabled

The project intends to apply to SignPath Foundation. There is currently no claim
that SignPath Foundation has accepted this project, signed its artifacts or endorsed
its behavior. Unsigned test packages remain unsigned. No Foundation certificate,
GitHub App installation, token or signing approval is created by this policy.

If approved and actually enabled, the project will publish the required attribution:
Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

## Proposed artifact scope

Only the project's own `MikuN2N.exe`, built from the corresponding source revision,
is proposed for signing. This applies to both self-contained and framework-dependent
Windows packages. Exact paths, product name `MikuN2N`, and the approved build version
must be restricted in the signing configuration; recursive EXE/DLL wildcards are not
the intended scope.

`Runtime/n3n-edge.exe`, a user-supplied legacy `edge.exe`, the TAP installer/driver,
Microsoft runtime libraries and other upstream components are outside the proposed
signing scope. The unmodified TAP installer keeps its OpenVPN signature. Bundling a
component does not make it a binary signed by this project. Existing upstream
licenses, notices and corresponding-source obligations remain in force.

## Maintainers, review and approval

The current repository owner/maintainer is
[Small-tailqwq](https://github.com/Small-tailqwq).
The proposed author, reviewer and signing-approver assignments must be confirmed by
the maintainer before service activation. SignPath team membership and MFA status
have not been verified; this policy does not assert otherwise. AI tools are not
SignPath team members or human signing approvers.

Activation requires the responsible members to meet Foundation MFA, review and
approval requirements. Each release signing request must receive approval from an
authorized human member. A GitHub draft-publication decision is separate from the
signing approval. Foundation retains its own acceptance and certificate decisions.

## Planned build and publication controls

Use publicly reviewable source and GitHub-hosted Windows build jobs, upload the
unsigned artifact to the workflow, and let the trusted build integration verify its
origin. A locally uploaded EXE is not a substitute for that provenance chain.
After approval, retrieve the signed application, package it with unchanged required
runtime/source/license files, and generate ZIP hashes, `SHA256SUMS` and `update.json`
from the **final signed package**. Do not publish pre-signing hashes for changed files.
Current workflows do not implement a SignPath signing step.

## Privacy and project description

[Privacy and network activity](PRIVACY.md) describes automatic update requests,
experimental STUN traffic, local logs and optional authorized diagnostic upload.
[Network changes and removal](UNINSTALL.md) describes system effects and controls.
The project does not automatically report personal profiles, usage statistics or
diagnostic logs to its developers. Remote receiver retention is provider-defined.

Suggested application description:

> MikuN2N is a GPL-3.0-only Windows desktop client for private virtual LAN connections
> between participating users for multiplayer gaming. It manages a locally modified
> n3n edge, user-configured supernodes, peer discovery, NAT traversal and consent-based
> relay assistance. Diagnostic upload is optional and off by default, for development
> and troubleshooting, with retention controlled by the receiving service provider.
> We propose signing only MikuN2N.exe built from public source on GitHub-hosted Windows
> runners. Bundled third-party components retain their own licenses and signatures.

Before submitting an application, provide a verifiable release/download link and
confirm the actual team roles and MFA. Planned work or draft releases must not be
presented as an already active Foundation signing service.
