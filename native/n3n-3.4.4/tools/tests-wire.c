/*
 * Copyright (C) 2023 Hamish Coleman
 * SPDX-License-Identifier: GPL-3.0-only
 *
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses/>
 *
 */


#include <assert.h>
#include <stdint.h>    // for uint8_t
#include <stdio.h>     // for printf, fprintf, size_t, stderr, stdout
#include <string.h>    // for memset, strcpy, strncpy
#include "hexdump.h"   // for fhexdump
#include "n2n.h"       // for n2n_common_t, n2n_REGISTER_SUPER_t, n2n_REGIST...
#include "n2n_wire.h"  // for encode_REGISTER, encode_REGISTER_SUPER, encode...


void init_ip_subnet (n2n_ip_subnet_t * d) {
    d->net_addr = 0x20212223;
    d->net_bitlen = 25;
}

void print_ip_subnet (char *test_name, char *field, n2n_ip_subnet_t * d) {
    printf("%s: %s.net_addr = 0x%08x\n",
           test_name, field, d->net_addr);
    printf("%s: %s.net_bitlen = %i\n",
           test_name, field, d->net_bitlen);
}

void init_mac (n2n_mac_t mac, const uint8_t o0, const uint8_t o1,
               const uint8_t o2, const uint8_t o3,
               const uint8_t o4, const uint8_t o5) {
    mac[0] = o0;
    mac[1] = o1;
    mac[2] = o2;
    mac[3] = o3;
    mac[4] = o4;
    mac[5] = o5;
}

void print_mac (char *test_name, char *field, n2n_mac_t mac) {
    printf("%s: %s[] = %x:%x:%x:%x:%x:%x\n",
           test_name, field,
           mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

void init_auth (n2n_auth_t *auth) {
    auth->scheme = n2n_auth_simple_id;
    auth->token_size = 16;
    auth->token[0] = 0xfe;
    auth->token[4] = 0xfd;
    auth->token[8] = 0xfc;
    auth->token[15] = 0xfb;
}

void print_auth (char *test_name, char *field, n2n_auth_t *auth) {
    printf("%s: %s.scheme = %i\n", test_name, field, auth->scheme);
    printf("%s: %s.token_size = %i\n", test_name, field, auth->token_size);
    printf("%s: %s.token[0] = 0x%02x\n", test_name, field, auth->token[0]);
}

void init_common (n2n_common_t *common, char *community) {
    memset( common, 0, sizeof(*common) );
    common->ttl = N2N_DEFAULT_TTL;
    common->flags = 0;
    strncpy( (char *)common->community, community, N2N_COMMUNITY_SIZE);
    common->community[N2N_COMMUNITY_SIZE - 1] = '\0';
}

void print_common (char *test_name, n2n_common_t *common) {
    printf("%s: common.ttl = %i\n", test_name, common->ttl);
    printf("%s: common.flags = %i\n", test_name, common->flags);
    printf("%s: common.community = \"%s\"\n", test_name, common->community);
}

void test_REGISTER (n2n_common_t *common) {
    char *test_name = "REGISTER";

    common->pc = MSG_TYPE_REGISTER;
    printf("%s: common.pc = %i\n", test_name, common->pc);

    n2n_REGISTER_t reg;
    memset( &reg, 0, sizeof(reg) );
    init_mac( reg.srcMac, 0,1,2,3,4,5);
    init_mac( reg.dstMac, 0x10,0x11,0x12,0x13,0x14,0x15);
    init_ip_subnet(&reg.dev_addr);
    strcpy( (char *)reg.dev_desc, "Dummy_Dev_Desc" );

    printf("%s: reg.cookie = %i\n", test_name, reg.cookie);
    print_mac(test_name, "reg.srcMac", reg.srcMac);
    print_mac(test_name, "reg.dstMac", reg.dstMac);
    // TODO: print reg.sock
    print_ip_subnet(test_name, "reg.dev_addr", &reg.dev_addr);
    printf("%s: reg.dev_desc = \"%s\"\n", test_name, reg.dev_desc);
    printf("\n");

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx = 0;
    size_t retval = encode_REGISTER( pktbuf, &idx, common, &reg);

    printf("%s: output retval = 0x%x\n", test_name, (uint32_t)retval);
    printf("%s: output idx = 0x%x\n", test_name, (uint32_t)idx);
    fhexdump(0, pktbuf, idx, stdout);

    // TODO: decode_REGISTER() and print

    fprintf(stderr, "%s: tested\n", test_name);
    printf("\n");
}

void test_REGISTER_SUPER (n2n_common_t *common) {
    char *test_name = "REGISTER_SUPER";

    common->pc = MSG_TYPE_REGISTER_SUPER;
    printf("%s: common.pc = %i\n", test_name, common->pc);

    n2n_REGISTER_SUPER_t reg;
    memset( &reg, 0, sizeof(reg) );
    init_mac( reg.edgeMac, 0x20,0x21,0x22,0x23,0x24,0x25);
    // n2n_sock_t sock
    init_ip_subnet(&reg.dev_addr);
    strcpy( (char *)reg.dev_desc, "Dummy_Dev_Desc" );
    init_auth(&reg.auth);
    reg.key_time = 600;


    printf("%s: reg.cookie = %i\n", test_name, reg.cookie);
    print_mac(test_name, "reg.edgeMac", reg.edgeMac);
    // TODO: print reg.sock
    print_ip_subnet(test_name, "reg.dev_addr", &reg.dev_addr);
    printf("%s: reg.dev_desc = \"%s\"\n", test_name, reg.dev_desc);
    print_auth(test_name, "reg.auth", &reg.auth);
    printf("%s: reg.key_time = %u\n", test_name, (uint32_t)reg.key_time);
    printf("\n");

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx = 0;
    size_t retval = encode_REGISTER_SUPER( pktbuf, &idx, common, &reg);

    printf("%s: output retval = 0x%x\n", test_name, (uint32_t)retval);
    printf("%s: output idx = 0x%x\n", test_name, (uint32_t)idx);
    fhexdump(0, pktbuf, idx, stdout);

    // TODO: decode_REGISTER_SUPER() and print

    fprintf(stderr, "%s: tested\n", test_name);
    printf("\n");
}

void test_UNREGISTER_SUPER (n2n_common_t *common) {
    char *test_name = "UNREGISTER_SUPER";

    common->pc = MSG_TYPE_UNREGISTER_SUPER;
    printf("%s: common.pc = %i\n", test_name, common->pc);

    n2n_UNREGISTER_SUPER_t unreg;
    memset( &unreg, 0, sizeof(unreg) );
    init_auth(&unreg.auth);
    init_mac( unreg.srcMac, 0x30,0x31,0x32,0x33,0x34,0x35);


    print_auth(test_name, "unreg.auth", &unreg.auth);
    print_mac(test_name, "unreg.srcMac", unreg.srcMac);
    printf("\n");

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx = 0;
    size_t retval = encode_UNREGISTER_SUPER( pktbuf, &idx, common, &unreg);

    printf("%s: output retval = 0x%x\n", test_name, (uint32_t)retval);
    printf("%s: output idx = 0x%x\n", test_name, (uint32_t)idx);
    fhexdump(0, pktbuf, idx, stdout);

    // TODO: decode_UNREGISTER_SUPER() and print

    fprintf(stderr, "%s: tested\n", test_name);
    printf("\n");
}

/*
 * Fill the memory region with a test pattern
 */
static void pattern_memset (void *buf, int size, int offset) {
    unsigned char *p = (unsigned char *)buf;
    unsigned char ch = (offset % 255) + 1;
    while(size--) {
        *p++ = ch++;
        // Avoid zeros in the test pattern
        if(!ch) {
            ch = 1;
        }
    }
}

unsigned char pktbuf[1600];
size_t pktbuf_size;
n2n_common_t in_common, out_common;
unsigned char in_data[1600], out_data[1600];
unsigned char in_tmpbuf[1600], out_tmpbuf[1600];

void pattern_init_out_buffers () {
    memset(&pktbuf, 0, sizeof(pktbuf));
    pktbuf_size = 0;
    memset(&out_common, 0, sizeof(out_common));
    memset(&out_data, 0, sizeof(out_data));
}

void pattern_print_pktbuf () {
    printf("pktbuf:\n");
    fhexdump(0, pktbuf, pktbuf_size, stdout);
}

void pattern_print_common () {
    printf("out_common:\n");
    fhexdump(0, (void *)&out_common, sizeof(out_common), stdout);
}

void pattern_REGISTER_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_REGISTER_t), sizeof(in_common));
}

void pattern_REGISTER_prep2 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    // relies on patterns remaining from prep1

    in_common.flags = N2N_FLAGS_SOCKET;
    n2n_REGISTER_t *reg = (n2n_REGISTER_t *)&in_data;
    reg->sock.family = AF_INET;

    pattern_init_out_buffers();
}

void pattern_REGISTER_codec () {
    encode_REGISTER(pktbuf, &pktbuf_size, &in_common, (n2n_REGISTER_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_REGISTER((n2n_REGISTER_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_REGISTER_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_REGISTER_t), stdout);

    printf("\n");
}

void pattern_PACKET_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_PACKET_t), sizeof(in_common));
}

void pattern_PACKET_codec () {
    encode_PACKET(pktbuf, &pktbuf_size, &in_common, (n2n_PACKET_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_PACKET((n2n_PACKET_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_PACKET_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_PACKET_t), stdout);

    printf("\n");
}

void pattern_REGISTER_ACK_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_REGISTER_ACK_t), sizeof(in_common));
}

void pattern_REGISTER_ACK_codec () {
    encode_REGISTER_ACK(pktbuf, &pktbuf_size, &in_common, (n2n_REGISTER_ACK_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_REGISTER_ACK((n2n_REGISTER_ACK_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_REGISTER_ACK_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_REGISTER_ACK_t), stdout);

    printf("\n");
}

void pattern_REGISTER_SUPER_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_REGISTER_SUPER_t), sizeof(in_common));

    n2n_REGISTER_SUPER_t *reg = (n2n_REGISTER_SUPER_t *)&in_data;
    reg->auth.token_size = N2N_AUTH_ID_TOKEN_SIZE;
}

void pattern_REGISTER_SUPER_codec () {
    encode_REGISTER_SUPER(pktbuf, &pktbuf_size, &in_common, (n2n_REGISTER_SUPER_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_REGISTER_SUPER((n2n_REGISTER_SUPER_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_REGISTER_SUPER_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_REGISTER_SUPER_t), stdout);

    printf("\n");
}

void pattern_UNREGISTER_SUPER_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_UNREGISTER_SUPER_t), sizeof(in_common));

    n2n_UNREGISTER_SUPER_t *reg = (n2n_UNREGISTER_SUPER_t *)&in_data;
    reg->auth.token_size = N2N_AUTH_ID_TOKEN_SIZE;
}

void pattern_UNREGISTER_SUPER_codec () {
    encode_UNREGISTER_SUPER(pktbuf, &pktbuf_size, &in_common, (n2n_UNREGISTER_SUPER_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_UNREGISTER_SUPER((n2n_UNREGISTER_SUPER_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_UNREGISTER_SUPER_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_UNREGISTER_SUPER_t), stdout);

    printf("\n");
}

void pattern_REGISTER_SUPER_ACK_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_REGISTER_SUPER_ACK_t), sizeof(in_common));
    pattern_memset(&in_tmpbuf, sizeof(REG_SUPER_ACK_PAYLOAD_ENTRY_SIZE), sizeof(in_common) + sizeof(n2n_REGISTER_SUPER_ACK_t));

    n2n_REGISTER_SUPER_ACK_t *reg = (n2n_REGISTER_SUPER_ACK_t *)&in_data;
    reg->sock.family = AF_INET;
    reg->auth.token_size = N2N_AUTH_ID_TOKEN_SIZE;
    reg->num_sn = 1;
    *((uint16_t *)&in_tmpbuf) = AF_INET;
}

void pattern_REGISTER_SUPER_ACK_codec () {
    encode_REGISTER_SUPER_ACK(pktbuf, &pktbuf_size, &in_common, (n2n_REGISTER_SUPER_ACK_t *)&in_data, in_tmpbuf);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_REGISTER_SUPER_ACK((n2n_REGISTER_SUPER_ACK_t *)&out_data, &out_common, pktbuf, &rem, &idx, out_tmpbuf);

}

void pattern_REGISTER_SUPER_ACK_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_REGISTER_SUPER_ACK_t), stdout);
    printf("out_tmpbuf:\n");
    fhexdump(0, (void *)&out_tmpbuf, REG_SUPER_ACK_PAYLOAD_ENTRY_SIZE, stdout);

    printf("\n");
}

void pattern_REGISTER_SUPER_NAK_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_REGISTER_SUPER_NAK_t), sizeof(in_common));

    n2n_REGISTER_SUPER_NAK_t *reg = (n2n_REGISTER_SUPER_NAK_t *)&in_data;
    reg->auth.token_size = N2N_AUTH_ID_TOKEN_SIZE;
}

void pattern_REGISTER_SUPER_NAK_codec () {
    encode_REGISTER_SUPER_NAK(pktbuf, &pktbuf_size, &in_common, (n2n_REGISTER_SUPER_NAK_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_REGISTER_SUPER_NAK((n2n_REGISTER_SUPER_NAK_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_REGISTER_SUPER_NAK_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_REGISTER_SUPER_NAK_t), stdout);

    printf("\n");
}

void pattern_PEER_INFO_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_PEER_INFO_t), sizeof(in_common));

    n2n_PEER_INFO_t *reg = (n2n_PEER_INFO_t *)&in_data;
    reg->sock.family = AF_INET;
}

void pattern_PEER_INFO_codec () {
    encode_PEER_INFO(pktbuf, &pktbuf_size, &in_common, (n2n_PEER_INFO_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_PEER_INFO((n2n_PEER_INFO_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_PEER_INFO_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_PEER_INFO_t), stdout);

    printf("\n");
}

void pattern_QUERY_PEER_prep1 () {
    printf("%s:\n", __func__);
    fprintf(stderr,"%s:\n", __func__);

    pattern_init_out_buffers();
    pattern_memset(&in_common, sizeof(in_common), 0);
    pattern_memset(&in_data, sizeof(n2n_QUERY_PEER_t), sizeof(in_common));
}

void pattern_QUERY_PEER_codec () {
    encode_QUERY_PEER(pktbuf, &pktbuf_size, &in_common, (n2n_QUERY_PEER_t *)&in_data);

    size_t rem = pktbuf_size;
    size_t idx = 0;
    decode_common(&out_common, pktbuf, &rem, &idx);
    decode_QUERY_PEER((n2n_QUERY_PEER_t *)&out_data, &out_common, pktbuf, &rem, &idx);

}

void pattern_QUERY_PEER_print () {
    pattern_print_pktbuf();
    pattern_print_common();

    printf("out_data:\n");
    fhexdump(0, (void *)&out_data, sizeof(n2n_QUERY_PEER_t), stdout);

    printf("\n");
}

void pattern_tests () {
    pattern_REGISTER_prep1();
    pattern_REGISTER_codec();
    pattern_REGISTER_print();
    pattern_REGISTER_prep2();
    pattern_REGISTER_codec();
    pattern_REGISTER_print();
    // TODO: REGISTER_prep3() with IPv6 sock

    pattern_PACKET_prep1();
    pattern_PACKET_codec();
    pattern_PACKET_print();

    pattern_REGISTER_ACK_prep1();
    pattern_REGISTER_ACK_codec();
    pattern_REGISTER_ACK_print();

    pattern_REGISTER_SUPER_prep1();
    pattern_REGISTER_SUPER_codec();
    pattern_REGISTER_SUPER_print();

    pattern_UNREGISTER_SUPER_prep1();
    pattern_UNREGISTER_SUPER_codec();
    pattern_UNREGISTER_SUPER_print();

    pattern_REGISTER_SUPER_ACK_prep1();
    pattern_REGISTER_SUPER_ACK_codec();
    pattern_REGISTER_SUPER_ACK_print();

    pattern_REGISTER_SUPER_NAK_prep1();
    pattern_REGISTER_SUPER_NAK_codec();
    pattern_REGISTER_SUPER_NAK_print();

    pattern_PEER_INFO_prep1();
    pattern_PEER_INFO_codec();
    pattern_PEER_INFO_print();

    pattern_QUERY_PEER_prep1();
    pattern_QUERY_PEER_codec();
    pattern_QUERY_PEER_print();

}

static void test_mikun2n_wire_extensions (void) {
    n2n_common_t common = {0};
    n2n_common_t decoded_common = {0};
    n2n_QUERY_PEER_t query = {0}, decoded_query = {0};
    n2n_PEER_INFO_t info = {0}, decoded_info = {0};
    uint8_t buf[N2N_PKT_BUF_SIZE] = {0};
    size_t idx = 0, rem, decoded_idx;

    init_common(&common, "mikun2n-wire");
    common.pc = MSG_TYPE_QUERY_PEER;
    query.aflags = N2N_AFLAGS_MIKUN2N_NAT |
                   N2N_AFLAGS_MIKUN2N_BANK_MODEL;
    query.mikun2n_nat_kind = MIKUN2N_NAT_KIND_EIM;
    query.mikun2n_eim_matches = 5;
    query.mikun2n_eim_samples = 5;
    query.mikun2n_punch_nonce = 0x12345678;
    query.mikun2n_bank_mode = MIKUN2N_BANK_MODE_SYMMETRIC;
    query.mikun2n_bank_direction = -1;
    query.mikun2n_bank_workers = 25;
    query.mikun2n_bank_reuse = 3;
    query.mikun2n_bank1 = 26476;
    query.mikun2n_bank2 = 27345;
    query.mikun2n_bank_spread = 18;
    query.mikun2n_bank_rate = 321;
    query.mikun2n_bank_nonce = 0x55667788;
    query.mikun2n_bank_generation = 0x33445566;
    encode_QUERY_PEER(buf, &idx, &common, &query);
    rem = idx;
    decoded_idx = 0;
    assert(decode_common(&decoded_common, buf, &rem, &decoded_idx) >= 0);
    decode_QUERY_PEER(&decoded_query, &decoded_common, buf, &rem, &decoded_idx);
    assert(decoded_query.mikun2n_nat_kind == query.mikun2n_nat_kind);
    assert(decoded_query.mikun2n_eim_matches == query.mikun2n_eim_matches);
    assert(decoded_query.mikun2n_eim_samples == query.mikun2n_eim_samples);
    assert(decoded_query.mikun2n_punch_nonce == query.mikun2n_punch_nonce);
    assert(decoded_query.mikun2n_bank_mode == query.mikun2n_bank_mode);
    assert(decoded_query.mikun2n_bank_direction == query.mikun2n_bank_direction);
    assert(decoded_query.mikun2n_bank_workers == query.mikun2n_bank_workers);
    assert(decoded_query.mikun2n_bank_reuse == query.mikun2n_bank_reuse);
    assert(decoded_query.mikun2n_bank1 == query.mikun2n_bank1);
    assert(decoded_query.mikun2n_bank2 == query.mikun2n_bank2);
    assert(decoded_query.mikun2n_bank_spread == query.mikun2n_bank_spread);
    assert(decoded_query.mikun2n_bank_rate == query.mikun2n_bank_rate);
    assert(decoded_query.mikun2n_bank_nonce == query.mikun2n_bank_nonce);
    assert(decoded_query.mikun2n_bank_generation ==
           query.mikun2n_bank_generation);
    assert(rem == 0);

    memset(buf, 0, sizeof(buf));
    idx = 0;
    common.pc = MSG_TYPE_PEER_INFO;
    info.aflags = N2N_AFLAGS_MIKUN2N_NAT |
                  N2N_AFLAGS_MIKUN2N_PUNCH_PLAN |
                  N2N_AFLAGS_MIKUN2N_BANK_MODEL;
    info.sock.family = AF_INET;
    info.sock.type = SOCK_DGRAM;
    info.mikun2n_nat_kind = MIKUN2N_NAT_KIND_APDM;
    info.mikun2n_eim_matches = 0;
    info.mikun2n_eim_samples = 5;
    info.mikun2n_punch_nonce = 0x89abcdef;
    info.mikun2n_punch_role = MIKUN2N_PUNCH_ROLE_SCANNER;
    info.mikun2n_punch_generation = 0x10203040;
    info.mikun2n_punch_delay_ms = 1500;
    info.mikun2n_bank_mode = MIKUN2N_BANK_MODE_CONE;
    info.mikun2n_bank_direction = 1;
    info.mikun2n_bank_workers = 25;
    info.mikun2n_bank_reuse = 25;
    info.mikun2n_bank1 = 58491;
    info.mikun2n_bank2 = 58491;
    info.mikun2n_bank_spread = 4;
    info.mikun2n_bank_rate = 0;
    info.mikun2n_bank_nonce = 0xaabbccdd;
    encode_PEER_INFO(buf, &idx, &common, &info);
    rem = idx;
    decoded_idx = 0;
    assert(decode_common(&decoded_common, buf, &rem, &decoded_idx) >= 0);
    decode_PEER_INFO(&decoded_info, &decoded_common, buf, &rem, &decoded_idx);
    fprintf(stderr, "MikuN2N PEER_INFO wire: flags=%04x nat=%u nonce=%08x "
                    "role=%u generation=%08x delay=%u rem=%u\n",
            decoded_info.aflags, decoded_info.mikun2n_nat_kind,
            decoded_info.mikun2n_punch_nonce, decoded_info.mikun2n_punch_role,
            decoded_info.mikun2n_punch_generation,
            decoded_info.mikun2n_punch_delay_ms, (unsigned int)rem);
    assert(decoded_info.mikun2n_nat_kind == info.mikun2n_nat_kind);
    assert(decoded_info.mikun2n_punch_nonce == info.mikun2n_punch_nonce);
    assert(decoded_info.mikun2n_punch_role == info.mikun2n_punch_role);
    assert(decoded_info.mikun2n_punch_generation == info.mikun2n_punch_generation);
    assert(decoded_info.mikun2n_punch_delay_ms == info.mikun2n_punch_delay_ms);
    assert(decoded_info.mikun2n_bank_mode == info.mikun2n_bank_mode);
    assert(decoded_info.mikun2n_bank_direction == info.mikun2n_bank_direction);
    assert(decoded_info.mikun2n_bank_workers == info.mikun2n_bank_workers);
    assert(decoded_info.mikun2n_bank_reuse == info.mikun2n_bank_reuse);
    assert(decoded_info.mikun2n_bank1 == info.mikun2n_bank1);
    assert(decoded_info.mikun2n_bank2 == info.mikun2n_bank2);
    assert(decoded_info.mikun2n_bank_spread == info.mikun2n_bank_spread);
    assert(decoded_info.mikun2n_bank_rate == info.mikun2n_bank_rate);
    assert(decoded_info.mikun2n_bank_nonce == info.mikun2n_bank_nonce);
    assert(rem == 0);
    fprintf(stderr, "MikuN2N wire extensions: tested\n");
}

int main (int argc, char * argv[]) {
    char *test_name = "environment";

    n2n_common_t common;
    init_common( &common, "abc123def456z" );
    print_common( test_name, &common );
    printf("\n");

    test_REGISTER(&common);
    test_REGISTER_SUPER(&common);
    test_UNREGISTER_SUPER(&common);
    // TODO: add more wire tests

    pattern_tests();
    test_mikun2n_wire_extensions();

    return 0;
}
