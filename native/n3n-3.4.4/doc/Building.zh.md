Copyright (C) 2023 Hamish Coleman and other contributors
SPDX-License-Identifier: GPL-3.0-only

> [English](Building.md) | [简体中文](Building.zh.md)
>
> 译注：构建 MikuN2N 随附的 Windows 版 edge，请按 MikuN2N 仓库 README 中的步骤，在 Git Bash 里运行 `sh scripts/build-mikun2n-windows.sh`；在 Linux 上构建配套的 supernode，请使用 MikuN2N 仓库中的 `supernode/build-supernode.sh`。下文是上游通用的构建说明。

本文介绍在几种不同场景下编译 n3n 的方法。

构建过程中有一些可用的配置选项，记录在[构建时配置](BuildConfig.md)（英文）页面中。

自动化持续集成所用的步骤也很有参考价值，见 [Github Actions 配置文件](../.github/workflows/tests.yml)。

项目的 _main_ 分支用于开发工作，反映的是预计进入下一个版本的代码，因此它可能没有经过充分测试，可能包含缺陷或尚未完成的功能。如果你想帮忙测试或实现新功能，欢迎从 _main_ 编译。也欢迎在 _issues_ 中反馈。

某个版本稳定后会打上 tag；如果需要把缺陷修复移植回某个稳定版，会为补丁版本创建一个分支，其中包含这些移植回去的修复。

# Git 子模块

如果编译时启用了 UPnP 功能，你的操作系统或构建系统可能没有提供所需库的二进制文件。

使用这些库可能会在某些构建系统上引起问题，所以请注意，并非所有组合都能得到支持。

为了简化这种情况，所需的源代码已经以 git `submodules`（子模块）的形式加入本仓库，需要多执行一步才能完整检出。

只有在运行 configure 时启用了任一 UPnP 库的选项，并且目标系统无法以二进制软件包的形式提供这些库时，才需要这一步。

最常见的就是 Windows，不过构建系统目前还不能自动检测这种情况并启用正确的功能来包含这些库。此外，libpmp 需要上游修改，才能在 Windows 上用 GCC 正常编译。这些问题应该会在以后的版本中解决。

如果你要使用这些功能，最简单的做法是：第一次克隆 n3n 的 git 仓库后，在 n3n 目录中运行下面的命令获取子模块：

```bash
git submodule update --init --recursive
```

> 译注：MikuN2N 仓库中的 `native/n3n-3.4.4` 来自上游的源码归档，不包含这两个子模块。

# 在 Linux 上构建 / 通用构建说明

在具备标准 POSIX 工具和开发库的系统上，从源码编译很简单：

```sh
./autogen.sh
./configure
make

# optionally install
make install
```

# 在 macOS 上构建

macOS 基本上可以使用上面的通用构建说明，但需要先安装几个软件包：

```bash
brew install automake
```

然后安装 TUN/TAP 网卡支持：

```bash
brew tap homebrew/cask
brew cask install tuntap
```

如果你使用的是较新版本的 macOS（例如 Catalina），上面的命令会要求你在 系统偏好设置 → 安全性与隐私 → 通用 中允许 TUN/TAP 内核扩展。

更多信息请参考厂商文档或 [Apple 技术说明](https://developer.apple.com/library/content/technotes/tn2459/_index.html)。

注意，在最新的 macOS 版本以及 Apple Silicon 上，系统的安全限制越来越多，安装 TUN/TAP 内核扩展可能会比较困难。未来的 n3n 版本正在讨论用其他软件实现来绕开这些困难。

# 在 BSD 上构建

## FreeBSD

基本上就是使用上面的通用构建说明，外加安装几个必需的软件包：

```bash
sudo pkg install -y \
  autoconf \
  automake \
  git-tiny \
  gmake \
  python3 \
  jq \
  bash
./autogen.sh
./configure CC=clang
gmake all
```

## OpenBSD

同样基本是通用构建说明，外加一些系统软件包：

```bash
sudo pkg_add \
  autoconf-2.71 \
  automake-1.16.5 \
  git \
  gmake \
  python3 \
  jq \
  bash
AUTOCONF_VERSION=2.71 AUTOMAKE_VERSION=1.16 ./autogen.sh
./configure CC=clang
gmake all
```

# 在 Windows 上构建

下面记录的是一种可行的 Windows 编译方法。之所以使用 MinGW 构建流程，是因为它对开源开发更友好。

## MinGW

这些步骤在一台全新安装、已打上截至 2021-09-29 所有补丁的 Windows 10 专业版上测试过。

- 安装 Chocolatey（按照 https://chocolatey.org/install 上的说明）
- 在管理员命令提示符中执行：
    - `choco install git mingw make`
- 安装好 git 软件包后，开始菜单中会出现一个名为"Git Bash"的新项目。其余所有命令都必须在该菜单项启动的 shell 中运行：
    - `git clone $THIS_REPO`
    - `cd n3n`
    - `./scripts/hack_fakeautoconf.sh`
    - `make`
    - `make test`

由于 Windows 环境的限制，常规的 autotools 步骤由 `hack_fakeautoconf` 模拟完成。

注意，目前在 Windows 上带 UPnP 库构建还需要一些手动操作。

## 在 Windows 上运行

要在 Windows 上运行 n3n，需要：

- 在系统中安装 TAP 驱动。可以从 http://build.openvpn.net/downloads/releases 下载安装，搜索"tap-windows"即可。

- 如果 OpenSSL 是动态链接的，目标电脑上需要有对应的 `.dll` 文件。

如果没有指定会话名选项，`edge.exe` 程序会读取 `%USERPROFILE%\n3n\edge.conf` 文件。

如果没有指定会话名选项，`supernode.exe` 程序会读取 `%USERPROFILE%\n3n\supernode.conf` 文件。

提供了 [edge.conf](edge.conf.sample) 和 [supernode.conf](supernode.conf.sample) 两个示例。

完整的可用选项列表请查看 `edge.exe --help` 和 `supernode.exe --help`。

# 在 Linux 上交叉编译

## 使用 Makefile 和 Autoconf

所有 Makefile 都已设置好，支持交叉编译这份代码。你需要为目标架构安装交叉编译器、binutils 以及所需的其他库，然后运行 `./configure` 并加上合适的 `--host` 选项。

在 Debian 或 Ubuntu 上编译时，可以像下面这个例子一样简单：

```
HOST_TRIPLET=arm-linux-gnueabi
sudo apt-get install binutils-$HOST_TRIPLET gcc-$HOST_TRIPLET
./autogen.sh
./configure --host $HOST_TRIPLET
make
```

想确定目标平台的 host 三元组，一个好办法是把 `./scripts/config.guess` 脚本复制到目标平台上运行。

对于嵌入式环境（例如 OpenWRT），这不是生成二进制文件的好办法，因为它们通常使用不同的 libc 环境。

# 构建 n3n 软件包

源码中还附带了一些打包示例：

- Debian：`make dpkg`
- [RPM](../packages/rpm)
- [OpenWRT](../packages/openwrt/README.md)（英文）
