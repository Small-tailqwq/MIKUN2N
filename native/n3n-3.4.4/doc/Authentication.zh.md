# Edge 认证

> [English](Authentication.md) | [简体中文](Authentication.zh.md)

在讨论如何防止 MAC 地址伪造时，人们意识到 edge 需要认证。实际上，REGISTER_SUPER 类型的消息中早就有一个一直没用上的 `auth` 字段，从 n3n 2.9 版本的开发过程中开始首次使用它。

## 实现

n3n 实现了两种不同的认证方案，供用户选择。

### 基于身份标识的方案

这是一种基于唯一标识号的非常基础的认证方案。这个 ID 在 edge 启动时随机生成，直到 edge 退出前都保持不变。每次发送 REGISTER_SUPER 时，ID 都会一并发给 supernode；supernode 记住第一次 REGISTER_SUPER 中的 ID，并与之后收到的每一次进行比对。这是一种"出示 ID"式的验证。只有验证通过，supernode 才会接受 REGISTER_SUPER（因而也才接受 MAC 地址或网络套接字的变化）和 UNREGISTER 类型的消息。

这不仅能防止 UNREGISTER 攻击，还能防止 MAC 伪造，即使有多台联邦 supernode 也一样，因为每条 REGISTER_SUPER 消息都会转发给其他所有 supernode。如果一个新 edge（有意或无意地）试图占用另一个 edge 已在使用的 MAC 地址，由于新 edge 出示的认证 ID 不同，这会被识别为未经授权的 MAC 变更。拒绝它的 supernode（因为比对失败）会向这个新 edge，以及新 edge 试图通过其注册的那台 supernode，发送一条 REGISTER_SUPER_**NAK** 消息。收到 REGISTER_SUPER_NAK 后，edge 会输出一条 ERROR 信息，但为了防止"强制下线"攻击，它不会再因此停止运行。

MAC 地址会随每个数据包发送，edge 之间也能看到；而这个 ID 只在 edge 和 supernode 之间传递，其他 edge 并不知道，因此也无法伪造。

如果攻击者费些功夫通过网络抓包观察到了认证 ID，这个方案就会被攻破。因此计划进一步开发更完善的、基于密码学的认证方案。

如果 edge 意外关闭、没能正常退出，在 supernode 内部把它从列表中移除之前（大约 90 秒后），这个认证方案会阻止它重新连接 supernode。虽然可以在 supernode 上使用 `-M` 命令行选项关闭认证 ID 比对来绕过这个问题，但更推荐改用基于用户名和密码的认证方案。

### 基于用户名和密码的认证

更进一步的方案依赖用户名，尤其是密码，并用公钥密码学（具体是 Curve25519）来保证安全。简单来说，密码加上混入的用户名，就相当于私钥。对应的公钥由 `edge tools keygen` 工具生成，生成的公钥存放在 supernode 上。

#### 准备 Supernode

要为用户 `logan` 及其非常机密的密码 `007` 生成公钥，可以把用户名和密码作为命令行参数调用 `edge tools keygen`：

```bash
[user@machine n3n]$ edge tools keygen logan 007
* logan nHWum+r42k1qDXdIeH-WFKeylK5UyLStRzxofRNAgpG
```

生成的这一行格式为 `* <用户名> <公钥>`，需要复制到 supernode 的小组列表文件中，例如下面这个示例 `community.list` 文件：

```
#
# List of allowed communities
# ---------------------------
#
#      these could either be fixed-name communities such as the following lines ...
#
mynetwork
netleo
* logan nHWum+r42k1qDXdIeH-WFKeylK5UyLStRzxofRNAgpG
* sister HwHpPrdMft+38tFDDiunUds6927t0+zhCMMkQdJafcC
#
#      ... or regular expressions that a community name must fully match
#      such as ntop[0-1][0-9] for communities from "ntop00" through "ntop19"
# 
ntop[0-1][0-9]

   ...
```

这个例子中还列出了另一个用户 `sister`（密码同样是 `007`）。这些用户属于它们上方的小组名称，这里是 `netleo`。公钥在密码学上只与用户名绑定，与小组名称无关。所以要把某个用户从一个小组转到另一个小组，只需把对应的那一行从一个小组段落复制到另一个即可。顺便提醒，别忘了通过 `supernode.community_file` 选项把 `community.list` 文件提供给 supernode。

目前 supernode 不限制同一用户名的同时使用，也就是说一个用户名可以同时被多个 edge 使用。不过建议每个 edge 或每台电脑使用各自不同的用户名和密码。这样管理接口的输出会有意义得多，`HINT` 列会显示对应的用户名。另外，自动分配 IP 地址（即 edge 不使用静态 IP）时，也更可能分到互不相同的地址，因为分配结果与用户名有关。

如果某个用户换了新密码，或者需要禁止其访问小组（例如 edge 所在的设备被盗），只要把 `community.list` 中对应的那一行替换为新生成的行，或者直接删除即可。修改之后，需要重启 supernode，或向管理端口发送 `reload_communities` 命令，让 supernode 重新读取这些数据。

在整个联邦范围内（即跨多台 supernode）使用这项功能时，请确保所有 supernode 的 `community.list` 文件保持同步。也就是说，在一台 supernode 上删除、修改（或新增）某个用户时，所有 supernode 上都要做同样的操作。联邦内部没有内置的 `community.list` 同步机制。可以借助 _Syncthing_ 这类外部工具，或你自己写的基于 scp 分发文件的脚本。同样，每次修改后都需要按上文所述，重启 supernode 或向管理接口发送 `reload_communities` 命令。

考虑到下文的详细说明，你的 supernode 应当通过 `supernode.federation` 配置选项设置一个非默认的联邦名称。它用来在 supernode 一侧派生私钥，只应在 supernode 之间共享。


#### Edge 一侧

edge 使用 `connection.description` 选项作为用户名。这个选项默认为本机主机名，可能不适合你的环境。密码用 `auth.password` 选项设置。

接着上面的例子，可以这样启动 edge：

```
[user@host n3n]$ sudo ./n3n-edge \
    start \
    -l <supernode:port> \
    -c netleo \
    -Oconnection.description=logan \
    -Oauth.password=007 \
    <any additional parameters>
```

注意，由于这个认证方案高度依赖头部加密，头部加密会自动启用。另外，目前只有流密码能在这个认证方案下可靠地保证安全。所以还需要额外提供 `-Ocommunity.cipher=ChaCha20` 或 `-Ocommunity.cipher=Speck` 以及密钥 `-k <key>` 这两个参数。

edge 需要知道 supernode 的公钥。默认情况下，edge 假定 supernode 使用默认的联邦名称，确切地说是使用与之对应的公钥。如果 supernode 设置了自定义的联邦名称（强烈建议这样做），就要通过 `auth.pubkey` 配置选项把 supernode 的公钥提供给 edge。这个公钥同样可以用 `edge tools keygen` 工具从联邦名称生成：

```bash
[user@host n3n]$ edge tools keygen secretFed
auth.pubkey=opIyaWhWjKLJSNOHNpKnGmelhHWRqkmY5pAx7lbDHp4
```

综上，示例扩展为：

```
[user@host n3n]$ sudo ./n3n-edge \
    start \
    -l <supernode:port> \
    -c netleo \
    -Oconnection.description=logan \
    -Oauth.password=007 \
    -Oauth.pubkey=opIyaWhWjKLJSNOHNpKnGmelhHWRqkmY5pAx7lbDHp4 \
    -Ocommunity.cipher=Speck \
    -k mySecretKey
```

可以考虑使用 [`.conf` 文件](ConfigurationFiles.zh.md)，更方便地容纳这么多命令行参数。另外，也可以用环境变量 `N3N_PASSWORD` 设置密码，避免密码出现在命令行中。


#### 工作原理

为了让这个认证方案生效，原有的头部加密被拆分为使用两个密钥：一个_静态_密钥和一个_动态_密钥。静态密钥保持不变，就是由小组名称派生出来的[经典头部加密密钥](Crypto.md#header)（英文）。它只用于 edge 与 supernode 之间最基本的注册流量（REGISTER_SUPER、REGISTER_SUPER_ACK、REGISTER_SUPER_NAK）。动态密钥（部分地）由联邦名称派生——请务必保密！——用于其他所有数据包，尤其是数据包（PACKET）和建立点对点连接的数据包（REGISTER），也包括 ping 和对端信息（QUERY_PEER、PEER_INFO）。没有拿到有效动态密钥的 edge，无法参与后续的通信。

在普通的头部加密模式下，静态密钥和动态密钥是相同的。启用用户名密码方案后，supernode 会生成一个动态密钥，通过 REGISTER_SUPER 的应答发给 edge 供后续使用。这个过程基于公钥密码学，是安全的。未通过认证的 edge（即 supernode 上没有对应条目，或凭据无效），拿不到可用于注册之后通信的有效动态密钥。

在用户名密码方案下，用静态密钥加密的数据包（REGISTER_SUPER、REGISTER_SUPER_ACK；对 REGISTER_SUPER_NAK 没有意义）会附带一个加密的外层哈希作为"签名"，所用的共享密钥只有联邦中的 supernode 和该 edge 知道。

#### 可能的扩展

如果有工具能为新注册并获批的用户自动生成、分发 [`.conf` 文件](ConfigurationFiles.zh.md)，将会大大完善这套生态；用户就不必折腾命令行参数，只需把一个 `.conf` 文件拷进指定目录即可。

如果你有兴趣实现或推进这些想法，请告诉我们。
