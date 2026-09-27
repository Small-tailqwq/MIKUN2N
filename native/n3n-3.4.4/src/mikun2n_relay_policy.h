/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_RELAY_POLICY_H
#define MIKUN2N_RELAY_POLICY_H

/* Supernode egress policy for relayed DATA only.
 *
 * A small cloud host (4 Mbit/s in the deployment this was written for) is
 * shaped by its provider, which drops whatever exceeds the limit. Once relayed
 * game traffic filled the link, registration renewals, PEER_INFO and punch
 * coordination were lost with it - so edges fell back to relay more often and
 * could not coordinate the punch that would have taken them off it. Policing
 * DATA slightly below the link rate keeps headroom for control messages, which
 * are never policed here. Under contention each active source gets an equal
 * share; a source may exceed its share only while the shared bucket is at least
 * half full, so an idle link is still fully usable. Broadcast copies count
 * against the same byte budget, and each source's broadcasts are additionally
 * limited in packets per second because every one is multiplied by the number
 * of community members. */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "uthash.h"

#define MIKUN2N_POLICY_KEY_SIZE 26            /* community (20) + source MAC (6) */
#define MIKUN2N_POLICY_MAX_SOURCES 1024
#define MIKUN2N_POLICY_BURST_MS 200
#define MIKUN2N_POLICY_SOURCE_BURST_MS 500
#define MIKUN2N_POLICY_ACTIVE_MS 2000
#define MIKUN2N_POLICY_RECOUNT_MS 250
#define MIKUN2N_POLICY_IDLE_MS 60000
#define MIKUN2N_POLICY_MIN_BURST_BYTES (4 * 1600)

struct mikun2n_policy_source {
    uint8_t key[MIKUN2N_POLICY_KEY_SIZE];
    uint64_t last_ms;
    int64_t tokens;              /* milli-bytes */
    int64_t broadcast_tokens;    /* milli-packets */
    UT_hash_handle hh;
};

typedef struct mikun2n_relay_policy {
    uint32_t rate;               /* bytes per second; 0 disables byte policing */
    uint32_t broadcast_pps;      /* per source; 0 disables broadcast policing */
    int64_t tokens;              /* milli-bytes */
    uint64_t last_ms;
    uint64_t recount_ms;
    uint32_t active;
    struct mikun2n_policy_source *sources;
    uint64_t dropped_bytes, dropped_packets;
    uint64_t borrowed_packets, broadcast_dropped;
} mikun2n_relay_policy_t;

static inline int64_t mikun2n_policy_cap (uint64_t rate, uint32_t burst_ms, int64_t floor_bytes) {
    int64_t cap = (int64_t)(rate * burst_ms);
    return cap < floor_bytes * 1000 ? floor_bytes * 1000 : cap;
}

static inline void mikun2n_policy_recount (mikun2n_relay_policy_t *p, uint64_t now_ms) {
    struct mikun2n_policy_source *s, *tmp;
    uint32_t active = 0;

    HASH_ITER(hh, p->sources, s, tmp) {
        if(now_ms - s->last_ms < MIKUN2N_POLICY_ACTIVE_MS)
            active++;
        else if(now_ms - s->last_ms >= MIKUN2N_POLICY_IDLE_MS) {
            HASH_DEL(p->sources, s);
            free(s);
        }
    }
    p->active = active ? active : 1;
    p->recount_ms = now_ms;
}

/* Returns 1 when the datagram may be sent. cost_bytes is what leaves the host:
 * the packet size times the number of copies for a broadcast. */
static inline int mikun2n_policy_admit (mikun2n_relay_policy_t *p, const uint8_t *key,
                                        uint32_t cost_bytes, int broadcast, uint64_t now_ms) {
    struct mikun2n_policy_source *s;
    int64_t cost, cap, share_cap;
    uint64_t share, dt;

    if(!p->rate && !(broadcast && p->broadcast_pps))
        return 1;

    if(p->rate) {
        cap = mikun2n_policy_cap(p->rate, MIKUN2N_POLICY_BURST_MS, MIKUN2N_POLICY_MIN_BURST_BYTES);
        if(!p->last_ms)
            p->tokens = cap;
        else
            p->tokens += (int64_t)(p->rate * (now_ms - p->last_ms));
        if(p->tokens > cap)
            p->tokens = cap;
    } else
        cap = 0;
    p->last_ms = now_ms;
    if(!p->recount_ms || now_ms - p->recount_ms >= MIKUN2N_POLICY_RECOUNT_MS)
        mikun2n_policy_recount(p, now_ms);

    HASH_FIND(hh, p->sources, key, MIKUN2N_POLICY_KEY_SIZE, s);
    if(!s && HASH_COUNT(p->sources) < MIKUN2N_POLICY_MAX_SOURCES) {
        s = calloc(1, sizeof(*s));
        if(s) {
            memcpy(s->key, key, MIKUN2N_POLICY_KEY_SIZE);
            s->last_ms = now_ms;
            s->tokens = INT64_MAX;       /* clamped to a full share below */
            s->broadcast_tokens = (int64_t)p->broadcast_pps * 1000;
            HASH_ADD(hh, p->sources, key, MIKUN2N_POLICY_KEY_SIZE, s);
            p->active++;
        }
    }

    share = p->rate / (p->active ? p->active : 1);
    share_cap = mikun2n_policy_cap(share, MIKUN2N_POLICY_SOURCE_BURST_MS, MIKUN2N_POLICY_MIN_BURST_BYTES);
    if(s) {
        dt = now_ms - s->last_ms;
        if(s->tokens < share_cap)
            s->tokens += (int64_t)(share * dt);
        if(s->tokens > share_cap)
            s->tokens = share_cap;
        if(p->broadcast_pps) {
            s->broadcast_tokens += (int64_t)(p->broadcast_pps * dt);
            if(s->broadcast_tokens > (int64_t)p->broadcast_pps * 1000)
                s->broadcast_tokens = (int64_t)p->broadcast_pps * 1000;
        }
        s->last_ms = now_ms;
    }

    if(broadcast && p->broadcast_pps && s) {
        if(s->broadcast_tokens < 1000) {
            p->broadcast_dropped++;
            p->dropped_packets++;
            p->dropped_bytes += cost_bytes;
            return 0;
        }
    }

    if(p->rate) {
        cost = (int64_t)cost_bytes * 1000;
        if(p->tokens < cost) {
            p->dropped_packets++;
            p->dropped_bytes += cost_bytes;
            return 0;
        }
        if(s && s->tokens >= cost)
            s->tokens -= cost;
        else if(p->tokens - cost >= cap / 2)
            p->borrowed_packets++;
        else {
            p->dropped_packets++;
            p->dropped_bytes += cost_bytes;
            return 0;
        }
        p->tokens -= cost;
    }
    if(broadcast && p->broadcast_pps && s)
        s->broadcast_tokens -= 1000;
    return 1;
}

static inline void mikun2n_policy_free (mikun2n_relay_policy_t *p) {
    struct mikun2n_policy_source *s, *tmp;

    HASH_ITER(hh, p->sources, s, tmp) {
        HASH_DEL(p->sources, s);
        free(s);
    }
}

#endif
