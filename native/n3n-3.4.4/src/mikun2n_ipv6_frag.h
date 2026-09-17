/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_IPV6_FRAG_H
#define MIKUN2N_IPV6_FRAG_H
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "n2n_define.h"

#define MIKUN2N_REASSEMBLY_SLOTS 4
#define MIKUN2N_REASSEMBLY_MS 2000
#define MIKUN2N_REPLAY_WINDOW 1024
typedef struct {
    uint64_t id, expires;
    uint16_t total, received;
    uint8_t data[N2N_PKT_BUF_SIZE], present[N2N_PKT_BUF_SIZE / 8];
} mikun2n_ipv6_fragment_t;
typedef struct {
    mikun2n_ipv6_fragment_t slots[MIKUN2N_REASSEMBLY_SLOTS];
    uint64_t completed_top, completed[MIKUN2N_REPLAY_WINDOW / 64], replay_until;
} mikun2n_ipv6_reassembly_t;

static inline int mikun2n_ipv6_fragment_retired(const mikun2n_ipv6_reassembly_t *state, uint64_t id) {
    if(id > state->completed_top) return 0;
    if(state->completed_top - id >= MIKUN2N_REPLAY_WINDOW) return 1;
    unsigned bit = id % MIKUN2N_REPLAY_WINDOW;
    return (state->completed[bit / 64] & (UINT64_C(1) << (bit % 64))) != 0;
}

/* During a fragment stream, IDs behind the window stay rejected even after bitmap
 * reuse. The original generation-3 two-second idle expiry allows older senders to
 * recreate peer records (and reset their counters) without a permanent blackout. */
static inline void mikun2n_ipv6_fragment_retire(mikun2n_ipv6_reassembly_t *state, uint64_t id) {
    if(id > state->completed_top) {
        uint64_t gap = id - state->completed_top;
        if(gap >= MIKUN2N_REPLAY_WINDOW) memset(state->completed, 0, sizeof(state->completed));
        else for(uint64_t step = 1; step <= gap; ++step) {
            unsigned bit = (state->completed_top + step) % MIKUN2N_REPLAY_WINDOW;
            state->completed[bit / 64] &= ~(UINT64_C(1) << (bit % 64));
        }
        state->completed_top = id;
    } else if(state->completed_top - id >= MIKUN2N_REPLAY_WINDOW) return;
    unsigned bit = id % MIKUN2N_REPLAY_WINDOW;
    state->completed[bit / 64] |= UINT64_C(1) << (bit % 64);
}

/* The caller has already validated the session and the checked source endpoint.
 * Duplicate bytes are accepted only when identical; conflicting overlap discards
 * that assembly. Fixed slots and fixed expiry bound both memory and stale data. */
static inline size_t mikun2n_ipv6_reassemble(mikun2n_ipv6_reassembly_t *state, uint64_t now,
        uint64_t id, uint16_t total, uint16_t offset, const uint8_t *payload,
        size_t length, uint8_t *out) {
    if(!id || !total || total > N2N_PKT_BUF_SIZE || !length ||
       offset >= total || length > (size_t)(total - offset)) return 0;
    if(state->replay_until <= now) {
        state->completed_top = 0;
        memset(state->completed, 0, sizeof(state->completed));
    }
    mikun2n_ipv6_fragment_t *slot = NULL, *free_slot = NULL;
    for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) {
        mikun2n_ipv6_fragment_t *item = &state->slots[i];
        if(item->id && item->expires <= now) {
            mikun2n_ipv6_fragment_retire(state, item->id);
            item->id = 0;
        }
        if(item->id == id) slot = item;
        if(!item->id && !free_slot) free_slot = item;
    }
    // A live assembly keeps its original deadline even when later global IDs
    // advance past the replay window. Retired IDs still cannot open a new slot.
    if(!slot && mikun2n_ipv6_fragment_retired(state, id)) return 0;
    state->replay_until = now + MIKUN2N_REASSEMBLY_MS;
    if(!slot) {
        if(!free_slot) return 0;
        slot = free_slot;
        memset(slot, 0, sizeof(*slot));
        slot->id = id; slot->total = total; slot->expires = now + MIKUN2N_REASSEMBLY_MS;
    }
    if(slot->total != total) { mikun2n_ipv6_fragment_retire(state, id); slot->id = 0; return 0; }
    for(size_t i = 0; i < length; ++i) {
        size_t pos = offset + i;
        uint8_t mask = 1u << (pos & 7);
        if(slot->present[pos >> 3] & mask) {
            if(slot->data[pos] != payload[i]) { mikun2n_ipv6_fragment_retire(state, id); slot->id = 0; return 0; }
        } else {
            slot->present[pos >> 3] |= mask;
            slot->data[pos] = payload[i];
            ++slot->received;
        }
    }
    if(slot->received != total) return 0;
    memcpy(out, slot->data, total);
    mikun2n_ipv6_fragment_retire(state, id);
    slot->id = 0;
    return total;
}
#endif
