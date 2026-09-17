/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_IPV6_FRAG_H
#define MIKUN2N_IPV6_FRAG_H
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "n2n_define.h"

#define MIKUN2N_REASSEMBLY_SLOTS 4
#define MIKUN2N_REASSEMBLY_MS 2000
typedef struct {
    uint64_t id, expires;
    uint16_t total, received;
    uint8_t data[N2N_PKT_BUF_SIZE], present[N2N_PKT_BUF_SIZE / 8];
} mikun2n_ipv6_fragment_t;
typedef struct {
    mikun2n_ipv6_fragment_t slots[MIKUN2N_REASSEMBLY_SLOTS];
    uint64_t completed[64], completed_until[64];
    unsigned next_completed;
} mikun2n_ipv6_reassembly_t;

/* The caller has already validated the session and the checked source endpoint.
 * Duplicate bytes are accepted only when identical; conflicting overlap discards
 * that assembly. Fixed slots and fixed expiry bound both memory and stale data. */
static inline size_t mikun2n_ipv6_reassemble(mikun2n_ipv6_reassembly_t *state, uint64_t now,
        uint64_t id, uint16_t total, uint16_t offset, const uint8_t *payload,
        size_t length, uint8_t *out) {
    if(!id || !total || total > N2N_PKT_BUF_SIZE || !length ||
       offset >= total || length > (size_t)(total - offset)) return 0;
    for(unsigned i = 0; i < 64; ++i)
        if(state->completed[i] == id && state->completed_until[i] > now) return 0;
    mikun2n_ipv6_fragment_t *slot = NULL, *free_slot = NULL;
    for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) {
        mikun2n_ipv6_fragment_t *item = &state->slots[i];
        if(item->expires <= now) item->id = 0;
        if(item->id == id) slot = item;
        if(!item->id && !free_slot) free_slot = item;
    }
    if(!slot) {
        if(!free_slot) return 0;
        slot = free_slot;
        memset(slot, 0, sizeof(*slot));
        slot->id = id; slot->total = total; slot->expires = now + MIKUN2N_REASSEMBLY_MS;
    }
    if(slot->total != total) { slot->id = 0; return 0; }
    for(size_t i = 0; i < length; ++i) {
        size_t pos = offset + i;
        uint8_t mask = 1u << (pos & 7);
        if(slot->present[pos >> 3] & mask) {
            if(slot->data[pos] != payload[i]) { slot->id = 0; return 0; }
        } else {
            slot->present[pos >> 3] |= mask;
            slot->data[pos] = payload[i];
            ++slot->received;
        }
    }
    if(slot->received != total) return 0;
    memcpy(out, slot->data, total);
    unsigned done = state->next_completed++ % 64;
    state->completed[done] = id;
    state->completed_until[done] = now + MIKUN2N_REASSEMBLY_MS;
    slot->id = 0;
    return total;
}
#endif
