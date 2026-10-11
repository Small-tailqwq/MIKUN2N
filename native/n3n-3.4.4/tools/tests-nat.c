/* SPDX-License-Identifier: GPL-3.0-only */
/* Offline regression for NAT verdicts and the bank/layered round choice. */
#include <assert.h>
#include <stdio.h>
#include "../src/mikun2n_nat_policy.h"

int main (void) {
    static const uint8_t kinds[] = {
        MIKUN2N_NAT_KIND_UNKNOWN, MIKUN2N_NAT_KIND_EIM,
        MIKUN2N_NAT_KIND_APDM, MIKUN2N_NAT_KIND_UNCERTAIN
    };
    unsigned a, b, r;

    assert(!mikun2n_nat_majority_eim(0, 5) && !mikun2n_nat_split_verdict(0, 5));
    assert(mikun2n_nat_majority_eim(5, 5) && !mikun2n_nat_split_verdict(5, 5));
    assert(mikun2n_nat_majority_eim(3, 5) && mikun2n_nat_split_verdict(3, 5));
    assert(!mikun2n_nat_majority_eim(2, 5) && mikun2n_nat_split_verdict(2, 5));
    assert(mikun2n_nat_majority_eim(1, 1) && !mikun2n_nat_majority_eim(0, 1));
    puts("PASS: five-sample majority and split verdicts are unchanged");

    /* The sample from 2026-10-10: A and the supernode both saw 34319, B never answered. */
    assert(mikun2n_nat_partial_eim(34319, 34319));
    assert(!mikun2n_nat_partial_eim(6336, 6915));
    assert(!mikun2n_nat_partial_eim(0, 0) && !mikun2n_nat_partial_eim(34319, 0));
    puts("PASS: a silent observer B falls back to the supernode's view of the same socket");

    assert(mikun2n_bank_round_for(MIKUN2N_NAT_KIND_APDM, MIKUN2N_NAT_KIND_APDM, 0) &&
           mikun2n_bank_round_for(MIKUN2N_NAT_KIND_APDM, MIKUN2N_NAT_KIND_APDM, 1) &&
           mikun2n_bank_round_for(MIKUN2N_NAT_KIND_APDM, MIKUN2N_NAT_KIND_APDM, 2));
    for(r = 0; r < 3; r++) {
        int bank = (r % 2U) == 0;
        assert(mikun2n_bank_round_for(MIKUN2N_NAT_KIND_EIM, MIKUN2N_NAT_KIND_APDM, (uint8_t)r) == bank);
        assert(mikun2n_bank_round_for(MIKUN2N_NAT_KIND_APDM, MIKUN2N_NAT_KIND_EIM, (uint8_t)r) == bank);
    }
    assert(!mikun2n_bank_round_for(MIKUN2N_NAT_KIND_EIM, MIKUN2N_NAT_KIND_EIM, 0));
    assert(!mikun2n_bank_round_for(MIKUN2N_NAT_KIND_UNCERTAIN, MIKUN2N_NAT_KIND_APDM, 0));
    assert(!mikun2n_bank_round_for(MIKUN2N_NAT_KIND_UNKNOWN, MIKUN2N_NAT_KIND_APDM, 0));
    puts("PASS: NAT4 pairs always use banks; cone<->NAT4 alternates banks and the layered sweep");

    /* Both ends of a pair choose the same kind of round, and the supernode can
     * plan a GO for every round an edge spends on banks. */
    for(a = 0; a < sizeof(kinds); a++)
        for(b = 0; b < sizeof(kinds); b++)
            for(r = 0; r < 4; r++) {
                int here = mikun2n_bank_round_for(kinds[a], kinds[b], (uint8_t)r);
                assert(here == mikun2n_bank_round_for(kinds[b], kinds[a], (uint8_t)r));
                assert(!here || mikun2n_bank_pair_kinds(kinds[a], kinds[b]));
                assert(mikun2n_bank_pair_kinds(kinds[a], kinds[b]) ==
                       mikun2n_bank_pair_kinds(kinds[b], kinds[a]));
            }
    puts("PASS: edges agree on each round and the supernode admits every bank round");
    return 0;
}
