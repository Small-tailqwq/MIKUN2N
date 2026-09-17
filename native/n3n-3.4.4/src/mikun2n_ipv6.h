/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_IPV6_H
#define MIKUN2N_IPV6_H

#define MIKUN2N_IPV6_WIRE_VERSION 3

#include <n2n_typedefs.h>
struct peer_info;

uint64_t mikun2n_ipv6_now_ms (void);
int mikun2n_ipv6_public (const n2n_sock_t *address);
int mikun2n_ipv6_candidate (const n2n_sock_t *address);
void mikun2n_ipv6_tick (struct n3n_runtime_data *eee, uint64_t now_ms);
void mikun2n_ipv6_close (struct n3n_runtime_data *eee);
n2n_sock_t mikun2n_ipv6_advertised (const struct n3n_runtime_data *eee, uint64_t now_ms);
void mikun2n_ipv6_update_peer (struct peer_info *peer, const n2n_PEER_INFO_t *info, uint64_t now_ms);
int mikun2n_ipv6_active (const struct n3n_runtime_data *eee, const struct peer_info *peer, uint64_t now_ms);
int mikun2n_ipv6_send (struct n3n_runtime_data *eee, struct peer_info *peer, const uint8_t *data, size_t size, uint64_t now_ms);
size_t mikun2n_ipv6_unwrap (struct n3n_runtime_data *eee, const n2n_sock_t *sender, uint8_t *data, size_t size, uint64_t now_ms);
int mikun2n_ipv6_accept (struct n3n_runtime_data *eee, const n2n_mac_t mac, const n2n_sock_t *sender, uint64_t now_ms);

#endif
