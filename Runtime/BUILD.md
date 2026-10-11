# Native build and corresponding source

> [English](BUILD.md) | [简体中文](BUILD.zh.md)

## Windows edge

From the root of the extracted `n3n-3.4.4-source.zip`, run the following in Git Bash with MinGW-w64 GCC and make on `PATH`:

```sh
sh scripts/build-mikun2n-windows.sh
```

Windows `CFLAGS` require `-std=gnu17`: the bundled old `src/win32/getopt.c` does not compile under this toolchain's C23 default. Retain `-ffile-prefix-map=<build-path>=<placeholder-path>` to replace personal paths in debug information, and check the rebuilt binary for any remaining personal paths.

## Source packaging

In the MikuN2N repository, the patched tree is `native/n3n-3.4.4/`. Before a new native release build, run `tools/sync-native-version.ps1` to synchronize its build identity. The supplied archive retains its included identity when rebuilt separately.

```text
python tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>
```

The source exporter excludes generated `config.mak`, `configure`, headers and build outputs; the build scripts recreate them. The archive must match the tracked native source bytes. Its root `MIKUN2N-MODIFICATIONS.md` identifies the modifier, date and scope of changes. Keep component and source hashes synchronized in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

The upstream source is [n42n/n3n, tag 3.4.4](https://github.com/n42n/n3n/tree/3.4.4). Bilingual implementation records are under [changes/](changes/README.md); exact source differences come from Git version comparisons.

## Linux supernode

Use `supernode/build-supernode.sh` from the MikuN2N source release. It runs `autogen.sh` before `configure`. Deployment remains a separate manual operation described in the source release's `supernode/README.md`.
