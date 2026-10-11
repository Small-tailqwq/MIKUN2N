# 原生构建与对应源码

> [English](BUILD.md) | [简体中文](BUILD.zh.md)

## Windows edge

在解压的 `n3n-3.4.4-source.zip` 根目录，通过 Git Bash 运行以下命令，确保 MinGW-w64 GCC 与 make 位于 `PATH`：

```sh
sh scripts/build-mikun2n-windows.sh
```

Windows 的 `CFLAGS` 必须包含 `-std=gnu17`，因为随包的旧 `src/win32/getopt.c` 无法使用本工具链默认的 C23 标准编译。保留 `-ffile-prefix-map=<build-path>=<placeholder-path>` 来替换调试信息中的个人路径，并检查重编后的二进制是否还有路径残留。

## 源码打包

MikuN2N 仓库中的补丁树为 `native/n3n-3.4.4/`。新的原生发布构建前运行 `tools/sync-native-version.ps1`，同步构建身份；单独重建随包源码归档时保留其内含身份。

```text
python tools/package-native-source.py --source native/n3n-3.4.4 --archive Runtime/n3n-3.4.4-source.zip --report <validation.json>
```

导出器排除生成的 `config.mak`、`configure`、头文件和构建产物，由构建脚本重新生成。归档必须与受跟踪原生源码逐字节匹配，根目录的 `MIKUN2N-MODIFICATIONS.md` 标明修改者、日期和改动范围。在 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 中同步组件与源码哈希。

上游源码为 [n42n/n3n 的 3.4.4 tag](https://github.com/n42n/n3n/tree/3.4.4)。双语实现记录位于 [changes/](changes/README.zh.md)，精确源码差异通过 Git 版本比较查看。

## Linux supernode

使用 MikuN2N 源码发行版中的 `supernode/build-supernode.sh`，它在 `configure` 前运行 `autogen.sh`。部署仍是独立的手工操作，参见源码发行版中的 `supernode/README.md`。
