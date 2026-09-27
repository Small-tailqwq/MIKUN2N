/* SPDX-License-Identifier: GPL-3.0-only */
/* Offline regression for the supernode relay policy: no sockets involved. */
#include <assert.h>
#include <stdio.h>
#include "../src/mikun2n_relay_policy.h"

static uint8_t key_a[MIKUN2N_POLICY_KEY_SIZE] = {'g', 1};
static uint8_t key_b[MIKUN2N_POLICY_KEY_SIZE] = {'g', 2};

/* Offers `a_pps` and `b_pps` packets of `size` bytes per second for `seconds`
 * and returns the bytes admitted for each source. */
static void run (mikun2n_relay_policy_t *p, uint64_t start, unsigned seconds, unsigned size,
                 unsigned a_pps, unsigned b_pps, uint64_t *a_bytes, uint64_t *b_bytes) {
    *a_bytes = *b_bytes = 0;
    for(uint64_t ms = 0; ms < seconds * 1000ULL; ms++) {
        uint64_t now = start + ms;
        for(uint64_t n = (ms * a_pps) / 1000; n < ((ms + 1) * a_pps) / 1000; n++)
            if(mikun2n_policy_admit(p, key_a, size, 0, now))
                *a_bytes += size;
        for(uint64_t n = (ms * b_pps) / 1000; n < ((ms + 1) * b_pps) / 1000; n++)
            if(mikun2n_policy_admit(p, key_b, size, 0, now))
                *b_bytes += size;
    }
}

int main (void) {
    mikun2n_relay_policy_t p = {0};
    uint64_t a, b;

    assert(mikun2n_policy_admit(&p, key_a, 60000, 0, 1000));
    assert(mikun2n_policy_admit(&p, key_a, 60000, 1, 1000));
    assert(!p.dropped_packets && !p.sources);
    puts("PASS: disabled policy admits everything and keeps no state");

    p.rate = 400000;                                   /* 3.2 Mbit/s */
    run(&p, 1000, 10, 1000, 1000, 0, &a, &b);          /* one heavy source, 8 Mbit/s offered */
    assert(a > 3800000 && a < 4200000);
    puts("PASS: a single source can use the whole budget and no more");

    mikun2n_policy_free(&p);
    memset(&p, 0, sizeof(p)); p.rate = 400000;
    run(&p, 1000, 10, 150, 5000, 200, &a, &b);         /* heavy 6 Mbit/s vs game-like 240 kbit/s */
    assert(b >= 150ULL * 200 * 10 * 99 / 100);         /* light source keeps its traffic */
    assert(a + b <= 400000ULL * 10 + 400000 / 5 + 1600 * 4 * 2);
    assert(a > 3500000);                               /* the heavy source still gets the rest */
    puts("PASS: a light source is protected; the heavy one borrows the spare capacity");

    mikun2n_policy_free(&p);
    memset(&p, 0, sizeof(p)); p.rate = 400000;
    run(&p, 1000, 10, 1000, 1000, 1000, &a, &b);       /* two equal heavy sources */
    assert(a > 1700000 && b > 1700000 && a + b <= 4200000);
    assert((a > b ? a - b : b - a) < 400000);
    puts("PASS: equal heavy sources split the budget");

    mikun2n_policy_free(&p);
    memset(&p, 0, sizeof(p)); p.broadcast_pps = 10;
    unsigned admitted = 0;
    for(uint64_t ms = 0; ms < 5000; ms += 10)          /* 100 broadcasts/s for 5 s */
        admitted += mikun2n_policy_admit(&p, key_a, 1400, 1, 1000 + ms);
    assert(admitted >= 50 && admitted <= 62);          /* 10/s plus a one-second burst */
    assert(mikun2n_policy_admit(&p, key_b, 1400, 1, 6000));
    assert(mikun2n_policy_admit(&p, key_a, 1400, 0, 6000));
    puts("PASS: broadcast storms are limited per source; unicast and other sources unaffected");

    mikun2n_policy_free(&p);
    memset(&p, 0, sizeof(p)); p.rate = 400000;
    assert(mikun2n_policy_admit(&p, key_a, 100, 0, 1000));
    mikun2n_policy_admit(&p, key_b, 100, 0, 1000 + MIKUN2N_POLICY_IDLE_MS + 1000);
    assert(HASH_COUNT(p.sources) == 1);
    mikun2n_policy_free(&p);
    puts("PASS: idle sources are released");
    return 0;
}
