/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_ASSIST_H
#define MIKUN2N_ASSIST_H

#include <n2n_typedefs.h>

#define MIKUN2N_ASSIST_VERSION 1
#define MIKUN2N_ASSIST_ENDPOINT 1
#define MIKUN2N_ASSIST_RELAY 2
#define MIKUN2N_ASSIST_STANDBY 3

typedef struct mikun2n_assist_rx_context {
    uint8_t valid;
    uint8_t circuit[16];
    n2n_mac_t source, destination;
    uint64_t sequence, created_ms;
    size_t inner_bytes;
} mikun2n_assist_rx_context_t;

typedef enum mikun2n_assist_delivery_result {
    MIKUN2N_ASSIST_INNER_ACCEPTED,
    MIKUN2N_ASSIST_TAP_ATTEMPT,
    MIKUN2N_ASSIST_TAP_SUBMITTED,
    MIKUN2N_ASSIST_REJECT_HEADER,
    MIKUN2N_ASSIST_REJECT_COMMON,
    MIKUN2N_ASSIST_REJECT_TYPE,
    MIKUN2N_ASSIST_REJECT_COMMUNITY,
    MIKUN2N_ASSIST_REJECT_PACKET,
    MIKUN2N_ASSIST_REJECT_MAC,
    MIKUN2N_ASSIST_REJECT_TIMESTAMP,
    MIKUN2N_ASSIST_REJECT_UNREGISTERED,
    MIKUN2N_ASSIST_REJECT_TRANSFORM,
    MIKUN2N_ASSIST_REJECT_PAYLOAD,
    MIKUN2N_ASSIST_REJECT_FILTER,
    MIKUN2N_ASSIST_REJECT_TAP
} mikun2n_assist_delivery_result_t;

const char *mikun2n_assist_rejection_name (unsigned reason);

int mikun2n_assist_set (struct n3n_runtime_data *eee, const uint8_t *circuit,
                       uint8_t role, const n2n_mac_t first, const n2n_mac_t second,
                       int enabled, uint64_t now_ms);
int mikun2n_assist_active (struct n3n_runtime_data *eee, const mikun2n_assist_t *assist, uint64_t now_ms);
void mikun2n_assist_snapshot (struct n3n_runtime_data *eee, unsigned index, mikun2n_assist_t *snapshot);
int mikun2n_assist_any (struct n3n_runtime_data *eee);
int mikun2n_assist_peer_active (struct n3n_runtime_data *eee, const n2n_mac_t mac, uint64_t now_ms);
void mikun2n_assist_tick (struct n3n_runtime_data *eee, uint64_t now_ms);
int mikun2n_assist_send (struct n3n_runtime_data *eee, const n2n_mac_t dest,
                        const uint8_t *data, size_t size, uint64_t now_ms);
/* 0 ordinary packet, -1 consumed/rejected, 1 validated inner data packet. */
int mikun2n_assist_receive (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                           SOCKET socket_fd, uint8_t *data, size_t *size,
                           n2n_mac_t source, mikun2n_assist_rx_context_t *context, uint64_t now_ms);
void mikun2n_assist_delivery (struct n3n_runtime_data *eee,
                             const mikun2n_assist_rx_context_t *context,
                             mikun2n_assist_delivery_result_t result, size_t bytes, uint64_t now_ms);

/* These never use supernode or another assistance circuit. */
int mikun2n_assist_direct_ready (struct n3n_runtime_data *eee, const n2n_mac_t mac, uint64_t now_ms);
/* 0: not consumed; 1: every UDP datagram submitted; 2: consumed with incomplete submission. */
int mikun2n_assist_direct_send (struct n3n_runtime_data *eee, const n2n_mac_t mac,
                               const uint8_t *data, size_t size, uint64_t now_ms);
int mikun2n_assist_direct_accept (struct n3n_runtime_data *eee, const n2n_mac_t mac,
                                 const n2n_sock_t *sender, SOCKET socket_fd, uint64_t now_ms);
int mikun2n_assist_forced_relay (struct n3n_runtime_data *eee, const n2n_mac_t mac);

#endif
