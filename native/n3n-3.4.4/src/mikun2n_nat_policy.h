/* SPDX-License-Identifier: GPL-3.0-only */
#ifndef MIKUN2N_NAT_POLICY_H
#define MIKUN2N_NAT_POLICY_H

/* NAT verdicts and punch-strategy choices shared by the edge and the supernode.
 * Kept free of sockets so tools/tests-nat.c can check that both sides agree. */

#include <stdint.h>

#ifndef MIKUN2N_NAT_KIND_UNKNOWN
#define MIKUN2N_NAT_KIND_UNKNOWN        0
#define MIKUN2N_NAT_KIND_EIM            1
#define MIKUN2N_NAT_KIND_APDM           2
#define MIKUN2N_NAT_KIND_UNCERTAIN      3
#endif

/* Majority of same-socket A/B samples kept the public port. */
static inline int mikun2n_nat_majority_eim (uint8_t matches, uint8_t samples) {
    return matches * 2 > samples;
}

/* Neither a clear match nor a clear mismatch: the NAT re-binds unpredictably. */
static inline int mikun2n_nat_split_verdict (uint8_t matches, uint8_t samples) {
    return matches * 5 < samples * 4 && matches * 5 > samples;
}

/* Observer B silent: compare A with the supernode's view of the same socket. */
static inline int mikun2n_nat_partial_eim (uint16_t port_a, uint16_t port_supernode) {
    return port_a != 0 && port_a == port_supernode;
}

/* Kinds for which the supernode may hand out a bank GO. */
static inline int mikun2n_bank_pair_kinds (uint8_t a, uint8_t b) {
    return (a == MIKUN2N_NAT_KIND_APDM &&
            (b == MIKUN2N_NAT_KIND_APDM || b == MIKUN2N_NAT_KIND_EIM)) ||
           (a == MIKUN2N_NAT_KIND_EIM && b == MIKUN2N_NAT_KIND_APDM);
}

/* Whether an edge spends this round on calibrated worker banks. NAT4<->NAT4
 * always does. Cone<->NAT4 alternates, starting with banks: cone peers that
 * were misreported as NAT4 reached NAT4 peers only through banks, while other
 * cone peers connect through the layered sweep, slowly but reliably. Both ends
 * count failed rounds alike, so they choose the same kind of round. */
static inline int mikun2n_bank_round_for (uint8_t local, uint8_t peer, uint8_t failed_rounds) {
    if(local == MIKUN2N_NAT_KIND_APDM && peer == MIKUN2N_NAT_KIND_APDM)
        return 1;
    if(mikun2n_bank_pair_kinds(local, peer))
        return (failed_rounds % 2U) == 0;
    return 0;
}

#endif
