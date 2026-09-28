/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_STUN_H
#define MIKUN2N_STUN_H

/* Tagged STUN Binding request for the supernode's own IPv6 mapping observer.
 *
 * The observer answers only requests that name a MikuN2N edge currently registered
 * on the same supernode, so it cannot be used as an open STUN service. The tag is
 * a SOFTWARE attribute (comprehension-optional, so any RFC 8489 server may ignore
 * it) holding "MikuN2N/1 " and the edge's 12-digit MAC. The request is also never
 * smaller than the observer's answer (an IPv6 XOR-MAPPED-ADDRESS), so a spoofed
 * source cannot be used for reflection with amplification. */

#include <stdint.h>
#include <string.h>

#define MIKUN2N_STUN_SOFTWARE_PREFIX "MikuN2N/1 "
#define MIKUN2N_STUN_SOFTWARE_BYTES 22        /* prefix (10) + MAC as hex (12) */
#define MIKUN2N_STUN_TAGGED_REQUEST_BYTES 48  /* header (20) + attribute (4 + 24) */

/* request holds the 20-byte header with type, cookie and transaction already set;
 * returns the total request size. */
static inline size_t mikun2n_stun_tag_request (uint8_t *request, const uint8_t mac[6]) {
    static const char hex[] = "0123456789abcdef";
    uint8_t *attribute = request + 20;
    const size_t padded = (MIKUN2N_STUN_SOFTWARE_BYTES + 3u) & ~3u;
    const size_t body = 4 + padded;

    request[2] = (uint8_t)(body >> 8);
    request[3] = (uint8_t)body;
    attribute[0] = 0x80;
    attribute[1] = 0x22;
    attribute[2] = 0;
    attribute[3] = MIKUN2N_STUN_SOFTWARE_BYTES;
    memcpy(attribute + 4, MIKUN2N_STUN_SOFTWARE_PREFIX, 10);
    for(int i = 0; i < 6; i++) {
        attribute[14 + 2 * i] = (uint8_t)hex[mac[i] >> 4];
        attribute[15 + 2 * i] = (uint8_t)hex[mac[i] & 15];
    }
    memset(attribute + 4 + MIKUN2N_STUN_SOFTWARE_BYTES, 0, padded - MIKUN2N_STUN_SOFTWARE_BYTES);
    return 20 + body;
}

#endif
