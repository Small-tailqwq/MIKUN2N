# Packages and updates

> [English](UPDATES.md) | [简体中文](UPDATES.zh.md)

## Choose a package

| Windows x64 package | Requirement |
|---|---|
| `MikuN2N-<version>-win-x64.zip` | Self-contained: includes the .NET runtime. |
| `MikuN2N-<version>-win-x64-framework-dependent.zip` | Requires the [.NET 9 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). |

Both contain the single-file `MikuN2N.exe`, build identity, modified edge, corresponding native source, TAP installer, licenses, notices and runtime documentation. Keep the extracted directory together. The `UpdateRuntime` assembly metadata identifies the flavor; an absent or unrecognized value uses `self-contained`. Updates preserve this flavor. An explicit `--self-contained false` selects framework-dependent metadata unless overridden; the release script passes `-p:UpdateRuntime=...` explicitly for each build.

## Build and distribute

On Windows with PowerShell 7, Python 3.9+ and the .NET 9 SDK:

```powershell
./tools/package-release.ps1
```

Everyday tests keep `BaseVersion` and `BuildNumber` unchanged. The default command creates uniquely named test packages under `artifacts/test-packages/<test-id>/`, with informational version `<baseline>+test.<test-id>`, `SHA256SUMS` and `test-build.json`. The default ID contains UTC time, source commit, dirty state and a short random suffix; an explicit `-TestId` is also supported. Test builds disable real update checks and do not emit `update.json` or public download URLs. Existing artifacts are refused rather than replaced. Use `-PlanOnly` to inspect identities and paths without building.

Only after user acceptance and explicit release approval, update the agreed source version and run:

```powershell
./tools/package-release.ps1 -ExpectedVersion '<approved-version>' -Output dist -Notes 'Release notes'
```

This explicit release mode verifies the project version, publishes both flavors into fresh run directories, validates the corresponding native source and licenses, and writes:

- The two ZIPs listed above.
- `SHA256SUMS`, with one SHA-256 line for each ZIP.
- `update.json`, containing the static discovery information below.

Size and source-check evidence is saved under `artifacts/package-checks/<run-id>/`, outside the ZIPs. Release builds keep Simplified Chinese satellite resources and neutral English fallback, omit external PDBs, and use ZIP `SmallestSize`. No WPF trimming or AOT is enabled. Inner single-file compression stays disabled because its measured ZIP was slightly larger. See Microsoft's [single-file compression guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#compress-assemblies-in-single-file-apps).

The [Actions workflow](../.github/workflows/release.yml) selects release mode only for a pushed version tag, checks it against the project version, then attaches the four assets to a draft Release. Publish a reviewed draft to enable automatic discovery. Manual workflow runs default to test mode. Set `source_commit` to build a specific source commit and `release_version` to preview canonical packages and `update.json` after checking that version against the project. Manual runs only upload packages and validation reports as workflow artifacts; they never create tags or Releases. Builds have read-only repository permissions; only a separate tag-triggered job can create a draft Release. Local builds never create tags or publish releases.

Release notes use the [bilingual template](../.github/release-notes-template.md), with Chinese and English changes in `.github/release-notes/<version>.json`. Update both change lists for the next release. The read-only build job renders `dist/release-notes.md` and uploads it as a workflow artifact; the draft job uses that file, attaching only the four distributable assets. Templates come from the workflow revision so a manually selected older source commit can still use the current format. To render notes without compiling:

```powershell
python tools/render-release-notes.py --version '<approved-version>' --repository 'owner/repo' --source-ref '<exact-source-commit>' --run-url '<successful-CI-run-URL>' --output release-notes.md
```

For a successful manual preview, reuse its ZIPs, checksums and manifest in a draft targeted at the exact packaged source commit. Do not rebuild or replace those files merely to edit notes. Notes describe that source; a later documentation commit does not change the existing binaries. When signing is enabled, update the template's unsigned status as part of that integration.

The updater ignores `+test...` metadata when comparing versions. To test a higher version, override `BuildNumber` only for an isolated fixture build and keep its package outside the public release directory. The immutable `-10` replay harness uses a deliberately artificial fixture version in its temporary project, never a project release; `Test-LegacyUpdater.ps1 -VersionOnly` checks that comparison and actual fixture EXE version without HTTP, replacement or UI. Neither approach edits the production version or existing artifacts.

Both packages retain `LICENSE.txt`, every `Runtime/LICENSE-*.txt` and notice, the native modification statement inside `Runtime/n3n-3.4.4-source.zip`, and the existing TAP source offer. Keep these with the binaries. A public release also provides MikuN2N's own corresponding source through its matching tagged tree. See [third-party notices](../Runtime/THIRD-PARTY-NOTICES.txt) and [native build instructions](../Runtime/BUILD.md).

## Static discovery and verification

The first request is `https://github.com/<owner>/<repo>/releases/latest/download/update.json`. It uses GitHub's release download endpoint without an API token. A usable manifest has:

| Field | Content |
|---|---|
| `schema` | Integer `1`. |
| `version` | Exact project version and release tag, such as `0.5.8-10`. |
| `notes` | Release notes string; may be empty. |
| `pageUrl` | Same repository's HTTPS GitHub release page for that tag. |
| `packages` | Entries for `self-contained` and `framework-dependent`; each has `runtime`, canonical ZIP `name`, versioned official GitHub `url`, byte `size`, and hexadecimal `sha256`. |

Package URLs point to `https://github.com/<owner>/<repo>/releases/download/<version>/<name>`, never a mutable latest-package URL. The client selects its current runtime. Missing, unreachable or unusable manifests fall back to GitHub's latest-release REST API. That path selects the same exact filename and uses GitHub's SHA-256 asset digest, or `SHA256SUMS` when the digest is absent.

A complete valid manifest can determine that no update exists without consulting the API. The client observes source-specific rate limits; a manual check does not override GitHub's retry time. Requests use normal TLS verification. The package hash, size and extracted executable version are checked before offering installation. The installer attempts replacement and rollback; process interruption is not a guarantee of crash-safe recovery.

SHA-256 establishes consistency with the fetched release metadata. It is not an independent publisher signature: someone able to replace both the ZIP and its manifest can replace the hash too. This flow trusts the repository's GitHub release channel and HTTPS.

`UpdateRepository` in the project controls the repository. A fork's direct builds use `-p:UpdateRepository=owner/name`; set the project property before packaging so the generated manifest and binaries agree. `-p:UpdateRepository=none` disables update checks for a direct build and is unsuitable for the public packaging script.

## Compatibility with 0.5.8-10

The already shipped `0.5.8-10` client queries only the GitHub API. A new static manifest cannot remove that client's API limit or make it discover an unpublished release. Keep the exact self-contained filename `MikuN2N-<version>-win-x64.zip`, a regular release tag matching the EXE version, and its asset digest or `SHA256SUMS` for this bridge. The framework-dependent suffix deliberately does not match the old client's ZIP selector.

Once an older client obtains and installs a newer build, future checks can use static discovery. This compatibility preserves discovery and package validation; it does not establish that an installation from the running older EXE succeeds on every Windows setup. If API access or replacement fails, download the chosen package from the release page and close the application before replacing its files.
