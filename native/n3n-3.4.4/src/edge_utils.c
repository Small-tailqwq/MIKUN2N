/**
 * (C) 2007-22 - ntop.org and contributors
 * Copyright (C) 2023-24 Hamish Coleman
 * SPDX-License-Identifier: GPL-3.0-only
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not see see <http://www.gnu.org/licenses/>
 *
 */

#ifdef _WIN32
#include "win32/defs.h"
#endif

#include <connslot/connslot.h>
#include <errno.h>                   // for errno, EAFNOSUPPORT, EINPROGRESS
#include <fcntl.h>                   // for fcntl, F_SETFL, O_NONBLOCK
#include <n3n/conffile.h>            // for n3n_config_load_env
#include <n3n/peer_info.h>           // for n3n_peer_add_by_hostname
#include <n3n/ethernet.h>            // for is_null_mac
#include <n3n/logging.h>             // for traceEvent
#include <n3n/metrics.h>
#include <n3n/network_traffic_filter.h>  // for create_network_traffic_filte...
#include <n3n/random.h>              // for n3n_rand, n3n_rand_sqr
#include <n3n/strings.h>             // for sock_to_cstr
#include <n3n/transform.h>           // for n3n_compression_id2str, n3n_tran...
#include <stdbool.h>
#include <stdint.h>                  // for uint8_t, uint16_t, uint32_t, uin...
#include <stdio.h>                   // for snprintf, sprintf
#include <stdlib.h>                  // for free, calloc, getenv
#include <string.h>                  // for memcpy, memset, NULL, memcmp
#include <sys/time.h>                // for timeval
#include <sys/types.h>               // for time_t, ssize_t, u_int
#include <time.h>                    // for time
#include <unistd.h>                  // for gethostname, sleep
#include "auth.h"                    // for generate_private_key
#include "header_encryption.h"       // for packet_header_encrypt, packet_he...
#include "management.h"              // for readFromMgmtSocket
#include "mikun2n_ipv6.h"
#include "mikun2n_build_version.h"
#include "n2n.h"                     // for n3n_runtime_data, n2n_edge_...
#include "n2n_wire.h"                // for fill_sockaddr, decod...
#include "pearson.h"                 // for pearson_hash_128, pearson_hash_64
#include "peer_info.h"               // for peer_info, clear_peer_list, ...
#include "portable_endian.h"         // for be16toh, htobe16
#include "resolve.h"                 // for resolve_create_thread, resolve_c...
#include "sn_selection.h"            // for sn_selection_criterion_common_da...
#include "speck.h"                   // for speck_128_decrypt, speck_128_enc...
#include "uthash.h"                  // for UT_hash_handle, HASH_COUNT, HASH...

#ifdef _WIN32
#include <direct.h>                  // for _mkdir
#include <windows.h>                 // for process and memory diagnostics
#include "win32/edge_utils_win32.h"
#else
#include <arpa/inet.h>               // for inet_ntoa, inet_addr, inet_ntop
#include <netinet/in.h>              // for sockaddr_in, ntohl, IPPROTO_IP
#include <netinet/tcp.h>             // for TCP_NODELAY
#include <sys/select.h>              // for select, FD_SET, FD_ISSET, FD_ZERO
#include <sys/socket.h>              // for setsockopt, AF_INET, connect
#endif


/* ************************************** */

static void send_register (struct n3n_runtime_data *eee, const n2n_sock_t *remote_peer, const n2n_mac_t peer_mac, n2n_cookie_t cookie);

static void check_peer_registration_needed (struct n3n_runtime_data *eee,
                                            uint8_t from_supernode,
                                            uint8_t via_multicast,
                                            const n2n_mac_t mac,
                                            const n2n_cookie_t cookie,
                                            const n2n_ip_subnet_t *dev_addr,
                                            const n2n_desc_t *dev_desc,
                                            const n2n_sock_t *peer);

static int edge_init_sockets (struct n3n_runtime_data *eee);
static int mikun2n_path_recoverable (const struct n3n_runtime_data *eee,
                                     const struct peer_info *pp,
                                     time_t now);

static void check_known_peer_sock_change (struct n3n_runtime_data *eee,
                                          uint8_t from_supernode,
                                          uint8_t via_multicast,
                                          const n2n_mac_t mac,
                                          const n2n_ip_subnet_t *dev_addr,
                                          const n2n_desc_t *dev_desc,
                                          const n2n_sock_t *peer,
                                          time_t when);

#define MIKUN2N_PROBE_PORT_A 21001
#define MIKUN2N_PROBE_PORT_B 21002
#define MIKUN2N_NAT_SAMPLE_ROUNDS 5
#define MIKUN2N_NAT_PROBE_INTERVAL_MS 250
#define MIKUN2N_NAT_CROSS_WAIT_MS 750
#define MIKUN2N_NAT_PROBE_TIMEOUT_MS 10000
#define MIKUN2N_COORD_QUERY_MS 750
#define MIKUN2N_COORD_FALLBACK_MS 4000
#define MIKUN2N_PUNCH_RETRY_SECS 30
#define MIKUN2N_PUNCH_MAX_ROUNDS 3
#define MIKUN2N_RELAY_KEEPALIVE_SECS 5
#define MIKUN2N_NATIVE_GRACE_SECS 5
#define MIKUN2N_PUNCH_BUDGET_SECS 25
#define MIKUN2N_PUNCH_TICK_MS 250
#define MIKUN2N_PUNCH_MAX_PACKETS 12000
#define MIKUN2N_CONE_ESCAPE_NEAR 64
#define MIKUN2N_CONE_ESCAPE_BAND 192
#define MIKUN2N_CONE_ESCAPE_TICKS 4
#define MIKUN2N_LAYERED_ESCAPE_MAX 4096
#define MIKUN2N_LAYERED_ESCAPE_TICKS 63
#define MIKUN2N_PUNCH_COOKIE 0x0000C000
#define MIKUN2N_PUNCH_COOKIE_MASK 0x0000C000
#define MIKUN2N_PUNCH_OFFSET_MASK 0x00003FFF
#define MIKUN2N_PUNCH_OFFSET_BIAS 8192
#define MIKUN2N_BANK_CALIBRATION_MS 800
#define MIKUN2N_BANK_SPRAY_MS 7000
#define MIKUN2N_BANK_TICK_MS 100

/* natpunch v7.3 "fast-cycle" lesson: a single-bank NAT allocating ports at
 * >=120/s (typical of mobile CGNAT) cannot be tracked as a rate - field data
 * shows 150-230/s swings round to round and thousands of ports of drift
 * inside one 7s spray, so the rate is used only to classify such NATs as
 * HARD (one wide-window scan, then settle on relay). */
#define MIKUN2N_BANK_FAST_RATE_MIN 120

/* Bank-model freshness: a calibrated model is only good for a few seconds.
 * If the peer model has not arrived by then, recalibrate (max twice) so the
 * GO-time model age stays bounded instead of drifting tens of ports past the
 * predicted band; the supernode refuses reports older than its TTL. */
#define MIKUN2N_BANK_MODEL_AGE_MS 5000
#define MIKUN2N_BANK_RECALIB_MAX 2
#define MIKUN2N_BANK_COORD_RETRIES 2
#define MIKUN2N_BANK_COORD_RETRY_MS 10000

/* Pending entries that have neither sent anything nor been answered for by the
 * supernode for this long are treated as departed identities. Longer than one
 * Tier 1 round, during which a plan-holding edge sends no coordination query. */
#define MIKUN2N_PEER_SILENT_SECS 30
/* Resume IPv4 scanning only after IPv6 has stayed unavailable this long; short
 * readiness gaps otherwise restarted worker calibration dozens of times. */
#define MIKUN2N_IPV6_RESUME_HOLD_MS 15000
/* An established direct path that stops receiving is kept, and probed from the
 * socket that owns its NAT mapping, before being torn down for a full re-punch. */
#define MIKUN2N_RECOVER_SECS 20

#define MIKUN2N_BANK_STATE_NONE 0
#define MIKUN2N_BANK_STATE_WAIT_A 1
#define MIKUN2N_BANK_STATE_WAIT_B 2
#define MIKUN2N_BANK_STATE_REPORTED 3
#define MIKUN2N_BANK_STATE_ARMED 4
#define MIKUN2N_BANK_STATE_SPRAY 5
#define MIKUN2N_BANK_STATE_FALLBACK 6

/* ************************************** */

static const char *mikun2n_punch_role_name (uint8_t role) {
    switch(role) {
        case MIKUN2N_PUNCH_ROLE_ANCHOR: return "anchor";
        case MIKUN2N_PUNCH_ROLE_SCANNER: return "scanner";
        case MIKUN2N_PUNCH_ROLE_LAYERED: return "layered";
        default: return "none";
    }
}

static uint8_t mikun2n_nat_kind (const mikun2n_nat_state_t *nat) {
    if(!nat->complete)
        return MIKUN2N_NAT_KIND_UNKNOWN;
    if(nat->eim_uncertain)
        return MIKUN2N_NAT_KIND_UNCERTAIN;
    return !strcmp(nat->mapping, "endpoint-independent")
           ? MIKUN2N_NAT_KIND_EIM : MIKUN2N_NAT_KIND_APDM;
}

static int mikun2n_peer_force_relay (struct n3n_runtime_data *eee,
                                     struct peer_info *peer) {
    uint8_t i;

    if(peer->force_relay)
        return 1;
    for(i = 0; i < eee->mikun2n_forced_relay_count; i++) {
        /* Match on either key. A peer entry first learned from a data PACKET carries
         * no dev_addr - only a REGISTER does - so an IPv4-only policy would never
         * attach to it and the pSp agreement would hold on one side only. */
        if((peer->dev_addr.net_addr != 0 &&
            peer->dev_addr.net_addr == eee->mikun2n_forced_relay_ips[i]) ||
           (!is_null_mac(eee->mikun2n_forced_relay_macs[i]) &&
            !memcmp(peer->mac_addr, eee->mikun2n_forced_relay_macs[i], sizeof(n2n_mac_t)))) {
            peer->force_relay = 1;
            return 1;
        }
    }
    return 0;
}

static mikun2n_punch_history_t *mikun2n_find_punch_history (
    struct n3n_runtime_data *eee,
    const n2n_mac_t mac,
    int create) {
    uint8_t i;

    for(i = 0; i < eee->mikun2n_punch_history_count; i++) {
        if(!memcmp(eee->mikun2n_punch_history[i].mac, mac,
                   sizeof(n2n_mac_t)))
            return &eee->mikun2n_punch_history[i];
    }
    if(!create ||
       eee->mikun2n_punch_history_count >= MIKUN2N_PUNCH_HISTORY_MAX)
        return NULL;

    i = eee->mikun2n_punch_history_count++;
    memset(&eee->mikun2n_punch_history[i], 0,
           sizeof(eee->mikun2n_punch_history[i]));
    memcpy(eee->mikun2n_punch_history[i].mac, mac, sizeof(n2n_mac_t));
    return &eee->mikun2n_punch_history[i];
}

static void mikun2n_clear_punch_history (struct n3n_runtime_data *eee,
                                         const n2n_mac_t mac) {
    uint8_t i;

    for(i = 0; i < eee->mikun2n_punch_history_count; i++) {
        if(memcmp(eee->mikun2n_punch_history[i].mac, mac,
                  sizeof(n2n_mac_t)))
            continue;
        eee->mikun2n_punch_history_count--;
        eee->mikun2n_punch_history[i] =
            eee->mikun2n_punch_history[eee->mikun2n_punch_history_count];
        return;
    }
}

static void mikun2n_apply_punch_history (struct n3n_runtime_data *eee,
                                         struct peer_info *peer,
                                         time_t now) {
    mikun2n_punch_history_t *history =
        mikun2n_find_punch_history(eee, peer->mac_addr, 0);

    if(!history)
        return;
    peer->punch_rounds = history->rounds;
    peer->punch_retry_at = history->retry_at;
    peer->punch_abandoned = history->abandoned;
    if(history->abandoned || now < history->retry_at)
        peer->punch_exhausted = 1;
}

static int mikun2n_record_punch_failure (struct n3n_runtime_data *eee,
                                         struct peer_info *peer,
                                         time_t now) {
    mikun2n_punch_history_t *history =
        mikun2n_find_punch_history(eee, peer->mac_addr, 1);

    if(history) {
        if(history->rounds < UINT8_MAX)
            history->rounds++;
        history->retry_at = now + MIKUN2N_PUNCH_RETRY_SECS;
        history->abandoned =
            history->rounds >= MIKUN2N_PUNCH_MAX_ROUNDS;
        peer->punch_rounds = history->rounds;
        peer->punch_retry_at = history->retry_at;
        peer->punch_abandoned = history->abandoned;
    } else {
        /* The fixed session table is deliberately bounded. Preserve the old
         * per-entry behavior if a session somehow has more than 64 peers. */
        peer->punch_rounds++;
        peer->punch_retry_at = now + MIKUN2N_PUNCH_RETRY_SECS;
        peer->punch_abandoned =
            peer->punch_rounds >= MIKUN2N_PUNCH_MAX_ROUNDS;
    }
    peer->punch_exhausted = 1;
    return peer->punch_abandoned;
}

static struct n3n_metrics_items_uint32 edge_utils_metrics_items1[] = {
    {
        .name = "tx_tuntap_error",
        .offset = offsetof(struct n2n_edge_stats, tx_tuntap_error),
    },
    { },
};

static struct n3n_metrics_items_llu32 edge_utils_metrics_items2 = {
    .name = "packets",
    .name1 = "direction",
    .name2 = "event",
    .items = {
        {
            .val1 = "tx",
            .val2 = "p2p",
            .offset = offsetof(struct n2n_edge_stats, tx_p2p),
        },
        {
            .val1 = "rx",
            .val2 = "p2p",
            .offset = offsetof(struct n2n_edge_stats, rx_p2p),
        },
        {
            .val1 = "tx",
            .val2 = "sup",
            .offset = offsetof(struct n2n_edge_stats, tx_sup),
        },
        {
            .val1 = "rx",
            .val2 = "sup",
            .offset = offsetof(struct n2n_edge_stats, rx_sup),
        },
        {
            .val1 = "tx",
            .val2 = "sup_broadcast",
            .offset = offsetof(struct n2n_edge_stats, tx_sup_broadcast),
        },
        {
            .val1 = "rx",
            .val2 = "sup_broadcast",
            .offset = offsetof(struct n2n_edge_stats, rx_sup_broadcast),
        },
        {
            .val1 = "tx",
            .val2 = "multicast_drop",
            .offset = offsetof(struct n2n_edge_stats, tx_multicast_drop),
        },
        {
            .val1 = "rx",
            .val2 = "multicast_drop",
            .offset = offsetof(struct n2n_edge_stats, rx_multicast_drop),
        },
        { },
    },
};

static struct n3n_metrics_module edge_metrics_module1 = {
    .name = "edge",
    .items_uint32 = edge_utils_metrics_items1,
    .type = n3n_metrics_type_uint32,
};

static struct n3n_metrics_module edge_metrics_module2 = {
    .name = "edge",
    .items_llu32 = &edge_utils_metrics_items2,
    .type = n3n_metrics_type_llu32,
};

/* ************************************** */

int edge_verify_conf (const n2n_edge_conf_t *conf) {

    if(conf->community_name[0] == 0)
        return -1;

    if(HASH_COUNT(conf->supernodes) == 0)
        return -5;

    if(conf->register_interval < 1)
        return -3;

    if(((conf->encrypt_key == NULL) && (conf->transop_id != N2N_TRANSFORM_ID_NULL)) ||
       ((conf->encrypt_key != NULL) && (conf->transop_id == N2N_TRANSFORM_ID_NULL)))
        return -4;

    return 0;
}


/* ************************************** */



/** Destination 01:00:5E:00:00:00 - 01:00:5E:7F:FF:FF is multicast ethernet.
 */
static int is_ethMulticast (const void * buf, size_t bufsize) {

    int retval = 0;

    /* Match 01:00:5E:00:00:00 - 01:00:5E:7F:FF:FF */
    if(bufsize >= sizeof(ether_hdr_t)) {
        /* copy to aligned memory */
        ether_hdr_t eh;
        memcpy(&eh, buf, sizeof(ether_hdr_t));

        if((0x01 == eh.dhost[0]) &&
           (0x00 == eh.dhost[1]) &&
           (0x5E == eh.dhost[2]) &&
           (0 == (0x80 & eh.dhost[3])))
            retval = 1; /* This is an ethernet multicast packet [RFC1112]. */
    }

    return retval;
}

/* ************************************** */

/** Destination MAC 33:33:0:00:00:00 - 33:33:FF:FF:FF:FF is reserved for IPv6
 *    neighbour discovery.
 */
static int is_ip6_discovery (const void * buf, size_t bufsize) {

    int retval = 0;

    if(bufsize >= sizeof(ether_hdr_t)) {
        /* copy to aligned memory */
        ether_hdr_t eh;

        memcpy(&eh, buf, sizeof(ether_hdr_t));

        if((0x33 == eh.dhost[0]) && (0x33 == eh.dhost[1]))
            retval = 1; /* This is an IPv6 multicast packet [RFC2464]. */
    }

    return retval;
}


/* ************************************** */


// reset number of supernode connection attempts: try only once for already more realiable tcp connections
void reset_sup_attempts (struct n3n_runtime_data *eee) {

    eee->sup_attempts = (eee->conf.connect_tcp) ? 1 : N2N_EDGE_SUP_ATTEMPTS;
}


// detect local IP address by probing a connection to the supernode
// TODO: should try to refactor this function to be more accurate and handle
// error cases better
static int detect_local_ip_address (n2n_sock_t* out_sock, const struct n3n_runtime_data* eee) {

    struct sockaddr_in local_sock;
    struct sockaddr_in sn_sock;
    socklen_t sock_len;
    SOCKET probe_sock;
    int ret = 0;

    memset(out_sock, 0, sizeof(*out_sock));
    out_sock->family = AF_INVALID;

    // always detect local port even/especially if chosen by OS...
    sock_len = sizeof(local_sock);
    if(getsockname(eee->sock, (struct sockaddr *)&local_sock, &sock_len) != 0) {
        return -1;
    }

    // TODO this whole function doesnt work with IPv6
    if(local_sock.sin_family != AF_INET) {
        return -1;
    }

    if(sock_len != sizeof(local_sock)) {
        return -1;
    }

    // remember the port number
    out_sock->port = ntohs(local_sock.sin_port);

    // probe for local IP address
    probe_sock = socket(PF_INET, SOCK_DGRAM, 0);
    if(probe_sock < 0) {
        return -2;
    }

    // connecting the UDP socket makes getsockname read the local address it
    // uses to connect (to the sn in this case);  we cannot do it with the
    // real (eee->sock) socket because socket does not accept any conenction
    // from elsewhere then, e.g. from another edge instead of the supernode;
    // as re-connecting to AF_UNSPEC might not work to release the socket
    // on non-UNIXoids, we use a temporary socket

    fill_sockaddr((struct sockaddr*)&sn_sock, sizeof(sn_sock), &eee->curr_sn->sock);
    if(connect(probe_sock, (struct sockaddr *)&sn_sock, sizeof(sn_sock)) != 0) {
        closesocket(probe_sock);
        return -3;
    }

    sock_len = sizeof(local_sock);
    if(getsockname(probe_sock, (struct sockaddr *)&local_sock, &sock_len) != 0) {
        closesocket(probe_sock);
        return -4;
    }

    if(local_sock.sin_family != AF_INET) {
        closesocket(probe_sock);
        return -4;
    }

    if(sock_len != sizeof(local_sock)) {
        closesocket(probe_sock);
        return -4;
    }

    memcpy(&(out_sock->addr.v4), &(local_sock.sin_addr.s_addr), IPV4_SIZE);

    closesocket(probe_sock);

    out_sock->family = AF_INET;

    return ret;
}


// open socket, close it before if TCP
// in case of TCP, 'connect()' is required
void supernode_connect (struct n3n_runtime_data *eee) {

    int sockopt;
    struct sockaddr_in sn_sock;
    n2n_sock_t local_sock;
    n2n_sock_str_t sockbuf;

    if((eee->conf.connect_tcp) && (eee->sock >= 0)) {
        closesocket(eee->sock);
        eee->sock = -1;
    }

    if(eee->sock < 0) {

        eee->sock = open_socket(
            eee->conf.bind_address,
            sizeof(struct sockaddr_in), // FIXME this forces only IPv4 bindings
            eee->conf.connect_tcp
        );

        if(eee->sock < 0) {
            traceEvent(TRACE_ERROR, "failed to bind main UDP port");
            return;
        }

        fill_sockaddr((struct sockaddr*)&sn_sock, sizeof(sn_sock), &eee->curr_sn->sock);

        // set tcp socket to O_NONBLOCK so connect does not hang
        // requires checking the socket for readiness before sending and receving
        if(eee->conf.connect_tcp) {
#ifdef _WIN32
            u_long value = 1;
            ioctlsocket(eee->sock, FIONBIO, &value);
#else
            fcntl(eee->sock, F_SETFL, O_NONBLOCK);
#endif
            if((connect(eee->sock, (struct sockaddr*)&(sn_sock), sizeof(struct sockaddr)) < 0)
               && (errno != EINPROGRESS)) {
                traceEvent(TRACE_INFO, "Error connecting TCP: %i", errno);
                eee->sock = -1;
                return;
            }
        }

        if(eee->conf.tos) {
            /*
             * See https://www.tucny.com/Home/dscp-tos for a quick table of
             * the intended functions of each TOS value
             *
             * Note that the tos value is a byte and the manpage for IP_TOS
             * defines it as a byte, but we hand setsockopt() an int value.
             * This does work on linux, but - TODO, check this on other OS
             */
            sockopt = eee->conf.tos;

            if(setsockopt(eee->sock, IPPROTO_IP, IP_TOS, (char *)&sockopt, sizeof(sockopt)) == 0)
                traceEvent(TRACE_INFO, "TOS set to 0x%x", eee->conf.tos);
            else
                traceEvent(TRACE_WARNING, "could not set TOS 0x%x[%d]: %s", eee->conf.tos, errno, strerror(errno));
        }
#ifdef IP_PMTUDISC_DO
        if(eee->conf.pmtu_discovery) {
            sockopt = IP_PMTUDISC_DO;
        } else {
            sockopt = IP_PMTUDISC_DONT;
        }
        traceEvent(
            TRACE_INFO,
            "Setting pmtu_discovery %s",
            (eee->conf.pmtu_discovery) ? "true" : "false"
        );

        int i = setsockopt(
            eee->sock,
            IPPROTO_IP,
            IP_MTU_DISCOVER,
            &sockopt,
            sizeof(sockopt)
        );

        if(i < 0) {
            traceEvent(
                TRACE_WARNING,
                "Setting pmtu_discovery failed: %s(%d)",
                strerror(errno),
                errno
            );
        }
#else
        traceEvent(TRACE_INFO, "No platform support for setting pmtu_discovery");
#endif

        if(detect_local_ip_address(&local_sock, eee) == 0) {
            /* "auto" used to keep AF_INVALID, so REGISTER_SUPER never carried
             * the detected LAN endpoint and same-LAN peers had to wait for
             * multicast discovery. Advertise the routed local endpoint while
             * retaining multicast as an independent fast path. An explicitly
             * configured address keeps its IP and only inherits the actual
             * data-socket port. */
            if(!eee->mikun2n_preferred_sock_mode_set) {
                eee->mikun2n_preferred_sock_auto =
                    eee->conf.preferred_sock.family == AF_INVALID;
                eee->mikun2n_preferred_sock_mode_set = 1;
            }
            if(eee->mikun2n_preferred_sock_auto) {
                memcpy(&eee->conf.preferred_sock, &local_sock,
                       sizeof(n2n_sock_t));
                traceEvent(TRACE_INFO, "determined and advertising local socket [%s]",
                           sock_to_cstr(sockbuf, &local_sock));
            } else {
                eee->conf.preferred_sock.port = local_sock.port;
            }
        }

        if(eee->cb.sock_opened)
            eee->cb.sock_opened(eee);
    }

    // REVISIT: add mgmt port notification to listener for better mgmt port
    //          subscription support

    return;
}


// always closes the socket
void supernode_disconnect (struct n3n_runtime_data *eee) {
    if(!eee) {
        return;
    }
    if(eee->sock >= 0) {
        closesocket(eee->sock);
        eee->sock = -1;
        traceEvent(TRACE_DEBUG, "closed");
    }
}


/* ************************************** */

/** Initialise an edge to defaults.
 *
 *    This also initialises the NULL transform operation opstruct.
 */
struct n3n_runtime_data* edge_init (const n2n_edge_conf_t *conf, int *rv) {

    n2n_transform_t transop_id = conf->transop_id;
    struct n3n_runtime_data *eee = calloc(1, sizeof(struct n3n_runtime_data));
    int rc = -1, i = 0;
    struct peer_info *scan, *tmp;
    uint8_t tmp_key[N2N_AUTH_CHALLENGE_SIZE];

    if((rc = edge_verify_conf(conf)) != 0) {
        traceEvent(TRACE_ERROR, "invalid configuration");
        goto edge_init_error;
    }

    if(!eee) {
        traceEvent(TRACE_ERROR, "cannot allocate memory");
        goto edge_init_error;
    }


    memcpy(&eee->conf, conf, sizeof(*conf));
    eee->curr_sn = eee->conf.supernodes;
    eee->start_time = time(NULL);

    eee->known_peers        = NULL;
    eee->pending_peers    = NULL;
    reset_sup_attempts(eee);

    sn_selection_criterion_common_data_default(eee);

    // always initialize compression transforms so we can at least decompress
    rc = n2n_transop_lzo_init(&eee->conf, &eee->transop_lzo);
    if(rc) goto edge_init_error; /* error message is printed in lzo_init */
#ifdef HAVE_LIBZSTD
    rc = n2n_transop_zstd_init(&eee->conf, &eee->transop_zstd);
    if(rc) goto edge_init_error; /* error message is printed in zstd_init */
#endif

    traceEvent(TRACE_INFO, "number of supernodes in the list: %d\n", HASH_COUNT(eee->conf.supernodes));
    HASH_ITER(hh, eee->conf.supernodes, scan, tmp) {
        traceEvent(
            TRACE_INFO,
            "supernode %u => %s\n",
            i,
            peer_info_get_hostname(scan)
        );
        i++;
    }

    /* Set active transop */
    switch(transop_id) {
        case N2N_TRANSFORM_ID_TWOFISH:
            rc = n2n_transop_tf_init(&eee->conf, &eee->transop);
            break;

        case N2N_TRANSFORM_ID_AES:
            rc = n2n_transop_aes_init(&eee->conf, &eee->transop);
            break;

        case N2N_TRANSFORM_ID_CHACHA20:
            rc = n2n_transop_cc20_init(&eee->conf, &eee->transop);
            break;

        case N2N_TRANSFORM_ID_SPECK:
            rc = n2n_transop_speck_init(&eee->conf, &eee->transop);
            break;

        default:
            rc = n2n_transop_null_init(&eee->conf, &eee->transop);
    }

    if((rc < 0) || (eee->transop.fwd == NULL) || (eee->transop.transform_id != transop_id)) {
        traceEvent(TRACE_ERROR, "transop init failed");
        goto edge_init_error;
    }

    // set the key schedule (context) for header encryption if enabled
    if(conf->header_encryption == HEADER_ENCRYPTION_ENABLED) {
        traceEvent(TRACE_NORMAL, "Header encryption is enabled.");
        packet_header_setup_key((char *)(eee->conf.community_name),
                                &(eee->conf.header_encryption_ctx_static),
                                &(eee->conf.header_encryption_ctx_dynamic),
                                &(eee->conf.header_iv_ctx_static),
                                &(eee->conf.header_iv_ctx_dynamic));
        // in case of user/password auth, initialize a random dynamic key to prevent
        // unintentional communication with only-header-encrypted community; will be
        // overwritten by legit key later
        if(conf->shared_secret) {
            memrnd(tmp_key, N2N_AUTH_CHALLENGE_SIZE);
            packet_header_change_dynamic_key(tmp_key,
                                             &(eee->conf.header_encryption_ctx_dynamic),
                                             &(eee->conf.header_iv_ctx_dynamic));
        }
    }

    // setup authentication scheme
    if(!conf->shared_secret) {
        // id-based scheme
        eee->conf.auth.scheme = n2n_auth_simple_id;
        // random authentication token
        memrnd(eee->conf.auth.token, N2N_AUTH_ID_TOKEN_SIZE);
        eee->conf.auth.token_size = N2N_AUTH_ID_TOKEN_SIZE;
    } else {
        // user-password scheme
        eee->conf.auth.scheme = n2n_auth_user_password;
        // 'token' stores public key and the last random challenge being set upon sending REGISTER_SUPER
        memcpy(eee->conf.auth.token, eee->conf.public_key, N2N_PRIVATE_PUBLIC_KEY_SIZE);
        // random part of token (challenge) will be generated and filled in at each REGISTER_SUPER
        eee->conf.auth.token_size = N2N_AUTH_PW_TOKEN_SIZE;
        // make sure that only stream ciphers are being used
        if((transop_id != N2N_TRANSFORM_ID_CHACHA20)
           && (transop_id != N2N_TRANSFORM_ID_SPECK)) {
            traceEvent(TRACE_ERROR, "user-password authentication requires ChaCha20 (-A4) or SPECK (-A5) to be used.");
            goto edge_init_error;
        }
    }

    if(eee->transop.no_encryption)
        traceEvent(TRACE_WARNING, "encryption is disabled in edge");

    // first time calling edge_init_sockets needs -1 in the sockets for it does throw an error
    // on trying to close them (open_sockets does so for also being able to RE-open the sockets
    // if called in-between, see "Supernode not responding" in update_supernode_reg(...)
    eee->sock = -1;
    eee->mikun2n_ipv6_socket = MIKUN2N_INVALID_SOCKET;
#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
    eee->udp_multicast_sock = -1;
#endif
    if(edge_init_sockets(eee) < 0) {
        traceEvent(TRACE_ERROR, "socket setup failed");
        goto edge_init_error;
    }

    if(resolve_create_thread(&(eee->resolve_parameter), eee->conf.supernodes) == 0) {
        traceEvent(TRACE_NORMAL, "successfully created resolver thread");
    }

    eee->network_traffic_filter = create_network_traffic_filter();
    network_traffic_filter_add_rule(eee->network_traffic_filter, eee->conf.network_traffic_filter_rules);

    //edge_init_success:
    *rv = 0;
    return(eee);

edge_init_error:
    if(eee)
        free(eee);
    *rv = rc;
    return(NULL);
}

/* ************************************** */

static uint32_t localhost_v4 = 0x7f000001;
static uint8_t localhost_v6[IPV6_SIZE] = {0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1};

/* Exclude localhost as it may be received when an edge node runs
 * in the same supernode host.
 */
static int is_valid_peer_sock (const n2n_sock_t *sock) {

    switch(sock->family) {
        case AF_INET: {
            uint32_t *a = (uint32_t*)sock->addr.v4;

            if(*a != htonl(localhost_v4))
                return(1);
        }
        break;

        case AF_INET6:
            if(memcmp(sock->addr.v6, localhost_v6, IPV6_SIZE))
                return(1);
            break;
    }

    return(0);
}

/* ************************************** */

/***
 *
 * Register over multicast in case there is a peer on the same network listening
 */
static void register_with_local_peers (struct n3n_runtime_data * eee) {
#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
    if(eee->multicast_joined && eee->conf.allow_p2p) {
        /* send registration to the local multicast group */
        traceEvent(TRACE_DEBUG, "registering with multicast group %s:%u",
                   N2N_MULTICAST_GROUP, N2N_MULTICAST_PORT);
        send_register(eee, &(eee->multicast_peer), NULL, N2N_MCAST_REG_COOKIE);
    }
#else
    traceEvent(TRACE_DEBUG, "multicast peers discovery is disabled, skipping");
#endif
}

/* ************************************** */

/** Start the registration process.
 *
 *    If the peer is already in pending_peers, ignore the request.
 *    If not in pending_peers, add it and send a REGISTER.
 *
 *    If hdr is for a direct peer-to-peer packet, try to register back to sender
 *    even if the MAC is in pending_peers. This is because an incident direct
 *    packet indicates that peer-to-peer exchange should work so more aggressive
 *    registration can be permitted (once per incoming packet) as this should only
 *    last for a small number of packets..
 *
 *    Called from the main loop when Rx a packet for our device mac.
 */
static void register_with_new_peer (struct n3n_runtime_data *eee,
                                    uint8_t from_supernode,
                                    uint8_t via_multicast,
                                    const n2n_mac_t mac,
                                    const n2n_ip_subnet_t *dev_addr,
                                    const n2n_desc_t *dev_desc,
                                    const n2n_sock_t *peer) {

    /* REVISIT: purge of pending_peers not yet done. */
    struct peer_info *scan;
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;

    HASH_FIND_PEER(eee->pending_peers, mac, scan);

    /* NOTE: pending_peers are purged periodically with purge_expired_nodes */
    if(scan == NULL) {
        scan = peer_info_malloc(mac);

        scan->sock = *peer;
        scan->timeout = eee->conf.register_interval; /* TODO: should correspond to the peer supernode registration timeout */
        if(via_multicast)
            scan->local = 1;

        HASH_ADD_PEER(eee->pending_peers, scan);

        traceEvent(TRACE_DEBUG, "new pending peer %s [%s]",
                   macaddr_str(mac_buf, scan->mac_addr),
                   sock_to_cstr(sockbuf, &(scan->sock)));

        traceEvent(TRACE_DEBUG, "pending peers list size=%u",
                   HASH_COUNT(eee->pending_peers));
        /* trace Sending REGISTER */
        if(from_supernode) {
            /* UDP NAT hole punching through supernode. Send to peer first(punch local UDP hole)
             * and then ask supernode to forward. Supernode then ask peer to ack. Some nat device
             * drop and block ports with incoming UDP packet if out-come traffic does not exist.
             * So we can alternatively set TTL so that the packet sent to peer never really reaches
             * The register_ttl is basically nat level + 1. Set it to 1 means host like DMZ.
             */
            if(eee->conf.register_ttl == 1) {
                /* We are DMZ host or port is directly accessible. Just let peer to send back the ack */
            } else if(eee->conf.register_ttl > 1) {
                /* register_ttl > 1 enables a full-TTL REGISTER spray across a
                 * window of the peer's supernode-observed port. Model (measured
                 * on a real dual-gateway CGNAT): the symmetric side's fresh
                 * mapping toward us lands either on the same NAT engine as its
                 * supernode registration (port = seen port + small sequential
                 * delta) or on a sibling engine whose counter offset drifts
                 * unpredictably (hundreds of ports over hours). Only the
                 * same-engine case is coverable, so spray a contiguous near
                 * window; the cross-engine case is handled by the client
                 * reconnecting with a fresh local port (rerolls the engine).
                 *
                 * Packets must carry full TTL: from a cone-NAT sender each
                 * sprayed packet both opens our own NAT filter for that exact
                 * peer port and, on a hit, is delivered inside the symmetric
                 * peer's punched session, completing the handshake. The old
                 * low-TTL trick (packets dying mid-path) would defeat both
                 * effects, so the TTL value itself is deliberately not applied
                 * to the socket anymore. */
                n2n_sock_t sock = scan->sock;
                int base_port = scan->sock.port;
                int p;

                static const int spray_lo = -8;
                static const int spray_hi = 80;

                for(p = base_port + spray_lo; p <= base_port + spray_hi; p++) {
                    if(p < 1 || p > 65535) continue;
                    sock.port = (uint16_t) p;
                    send_register(eee, &sock, mac, N2N_PORT_REG_COOKIE);
                }
                /* keep the normal-path REGISTER to the exact seen port too */
                send_register(eee, &(scan->sock), mac, N2N_REGULAR_REG_COOKIE);
            } else { /* eee->conf.register_ttl == 0 */
                /* Normal STUN */
                send_register(eee, &(scan->sock), mac, N2N_REGULAR_REG_COOKIE);
            }
            send_register(eee, &(eee->curr_sn->sock), mac, N2N_FORWARDED_REG_COOKIE);
        } else {
            /* P2P register, send directly */
            send_register(eee, &(scan->sock), mac, N2N_REGULAR_REG_COOKIE);
        }
        register_with_local_peers(eee);
    } else{
        scan->sock = *peer;
    }
    scan->last_seen = time(NULL);
    if(dev_addr != NULL) {
        memcpy(&(scan->dev_addr), dev_addr, sizeof(n2n_ip_subnet_t));
    }
    if(dev_desc) memcpy(scan->dev_desc, dev_desc, N2N_DESC_SIZE);
}


/* ************************************** */

/** Update the last_seen time for this peer, or get registered. */
static void check_peer_registration_needed (struct n3n_runtime_data *eee,
                                            uint8_t from_supernode,
                                            uint8_t via_multicast,
                                            const n2n_mac_t mac,
                                            const n2n_cookie_t cookie,
                                            const n2n_ip_subnet_t *dev_addr,
                                            const n2n_desc_t *dev_desc,
                                            const n2n_sock_t *peer) {

    struct peer_info *scan;

    HASH_FIND_PEER(eee->known_peers, mac, scan);

    /* If we were not able to find it by MAC, we try to find it by socket. */
    if(scan == NULL ) {
        scan = find_peer_by_sock(peer, eee->known_peers);

        // MAC change
        if(scan) {
            HASH_DEL(eee->known_peers, scan);
            memcpy(scan->mac_addr, mac, sizeof(n2n_mac_t));
            HASH_ADD_PEER(eee->known_peers, scan);
            // reset last_local_reg to allow re-registration
            scan->last_cookie = N2N_NO_REG_COOKIE;
        }
    }

    if(scan == NULL) {
        /* Not in known_peers - start the REGISTER process. */
        register_with_new_peer(eee, from_supernode, via_multicast, mac, dev_addr, dev_desc, peer);
    } else {
        /* Already in known_peers. */
        time_t now = time(NULL);

        if(!from_supernode) {
            scan->last_p2p = now;
            mikun2n_clear_punch_history(eee, scan->mac_addr);
        }

        if(via_multicast)
            scan->local = 1;

        /* MikuN2N: only a REGISTER carries the peer's virtual IPv4, so an entry first
         * learned from a data PACKET keeps dev_addr zeroed forever - and the
         * forced-relay policy, which is keyed on that address, then never matches it
         * (management reports "0 peer entries updated" and the pSp request is lost on
         * this side only). Adopt the address as soon as any packet does carry it. */
        if(dev_addr != NULL && dev_addr->net_addr != 0 &&
           scan->dev_addr.net_addr != dev_addr->net_addr) {
            memcpy(&(scan->dev_addr), dev_addr, sizeof(n2n_ip_subnet_t));
            mikun2n_peer_force_relay(eee, scan);
        }

        if(((now - scan->last_seen) > 0 /* >= 1 sec */)
           ||(cookie > scan->last_cookie)) {
            /* Don't register too often */
            check_known_peer_sock_change(eee, from_supernode, via_multicast, mac, dev_addr, dev_desc, peer, now);
        }
    }
}

/* ************************************** */


/* Confirm that a pending peer is reachable directly via P2P.
 *
 * peer must be a pointer to an element of the pending_peers list.
 */
static void peer_set_p2p_confirmed (struct n3n_runtime_data * eee,
                                    const n2n_mac_t mac,
                                    const n2n_cookie_t cookie,
                                    const n2n_sock_t * peer,
                                    time_t now,
                                    SOCKET in_sock) {

    struct peer_info *scan, *scan_tmp;
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;

    HASH_FIND_PEER(eee->pending_peers, mac, scan);
    if(scan == NULL) {
        scan = find_peer_by_sock(peer, eee->pending_peers);
        // in case of MAC change, reset last_local_reg to allow re-registration
        if(scan)
            scan->last_cookie = N2N_NO_REG_COOKIE;
    }

    if(scan) {
        int worker_index;

        HASH_DEL(eee->pending_peers, scan);
        mikun2n_clear_punch_history(eee, mac);

        if(in_sock != MIKUN2N_INVALID_SOCKET && in_sock != eee->sock &&
           in_sock != eee->udp_multicast_sock) {
            for(worker_index = 0;
                worker_index < MIKUN2N_BANK_WORKERS;
                worker_index++) {
                if(scan->punch_workers[worker_index].socket_fd == in_sock) {
                    int close_index;

                    scan->punch_data_sock = in_sock;
                    for(close_index = 0;
                        close_index < MIKUN2N_BANK_WORKERS;
                        close_index++) {
                        if(close_index != worker_index &&
                           scan->punch_workers[close_index].socket_fd !=
                               MIKUN2N_INVALID_SOCKET) {
                            closesocket(
                                scan->punch_workers[close_index].socket_fd);
                            scan->punch_workers[close_index].socket_fd =
                                MIKUN2N_INVALID_SOCKET;
                        }
                    }
                    traceEvent(TRACE_NORMAL,
                               "MikuN2N bank punch promoted worker=%d "
                               "local_port=%u as peer data socket",
                               worker_index,
                               scan->punch_workers[worker_index].local_port);
                    break;
                }
            }
        }

        scan_tmp = find_peer_by_sock(peer, eee->known_peers);
        if(scan_tmp != NULL) {
            HASH_DEL(eee->known_peers, scan_tmp);
            if(scan->punch_data_sock != MIKUN2N_INVALID_SOCKET) {
                /* Keep the bank entry because it owns the winning socket.
                 * A stale known entry for the same endpoint must not replace
                 * it and silently move subsequent traffic back to the control
                 * socket. Preserve any virtual address learned there first. */
                if(scan->dev_addr.net_addr == 0 &&
                   scan_tmp->dev_addr.net_addr != 0)
                    scan->dev_addr = scan_tmp->dev_addr;
                peer_info_free(scan_tmp);
            } else {
                peer_info_free(scan);
                scan = scan_tmp;
                memcpy(scan->mac_addr, mac, sizeof(n2n_mac_t));
                // in case of MAC change, reset cookie to allow immediate re-registration
                scan->last_cookie = N2N_NO_REG_COOKIE;
            }
        } else {
            // update sock but ...
            // ... ignore ACKs's (and their socks) from lower ranked inbound ways for a while
            if(((now - scan->last_seen) > REGISTRATION_TIMEOUT / 4)
               ||(cookie > scan->last_cookie)) {
                scan->sock = *peer;
                scan->last_cookie = cookie;
            }
        }

        HASH_ADD_PEER(eee->known_peers, scan);
        scan->last_p2p = now;
        mgmt_event_post(N3N_EVENT_PEER,N3N_EVENT_PEER_P2P_ADD,scan);
        if(scan->punch_started) {
            int hit_offset = (cookie & MIKUN2N_PUNCH_COOKIE_MASK) == MIKUN2N_PUNCH_COOKIE
                ? (int)(cookie & MIKUN2N_PUNCH_OFFSET_MASK) - MIKUN2N_PUNCH_OFFSET_BIAS
                : 0;
            const char *hit_lane = hit_offset == 0 ? "control" :
                                   abs(hit_offset) <= 64 ? "low" :
                                   abs(hit_offset) <= 256 ? "mid" : "far";
            traceEvent(TRACE_NORMAL,
                       "MikuN2N Tier 1 punch succeeded after %us/%u attempts/%u packets role=%s lane=%s offset=%d peer=%s",
                       (unsigned int)(now - scan->punch_started),
                       scan->punch_attempt,
                       scan->punch_packets,
                       mikun2n_punch_role_name(scan->punch_role),
                       hit_lane,
                       hit_offset,
                       macaddr_str(mac_buf, scan->mac_addr));
        }

        traceEvent(TRACE_DEBUG, "p2p connection established: %s [%s]",
                   macaddr_str(mac_buf, mac),
                   sock_to_cstr(sockbuf, peer));

        traceEvent(TRACE_DEBUG, "new peer %s [%s]",
                   macaddr_str(mac_buf, scan->mac_addr),
                   sock_to_cstr(sockbuf, &(scan->sock)));

        traceEvent(TRACE_DEBUG, "pending peers list size=%u",
                   HASH_COUNT(eee->pending_peers));

        traceEvent(TRACE_DEBUG, "known peers list size=%u",
                   HASH_COUNT(eee->known_peers));

        scan->last_seen = now;
    } else {
        /* Not pending: an ACK from an already-known peer (e.g. a reply to the
         * forced-relay keepalive) still proves the direct path is alive, and
         * without this refresh a canceled relay tears the entry down at the
         * timeout/2 idle check and needs a full re-punch. */
        HASH_FIND_PEER(eee->known_peers, mac, scan);
        if(scan != NULL) {
            scan->last_p2p = now;
            scan->last_seen = now;
        } else
            traceEvent(TRACE_DEBUG, "failed to find sender in pending_peers");
    }
}


/* ************************************** */


// provides the current / a new local auth token
static int get_local_auth (struct n3n_runtime_data *eee, n2n_auth_t *auth) {

    switch(eee->conf.auth.scheme) {
        case n2n_auth_simple_id:
            memcpy(auth, &(eee->conf.auth), sizeof(n2n_auth_t));
            break;
        case n2n_auth_user_password:
            // start from the locally stored complete auth token (including type and size fields)
            memcpy(auth, &(eee->conf.auth), sizeof(n2n_auth_t));

            // the token data consists of
            //    32 bytes public key
            //    16 bytes random challenge

            // generate a new random auth challenge every time
            memrnd(auth->token + N2N_PRIVATE_PUBLIC_KEY_SIZE, N2N_AUTH_CHALLENGE_SIZE);
            // store it in local auth token (for comparison later)
            memcpy(eee->conf.auth.token + N2N_PRIVATE_PUBLIC_KEY_SIZE, auth->token + N2N_PRIVATE_PUBLIC_KEY_SIZE, N2N_AUTH_CHALLENGE_SIZE);
            // encrypt the challenge for transmission
            speck_128_encrypt(auth->token + N2N_PRIVATE_PUBLIC_KEY_SIZE, (speck_context_t*)eee->conf.shared_secret_ctx);
            break;
        default:
            break;
    }

    return 0;
}


// handles a returning (remote) auth token, takes action as required by auth scheme
static int handle_remote_auth (struct n3n_runtime_data *eee, struct peer_info *peer, const n2n_auth_t *remote_auth) {

    uint8_t tmp_token[N2N_AUTH_MAX_TOKEN_SIZE];

    switch(eee->conf.auth.scheme) {
        case n2n_auth_simple_id:
            // no action required
            break;
        case n2n_auth_user_password:
            memcpy(tmp_token, remote_auth->token, N2N_AUTH_PW_TOKEN_SIZE);

            // the returning token data consists of
            //    16 bytes double-encrypted challenge
            //    16 bytes public key (second half)
            //    16 bytes encrypted (original random challenge XOR shared secret XOR dynamic key)

            // decrypt double-encrypted received challenge (first half of public key field)
            speck_128_decrypt(tmp_token, (speck_context_t*)eee->conf.shared_secret_ctx);
            speck_128_decrypt(tmp_token, (speck_context_t*)eee->conf.shared_secret_ctx);

            // compare to original challenge
            if(0 != memcmp(tmp_token, eee->conf.auth.token + N2N_PRIVATE_PUBLIC_KEY_SIZE, N2N_AUTH_CHALLENGE_SIZE))
                return -1;

            // decrypt the received challenge in which the dynamic key is wrapped
            speck_128_decrypt(tmp_token + N2N_PRIVATE_PUBLIC_KEY_SIZE, (speck_context_t*)eee->conf.shared_secret_ctx);
            // un-XOR the original challenge
            memxor(tmp_token + N2N_PRIVATE_PUBLIC_KEY_SIZE, eee->conf.auth.token + N2N_PRIVATE_PUBLIC_KEY_SIZE, N2N_AUTH_CHALLENGE_SIZE);
            // un-XOR the shared secret
            memxor(tmp_token + N2N_PRIVATE_PUBLIC_KEY_SIZE, *(eee->conf.shared_secret), N2N_AUTH_CHALLENGE_SIZE);
            // setup for use as dynamic key
            packet_header_change_dynamic_key(tmp_token + N2N_PRIVATE_PUBLIC_KEY_SIZE,
                                             &(eee->conf.header_encryption_ctx_dynamic),
                                             &(eee->conf.header_iv_ctx_dynamic));
            break;
        default:
            break;
    }

    return 0;
}


/* ************************************** */


int is_empty_ip_address (const n2n_sock_t * sock) {

    const uint8_t * ptr = NULL;
    size_t len = 0;
    size_t i;

    if(AF_INET6 == sock->family) {
        ptr = sock->addr.v6;
        len = 16;
    } else {
        ptr = sock->addr.v4;
        len = 4;
    }

    for(i = 0; i < len; ++i) {
        if(0 != ptr[i]) {
            /* found a non-zero byte in address */
            return 0;
        }
    }

    return 1;
}

/* ************************************** */


/** Check if a known peer socket has changed and possibly register again.
 */
static void check_known_peer_sock_change (struct n3n_runtime_data *eee,
                                          uint8_t from_supernode,
                                          uint8_t via_multicast,
                                          const n2n_mac_t mac,
                                          const n2n_ip_subnet_t *dev_addr,
                                          const n2n_desc_t *dev_desc,
                                          const n2n_sock_t *peer,
                                          time_t when) {

    struct peer_info *scan;
    n2n_sock_str_t sockbuf1;
    n2n_sock_str_t sockbuf2; /* don't clobber sockbuf1 if writing two addresses to trace */
    macstr_t mac_buf;

    if(is_empty_ip_address(peer))
        return;

    if(is_multi_broadcast(mac))
        return;

    /* Search the peer in known_peers */
    HASH_FIND_PEER(eee->known_peers, mac, scan);

    if(!scan)
        /* Not in known_peers */
        return;

    if(!sock_equal(&(scan->sock), peer)) {
        if(!from_supernode) {
            /* This is a P2P packet */
            traceEvent(TRACE_NORMAL, "peer %s changed [%s] -> [%s]",
                       macaddr_str(mac_buf, scan->mac_addr),
                       sock_to_cstr(sockbuf1, &(scan->sock)),
                       sock_to_cstr(sockbuf2, peer));
            /* MikuN2N: this rebuilds the entry from scratch, and most callers have no
             * dev_addr to hand (a data PACKET does not carry one). Dropping it would
             * lose the virtual IPv4 that the forced-relay policy is keyed on, silently
             * turning a two-sided pSp agreement back into a one-sided relay. Carry the
             * address we already learned into the replacement entry. */
            n2n_ip_subnet_t kept_addr = scan->dev_addr;

            /* The peer has changed public socket. It can no longer be assumed to be reachable. */
            HASH_DEL(eee->known_peers, scan);
            mgmt_event_post(N3N_EVENT_PEER,N3N_EVENT_PEER_P2P_CHANGED,scan);
            peer_info_free(scan);

            if((dev_addr == NULL || dev_addr->net_addr == 0) && kept_addr.net_addr != 0)
                dev_addr = &kept_addr;

            register_with_new_peer(eee, from_supernode, via_multicast, mac, dev_addr, dev_desc, peer);
        } else {
            /* Don't worry about what the supernode reports, it could be seeing a different socket. */
        }
    } else
        scan->last_seen = when;
}

/* ************************************** */

/*
 * Confirm that we can send to this edge.
 * TODO: for the TCP case, this could cause a stall in the packet
 * send path, so this probably should be reworked to use a queue
 * (and non blocking IO)
 */
static bool check_sock_ready (struct n3n_runtime_data *eee) {
    if(!eee->conf.connect_tcp) {
        // Just show udp sockets as ready
        // TODO: this is may not be always true
        return true;
    }

    if(eee->sock == -1) {
        // If we have no sock, dont attempt to FD_SET() it
        return false;
    }

    // if required (tcp), wait until writeable as soket is set to
    // O_NONBLOCK, could require some wait time directly after re-opening
    fd_set socket_mask;
    struct timeval wait_time;

    FD_ZERO(&socket_mask);
    FD_SET(eee->sock, &socket_mask);
    wait_time.tv_sec = 0;
    wait_time.tv_usec = 500000;
    return select(eee->sock + 1, NULL, &socket_mask, NULL, &wait_time);
}

#ifdef _WIN32
static void mikun2n_trace_wsa_enobufs (const struct n3n_runtime_data *eee,
                                      size_t len) {
    struct peer_info *peer, *tmp;
    MEMORYSTATUSEX memory;
    DWORD handle_count = 0;
    unsigned int active_workers = 0;
    unsigned int i;

    HASH_ITER(hh, eee->pending_peers, peer, tmp) {
        for(i = 0; i < MIKUN2N_BANK_WORKERS; i++) {
            if(peer->punch_workers[i].socket_fd != MIKUN2N_INVALID_SOCKET)
                active_workers++;
        }
    }

    memset(&memory, 0, sizeof(memory));
    memory.dwLength = sizeof(memory);
    GetProcessHandleCount(GetCurrentProcess(), &handle_count);
    if(GlobalMemoryStatusEx(&memory)) {
        traceEvent(
            TRACE_WARNING,
            "MikuN2N WSAENOBUFS diagnostics: socket=%llu len=%llu "
            "handles=%lu memory_load=%lu%% avail_phys_mb=%llu "
            "known=%u pending=%u workers=%u "
            "tx_p2p=%u tx_supernode=%u tx_broadcast=%u",
            (unsigned long long)eee->sock,
            (unsigned long long)len,
            (unsigned long)handle_count,
            (unsigned long)memory.dwMemoryLoad,
            (unsigned long long)(memory.ullAvailPhys / (1024ULL * 1024ULL)),
            (unsigned int)HASH_COUNT(eee->known_peers),
            (unsigned int)HASH_COUNT(eee->pending_peers),
            active_workers,
            eee->stats.tx_p2p,
            eee->stats.tx_sup,
            eee->stats.tx_sup_broadcast);
    } else {
        traceEvent(
            TRACE_WARNING,
            "MikuN2N WSAENOBUFS diagnostics: socket=%llu len=%llu "
            "handles=%lu known=%u pending=%u workers=%u "
            "tx_p2p=%u tx_supernode=%u tx_broadcast=%u",
            (unsigned long long)eee->sock,
            (unsigned long long)len,
            (unsigned long)handle_count,
            (unsigned int)HASH_COUNT(eee->known_peers),
            (unsigned int)HASH_COUNT(eee->pending_peers),
            active_workers,
            eee->stats.tx_p2p,
            eee->stats.tx_sup,
            eee->stats.tx_sup_broadcast);
    }
}
#endif

/** Send a datagram to a socket file descriptor */
static ssize_t sendto_fd (struct n3n_runtime_data *eee, const void *buf,
                          size_t len, struct sockaddr_in *dest,
                          const n2n_sock_t * n2ndest) {

    ssize_t sent = 0;

    if(!check_sock_ready(eee)) {
        goto err_out;
    }

    sent = sendto(eee->sock, buf, len, 0 /*flags*/,
                  (struct sockaddr *)dest, sizeof(struct sockaddr_in));

    if(sent != -1) {
        // sendto success
        traceEvent(TRACE_DEBUG, "sent=%d", (signed int)sent);
        return sent;
    }

#ifdef _WIN32
    // Winsock's last-error value is thread-local and must be captured before
    // strerror() or logging calls can overwrite it.
    int socket_error = WSAGetLastError();
#endif

    // We only get here if sendto failed, so errno must be valid

    char * errstr = strerror(errno);
    n2n_sock_str_t sockbuf;

    if(!errstr) {
        traceEvent(TRACE_WARNING, "bad strerror");
    }

    int level = TRACE_WARNING;
    // downgrade to TRACE_DEBUG in case of custom AF_INVALID,
    // i.e. supernode not resolved yet
#ifdef _WIN32
    if(socket_error == WSAEAFNOSUPPORT) {
#else
    if(errno == EAFNOSUPPORT /* 93 */) {
#endif
        level = TRACE_DEBUG;
    }

    traceEvent(level, "sendto(%s) failed (%d) %s",
               sock_to_cstr(sockbuf, n2ndest),
               errno, errstr);
#ifdef _WIN32
    traceEvent(level, "WSAGetLastError(): %u", socket_error);
    if(socket_error == WSAENOBUFS)
        mikun2n_trace_wsa_enobufs(eee, len);
#endif

    /*
     * we get here if the sock is not ready or
     * if the sendto had an error
     */
err_out:
    if(eee->conf.connect_tcp) {
        supernode_disconnect(eee);
        eee->sn_wait = 1;
        // Not true if eee->sock == -1
        traceEvent(TRACE_DEBUG, "error in sendto_fd");
    }

    /*
     * If we got an error and are using UDP, this is still an error
     * case.  The only caller of sendto_fd() checks the return only
     * in the TCP case.
     *
     * Thus, we can safely return an error code for any error.
     */
    return -1;
}


/** Send a datagram to a socket defined by a n2n_sock_t */
static void sendto_sock (struct n3n_runtime_data *eee, const void * buf,
                         size_t len, const n2n_sock_t * dest) {

    struct sockaddr_in peer_addr;
    ssize_t sent;
    int value = 0;

    // TODO: audit callers and confirm if this can ever happen
    if(!eee) {
        traceEvent(TRACE_WARNING, "bad eee");
        return;
    }

    if(!dest->family)
        // invalid socket
        return;

    if(eee->sock < 0)
        // invalid socket file descriptor, e.g. TCP unconnected has fd of '-1'
        return;

    peer_addr.sin_port = 0;

    // network order socket
    fill_sockaddr((struct sockaddr *) &peer_addr, sizeof(peer_addr), dest);

    // if the connection is tcp, i.e. not the regular sock...
    if(eee->conf.connect_tcp) {

        setsockopt(eee->sock, IPPROTO_TCP, TCP_NODELAY, (void *)&value, sizeof(value));
        value = 1;
#ifdef LINUX
        setsockopt(eee->sock, IPPROTO_TCP, TCP_CORK, &value, sizeof(value));
#endif

        // prepend packet length...
        uint16_t pktsize16 = htobe16(len);
        sent = sendto_fd(eee, (uint8_t*)&pktsize16, sizeof(pktsize16), &peer_addr, dest);

        if(sent <= 0)
            return;
        // ...before sending the actual data
    }
    sent = sendto_fd(eee, buf, len, &peer_addr, dest);

    // if the connection is tcp, i.e. not the regular sock...
    if(eee->conf.connect_tcp) {
        value = 1; /* value should still be set to 1 */
        setsockopt(eee->sock, IPPROTO_TCP, TCP_NODELAY, (void *)&value, sizeof(value));
#ifdef LINUX
        value = 0;
        setsockopt(eee->sock, IPPROTO_TCP, TCP_CORK, &value, sizeof(value));
#endif
    }

    return;
}

static ssize_t mikun2n_sendto_socket (SOCKET socket_fd, const void *buf,
                                      size_t len, const n2n_sock_t *dest) {
    struct sockaddr_in peer_addr;

    if(socket_fd == MIKUN2N_INVALID_SOCKET || !dest || dest->family != AF_INET)
        return -1;
    memset(&peer_addr, 0, sizeof(peer_addr));
    fill_sockaddr((struct sockaddr *)&peer_addr, sizeof(peer_addr), dest);
    return sendto(socket_fd, (const char *)buf, (int)len, 0,
                  (struct sockaddr *)&peer_addr, sizeof(peer_addr));
}


/* ************************************** */

static uint64_t mikun2n_now_ms (void) {
#ifdef _WIN32
    return (uint64_t)GetTickCount64();
#else
    struct timespec ts;

    if(clock_gettime(CLOCK_MONOTONIC, &ts) == 0)
        return (uint64_t)ts.tv_sec * 1000ULL +
               (uint64_t)ts.tv_nsec / 1000000ULL;
    return (uint64_t)time(NULL) * 1000ULL;
#endif
}

/* With a federation every entry in conf.supernodes is a relay endpoint the
 * punch machinery must leave alone; curr_sn alone no longer covers them. */
static int mikun2n_sock_is_supernode (const struct n3n_runtime_data *eee,
                                      const n2n_sock_t *sock) {
    struct peer_info *scan, *tmp;

    HASH_ITER(hh, eee->conf.supernodes, scan, tmp) {
        if(sock_equal(sock, &scan->sock))
            return 1;
    }
    return 0;
}

static int mikun2n_ip_is_supernode (const struct n3n_runtime_data *eee,
                                    const n2n_sock_t *sock) {
    struct peer_info *scan, *tmp;

    if(sock->family != AF_INET)
        return 0;
    HASH_ITER(hh, eee->conf.supernodes, scan, tmp) {
        if(scan->sock.family == AF_INET &&
           memcmp(sock->addr.v4, scan->sock.addr.v4, IPV4_SIZE) == 0)
            return 1;
    }
    return 0;
}

static int mikun2n_probe_source (const struct n3n_runtime_data *eee,
                                 const n2n_sock_t *sender) {
    if(sender->port != MIKUN2N_PROBE_PORT_A &&
       sender->port != MIKUN2N_PROBE_PORT_B)
        return 0;

    /* Every federated supernode runs the probe responder, and rtt selection
     * can re-anchor between our request and the reply. */
    return mikun2n_ip_is_supernode(eee, sender);
}

static void mikun2n_finish_nat_probe (struct n3n_runtime_data *eee) {
    mikun2n_nat_state_t *nat = &eee->mikun2n_nat;
    int endpoint_independent;

    if(nat->probe_mask != 3 || nat->complete)
        return;

    /* One A/B pair cannot type a CGNAT. A single transient re-binding used to
     * pin the whole session to NAT4 with no re-probe, which then selected the
     * NAT4 punch strategy against a peer that was really NAT3. Sample several
     * rounds and decide on the majority; only the first round can measure
     * filtering, because afterwards the mapping is primed. */
    if(nat->observed_port_a == nat->observed_port_b)
        nat->eim_matches++;
    nat->eim_samples++;

    if(nat->eim_samples < MIKUN2N_NAT_SAMPLE_ROUNDS) {
        nat->probe_mask = 0;
        nat->observed_port_a = 0;
        nat->observed_port_b = 0;
        return;
    }

    /* A split verdict means the NAT is genuinely unstable rather than plainly
     * symmetric. Report the majority for display but flag it, so the punch
     * scheduler prefers the layered sweep, which covers its range in fewer
     * ticks and does not depend on a correct NAT4 role split. */
    nat->eim_uncertain = (nat->eim_matches * 5 < nat->eim_samples * 4) &&
                         (nat->eim_matches * 5 > nat->eim_samples);
    endpoint_independent = nat->eim_matches * 2 > nat->eim_samples;

    if(!nat->multi_public_ip && endpoint_independent) {
        snprintf(nat->mapping, sizeof(nat->mapping), "endpoint-independent");
        if(nat->cross_reply) {
            snprintf(nat->type, sizeof(nat->type), "NAT1/2");
            snprintf(nat->filtering, sizeof(nat->filtering), "endpoint-or-address-independent");
        } else {
            snprintf(nat->type, sizeof(nat->type), "NAT3");
            snprintf(nat->filtering, sizeof(nat->filtering), "address-and-port-dependent");
        }
    } else {
        snprintf(nat->mapping, sizeof(nat->mapping), "address-and-port-dependent");
        snprintf(nat->type, sizeof(nat->type), "NAT4");
        snprintf(nat->filtering, sizeof(nat->filtering), "%s",
                 nat->cross_reply ? "address-dependent" : "address-and-port-dependent");
    }
    nat->complete = 1;
    nat->unavailable = 0;

    traceEvent(TRACE_NORMAL,
               "MikuN2N NAT probe: type=%s mapping=%s filtering=%s external=%s:%u/%u eim=%u/%u%s",
               nat->type, nat->mapping, nat->filtering, nat->public_ip,
               nat->observed_port_a, nat->observed_port_b,
               nat->eim_matches, nat->eim_samples,
               nat->eim_uncertain ? " (uncertain)" : "");
}

static void mikun2n_send_cross_probe (struct n3n_runtime_data *eee) {
    mikun2n_nat_state_t *nat = &eee->mikun2n_nat;
    n2n_sock_t endpoint;
    char request[128];

    if(!eee->curr_sn || !nat->public_ip[0] || !nat->public_port)
        return;

    endpoint = eee->curr_sn->sock;
    endpoint.port = MIKUN2N_PROBE_PORT_A;
    snprintf(request, sizeof(request), "XPROBE %s %u",
             nat->public_ip, nat->public_port);
    sendto_sock(eee, request, strlen(request), &endpoint);
    nat->cross_probe_attempts++;
}

static int mikun2n_handle_nat_probe (struct n3n_runtime_data *eee,
                                     const n2n_sock_t *sender,
                                     const uint8_t *udp_buf,
                                     size_t udp_size,
                                     time_t now) {
    mikun2n_nat_state_t *nat = &eee->mikun2n_nat;
    char response[256];
    char ip[64];
    char label[16];
    int port;

    if(!mikun2n_probe_source(eee, sender) || udp_size == 0 ||
       udp_size >= sizeof(response))
        return 0;

    memcpy(response, udp_buf, udp_size);
    response[udp_size] = '\0';

    if(!strcmp(response, "XREPLY")) {
        /* A reply received only after we contacted B no longer proves that B
         * could enter an unprimed mapping, so ignore such a late packet. */
        if(!(nat->probe_mask & 2))
            nat->cross_reply = 1;
        return 1;
    }

    if(sscanf(response, "PROBED %63s %d %15s", ip, &port, label) != 3 ||
       port < 1 || port > 65535)
        return 0;

    if(sender->port == MIKUN2N_PROBE_PORT_A && !nat->public_ip[0]) {
        snprintf(nat->public_ip, sizeof(nat->public_ip), "%s", ip);
        nat->public_port = (uint16_t)port;
    }

    if(sender->port == MIKUN2N_PROBE_PORT_A) {
        nat->observed_port_a = (uint16_t)port;
        nat->probe_mask |= 1;
        if(!nat->cross_probe_ms) {
            nat->cross_probe_ms = mikun2n_now_ms();
            mikun2n_send_cross_probe(eee);
        }
    } else {
        if(nat->public_ip[0] && strcmp(nat->public_ip, ip))
            nat->multi_public_ip = 1;
        nat->observed_port_b = (uint16_t)port;
        nat->probe_mask |= 2;
    }

    if(nat->probe_mask == 3)
        mikun2n_finish_nat_probe(eee);

    return 1;
}

static void mikun2n_reset_nat_probe (struct n3n_runtime_data *eee, time_t now) {
    memset(&eee->mikun2n_nat, 0, sizeof(eee->mikun2n_nat));
    snprintf(eee->mikun2n_nat.type, sizeof(eee->mikun2n_nat.type), "detecting");
    snprintf(eee->mikun2n_nat.mapping, sizeof(eee->mikun2n_nat.mapping), "unknown");
    snprintf(eee->mikun2n_nat.filtering, sizeof(eee->mikun2n_nat.filtering), "unknown");
    eee->mikun2n_nat.started_at = now;
    eee->mikun2n_nat.started_ms = mikun2n_now_ms();
    eee->mikun2n_nat.next_probe_ms = eee->mikun2n_nat.started_ms;
}

static void mikun2n_update_nat_probe (struct n3n_runtime_data *eee, time_t now,
                                      uint64_t now_ms) {
    mikun2n_nat_state_t *nat = &eee->mikun2n_nat;
    n2n_sock_t endpoint;
    static const char request[] = "PROBE";

    if(eee->conf.connect_tcp || !eee->curr_sn || !eee->last_sup)
        return;

    if(!nat->started_at)
        mikun2n_reset_nat_probe(eee, now);

    if(nat->complete)
        return;

    if(nat->unavailable) {
        if(now - nat->last_probe_at < 60)
            return;
        mikun2n_reset_nat_probe(eee, now);
    }

    if(nat->probe_mask == 3) {
        mikun2n_finish_nat_probe(eee);
        return;
    }

    if(now_ms - nat->started_ms >= MIKUN2N_NAT_PROBE_TIMEOUT_MS) {
        snprintf(nat->type, sizeof(nat->type), "unknown");
        nat->unavailable = 1;
        nat->last_probe_at = now;
        traceEvent(TRACE_INFO, "MikuN2N NAT probe unavailable; will retry later");
        return;
    }

    if(now_ms < nat->next_probe_ms)
        return;

    endpoint = eee->curr_sn->sock;
    if(!(nat->probe_mask & 1)) {
        endpoint.port = MIKUN2N_PROBE_PORT_A;
    } else {
        /* Do not contact B until its unprimed cross-port reply either arrived
         * or timed out, otherwise an APDF NAT would be misreported as open. */
        if(nat->eim_samples == 0 && !nat->cross_reply &&
           now_ms - nat->cross_probe_ms < MIKUN2N_NAT_CROSS_WAIT_MS) {
            if(nat->cross_probe_attempts < 3) {
                mikun2n_send_cross_probe(eee);
            }
            nat->next_probe_ms = now_ms + MIKUN2N_NAT_PROBE_INTERVAL_MS;
            return;
        }
        endpoint.port = MIKUN2N_PROBE_PORT_B;
    }
    sendto_sock(eee, request, sizeof(request) - 1, &endpoint);
    nat->probe_round++;
    nat->last_probe_at = now;
    nat->next_probe_ms = now_ms + MIKUN2N_NAT_PROBE_INTERVAL_MS;
}

/* ************************************** */


/* Bind eee->udp_multicast_sock to multicast group */
static void check_join_multicast_group (struct n3n_runtime_data *eee) {

#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
    if(eee->conf.allow_p2p) {
        if(!eee->multicast_joined) {
            struct ip_mreq mreq;
            mreq.imr_multiaddr.s_addr = inet_addr(N2N_MULTICAST_GROUP);
#ifdef _WIN32
            uint32_t raw_addr = *(uint32_t *)&eee->curr_sn->sock.addr.v4;
            dec_ip_str_t ip_addr;
            get_best_interface_ip(raw_addr, &ip_addr);
            mreq.imr_interface.s_addr = inet_addr(ip_addr);
#else
            mreq.imr_interface.s_addr = htonl(INADDR_ANY);
#endif

            if(setsockopt(eee->udp_multicast_sock, IPPROTO_IP, IP_ADD_MEMBERSHIP, (char *)&mreq, sizeof(mreq)) < 0) {
                traceEvent(TRACE_WARNING, "failed to bind to local multicast group %s:%u [errno %u]",
                           N2N_MULTICAST_GROUP, N2N_MULTICAST_PORT, errno);

#ifdef _WIN32
                traceEvent(TRACE_WARNING, "WSAGetLastError(): %u", WSAGetLastError());
#endif
            } else {
                traceEvent(TRACE_NORMAL, "successfully joined multicast group %s:%u",
                           N2N_MULTICAST_GROUP, N2N_MULTICAST_PORT);
                eee->multicast_joined = true;
            }
        }
    }
#endif
}

/* ************************************** */

/** Send a QUERY_PEER packet to the current supernode. */
void send_query_peer (struct n3n_runtime_data * eee,
                      const n2n_mac_t dst_mac) {

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx;
    n2n_common_t cmn = {0};
    n2n_QUERY_PEER_t query = {0};
    struct peer_info *peer, *tmp;
    int n_o_pings = 0;
    int n_o_top_sn = 0;
    int n_o_rest_sn = 0;
    int n_o_skip_sn = 0;

    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_QUERY_PEER;
    cmn.flags = 0;
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);

    memcpy(query.srcMac, eee->device.mac_addr, sizeof(n2n_mac_t));
    memcpy(query.targetMac, dst_mac, sizeof(n2n_mac_t));
    query.aflags |= N2N_AFLAGS_MIKUN2N_IDENTITY;
    snprintf(query.mikun2n_build_version, sizeof(query.mikun2n_build_version), "%s", MIKUN2N_BUILD_VERSION);
    query.mikun2n_ipv6_wire_version = MIKUN2N_IPV6_WIRE_VERSION;
    if(eee->conf.mikun2n_ipv6) {
        query.aflags |= N2N_AFLAGS_MIKUN2N_IPV6;
        query.mikun2n_ipv6_address = mikun2n_ipv6_advertised(eee, mikun2n_ipv6_now_ms());
        query.mikun2n_ipv6_token = eee->mikun2n_ipv6_token;
    }

    if(!is_null_mac(dst_mac) && eee->mikun2n_nat.complete) {
        HASH_FIND_PEER(eee->pending_peers, dst_mac, peer);
        if(!peer)
            HASH_FIND_PEER(eee->known_peers, dst_mac, peer);
        if(peer) {
            if(!eee->mikun2n_punch_nonce) {
                eee->mikun2n_punch_nonce = n3n_rand();
                if(!eee->mikun2n_punch_nonce)
                    eee->mikun2n_punch_nonce = 1;
            }
            query.aflags |= N2N_AFLAGS_MIKUN2N_NAT;
            query.mikun2n_nat_kind = mikun2n_nat_kind(&eee->mikun2n_nat);
            query.mikun2n_eim_matches = eee->mikun2n_nat.eim_matches;
            query.mikun2n_eim_samples = eee->mikun2n_nat.eim_samples;
            query.mikun2n_punch_nonce = eee->mikun2n_punch_nonce;
            if(peer->punch_bank_state >= MIKUN2N_BANK_STATE_REPORTED &&
               peer->punch_bank_model_mode != MIKUN2N_BANK_MODE_NONE &&
               peer->punch_bank_nonce != 0 &&
               peer->punch_generation != 0) {
                query.aflags |= N2N_AFLAGS_MIKUN2N_BANK_MODEL;
                query.mikun2n_bank_mode = peer->punch_bank_model_mode;
                query.mikun2n_bank_direction = peer->punch_bank_direction;
                query.mikun2n_bank_workers =
                    peer->punch_bank_worker_count;
                query.mikun2n_bank_reuse = peer->punch_bank_reuse;
                query.mikun2n_bank1 = peer->punch_bank1;
                query.mikun2n_bank2 = peer->punch_bank2;
                query.mikun2n_bank_spread = peer->punch_bank_spread;
                query.mikun2n_bank_rate = peer->punch_bank_rate;
                query.mikun2n_bank_nonce = peer->punch_bank_nonce;
                query.mikun2n_bank_generation = peer->punch_generation;
            }
        }
    }

    idx = 0;
    encode_QUERY_PEER(pktbuf, &idx, &cmn, &query);

    if(!is_null_mac(dst_mac)) {

        traceEvent(TRACE_DEBUG, "send QUERY_PEER to supernode");

        if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
            packet_header_encrypt(pktbuf, idx, idx,
                                  eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                                  time_stamp());
        }

        sendto_sock(eee, pktbuf, idx, &(eee->curr_sn->sock));

    } else {
        traceEvent(TRACE_DEBUG, "send PING to supernodes");

        if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
            packet_header_encrypt(pktbuf, idx, idx,
                                  eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                                  time_stamp());
        }

        n_o_pings = eee->conf.number_max_sn_pings;
        eee->conf.number_max_sn_pings = NUMBER_SN_PINGS_REGULAR;

        // ping the 'floor(n/2)' top supernodes and 'ceiling(n/2)' of the remaining
        n_o_top_sn  = n_o_pings >> 1;
        n_o_rest_sn = (n_o_pings + 1) >> 1;

        // skip a random number of supernodes between top and remaining
        n_o_skip_sn = HASH_COUNT(eee->conf.supernodes) - n_o_pings;
        n_o_skip_sn = (n_o_skip_sn < 0) ? 0 : n3n_rand_sqr(n_o_skip_sn);
        HASH_ITER(hh, eee->conf.supernodes, peer, tmp) {
            if(n_o_top_sn) {
                n_o_top_sn--;
                // fall through (send to top supernode)
            } else if(n_o_skip_sn) {
                n_o_skip_sn--;
                // skip (do not send)
                continue;
            } else if(n_o_rest_sn) {
                n_o_rest_sn--;
                // fall through (send to remaining supernode)
            } else {
                // done with the remaining (do not send anymore)
                break;
            }
            sendto_sock(eee, pktbuf, idx, &(peer->sock));
        }
    }
}

/* ******************************************************** */

/** Send a REGISTER_SUPER packet to the current supernode. */
void send_register_super (struct n3n_runtime_data *eee) {

    uint8_t pktbuf[N2N_PKT_BUF_SIZE] = {0};
    uint8_t hash_buf[16] = {0};
    size_t idx;
    /* ssize_t sent; */
    n2n_common_t cmn;
    n2n_REGISTER_SUPER_t reg;
    n2n_sock_str_t sockbuf;

    // FIXME: fix encode_* functions to not need memsets
    memset(&cmn, 0, sizeof(cmn));
    memset(&reg, 0, sizeof(reg));

    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_REGISTER_SUPER;
    if(eee->conf.preferred_sock.family == (uint8_t)AF_INVALID) {
        cmn.flags = 0;
    } else {
        cmn.flags = N2N_FLAGS_SOCKET;
        memcpy(&(reg.sock), &(eee->conf.preferred_sock), sizeof(n2n_sock_t));
    }
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);

    eee->curr_sn->last_cookie = n3n_rand();

    reg.cookie = eee->curr_sn->last_cookie;
    reg.dev_addr.net_addr = ntohl(eee->device.ip_addr);
    reg.dev_addr.net_bitlen = eee->conf.tuntap_v4.net_bitlen;
    memcpy(reg.dev_desc, eee->conf.dev_desc, N2N_DESC_SIZE);
    get_local_auth(eee, &(reg.auth));

    memcpy(reg.edgeMac, eee->device.mac_addr, sizeof(n2n_mac_t));

    idx = 0;
    encode_REGISTER_SUPER(pktbuf, &idx, &cmn, &reg);

    traceEvent(TRACE_DEBUG, "send REGISTER_SUPER to [%s]",
               sock_to_cstr(sockbuf, &(eee->curr_sn->sock)));

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
        packet_header_encrypt(pktbuf, idx, idx,
                              eee->conf.header_encryption_ctx_static, eee->conf.header_iv_ctx_static,
                              time_stamp());

        if(eee->conf.shared_secret) {
            pearson_hash_128(hash_buf, pktbuf, idx);
            speck_128_encrypt(hash_buf, (speck_context_t*)eee->conf.shared_secret_ctx);
            encode_buf(pktbuf, &idx, hash_buf, N2N_REG_SUP_HASH_CHECK_LEN);
        }
    }

    sendto_sock(eee, pktbuf, idx, &(eee->curr_sn->sock));
}


static void send_unregister_super (struct n3n_runtime_data *eee) {

    uint8_t pktbuf[N2N_PKT_BUF_SIZE] = {0};
    size_t idx;
    /* ssize_t sent; */
    n2n_common_t cmn;
    n2n_UNREGISTER_SUPER_t unreg;
    n2n_sock_str_t sockbuf;

    // FIXME: fix encode_* functions to not need memsets
    memset(&cmn, 0, sizeof(cmn));
    memset(&unreg, 0, sizeof(unreg));

    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_UNREGISTER_SUPER;
    cmn.flags = 0;
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);
    get_local_auth(eee, &(unreg.auth));

    memcpy(unreg.srcMac, eee->device.mac_addr, sizeof(n2n_mac_t));

    idx = 0;
    encode_UNREGISTER_SUPER(pktbuf, &idx, &cmn, &unreg);

    traceEvent(TRACE_DEBUG, "send UNREGISTER_SUPER to [%s]",
               sock_to_cstr(sockbuf, &(eee->curr_sn->sock)));

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED)
        packet_header_encrypt(pktbuf, idx, idx,
                              eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                              time_stamp());

    sendto_sock(eee, pktbuf, idx, &(eee->curr_sn->sock));

}


static int sort_supernodes (struct n3n_runtime_data *eee, time_t now) {

    struct peer_info *scan, *tmp;

    if(now - eee->last_sweep > SWEEP_TIME) {
        // this routine gets periodically called

        if(!eee->sn_wait) {
            // sort supernodes in ascending order of their selection_criterion fields
            sn_selection_sort(&(eee->conf.supernodes));
        }

        if(eee->curr_sn != eee->conf.supernodes) {
            // we have not been connected to the best/top one
            send_unregister_super(eee);
            eee->curr_sn = eee->conf.supernodes;
            reset_sup_attempts(eee);
            supernode_connect(eee);

            traceEvent(
                TRACE_INFO,
                "registering with supernode [%s][number of supernodes %d][attempts left %u]",
                peer_info_get_hostname(eee->curr_sn),
                HASH_COUNT(eee->conf.supernodes),
                (unsigned int)eee->sup_attempts
            );

            send_register_super(eee);
            eee->last_register_req = now;
            eee->sn_wait = 1;
        }

        HASH_ITER(hh, eee->conf.supernodes, scan, tmp) {
            if(scan == eee->curr_sn)
                sn_selection_criterion_good(&(scan->selection_criterion));
            else
                sn_selection_criterion_default(&(scan->selection_criterion));
        }
        sn_selection_criterion_common_data_default(eee);

        // send PING to all the supernodes
        if(!eee->conf.connect_tcp)
            send_query_peer(eee, null_mac);
        eee->last_sweep = now;

        // no answer yet (so far, unused in regular edge code; mainly used during bootstrap loading)
        eee->sn_pong = 0;
    }

    return 0; /* OK */
}

/** Send a REGISTER packet to another edge. */
static void send_register (struct n3n_runtime_data * eee,
                           const n2n_sock_t * remote_peer,
                           const n2n_mac_t peer_mac,
                           const n2n_cookie_t cookie) {

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx;
    /* ssize_t sent; */
    n2n_common_t cmn;
    n2n_REGISTER_t reg;
    n2n_sock_str_t sockbuf;

    if(!eee->conf.allow_p2p) {
        traceEvent(TRACE_DEBUG, "skipping register as P2P is disabled");
        return;
    }

    // FIXME: fix encode_* functions to not need memsets
    memset(&cmn, 0, sizeof(cmn));
    memset(&reg, 0, sizeof(reg));
    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_REGISTER;
    cmn.flags = 0;
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);

    reg.cookie = cookie;
    memcpy(reg.srcMac, eee->device.mac_addr, sizeof(n2n_mac_t));

    if(peer_mac) {
        // can be NULL for multicast registrations
        memcpy(reg.dstMac, peer_mac, sizeof(n2n_mac_t));
    }
    reg.dev_addr.net_addr = ntohl(eee->device.ip_addr);
    reg.dev_addr.net_bitlen = eee->conf.tuntap_v4.net_bitlen;
    memcpy(reg.dev_desc, eee->conf.dev_desc, N2N_DESC_SIZE);

    idx = 0;
    encode_REGISTER(pktbuf, &idx, &cmn, &reg);

    traceEvent((cookie & N2N_PORT_REG_COOKIE) ? TRACE_DEBUG : TRACE_INFO,
               "send REGISTER to [%s]",
               sock_to_cstr(sockbuf, remote_peer));

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED)
        packet_header_encrypt(pktbuf, idx, idx,
                              eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                              time_stamp());

    sendto_sock(eee, pktbuf, idx, remote_peer);
}

/* ************************************** */

/** Send a REGISTER_ACK packet to a peer edge. */
static void send_register_ack (struct n3n_runtime_data * eee,
                               const n2n_sock_t * remote_peer,
                               const n2n_REGISTER_t * reg,
                               SOCKET out_sock) {

    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx;
    /* ssize_t sent; */
    n2n_common_t cmn;
    n2n_REGISTER_ACK_t ack;
    n2n_sock_str_t sockbuf;

    if(!eee->conf.allow_p2p) {
        traceEvent(TRACE_DEBUG, "skipping register ACK as P2P is disabled");
        return;
    }

    // FIXME: fix encode_* functions to not need memsets
    memset(&cmn, 0, sizeof(cmn));
    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_REGISTER_ACK;
    cmn.flags = 0;
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);

    // FIXME: fix encode_* functions to not need memsets
    memset(&ack, 0, sizeof(ack));
    ack.cookie = reg->cookie;
    memcpy(ack.srcMac, eee->device.mac_addr, N2N_MAC_SIZE);
    memcpy(ack.dstMac, reg->srcMac, N2N_MAC_SIZE);

    idx = 0;
    encode_REGISTER_ACK(pktbuf, &idx, &cmn, &ack);

    traceEvent(TRACE_INFO, "send REGISTER_ACK to [%s]",
               sock_to_cstr(sockbuf, remote_peer));

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED)
        packet_header_encrypt(pktbuf, idx, idx,
                              eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                              time_stamp());

    if(out_sock != MIKUN2N_INVALID_SOCKET && out_sock != eee->sock)
        mikun2n_sendto_socket(out_sock, pktbuf, idx, remote_peer);
    else
        sendto_sock(eee, pktbuf, idx, remote_peer);
}

/* ************************************** */

static char gratuitous_arp[] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, /* dest MAC */
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, /* src MAC */
    0x08, 0x06, /* ARP */
    0x00, 0x01, /* ethernet */
    0x08, 0x00, /* IP */
    0x06, /* hw Size */
    0x04, /* protocol Size */
    0x00, 0x02, /* ARP reply */
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, /* src MAC */
    0x00, 0x00, 0x00, 0x00, /* src IP */
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, /* target MAC */
    0x00, 0x00, 0x00, 0x00 /* target IP */
};

// build a gratuitous ARP packet */
static int build_gratuitous_arp (struct n3n_runtime_data * eee, char *buffer, uint16_t buffer_len) {

    if(buffer_len < sizeof(gratuitous_arp)) return(-1);

    memcpy(buffer, gratuitous_arp, sizeof(gratuitous_arp));
    memcpy(&buffer[6], eee->device.mac_addr, 6);
    memcpy(&buffer[22], eee->device.mac_addr, 6);
    memcpy(&buffer[28], &(eee->device.ip_addr), 4);
    memcpy(&buffer[38], &(eee->device.ip_addr), 4);

    return(sizeof(gratuitous_arp));
}

/** Called from update_supernode_reg to periodically send gratuitous ARP
 *    broadcasts. */
static void send_grat_arps (struct n3n_runtime_data * eee) {

    uint8_t buffer[48];
    size_t len;

    traceEvent(TRACE_DEBUG, "sending gratuitous ARP...");
    len = build_gratuitous_arp(eee, (char*)buffer, sizeof(buffer));

    edge_send_packet2net(eee, buffer, len);
    edge_send_packet2net(eee, buffer, len); /* Two is better than one :-) */
}

/* ************************************** */

/** @brief Check to see if we should re-register with the supernode.
 *
 *    This is frequently called by the main loop.
 */
void update_supernode_reg (struct n3n_runtime_data * eee, time_t now) {

    struct peer_info *peer, *tmp_peer;
    int cnt = 0;
    int off = 0;

    if((eee->sn_wait && (now > (eee->last_register_req + (eee->conf.register_interval / 10))))
       ||(eee->sn_wait == 2)) { /* immediately re-register in case of RE_REGISTER_SUPER */
        /* fall through */
        traceEvent(TRACE_DEBUG, "update_supernode_reg: doing fast retry.");
    } else if(now < (eee->last_register_req + eee->conf.register_interval))
        return; /* Too early */

    // determine time offset to apply on last_register_req for
    // all edges's next re-registration does not happen all at once
    if(eee->sn_wait == 2) {
        // remaining 1/4 is greater than 1/10 fast retry allowance;
        // '%' might be expensive but does not happen all too often
        off = n3n_rand() % ((eee->conf.register_interval * 3) / 4);
    }

    check_join_multicast_group(eee);

    if(0 == eee->sup_attempts) {
        /* Give up on that supernode and try the next one. */
        sn_selection_criterion_bad(&(eee->curr_sn->selection_criterion));
        sn_selection_sort(&(eee->conf.supernodes));
        eee->curr_sn = eee->conf.supernodes;
        traceEvent(
            TRACE_WARNING,
            "supernode not responding, now trying [%s]",
            peer_info_get_hostname(eee->curr_sn)
        );
        reset_sup_attempts(eee);
        // trigger out-of-schedule DNS resolution
        eee->resolution_request = true;

        // in some multi-NATed scenarios communication gets stuck on losing connection to supernode
        // closing and re-opening the socket allows for re-establishing communication
        // this can only be done, if working on some unprivileged port and/or having sufficent
        // privileges. as we are not able to check for sufficent privileges here, we only do it
        // If bind_address is null then we definitely dont have a local port
        // set.  Since we have converted to using a sockaddr struct for the
        // bind details, it is not simple to check the local port.
        //
        // TODO:
        // - probably should re-add checking for unprivileged port
        // - count this condition in a metric so we can see how often it is
        //   triggered
        //

        if(eee->conf.bind_address) {
            // do not explicitly disconnect every time as the condition described is rare, so ...
            // ... check that there are no external peers (indicating a working socket) ...
            HASH_ITER(hh, eee->known_peers, peer, tmp_peer)
            if(!peer->local) {
                cnt++;
                break;
            }
            if(!cnt) {
                // ... and then count the connection retries
                (eee->close_socket_counter)++;
                if(eee->close_socket_counter >= N2N_CLOSE_SOCKET_COUNTER_MAX) {
                    eee->close_socket_counter = 0;
                    supernode_disconnect(eee);
                }
            }

            traceEvent(TRACE_DEBUG, "reconnected to supernode");
        }
        supernode_connect(eee);

    } else {
        --(eee->sup_attempts);
    }

    if(maybe_supernode2sock(&(eee->curr_sn->sock), peer_info_get_hostname(eee->curr_sn)) == 0) {
        traceEvent(
            TRACE_INFO,
            "registering with supernode [%s][number of supernodes %d][attempts left %u]",
            peer_info_get_hostname(eee->curr_sn),
            HASH_COUNT(eee->conf.supernodes),
            (unsigned int)eee->sup_attempts
        );

        send_register_super(eee);
    }

    register_with_local_peers(eee);

    // if supernode repeatedly not responding (already waiting), safeguard the
    // current known connections to peers by re-registering
    if(eee->sn_wait == 1)
        HASH_ITER(hh, eee->known_peers, peer, tmp_peer)
        if((now - peer->last_seen) > REGISTER_SUPER_INTERVAL_DFL)
            send_register(eee, &(peer->sock), peer->mac_addr, peer->last_cookie);

    eee->sn_wait = 1;

    eee->last_register_req = now - off;
}

/* ************************************** */

/** A PACKET has arrived containing an encapsulated ethernet datagram - usually
 *    encrypted. */
static int handle_PACKET (struct n3n_runtime_data * eee,
                          const uint8_t from_supernode,
                          const n2n_PACKET_t * pkt,
                          const n2n_sock_t * orig_sender,
                          uint8_t * payload,
                          size_t psize) {

    ssize_t data_sent_len;
    uint8_t *                 eth_payload = NULL;
    time_t now;
    ether_hdr_t *             eh;
    ipstr_t ip_buf;
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;

    now = time(NULL);

    traceEvent(TRACE_DEBUG, "handle_PACKET size %u transform %u",
               (unsigned int)psize, (unsigned int)pkt->transform);

    if(from_supernode) {
        if(is_multi_broadcast(pkt->dstMac))
            ++(eee->stats.rx_sup_broadcast);

        ++(eee->stats.rx_sup);
        eee->last_sup = now;
    } else {
        ++(eee->stats.rx_p2p);
        eee->last_p2p=now;
    }

    /* Handle transform. */
    uint8_t decode_buf[N2N_PKT_BUF_SIZE];
    uint8_t deflate_buf[N2N_PKT_BUF_SIZE];
    size_t eth_size;

    n2n_transform_t rx_transop_id = (n2n_transform_t)pkt->transform;
    uint8_t rx_compression_id = pkt->compression;

    if(rx_transop_id != eee->conf.transop_id) {
        traceEvent(
            TRACE_WARNING,
            "invalid transop ID: expected %s (%u), got %s (%u) from %s [%s]",
            n3n_transform_id2str(eee->conf.transop_id),
            eee->conf.transop_id,
            n3n_transform_id2str(rx_transop_id),
            rx_transop_id,
            macaddr_str(mac_buf, pkt->srcMac),
            sock_to_cstr(sockbuf, orig_sender)
        );
        return -1;
    }

    uint8_t is_multicast;
    // decrypt
    eth_payload = decode_buf;
    eth_size = eee->transop.rev(&eee->transop,
                                eth_payload, N2N_PKT_BUF_SIZE,
                                payload, psize, pkt->srcMac);
    ++(eee->transop.rx_cnt); /* stats */

    /* decompress if necessary */
    size_t deflate_len;

    switch(rx_compression_id) {
        case N2N_COMPRESSION_ID_NONE:
            break; // continue afterwards

        case N2N_COMPRESSION_ID_LZO:
            deflate_len = eee->transop_lzo.rev(&eee->transop_lzo,
                                               deflate_buf, N2N_PKT_BUF_SIZE,
                                               decode_buf, eth_size, pkt->srcMac);
            break;

#ifdef HAVE_LIBZSTD
        case N2N_COMPRESSION_ID_ZSTD:
            deflate_len = eee->transop_zstd.rev(&eee->transop_zstd,
                                                deflate_buf, N2N_PKT_BUF_SIZE,
                                                decode_buf, eth_size, pkt->srcMac);
            break;
#endif
        default:
            traceEvent(
                TRACE_WARNING,
                "decompression: failed: unsupported %i",
                rx_compression_id
            );
            return(-1); // cannot handle it
    }

    if(rx_compression_id != N2N_COMPRESSION_ID_NONE) {
        traceEvent(
            TRACE_DEBUG,
            "decompression: %i: deflated %u bytes to %u bytes",
            rx_compression_id,
            eth_size,
            deflate_len
        );
        eth_payload = deflate_buf;
        eth_size = deflate_len;
    }

    eh = (ether_hdr_t*)eth_payload;

    is_multicast = (is_ip6_discovery(eth_payload, eth_size) || is_ethMulticast(eth_payload, eth_size));

    if(!eee->conf.allow_multicast && is_multicast) {
        traceEvent(TRACE_INFO, "dropping RX multicast");
        eee->stats.rx_multicast_drop++;
        return(-1);
    }

    if((!eee->conf.allow_routing) && (!is_multicast)) {
        /* Check if it is a routed packet */

        if((ntohs(eh->type) == 0x0800) && (eth_size >= ETH_FRAMESIZE + IP4_MIN_SIZE)) {

            uint32_t *dst = (uint32_t*)&eth_payload[ETH_FRAMESIZE + IP4_DSTOFFSET];
            uint8_t *dst_mac = (uint8_t*)eth_payload;

            /* Note: all elements of the_ip are in network order */
            if(!memcmp(dst_mac, broadcast_mac, N2N_MAC_SIZE))
                traceEvent(TRACE_DEBUG, "RX broadcast packet destined to [%s]",
                           intoa(ntohl(*dst), ip_buf, sizeof(ip_buf)));
            else if((*dst != eee->device.ip_addr)) {
                /* This is a packet that needs to be routed */
                traceEvent(TRACE_INFO, "discarding routed packet destined to [%s]",
                           intoa(ntohl(*dst), ip_buf, sizeof(ip_buf)));
                return(-1);
            }

            /* This packet is directed to us */
            /* traceEvent(TRACE_INFO, "Sending non-routed packet"); */
        }
    }

#ifdef HAVE_BRIDGING_SUPPORT
    if((eee->conf.allow_routing) && (!is_multi_broadcast(eh->shost))) {
        struct host_info *host = NULL;

        HASH_FIND(hh, eee->known_hosts, eh->shost, sizeof(n2n_mac_t), host);
        if(host == NULL) {
            host = calloc(1, sizeof(struct host_info));
            // TODO: alloc() on the packet path can cause bad latency

            memcpy(host->mac_addr, eh->shost, sizeof(n2n_mac_t));
            HASH_ADD(hh, eee->known_hosts, mac_addr, sizeof(n2n_mac_t), host);
        }
        memcpy(host->edge_addr, pkt->srcMac, sizeof(n2n_mac_t));
        host->last_seen = now;
    }
#endif

    if(eee->network_traffic_filter->filter_packet_from_peer(eee->network_traffic_filter, eee, orig_sender,
                                                            eth_payload, eth_size) == N2N_DROP) {
        traceEvent(TRACE_DEBUG, "filtered packet of size %u", (unsigned int)eth_size);
        return(0);
    }

    if(eee->cb.packet_from_peer) {
        uint16_t tmp_eth_size = eth_size;
        if(eee->cb.packet_from_peer(eee, orig_sender, eth_payload, &tmp_eth_size) == N2N_DROP) {
            traceEvent(TRACE_DEBUG, "DROP packet of size %u", (unsigned int)eth_size);
            return(0);
        }
        eth_size = tmp_eth_size;
    }

    /* Write ethernet packet to tap device. */
    traceEvent(TRACE_DEBUG, "sending data of size %u to TAP", (unsigned int)eth_size);
    data_sent_len = tuntap_write(&(eee->device), eth_payload, eth_size);

    if(data_sent_len == eth_size) {
        return 0;
    }

    return -1;
}

/* ************************************** */


#if 0
#ifndef _WIN32

static char *get_ip_from_arp (dec_ip_str_t buf, const n2n_mac_t req_mac) {

    FILE *fd;
    dec_ip_str_t ip_str = {'\0'};
    devstr_t dev_str = {'\0'};
    macstr_t mac_str = {'\0'};
    n2n_mac_t mac = {'\0'};

    strncpy(buf, "0.0.0.0", N2N_NETMASK_STR_SIZE - 1);

    if(is_null_mac(req_mac)) {
        traceEvent(TRACE_DEBUG, "MAC address is null.");
        return buf;
    }

    if(!(fd = fopen("/proc/net/arp", "r"))) {
        traceEvent(TRACE_WARNING, "could not open arp table: %d - %s", errno, strerror(errno));
        return buf;
    }

    while(!feof(fd) && fgetc(fd) != '\n');
    while(!feof(fd) && (fscanf(fd, " %15[0-9.] %*s %*s %17[A-Fa-f0-9:] %*s %15s", ip_str, mac_str, dev_str) == 3)) {
        str2mac(mac, mac_str);
        if(0 == memcmp(mac, req_mac, sizeof(n2n_mac_t))) {
            strncpy(buf, ip_str, N2N_NETMASK_STR_SIZE - 1);
            break;
        }
    }
    fclose(fd);

    return buf;
}

#endif
#endif

/* ************************************** */

static int check_query_peer_info (struct n3n_runtime_data *eee, time_t now, n2n_mac_t mac) {

    struct peer_info *scan;

    HASH_FIND_PEER(eee->pending_peers, mac, scan);

    if(!scan) {
        scan = peer_info_malloc(mac);

        scan->timeout = eee->conf.register_interval; /* TODO: should correspond to the peer supernode registration timeout */
        scan->last_seen = now; /* Don't change this it marks the pending peer for removal. */

        HASH_ADD_PEER(eee->pending_peers, scan);
    }

    if(now - scan->last_sent_query > eee->conf.register_interval) {
        send_register(eee, &(eee->curr_sn->sock), mac, N2N_FORWARDED_REG_COOKIE);
        send_query_peer(eee, scan->mac_addr);
        scan->last_sent_query = now;
        return(0);
    }

    return(1);
}

/* ************************************** */

/* @return 1 if destination is a peer, 0 if destination is supernode */
static int find_peer_destination (struct n3n_runtime_data * eee,
                                  n2n_mac_t mac_address,
                                  n2n_sock_t * destination) {

    struct peer_info *scan;
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;
    int retval = 0;
    time_t now = time(NULL);

    if(is_multi_broadcast(mac_address)) {
        traceEvent(TRACE_DEBUG, "multicast or broadcast destination peer, using supernode");
        memcpy(destination, &(eee->curr_sn->sock), sizeof(struct sockaddr_in));
        return(0);
    }

    traceEvent(TRACE_DEBUG, "searching destination socket for %s",
               macaddr_str(mac_buf, mac_address));

    HASH_FIND_PEER(eee->known_peers, mac_address, scan);

    if(scan && (scan->last_seen > 0)) {
        if(mikun2n_peer_force_relay(eee, scan)) {
            memcpy(destination, &(eee->curr_sn->sock), sizeof(struct sockaddr_in));
            traceEvent(TRACE_DEBUG, "peer %s is forced to use supernode",
                       macaddr_str(mac_buf, mac_address));
            return 0;
        }
        if((now - scan->last_p2p) >= (scan->timeout / 2) &&
           mikun2n_path_recoverable(eee, scan, now)) {
            /* Relay meanwhile, and do not open a pending entry for the same MAC
             * while the known one is still being revalidated. */
            if(!scan->punch_recover_since) {
                n2n_sock_str_t sockbuf2;
                scan->punch_recover_since = now;
                traceEvent(TRACE_NORMAL,
                           "MikuN2N IPv4 path idle %us peer=%s; relaying and probing %s for up to %us",
                           (unsigned int)(now - scan->last_p2p),
                           macaddr_str(mac_buf, mac_address),
                           sock_to_cstr(sockbuf2, &scan->sock),
                           MIKUN2N_RECOVER_SECS);
            }
            memcpy(destination, &(eee->curr_sn->sock), sizeof(struct sockaddr_in));
            return 0;
        }
        if((now - scan->last_p2p) >= (scan->timeout / 2)) {
            /* Too much time passed since we saw the peer, need to register again
             * since the peer address may have changed. */
            traceEvent(TRACE_DEBUG, "refreshing idle known peer");
            if(scan->punch_recover_since)
                traceEvent(TRACE_NORMAL,
                           "MikuN2N IPv4 path not recovered peer=%s after %us; re-punching",
                           macaddr_str(mac_buf, mac_address),
                           (unsigned int)(now - scan->punch_recover_since));
            HASH_DEL(eee->known_peers, scan);
            mgmt_event_post(N3N_EVENT_PEER,N3N_EVENT_PEER_P2P_EXPIRED,scan);
            peer_info_free(scan);
            /* NOTE: registration will be performed upon the receival of the next response packet */
        } else {
            /* Valid known peer found */
            memcpy(destination, &scan->sock, sizeof(n2n_sock_t));
            retval = 1;
        }
    }

    if(retval == 0) {
        memcpy(destination, &(eee->curr_sn->sock), sizeof(struct sockaddr_in));
        traceEvent(TRACE_DEBUG, "p2p peer %s not found, using supernode",
                   macaddr_str(mac_buf, mac_address));

        check_query_peer_info(eee, now, mac_address);
    }

    traceEvent(TRACE_DEBUG, "found peer's socket %s [%s]",
               macaddr_str(mac_buf, mac_address),
               sock_to_cstr(sockbuf, destination));

    return retval;
}

/* ***************************************************** */

/** Send an ecapsulated ethernet PACKET to a destination edge or broadcast MAC
 *    address. */
static int send_packet (struct n3n_runtime_data * eee,
                        n2n_mac_t dstMac,
                        const uint8_t * pktbuf,
                        size_t pktlen) {

    int is_p2p;
    /*ssize_t s; */
    n2n_sock_str_t sockbuf;
    n2n_sock_t destination;
    macstr_t mac_buf;
    struct peer_info *peer, *tmp_peer;

    if(!is_multi_broadcast(dstMac) && eee->conf.mikun2n_ipv6) {
        HASH_FIND_PEER(eee->known_peers, dstMac, peer);
        if(!peer)
            HASH_FIND_PEER(eee->pending_peers, dstMac, peer);
        if(peer && !peer->local && !mikun2n_peer_force_relay(eee, peer) &&
           mikun2n_ipv6_send(eee, peer, pktbuf, pktlen, mikun2n_now_ms())) {
            ++eee->stats.tx_p2p;
            return 0;
        }
    }
    is_p2p = find_peer_destination(eee, dstMac, &destination);

    traceEvent(TRACE_INFO, "Tx PACKET of %u bytes to %s [%s]",
               pktlen, macaddr_str(mac_buf, dstMac),
               sock_to_cstr(sockbuf, &destination));

    if(is_p2p)
        ++(eee->stats.tx_p2p);
    else
        ++(eee->stats.tx_sup);

    if(is_multi_broadcast(dstMac)) {
        ++(eee->stats.tx_sup_broadcast);

        // if no supernode around, foward the broadcast to all known peers
        if(eee->sn_wait) {
            HASH_ITER(hh, eee->known_peers, peer, tmp_peer) {
                if(peer->punch_data_sock != MIKUN2N_INVALID_SOCKET)
                    mikun2n_sendto_socket(peer->punch_data_sock,
                                          pktbuf, pktlen, &peer->sock);
                else
                    sendto_sock(eee, pktbuf, pktlen, &peer->sock);
            }
            return 0;
        }
        // fall through otherwise
    }

    if(is_p2p) {
        HASH_FIND_PEER(eee->known_peers, dstMac, peer);
        if(peer && peer->punch_data_sock != MIKUN2N_INVALID_SOCKET) {
            mikun2n_sendto_socket(peer->punch_data_sock,
                                  pktbuf, pktlen, &destination);
            return 0;
        }
    }
    sendto_sock(eee, pktbuf, pktlen, &destination);

    return 0;
}

/* ************************************** */

/** A layer-2 packet was received at the tunnel and needs to be sent via UDP. */
void edge_send_packet2net (struct n3n_runtime_data * eee,
                           uint8_t *tap_pkt, size_t len) {

    ipstr_t ip_buf;
    n2n_mac_t destMac;
    n2n_common_t cmn;
    n2n_PACKET_t pkt;
    uint8_t *enc_src = tap_pkt;
    size_t enc_len = len;
    uint8_t compression_buf[N2N_PKT_BUF_SIZE];
    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx = 0;
    n2n_transform_t tx_transop_idx = eee->transop.transform_id;
    ether_hdr_t eh;

    /* tap_pkt is not aligned so we have to copy to aligned memory */
    memcpy(&eh, tap_pkt, sizeof(ether_hdr_t));

    /* Discard IP packets that are not originated by this hosts */
    if(!(eee->conf.allow_routing)) {
        if(ntohs(eh.type) == 0x0800) {
            /* This is an IP packet from the local source address - not forwarded. */
            uint32_t *src = (uint32_t*)&tap_pkt[ETH_FRAMESIZE + IP4_SRCOFFSET];

            /* Note: all elements of the_ip are in network order */
            if(*src != eee->device.ip_addr) {
                /* This is a packet that needs to be routed */
                traceEvent(TRACE_INFO, "discarding routed packet destined to [%s]",
                           intoa(ntohl(*src), ip_buf, sizeof(ip_buf)));
                return;
            } else {
                /* This packet is originated by us */
                /* traceEvent(TRACE_INFO, "Sending non-routed packet"); */
            }
        }
    }

    /* Optionally compress then apply transforms, eg encryption. */

    /* Once processed, send to destination in PACKET */

    memcpy(destMac, tap_pkt, N2N_MAC_SIZE); /* dest MAC is first in ethernet header */
#ifdef HAVE_BRIDGING_SUPPORT
    /* find the destMac behind which edge, and change dest to this edge */
    if((eee->conf.allow_routing) && (!is_multi_broadcast(destMac))) {
        struct host_info *host = NULL;
        HASH_FIND(hh, eee->known_hosts, destMac, sizeof(n2n_mac_t), host);
        if(host) {
            memcpy(destMac, host->edge_addr, N2N_MAC_SIZE);
        }
    }
#endif

    // FIXME: fix encode_* functions to not need memsets
    memset(&cmn, 0, sizeof(cmn));
    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_PACKET;
    cmn.flags = 0; /* no options, not from supernode, no socket */
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);

    // FIXME: fix encode_* functions to not need memsets
    memset(&pkt, 0, sizeof(pkt));
    memcpy(pkt.srcMac, eee->device.mac_addr, N2N_MAC_SIZE);
    memcpy(pkt.dstMac, destMac, N2N_MAC_SIZE);

    pkt.transform = tx_transop_idx;

    // compression needs to be tried before encode_PACKET is called for compression indication gets encoded there
    pkt.compression = N2N_COMPRESSION_ID_NONE;

    if(eee->conf.compression) {
        int32_t compression_len;

        switch(eee->conf.compression) {
            case N2N_COMPRESSION_ID_LZO:
                compression_len = eee->transop_lzo.fwd(&eee->transop_lzo,
                                                       compression_buf, sizeof(compression_buf),
                                                       tap_pkt, len,
                                                       pkt.dstMac);

                if((compression_len > 0) && (compression_len < len)) {
                    pkt.compression = N2N_COMPRESSION_ID_LZO;
                }
                break;

#ifdef HAVE_LIBZSTD
            case N2N_COMPRESSION_ID_ZSTD:
                compression_len = eee->transop_zstd.fwd(&eee->transop_zstd,
                                                        compression_buf, sizeof(compression_buf),
                                                        tap_pkt, len,
                                                        pkt.dstMac);

                if((compression_len > 0) && (compression_len < len)) {
                    pkt.compression = N2N_COMPRESSION_ID_ZSTD;
                }
                break;
#endif

            default:
                break;
        }

        if(pkt.compression != N2N_COMPRESSION_ID_NONE) {
            traceEvent(TRACE_DEBUG, "payload compression [%s]: compressed %u bytes to %u bytes\n",
                       n3n_compression_id2str(pkt.compression),
                       len, compression_len);
            enc_src = compression_buf;
            enc_len = compression_len;
        }
    }

    idx = 0;
    encode_PACKET(pktbuf, &idx, &cmn, &pkt);

    uint16_t headerIdx = idx;

    idx += eee->transop.fwd(&eee->transop,
                            pktbuf + idx, N2N_PKT_BUF_SIZE - idx,
                            enc_src, enc_len, pkt.dstMac);

    traceEvent(TRACE_DEBUG, "encode PACKET of %u bytes, %u bytes data, %u bytes overhead, transform %u",
               (u_int)idx, (u_int)len, (u_int)(idx - len), tx_transop_idx);

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED)
        // in case of user-password auth, also encrypt the iv of payload assuming ChaCha20 and SPECK having the same iv size
        packet_header_encrypt(pktbuf, headerIdx + (NULL != eee->conf.shared_secret) * min(idx - headerIdx, N2N_SPECK_IVEC_SIZE), idx,
                              eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                              time_stamp());

#ifdef MTU_ASSERT_VALUE
    {
        const u_int eth_udp_overhead = ETH_FRAMESIZE + IP4_MIN_SIZE + UDP_SIZE;

        // MTU assertion which avoids fragmentation by N2N
        assert(idx + eth_udp_overhead <= MTU_ASSERT_VALUE);
    }
#endif

    eee->transop.tx_cnt++; /* stats */

    send_packet(eee, destMac, pktbuf, idx); /* to peer or supernode */
}

/* ************************************** */

/** Read a single packet from the TAP interface, process it and write out the
 *    corresponding packet to the cooked socket.
 */
void edge_read_from_tap (struct n3n_runtime_data * eee) {

    /* tun -> remote */
    uint8_t eth_pkt[N2N_PKT_BUF_SIZE];
    macstr_t mac_buf;
    ssize_t len;

    len = tuntap_read( &(eee->device), eth_pkt, N2N_PKT_BUF_SIZE );
    if((len <= 0) || (len > N2N_PKT_BUF_SIZE)) {
        traceEvent(
            TRACE_WARNING,
            "read()=%d [%d/%s]",
            len,
            errno,
            strerror(errno)
        );
        traceEvent(TRACE_WARNING, "TAP I/O operation aborted, restart later.");
        eee->stats.tx_tuntap_error++;

        sleep(3);
        tuntap_close(&(eee->device));
        tuntap_open(&(eee->device),
                    eee->conf.tuntap_dev_name,
                    eee->conf.tuntap_ip_mode,
                    eee->conf.tuntap_v4,
                    eee->conf.device_mac,
                    eee->conf.mtu,
                    eee->conf.metric
        );
        return;

    }

    const uint8_t * mac = eth_pkt;
    traceEvent(TRACE_DEBUG, "Rx TAP packet (%4d) for %s",
               (signed int)len, macaddr_str(mac_buf, mac));

    if(!eee->conf.allow_multicast &&
       (is_ip6_discovery(eth_pkt, len) ||
        is_ethMulticast(eth_pkt, len))) {
        traceEvent(TRACE_INFO, "dropping Tx multicast");
        eee->stats.tx_multicast_drop++;
        return;
    }

    /* Group addresses outside IPv4/IPv6 multicast (LLDP, STP and the rest of the
     * IEEE 802.1 reserved range) are never forwarded by a bridge, and the
     * supernode drops them as unknown unicast. Left alone, each one became a
     * pending "peer" that can never register and was queried every 3 s for the
     * whole session. */
    if((mac[0] & 0x01) && !is_multi_broadcast(mac)) {
        eee->stats.tx_multicast_drop++;
        return;
    }

    if(!eee->last_sup) {
        // drop packets before first registration with supernode
        traceEvent(TRACE_DEBUG, "DROP packet before first registration with supernode");
        return;
    }

    if(eee->network_traffic_filter) {
        if(eee->network_traffic_filter->filter_packet_from_tap(eee->network_traffic_filter, eee, eth_pkt,
                                                               len) == N2N_DROP) {
            traceEvent(TRACE_DEBUG, "filtered packet of size %u", (unsigned int)len);
            return;
        }
    }

    if(eee->cb.packet_from_tap) {
        uint16_t tmp_len = len;
        if(eee->cb.packet_from_tap(eee, eth_pkt, &tmp_len) == N2N_DROP) {
            traceEvent(TRACE_DEBUG, "DROP packet of size %u", (unsigned int)len);
            return;
        }
        len = tmp_len;
    }

    edge_send_packet2net(eee, eth_pkt, len);
}


/* ************************************** */


/** handle a datagram from the main UDP socket to the internet. */
void process_udp (struct n3n_runtime_data *eee, const struct sockaddr *sender_sock, const SOCKET in_sock,
                  uint8_t *udp_buf, size_t udp_size, time_t now) {

    n2n_common_t cmn;          /* common fields in the packet header */
    n2n_sock_str_t sockbuf1;
    n2n_sock_str_t sockbuf2;        /* don't clobber sockbuf1 if writing two addresses to trace */
    macstr_t mac_buf1;
    macstr_t mac_buf2;
    uint8_t hash_buf[16];
    size_t rem;
    size_t idx;
    size_t msg_type;
    uint8_t from_supernode;
    uint8_t via_multicast;
    struct peer_info *sn = NULL;
    n2n_sock_t sender;
    n2n_sock_t *          orig_sender = NULL;
    uint32_t header_enc = 0;
    uint64_t stamp = 0;
    int skip_add = 0;

    /* REVISIT: when UDP/IPv6 is supported we will need a flag to indicate which
     * IP transport version the packet arrived on. May need to UDP sockets. */

    if(eee->conf.connect_tcp)
        // TCP expects that we know our comm partner and does not deliver the sender
        memcpy(&sender, &(eee->curr_sn->sock), sizeof(sender));
    else {
        // FIXME: do not do random memset on the packet processing path
        memset(&sender, 0, sizeof(sender));
        // REVISIT: type conversion back and forth, choose a consistent approach throughout whole code,
        //          i.e. stick with more general sockaddr as long as possible and narrow only if required
        fill_n2nsock(&sender, sender_sock);
    }
    /* The packet may not have an orig_sender socket spec. So default to last
     * hop as sender. */
    orig_sender = &sender;

#ifdef SKIP_MULTICAST_PEERS_DISCOVERY
    via_multicast = 0;
#else
    via_multicast = (in_sock == eee->udp_multicast_sock);
#endif

    traceEvent(TRACE_DEBUG, "Rx N2N_UDP of size %d from [%s]",
               (signed int)udp_size, sock_to_cstr(sockbuf1, &sender));

    if(mikun2n_handle_nat_probe(eee, &sender, udp_buf, udp_size, now))
        return;

    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
        // match with static (1) or dynamic (2) ctx?
        // check dynamic first as it is identical to static in normal header encryption mode
        if(packet_header_decrypt(udp_buf, udp_size,
                                 (char *)eee->conf.community_name,
                                 eee->conf.header_encryption_ctx_dynamic, eee->conf.header_iv_ctx_dynamic,
                                 &stamp)) {
            header_enc = 2;     /* not accurate with normal header encryption but does not matter */
        }
        if(!header_enc) {
            // check static now (very likely to be REGISTER_SUPER_ACK, REGISTER_SUPER_NAK or invalid)
            if(eee->conf.shared_secret) {
                // hash the still encrypted packet to eventually be able to check it later (required for REGISTER_SUPER_ACK with user/pw auth)
                pearson_hash_128(hash_buf, udp_buf, max(0, (int)udp_size - (int)N2N_REG_SUP_HASH_CHECK_LEN));
            }
            header_enc = packet_header_decrypt(udp_buf, max(0, (int)udp_size - (int)N2N_REG_SUP_HASH_CHECK_LEN),
                                               (char *)eee->conf.community_name,
                                               eee->conf.header_encryption_ctx_static, eee->conf.header_iv_ctx_static,
                                               &stamp);
        }
        if(!header_enc) {
            traceEvent(TRACE_DEBUG, "failed to decrypt header");
            return;
        }
        // time stamp verification follows in the packet specific section as it requires to determine the
        // sender from the hash list by its MAC, or the packet might be from the supernode, this all depends
        // on packet type, path taken (via supernode) and packet structure (MAC is not always in the same place)
    }

    rem = udp_size; /* Counts down bytes of packet to protect against buffer overruns. */
    idx = 0; /* marches through packet header as parts are decoded. */
    if(decode_common(&cmn, udp_buf, &rem, &idx) < 0) {
        if(via_multicast) {
            // from some other edge on local network, possibly header encrypted
            traceEvent(TRACE_DEBUG, "dropped packet arriving via multicast due to error while decoding N2N_UDP");
        } else {
            traceEvent(TRACE_INFO, "failed to decode common section in N2N_UDP");
        }
        return; /* failed to decode packet */
    }

    msg_type = cmn.pc; /* packet code */
    if(sender.family == AF_INET6 &&
       (in_sock != eee->mikun2n_ipv6_socket || msg_type != MSG_TYPE_PACKET ||
        (cmn.flags & N2N_FLAGS_FROM_SUPERNODE)))
        return;

    // special case for user/pw auth
    // community's auth scheme and message type need to match the used key (dynamic)
    if((eee->conf.shared_secret)
       && (msg_type != MSG_TYPE_REGISTER_SUPER_ACK)
       && (msg_type != MSG_TYPE_REGISTER_SUPER_NAK)) {
        if(header_enc != 2) {
            traceEvent(TRACE_INFO, "dropped packet encrypted with static key where dynamic key expected");
            return;
        }
    }

    // check if packet is from supernode and find the corresponding supernode in list
    from_supernode = cmn.flags & N2N_FLAGS_FROM_SUPERNODE;
    if(from_supernode) {
        skip_add = SN_ADD_SKIP;
        sn = add_sn_to_list_by_mac_or_sock(&(eee->conf.supernodes), &sender, null_mac, &skip_add);
        if(!sn) {
            traceEvent(TRACE_DEBUG, "dropped incoming data from unknown supernode");
            return;
        }
    }

    if(0 != memcmp(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE)) {
        // The community in the packet is not matching ours

        if(from_supernode) {
            traceEvent(TRACE_INFO, "received packet with unknown community");
            // TODO:
            // stats.errors.community.supernode ++;
        } else {
            traceEvent(TRACE_INFO, "ignoring packet with unknown community");
            // TODO:
            // stats.errors.community.other ++;
        }

        return;
    }

    switch(msg_type) {
        case MSG_TYPE_PACKET: {
            /* process PACKET - most frequent so first in list. */
            n2n_PACKET_t pkt;

            decode_PACKET(&pkt, &cmn, udp_buf, &rem, &idx);

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       pkt.srcMac,
                       stamp,
                       TIME_STAMP_ALLOW_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped PACKET due to time stamp error");
                    return;
                }
            }

            if(!eee->last_sup) {
                // drop packets received before first registration with supernode
                traceEvent(TRACE_DEBUG, "dropped PACKET recevied before first registration with supernode");
                return;
            }

            if(sender.family == AF_INET6) {
                if(!mikun2n_ipv6_accept(eee, pkt.srcMac, &sender, mikun2n_now_ms()) ||
                   (memcmp(pkt.dstMac, eee->device.mac_addr, N2N_MAC_SIZE) && !is_multi_broadcast(pkt.dstMac)))
                    return;
                handle_PACKET(eee, 0, &pkt, &sender, udp_buf + idx, udp_size - idx);
                break;
            }

            if(!from_supernode) {
                /* This is a P2P packet from the peer. We purge a pending
                 * registration towards the possibly nat-ted peer address as we now have
                 * a valid channel. We still use check_peer_registration_needed in
                 * handle_PACKET to double check this.
                 */
                traceEvent(TRACE_DEBUG, "[p2p] from %s",
                           macaddr_str(mac_buf1, pkt.srcMac));
                find_and_remove_peer(&eee->pending_peers, pkt.srcMac);
            } else {
                /* [PsP] : edge Peer->Supernode->edge Peer */

                if(is_valid_peer_sock(&pkt.sock))
                    orig_sender = &(pkt.sock);

                traceEvent(TRACE_DEBUG, "[pSp] from %s via [%s]",
                           macaddr_str(mac_buf1, pkt.srcMac),
                           sock_to_cstr(sockbuf1, &sender));
            }

            /* Update the sender in peer table entry */
            check_peer_registration_needed(eee, from_supernode, via_multicast,
                                           pkt.srcMac,
                                           // REVISIT: also consider PORT_REG_COOKIEs when implemented
                                           from_supernode ? N2N_FORWARDED_REG_COOKIE : N2N_REGULAR_REG_COOKIE,
                                           NULL, NULL, orig_sender);

            handle_PACKET(eee, from_supernode, &pkt, orig_sender, udp_buf + idx, udp_size - idx);
            break;
        }

        case MSG_TYPE_REGISTER: {
            /* Another edge is registering with us */
            n2n_REGISTER_t reg;

            decode_REGISTER(&reg, &cmn, udp_buf, &rem, &idx);

            via_multicast &= is_null_mac(reg.dstMac);

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       reg.srcMac,
                       stamp,
                       via_multicast ? TIME_STAMP_ALLOW_JITTER : TIME_STAMP_NO_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped REGISTER due to time stamp error");
                    return;
                }
            }

            if(is_valid_peer_sock(&reg.sock))
                orig_sender = &(reg.sock);

            if(via_multicast && !memcmp(reg.srcMac, eee->device.mac_addr, N2N_MAC_SIZE)) {
                traceEvent(TRACE_DEBUG, "skipping REGISTER from self");
                break;
            }

            if(!via_multicast && memcmp(reg.dstMac, eee->device.mac_addr, N2N_MAC_SIZE)) {
                traceEvent(TRACE_DEBUG, "skipping REGISTER for other peer");
                break;
            }

            if(!from_supernode) {
                /* This is a P2P registration from the peer. We purge a pending
                 * registration towards the possibly nat-ted peer address as we now have
                 * a valid channel. We still use check_peer_registration_needed below
                 * to double check this.
                 */
                traceEvent(TRACE_INFO, "[p2p] Rx REGISTER from %s [%s]%s",
                           macaddr_str(mac_buf1, reg.srcMac),
                           sock_to_cstr(sockbuf1, &sender),
                           (reg.cookie & N2N_LOCAL_REG_COOKIE) ? " (local)" : "");
                /* NOTE: only ACK to peers */
                send_register_ack(eee, orig_sender, &reg, in_sock);
                peer_set_p2p_confirmed(eee, reg.srcMac, reg.cookie,
                                       &sender, now, in_sock);
            } else {
                traceEvent(TRACE_INFO, "[pSp] Rx REGISTER from %s [%s] to %s via [%s]",
                           macaddr_str(mac_buf1, reg.srcMac), sock_to_cstr(sockbuf2, orig_sender),
                           macaddr_str(mac_buf2, reg.dstMac), sock_to_cstr(sockbuf1, &sender));
            }

            check_peer_registration_needed(eee, from_supernode, via_multicast,
                                           reg.srcMac, reg.cookie, &reg.dev_addr, (const n2n_desc_t*)&reg.dev_desc, orig_sender);
            break;
        }

        case MSG_TYPE_REGISTER_ACK: {
            /* Peer edge is acknowledging our register request */
            n2n_REGISTER_ACK_t ra;

            decode_REGISTER_ACK(&ra, &cmn, udp_buf, &rem, &idx);

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       ra.srcMac,
                       stamp,
                       TIME_STAMP_NO_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped REGISTER_ACK due to time stamp error");
                    return;
                }
            }

            if(is_valid_peer_sock(&ra.sock))
                orig_sender = &(ra.sock);

            traceEvent(TRACE_INFO, "Rx REGISTER_ACK from %s [%s] to %s via [%s]%s",
                       macaddr_str(mac_buf1, ra.srcMac),
                       sock_to_cstr(sockbuf2, orig_sender),
                       macaddr_str(mac_buf2, ra.dstMac),
                       sock_to_cstr(sockbuf1, &sender),
                       (ra.cookie & N2N_LOCAL_REG_COOKIE) ? " (local)" : "");

            peer_set_p2p_confirmed(eee, ra.srcMac,
                                   ra.cookie,
                                   &sender, now, in_sock);
            break;
        }

        case MSG_TYPE_REGISTER_SUPER_ACK: {
            n2n_REGISTER_SUPER_ACK_t ra;
            uint8_t tmpbuf[REG_SUPER_ACK_PAYLOAD_SPACE];
            int i;
            int skip_add;

            if(!(eee->sn_wait)) {
                traceEvent(TRACE_DEBUG, "Rx REGISTER_SUPER_ACK with no outstanding REGISTER_SUPER");
                return;
            }

            // FIXME: fix decode_* functions to not need memsets
            memset(&ra, 0, sizeof(ra));
            decode_REGISTER_SUPER_ACK(&ra, &cmn, udp_buf, &rem, &idx, tmpbuf);

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       ra.srcMac,
                       stamp,
                       TIME_STAMP_NO_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped REGISTER_SUPER_ACK due to time stamp error");
                    return;
                }
            }

            // hash check (user/pw auth only)
            if(eee->conf.shared_secret) {
                speck_128_encrypt(hash_buf, (speck_context_t*)eee->conf.shared_secret_ctx);
                if(memcmp(hash_buf, udp_buf + udp_size - N2N_REG_SUP_HASH_CHECK_LEN /* length is has already been checked */, N2N_REG_SUP_HASH_CHECK_LEN)) {
                    traceEvent(TRACE_INFO, "Rx REGISTER_SUPER_ACK with wrong hash");
                    return;
                }
            }

            if(ra.cookie != eee->curr_sn->last_cookie) {
                traceEvent(TRACE_INFO, "Rx REGISTER_SUPER_ACK with wrong or old cookie");
                return;
            }

            if(handle_remote_auth(eee, sn, &(ra.auth))) {
                traceEvent(TRACE_INFO, "Rx REGISTER_SUPER_ACK with wrong or old response to challenge");
                if(eee->conf.shared_secret) {
                    traceEvent(TRACE_NORMAL, "Rx REGISTER_SUPER_ACK with wrong or old response to challenge, maybe indicating wrong federation public key (-P)");
                }
                return;
            }

            if(is_valid_peer_sock(&ra.sock))
                orig_sender = &(ra.sock);

            traceEvent(TRACE_INFO, "Rx REGISTER_SUPER_ACK from %s [%s] (external %s) with %u attempts left",
                       macaddr_str(mac_buf1, ra.srcMac),
                       sock_to_cstr(sockbuf1, &sender),
                       sock_to_cstr(sockbuf2, orig_sender),
                       (unsigned int)eee->sup_attempts);

            if(is_null_mac(eee->curr_sn->mac_addr)) {
                HASH_DEL(eee->conf.supernodes, eee->curr_sn);
                memcpy(&eee->curr_sn->mac_addr, ra.srcMac, N2N_MAC_SIZE);
                HASH_ADD_PEER(eee->conf.supernodes, eee->curr_sn);
            }

            n2n_REGISTER_SUPER_ACK_payload_t *payload;
            payload = (n2n_REGISTER_SUPER_ACK_payload_t*)tmpbuf;

            // from here on, 'sn' gets used differently
            for(i = 0; i < ra.num_sn; i++) {
                n2n_sock_t payload_sock;
                skip_add = SN_ADD;

                // bugfix for https://github.com/ntop/n2n/issues/1029
                // REVISIT: best to be removed with 4.0
                idx = 0;
                rem = sizeof(payload->sock);
                decode_sock_payload(&payload_sock, payload->sock, &rem, &idx);

                sn = add_sn_to_list_by_mac_or_sock(&(eee->conf.supernodes), &payload_sock, payload->mac, &skip_add);

                if(skip_add == SN_ADD_ADDED) {
                    // TODO: could just avoid adding the special string
                    // with the hostname when we are adding a supernode
                    // discovered from the reg packet
                    sn->hostname = calloc(1, N2N_EDGE_SN_HOST_SIZE);
                    if(sn->hostname != NULL) {
                        char ip_tmp[N2N_EDGE_SN_HOST_SIZE];

                        inet_ntop(payload_sock.family,
                                  (payload_sock.family == AF_INET) ? (void*)&(payload_sock.addr.v4) : (void*)&(payload_sock.addr.v6),
                                  sn->hostname, N2N_EDGE_SN_HOST_SIZE - 1);
                        sprintf(ip_tmp, "%s:%u", (char*)sn->hostname, (uint16_t)(payload_sock.port));
                        memcpy(sn->hostname, ip_tmp, sizeof(ip_tmp));
                    }
                    sn->last_seen = 0; /* as opposed to payload handling in supernode */
                    traceEvent(
                        TRACE_NORMAL,
                        "supernode '%s' added to the list of supernodes.",
                        peer_info_get_hostname(sn)
                    );
                }
                // shift to next payload entry
                payload++;
            }

            if(eee->conf.tuntap_ip_mode == TUNTAP_IP_MODE_SN_ASSIGN) {
                if((ra.dev_addr.net_addr != 0) && (ra.dev_addr.net_bitlen != 0)) {
                    eee->conf.tuntap_v4.net_addr = htonl(ra.dev_addr.net_addr);
                    eee->conf.tuntap_v4.net_bitlen = ra.dev_addr.net_bitlen;
                }
            }

            eee->sn_wait = 0;
            reset_sup_attempts(eee); /* refresh because we got a response */

            // update last_sup only on 'real' REGISTER_SUPER_ACKs, not on bootstrap ones (own MAC address
            // still null_mac) this allows reliable in/out PACKET drop if not really registered with a supernode yet
            if(!is_null_mac(eee->device.mac_addr)) {
                if(!eee->last_sup) {
                    // indicates first successful connection between the edge and a supernode
                    traceEvent(TRACE_NORMAL, "[OK] edge <<< ================ >>> supernode");
                    // send gratuitous ARP only upon first registration with supernode
                    send_grat_arps(eee);
                }
                eee->last_sup = now;
            }

            // NOTE: the register_interval should be chosen by the edge node based on its NAT configuration.
            // eee->conf.register_interval = ra.lifetime;

            if(eee->cb.sn_registration_updated && !is_null_mac(eee->device.mac_addr))
                eee->cb.sn_registration_updated(eee, now, &sender);

            break;
        }

        case MSG_TYPE_REGISTER_SUPER_NAK: {

            n2n_REGISTER_SUPER_NAK_t nak;

            if(!(eee->sn_wait)) {
                traceEvent(TRACE_DEBUG, "Rx REGISTER_SUPER_NAK with no outstanding REGISTER_SUPER");
                return;
            }

            // FIXME: fix decode_* functions to not need memsets
            memset(&nak, 0, sizeof(nak));
            decode_REGISTER_SUPER_NAK(&nak, &cmn, udp_buf, &rem, &idx);

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       nak.srcMac,
                       stamp,
                       TIME_STAMP_NO_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped REGISTER_SUPER_NAK due to time stamp error");
                    return;
                }
            }

            if(nak.cookie != eee->curr_sn->last_cookie) {
                traceEvent(TRACE_DEBUG, "Rx REGISTER_SUPER_NAK with wrong or old cookie");
                return;
            }

            // REVISIT: authenticate the NAK packet really originating from the supernode along the auth token.
            //          this must follow a different scheme because it needs to prove authenticity although the
            //          edge-provided credentials are wrong

            traceEvent(TRACE_INFO, "Rx REGISTER_SUPER_NAK");

            if((memcmp(nak.srcMac, eee->device.mac_addr, sizeof(n2n_mac_t))) == 0) {
                macstr_t buf_src;
                traceEvent(
                    TRACE_ERROR,
                    "auth error: mac %s",
                    macaddr_str(buf_src, nak.srcMac)
                );
                if(eee->conf.shared_secret) {
                    traceEvent(TRACE_ERROR, "authentication error, username or password not recognized by supernode");
                } else {
                    traceEvent(TRACE_ERROR, "authentication error, MAC or IP address already in use or not released yet by supernode");
                }
                // REVISIT: the following portion is too harsh, repeated error warning should be sufficient until it eventually is resolved,
                //           preventing de-auth attacks
                /* exit(1); this is too harsh, repeated error warning should be sufficient until it eventually is resolved, preventing de-auth attacks
                   } else {
                   HASH_FIND_PEER(eee->known_peers, nak.srcMac, peer);
                   if(peer != NULL) {
                    HASH_DEL(eee->known_peers, peer);
                   }
                   HASH_FIND_PEER(eee->pending_peers, nak.srcMac, scan);
                   if(scan != NULL) {
                    HASH_DEL(eee->pending_peers, scan);
                   } */
            }
            break;
        }

        case MSG_TYPE_PEER_INFO: {

            n2n_PEER_INFO_t pi;
            struct peer_info * scan;
            int skip_add;

            if(decode_PEER_INFO(&pi, &cmn, udp_buf, &rem, &idx) < 0)
                return;

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       null_mac,
                       stamp,
                       TIME_STAMP_ALLOW_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped PEER_INFO due to time stamp error");
                    return;
                }
            }

            if((cmn.flags & N2N_FLAGS_SOCKET) && !is_valid_peer_sock(&pi.sock)) {
                traceEvent(TRACE_DEBUG, "skip invalid PEER_INFO from %s [%s]",
                           macaddr_str(mac_buf1, pi.mac),
                           sock_to_cstr(sockbuf1, &pi.sock));
                break;
            }

            if(is_null_mac(pi.mac)) {
                // PONG - answer to PING (QUERY_PEER_INFO with null mac)
                skip_add = SN_ADD_SKIP;
                scan = add_sn_to_list_by_mac_or_sock(&(eee->conf.supernodes), &sender, pi.srcMac, &skip_add);
                if(scan != NULL) {
                    eee->sn_pong = 1;
                    scan->last_seen = now;
                    scan->uptime = pi.uptime;
                    memcpy(scan->version, pi.version, sizeof(n2n_version_t));
                    /* The data type depends on the actual selection strategy that has been chosen. */
                    SN_SELECTION_CRITERION_DATA_TYPE sn_sel_tmp = pi.load;
                    sn_selection_criterion_calculate(eee, scan, &sn_sel_tmp);

                    traceEvent(TRACE_INFO, "Rx PONG from supernode %s version '%s'",
                               macaddr_str(mac_buf1, pi.srcMac),
                               pi.version);

                    break;
                }
            } else {
                // regular PEER_INFO
                HASH_FIND_PEER(eee->pending_peers, pi.mac, scan);
                int known = !scan;
                if(!scan)
                    // just in case the remote edge has been upgraded by the REG/ACK mechanism in the meantime
                    HASH_FIND_PEER(eee->known_peers, pi.mac, scan);

                if(scan) {
                    scan->mikun2n_peer_info_ms = mikun2n_now_ms();
                    if(eee->conf.mikun2n_ipv6)
                        mikun2n_ipv6_update_peer(scan, &pi, mikun2n_now_ms());
                    if(pi.aflags & N2N_AFLAGS_MIKUN2N_IDENTITY) {
                        memcpy(scan->version, pi.version, sizeof(scan->version));
                        scan->mikun2n_ipv6_wire_version = pi.mikun2n_ipv6_wire_version;
                    } else
                        memset(scan->version, 0, sizeof(scan->version));
                    // Candidate refreshes must not replace an established IPv4 worker path.
                    if(known && eee->conf.mikun2n_ipv6)
                        break;
                    scan->sock = pi.sock;

                    if(pi.aflags & N2N_AFLAGS_MIKUN2N_NAT) {
                        scan->mikun2n_nat_kind = pi.mikun2n_nat_kind;
                        scan->mikun2n_eim_matches = pi.mikun2n_eim_matches;
                        scan->mikun2n_eim_samples = pi.mikun2n_eim_samples;
                    }

                    if(!scan->punch_ipv6_paused_ms &&
                       (pi.aflags & N2N_AFLAGS_MIKUN2N_PUNCH_PLAN) &&
                       pi.mikun2n_punch_role >= MIKUN2N_PUNCH_ROLE_ANCHOR &&
                       pi.mikun2n_punch_role <= MIKUN2N_PUNCH_ROLE_LAYERED &&
                       pi.mikun2n_punch_generation != 0 &&
                       pi.mikun2n_eim_samples <= MIKUN2N_NAT_SAMPLE_ROUNDS) {
                        if(scan->punch_generation != pi.mikun2n_punch_generation) {
                            scan->punch_generation = pi.mikun2n_punch_generation;
                            scan->punch_peer_nonce = pi.mikun2n_punch_nonce;
                            scan->punch_role = pi.mikun2n_punch_role;
                            scan->punch_plan_ready =
                                (pi.mikun2n_nat_kind !=
                                     MIKUN2N_NAT_KIND_APDM ||
                                 mikun2n_nat_kind(&eee->mikun2n_nat) !=
                                     MIKUN2N_NAT_KIND_APDM);
                            scan->punch_go_at_ms = mikun2n_now_ms() +
                                                   pi.mikun2n_punch_delay_ms;
                            scan->punch_started = 0;
                            scan->punch_last_ms = 0;
                            scan->punch_attempt = 0;
                            scan->punch_packets = 0;
                            scan->punch_exhausted = 0;
                            scan->punch_rounds = 0;
                            scan->punch_abandoned = 0;
                            scan->punch_coord_misses = 0;
                            scan->punch_bank_retry_ms = 0;
                            mikun2n_apply_punch_history(eee, scan, now);
                            traceEvent(TRACE_NORMAL,
                                       "MikuN2N punch plan generation=%u peer_role=%s go_in=%ums "
                                       "peer_nat=%u eim=%u/%u",
                                       pi.mikun2n_punch_generation,
                                       mikun2n_punch_role_name(pi.mikun2n_punch_role),
                                       pi.mikun2n_punch_delay_ms,
                                       pi.mikun2n_nat_kind,
                                       pi.mikun2n_eim_matches,
                                       pi.mikun2n_eim_samples);
                        }
                        /* A plan that arrives after our calibration expired
                         * would arm a spray with no worker sockets: seven
                         * silent seconds that still consumed a retry round.
                         * Only a live local pool can act on it; otherwise the
                         * next round recalibrates. */
                        int bank_live =
                            scan->punch_bank_state >= MIKUN2N_BANK_STATE_WAIT_A &&
                            scan->punch_bank_state <= MIKUN2N_BANK_STATE_ARMED &&
                            scan->punch_bank_worker_count > 0;
                        static uint32_t ignored_bank_nonce;
                        if((pi.aflags & N2N_AFLAGS_MIKUN2N_BANK_MODEL) &&
                           pi.mikun2n_bank_nonce != 0 && !bank_live &&
                           scan->punch_peer_bank_nonce !=
                               pi.mikun2n_bank_nonce &&
                           ignored_bank_nonce != pi.mikun2n_bank_nonce) {
                            /* Keep the peer nonce unrecorded so the same plan
                             * is still accepted once a new calibration is live. */
                            ignored_bank_nonce = pi.mikun2n_bank_nonce;
                            traceEvent(TRACE_NORMAL,
                                       "MikuN2N bank plan generation=%u ignored "
                                       "peer=%s: local calibration no longer live (state=%u workers=%u)",
                                       pi.mikun2n_punch_generation,
                                       macaddr_str(mac_buf1, pi.mac),
                                       scan->punch_bank_state,
                                       scan->punch_bank_worker_count);
                        }
                        if((pi.aflags & N2N_AFLAGS_MIKUN2N_BANK_MODEL) &&
                           pi.mikun2n_bank_nonce != 0 && bank_live &&
                           scan->punch_peer_bank_nonce !=
                               pi.mikun2n_bank_nonce) {
                            scan->punch_peer_bank_ready = 1;
                            scan->punch_peer_bank_mode =
                                pi.mikun2n_bank_mode;
                            scan->punch_peer_bank_direction =
                                pi.mikun2n_bank_direction;
                            scan->punch_peer_bank_workers =
                                pi.mikun2n_bank_workers;
                            scan->punch_peer_bank_reuse =
                                pi.mikun2n_bank_reuse;
                            scan->punch_peer_bank1 = pi.mikun2n_bank1;
                            scan->punch_peer_bank2 = pi.mikun2n_bank2;
                            scan->punch_peer_bank_spread =
                                pi.mikun2n_bank_spread;
                            scan->punch_peer_bank_rate =
                                pi.mikun2n_bank_rate;
                            scan->punch_peer_bank_nonce =
                                pi.mikun2n_bank_nonce;
                            scan->punch_plan_ready = 1;
                            scan->punch_go_at_ms = mikun2n_now_ms() +
                                                   pi.mikun2n_punch_delay_ms;
                            scan->punch_bank_state =
                                MIKUN2N_BANK_STATE_ARMED;
                            traceEvent(
                                TRACE_NORMAL,
                                "MikuN2N bank plan generation=%u "
                                "go_in=%ums peer_model=%u banks=%u/%u "
                                "workers=%u",
                                pi.mikun2n_punch_generation,
                                pi.mikun2n_punch_delay_ms,
                                pi.mikun2n_bank_mode,
                                pi.mikun2n_bank1,
                                pi.mikun2n_bank2,
                                pi.mikun2n_bank_workers);
                        }
                    }

                    traceEvent(TRACE_INFO, "Rx PEER_INFO %s can be found at [%s]",
                               macaddr_str(mac_buf1, pi.mac),
                               sock_to_cstr(sockbuf1, &pi.sock));

                    if(cmn.flags & N2N_FLAGS_SOCKET) {
                        scan->preferred_sock = pi.preferred_sock;
                        send_register(eee, &scan->preferred_sock, scan->mac_addr, N2N_LOCAL_REG_COOKIE);

                        traceEvent(TRACE_INFO, "%s has preferred local socket at [%s]",
                                   macaddr_str(mac_buf1, pi.mac),
                                   sock_to_cstr(sockbuf1, &pi.preferred_sock));
                    }

                    send_register(eee, &scan->sock, scan->mac_addr, N2N_REGULAR_REG_COOKIE);

                } else {
                    traceEvent(TRACE_INFO, "Rx PEER_INFO unknown peer %s",
                               macaddr_str(mac_buf1, pi.mac));
                }
            }
            break;
        }

        case MSG_TYPE_RE_REGISTER_SUPER: {

            if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED) {
                if(!find_peer_time_stamp_and_verify(
                       eee->pending_peers,
                       eee->known_peers,
                       sn,
                       null_mac,
                       stamp,
                       TIME_STAMP_NO_JITTER)) {
                    traceEvent(TRACE_DEBUG, "dropped RE_REGISTER due to time stamp error");
                    return;
                }
            }

            // only accept in user/pw mode for immediate re-registration because the new
            // key is required for continous traffic flow, in other modes edge will realize
            // changes with regular recurring REGISTER_SUPER
            if(!eee->conf.shared_secret) {
                traceEvent(TRACE_DEBUG, "dropped RE_REGISTER_SUPER as not in user/pw auth mode");
                return;
            }

            traceEvent(TRACE_INFO, "Rx RE_REGISTER_SUPER");

            eee->sn_wait = 2; /* immediately */

            break;
        }

        default:
            /* Not a known message type */
            traceEvent(TRACE_INFO, "unable to handle packet type %d: ignored", (signed int)msg_type);
            return;
    } /* switch(msg_type) */
}


/* ************************************** */


int fetch_and_eventually_process_data (struct n3n_runtime_data *eee, SOCKET sock,
                                       uint8_t *pktbuf, uint16_t *expected, uint16_t *position,
                                       time_t now) {

    ssize_t bread = 0;

    struct sockaddr_storage sas;
    struct sockaddr *sender_sock = (struct sockaddr*)&sas;
    socklen_t ss_size = sizeof(sas);

    if((!eee->conf.connect_tcp)
#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
       || (sock == eee->udp_multicast_sock)
#endif
    ) {
        // udp
        bread = recvfrom(sock, (void *)pktbuf, N2N_PKT_BUF_SIZE, 0 /*flags*/,
                         sender_sock, &ss_size);

#ifdef _WIN32
        int socket_error = (bread < 0) ? WSAGetLastError() : 0;
#endif
        if((bread < 0)
#ifdef _WIN32
           /* Low-TTL UDP NAT probes can surface as WSAENETRESET on recvfrom(). */
           && (socket_error != WSAECONNRESET)
           && (socket_error != WSAENETRESET)
#endif
        ) {
            /* For UDP bread of zero just means no data (unlike TCP). */
            /* The fd is no good now. Maybe we lost our interface. */
            traceEvent(TRACE_ERROR, "recvfrom() failed %d errno %d (%s)", bread, errno, strerror(errno));
#ifdef _WIN32
            traceEvent(TRACE_ERROR, "WSAGetLastError(): %u", socket_error);
#endif
            return -1;
        }

        // TODO: if bread > 64K, something is wrong
        // but this case should not happen

        // we have a datagram to process...
        if(bread > 0) {
            // ...and the datagram has data (not just a header)
            if(sock == eee->mikun2n_ipv6_socket) {
                n2n_sock_t sender;
                fill_n2nsock(&sender, sender_sock);
                bread = (ssize_t)mikun2n_ipv6_unwrap(eee, &sender, pktbuf, (size_t)bread, mikun2n_now_ms());
                if(!bread)
                    return 0;
            }
            process_udp(eee, sender_sock, sock, pktbuf, bread, now);
        }

    } else {
        // tcp
        bread = recvfrom(sock,
                         (void *)(pktbuf + *position), *expected - *position, 0 /*flags*/,
                         sender_sock, &ss_size);
        if((bread <= 0) && (errno)) {
            traceEvent(TRACE_ERROR, "recvfrom() failed %d errno %d (%s)", bread, errno, strerror(errno));
#ifdef _WIN32
            traceEvent(TRACE_ERROR, "WSAGetLastError(): %u", WSAGetLastError());
#endif
            supernode_disconnect(eee);
            eee->sn_wait = 1;
            goto tcp_done;
        }
        *position = *position + bread;

        if(*position == *expected) {
            if(*position == sizeof(uint16_t)) {
                // the prepended length has been read, preparing for the packet
                *expected = *expected + be16toh(*(uint16_t*)(pktbuf));
                if(*expected > N2N_PKT_BUF_SIZE) {
                    supernode_disconnect(eee);
                    eee->sn_wait = 1;
                    traceEvent(TRACE_DEBUG, "too many bytes expected");
                    goto tcp_done;
                }
            } else {
                // full packet read, handle it
                process_udp(eee, sender_sock, sock,
                            pktbuf + sizeof(uint16_t), *position - sizeof(uint16_t), now);
                // reset, await new prepended length
                *expected = sizeof(uint16_t);
                *position = 0;
            }
        }
    }
tcp_done:
    ;

    return 0;
}


void print_edge_stats (const struct n3n_runtime_data *eee) {

    const struct n2n_edge_stats *s = &eee->stats;

    traceEvent(TRACE_NORMAL, "**********************************");
    traceEvent(TRACE_NORMAL, "Packet stats:");
    traceEvent(TRACE_NORMAL, "      TX P2P: %u pkts", s->tx_p2p);
    traceEvent(TRACE_NORMAL, "      RX P2P: %u pkts", s->rx_p2p);
    traceEvent(TRACE_NORMAL, "      TX Supernode: %u pkts (%u broadcast)", s->tx_sup, s->tx_sup_broadcast);
    traceEvent(TRACE_NORMAL, "      RX Supernode: %u pkts (%u broadcast)", s->rx_sup, s->rx_sup_broadcast);
    traceEvent(TRACE_NORMAL, "**********************************");
}


/* ************************************** */

static int mikun2n_cmp_uint16 (const void *a, const void *b) {
    return (int)*(const uint16_t *)a - (int)*(const uint16_t *)b;
}

static int mikun2n_cmp_int (const void *a, const void *b) {
    return *(const int *)a - *(const int *)b;
}

static int mikun2n_circular_forward (int a, int b) {
    int d = b - a;

    while(d < 0)
        d += 65535;
    while(d >= 65535)
        d -= 65535;
    return d;
}

static uint16_t mikun2n_shift_port (uint16_t base, int direction, int offset) {
    int port = (int)base + direction * offset;

    while(port < 1)
        port += 65535;
    while(port > 65535)
        port -= 65535;
    return (uint16_t)port;
}

static void mikun2n_close_bank_workers (struct peer_info *pp, int keep) {
    int i;

    for(i = 0; i < MIKUN2N_BANK_WORKERS; i++) {
        if(i != keep && pp->punch_workers[i].socket_fd != MIKUN2N_INVALID_SOCKET) {
            closesocket(pp->punch_workers[i].socket_fd);
            pp->punch_workers[i].socket_fd = MIKUN2N_INVALID_SOCKET;
        }
    }
    if(keep < 0)
        pp->punch_bank_worker_count = 0;
}

static int mikun2n_bank_pool_busy (struct n3n_runtime_data *eee,
                                   const struct peer_info *owner) {
    struct peer_info *scan, *tmp;

    HASH_ITER(hh, eee->pending_peers, scan, tmp) {
        if(scan != owner && scan->punch_bank_worker_count > 1 &&
           scan->punch_bank_state != MIKUN2N_BANK_STATE_NONE &&
           scan->punch_bank_state != MIKUN2N_BANK_STATE_FALLBACK)
            return 1;
    }
    return 0;
}

static int mikun2n_create_bank_workers (struct peer_info *pp) {
    int i;

    for(i = 0; i < MIKUN2N_BANK_WORKERS; i++) {
        struct sockaddr_in local;
        socklen_t local_len = sizeof(local);
        SOCKET fd;
#ifdef _WIN32
        u_long non_blocking = 1;
#endif

        memset(&local, 0, sizeof(local));
        local.sin_family = AF_INET;
        local.sin_addr.s_addr = htonl(INADDR_ANY);
        local.sin_port = 0;
        fd = open_socket((struct sockaddr *)&local, sizeof(local), 0);
        if(fd == MIKUN2N_INVALID_SOCKET) {
            mikun2n_close_bank_workers(pp, -1);
            return 0;
        }
#ifdef _WIN32
        ioctlsocket(fd, FIONBIO, &non_blocking);
#else
        fcntl(fd, F_SETFL, O_NONBLOCK);
#endif
        memset(&local, 0, sizeof(local));
        if(getsockname(fd, (struct sockaddr *)&local, &local_len) != 0) {
            closesocket(fd);
            mikun2n_close_bank_workers(pp, -1);
            return 0;
        }
        pp->punch_workers[i].socket_fd = fd;
        pp->punch_workers[i].local_port = ntohs(local.sin_port);
    }
    pp->punch_bank_worker_count = MIKUN2N_BANK_WORKERS;
    return 1;
}

static void mikun2n_send_bank_probe (struct n3n_runtime_data *eee,
                                     struct peer_info *pp,
                                     int probe_b,
                                     uint64_t now_ms) {
    char request[96];
    char client_id[16];
    n2n_sock_t endpoint;
    int i;

    if(!eee->curr_sn)
        return;
    snprintf(client_id, sizeof(client_id), "%08x", pp->punch_bank_nonce);
    endpoint = eee->curr_sn->sock;
    endpoint.port = probe_b ? MIKUN2N_PROBE_PORT_B : MIKUN2N_PROBE_PORT_A;
    for(i = 0; i < pp->punch_bank_worker_count; i++) {
        mikun2n_bank_worker_t *worker = &pp->punch_workers[i];
        uint32_t seq = (pp->punch_bank_nonce & 0x7FFFFF00U) ^
                       (probe_b ? 0x00550000U : 0x002A0000U) ^
                       (uint32_t)i;

        snprintf(request, sizeof(request), "PROBE7 %s %u",
                 client_id, seq);
        mikun2n_sendto_socket(worker->socket_fd, request,
                              strlen(request), &endpoint);
        if(probe_b) {
            worker->seq_b = seq;
            worker->sent_b_ms = now_ms;
        } else {
            worker->seq_a = seq;
            worker->sent_a_ms = now_ms;
        }
    }
}

static void mikun2n_fit_bank_model (struct n3n_runtime_data *eee,
                                    struct peer_info *pp) {
    uint16_t ports[MIKUN2N_BANK_WORKERS];
    int valid = 0, valid_a = 0, reuse = 0;
    int largest_gap = 0, split = -1;
    int small_deltas[MIKUN2N_BANK_WORKERS], small_count = 0;
    int i, median_step = 1;
    uint32_t first_ip = 0;
    int ip_changes = 0;
    int pairs = 0, jumpy = 0;
    uint64_t now_ms = mikun2n_now_ms();
    uint64_t prev_ms = pp->punch_bank_fit_ms;
    uint8_t prev_mode = pp->punch_bank_model_mode;
    uint16_t prev_bank1 = pp->punch_bank1;
    uint16_t prev_bank2 = pp->punch_bank2;
    uint16_t prev_rate = pp->punch_bank_rate;
    int rates[2], nrate = 0, instant_rate = 0, volatile_dual = 0;

    for(i = 0; i < pp->punch_bank_worker_count; i++) {
        mikun2n_bank_worker_t *worker = &pp->punch_workers[i];

        if(worker->mapped_a) {
            valid_a++;
            if(!first_ip)
                first_ip = worker->public_ip_a;
            else if(worker->public_ip_a &&
                    first_ip != worker->public_ip_a)
                ip_changes++;
        }
        if(worker->mapped_b) {
            if(!first_ip)
                first_ip = worker->public_ip_b;
            else if(worker->public_ip_b &&
                    first_ip != worker->public_ip_b)
                ip_changes++;
        }
        if(worker->mapped_a &&
           worker->mapped_a == worker->mapped_b)
            reuse++;
        if(worker->mapped_a && worker->mapped_b &&
           worker->mapped_a != worker->mapped_b) {
            int jump = (int)worker->mapped_b - (int)worker->mapped_a;

            pairs++;
            if(jump < 0)
                jump = -jump;
            if(jump > 96)
                jumpy++;
        }
        if(worker->mapped_b)
            ports[valid++] = worker->mapped_b;
        else if(worker->mapped_a)
            ports[valid++] = worker->mapped_a;
    }

    pp->punch_bank_reuse = (uint8_t)reuse;
    pp->punch_bank_direction = 1;
    pp->punch_bank_rate = 0;

    if(valid < 4) {
        /* Scarce samples. If the previous round had a calibrated fast or
         * volatile model, keep trusting it instead of collapsing to HARD:
         * a CGNAT that cycles its allocation window looks hard on any single
         * under-sampled round. */
        if(prev_ms &&
           prev_mode == MIKUN2N_BANK_MODE_VOLATILE) {
            pp->punch_bank_model_mode = prev_mode;
            pp->punch_bank_rate = prev_rate;
            pp->punch_bank_spread = pp->punch_bank_spread < 64
                                    ? 64 : pp->punch_bank_spread;
            pp->punch_bank_fit_ms = now_ms;
            return;
        }
        pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_HARD;
        pp->punch_bank1 = eee->mikun2n_nat.public_port
                          ? eee->mikun2n_nat.public_port : 1;
        pp->punch_bank2 = pp->punch_bank1;
        pp->punch_bank_spread = 384;
    } else if(!ip_changes && valid_a > 0 &&
              reuse * 100 >= valid_a * 80) {
        pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_CONE;
        pp->punch_bank1 = eee->mikun2n_nat.public_port
                          ? eee->mikun2n_nat.public_port : ports[valid - 1];
        pp->punch_bank2 = pp->punch_bank1;
        pp->punch_bank_spread = 4;
    } else {
        qsort(ports, (size_t)valid, sizeof(ports[0]),
              mikun2n_cmp_uint16);
        for(i = 1; i < valid; i++) {
            int gap = (int)ports[i] - (int)ports[i - 1];

            if(gap > largest_gap) {
                largest_gap = gap;
                split = i;
            }
            if(gap > 0 && gap <= 64)
                small_deltas[small_count++] = gap;
        }
        if(small_count) {
            qsort(small_deltas, (size_t)small_count,
                  sizeof(small_deltas[0]), mikun2n_cmp_int);
            median_step = small_deltas[small_count / 2];
        }

        int dual_bank = largest_gap >= 128 && split >= 3 &&
                        valid - split >= 3;
        int span = (int)ports[valid - 1] - (int)ports[0];

        if(!ip_changes && !dual_bank &&
           (span >= 192 || (pairs >= 4 && jumpy * 4 >= pairs))) {
            /* Wide scatter without bank structure means the NAT allocates
             * randomly inside one window (natpunch's "volatile" NAT).
             * Collapsing to the newest sample discards the window and the
             * scanner then sweeps a +/-15 slice of a ~500-port region, so
             * target the whole observed range instead: lanes tile upward
             * from the low edge and the midpoint, and spread carries the
             * measured width into the peer's predicted lane. */
            pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_VOLATILE;
            pp->punch_bank1 = ports[0];
            pp->punch_bank2 = span > 256
                              ? (uint16_t)(ports[0] + span / 2)
                              : ports[valid - 1];
            pp->punch_bank_spread = (uint16_t)
                (span > 512 ? 512 : (span < 64 ? 64 : span));
        } else {
            pp->punch_bank_model_mode = ip_changes
                                        ? MIKUN2N_BANK_MODE_HARD
                                        : MIKUN2N_BANK_MODE_SYMMETRIC;
            if(dual_bank) {
                pp->punch_bank1 = ports[split - 1];
                pp->punch_bank2 = ports[valid - 1];
            } else {
                pp->punch_bank1 = ports[valid - 1];
                pp->punch_bank2 = pp->punch_bank1;
            }
            pp->punch_bank_spread = (uint16_t)
                (ip_changes ? 512 : median_step * 3 + 12);
        }

        /* Cross-round drift and rate (natpunch v7.3 "volatile dual" /
         * "fast-cycle"). The previous round's bank positions are still in
         * place until we overwrite them, so prev_* captures the old model. */
        if(prev_ms && now_ms > prev_ms) {
            double seconds = (double)(now_ms - prev_ms) / 1000.0;
            int prev1 = (int)prev_bank1, prev2 = (int)prev_bank2;
            int gap_now = abs((int)pp->punch_bank2 - (int)pp->punch_bank1);
            int gap_prev = abs((int)prev_bank2 - (int)prev_bank1);

            if(prev_mode == MIKUN2N_BANK_MODE_SYMMETRIC &&
               pp->punch_bank_model_mode == MIKUN2N_BANK_MODE_SYMMETRIC) {
                int direct = mikun2n_circular_forward(prev1, pp->punch_bank1) +
                             mikun2n_circular_forward(prev2, pp->punch_bank2);
                int swapped = mikun2n_circular_forward(prev1, pp->punch_bank2) +
                              mikun2n_circular_forward(prev2, pp->punch_bank1);
                if(swapped < direct) {
                    prev1 = (int)prev_bank2;
                    prev2 = (int)prev_bank1;
                }
                if((direct < swapped ? direct : swapped) > 1024 ||
                   abs(gap_now - gap_prev) > 256)
                    volatile_dual = 1;
            } else if(prev_mode == MIKUN2N_BANK_MODE_VOLATILE ||
                      pp->punch_bank_model_mode == MIKUN2N_BANK_MODE_VOLATILE) {
                volatile_dual = 1;
            }
            {
                int d1 = mikun2n_circular_forward(prev1, pp->punch_bank1);
                int d2 = mikun2n_circular_forward(prev2, pp->punch_bank2);
                if(d1 > 0 && d1 < 4096)
                    rates[nrate++] = (int)(d1 / seconds);
                if(pp->punch_bank2 != pp->punch_bank1 && d2 > 0 && d2 < 4096)
                    rates[nrate++] = (int)(d2 / seconds);
            }
            if(nrate) {
                qsort(rates, (size_t)nrate, sizeof(rates[0]), mikun2n_cmp_int);
                instant_rate = rates[nrate / 2];
                pp->punch_bank_rate = prev_rate > 0
                    ? (uint16_t)((prev_rate * 2 + instant_rate) / 3)
                    : (uint16_t)instant_rate;
            }
        }
        /* A load-balanced CGNAT can briefly collapse two observed banks into
         * one; keep the volatile classification until a genuinely
         * endpoint-independent (cone) round is observed. */
        if(prev_ms && prev_mode != MIKUN2N_BANK_MODE_CONE &&
           prev_mode == MIKUN2N_BANK_MODE_VOLATILE)
            volatile_dual = 1;
        if(volatile_dual &&
           pp->punch_bank_model_mode == MIKUN2N_BANK_MODE_SYMMETRIC)
            pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_VOLATILE;
        /* A fast-moving single bank (>=120 ports/s between calibration
         * rounds) cannot be tracked: measured mobile-CGNAT rates swing
         * 150-230/s round to round and the window drifts thousands of ports
         * inside one 7s spray (natpunch v7.2 field data: 17 fast-target
         * rounds, zero hits). Do not burn budget chasing a phase - classify
         * as HARD so the peer scans one wide window and settles on relay. */
        if(pp->punch_bank_rate >= MIKUN2N_BANK_FAST_RATE_MIN &&
           pp->punch_bank_model_mode != MIKUN2N_BANK_MODE_CONE) {
            pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_HARD;
            pp->punch_bank_spread = 512;
        }
    }
    pp->punch_bank_fit_ms = now_ms;

    traceEvent(TRACE_NORMAL,
               "MikuN2N bank calibration model=%u workers=%u "
               "reuse=%u banks=%u/%u spread=%u rate=%u/s",
               pp->punch_bank_model_mode,
               pp->punch_bank_worker_count,
               pp->punch_bank_reuse,
               pp->punch_bank1,
               pp->punch_bank2,
               pp->punch_bank_spread,
               pp->punch_bank_rate);
}

static int mikun2n_handle_bank_probe (struct peer_info *pp,
                                      SOCKET in_sock,
                                      const uint8_t *buf,
                                      size_t len,
                                      uint64_t now_ms) {
    char response[256], client_id[32], ip[64], label[16];
    char expected_id[16];
    unsigned int seq;
    unsigned int port;
    long long server_ms;
    int i;

    if(len == 0 || len >= sizeof(response))
        return 0;
    memcpy(response, buf, len);
    response[len] = '\0';
    if(strncmp(response, "PROBED7 ", 8))
        return 0;
    if(sscanf(response, "PROBED7 %31s %u %63s %u %15s %lld",
              client_id, &seq, ip, &port, label, &server_ms) != 6 ||
       port == 0 || port > 65535)
        return 1;
    (void)server_ms;
    snprintf(expected_id, sizeof(expected_id), "%08x",
             pp->punch_bank_nonce);
    if(strcmp(client_id, expected_id))
        return 1;

    for(i = 0; i < pp->punch_bank_worker_count; i++) {
        mikun2n_bank_worker_t *worker = &pp->punch_workers[i];

        if(worker->socket_fd != in_sock)
            continue;
        if(seq == worker->seq_a && label[0] == 'A') {
            worker->mapped_a = (uint16_t)port;
            worker->public_ip_a = inet_addr(ip);
        } else if(seq == worker->seq_b && label[0] == 'B') {
            worker->mapped_b = (uint16_t)port;
            worker->public_ip_b = inet_addr(ip);
        }
        (void)now_ms;
        return 1;
    }
    return 1;
}

static void mikun2n_send_register_worker (struct n3n_runtime_data *eee,
                                          struct peer_info *pp,
                                          SOCKET out_sock,
                                          uint16_t target_port) {
    uint8_t pktbuf[N2N_PKT_BUF_SIZE];
    size_t idx = 0;
    n2n_common_t cmn = {0};
    n2n_REGISTER_t reg = {0};
    n2n_sock_t target = pp->sock;
    int offset = (int)target_port - (int)pp->sock.port;

    if(offset > 32767)
        offset -= 65535;
    else if(offset < -32767)
        offset += 65535;
    if(offset < -MIKUN2N_PUNCH_OFFSET_BIAS ||
       offset > MIKUN2N_PUNCH_OFFSET_BIAS - 1)
        offset = 0;

    cmn.ttl = N2N_DEFAULT_TTL;
    cmn.pc = MSG_TYPE_REGISTER;
    memcpy(cmn.community, eee->conf.community_name, N2N_COMMUNITY_SIZE);
    reg.cookie = MIKUN2N_PUNCH_COOKIE |
                 ((offset + MIKUN2N_PUNCH_OFFSET_BIAS) &
                  MIKUN2N_PUNCH_OFFSET_MASK);
    memcpy(reg.srcMac, eee->device.mac_addr, sizeof(n2n_mac_t));
    memcpy(reg.dstMac, pp->mac_addr, sizeof(n2n_mac_t));
    reg.dev_addr.net_addr = ntohl(eee->device.ip_addr);
    reg.dev_addr.net_bitlen = eee->conf.tuntap_v4.net_bitlen;
    memcpy(reg.dev_desc, eee->conf.dev_desc, N2N_DESC_SIZE);
    encode_REGISTER(pktbuf, &idx, &cmn, &reg);
    if(eee->conf.header_encryption == HEADER_ENCRYPTION_ENABLED)
        packet_header_encrypt(pktbuf, idx, idx,
                              eee->conf.header_encryption_ctx_dynamic,
                              eee->conf.header_iv_ctx_dynamic,
                              time_stamp());
    target.port = target_port;
    if(mikun2n_sendto_socket(out_sock, pktbuf, idx, &target) >= 0)
        pp->punch_packets++;
}

/* While the user forces pSp for a peer, no traffic flows on the direct path,
 * so the NAT pinhole decays and last_p2p goes stale; canceling the relay then
 * hits the timeout/2 idle check, deletes the entry and needs a full re-punch
 * (observed: a 4 s toggle resumed direct instantly, a 24 s one degraded to
 * pSp). A periodic REGISTER over the socket that carries the direct session
 * keeps both the mapping and the entry fresh, so cancel restores P2P at once.
 *
 * IPv6 DATA bypasses the IPv4 path the same way. Keeping a working IPv4 path
 * warm makes it the fallback for a brief IPv6 readiness gap instead of the
 * supernode; a path that stops answering is left to expire normally. */
static void mikun2n_standby_keepalive (struct n3n_runtime_data *eee,
                                       struct peer_info *pp,
                                       time_t now) {
    if(pp->local || pp->sock.family != AF_INET)
        return;
    if(pp->last_p2p == 0)
        return;
    if(mikun2n_sock_is_supernode(eee, &pp->sock))
        return;
    if(!mikun2n_peer_force_relay(eee, pp) &&
       !(mikun2n_ipv6_active(eee, pp, mikun2n_now_ms()) &&
         now - pp->last_p2p < pp->timeout))
        return;
    if(now - pp->punch_keepalive_at < MIKUN2N_RELAY_KEEPALIVE_SECS)
        return;
    pp->punch_keepalive_at = now;
    mikun2n_send_register_worker(
        eee, pp,
        pp->punch_data_sock != MIKUN2N_INVALID_SOCKET
            ? pp->punch_data_sock : eee->sock,
        pp->sock.port);
}

/* A known path whose receive side went quiet (find_peer_destination) keeps its
 * entry, endpoint and - crucially - the socket owning our NAT mapping for a
 * bounded window while DATA uses the supernode. The peer's filter only admits
 * that exact mapping, so tearing it down made even a brief interruption require
 * a full re-punch: one such pair then relayed for hours after three failed
 * rounds. Any direct REGISTER/ACK from the peer restores the path at once. */
static int mikun2n_path_recoverable (const struct n3n_runtime_data *eee,
                                     const struct peer_info *pp,
                                     time_t now) {
    return pp->sock.family == AF_INET && !pp->local && pp->last_p2p &&
           !mikun2n_sock_is_supernode(eee, &pp->sock) &&
           now - pp->last_p2p < pp->timeout / 2 + MIKUN2N_RECOVER_SECS;
}

static void mikun2n_recover_probe (struct n3n_runtime_data *eee,
                                   struct peer_info *pp,
                                   time_t now) {
    macstr_t mac;

    if(!pp->punch_recover_since)
        return;
    if(now - pp->last_p2p < pp->timeout / 2) {
        traceEvent(TRACE_NORMAL,
                   "MikuN2N IPv4 path recovered peer=%s after %us on the original mapping",
                   macaddr_str(mac, pp->mac_addr),
                   (unsigned int)(now - pp->punch_recover_since));
        pp->punch_recover_since = 0;
        return;
    }
    if(!mikun2n_path_recoverable(eee, pp, now) ||
       now == pp->punch_keepalive_at)
        return;
    pp->punch_keepalive_at = now;
    mikun2n_send_register_worker(
        eee, pp,
        pp->punch_data_sock != MIKUN2N_INVALID_SOCKET
            ? pp->punch_data_sock : eee->sock,
        pp->sock.port);
}

static void mikun2n_bank_spray_tick (struct n3n_runtime_data *eee,
                                     struct peer_info *pp) {
    int i;
    uint32_t tick = pp->punch_attempt;
    int peer_cone = pp->punch_peer_bank_mode ==
                    MIKUN2N_BANK_MODE_CONE;
    int peer_hard = pp->punch_peer_bank_mode ==
                    MIKUN2N_BANK_MODE_HARD ||
                    pp->punch_peer_bank_mode ==
                    MIKUN2N_BANK_MODE_VOLATILE;

    for(i = 0; i < pp->punch_bank_worker_count &&
               pp->punch_packets < eee->conf.mikun2n_punch_max_packets;
        i++) {
        SOCKET fd = pp->punch_workers[i].socket_fd;
        uint16_t bank = (pp->punch_peer_bank2 !=
                         pp->punch_peer_bank1 && (i & 1))
                        ? pp->punch_peer_bank2
                        : pp->punch_peer_bank1;

        if(fd == MIKUN2N_INVALID_SOCKET)
            continue;
        mikun2n_send_register_worker(eee, pp, fd, pp->sock.port);
        if(peer_cone) {
            int near_offset =
                1 + (int)((tick * 37U + (uint32_t)i * 11U) % 64U);
            int sign = ((tick + (uint32_t)i) & 1U) ? -1 : 1;
            int band_lo = 65 + (int)(pp->punch_rounds % 22U) * 192;
            int wide = band_lo +
                       (int)((tick * 53U + (uint32_t)i * 17U) % 192U);

            mikun2n_send_register_worker(
                eee, pp, fd,
                mikun2n_shift_port(pp->sock.port, sign, near_offset));
            mikun2n_send_register_worker(
                eee, pp, fd,
                mikun2n_shift_port(pp->sock.port, -sign, wide));
        } else {
            int direction = pp->punch_peer_bank_direction == -1 ? -1 : 1;
            /* Deterministic near-to-far coverage: consecutive ticks advance
             * through low offsets in order, so an exhausted budget has
             * already swept the most likely (nearest) ports. */
            int low = 1 + (int)((tick * MIKUN2N_BANK_WORKERS + i) % 64U);
            int mid = 65 + (int)((tick * MIKUN2N_BANK_WORKERS + i) % 192U);
            int drift = pp->punch_peer_bank_rate
                        ? pp->punch_peer_bank_rate * 8 / 10 : 32;
            int predicted_lo = drift -
                               (int)pp->punch_peer_bank_spread - 64;
            int predicted_hi = drift +
                               (int)pp->punch_peer_bank_spread + 64;
            int predicted;

            if(predicted_lo < 1)
                predicted_lo = 1;
            if(predicted_hi > 512)
                predicted_hi = 512;
            predicted = predicted_lo +
                        (int)((tick * 97U + (uint32_t)i * 23U) %
                              (uint32_t)(predicted_hi - predicted_lo + 1));
            mikun2n_send_register_worker(
                eee, pp, fd,
                mikun2n_shift_port(bank, direction, low));
            mikun2n_send_register_worker(
                eee, pp, fd,
                mikun2n_shift_port(bank, direction, mid));
            mikun2n_send_register_worker(
                eee, pp, fd,
                mikun2n_shift_port(bank, direction, predicted));
            if(peer_hard) {
                int tail = 257 +
                           (int)((tick * 89U + (uint32_t)i * 29U) % 128U);
                mikun2n_send_register_worker(
                    eee, pp, fd,
                    mikun2n_shift_port(bank, direction, tail));
            }
        }
    }
    pp->punch_attempt++;
}

static int mikun2n_bank_update (struct n3n_runtime_data *eee,
                                struct peer_info *pp,
                                time_t now,
                                uint64_t now_ms) {
    int i;
    macstr_t mac_buf;

    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_FALLBACK)
        return 0;
    if(pp->punch_abandoned)
        return 1;
    if(pp->punch_exhausted) {
        if(now < pp->punch_retry_at)
            return 1;
        pp->punch_exhausted = 0;
        pp->punch_started = 0;
        pp->punch_attempt = 0;
        pp->punch_packets = 0;
        pp->punch_peer_bank_ready = 0;
        pp->punch_peer_bank_nonce = 0;
        pp->punch_bank_nonce = 0;
        pp->punch_bank_recalib = 0;
        pp->punch_coord_misses = 0;
        pp->punch_bank_state = MIKUN2N_BANK_STATE_NONE;
    }
    if(!pp->punch_generation) {
        send_query_peer(eee, pp->mac_addr);
        return 1;
    }

    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_NONE) {
        if(now_ms < pp->punch_bank_retry_ms)
            return 1;
        if(mikun2n_bank_pool_busy(eee, pp))
            return 1;
        if(!mikun2n_create_bank_workers(pp)) {
            traceEvent(TRACE_WARNING,
                       "MikuN2N bank calibration could not create workers; "
                       "using legacy NAT4 strategy");
            pp->punch_bank_state = MIKUN2N_BANK_STATE_FALLBACK;
            pp->punch_plan_ready = 1;
            pp->punch_go_at_ms = now_ms;
            return 0;
        }
        pp->punch_bank_nonce = n3n_rand();
        if(!pp->punch_bank_nonce)
            pp->punch_bank_nonce = 1;
        pp->punch_coord_started_ms = now_ms;
        mikun2n_send_bank_probe(eee, pp, 0, now_ms);
        pp->punch_bank_deadline_ms =
            now_ms + MIKUN2N_BANK_CALIBRATION_MS;
        pp->punch_bank_state = MIKUN2N_BANK_STATE_WAIT_A;
        traceEvent(TRACE_NORMAL,
                   "MikuN2N NAT4 bank calibration started workers=%u peer=%s",
                   pp->punch_bank_worker_count,
                   macaddr_str(mac_buf, pp->mac_addr));
        return 1;
    }
    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_WAIT_A &&
       now_ms >= pp->punch_bank_deadline_ms) {
        mikun2n_send_bank_probe(eee, pp, 1, now_ms);
        pp->punch_bank_deadline_ms =
            now_ms + MIKUN2N_BANK_CALIBRATION_MS;
        pp->punch_bank_state = MIKUN2N_BANK_STATE_WAIT_B;
        return 1;
    }
    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_WAIT_B &&
       now_ms >= pp->punch_bank_deadline_ms) {
        mikun2n_fit_bank_model(eee, pp);
        pp->punch_bank_state = MIKUN2N_BANK_STATE_REPORTED;
        pp->punch_coord_last_query_ms = 0;
    }
    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_REPORTED) {
        if(!pp->punch_coord_last_query_ms ||
           now_ms - pp->punch_coord_last_query_ms >=
               MIKUN2N_COORD_QUERY_MS) {
            send_query_peer(eee, pp->mac_addr);
            pp->punch_coord_last_query_ms = now_ms;
        }
        /* The model ages while the peer finishes its own calibration. If it
         * is stale by the time the peer is ready, a punch would aim at a
         * predicted band the NAT has already drifted past. Recalibrate in
         * place (workers stay open, nonce rotates) so the supernode sees a
         * fresh report; cap it so a peer that never reports still falls back
         * to legacy via the timeout below. */
        if(pp->punch_bank_fit_ms &&
           now_ms - pp->punch_bank_fit_ms >= MIKUN2N_BANK_MODEL_AGE_MS &&
           pp->punch_bank_recalib < MIKUN2N_BANK_RECALIB_MAX) {
            pp->punch_bank_recalib++;
            pp->punch_bank_nonce = n3n_rand();
            if(!pp->punch_bank_nonce)
                pp->punch_bank_nonce = 1;
            pp->punch_peer_bank_ready = 0;
            pp->punch_peer_bank_nonce = 0;
            pp->punch_coord_started_ms = now_ms;
            pp->punch_coord_last_query_ms = 0;
            for(i = 0; i < pp->punch_bank_worker_count; i++) {
                pp->punch_workers[i].mapped_a = 0;
                pp->punch_workers[i].mapped_b = 0;
            }
            mikun2n_send_bank_probe(eee, pp, 0, now_ms);
            pp->punch_bank_deadline_ms =
                now_ms + MIKUN2N_BANK_CALIBRATION_MS;
            pp->punch_bank_state = MIKUN2N_BANK_STATE_WAIT_A;
            traceEvent(TRACE_NORMAL,
                       "MikuN2N bank model aged %ums; recalibrating "
                       "(%u/%u)",
                       (unsigned int)(now_ms - pp->punch_bank_fit_ms),
                       pp->punch_bank_recalib,
                       MIKUN2N_BANK_RECALIB_MAX);
            return 1;
        }
        if(now_ms - pp->punch_coord_started_ms >= 8000) {
            macstr_t mac;
            mikun2n_close_bank_workers(pp, -1);
            /* Across ten days of logs the single-socket legacy scanner never
             * connected two APDM endpoints (0 of 39 rounds in one session,
             * about 11,600 packets each), while coordinated banks connected
             * about half of their rounds. A missed coordination - usually the
             * peer's only worker pool serving another peer, or a late
             * calibration - costs no scan traffic, so recalibrate after a short
             * pause before spending this round on legacy. */
            if(pp->punch_coord_misses < MIKUN2N_BANK_COORD_RETRIES) {
                pp->punch_coord_misses++;
                pp->punch_bank_state = MIKUN2N_BANK_STATE_NONE;
                pp->punch_bank_nonce = 0;
                pp->punch_bank_recalib = 0;
                pp->punch_peer_bank_ready = 0;
                pp->punch_peer_bank_nonce = 0;
                pp->punch_bank_retry_ms = now_ms + MIKUN2N_BANK_COORD_RETRY_MS;
                traceEvent(TRACE_NORMAL,
                           "MikuN2N bank coordination missed peer=%s (%u/%u); "
                           "recalibrating in %ums, no round charged",
                           macaddr_str(mac, pp->mac_addr),
                           pp->punch_coord_misses, MIKUN2N_BANK_COORD_RETRIES,
                           MIKUN2N_BANK_COORD_RETRY_MS);
                return 1;
            }
            traceEvent(TRACE_WARNING,
                       "MikuN2N bank coordination timed out; "
                       "using legacy NAT4 strategy peer=%s",
                       macaddr_str(mac, pp->mac_addr));
            pp->punch_bank_state = MIKUN2N_BANK_STATE_FALLBACK;
            pp->punch_plan_ready = 1;
            pp->punch_go_at_ms = now_ms;
            return 0;
        }
        return 1;
    }
    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_ARMED) {
        if(now_ms < pp->punch_go_at_ms ||
           now - pp->time_alloc < eee->conf.mikun2n_punch_grace)
            return 1;
        if(!pp->punch_bank_worker_count) {
            /* Nothing can be sent; never charge a round for it. */
            traceEvent(TRACE_WARNING,
                       "MikuN2N bank punch skipped: no worker sockets; recalibrating");
            pp->punch_bank_state = MIKUN2N_BANK_STATE_NONE;
            pp->punch_bank_nonce = 0;
            pp->punch_peer_bank_ready = 0;
            pp->punch_peer_bank_nonce = 0;
            pp->punch_plan_ready = 0;
            return 1;
        }
        pp->punch_started = now;
        pp->punch_last_ms = now_ms - MIKUN2N_BANK_TICK_MS;
        pp->punch_attempt = 0;
        pp->punch_packets = 0;
        pp->punch_bank_deadline_ms = now_ms + MIKUN2N_BANK_SPRAY_MS;
        pp->punch_bank_state = MIKUN2N_BANK_STATE_SPRAY;
        traceEvent(TRACE_NORMAL,
                   "MikuN2N NAT4 bank punch started peer_control=%u "
                   "self_model=%u banks=%u/%u peer_model=%u banks=%u/%u "
                   "workers=%u budget=%ums peer=%s",
                   pp->sock.port,
                   pp->punch_bank_model_mode,
                   pp->punch_bank1, pp->punch_bank2,
                   pp->punch_peer_bank_mode,
                   pp->punch_peer_bank1, pp->punch_peer_bank2,
                   pp->punch_bank_worker_count,
                   MIKUN2N_BANK_SPRAY_MS,
                   macaddr_str(mac_buf, pp->mac_addr));
    }
    if(pp->punch_bank_state == MIKUN2N_BANK_STATE_SPRAY) {
        if(now_ms >= pp->punch_bank_deadline_ms ||
           pp->punch_packets >= eee->conf.mikun2n_punch_max_packets) {
            int abandoned =
                mikun2n_record_punch_failure(eee, pp, now);
            if(abandoned) {
                traceEvent(TRACE_NORMAL,
                           "MikuN2N NAT4 bank punch abandoned after "
                           "%us/%u ticks/%u packets (round %u/%u); "
                           "stable pSp relay, manual retry required peer=%s",
                           (unsigned int)(now - pp->punch_started),
                           pp->punch_attempt, pp->punch_packets,
                           pp->punch_rounds, MIKUN2N_PUNCH_MAX_ROUNDS,
                           macaddr_str(mac_buf, pp->mac_addr));
            } else {
                traceEvent(TRACE_NORMAL,
                           "MikuN2N NAT4 bank punch exhausted after "
                           "%us/%u ticks/%u packets (round %u/%u); retry in %us peer=%s",
                           (unsigned int)(now - pp->punch_started),
                           pp->punch_attempt, pp->punch_packets,
                           pp->punch_rounds, MIKUN2N_PUNCH_MAX_ROUNDS,
                           MIKUN2N_PUNCH_RETRY_SECS,
                           macaddr_str(mac_buf, pp->mac_addr));
            }
            mikun2n_close_bank_workers(pp, -1);
            return 1;
        }
        if(now_ms - pp->punch_last_ms >= MIKUN2N_BANK_TICK_MS) {
            pp->punch_last_ms += MIKUN2N_BANK_TICK_MS;
            if(now_ms - pp->punch_last_ms >= MIKUN2N_BANK_TICK_MS)
                pp->punch_last_ms = now_ms;
            mikun2n_bank_spray_tick(eee, pp);
        }
        return 1;
    }
    return 1;
}

static int mikun2n_bank_fdset_list (struct peer_info *peers,
                                    fd_set *readers,
                                    int max_sock) {
    struct peer_info *pp, *tmp;
    int i;

    HASH_ITER(hh, peers, pp, tmp) {
        for(i = 0; i < MIKUN2N_BANK_WORKERS; i++) {
            SOCKET fd = pp->punch_workers[i].socket_fd;

            if(fd == MIKUN2N_INVALID_SOCKET)
                continue;
            FD_SET(fd, readers);
            max_sock = max(max_sock, (int)fd);
        }
    }
    return max_sock;
}

static void mikun2n_bank_process_ready_list (
    struct n3n_runtime_data *eee,
    struct peer_info *peers,
    fd_set *readers,
    time_t now) {
    struct peer_info *pp, *tmp;
    int i;

    HASH_ITER(hh, peers, pp, tmp) {
        for(i = 0; i < MIKUN2N_BANK_WORKERS; i++) {
            SOCKET fd = pp->punch_workers[i].socket_fd;
            struct sockaddr_in sender;
            socklen_t sender_len = sizeof(sender);
            uint8_t buf[N2N_PKT_BUF_SIZE];
            int received;

            if(fd == MIKUN2N_INVALID_SOCKET || !FD_ISSET(fd, readers))
                continue;
            memset(&sender, 0, sizeof(sender));
            received = recvfrom(fd, (char *)buf, sizeof(buf), 0,
                                (struct sockaddr *)&sender, &sender_len);
            if(received <= 0)
                continue;
            if(mikun2n_handle_bank_probe(pp, fd, buf,
                                         (size_t)received,
                                         mikun2n_now_ms()))
                continue;
            process_udp(eee, (struct sockaddr *)&sender, fd,
                        buf, (size_t)received, now);
        }
    }
}

/* ************************************** */

static void mikun2n_punch_offset (struct n3n_runtime_data *eee,
                                  struct peer_info *pp,
                                  int base,
                                  int offset) {
    n2n_sock_t target = pp->sock;
    int port = base + offset;

    while(port < 1)
        port += 65535;
    while(port > 65535)
        port -= 65535;

    target.port = (uint16_t)port;
    send_register(eee, &target, pp->mac_addr,
                  MIKUN2N_PUNCH_COOKIE |
                  ((offset + MIKUN2N_PUNCH_OFFSET_BIAS) & MIKUN2N_PUNCH_OFFSET_MASK));
    pp->punch_packets++;
}

/* The NAT4 cone escape: control port, a symmetric near window, and one
 * rotating band.
 *
 * Driven by attempt count, never by wall clock. A wall-clock schedule only
 * looks synchronized: measured peer clock skew is 3.65 s (14 ticks), so the
 * two sides select different bands regardless, and because the loop sustains
 * ~3.1 ticks/s rather than the nominal 4, every slot it misses becomes a
 * permanently unscanned hole. Counting attempts keeps the bands contiguous and
 * ordered near-to-far, so an exhausted budget only drops the far, least likely
 * tail. That ordering is what established NAT4<->NAT4 links before the
 * schedule was switched to wall clock. */
static void mikun2n_punch_cone_sweep (struct n3n_runtime_data *eee,
                                      struct peer_info *pp,
                                      int base,
                                      uint32_t attempt) {
    uint32_t phase = attempt % MIKUN2N_CONE_ESCAPE_TICKS;
    uint32_t round = (attempt / MIKUN2N_CONE_ESCAPE_TICKS) % 22;
    int band_lo = MIKUN2N_CONE_ESCAPE_NEAR +
                  (int)round * MIKUN2N_CONE_ESCAPE_BAND + 1;
    int band_hi = band_lo + MIKUN2N_CONE_ESCAPE_BAND - 1;
    int i;

    pp->punch_band_lo = (uint16_t)band_lo;
    pp->punch_band_hi = (uint16_t)band_hi;

    /* Equal A/B mappings only prove stability across two ports on the probe
     * server. The real peer IP can still select a nearby mapping. Four
     * consecutive ticks cover all 128 near and 384 rotating candidates. */
    for(i = 0; i < 4 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++)
        mikun2n_punch_offset(eee, pp, base, 0);
    for(i = 0; i < 32 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++) {
        int index = (int)phase * 32 + i;
        int magnitude = 1 + index / 2;
        mikun2n_punch_offset(eee, pp, base,
                             (index & 1) ? -magnitude : magnitude);
    }
    for(i = 0; i < 96 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++) {
        int index = (int)phase * 96 + i;
        int magnitude = band_lo + index / 2;
        mikun2n_punch_offset(eee, pp, base,
                             (index & 1) ? -magnitude : magnitude);
    }
    if(pp->punch_drift &&
       pp->punch_packets < eee->conf.mikun2n_punch_max_packets)
        mikun2n_punch_offset(eee, pp, base, pp->punch_drift);
}

/* The supernode answers QUERY_PEER only for registered MACs, and a live peer's
 * relayed traffic refreshes last_seen. Neither for a while: a departed identity. */
static int mikun2n_peer_silent (const struct peer_info *pp, time_t now,
                                uint64_t now_ms) {
    time_t seen = pp->last_seen ? pp->last_seen : pp->time_alloc;

    if(now - seen < MIKUN2N_PEER_SILENT_SECS)
        return 0;
    return !pp->mikun2n_peer_info_ms ||
           now_ms - pp->mikun2n_peer_info_ms >=
               (uint64_t)MIKUN2N_PEER_SILENT_SECS * 1000;
}

/* Drop the round in progress without recording a failure: nothing was learned
 * about the path, and the retry budget belongs to a reachable peer. */
static void mikun2n_suspend_round (struct peer_info *pp) {
    int keep = -1;
    int i;

    if(pp->punch_data_sock != MIKUN2N_INVALID_SOCKET)
        for(i = 0; i < MIKUN2N_BANK_WORKERS; i++)
            if(pp->punch_workers[i].socket_fd == pp->punch_data_sock)
                keep = i;
    mikun2n_close_bank_workers(pp, keep);
    pp->punch_bank_state = MIKUN2N_BANK_STATE_NONE;
    pp->punch_bank_nonce = 0;
    pp->punch_bank_recalib = 0;
    pp->punch_peer_bank_ready = 0;
    pp->punch_peer_bank_nonce = 0;
    pp->punch_started = 0;
    pp->punch_attempt = 0;
    pp->punch_packets = 0;
    pp->punch_plan_ready = 0;
    pp->punch_role = MIKUN2N_PUNCH_ROLE_NONE;
    pp->punch_coord_started_ms = 0;
    pp->punch_coord_last_query_ms = 0;
    pp->punch_generation = 0;
}

/* One heavy scan at a time per edge. Concurrent rounds toward several peers
 * reached ~3,500 packets/s, overflowed the socket send buffer (WSAENOBUFS) and
 * each APDM destination allocated its own CGNAT mapping. A queued round has not
 * started, so it has consumed neither time nor packet budget. */
static int mikun2n_scan_slot_busy (const struct n3n_runtime_data *eee,
                                   const struct peer_info *self) {
    const struct peer_info *pp, *tmp;

    HASH_ITER(hh, eee->pending_peers, pp, tmp) {
        if(pp == self)
            continue;
        if(pp->punch_exhausted)
            continue;
        if(pp->punch_bank_state == MIKUN2N_BANK_STATE_SPRAY)
            return 1;
        if(pp->punch_started &&
           !pp->punch_ipv6_paused_ms &&
           pp->punch_bank_state != MIKUN2N_BANK_STATE_ARMED)
            return 1;
    }
    return 0;
}

/* MikuN2N Tier 1 fallback. n3n's ordinary REGISTER exchange remains Tier 0.
 * A peer that stays pSp is then scanned in 250 ms ticks. Both NAT4 roles run
 * the cone escape above; the anchor additionally refreshes a small fixed
 * mapping set. Other senders retain the layered low/mid/far schedule. Strict
 * time/packet budgets ensure a hard/fast CGNAT quickly settles on reliable
 * relay. */
static void mikun2n_punch_peer (struct n3n_runtime_data *eee,
                                struct peer_info *pp,
                                time_t now,
                                uint64_t now_ms) {
    int i, base, delta;
    int nat4_sender;
    uint32_t required_attempts;
    macstr_t mac_buf;
    static const int anchor_offsets[] = {
        0, 1, -1, 2, -2, 4, -4, 8, -8, 16, -16,
        32, -32, 64, -64, 128, -128, 192, -192, 256, -256
    };

    if(pp->local || pp->sock.family != AF_INET)
        return;
    if(mikun2n_sock_is_supernode(eee, &pp->sock))
        return;                                      /* never scan any federated supernode */
    if((now - pp->last_p2p) < 5)                     /* p2p already established */
        return;
    if(memcmp(pp->mac_addr, eee->device.mac_addr, sizeof(n2n_mac_t)) == 0)
        return;                                      /* don't punch our own echo */
    if(mikun2n_peer_force_relay(eee, pp))
        return;                                      /* user requested relay */

    if(!eee->conf.mikun2n_punch)
        return;

    if(mikun2n_peer_silent(pp, now, now_ms)) {
        if(!pp->punch_exhausted &&
           (pp->punch_started || pp->punch_bank_state != MIKUN2N_BANK_STATE_NONE)) {
            macstr_t mac;
            traceEvent(TRACE_NORMAL,
                       "MikuN2N punch suspended peer=%s: no packets or supernode answer for %us",
                       macaddr_str(mac, pp->mac_addr), MIKUN2N_PEER_SILENT_SECS);
            mikun2n_suspend_round(pp);
        }
        return;
    }

    if(mikun2n_ipv6_active(eee, pp, now_ms)) {
        pp->punch_ipv6_lost_ms = 0;
        if(!pp->punch_ipv6_stable_ms)
            pp->punch_ipv6_stable_ms = now_ms;
        if(now_ms - pp->punch_ipv6_stable_ms >= 5000) {
            if(!pp->punch_ipv6_paused_ms) {
                int keep = -1;
                macstr_t mac;
                /* Release exploratory sockets, never the winning IPv4 socket. */
                if(pp->punch_data_sock != MIKUN2N_INVALID_SOCKET)
                    for(i = 0; i < MIKUN2N_BANK_WORKERS; i++)
                        if(pp->punch_workers[i].socket_fd == pp->punch_data_sock)
                            keep = i;
                mikun2n_close_bank_workers(pp, keep);
                pp->punch_bank_state = MIKUN2N_BANK_STATE_FALLBACK;
                pp->punch_bank_model_mode = MIKUN2N_BANK_MODE_NONE;
                pp->punch_peer_bank_ready = 0;
                pp->punch_peer_bank_nonce = 0;
                pp->punch_plan_ready = 0;
                pp->punch_ipv6_paused_ms = now_ms;
                traceEvent(TRACE_NORMAL, "MikuN2N IPv4 scan paused peer=%s: IPv6 stable; IPv4 destinations retained",
                           macaddr_str(mac, pp->mac_addr));
            }
            return;
        }
    } else {
        pp->punch_ipv6_stable_ms = 0;
        if(pp->punch_ipv6_paused_ms) {
            macstr_t mac;
            /* Readiness gaps are usually well under a second, and the IPv4 path
             * or relay carries DATA meanwhile. Restarting calibration for each
             * one (65 times in one 15-hour log) only spent packets. */
            if(!pp->punch_ipv6_lost_ms)
                pp->punch_ipv6_lost_ms = now_ms;
            if(now_ms - pp->punch_ipv6_lost_ms < MIKUN2N_IPV6_RESUME_HOLD_MS)
                return;
            pp->punch_ipv6_lost_ms = 0;
            /* Paused time is not a failed scan; retain consumed packet budgets. */
            if(pp->punch_started)
                pp->punch_started += (time_t)((now_ms - pp->punch_ipv6_paused_ms) / 1000);
            pp->punch_ipv6_paused_ms = 0;
            pp->punch_last_ms = now_ms;
            pp->punch_coord_started_ms = 0;
            pp->punch_coord_last_query_ms = 0;
            pp->punch_go_at_ms = 0;
            traceEvent(TRACE_NORMAL, "MikuN2N IPv4 scan resumed peer=%s: IPv6 unavailable; remaining budgets retained",
                       macaddr_str(mac, pp->mac_addr));
        }
    }

    /* Do not freeze a role while the NAT result is still "detecting". That
     * produced scanner/scanner when both peers crossed the old 5 s grace
     * before the five-sample classifier completed. */
    if(!eee->mikun2n_nat.complete && !eee->mikun2n_nat.unavailable)
        return;

    mikun2n_apply_punch_history(eee, pp, now);
    if(pp->punch_abandoned)
        return;

    /* NAT3<->NAT4 deliberately remains on the proven layered path. The
     * calibrated worker/bank accelerator is entered only when both ends'
     * current summaries say address-and-port-dependent mapping. */
    if(eee->mikun2n_nat.complete &&
       mikun2n_nat_kind(&eee->mikun2n_nat) == MIKUN2N_NAT_KIND_APDM &&
       pp->mikun2n_nat_kind == MIKUN2N_NAT_KIND_APDM) {
        /* Give native unicast/multicast registration the full grace interval
         * before allocating NAT4 calibration sockets. Keep coordination for
         * the already-proven NAT3/layered path running during that grace. */
        if(now - pp->time_alloc < eee->conf.mikun2n_punch_grace)
            return;
        if(mikun2n_bank_update(eee, pp, now, now_ms))
            return;
    }

    if(eee->mikun2n_nat.complete && !pp->punch_plan_ready) {
        if(!pp->punch_coord_started_ms)
            pp->punch_coord_started_ms = now_ms;
        if(!pp->punch_coord_last_query_ms ||
           now_ms - pp->punch_coord_last_query_ms >= MIKUN2N_COORD_QUERY_MS) {
            send_query_peer(eee, pp->mac_addr);
            pp->punch_coord_last_query_ms = now_ms;
        }
        if(now_ms - pp->punch_coord_started_ms < MIKUN2N_COORD_FALLBACK_MS)
            return;

        /* Backward-compatible fallback for an older/unreachable supernode.
         * The role is selected once and remains immutable for this round. */
        pp->punch_role = mikun2n_nat_kind(&eee->mikun2n_nat) == MIKUN2N_NAT_KIND_APDM
                         ? (memcmp(eee->device.mac_addr, pp->mac_addr, sizeof(n2n_mac_t)) < 0
                            ? MIKUN2N_PUNCH_ROLE_SCANNER : MIKUN2N_PUNCH_ROLE_ANCHOR)
                         : MIKUN2N_PUNCH_ROLE_LAYERED;
        pp->punch_plan_ready = 1;
        pp->punch_go_at_ms = now_ms;
        traceEvent(TRACE_INFO,
                   "MikuN2N punch coordination timed out; using locked local role=%s",
                   mikun2n_punch_role_name(pp->punch_role));
    }

    if(pp->punch_plan_ready && now_ms < pp->punch_go_at_ms)
        return;

    nat4_sender = pp->punch_role == MIKUN2N_PUNCH_ROLE_SCANNER ||
                  pp->punch_role == MIKUN2N_PUNCH_ROLE_ANCHOR;

    if(!pp->punch_started) {
        macstr_t mac;
        if(now - pp->time_alloc < eee->conf.mikun2n_punch_grace)
            return;
        if(mikun2n_scan_slot_busy(eee, pp))
            return;
        if(pp->punch_role == MIKUN2N_PUNCH_ROLE_NONE)
            pp->punch_role = MIKUN2N_PUNCH_ROLE_LAYERED;
        pp->punch_started = now;
        pp->punch_observed_port = pp->sock.port;
        traceEvent(TRACE_NORMAL,
                   "MikuN2N Tier 1 punch started for peer port %u role=%s (budget %us/%u packets) peer=%s",
                   pp->sock.port, mikun2n_punch_role_name(pp->punch_role),
                   eee->conf.mikun2n_punch_budget,
                   eee->conf.mikun2n_punch_max_packets,
                   macaddr_str(mac, pp->mac_addr));
    }

    required_attempts = pp->punch_role == MIKUN2N_PUNCH_ROLE_LAYERED
                        ? MIKUN2N_LAYERED_ESCAPE_TICKS
                        : MIKUN2N_CONE_ESCAPE_TICKS * 22;
    if(pp->punch_exhausted ||
       pp->punch_attempt >= required_attempts ||
       now - pp->punch_started >= eee->conf.mikun2n_punch_budget ||
       pp->punch_packets >= eee->conf.mikun2n_punch_max_packets) {
        if(!pp->punch_exhausted) {
            int abandoned =
                mikun2n_record_punch_failure(eee, pp, now);
            /* This is recovery from an independently discovered lifetime
             * latch, not the cause of the captured failures. Keep the same
             * cooldown on both peers: the supernode plan aligns the first
             * round, so equal cooldowns preserve that overlap on retries. */
            if(abandoned) {
                traceEvent(TRACE_NORMAL,
                           "MikuN2N Tier 1 punch abandoned after %us/%u packets "
                           "(round %u/%u); stable pSp relay, manual retry required peer=%s",
                           (unsigned int)(now - pp->punch_started),
                           pp->punch_packets, pp->punch_rounds,
                           MIKUN2N_PUNCH_MAX_ROUNDS,
                           macaddr_str(mac_buf, pp->mac_addr));
            } else {
                traceEvent(TRACE_NORMAL,
                           "MikuN2N Tier 1 punch exhausted after %us/%u packets "
                           "(round %u/%u); pSp relay, retry in %us peer=%s",
                           (unsigned int)(now - pp->punch_started),
                           pp->punch_packets, pp->punch_rounds,
                           MIKUN2N_PUNCH_MAX_ROUNDS,
                           (unsigned int)(pp->punch_retry_at - now),
                           macaddr_str(mac_buf, pp->mac_addr));
            }
        }
        if(!pp->punch_abandoned && now >= pp->punch_retry_at) {
            pp->punch_exhausted = 0;
            pp->punch_started = 0;
            pp->punch_attempt = 0;
            pp->punch_packets = 0;
            pp->punch_plan_ready = 0;
            pp->punch_role = MIKUN2N_PUNCH_ROLE_NONE;
            pp->punch_coord_started_ms = 0;
            pp->punch_coord_last_query_ms = 0;
            pp->punch_generation = 0;
            /* A bank coordination timeout falls back to the legacy scanner
             * for the remainder of one round only. A fresh automatic/manual
             * round must recalibrate instead of remaining latched in
             * FALLBACK forever. */
            pp->punch_bank_state = MIKUN2N_BANK_STATE_NONE;
            pp->punch_bank_nonce = 0;
            pp->punch_peer_bank_ready = 0;
            pp->punch_peer_bank_nonce = 0;
            pp->punch_coord_misses = 0;
            pp->punch_bank_retry_ms = 0;
        }
        return;
    }

    if(now_ms - pp->punch_last_ms < MIKUN2N_BANK_TICK_MS)
        return;
    pp->punch_last_ms += MIKUN2N_BANK_TICK_MS;
    if(now_ms - pp->punch_last_ms >= MIKUN2N_BANK_TICK_MS)
        pp->punch_last_ms = now_ms;

    base = pp->sock.port;
    if(pp->punch_observed_port && pp->punch_observed_port != pp->sock.port) {
        delta = (int)pp->sock.port - (int)pp->punch_observed_port;
        if(delta > 32767)
            delta -= 65535;
        else if(delta < -32767)
            delta += 65535;
        if(abs(delta) <= 4096)
            pp->punch_drift = (int16_t)delta;
        pp->punch_observed_port = pp->sock.port;
    }

    if(nat4_sender && pp->punch_role == MIKUN2N_PUNCH_ROLE_ANCHOR) {
        pp->punch_band_lo = 0;
        pp->punch_band_hi = 256;
        /* Keep a small, fixed mapping set alive while the complementary peer
         * scans. Repetition is intentional: it avoids both NAT4 endpoints
         * continuously allocating new mappings for hundreds of destinations. */
        for(i = 0; i < (int)(sizeof(anchor_offsets) / sizeof(anchor_offsets[0])); i++) {
            mikun2n_punch_offset(eee, pp, base, anchor_offsets[i]);
            mikun2n_punch_offset(eee, pp, base, anchor_offsets[i]);
        }
        if(pp->punch_drift)
            mikun2n_punch_offset(eee, pp, base, pp->punch_drift);
        if((pp->punch_attempt & 3U) == 0)
            traceEvent(TRACE_NORMAL,
                       "MikuN2N Tier 1 anchor peer_control=%u stable_targets=%u range=+/-256",
                       pp->sock.port,
                       (unsigned int)(sizeof(anchor_offsets) / sizeof(anchor_offsets[0])));
    } else if(nat4_sender) {
        mikun2n_punch_cone_sweep(eee, pp, base, pp->punch_attempt);
        if((pp->punch_attempt % MIKUN2N_CONE_ESCAPE_TICKS) == 0)
            traceEvent(TRACE_NORMAL,
                       "MikuN2N Tier 1 scanner round=%u peer_control=%u near=+/-1..%d rotating=+/-%u..%u",
                       (pp->punch_attempt / MIKUN2N_CONE_ESCAPE_TICKS) % 22 + 1,
                       pp->sock.port, MIKUN2N_CONE_ESCAPE_NEAR,
                       pp->punch_band_lo, pp->punch_band_hi);
    } else {
        uint32_t near_phase = pp->punch_attempt % 4;
        uint32_t wide_phase = pp->punch_attempt % MIKUN2N_LAYERED_ESCAPE_TICKS;
        pp->punch_band_lo = 1;
        pp->punch_band_hi = MIKUN2N_LAYERED_ESCAPE_MAX;
        /* A stable-mapping sender is the best scanner for a NAT4 peer. Keep
         * the advertised endpoint alive while consecutive, non-skipping
         * batches cover every positive and negative offset through +/-4096. */
        for(i = 0; i < 4 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++)
            mikun2n_punch_offset(eee, pp, base, 0);
        for(i = 0; i < 32 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++) {
            int index = (int)near_phase * 32 + i;
            int magnitude = 1 + index / 2;
            mikun2n_punch_offset(eee, pp, base,
                                 (index & 1) ? -magnitude : magnitude);
        }
        for(i = 0; i < 128 && pp->punch_packets < eee->conf.mikun2n_punch_max_packets; i++) {
            int index = (int)wide_phase * 128 + i;
            int magnitude = MIKUN2N_CONE_ESCAPE_NEAR + 1 + index / 2;
            mikun2n_punch_offset(eee, pp, base,
                                 (index & 1) ? -magnitude : magnitude);
        }
        if(pp->punch_drift &&
           pp->punch_packets < eee->conf.mikun2n_punch_max_packets)
            mikun2n_punch_offset(eee, pp, base, pp->punch_drift);
        if((wide_phase % 21U) == 0)
            traceEvent(TRACE_NORMAL,
                       "MikuN2N Tier 1 layered sequential_phase=%u peer_control=%u symmetric=+/-1..%u cycle=%ums",
                       wide_phase + 1, pp->sock.port,
                       MIKUN2N_LAYERED_ESCAPE_MAX,
                       MIKUN2N_LAYERED_ESCAPE_TICKS * MIKUN2N_BANK_TICK_MS);
    }

    pp->punch_attempt++;
}

/* ************************************** */


/* Upstream purge_peer_list skips tables with fewer than 16 entries - sensible
 * for a supernode, but on an edge it kept every departed identity (a reconnect
 * registers a fresh MAC) pending for the whole session, still queried and
 * punched: one evening accumulated 15 identities of a single host. Expire on
 * last_seen regardless of table size, except while an IPv6 path or a bank spray
 * still depends on the entry. */
static size_t mikun2n_purge_pending (struct n3n_runtime_data *eee, time_t now,
                                     uint64_t now_ms) {
    struct peer_info *scan, *tmp;
    size_t purged = 0;

    HASH_ITER(hh, eee->pending_peers, scan, tmp) {
        if(!scan->purgeable ||
           now - scan->last_seen < REGISTRATION_TIMEOUT)
            continue;
        if(mikun2n_ipv6_active(eee, scan, now_ms) ||
           (scan->punch_started && !scan->punch_exhausted))
            continue;
        HASH_DEL(eee->pending_peers, scan);
        mgmt_event_post(N3N_EVENT_PEER, N3N_EVENT_PEER_PURGE, scan);
        peer_info_free(scan);
        purged++;
    }
    return purged;
}

int run_edge_loop (struct n3n_runtime_data *eee) {

    size_t numPurged;
    time_t lastIfaceCheck = 0;
    time_t last_purge_known = 0;
    time_t last_purge_pending = 0;
    uint64_t last_punch_ms = 0;
#ifdef HAVE_BRIDGING_SUPPORT
    time_t last_purge_host = 0;
#endif

    uint16_t expected = sizeof(uint16_t);
    uint16_t position = 0;
    uint8_t pktbuf[N2N_PKT_BUF_SIZE + sizeof(uint16_t)];  /* buffer + prepended buffer length in case of tcp */

#ifdef _WIN32
    struct tunread_arg arg;
    arg.eee = eee;
    HANDLE tun_read_thread = startTunReadThread(&arg);
#endif

    *eee->keep_running = true;
    update_supernode_reg(eee, time(NULL));

    edge_metrics_module1.data = &eee->stats;
    edge_metrics_module2.data = &eee->stats;
    n3n_metrics_register(&edge_metrics_module1);
    n3n_metrics_register(&edge_metrics_module2);

    /* Main loop
     *
     * select() is used to wait for input on either the TAP fd or the UDP/TCP
     * socket. When input is present the data is read and processed by either
     * readFromIPSocket() or edge_read_from_tap()
     */

    while(*eee->keep_running) {

        int rc, max_sock = 0;
        fd_set readers;
        fd_set writers;
        time_t now;

        FD_ZERO(&readers);
        FD_ZERO(&writers);
        mikun2n_ipv6_tick(eee, mikun2n_now_ms());
        if(eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET) {
            FD_SET(eee->mikun2n_ipv6_socket, &readers);
            max_sock = max(max_sock, (int)eee->mikun2n_ipv6_socket);
        }

        if(eee->sock >= 0) {
            FD_SET(eee->sock, &readers);
            max_sock = max(max_sock, eee->sock);
        }
#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
        if(eee->conf.allow_p2p) {
            FD_SET(eee->udp_multicast_sock, &readers);
            max_sock = max(max_sock, eee->udp_multicast_sock);
        }
#endif
        max_sock = mikun2n_bank_fdset_list(eee->pending_peers,
                                            &readers, max_sock);
        max_sock = mikun2n_bank_fdset_list(eee->known_peers,
                                            &readers, max_sock);

#ifndef _WIN32
        FD_SET(eee->device.fd, &readers);
        max_sock = max(max_sock, eee->device.fd);
#endif

        slots_t *slots = eee->mgmt_slots;
        max_sock = max(
            max_sock,
            slots_fdset(
                slots,
                &readers,
                &writers
            )
        );

        // FIXME:
        // unlock the windows tun reader thread before select() and lock it
        // again after select().  It currently works by accident, but the
        // structures it manipulates are not thread-safe, so try to make it
        // work by /design/

        struct timeval wait_time;
        if(!eee->sn_wait &&
           (HASH_COUNT(eee->pending_peers) > 0 ||
            (!eee->mikun2n_nat.complete && !eee->mikun2n_nat.unavailable))) {
            /* Sleep until the next punch is due, not a flat tick past the last
             * wake. A flat 250 ms timeout plus ~65 ms of loop and send latency
             * sustained only ~3.1 ticks/s against a nominal 4, and each tick
             * lost is a band of scan candidates that never goes out. */
            uint64_t left_ms = MIKUN2N_BANK_TICK_MS;
            if(eee->conf.register_ttl > 1) {
                uint64_t wake_ms = mikun2n_now_ms();
                uint64_t due_ms = last_punch_ms + MIKUN2N_BANK_TICK_MS;
                left_ms = (due_ms > wake_ms) ? (due_ms - wake_ms) : 0;
                if(left_ms > MIKUN2N_BANK_TICK_MS)
                    left_ms = MIKUN2N_BANK_TICK_MS;
            }
            wait_time.tv_sec = 0;
            wait_time.tv_usec = (long)(left_ms * 1000);
        } else {
            wait_time.tv_sec = (eee->sn_wait) ? (SOCKET_TIMEOUT_INTERVAL_SECS / 10 + 1) : (SOCKET_TIMEOUT_INTERVAL_SECS);
            wait_time.tv_usec = 0;
        }
        if(eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET && wait_time.tv_sec > 0) {
            wait_time.tv_sec = 0;
            wait_time.tv_usec = 250000;
        }
        rc = select(max_sock + 1, &readers, &writers, NULL, &wait_time);
        now = time(NULL);

        if(rc > 0) {
            // any or all of the FDs could have input; check them all
            if(eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET && FD_ISSET(eee->mikun2n_ipv6_socket, &readers)) {
                if(fetch_and_eventually_process_data(eee, eee->mikun2n_ipv6_socket,
                                                     pktbuf, &expected, &position, now) != 0) {
                    mikun2n_ipv6_close(eee);
                }
            }

            // external packets
            if((eee->sock != -1) && FD_ISSET(eee->sock, &readers)) {
                if(0 != fetch_and_eventually_process_data(
                       eee,
                       eee->sock,
                       pktbuf,
                       &expected,
                       &position,
                       now
                   )) {
                    *eee->keep_running = false;
                }
                if(eee->conf.connect_tcp) {
                    if((expected >= N2N_PKT_BUF_SIZE) || (position >= N2N_PKT_BUF_SIZE)) {
                        // something went wrong, possibly even before
                        // e.g. connection failure/closure in the middle of transmission (between len & data)
                        supernode_disconnect(eee);
                        eee->sn_wait = 1;

                        expected = sizeof(uint16_t);
                        position = 0;
                    }
                }
            }

#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
            if((eee->udp_multicast_sock != -1) && FD_ISSET(eee->udp_multicast_sock, &readers)) {
                if(0 != fetch_and_eventually_process_data(
                       eee,
                       eee->udp_multicast_sock,
                       pktbuf,
                       &expected,
                       &position,
                       now
                   )) {
                    *eee->keep_running = false;
                }
            }
#endif

#ifndef _WIN32
            if((eee->device.fd != -1) && FD_ISSET(eee->device.fd, &readers)) {
                // read an ethernet frame from the TAP socket; write on the IP socket
                edge_read_from_tap(eee);
            }
#endif

            mikun2n_bank_process_ready_list(eee, eee->pending_peers,
                                             &readers, now);
            mikun2n_bank_process_ready_list(eee, eee->known_peers,
                                             &readers, now);

            int slots_ready = slots_fdset_loop(slots, &readers, &writers);

            if(slots_ready < 0) {
                traceEvent(
                    TRACE_ERROR,
                    "slots_fdset_loop returns %i (Is daemon exiting?)", slots_ready
                );
            } else if(slots_ready > 0) {
                // A linear scan is not ideal, but this is a select() loop
                // not one built for performance.
                // - update connslot to have callbacks instead of scan
                // - switch to a modern poll loop (and reimplement differently
                //   for each OS supported)
                // This should only be a concern if we are doing a large
                // number of slot connections
                for(int i=0; i<slots->nr_slots; i++) {
                    if(slots->conn[i].fd == -1) {
                        continue;
                    }

                    if(slots->conn[i].state == CONN_READY) {
                        mgmt_api_handler(eee, &slots->conn[i]);
                    }
                }
            }
        }

        // check for timed out slots
        slots_closeidle(slots);

        // If anything we recieved caused us to stop..
        if(!(*eee->keep_running))
            break;

        // finished processing select data
        update_supernode_reg(eee, now);
        mikun2n_update_nat_probe(eee, now, mikun2n_now_ms());

        numPurged = 0;
        // keep, i.e. do not purge, the known peers while no supernode supernode connection
        if(!eee->sn_wait)
            numPurged = purge_expired_nodes(&eee->known_peers,
                                            eee->sock, NULL,
                                            &last_purge_known,
                                            PURGE_REGISTRATION_FREQUENCY, REGISTRATION_TIMEOUT);
        if(now - last_purge_pending >= PURGE_REGISTRATION_FREQUENCY) {
            numPurged += mikun2n_purge_pending(eee, now, mikun2n_now_ms());
            last_purge_pending = now;
        }

        if(numPurged > 0) {
            traceEvent(
                TRACE_INFO,
                "%u peers removed. now: pending=%u, operational=%u",
                numPurged,
                HASH_COUNT(eee->pending_peers),
                HASH_COUNT(eee->known_peers)
            );
        }

        if((eee->conf.register_ttl > 1) &&
           (mikun2n_now_ms() - last_punch_ms >= MIKUN2N_BANK_TICK_MS)) {
            struct peer_info *pp, *pp_tmp;
            uint64_t punch_now_ms = mikun2n_now_ms();
            HASH_ITER(hh, eee->pending_peers, pp, pp_tmp)
                mikun2n_punch_peer(eee, pp, now, punch_now_ms);
            HASH_ITER(hh, eee->known_peers, pp, pp_tmp) {
                mikun2n_standby_keepalive(eee, pp, now);
                mikun2n_recover_probe(eee, pp, now);
            }
            /* Advance by whole ticks so per-tick latency does not accumulate
             * into the period; resync only when a real stall cost us more than
             * a full tick, so a stalled loop cannot burst to catch up. */
            last_punch_ms += MIKUN2N_BANK_TICK_MS;
            if(punch_now_ms - last_punch_ms >= MIKUN2N_BANK_TICK_MS)
                last_punch_ms = punch_now_ms;
        }

#ifdef HAVE_BRIDGING_SUPPORT
        if((eee->conf.allow_routing) && (now > last_purge_host + SWEEP_TIME)) {
            struct host_info *host, *host_tmp;
            HASH_ITER(hh, eee->known_hosts, host, host_tmp) {
                if(now > host->last_seen + HOSTINFO_TIMEOUT) {
                    HASH_DEL(eee->known_hosts, host);
                    free(host);
                }
            }
            last_purge_host = now;
        }
#endif

        // TODO:
        // - a static ip address mode
        // - a notifier so we dont need to poll for changes
        // - ipv6 support
        // - multi-homing support
        if((eee->conf.tuntap_ip_mode == TUNTAP_IP_MODE_DHCP) &&
           ((now - lastIfaceCheck) > IFACE_UPDATE_INTERVAL)) {
            uint32_t old_ip = eee->device.ip_addr;

            traceEvent(TRACE_INFO, "re-checking dynamic IP address");
            tuntap_get_address(&(eee->device));
            lastIfaceCheck = now;

            if((old_ip != eee->device.ip_addr) && eee->cb.ip_address_changed)
                eee->cb.ip_address_changed(eee, old_ip, eee->device.ip_addr);
        }

        sort_supernodes(eee, now);

        eee->resolution_request = resolve_check(
            eee->resolve_parameter,
            eee->resolution_request,
            now
        );

        if(eee->cb.main_loop_period)
            eee->cb.main_loop_period(eee, now);

    } /* while */

    send_unregister_super(eee);

#ifdef _WIN32
    // No, I dont want to wait for the thread to receive a tap packet
    // and unblock to read it.  So I kill it.  The MSDN warns against
    // using this function, but we are on the way to exit the program,
    // so I believe that the risk is low
    TerminateThread(tun_read_thread, 1);

    WaitForSingleObject(tun_read_thread, INFINITE);
#endif

    supernode_disconnect(eee);

    return 0;
}

/* ************************************** */

/** Deinitialise the edge and deallocate any owned memory. */
void edge_term (struct n3n_runtime_data * eee) {

    mikun2n_ipv6_close(eee);

    resolve_cancel_thread(eee->resolve_parameter);

    if(eee->sock >= 0)
        closesocket(eee->sock);

#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
    if(eee->udp_multicast_sock >= 0)
        closesocket(eee->udp_multicast_sock);
#endif

    clear_peer_list(&eee->pending_peers);
    clear_peer_list(&eee->known_peers);
    clear_peer_list(&eee->conf.supernodes);

#ifdef HAVE_BRIDGING_SUPPORT
    if(eee->conf.allow_routing) {
        struct host_info *host, *host_tmp;
        HASH_ITER(hh, eee->known_hosts, host, host_tmp) {
            HASH_DEL(eee->known_hosts, host);
            free(host);
        }
    }
#endif

    eee->transop.deinit(&eee->transop);
    eee->transop_lzo.deinit(&eee->transop_lzo);
#ifdef HAVE_LIBZSTD
    eee->transop_zstd.deinit(&eee->transop_zstd);
#endif

    destroy_network_traffic_filter(eee->network_traffic_filter);

    // TODO:
    // - slots_close(eee->mgmt_slots)
    // - have a helper to calculate/remember the socket pathname
#ifndef _WIN32
    char unixsock[1024];
    snprintf(unixsock, sizeof(unixsock), "%s/mgmt", eee->conf.sessiondir);
    unlink(unixsock);
    rmdir(eee->conf.sessiondir);
#else
    _rmdir(eee->conf.sessiondir);
#endif
    // Ignore errors in the unlink/rmdir as they could simply be that the
    // paths were chown/chmod by the administrator

    free(eee->conf.sessiondir);

    closeTraceFile();

    slots_free(eee->mgmt_slots);
    free(eee);

#ifdef _WIN32
    destroyWin32();
#endif

}


/* ************************************** */

static int edge_init_sockets (struct n3n_runtime_data *eee) {

    eee->mgmt_slots = slots_malloc(5, 5000, 500);
    if(!eee->mgmt_slots) {
        abort();
    }

    if(eee->conf.mgmt_port) {
        if(slots_listen_tcp(eee->mgmt_slots, eee->conf.mgmt_port, false)!=0) {
            perror("slots_listen_tcp");
            exit(1);
        }
    }

    n3n_config_setup_sessiondir(&eee->conf);

#ifndef _WIN32
    char unixsock[1024];
    snprintf(unixsock, sizeof(unixsock), "%s/mgmt", eee->conf.sessiondir);

    int e = slots_listen_unix(
        eee->mgmt_slots,
        unixsock,
        eee->conf.mgmt_sock_perms,
        eee->conf.userid,
        eee->conf.groupid
    );
    // TODO:
    // - do we actually want to tie the user/group to the running pid?

    if(e!=0) {
        perror("slots_listen_tcp");
        exit(1);
    }
#endif

#ifndef SKIP_MULTICAST_PEERS_DISCOVERY
    if(eee->udp_multicast_sock >= 0)
        closesocket(eee->udp_multicast_sock);

    /* Populate the multicast group for local edge */
    eee->multicast_peer.family     = AF_INET;
    eee->multicast_peer.port       = N2N_MULTICAST_PORT;
    eee->multicast_peer.addr.v4[0] = 224; /* N2N_MULTICAST_GROUP */
    eee->multicast_peer.addr.v4[1] = 0;
    eee->multicast_peer.addr.v4[2] = 0;
    eee->multicast_peer.addr.v4[3] = 68;

    struct sockaddr_in local_address;
    memset(&local_address, 0, sizeof(local_address));
    local_address.sin_family = AF_INET;
    local_address.sin_port = htons(N2N_MULTICAST_PORT);
    local_address.sin_addr.s_addr = htonl(INADDR_ANY);

    eee->udp_multicast_sock = open_socket(
        (struct sockaddr *)&local_address,
        sizeof(local_address),
        0 /* UDP */
    );
    if(eee->udp_multicast_sock < 0) {
        return(-3);
    }

    u_int enable_reuse = 1;

    /* allow multiple sockets to use the same PORT number */
    setsockopt(eee->udp_multicast_sock, SOL_SOCKET, SO_REUSEADDR, (char *)&enable_reuse, sizeof(enable_reuse));
#ifdef SO_REUSEPORT /* no SO_REUSEPORT in Windows / old linux versions */
    setsockopt(eee->udp_multicast_sock, SOL_SOCKET, SO_REUSEPORT, &enable_reuse, sizeof(enable_reuse));
#endif

#endif

    return(0);
}


/* ************************************** */


void edge_init_conf_defaults (n2n_edge_conf_t *conf, char *sessionname) {

    memset(conf, 0, sizeof(*conf));

    // Record the session name we used
    if(sessionname) {
        conf->sessionname = sessionname;
    } else {
        conf->sessionname = "NULL";
    }

    conf->is_edge = true;

    conf->bind_address = NULL;
    conf->preferred_sock.family = AF_INVALID;
#ifdef _WIN32
    // Cannot rely on having unix domain sockets on windows
    conf->mgmt_port = N2N_EDGE_MGMT_PORT;
#endif
    conf->transop_id = N2N_TRANSFORM_ID_NULL;
    conf->header_encryption = HEADER_ENCRYPTION_NONE;
    conf->compression = N2N_COMPRESSION_ID_NONE;
    conf->allow_p2p = true;
    conf->register_interval = REGISTER_SUPER_INTERVAL_DFL;
    conf->mikun2n_punch = true;
    conf->mikun2n_ipv6 = false;
    conf->mikun2n_punch_grace = MIKUN2N_NATIVE_GRACE_SECS;
    conf->mikun2n_punch_budget = MIKUN2N_PUNCH_BUDGET_SECS;
    conf->mikun2n_punch_max_packets = MIKUN2N_PUNCH_MAX_PACKETS;

#ifdef _WIN32
    // TODO: more investigations in interface naming/renaming on windows
    conf->tuntap_dev_name[0] = '\0';
#else
    snprintf(
        conf->tuntap_dev_name,
        sizeof(conf->tuntap_dev_name),
        "%s",
        conf->sessionname
    );
#endif

    conf->tuntap_ip_mode = TUNTAP_IP_MODE_SN_ASSIGN;
    conf->tuntap_v4.net_bitlen = N2N_EDGE_DEFAULT_V4MASKLEN;

    /* reserve possible last char as null terminator. */
    gethostname((char*)conf->dev_desc, N2N_DESC_SIZE-1);

    conf->mgmt_password = strdup(N3N_MGMT_PASSWORD);

    conf->sn_selection_strategy = SN_SELECTION_STRATEGY_LOAD;
    conf->metric = 0;
    conf->mtu = DEFAULT_MTU;

#ifndef _WIN32
    struct passwd *pw = NULL;
    // Search a couple of usernames for one to use
    pw = getpwnam("n3n");
    if(pw == NULL) {
        pw = getpwnam("nobody");
    }
    if(pw != NULL) {
        // If we find one, use that as our default
        conf->userid = pw->pw_uid;
        conf->groupid = pw->pw_gid;
    }
#endif
}

/* ************************************** */

void edge_term_conf (n2n_edge_conf_t *conf) {

    free(conf->encrypt_key);
    free(conf->mgmt_password);

    if(conf->network_traffic_filter_rules) {
        filter_rule_t *el = 0, *tmp = 0;
        HASH_ITER(hh, conf->network_traffic_filter_rules, el, tmp) {
            HASH_DEL(conf->network_traffic_filter_rules, el);
            free(el);
        }
    }
}

/* ************************************** */

int quick_edge_init (char *device_name, char *community_name,
                     char *encrypt_key, char *device_mac,
                     in_addr_t local_ip_address,
                     char *supernode_ip_address_port,
                     bool *keep_on_running) {

    tuntap_dev tuntap;
    struct n3n_runtime_data *eee;
    n2n_edge_conf_t conf;
    int rv;

    /* Setup the configuration */
    edge_init_conf_defaults(&conf,"edge");
    n3n_config_load_env(&conf);
    conf.encrypt_key = encrypt_key;
    conf.transop_id = N2N_TRANSFORM_ID_AES;
    conf.compression = N2N_COMPRESSION_ID_NONE;
    snprintf((char*)conf.community_name, sizeof(conf.community_name), "%s", community_name);
    n3n_peer_add_by_hostname(&conf.supernodes, supernode_ip_address_port);

    /* Validate configuration */
    if(edge_verify_conf(&conf) != 0)
        return(-1);

    struct n2n_ip_subnet subnet;
    subnet.net_addr = htonl(local_ip_address);
    subnet.net_bitlen = N2N_EDGE_DEFAULT_V4MASKLEN;

    /* Open the tuntap device */
    if(tuntap_open(&tuntap, device_name, TUNTAP_IP_MODE_STATIC,
                   subnet,
                   device_mac, DEFAULT_MTU,
                   0) < 0)
        return(-2);

    /* Init edge */
    if((eee = edge_init(&conf, &rv)) == NULL)
        goto quick_edge_init_end;

    eee->keep_running = keep_on_running;
    rv = run_edge_loop(eee);
    edge_term(eee);
    edge_term_conf(&conf);

quick_edge_init_end:
    tuntap_close(&tuntap);
    return(rv);
}

/* ************************************** */
