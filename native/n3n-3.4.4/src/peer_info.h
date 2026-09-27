/**
 * (C) 2007-22 - ntop.org and contributors
 * Copyright (C) 2023 Hamish Coleman
 *
 * non public structure and function definitions
 */

#ifndef _PEER_INFO_H_
#define _PEER_INFO_H_
#include "mikun2n_ipv6_frag.h"

#include <n2n_typedefs.h>   // for n2n_mac_t, n2n_ip_subnet_t, n2n_desc_t, n2n_sock_t

#define HASH_ADD_PEER(head,add) \
    HASH_ADD(hh,head,mac_addr,sizeof(n2n_mac_t),add)
#define HASH_FIND_PEER(head,mac,out) \
    HASH_FIND(hh,head,mac,sizeof(n2n_mac_t),out)

/* flag used in add_sn_to_list_by_mac_or_sock */
enum skip_add {SN_ADD = 0, SN_ADD_SKIP = 1, SN_ADD_ADDED = 2};

#define MIKUN2N_BANK_WORKERS 25
#define MIKUN2N_IPV6_PROBES 8
#ifdef _WIN32
#define MIKUN2N_INVALID_SOCKET INVALID_SOCKET
#else
#define MIKUN2N_INVALID_SOCKET (-1)
#endif

typedef struct mikun2n_bank_worker {
    SOCKET socket_fd;
    uint16_t local_port;
    uint16_t mapped_a;
    uint16_t mapped_b;
    uint32_t public_ip_a;
    uint32_t public_ip_b;
    uint32_t seq_a;
    uint32_t seq_b;
    uint64_t sent_a_ms;
    uint64_t sent_b_ms;
} mikun2n_bank_worker_t;

typedef struct mikun2n_ipv6_probe {
    uint64_t challenge;
    uint64_t sent_ms;
    n2n_sock_t destination;
    uint16_t bytes;
} mikun2n_ipv6_probe_t;

struct peer_info {
    n2n_mac_t mac_addr;
    bool purgeable;
    uint8_t local;
    n2n_ip_subnet_t dev_addr;
    n2n_desc_t dev_desc;
    n2n_sock_t sock;
    SOCKET socket_fd;
    n2n_sock_t preferred_sock;
    n2n_cookie_t last_cookie;
    n2n_auth_t auth;
    int timeout;
    time_t last_seen;
    time_t last_p2p;
    time_t last_sent_query;
    time_t time_alloc;
    SN_SELECTION_CRITERION_DATA_TYPE selection_criterion;
    uint64_t last_valid_time_stamp;
    char *hostname;
    time_t uptime;
    n2n_version_t version;
    uint8_t mikun2n_ipv6_wire_version;
    time_t punch_started;
    uint64_t punch_last_ms;
    uint32_t punch_attempt;
    uint32_t punch_packets;
    uint16_t punch_observed_port;
    int16_t punch_drift;
    uint8_t punch_exhausted;
    uint8_t punch_rounds;
    uint8_t punch_abandoned;
    time_t punch_retry_at;
    uint8_t punch_role;
    uint16_t punch_band_lo;
    uint16_t punch_band_hi;
    uint8_t force_relay;
    uint8_t mikun2n_nat_kind;
    uint8_t mikun2n_eim_matches;
    uint8_t mikun2n_eim_samples;
    uint32_t mikun2n_punch_nonce;
    uint32_t punch_generation;
    uint64_t punch_go_at_ms;
    uint64_t punch_coord_started_ms;
    uint64_t punch_coord_last_query_ms;
    uint8_t punch_plan_ready;
    uint32_t punch_peer_nonce;

    /* Supernode-side report cache. Only one NAT4 bank calibration is active
     * per edge at a time, and the target MAC binds the report to that pair. */
    n2n_mac_t mikun2n_bank_target;
    uint8_t mikun2n_bank_mode;
    int8_t mikun2n_bank_direction;
    uint8_t mikun2n_bank_workers;
    uint8_t mikun2n_bank_reuse;
    uint16_t mikun2n_bank1;
    uint16_t mikun2n_bank2;
    uint16_t mikun2n_bank_spread;
    uint16_t mikun2n_bank_rate;
    uint32_t mikun2n_bank_nonce;
    uint32_t mikun2n_bank_generation;
    uint64_t mikun2n_bank_report_ms;
    uint64_t mikun2n_go_deadline_ms;
    uint32_t mikun2n_go_local_bank_nonce;
    uint32_t mikun2n_go_peer_bank_nonce;

    /* Edge-side NAT4 worker pool. The winning socket remains attached to the
     * peer after the other calibration sockets have been closed. */
    mikun2n_bank_worker_t punch_workers[MIKUN2N_BANK_WORKERS];
    SOCKET punch_data_sock;
    time_t punch_keepalive_at;
    uint8_t punch_bank_state;
    uint8_t punch_bank_worker_count;
    uint8_t punch_bank_model_mode;
    int8_t punch_bank_direction;
    uint8_t punch_bank_reuse;
    uint16_t punch_bank1;
    uint16_t punch_bank2;
    uint16_t punch_bank_spread;
    uint16_t punch_bank_rate;
    uint32_t punch_bank_nonce;
    uint64_t punch_bank_deadline_ms;
    uint64_t punch_bank_fit_ms;
    uint8_t punch_bank_recalib;
    uint8_t punch_peer_bank_ready;
    uint8_t punch_peer_bank_mode;
    int8_t punch_peer_bank_direction;
    uint8_t punch_peer_bank_workers;
    uint8_t punch_peer_bank_reuse;
    uint16_t punch_peer_bank1;
    uint16_t punch_peer_bank2;
    uint16_t punch_peer_bank_spread;
    uint16_t punch_peer_bank_rate;
    uint32_t punch_peer_bank_nonce;
    uint8_t punch_coord_misses;
    uint64_t punch_bank_retry_ms;
    uint64_t punch_ipv6_lost_ms;
    uint64_t mikun2n_peer_info_ms;
    time_t punch_recover_since;

    n2n_sock_t mikun2n_ipv6_address;
    /* Advertised candidates can be ULA; only a checked public endpoint carries data. */
    n2n_sock_t mikun2n_ipv6_path_address;
    uint64_t mikun2n_ipv6_token;
    uint64_t mikun2n_ipv6_seen_ms;
    uint64_t mikun2n_ipv6_query_ms;
    mikun2n_ipv6_probe_t mikun2n_ipv6_probes[MIKUN2N_IPV6_PROBES];
    uint8_t mikun2n_ipv6_probe_index;
    uint64_t mikun2n_ipv6_next_probe_ms;
    uint64_t mikun2n_ipv6_next_learn_ms;
    uint64_t mikun2n_ipv6_valid_until_ms;
    uint64_t mikun2n_ipv6_peer_ready_until_ms;
    uint64_t mikun2n_ipv6_ready_report_ms;
    uint64_t mikun2n_ipv6_ready_seen_ms;
    uint64_t mikun2n_ipv6_legacy_log_ms;
    uint64_t punch_ipv6_stable_ms;
    uint64_t punch_ipv6_paused_ms;
    uint32_t mikun2n_ipv6_rtt_ms;
    uint8_t mikun2n_ipv6_attempts;
    uint16_t mikun2n_ipv6_probe_bytes;
    uint16_t mikun2n_ipv6_path_bytes;
    uint64_t mikun2n_ipv6_oversize_log_ms;
    uint64_t mikun2n_ipv6_search_ms;
    uint8_t mikun2n_ipv6_large_failures;
    uint16_t mikun2n_ipv6_peer_rx_limit;
    /* Allocated only by an edge receiving fragments; supernodes never allocate it. */
    mikun2n_ipv6_reassembly_t *mikun2n_ipv6_reassembly;

    UT_hash_handle hh;     /* makes this structure hashable */
};

typedef struct peer_info peer_info_t;

void peer_info_init (struct peer_info *, const n2n_mac_t mac);
struct peer_info* peer_info_malloc (const n2n_mac_t mac);
void peer_info_free (struct peer_info *);
struct peer_info* peer_info_validate (struct peer_info **, struct peer_info *);

char *peer_info_get_hostname (struct peer_info *);

/* Operations on peer_info lists. */
size_t purge_peer_list (struct peer_info ** peer_list,
                        SOCKET socket_not_to_close,
                        n2n_tcp_connection_t **tcp_connections,
                        time_t purge_before);

size_t clear_peer_list (struct peer_info ** peer_list);

size_t purge_expired_nodes (struct peer_info **peer_list,
                            SOCKET socket_not_to_close,
                            n2n_tcp_connection_t **tcp_connections,
                            time_t *p_last_purge,
                            int frequency, int timeout);

struct peer_info* add_sn_to_list_by_mac_or_sock (
    struct peer_info **sn_list,
    n2n_sock_t *sock,
    const n2n_mac_t mac,
    int *skip_add
);

int find_and_remove_peer (struct peer_info **, const n2n_mac_t);
struct peer_info* find_peer_by_sock (const n2n_sock_t *, struct peer_info *);

int find_peer_time_stamp_and_verify (
    struct peer_info *peers1,
    struct peer_info *peers2,
    struct peer_info *sn,
    const n2n_mac_t mac,
    uint64_t stamp,
    int allow_jitter
);

#endif
