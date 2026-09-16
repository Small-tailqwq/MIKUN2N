/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_RELAY_H
#define MIKUN2N_RELAY_H

#include "n2n_typedefs.h"
#include "uthash.h"

#define MIKUN2N_RELAY_MAX 8192
#define MIKUN2N_RELAY_KEY_SIZE (N2N_COMMUNITY_SIZE + 2 * N2N_MAC_SIZE + 1)

enum mikun2n_relay_kind {
    MIKUN2N_RELAY_UNICAST = 1,
    MIKUN2N_RELAY_BROADCAST,
    MIKUN2N_RELAY_FED_UNICAST,
    MIKUN2N_RELAY_FED_FLOOD
};

struct mikun2n_relay_flow {
    /* A byte key avoids struct-padding dependence; community is stored as hex in JSON. */
    uint8_t key[MIKUN2N_RELAY_KEY_SIZE];
    uint64_t bytes;
    uint64_t packets;
    time_t last_seen;
    UT_hash_handle hh;
};

#endif
