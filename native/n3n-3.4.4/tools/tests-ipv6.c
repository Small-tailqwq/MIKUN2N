/* SPDX-License-Identifier: GPL-3.0-only */
/* Offline regression: all IPv6 sends are intercepted before any OS socket call. */
#ifdef _WIN32
#include "win32/defs.h"
#endif
#include <n2n.h>
#include <n3n/edge.h>
#include <n3n/random.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static struct { uint8_t bytes[N2N_PKT_BUF_SIZE]; size_t size; } sent_packets[64];
static unsigned sent_count;
static int fail_next;
#ifdef _WIN32
static int ipv6_test_sendto(SOCKET fd, const char *data, int size, int flags,
                            const struct sockaddr *to, int tolen) {
#else
static ssize_t ipv6_test_sendto(int fd, const void *data, size_t size, int flags,
                                const struct sockaddr *to, socklen_t tolen) {
#endif
    (void)fd; (void)flags; (void)to; (void)tolen;
    if(fail_next) {
#ifdef _WIN32
        WSASetLastError(fail_next);
#else
        errno = fail_next;
#endif
        fail_next = 0; return -1;
    }
    assert(sent_count < 64 && size <= N2N_PKT_BUF_SIZE);
    memcpy(sent_packets[sent_count].bytes, data, size);
    sent_packets[sent_count++].size = size;
    return size;
}
#define sendto ipv6_test_sendto
#include "../src/mikun2n_ipv6.c"
#undef sendto

static struct n3n_runtime_data *a, *b;
static struct peer_info *ab, *ba;
static n2n_sock_t addr_a, addr_b;
static void setup(void) {
    a = calloc(1,sizeof(*a)); b = calloc(1,sizeof(*b));
    ab = peer_info_malloc(null_mac); ba = peer_info_malloc(null_mac);
    assert(a && b && ab && ba);
    addr_a.family = addr_b.family = AF_INET6; addr_a.port = addr_b.port = 50001;
    addr_a.addr.v6[0] = addr_b.addr.v6[0] = 0x26;
    addr_a.addr.v6[15] = 1; addr_b.addr.v6[15] = 2;
    a->device.mac_addr[0] = b->device.mac_addr[0] = 2;
    a->device.mac_addr[5] = 1; b->device.mac_addr[5] = 2;
    a->mikun2n_ipv6_socket = b->mikun2n_ipv6_socket = 1;
    a->conf.mikun2n_ipv6 = b->conf.mikun2n_ipv6 = true;
    a->conf.allow_p2p = b->conf.allow_p2p = true;
    a->conf.mtu = b->conf.mtu = 1290;
    a->mikun2n_ipv6_token = 101; b->mikun2n_ipv6_token = 202;
    memcpy(ab->mac_addr,b->device.mac_addr,6); memcpy(ba->mac_addr,a->device.mac_addr,6);
    ab->mikun2n_ipv6_token = 202; ba->mikun2n_ipv6_token = 101;
    ab->mikun2n_ipv6_address = ab->mikun2n_ipv6_path_address = addr_b;
    ba->mikun2n_ipv6_address = ba->mikun2n_ipv6_path_address = addr_a;
    ab->mikun2n_ipv6_seen_ms = ba->mikun2n_ipv6_seen_ms = 1000;
    ab->mikun2n_ipv6_valid_until_ms = ba->mikun2n_ipv6_valid_until_ms = 7000;
    ab->mikun2n_ipv6_peer_ready_until_ms = ba->mikun2n_ipv6_peer_ready_until_ms = 7000;
    ab->mikun2n_ipv6_path_bytes = ba->mikun2n_ipv6_path_bytes = 1232;
    HASH_ADD_PEER(a->known_peers,ab); HASH_ADD_PEER(b->known_peers,ba);
}
static size_t deliver(unsigned index, struct n3n_runtime_data *receiver,
                      const n2n_sock_t *sender, uint64_t now, uint8_t *output) {
    memcpy(output,sent_packets[index].bytes,sent_packets[index].size);
    return mikun2n_ipv6_unwrap(receiver,sender,output,sent_packets[index].size,now);
}
int main(void) {
    uint8_t out[N2N_PKT_BUF_SIZE], original[1342];
    for(unsigned i=0;i<sizeof(original);++i) original[i]=(uint8_t)(i*17);
    setup();
    assert(ipv6_probe_send(a,ab,&addr_b,1000,1418,"test"));
    assert(deliver(0,b,&addr_a,1020,out)==0);
    assert(sent_packets[1].size==IPV6_CONTROL_SIZE);
    assert(deliver(1,a,&addr_b,1040,out)==0 && ab->mikun2n_ipv6_path_bytes==1418);
    sent_count=0;
    assert(ipv6_probe_send(b,ba,&addr_a,1050,1232,"test"));
    assert(deliver(0,a,&addr_b,1070,out)==0 && ab->mikun2n_ipv6_path_bytes==1418);
    assert(deliver(1,b,&addr_a,1090,out)==0 && ba->mikun2n_ipv6_path_bytes==1232);
    puts("PASS: directional sizes, short ACK, small peer probe preserves larger path");

    n2n_PEER_INFO_t refreshed={0};
    refreshed.aflags=N2N_AFLAGS_MIKUN2N_IPV6;
    refreshed.mikun2n_ipv6_token=ab->mikun2n_ipv6_token;
    refreshed.mikun2n_ipv6_address=addr_b;refreshed.mikun2n_ipv6_address.port++;
    mikun2n_ipv6_update_peer(ab,&refreshed,1095);
    assert(ab->mikun2n_ipv6_path_bytes==1418 && mikun2n_ipv6_active(a,ab,1095));
    assert(sock_equal(&ab->mikun2n_ipv6_path_address,&addr_b));
    puts("PASS: same-session candidate refresh preserves checked mapping and size");

    assert(!ba->mikun2n_ipv6_reassembly);
    sent_count=0; ab->mikun2n_ipv6_path_bytes=1232;
    assert(mikun2n_ipv6_send(a,ab,original,sizeof(original),1100)==1 && sent_count==2);
    assert(sent_packets[0].size<=1232 && sent_packets[1].size<=1232);
    assert(deliver(1,b,&addr_a,1110,out)==0);
    assert(deliver(1,b,&addr_a,1111,out)==0);
    assert(deliver(0,b,&addr_a,1112,out)==sizeof(original));
    assert(!memcmp(out,original,sizeof(original)));
    assert(deliver(0,b,&addr_a,1113,out)==0 && deliver(1,b,&addr_a,1114,out)==0);
    puts("PASS: oversized DATA stays IPv6, reorder, duplicates, exact reassembly");

    struct peer_info replacement={0};
    memcpy(replacement.mac_addr,ab->mac_addr,6);
    replacement.mikun2n_ipv6_token=ab->mikun2n_ipv6_token;
    replacement.mikun2n_ipv6_path_address=addr_b;
    replacement.mikun2n_ipv6_seen_ms=1000;
    replacement.mikun2n_ipv6_valid_until_ms=replacement.mikun2n_ipv6_peer_ready_until_ms=7000;
    replacement.mikun2n_ipv6_path_bytes=1232;
    uint64_t previous_id=ipv6_get_u64(sent_packets[0].bytes+33);
    sent_count=0;
    assert(mikun2n_ipv6_send(a,&replacement,original,sizeof(original),1150));
    assert(ipv6_get_u64(sent_packets[0].bytes+33)>previous_id);
    assert(!deliver(0,b,&addr_a,1151,out) && deliver(1,b,&addr_a,1152,out)==sizeof(original));
    puts("PASS: recreating a sender peer does not reuse fragment IDs");

    memset(ba->mikun2n_ipv6_reassembly,0,sizeof(*ba->mikun2n_ipv6_reassembly));
    assert(deliver(0,b,&addr_a,1200,out)==0);
    assert(deliver(1,b,&addr_a,3201,out)==0);
    memset(ba->mikun2n_ipv6_reassembly,0,sizeof(*ba->mikun2n_ipv6_reassembly));
    assert(deliver(0,b,&addr_a,3300,out)==0);
    sent_packets[0].bytes[IPV6_FRAGMENT_HEADER]^=1;
    assert(deliver(0,b,&addr_a,3301,out)==0);
    assert(deliver(1,b,&addr_a,3302,out)==0);
    sent_packets[0].bytes[IPV6_FRAGMENT_HEADER]^=1;
    puts("PASS: missing-fragment expiry and conflicting overlap do not deliver");

    mikun2n_ipv6_reassembly_t state={0};
    for(unsigned i=1;i<=5;++i) assert(!mikun2n_ipv6_reassemble(&state,1000,i,1342,0,original,10,out));
    unsigned used=0;for(unsigned i=0;i<4;++i)used+=state.slots[i].id!=0;assert(used==4);
    assert(!mikun2n_ipv6_reassemble(&state,1001,9,2049,0,original,10,out));
    assert(!mikun2n_ipv6_reassemble(&state,1001,9,1342,1340,original,10,out));
    assert(!mikun2n_ipv6_reassemble(&state,3001,9,1342,0,original,10,out));
    assert(state.slots[0].id==9);
    puts("PASS: bounded memory, range rejection, expired-slot reuse");

    memset(&state,0,sizeof(state));
    for(uint64_t id=1;id<=1200;id++)
        assert(mikun2n_ipv6_reassemble(&state,4000,id,10,0,original,10,out)==10);
    for(uint64_t id=1;id<=1200;id++)
        assert(!mikun2n_ipv6_reassemble(&state,4001,id,10,0,original,10,out));
    assert(mikun2n_ipv6_reassemble(&state,9000,1,10,0,original,10,out)==10);
    assert(!mikun2n_ipv6_reassemble(&state,9000,1,10,0,original,10,out));
    assert(mikun2n_ipv6_reassemble(&state,9001,1300,10,0,original,10,out)==10);
    assert(mikun2n_ipv6_reassemble(&state,9001,1250,10,0,original,10,out)==10);
    assert(!mikun2n_ipv6_reassemble(&state,9002,1250,10,0,original,10,out));
    printf("PASS: 1200 datagrams cannot reopen duplicates; in-window reorder; peer=%u bytes, lazy assembly=%u bytes\n",
           (unsigned)sizeof(*ab),(unsigned)sizeof(state));

    uint64_t token=ba->mikun2n_ipv6_token;ba->mikun2n_ipv6_token++;
    assert(deliver(0,b,&addr_a,3400,out)==0);ba->mikun2n_ipv6_token=token;
    sent_packets[0].bytes[3]=2;assert(deliver(0,b,&addr_a,3400,out)==0);sent_packets[0].bytes[3]=3;
    n2n_sock_t wrong=addr_a;wrong.port++;assert(deliver(0,b,&wrong,3400,out)==0);
    puts("PASS: wrong session, protocol generation and endpoint rejected");

    ab->mikun2n_ipv6_valid_until_ms=100000;ab->mikun2n_ipv6_path_bytes=1418;
    ab->mikun2n_ipv6_query_ms=ab->mikun2n_ipv6_search_ms=ab->mikun2n_ipv6_next_probe_ms=UINT64_MAX;
    memset(ab->mikun2n_ipv6_probes,0,sizeof(ab->mikun2n_ipv6_probes));
    for(unsigned i=0;i<3;++i) {ab->mikun2n_ipv6_probes[i].challenge=i+10;ab->mikun2n_ipv6_probes[i].sent_ms=1000;ab->mikun2n_ipv6_probes[i].bytes=1418;}
    ipv6_tick_peers(a,a->known_peers,8000);
    assert(ab->mikun2n_ipv6_path_bytes==1232 && ab->mikun2n_ipv6_valid_until_ms==100000);
    puts("PASS: three large probe losses preserve base-path liveness");

    ab->mikun2n_ipv6_peer_ready_until_ms=100000;ab->mikun2n_ipv6_path_bytes=1418;sent_count=0;
#ifdef _WIN32
    fail_next=WSAEMSGSIZE;
#else
    fail_next=EMSGSIZE;
#endif
    assert(mikun2n_ipv6_send(a,ab,original,sizeof(original),8100)==1 && sent_count==2);
    assert(ab->mikun2n_ipv6_path_bytes==1232 && ab->mikun2n_ipv6_valid_until_ms==100000);
#ifdef _WIN32
    fail_next=WSAEWOULDBLOCK;
#else
    fail_next=EAGAIN;
#endif
    sent_count=0;assert(mikun2n_ipv6_send(a,ab,original,100,8200)==1 && sent_count==0);
    assert(ab->mikun2n_ipv6_valid_until_ms==100000);
#ifdef _WIN32
    fail_next=WSAEMSGSIZE;
#else
    fail_next=EMSGSIZE;
#endif
    assert(!mikun2n_ipv6_send(a,ab,original,100,8201));
    assert(!ab->mikun2n_ipv6_valid_until_ms);
    ab->mikun2n_ipv6_valid_until_ms=8199;assert(!mikun2n_ipv6_send(a,ab,original,100,8200));
    puts("PASS: EMSGSIZE fragments in place, transient queue failure preserves path, base-size failure and real expiry fall back");
    ab->mikun2n_ipv6_reassembly=calloc(1,sizeof(*ab->mikun2n_ipv6_reassembly));
    assert(ab->mikun2n_ipv6_reassembly);
    refreshed.mikun2n_ipv6_token++;
    mikun2n_ipv6_update_peer(ab,&refreshed,8300);
    assert(!ab->mikun2n_ipv6_path_bytes && !ab->mikun2n_ipv6_peer_ready_until_ms && !ab->mikun2n_ipv6_reassembly);
    puts("PASS: a new peer session revokes previous path proof");
    HASH_DEL(a->known_peers,ab);HASH_DEL(b->known_peers,ba);peer_info_free(ab);peer_info_free(ba);free(a);free(b);
    return 0;
}
