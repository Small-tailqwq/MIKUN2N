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

/* TAP submission and receive-side forwarding can update one peer concurrently.
 * Relaxed atomics preserve counts; a summary is an observation, not a transaction. */
static inline uint64_t mikun2n_v6_diag_read(const uint64_t *value) {
    return __atomic_load_n(value, __ATOMIC_RELAXED);
}
static inline void mikun2n_v6_diag_add(uint64_t *value, uint64_t amount) {
    __atomic_fetch_add(value, amount, __ATOMIC_RELAXED);
}
static inline void mikun2n_v6_diag_max(uint64_t *value, uint64_t candidate) {
    uint64_t before = mikun2n_v6_diag_read(value);
    while(before < candidate && !__atomic_compare_exchange_n(value, &before, candidate, 0,
            __ATOMIC_RELAXED, __ATOMIC_RELAXED)) { }
}
static inline void mikun2n_v6_diag_min(uint64_t *value, uint64_t candidate) {
    uint64_t before = mikun2n_v6_diag_read(value);
    while((!before || before > candidate) && !__atomic_compare_exchange_n(value, &before, candidate, 0,
            __ATOMIC_RELAXED, __ATOMIC_RELAXED)) { }
}

typedef struct {
    /* started/completed/expired count assemblies; waiting/rejections count input
     * fragments. Identical duplicates and normal waiting are not packet loss. */
    uint64_t started, completed, waiting, expired, expired_missing_bytes;
    uint64_t slot_full, duplicate, overlap, conflicting, invalid, retired, retired_window;
    uint64_t slots_current, slots_highwater, reset_unfinished, reset_missing_bytes;
    uint64_t last_reject_id, last_reject_offset, last_reject_length;
    uint64_t last_expired_id, last_expired_received, last_expired_total;
} mikun2n_ipv6_frag_diag_t;
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

static inline void mikun2n_ipv6_fragment_diag_slots(const mikun2n_ipv6_reassembly_t *state,
        mikun2n_ipv6_frag_diag_t *diag) {
    if(!diag) return;
    uint64_t current = 0;
    for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) current += state->slots[i].id != 0;
    __atomic_store_n(&diag->slots_current, current, __ATOMIC_RELAXED);
    mikun2n_v6_diag_max(&diag->slots_highwater, current);
}

static inline void mikun2n_ipv6_fragment_diag_reject(mikun2n_ipv6_frag_diag_t *diag,
        uint64_t id, uint16_t offset, size_t length) {
    if(!diag) return;
    __atomic_store_n(&diag->last_reject_id, id, __ATOMIC_RELAXED);
    __atomic_store_n(&diag->last_reject_offset, offset, __ATOMIC_RELAXED);
    __atomic_store_n(&diag->last_reject_length, length, __ATOMIC_RELAXED);
}

/* The caller has already validated the session and the checked source endpoint.
 * Duplicate bytes are accepted only when identical; conflicting overlap discards
 * that assembly. Fixed slots and fixed expiry bound both memory and stale data. */
static inline size_t mikun2n_ipv6_reassemble_ex(mikun2n_ipv6_reassembly_t *state, uint64_t now,
        uint64_t id, uint16_t total, uint16_t offset, const uint8_t *payload,
        size_t length, uint8_t *out, mikun2n_ipv6_frag_diag_t *diag) {
    if(!id || !total || total > N2N_PKT_BUF_SIZE || !length ||
       offset >= total || length > (size_t)(total - offset)) {
        if(diag) mikun2n_v6_diag_add(&diag->invalid, 1);
        mikun2n_ipv6_fragment_diag_reject(diag, id, offset, length);
        return 0;
    }
    if(state->replay_until <= now) {
        state->completed_top = 0;
        memset(state->completed, 0, sizeof(state->completed));
    }
    mikun2n_ipv6_fragment_t *slot = NULL, *free_slot = NULL;
    for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) {
        mikun2n_ipv6_fragment_t *item = &state->slots[i];
        if(item->id && item->expires <= now) {
            if(diag) {
                mikun2n_v6_diag_add(&diag->expired, 1);
                mikun2n_v6_diag_add(&diag->expired_missing_bytes, item->total - item->received);
                __atomic_store_n(&diag->last_expired_id, item->id, __ATOMIC_RELAXED);
                __atomic_store_n(&diag->last_expired_received, item->received, __ATOMIC_RELAXED);
                __atomic_store_n(&diag->last_expired_total, item->total, __ATOMIC_RELAXED);
            }
            mikun2n_ipv6_fragment_retire(state, item->id);
            item->id = 0;
        }
        if(item->id == id) slot = item;
        if(!item->id && !free_slot) free_slot = item;
    }
    mikun2n_ipv6_fragment_diag_slots(state, diag);
    // A live assembly keeps its original deadline even when later global IDs
    // advance past the replay window. Retired IDs still cannot open a new slot.
    if(!slot && mikun2n_ipv6_fragment_retired(state, id)) {
        if(diag) mikun2n_v6_diag_add(state->completed_top - id >= MIKUN2N_REPLAY_WINDOW
                ? &diag->retired_window : &diag->retired, 1);
        mikun2n_ipv6_fragment_diag_reject(diag, id, offset, length);
        return 0;
    }
    state->replay_until = now + MIKUN2N_REASSEMBLY_MS;
    if(!slot) {
        if(!free_slot) {
            if(diag) mikun2n_v6_diag_add(&diag->slot_full, 1);
            mikun2n_ipv6_fragment_diag_reject(diag, id, offset, length);
            return 0;
        }
        slot = free_slot;
        memset(slot, 0, sizeof(*slot));
        slot->id = id; slot->total = total; slot->expires = now + MIKUN2N_REASSEMBLY_MS;
        if(diag) mikun2n_v6_diag_add(&diag->started, 1);
        mikun2n_ipv6_fragment_diag_slots(state, diag);
    }
    if(slot->total != total) {
        if(diag) mikun2n_v6_diag_add(&diag->conflicting, 1);
        mikun2n_ipv6_fragment_diag_reject(diag, id, offset, length);
        mikun2n_ipv6_fragment_retire(state, id); slot->id = 0;
        mikun2n_ipv6_fragment_diag_slots(state, diag);
        return 0;
    }
    size_t identical = 0;
    for(size_t i = 0; i < length; ++i) {
        size_t pos = offset + i;
        uint8_t mask = 1u << (pos & 7);
        if(slot->present[pos >> 3] & mask) {
            if(slot->data[pos] != payload[i]) {
                if(diag) mikun2n_v6_diag_add(&diag->conflicting, 1);
                mikun2n_ipv6_fragment_diag_reject(diag, id, offset, length);
                mikun2n_ipv6_fragment_retire(state, id); slot->id = 0;
                mikun2n_ipv6_fragment_diag_slots(state, diag);
                return 0;
            }
            ++identical;
        } else {
            slot->present[pos >> 3] |= mask;
            slot->data[pos] = payload[i];
            ++slot->received;
        }
    }
    if(diag && identical) mikun2n_v6_diag_add(identical == length ? &diag->duplicate : &diag->overlap, 1);
    if(slot->received != total) {
        if(diag) mikun2n_v6_diag_add(&diag->waiting, 1);
        return 0;
    }
    memcpy(out, slot->data, total);
    mikun2n_ipv6_fragment_retire(state, id);
    slot->id = 0;
    if(diag) mikun2n_v6_diag_add(&diag->completed, 1);
    mikun2n_ipv6_fragment_diag_slots(state, diag);
    return total;
}

static inline size_t mikun2n_ipv6_reassemble(mikun2n_ipv6_reassembly_t *state, uint64_t now,
        uint64_t id, uint16_t total, uint16_t offset, const uint8_t *payload, size_t length, uint8_t *out) {
    return mikun2n_ipv6_reassemble_ex(state, now, id, total, offset, payload, length, out, NULL);
}
#endif
