/*
 * Copyright (C) 2023-24 Hamish Coleman
 * SPDX-License-Identifier: GPL-3.0-only
 *
 * Common routines shared between the management interfaces
 *
 */


#include <connslot/connslot.h>  // for conn_t
#include <connslot/jsonrpc.h>   // for jsonrpc_t, jsonrpc_parse
#include <n3n/ethernet.h>       // for is_null_mac
#include <n3n/logging.h> // for traceEvent
#include <n3n/metrics.h> // for n3n_metrics_render
#include <n3n/strings.h> // for ip_subnet_to_str, sock_to_cstr
#include <n3n/supernode.h>      // for load_allowed_sn_community
#include <sn_selection.h> // for sn_selection_criterion_str
#include <stdbool.h>
#include <stdio.h>       // for snprintf, NULL, size_t
#include <stdlib.h>      // for strtoul
#include <string.h>      // for strtok, strlen, strncpy
#include "base64.h"      // for base64decode
#include "management.h"
#include "mikun2n_ipv6.h"
#include "mikun2n_build_version.h"
#include "mikun2n_relay.h"
#include "peer_info.h"   // for peer_info

#ifdef _WIN32
#include "win32/defs.h"
#else
#include <netdb.h>       // for getnameinfo, NI_NUMERICHOST, NI_NUMERICSERV
#include <sys/socket.h>  // for sendto, sockaddr
#endif

static struct metrics {
    uint32_t event_write_error;
} metrics;

static void generate_http_headers (conn_t *conn, const char *type, int code) {
    strbuf_t **pp = &conn->reply_header;
    sb_reprintf(pp, "HTTP/1.1 %i result\r\n", code);
    // TODO:
    // - caching
    int len = sb_len(conn->reply);
    sb_reprintf(pp, "Content-Type: %s\r\n", type);
    sb_reprintf(pp, "Content-Length: %i\r\n\r\n", len);
}

static void render_error (conn_t *conn, const char *message) {
    sb_zero(conn->request);
    sb_printf(conn->request, "%s\n", message);

    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;

    generate_http_headers(conn, "text/plain", 404);
}

static bool auth_check (struct n3n_runtime_data *eee, conn_t *conn) {
    char *p = strstr(conn->request->str, "Authorization:");
    if(!p) {
        // No auth header
        return false;
    }
    strtok(p, " "); // Skip the Authorization: header
    p = strtok(NULL, " ");
    if(strcmp(p, "Basic")) {
        // They sent something other than basic
        return false;
    }

    p = strtok(NULL, " \r\n");

    char *decoded = base64decode(p);
    if(!decoded) {
        // they didnt send us valid base64
        return false;
    }

    p = strtok(decoded,":"); // Skip the username
    p = strtok(NULL,":");
    if(!p) {
        // they didnt send us a complete auth header
        return false;
    }

    if(strcmp(eee->conf.mgmt_password, p)) {
        // They didnt send the right password
        free(decoded);
        return false;
    }

    free(decoded);
    return true;
}

static void auth_request (conn_t *conn) {
    sb_zero(conn->request);
    sb_printf(conn->request, "%s\n", "unauthorised");

    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;

    strbuf_t **pp = &conn->reply_header;
    sb_reprintf(pp, "HTTP/1.1 401 unauth\r\n");
    int len = sb_len(conn->reply);
    sb_reprintf(pp, "Content-Type: text/plain\r\n");
    sb_reprintf(pp, "WWW-Authenticate: Basic realm=\"n3n\"\r\n");
    sb_reprintf(pp, "Content-Length: %i\r\n\r\n", len);
}

#if 0
/*
 * Check if the user is authorised for this command.
 * - this should be more configurable!
 * - for the moment we use some simple heuristics:
 *   Reads are not dangerous, so they are simply allowed
 *   Writes are possibly dangerous, so they need a fake password
 */
int mgmt_auth (mgmt_req_t *req, char *auth) {

    if(auth) {
        /* If we have an auth key, it must match */
        if(!strcmp(req->mgmt_password, auth)) {
            return 1;
        }
        return 0;
    }
    /* if we dont have an auth key, we can still read */
    if(req->type == N2N_MGMT_READ) {
        return 1;
    }

    return 0;
}
#endif

static void event_debug (strbuf_t *buf, enum n3n_event_topic topic, int data0, const void *data1) {
    traceEvent(TRACE_DEBUG, "Unexpected call to event_debug");
    return;
}

static void event_test (strbuf_t *buf, enum n3n_event_topic topic, int data0, const void *data1) {
    sb_printf(
        buf,
        "\x1e{"
        "\"event\":\"test\","
        "\"params\":%s}\n",
        (char *)data1);
}

static const char *event_peer_actions[] = {
    [N3N_EVENT_PEER_PURGE] = "purge",
    [N3N_EVENT_PEER_CLEAR] = "clear",
    [N3N_EVENT_PEER_P2P_ADD] = "p2p_add",
    [N3N_EVENT_PEER_P2P_CHANGED] = "p2p_changed",
    [N3N_EVENT_PEER_P2P_EXPIRED] = "p2p_expired",
};

static void event_peer (strbuf_t *buf, enum n3n_event_topic topic, int data0, const void *data1) {
    int action = data0;
    struct peer_info *peer = (struct peer_info *)data1;

    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;
    uint32_t age = time(NULL) - peer->time_alloc;

    /*
     * Just the peer_info bits that are needed for lookup (maccaddr) or
     * firewall and routing (sockaddr)
     * If needed, other details can be fetched via the edges method call.
     */
    sb_printf(
        buf,
        "\x1e{"
        "\"event\":\"peer\","
        "\"action\":\"%s\","
        "\"macaddr\":\"%s\","
        "\"age\":%u,"
        "\"sockaddr\":\"%s\"}\n",
        event_peer_actions[action],
        (is_null_mac(peer->mac_addr)) ? "" : macaddr_str(mac_buf, peer->mac_addr),
        age,
        sock_to_cstr(sockbuf, &(peer->sock))
    );

    // TODO: a generic truncation watcher for these buffers
}

/* Current subscriber for each event topic */
static SOCKET mgmt_event_subscribers[] = {
    [N3N_EVENT_DEBUG] = -1,
    [N3N_EVENT_TEST] = -1,
    [N3N_EVENT_PEER] = -1,
};

struct mgmt_event {
    char *topic;
    char *desc;
    void (*func)(strbuf_t *buf, enum n3n_event_topic topic, int data0, const void *data1);
};

static const struct mgmt_event mgmt_events[] = {
    [N3N_EVENT_DEBUG] = {
        .topic = "debug",
        .desc = "All events - for event debugging",
        .func = event_debug,
    },
    [N3N_EVENT_TEST] = {
        .topic = "test",
        .desc = "Used only by post.test",
        .func = event_test,
    },
    [N3N_EVENT_PEER] = {
        .topic = "peer",
        .desc = "Changes to peer list",
        .func = event_peer,
    },
};

static void event_subscribe (struct n3n_runtime_data *eee, conn_t *conn) {
    char *match = "GET /events/"; // what we expect to have been called with
    char *urltail = &conn->request->str[strlen(match)];
    char *topic = strtok(urltail, " ");

    enum n3n_event_topic topicid;

    int nr_topics = sizeof(mgmt_events) / sizeof(mgmt_events[0]);
    for( topicid=0; topicid < nr_topics; topicid++ ) {
        if(!strcmp(mgmt_events[topicid].topic,topic)) {
            break;
        }
    }
    if( topicid >= nr_topics ) {
        render_error(conn, "unknown topic");
        return;
    }

    bool replacing = false;

    if(mgmt_event_subscribers[topicid] != -1) {
        // TODO: send a goodbye message to old subscriber
        close(mgmt_event_subscribers[topicid]);

        replacing = true;
    }

    // Take the filehandle away from the connslots.
    mgmt_event_subscribers[topicid] = conn->fd;
    conn_zero(conn);

    // TODO: shutdown(fd, SHUT_RD) - but that does nothing for unix domain

    // The assigned mime type is actually application/json-seq, but firefox
    // will usefully show you the raw streaming data if we use the wrong
    // content type
    char *msg1 = "HTTP/1.1 200 event\r\nContent-Type: application/json\r\n\r\n";
    if(write(mgmt_event_subscribers[topicid], msg1, strlen(msg1))<1) {
        metrics.event_write_error++;
    }

    if(replacing) {
        char *msg2 = "\x1e\"replacing\"\n";
        if(write(mgmt_event_subscribers[topicid], msg2, strlen(msg2))<1) {
            metrics.event_write_error++;
        }
    }
}

void mgmt_event_post (const enum n3n_event_topic topic, int data0, const void *data1) {
    traceEvent(TRACE_DEBUG, "post topic=%i data0=%i", topic, data0);

    SOCKET debug = mgmt_event_subscribers[N3N_EVENT_DEBUG];
    SOCKET sub = mgmt_event_subscribers[topic];

    if( sub == -1 && debug == -1) {
        // If neither of this topic or the debug topic have a subscriber
        // then we dont need to do any work
        return;
    }

    char buf_space[200];
    strbuf_t *buf;
    STRBUF_INIT(buf, buf_space);

    mgmt_events[topic].func(buf, topic, data0, data1);

    if( sub != -1 ) {
        if(sb_write(sub, buf, 0, -1) == -1) {
            mgmt_event_subscribers[topic] = -1;
            close(sub);
        }
    }
    if( debug != -1 ) {
        if(sb_write(debug, buf, 0, -1) == -1) {
            mgmt_event_subscribers[N3N_EVENT_DEBUG] = -1;
            close(debug);
        }
    }
    // TODO:
    // - ideally, we would detect that the far end has gone away and
    //   set the subscriber socket back to -1
    // - this all assumes that the socket is set to non blocking
    // - if the write returns EWOULDBLOCK, increment a metric and return
}

static void extract_pagination (char *params, int *limit, int *offset) {
    char *limitstr = json_find_field(params, "\"limit\"");
    char *offsetstr = json_find_field(params, "\"offset\"");

    // do all the field finding first, since the value extractor will
    // insert nulls at the end of its strings

    if(limitstr) {
        *limit = atoi(json_extract_val(limitstr));
    } else {
        *limit = 2147483647; // default
    }

    if(offsetstr) {
        *offset = atoi(json_extract_val(offsetstr));
    } else {
        offset = 0;
    }
}

static void jsonrpc_error (char *id, conn_t *conn, int code, char *message, int count) {
    // Reuse the request buffer
    sb_zero(conn->request);

    sb_reprintf(
        &conn->request,
        "{"
        "\"jsonrpc\":\"2.0\","
        "\"id\":\"%s\","
        "\"error\":{"
        "\"code\":%i,"
        "\"message\":\"%s\"",
        id,
        code,
        message
    );

    if(count) {
        sb_reprintf(
            &conn->request,
            ","
            "\"data\":"
            "{"
            "\"count\":%i"
            "}",
            count
        );
    }
    sb_reprintf(&conn->request, "}");
}

static void jsonrpc_result_head (char *id, conn_t *conn) {
    // Reuse the request buffer
    sb_zero(conn->request);

    sb_reprintf(
        &conn->request,
        "{"
        "\"jsonrpc\":\"2.0\","
        "\"id\":\"%s\","
        "\"result\":",
        id
    );
}

static void jsonrpc_result_tail (conn_t *conn, int code) {
    sb_reprintf(&conn->request, "}");

    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;

    generate_http_headers(conn, "application/json", code);
}

static void jsonrpc_1uint (char *id, conn_t *conn, uint32_t result) {
    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "%u", result);
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_verbose (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    jsonrpc_1uint(id, conn, getTraceLevel());
}

static void jsonrpc_set_verbose (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params_in) {
    if(!auth_check(eee, conn)) {
        auth_request(conn);
        return;
    }

    if(!params_in) {
        jsonrpc_error(id, conn, 400, "missing param", 0);
        return;
    }

    if(*params_in != '[') {
        jsonrpc_error(id, conn, 400, "expecting array", 0);
        return;
    }

    // Avoid discarding the const attribute
    // TODO: avoid malloc()
    char *params = strdup(params_in+1);

    char *arg1 = json_extract_val(params);

    if(*arg1 == '"') {
        arg1++;
    }

    setTraceLevel(strtoul(arg1, NULL, 0));
    jsonrpc_get_verbose(id, eee, conn, params);
    free(params);
}

static void jsonrpc_stop (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    if(!auth_check(eee, conn)) {
        auth_request(conn);
        return;
    }

    *eee->keep_running = false;

    jsonrpc_1uint(id, conn, *eee->keep_running);
}

/* Does this peer entry carry either key of the requested policy? A peer entry
 * created from a data PACKET has no dev_addr - only a REGISTER carries one - so
 * matching on the virtual IPv4 alone silently misses exactly the peers a manual
 * pSp request is aimed at, leaving the relay one-sided. */
static int mikun2n_relay_target_matches (const struct peer_info *peer,
                                         uint32_t target,
                                         const n2n_mac_t mac,
                                         int have_mac) {
    if(peer->dev_addr.net_addr != 0 && peer->dev_addr.net_addr == target)
        return 1;
    if(have_mac && !memcmp(peer->mac_addr, mac, sizeof(n2n_mac_t)))
        return 1;
    return 0;
}

static void mikun2n_reset_punch_history (struct n3n_runtime_data *eee,
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

static void mikun2n_reset_peer_punch (struct peer_info *peer) {
    peer->punch_started = 0;
    peer->punch_last_ms = 0;
    peer->punch_keepalive_at = 0;
    peer->punch_attempt = 0;
    peer->punch_packets = 0;
    peer->punch_exhausted = 0;
    peer->punch_rounds = 0;
    peer->punch_abandoned = 0;
    peer->punch_retry_at = 0;
    peer->punch_role = MIKUN2N_PUNCH_ROLE_NONE;
    peer->punch_plan_ready = 0;
    peer->punch_generation = 0;
    peer->punch_coord_started_ms = 0;
    peer->punch_coord_last_query_ms = 0;
    peer->punch_bank_state = 0;
    peer->punch_bank_nonce = 0;
    peer->punch_peer_bank_ready = 0;
    peer->punch_peer_bank_nonce = 0;
}

static void mikun2n_restore_peer_punch_history (
    const struct n3n_runtime_data *eee,
    struct peer_info *peer) {
    uint8_t i;

    for(i = 0; i < eee->mikun2n_punch_history_count; i++) {
        const mikun2n_punch_history_t *history =
            &eee->mikun2n_punch_history[i];
        if(memcmp(history->mac, peer->mac_addr, sizeof(n2n_mac_t)))
            continue;
        peer->punch_rounds = history->rounds;
        peer->punch_retry_at = history->retry_at;
        peer->punch_abandoned = history->abandoned;
        if(history->abandoned || time(NULL) < history->retry_at)
            peer->punch_exhausted = 1;
        return;
    }
}

static void jsonrpc_set_peer_relay (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    char address[64] = {0};
    char enabled[8] = {0};
    char mac_text[32] = {0};
    struct peer_info *peer, *tmp;
    in_addr_t network_address;
    n2n_mac_t mac = {0};
    int have_mac = 0;
    uint32_t target;
    uint32_t changed = 0;
    int force_relay;
    uint8_t policy_index;

    if(!auth_check(eee, conn)) {
        auth_request(conn);
        return;
    }
    /* The MAC is optional so an older caller keeps working, but MikuN2N always
     * sends it: it reads the peer's MAC straight out of get_edges, which is the
     * one identifier every peer entry is guaranteed to have. */
    if(!params ||
       (sscanf(params, " [ \"%63[0-9.]\" , %7[a-z] , \"%31[0-9a-fA-F:]\" ]",
               address, enabled, mac_text) != 3 &&
        sscanf(params, " [ \"%63[0-9.]\" , %7[a-z] ]", address, enabled) != 2) ||
       (strcmp(enabled, "true") && strcmp(enabled, "false"))) {
        jsonrpc_error(id, conn, 400, "expected [IPv4, boolean, MAC?]", 0);
        return;
    }

    network_address = inet_addr(address);
    if(network_address == INADDR_NONE) {
        jsonrpc_error(id, conn, 400, "invalid IPv4 address", 0);
        return;
    }
    target = ntohl(network_address);
    force_relay = !strcmp(enabled, "true");
    /* str2mac reads a fixed 17-char layout without validating it, so check the
     * length here rather than letting it walk past a short string. */
    if(strlen(mac_text) == 17 && str2mac(mac, mac_text) == 0 && !is_null_mac(mac))
        have_mac = 1;
    if(!force_relay && have_mac)
        mikun2n_reset_punch_history(eee, mac);

    for(policy_index = 0; policy_index < eee->mikun2n_forced_relay_count; policy_index++) {
        if(eee->mikun2n_forced_relay_ips[policy_index] == target)
            break;
        if(have_mac && !memcmp(eee->mikun2n_forced_relay_macs[policy_index], mac, sizeof(n2n_mac_t)))
            break;
    }
    if(force_relay && policy_index == eee->mikun2n_forced_relay_count) {
        if(eee->mikun2n_forced_relay_count >= MIKUN2N_FORCED_RELAY_MAX) {
            jsonrpc_error(id, conn, 507, "forced relay list is full", 0);
            return;
        }
        eee->mikun2n_forced_relay_ips[eee->mikun2n_forced_relay_count] = target;
        memcpy(eee->mikun2n_forced_relay_macs[eee->mikun2n_forced_relay_count],
               mac, sizeof(n2n_mac_t));
        eee->mikun2n_forced_relay_count++;
    } else if(force_relay) {
        /* Refresh both keys: the supernode hands out a new virtual IPv4 on
         * reconnect, and a peer that restarts comes back on a different MAC. */
        eee->mikun2n_forced_relay_ips[policy_index] = target;
        if(have_mac)
            memcpy(eee->mikun2n_forced_relay_macs[policy_index], mac, sizeof(n2n_mac_t));
    } else if(policy_index < eee->mikun2n_forced_relay_count) {
        eee->mikun2n_forced_relay_count--;
        eee->mikun2n_forced_relay_ips[policy_index] =
            eee->mikun2n_forced_relay_ips[eee->mikun2n_forced_relay_count];
        memcpy(eee->mikun2n_forced_relay_macs[policy_index],
               eee->mikun2n_forced_relay_macs[eee->mikun2n_forced_relay_count],
               sizeof(n2n_mac_t));
    }

    HASH_ITER(hh, eee->pending_peers, peer, tmp) {
        if(!mikun2n_relay_target_matches(peer, target, mac, have_mac))
            continue;
        peer->force_relay = force_relay;
        if(!force_relay) {
            mikun2n_reset_punch_history(eee, peer->mac_addr);
            mikun2n_reset_peer_punch(peer);
        }
        changed++;
    }
    HASH_ITER(hh, eee->known_peers, peer, tmp) {
        if(!mikun2n_relay_target_matches(peer, target, mac, have_mac))
            continue;
        peer->force_relay = force_relay;
        if(!force_relay) {
            mikun2n_reset_punch_history(eee, peer->mac_addr);
            mikun2n_reset_peer_punch(peer);
        }
        changed++;
    }
    /* Report the real match count. The policy list itself is always updated, so
     * zero only means no peer entry carries either key yet - the caller needs to
     * see that to tell a recorded policy from an effective one. */
    traceEvent(TRACE_NORMAL, "MikuN2N peer %s%s%s forced relay=%s (%u peer entries updated)",
               address, have_mac ? "/" : "", have_mac ? mac_text : "",
               force_relay ? "on" : "off", changed);
    jsonrpc_1uint(id, conn, changed);
}

static bool jsonrpc_error_overflow (char *id, conn_t *conn, int count) {
    if(!sb_overflowed(conn->request)) {
        // Nothing to do
        return false;
    }

    jsonrpc_error(id, conn, 507, "overflow", count);
    jsonrpc_result_tail(conn, 507);
    return true;
}

static void jsonrpc_listend_hack (conn_t *conn, const char *endch) {
    // HACK: back up over the final ','
    if(conn->request->str[conn->request->wr_pos-1] == ',') {
        conn->request->wr_pos--;
    }
    // and replace with the relevant list ending char
    sb_reprintf(&conn->request, "%s", endch);
}

static void jsonrpc_get_mac (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    // Want to eventually output all the known MAC addresses and the routing
    // destination for each one.  Kind of a superset of the get_edges output
    // but intended to be both more complete and more focused

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    struct sn_community *community;
    struct sn_community *tmp_community;
    struct node_supernode_association *assoc;
    struct node_supernode_association *tmp_assoc;

    int limit;      // max number of items to add to this packet
    int offset = 0; // Number of items to skip before adding
    extract_pagination((char *)params, &limit, &offset);
    int count = 0;  // Number of items in this reply packet
    int index = 0;  // Track the current item number

    HASH_ITER(hh, eee->communities, community, tmp_community) {
        HASH_ITER(hh, community->assoc, assoc, tmp_assoc) {
            if(index < offset) {
                index++;
                continue;
            }
            index++;


            char buf[1000];
            char port[10];
            getnameinfo(
                &assoc->sock,
                assoc->sock_len,
                buf,
                sizeof(buf),
                port,
                sizeof(port),
                NI_NUMERICHOST | NI_NUMERICSERV
            );

            macstr_t mac_buf;
            sb_reprintf(&conn->request,
                        "{"
                        "\"_type\":\"assoc\","  // Federated Supernode
                        "\"mac\":\"%s\","
                        "\"community\":\"%s\","
                        "\"dest\":\"%s:%s\","
                        "\"last_seen\":%u},",
                        macaddr_str(mac_buf, assoc->mac),
                        (community->is_federation) ? "-/-" : community->community,
                        buf,
                        port,
                        (uint32_t)assoc->last_seen
            );

            if(jsonrpc_error_overflow(id, conn, count)) {
                return;
            }
            count++;
            if(count >= limit) {
                break;
            }
        }
    }

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_communities (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    if(!eee->communities) {
        // This is an edge
        if(eee->conf.header_encryption != HEADER_ENCRYPTION_NONE) {
            jsonrpc_error(id, conn, 403, "Forbidden", 0);
            return;
        }

        jsonrpc_result_head(id, conn);
        sb_reprintf(
            &conn->request,
            "[{\"community\":\"%s\"}]",
            eee->conf.community_name
        );
        jsonrpc_result_tail(conn, 200);
        return;
    }

    // Otherwise send the supernode's view
    struct sn_community *community, *tmp;
    dec_ip_bit_str_t ip_bit_str = {'\0'};

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    int limit;      // max number of items to add to this packet
    int offset = 0; // Number of items to skip before adding
    extract_pagination((char *)params, &limit, &offset);
    int count = 0;  // Number of items in this reply packet
    int index = 0;  // Track the current item number

    HASH_ITER(hh, eee->communities, community, tmp) {
        if(index < offset) {
            index++;
            continue;
        }
        index++;

        sb_reprintf(&conn->request,
                    "{"
                    "\"community\":\"%s\","
                    "\"purgeable\":%i,"
                    "\"is_federation\":%i,"
                    "\"ip4addr\":\"%s\"},",
                    (community->is_federation) ? "-/-" : community->community,
                    community->purgeable,
                    community->is_federation,
                    (community->auto_ip_net.net_addr == 0) ? "" : ip_subnet_to_str(ip_bit_str, &community->auto_ip_net));

        if(jsonrpc_error_overflow(id, conn, count)) {
            return;
        }
        count++;
        if(count >= limit) {
            break;
        }
    }

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static int mikun2n_peer_forced_relay (const struct n3n_runtime_data *eee,
                                      const struct peer_info *peer) {
    uint8_t i;

    for(i = 0; i < eee->mikun2n_forced_relay_count; i++) {
        if(peer->dev_addr.net_addr != 0 &&
           eee->mikun2n_forced_relay_ips[i] == peer->dev_addr.net_addr)
            return 1;
        if(!is_null_mac(eee->mikun2n_forced_relay_macs[i]) &&
           !memcmp(peer->mac_addr, eee->mikun2n_forced_relay_macs[i], sizeof(n2n_mac_t)))
            return 1;
    }
    return 0;
}

static void jsonrpc_get_edges_row (strbuf_t **reply, struct peer_info *peer, const char *mode, const char *community, int ipv6) {
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;
    n2n_sock_str_t sockbuf2;
    n2n_sock_str_t ipv6_candidate, ipv6_path;
    dec_ip_bit_str_t ip_bit_str = {'\0'};
    const char *punch_state = "native";
    const char *punch_role = "none";
    uint32_t punch_elapsed = 0;

    if(ipv6)
        mode = "p2p";

    if(peer->force_relay) {
        punch_state = "forced_relay";
    } else if(!strcmp(mode, "p2p")) {
        punch_state = "direct";
    } else if(peer->punch_abandoned) {
        punch_state = "failed";
    } else if(peer->punch_exhausted) {
        punch_state = "relay";
    } else if(peer->punch_started) {
        punch_state = "punching";
        punch_elapsed = (uint32_t)max(0, (int)(time(NULL) - peer->punch_started));
    }
    switch(peer->punch_role) {
        case MIKUN2N_PUNCH_ROLE_ANCHOR: punch_role = "anchor"; break;
        case MIKUN2N_PUNCH_ROLE_SCANNER: punch_role = "scanner"; break;
        case MIKUN2N_PUNCH_ROLE_LAYERED: punch_role = "layered"; break;
    }

    sb_reprintf(reply,
                "{"
                "\"mode\":\"%s\","
                "\"transport\":\"%s\","
                "\"ipv6_rtt_ms\":%u,"
                "\"ipv6_peer_rx_udp_bytes\":%u,"
                "\"ipv6_checked_udp_bytes\":%u,"
                "\"ipv6_wire_version\":%u,"
                "\"peer_ipv6_wire_version\":%u,"
                "\"ipv4_scan_paused\":%s,"
                "\"ipv6_candidate\":\"%s\","
                "\"ipv6_path\":\"%s\","
                "\"ipv6_path_kind\":\"%s\","
                "\"community\":\"%s\","
                "\"ip4addr\":\"%s\","
                "\"purgeable\":%i,"
                "\"local\":%i,"
                "\"macaddr\":\"%s\","
                "\"sockaddr\":\"%s\","
                "\"prefered_sockaddr\":\"%s\","
                "\"desc\":\"%.20s\","
                "\"version\":\"%.20s\","
                "\"timeout\":%i,"
                "\"uptime\":%u,"
                "\"time_alloc\":%u,"
                "\"last_p2p\":%u,"
                "\"last_sent_query\":%u,"
                "\"last_seen\":%u,"
                "\"punch_state\":\"%s\","
                "\"punch_elapsed\":%u,"
                "\"punch_attempt\":%u,"
                "\"punch_packets\":%u,"
                "\"punch_role\":\"%s\","
                "\"punch_band_lo\":%u,"
                "\"punch_band_hi\":%u,"
                "\"force_relay\":%s},",
                mode,
                ipv6 ? "ipv6" : "ipv4",
                ipv6 ? peer->mikun2n_ipv6_rtt_ms : 0,
                peer->mikun2n_ipv6_peer_rx_limit,
                ipv6 ? peer->mikun2n_ipv6_path_bytes : 0,
                MIKUN2N_IPV6_WIRE_VERSION,
                peer->mikun2n_ipv6_wire_version,
                peer->punch_ipv6_paused_ms ? "true" : "false",
                sock_to_cstr(ipv6_candidate, &peer->mikun2n_ipv6_address),
                ipv6 ? sock_to_cstr(ipv6_path, &peer->mikun2n_ipv6_path_address) : "",
                !ipv6 ? "inactive" : sock_equal(&peer->mikun2n_ipv6_path_address, &peer->mikun2n_ipv6_address)
                                         ? "advertised" : "peer_reflexive",
                community,
                (peer->dev_addr.net_addr == 0) ? "" : ip_subnet_to_str(ip_bit_str, &peer->dev_addr),
                peer->purgeable,
                peer->local,
                (is_null_mac(peer->mac_addr)) ? "" : macaddr_str(mac_buf, peer->mac_addr),
                sock_to_cstr(sockbuf, &(peer->sock)),
                sock_to_cstr(sockbuf2, &(peer->preferred_sock)),
                peer->dev_desc,
                peer->version,
                peer->timeout,
                (uint32_t)peer->uptime,
                (uint32_t)peer->time_alloc,
                (uint32_t)peer->last_p2p,
                (uint32_t)peer->last_sent_query,
                (uint32_t)peer->last_seen,
                punch_state,
                punch_elapsed,
                peer->punch_attempt,
                peer->punch_packets,
                punch_role,
                peer->punch_band_lo,
                peer->punch_band_hi,
                peer->force_relay ? "true" : "false"
    );

    // TODO: add a proto: TCP|UDP item to the output
}

static void jsonrpc_get_edges (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    struct peer_info *peer, *tmpPeer;

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    int limit;      // max number of items to add to this packet
    int offset = 0; // Number of items to skip before adding
    extract_pagination((char *)params, &limit, &offset);
    int count = 0;  // Number of items in this reply packet
    int index = 0;  // Track the current item number

    // dump nodes with forwarding through supernodes
    HASH_ITER(hh, eee->pending_peers, peer, tmpPeer) {
        peer->force_relay = mikun2n_peer_forced_relay(eee, peer);
        mikun2n_restore_peer_punch_history(eee, peer);
        if(index < offset) {
            index++;
            continue;
        }
        index++;

        jsonrpc_get_edges_row(
            &conn->request,
            peer,
            "pSp",
            eee->conf.community_name,
            !peer->local && !peer->force_relay && mikun2n_ipv6_active(eee, peer, mikun2n_ipv6_now_ms())
        );

        if(jsonrpc_error_overflow(id, conn, count)) {
            return;
        }
        count++;
        if(count >= limit) {
            break;
        }
    }

    // dump peer-to-peer nodes
    HASH_ITER(hh, eee->known_peers, peer, tmpPeer) {
        peer->force_relay = mikun2n_peer_forced_relay(eee, peer);
        mikun2n_restore_peer_punch_history(eee, peer);
        if(index < offset) {
            index++;
            continue;
        }
        index++;

        jsonrpc_get_edges_row(
            &conn->request,
            peer,
            peer->force_relay ? "pSp" : "p2p",
            eee->conf.community_name,
            !peer->local && !peer->force_relay && mikun2n_ipv6_active(eee, peer, mikun2n_ipv6_now_ms())
        );

        if(jsonrpc_error_overflow(id, conn, count)) {
            return;
        }
        count++;
        if(count >= limit) {
            break;
        }
    }

    struct sn_community *community, *tmp;
    HASH_ITER(hh, eee->communities, community, tmp) {
        HASH_ITER(hh, community->edges, peer, tmpPeer) {
            if(index < offset) {
                index++;
                continue;
            }
            index++;

            jsonrpc_get_edges_row(
                &conn->request,
                peer,
                "sn",
                (community->is_federation) ? "-/-" : community->community,
                0
            );

            if(jsonrpc_error_overflow(id, conn, count)) {
                return;
            }
            count++;
            if(count >= limit) {
                break;
            }
        }
    }


    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_nat (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    mikun2n_nat_state_t *nat = &eee->mikun2n_nat;
    const char *strategy = "native-first";

    if(nat->complete && !strcmp(nat->type, "NAT4"))
        strategy = "tier1-cone-escape";
    else if(nat->complete)
        strategy = "tier1-layered-scan";

    jsonrpc_result_head(id, conn);
    sb_reprintf(
        &conn->request,
        "[{"
        "\"type\":\"%s\","
        "\"mapping\":\"%s\","
        "\"filtering\":\"%s\","
        "\"public_ip\":\"%s\","
        "\"public_port\":%u,"
        "\"observed_port_a\":%u,"
        "\"observed_port_b\":%u,"
        "\"available\":%s,"
        "\"complete\":%s,"
        "\"probe_mask\":%u,"
        "\"probe_round\":%u,"
        "\"cross_probe_attempts\":%u,"
        "\"ipv6_enabled\":%s,"
        "\"ipv6_nat66_supported\":true,"
        "\"strategy\":\"%s\"}]",
        nat->type[0] ? nat->type : "detecting",
        nat->mapping[0] ? nat->mapping : "unknown",
        nat->filtering[0] ? nat->filtering : "unknown",
        nat->public_ip,
        nat->public_port,
        nat->observed_port_a,
        nat->observed_port_b,
        nat->unavailable ? "false" : "true",
        nat->complete ? "true" : "false",
        nat->probe_mask,
        nat->probe_round,
        nat->cross_probe_attempts,
        eee->conf.mikun2n_ipv6 ? "true" : "false",
        strategy
    );
    jsonrpc_result_tail(conn, 200);
    (void)params;
}

static void jsonrpc_get_info (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;

    struct in_addr ip_addr;
    ipstr_t ip_address;

    ip_addr.s_addr = eee->device.ip_addr;
    inaddrtoa(ip_address, ip_addr);

    jsonrpc_result_head(id, conn);

    sb_reprintf(&conn->request,
                "{"
                "\"version\":\"%s\","
                "\"builddate\":\"%s\","
                "\"mikun2n_build_version\":\"%s\","
                "\"ipv6_wire_version\":%u,"
                "\"is_edge\":%i,"
                "\"is_supernode\":%i,"
                "\"macaddr\":\"%s\","
                "\"ip4addr\":\"%s\","
                "\"sockaddr\":\"%s\"}",
                VERSION,
                BUILDDATE,
                MIKUN2N_BUILD_VERSION,
                MIKUN2N_IPV6_WIRE_VERSION,
                eee->conf.is_edge,
                eee->conf.is_supernode,
                is_null_mac(eee->device.mac_addr) ? "" : macaddr_str(mac_buf, eee->device.mac_addr),
                ip_address,
                sock_to_cstr(sockbuf, &eee->conf.preferred_sock)
    );

    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_supernodes (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    struct peer_info *peer, *tmpPeer;
    macstr_t mac_buf;
    n2n_sock_str_t sockbuf;
    selection_criterion_str_t sel_buf;

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    HASH_ITER(hh, eee->conf.supernodes, peer, tmpPeer) {

        /*
         * TODO:
         * The version string provided by the remote supernode could contain
         * chars that make our JSON invalid.
         * - do we care?
         */

        sb_reprintf(&conn->request,
                    "{"
                    "\"version\":\"%s\","
                    "\"purgeable\":%i,"
                    "\"current\":%i,"
                    "\"macaddr\":\"%s\","
                    "\"sockaddr\":\"%s\","
                    "\"selection\":\"%s\","
                    "\"last_seen\":%u,"
                    "\"uptime\":%u},",
                    peer->version,
                    peer->purgeable,
                    (peer == eee->curr_sn) ? (eee->sn_wait ? 2 : 1 ) : 0,
                    is_null_mac(peer->mac_addr) ? "" : macaddr_str(mac_buf, peer->mac_addr),
                    sock_to_cstr(sockbuf, &(peer->sock)),
                    sn_selection_criterion_str(eee, sel_buf, peer),
                    (uint32_t)peer->last_seen,
                    (uint32_t)peer->uptime);
    }

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_timestamps (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request,
                "{"
                "\"last_register_req\":%u,"
                "\"last_rx_p2p\":%u,"
                "\"last_rx_super\":%u,"
                "\"last_sweep\":%u,"
                "\"last_sn_fwd\":%u,"
                "\"last_sn_reg\":%u,"
                "\"start_time\":%u}",
                (uint32_t)eee->last_register_req,
                (uint32_t)eee->last_p2p,
                (uint32_t)eee->last_sup,
                (uint32_t)eee->last_sweep,
                (uint32_t)eee->last_sn_fwd,
                (uint32_t)eee->last_sn_reg,
                (uint32_t)eee->start_time
    );

    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_packetstats (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"transop\","
                "\"tx_pkt\":%u,"
                "\"rx_pkt\":%u},",
                (uint32_t)eee->transop.tx_cnt,
                (uint32_t)eee->transop.rx_cnt);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"p2p\","
                "\"tx_pkt\":%u,"
                "\"rx_pkt\":%u},",
                eee->stats.tx_p2p,
                eee->stats.rx_p2p);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"super\","
                "\"tx_pkt\":%u,"
                "\"rx_pkt\":%u},",
                eee->stats.tx_sup,
                eee->stats.rx_sup);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"super_broadcast\","
                "\"tx_pkt\":%u,"
                "\"rx_pkt\":%u},",
                eee->stats.tx_sup_broadcast,
                eee->stats.rx_sup_broadcast);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"tuntap_error\","
                "\"tx_pkt\":%u},",
                eee->stats.tx_tuntap_error);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"multicast_drop\","
                "\"tx_pkt\":%u,"
                "\"rx_pkt\":%u},",
                eee->stats.tx_multicast_drop,
                eee->stats.rx_multicast_drop);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"sn_fwd\","
                "\"tx_pkt\":%u},",
                eee->stats.sn_fwd);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"sn_broadcast\","
                "\"tx_pkt\":%u},",
                eee->stats.sn_broadcast);

    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"sn_reg\","
                "\"tx_pkt\":%u,"
                "\"nak\":%u},",
                eee->stats.sn_reg,
                eee->stats.sn_reg_nak);

    /* Note: sn_reg_nak is not currently incremented anywhere */

    /* Generic errors when trying to sendto() */
    sb_reprintf(&conn->request,
                "{"
                "\"type\":\"sn_errors\","
                "\"tx_pkt\":%u},",
                eee->stats.sn_errors);

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

#if 0
static void jsonrpc_todo (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    jsonrpc_error(id, conn, 501, "TODO");
}
#endif

static void jsonrpc_post_test (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {

    mgmt_event_post(N3N_EVENT_TEST, -1, params);

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "\"sent\"\n");
    jsonrpc_result_tail(conn, 200);
}


static void jsonrpc_reload_communities (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    if(!auth_check(eee, conn)) {
        auth_request(conn);
        return;
    }

    int ok = load_allowed_sn_community(eee);

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "%i", ok);
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_help_events (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    int nr_handlers = sizeof(mgmt_events) / sizeof(mgmt_events[0]);

    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");
    for( int topic=0; topic < nr_handlers; topic++ ) {
        int sub = mgmt_event_subscribers[topic];
        char host[40];
        char serv[6];
        host[0] = '?';
        host[1] = 0;
        serv[0] = '?';
        serv[1] = 0;

        if(sub != -1) {
            struct sockaddr_storage sa;
            socklen_t sa_size = sizeof(sa);

            if(getpeername(sub, (struct sockaddr *)&sa, &sa_size) == 0) {
                getnameinfo(
                    (struct sockaddr *)&sa, sa_size,
                    host, sizeof(host),
                    serv, sizeof(serv),
                    NI_NUMERICHOST|NI_NUMERICSERV
                );
            }
        }

        sb_reprintf(
            &conn->request,
            "{"
            "\"topic\":\"%s\","
            "\"sockaddr\":\"%s:%s\","
            "\"desc\":\"%s\"},",
            mgmt_events[topic].topic,
            host, serv,
            mgmt_events[topic].desc
        );
    }

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_get_relay_stats (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    struct mikun2n_relay_flow *flow, *tmp;
    int limit = 16, offset = 0, index = 0, count = 0;
    extract_pagination((char *)params, &limit, &offset);
    if(limit < 1 || limit > 64) limit = 16;
    if(offset < 0) offset = 0;
    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request,
        "{\"schema\":1,\"started_at\":%llu,\"sampled_at\":%llu,"
        "\"out_bytes\":%llu,\"out_sends\":%llu,\"send_errors\":%llu,"
        "\"overflow_bytes\":%llu,\"overflow_packets\":%llu,"
        "\"flow_count\":%u,\"flow_limit\":%u,\"flows\":[",
        (unsigned long long)eee->start_time, (unsigned long long)time(NULL),
        (unsigned long long)eee->relay_out_bytes, (unsigned long long)eee->relay_out_sends,
        (unsigned long long)eee->relay_send_errors,
        (unsigned long long)eee->relay_overflow_bytes, (unsigned long long)eee->relay_overflow_packets,
        HASH_COUNT(eee->relay_flows), MIKUN2N_RELAY_MAX);
    HASH_ITER(hh, eee->relay_flows, flow, tmp) {
        if(index++ < offset) continue;
        char community[2 * N2N_COMMUNITY_SIZE + 1];
        macstr_t src, dst;
        for(int i = 0; i < N2N_COMMUNITY_SIZE; ++i)
            snprintf(community + 2 * i, 3, "%02x", flow->key[i]);
        sb_reprintf(&conn->request,
            "{\"community_hex\":\"%s\",\"src\":\"%s\",\"dst\":\"%s\","
            "\"kind\":%u,\"bytes\":%llu,\"packets\":%llu,\"last_seen\":%llu},",
            community, macaddr_str(src, flow->key + N2N_COMMUNITY_SIZE),
            macaddr_str(dst, flow->key + N2N_COMMUNITY_SIZE + N2N_MAC_SIZE),
            flow->key[MIKUN2N_RELAY_KEY_SIZE - 1],
            (unsigned long long)flow->bytes, (unsigned long long)flow->packets,
            (unsigned long long)flow->last_seen);
        if(jsonrpc_error_overflow(id, conn, count)) return;
        if(++count >= limit) break;
    }
    jsonrpc_listend_hack(conn, "]}");
    jsonrpc_result_tail(conn, 200);
}

static void jsonrpc_help (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params);

struct mgmt_jsonrpc_method {
    char *method;
    void (*func)(char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params);
    char *desc;
};

static const struct mgmt_jsonrpc_method jsonrpc_methods[] = {
    { "get_communities", jsonrpc_get_communities, "Show current communities" },
    { "get_edges", jsonrpc_get_edges, "List current edges/peers" },
    { "get_info", jsonrpc_get_info, "Provide basic edge information" },
    { "get_nat", jsonrpc_get_nat, "Show MikuN2N NAT behavior probe" },
    { "get_mac", jsonrpc_get_mac, "Show known mac addresses" },
    { "get_packetstats", jsonrpc_get_packetstats, "traffic counters" },
    { "get_relay_stats", jsonrpc_get_relay_stats, "Supernode cumulative outbound relay bytes by directed MAC pair" },
    { "get_supernodes", jsonrpc_get_supernodes, "List current supernodes" },
    { "get_timestamps", jsonrpc_get_timestamps, "Event timestamps" },
    { "get_verbose", jsonrpc_get_verbose, "Logging verbosity" },
    { "help", jsonrpc_help, "Show JsonRPC methods" },
    { "help.events", jsonrpc_help_events, "Show available event topics" },
    { "post.test", jsonrpc_post_test, "Send a test event" },
    { "reload_communities", jsonrpc_reload_communities, "Reloads communities and user's public keys" },
    { "set_peer_relay", jsonrpc_set_peer_relay, "Force or release supernode relay for one peer" },
    { "set_verbose", jsonrpc_set_verbose, "Set logging verbosity" },
    { "stop", jsonrpc_stop, "Stop the daemon" },
    // get_last_event?
};

static void jsonrpc_help (char *id, struct n3n_runtime_data *eee, conn_t *conn, const char *params) {
    jsonrpc_result_head(id, conn);
    sb_reprintf(&conn->request, "[");

    int i;
    int nr_handlers = sizeof(jsonrpc_methods) / sizeof(jsonrpc_methods[0]);
    for( i=0; i < nr_handlers; i++ ) {
        sb_reprintf(&conn->request,
                    "{"
                    "\"method\":\"%s\","
                    "\"desc\":\"%s\"},",
                    jsonrpc_methods[i].method,
                    jsonrpc_methods[i].desc
        );

    }

    jsonrpc_listend_hack(conn, "]");
    jsonrpc_result_tail(conn, 200);
}

static void handle_jsonrpc (struct n3n_runtime_data *eee, conn_t *conn) {
    char *body = strstr(conn->request->str, "\r\n\r\n");
    if(!body) {
        render_error(conn, "Error: no body");
        return;
    }
    body += 4;

    jsonrpc_t json;

    if(jsonrpc_parse(body, &json) != 0) {
        render_error(conn, "Error: parsing json");
        return;
    }

    traceEvent(
        TRACE_DEBUG,
        "jsonrpc id=%s, method=%s, params=%s",
        json.id,
        json.method,
        json.params
    );

    // Since we are going to reuse the request buffer for the reply, copy
    // the id string out of it as every single reply will need it
    char idbuf[10];
    strncpy(idbuf, json.id, sizeof(idbuf)-1);

    int i;
    int nr_handlers = sizeof(jsonrpc_methods) / sizeof(jsonrpc_methods[0]);
    for( i=0; i < nr_handlers; i++ ) {
        if(!strcmp(
               jsonrpc_methods[i].method,
               json.method
           )) {
            break;
        }
    }
    if( i >= nr_handlers ) {
        render_error(conn, "Unknown method");
        return;
    } else {
        jsonrpc_methods[i].func(idbuf, eee, conn, json.params);
    }
    return;
}

static void render_todo_page (struct n3n_runtime_data *eee, conn_t *conn) {
    sb_zero(conn->request);
    sb_printf(conn->request, "TODO\n");

    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;
    generate_http_headers(conn, "text/plain", 501);
}

static void render_metrics_page (struct n3n_runtime_data *eee, conn_t *conn) {
    n3n_metrics_render(&conn->request);

    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;
    generate_http_headers(conn, "text/plain", 501);
}

#include "management_index.html.h"

// Generate the output for the human user interface
static void render_index_page (struct n3n_runtime_data *eee, conn_t *conn) {
    // TODO:
    // - could allow overriding of built in text with an external file
    conn->reply = &management_index;
    generate_http_headers(conn, "text/html", 200);
}

#include "management_script.js.h"

// Generate the output for the small set of javascript functions
static void render_script_page (struct n3n_runtime_data *eee, conn_t *conn) {
    conn->reply = &management_script;
    generate_http_headers(conn, "text/javascript", 200);
}

static void render_debug_slots (struct n3n_runtime_data *eee, conn_t *conn) {
    int status;
    sb_zero(conn->request);
    if(eee->conf.enable_debug_pages) {
        slots_dump(&conn->request, eee->mgmt_slots);
        status = 200;
    } else {
        sb_printf(conn->request, "enable_debug_pages is false\n");
        status = 403;
    }
    // Update the reply buffer after last potential realloc
    conn->reply = conn->request;
    generate_http_headers(conn, "text/plain", status);
}

static void render_help_page (struct n3n_runtime_data *eee, conn_t *conn);

struct mgmt_api_endpoint {
    char *match;    // when the request buffer starts with this
    void (*func)(struct n3n_runtime_data *eee, conn_t *conn);
    char *desc;
};

static const struct mgmt_api_endpoint api_endpoints[] = {
    { "POST /v1 ", handle_jsonrpc, "JsonRPC" },
    { "GET / ", render_index_page, "Human interface" },
    { "GET /debug/slots ", render_debug_slots, "Internal slots dump" },
    { "GET /events/", event_subscribe, "Subscribe to events" },
    { "GET /help ", render_help_page, "Describe available endpoints" },
    { "GET /metrics ", render_metrics_page, "Fetch metrics data" },
    { "GET /script.js ", render_script_page, "javascript helpers" },
    { "GET /status ", render_todo_page, "Quick health check" },
};

static void render_help_page (struct n3n_runtime_data *eee, conn_t *conn) {
    // Reuse the request buffer
    sb_zero(conn->request);
    sb_reprintf(&conn->request, "endpoint, desc\n");

    int i;
    int nr_handlers = sizeof(api_endpoints) / sizeof(api_endpoints[0]);
    for( i=0; i < nr_handlers; i++ ) {
        sb_reprintf(
            &conn->request,
            "%s, %s\n",
            api_endpoints[i].match,
            api_endpoints[i].desc
        );
    }

    // Update the reply buffer only after last potential realloc
    conn->reply = conn->request;

    generate_http_headers(conn, "text/plain", 200);
}

void mgmt_api_handler (struct n3n_runtime_data *eee, conn_t *conn) {
    int i;
    int nr_handlers = sizeof(api_endpoints) / sizeof(api_endpoints[0]);
    for( i=0; i < nr_handlers; i++ ) {
        if(!strncmp(
               api_endpoints[i].match,
               conn->request->str,
               strlen(api_endpoints[i].match))) {
            break;
        }
    }
    if( i >= nr_handlers ) {
        render_error(conn, "unknown endpoint");
    } else {
        api_endpoints[i].func(eee, conn);
    }

    // Try to immediately start sending the reply
    conn_write(conn);
}
