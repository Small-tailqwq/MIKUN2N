[中文](#中文) | [English](#english)

## 中文

### 本次改进

{{CHANGES_ZH}}

### 下载与安装

| Windows x64 包 | 适用情况 |
|---|---|
| `MikuN2N-{{VERSION}}-win-x64.zip` | 自包含，内含 .NET 运行时，适合大多数玩家。 |
| `MikuN2N-{{VERSION}}-win-x64-framework-dependent.zip` | 下载更小，需要先安装 [.NET 9 桌面运行时 x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)。 |

下载所选 ZIP，解压并保留完整目录，再运行 `MikuN2N.exe`。首次使用且没有 TAP 网卡时，在程序中安装网络组件。客户端不内置服务器，请添加自己的节点；联机双方使用相同的小组名称和密钥。

### 程序内更新

客户端优先读取 GitHub 的静态更新清单，失败时回退到 API；更新保持当前包类型，并校验下载大小、SHA-256 和程序版本。下载就绪后，由你选择立即安装或稍后处理；安装会断开连接并重启程序，之后需要重新连接。只有公开发布的正式 Release 才能被正常发现。{{VERSION_NOTE_ZH}}

### 构建与许可

[对应源码]({{SOURCE_URL}}) · [CI 构建]({{BUILD_URL}})

两个 ZIP 均附 GPL-3.0-only 许可证、第三方声明、配套 n3n 源码及 TAP 源码提供承诺；`SHA256SUMS` 和 `update.json` 对应随附 ZIP。当前 `MikuN2N.exe` 未签名，哈希用于核对文件一致性。

诊断上传默认关闭，授权后才上传到选定节点；远端保留期由接收服务商决定。详见[隐私]({{DOC_URL}}/PRIVACY.md)、[网络改动与清理]({{DOC_URL}}/UNINSTALL.md)及[签名计划]({{DOC_URL}}/CODE_SIGNING.md)。

---

## English

### Changes

{{CHANGES_EN}}

### Download and install

| Windows x64 package | Choose this when |
|---|---|
| `MikuN2N-{{VERSION}}-win-x64.zip` | You want the included .NET runtime; recommended for most players. |
| `MikuN2N-{{VERSION}}-win-x64-framework-dependent.zip` | You want a smaller download and have installed the [.NET 9 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/9.0). |

Download your ZIP, extract the complete directory and run `MikuN2N.exe`. If no TAP adapter is installed, use the client's network-component installer. Add your own node: no servers are preconfigured. Players must use the same community and key. The current interface is in Simplified Chinese.

### In-app updates

The client tries GitHub's static update manifest first and falls back to the API. Updates keep your package flavor and check the download size, SHA-256 and executable version. Once a download is ready, choose when to install. Installation disconnects the virtual network and restarts the client; reconnect afterward. Normal discovery only sees published regular releases.{{VERSION_NOTE_EN}}

### Build and licenses

[Corresponding source]({{SOURCE_URL}}) · [CI build]({{BUILD_URL}})

Both ZIPs include the GPL-3.0-only license, third-party notices, matching n3n source and TAP source offer. `SHA256SUMS` and `update.json` describe the attached ZIPs. `MikuN2N.exe` is currently unsigned; hashes establish file consistency.

Diagnostic upload is off by default and requires authorization for the selected node. The receiving provider determines remote retention. See [privacy]({{DOC_URL}}/PRIVACY.md), [network changes and removal]({{DOC_URL}}/UNINSTALL.md) and the [signing plan]({{DOC_URL}}/CODE_SIGNING.md).
