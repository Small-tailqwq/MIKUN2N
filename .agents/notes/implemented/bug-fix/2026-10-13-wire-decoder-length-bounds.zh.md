# Agent Note: 限制 n3n wire 解码器中来自链路的长度

Status: implemented

> [English](2026-10-13-wire-decoder-length-bounds.md) | [简体中文](2026-10-13-wire-decoder-length-bounds.zh.md)

## 问题

n3n 的 wire 解码器把报文中的长度字段直接当作拷贝长度，从不检查目标容量。`decode_buf` 只确认源数据还剩足够字节，于是来自链路的 `auth.token_size`（`uint16`）会写满固定的 48 字节 `auth.token` 字段。单个未认证 UDP 报文就能覆盖 `process_udp` 栈上约 2 KB 的内容；线上 supernode 正是被这样的输入打死，glibc 的栈破坏检测终止了进程；没有这项检测，同一次写入会静默破坏栈。

只在解码器内校验长度会留下两条路径。`process_udp` 在这些调用点忽略解码器的返回值，被拒绝的报文仍会进入转发流程，而编码器会按同一个 `auth.token_size` 从这块 48 字节数组里读出同样多的字节。

## 决策

`src/wire.c` 在使用来自链路的长度作为定长目标的拷贝长度之前，先校验它。

- `decode_REGISTER_SUPER`、`decode_UNREGISTER_SUPER`、`decode_REGISTER_SUPER_ACK` 与 `decode_REGISTER_SUPER_NAK` 拒绝超过 `N2N_AUTH_MAX_TOKEN_SIZE` 的 `auth.token_size`。
- `decode_REGISTER_SUPER_ACK` 拒绝字节数超过 `REG_SUPER_ACK_PAYLOAD_SPACE` 的备份 supernode 条目数。
- 拒绝时先清零出错的字段再返回 `-1`，使忽略返回值的调用方无法按被拒绝的长度重新编码。
- `process_udp` 在 `decode_REGISTER_SUPER` 或 `decode_UNREGISTER_SUPER` 返回负值时丢弃该报文。

MAC、小组名、套接字、描述与版本等长度在编译期确定的拷贝继续原样使用 `decode_buf`。

## 曾考虑的替代方案

**给 `decode_buf` 增加目标容量参数。**集中处理边界，但会改动被所有解码器和所有定长拷贝共用的辅助函数，而这个风险只存在于长度来自链路的调用点；在调用点校验能把改动面和评审面保持在最小。若以后出现更多来自链路的长度，再重新考虑。

**在编码器里夹住长度。**只保护重新编码的路径，解码器仍会先把数据写到字段之外，溢出依旧存在。

**只依赖调用方检查解码器返回值。**`process_udp` 在其它解码调用点忽略返回值，例如 `decode_PACKET` 与 `decode_QUERY_PEER`，而且注册报文的转发副本是在解码器之外编码的，第二次读取仍然无界。

## 后果

超长令牌长度与超长备份 supernode 数组在任何拷贝发生前就被丢弃，编码器只会在令牌数组内部读取。合法报文不受影响：0 到 48 的 `token_size` 与空备份列表照常解码，解码器对合法报文报告的字节数与之前一致。

这些校验表达的是一个字段与一个数组之间的固定关系，因此将来新增带链路长度的字段需要各自的边界。两个注册解码器的负返回值现在承担了实际作用：`process_udp` 中新增的解码调用点必须继续处理它。
