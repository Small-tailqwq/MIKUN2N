# MikuN2N runtime components

> [English](README.md) | [简体中文](README.zh.md)

This directory contains MikuN2N's runtime components, including a locally modified n3n 3.4.4 edge. Build and publish copy them into the application's `Runtime/` directory.

## Read as needed

| Question | Read |
|---|---|
| What changed in a version, why, and with what evidence? | [Bilingual change records](changes/README.md) |
| How is the native binary rebuilt and its source packaged? | [Build and source instructions](BUILD.md) |
| Which licenses, source offers and binary hashes apply? | [Third-party notices](THIRD-PARTY-NOTICES.txt) |

## Bundled components

| Files | Purpose |
|---|---|
| `n3n-edge.exe` | Preferred patched Windows edge. |
| `n3n-3.4.4-source.zip` | Corresponding modified source, including `MIKUN2N-MODIFICATIONS.md`, the GPLv3 §5(a) modification statement. |
| `LICENSE-n2n.txt` | Full n3n GPLv3 license. |
| `tap-windows-installer.exe` | Unmodified, officially signed OpenVPN TAP-Windows 9.24.7 installer. |
| `LICENSE-tap-windows.txt` | License extracted from that installer: GPLv2 with the WDK system-library exception. |
| `LICENSE-dotnet.txt`, `THIRD-PARTY-NOTICES-dotnet.txt` | Licenses and third-party notices for the .NET 9/WPF/WinForms runtime embedded in self-contained packages; taken from the Microsoft.NETCore.App.Runtime.win-x64 package. |
| `README.txt`, `README*.md`, `BUILD*.md`, `changes/*.md` | Reading entry points, build instructions and bilingual version records. |

The TAP installer is optional for using an already configured adapter; the public release packaging script includes it and its license.

The legacy n2n `edge.exe` has no reproducible corresponding source and must not be distributed. A user-supplied `Runtime/edge.exe` remains a local fallback when `n3n-edge.exe` is missing.

Distributing n3n requires the corresponding source and GPLv3 license. Distributing the TAP installer requires its license and the source offer in the [third-party notices](THIRD-PARTY-NOTICES.txt), which also list component hashes.
