/* SPDX-License-Identifier: GPL-3.0-only */
#ifdef _WIN32
#include "win32/defs.h"
#endif
#include "mikun2n_assist.h"
#include "peer_info.h"
#include <n2n.h>
#include <n3n/ethernet.h>
#include <n3n/logging.h>
#include <n3n/random.h>
#include <stdio.h>
#include <string.h>

#define ASSIST_HEADER 44
#define ASSIST_DATA 1
#define ASSIST_PING 2
#define ASSIST_PONG 3
#define ASSIST_LEASE_MS 30000
#define ASSIST_READY_MS 6000
#define ASSIST_PROBE_MS 1000
#define ASSIST_LOG_MS 10000
#define ASSIST_U64(value) ((unsigned long long)(value))

static const uint8_t assist_magic[4] = {'M', 'A', 'S', MIKUN2N_ASSIST_VERSION};

/* The Windows TAP reader sends concurrently with management and UDP receive.
 * Circuit removal must not change identities halfway through encapsulation. */
static void assist_lock (struct n3n_runtime_data *eee) {
#ifdef _WIN32
    AcquireSRWLockExclusive(&eee->mikun2n_assist_lock);
#endif
}

static void assist_unlock (struct n3n_runtime_data *eee) {
#ifdef _WIN32
    ReleaseSRWLockExclusive(&eee->mikun2n_assist_lock);
#endif
}

static uint64_t assist_read64 (const uint8_t *p) {
    uint64_t value = 0;
    for(int i = 0; i < 8; i++) value = (value << 8) | p[i];
    return value;
}

static void assist_write64 (uint8_t *p, uint64_t value) {
    for(int i = 7; i >= 0; i--) { p[i] = value & 0xff; value >>= 8; }
}

static void assist_header (uint8_t *data, const mikun2n_assist_t *assist,
                           const n2n_mac_t src, const n2n_mac_t dst,
                           uint8_t kind, uint64_t sequence) {
    memset(data, 0, ASSIST_HEADER);
    memcpy(data, assist_magic, 4);
    data[4] = kind;
    memcpy(data + 8, assist->circuit, 16);
    memcpy(data + 24, src, N2N_MAC_SIZE);
    memcpy(data + 30, dst, N2N_MAC_SIZE);
    assist_write64(data + 36, sequence);
}

const char *mikun2n_assist_rejection_name (unsigned reason) {
    static const char *names[MIKUN2N_ASSIST_DROP_COUNT] = {
        "header", "direction", "hop", "source_endpoint", "not_ready", "forced_relay",
        "duplicate", "window", "invalid_sequence", "inner_header", "common", "type",
        "community", "packet", "mac", "timestamp", "unregistered", "transform",
        "payload", "filter", "tap"
    };
    return reason < MIKUN2N_ASSIST_DROP_COUNT ? names[reason] : "unknown";
}

static void assist_changed (mikun2n_assist_t *assist, uint64_t now_ms) {
    assist->diagnostic_revision++;
    assist->last_changed_ms = now_ms;
}

static int assist_direction (const mikun2n_assist_t *assist, const uint8_t *src, const uint8_t *dst) {
    for(unsigned i = 0; i < 2; i++)
        if(!memcmp(src, assist->directions[i].source_mac, N2N_MAC_SIZE) &&
           !memcmp(dst, assist->directions[i].destination_mac, N2N_MAC_SIZE)) return i;
    return -1;
}

static void assist_reject (mikun2n_assist_t *assist, int direction, uint8_t kind,
                           unsigned reason, uint64_t now_ms) {
    if(direction < 0) {
        if(kind == ASSIST_DATA) assist->unassigned_rejected[reason]++;
        else assist->unassigned_control_rejected[reason]++;
    }
    else if(kind == ASSIST_DATA) assist->directions[direction].rejected[reason]++;
    else assist->directions[direction].heartbeat_rejected++;
    assist_changed(assist, now_ms);
}

static void assist_tx_result (mikun2n_assist_t *assist, unsigned direction,
                              uint8_t kind, size_t bytes, int result, uint64_t now_ms) {
    mikun2n_assist_direction_t *diag = &assist->directions[direction];
    if(kind == ASSIST_DATA) {
        if(result == 1) { diag->tx_submitted++; diag->tx_submitted_bytes += bytes; }
        else if(result) { diag->tx_consumed_drop++; diag->tx_consumed_drop_bytes += bytes; }
        else { diag->tx_failed++; diag->tx_failed_bytes += bytes; }
        if(result) assist->tx_packets++;
    } else {
        unsigned control = kind - ASSIST_PING;
        diag->heartbeat_tx[control]++;
        if(result == 1) diag->heartbeat_tx_submitted[control]++;
        else if(result) diag->heartbeat_tx_consumed_drop[control]++;
        else diag->heartbeat_tx_failed[control]++;
    }
    assist_changed(assist, now_ms);
}

static void assist_log_rejections (const char *prefix, const uint64_t *rejected, const char *group) {
    /* traceEvent has a fixed 1024-byte buffer; bounded groups leave room for
     * its timestamp and source prefix, even when every counter is UINT64_MAX. */
    for(unsigned start = 0; start < MIKUN2N_ASSIST_DROP_COUNT; start += 7) {
        char counters[320];
        size_t used = 0;
        counters[0] = 0;
        for(unsigned j = start; j < start + 7 && j < MIKUN2N_ASSIST_DROP_COUNT; j++)
            used += snprintf(counters + used, sizeof(counters) - used, " %s=%llu",
                             mikun2n_assist_rejection_name(j), ASSIST_U64(rejected[j]));
        traceEvent(TRACE_NORMAL, "%s group=%s%u%s", prefix, group, start / 7, counters);
    }
}

static void assist_log (struct n3n_runtime_data *eee, mikun2n_assist_t *assist,
                        const char *event, uint8_t next_role, uint64_t now_ms) {
    char circuit[33], prefix[360];
    macstr_t local, src, dst;
    for(unsigned j = 0; j < 16; j++) snprintf(circuit + j * 2, 3, "%02x", assist->circuit[j]);
    const char *role = assist->role == MIKUN2N_ASSIST_RELAY ? "relay" :
                       assist->role == MIKUN2N_ASSIST_STANDBY ? "standby" : "endpoint";
    int active = mikun2n_assist_active(eee, assist, now_ms);
    for(unsigned direction = 0; direction < 2; direction++) {
        const mikun2n_assist_direction_t *d = &assist->directions[direction];
        snprintf(prefix, sizeof(prefix),
                 "MikuN2N assistdiag event=%s local=%s role=%s next_role=%u circuit=%s sample=%llu mono_ms=%llu created_ms=%llu lease_ms=%llu active=%d src=%s dst=%s",
                 event, macaddr_str(local, eee->device.mac_addr), role, next_role, circuit,
                 ASSIST_U64(assist->diagnostic_revision), ASSIST_U64(now_ms), ASSIST_U64(assist->created_ms),
                 ASSIST_U64(assist->expires_ms > now_ms ? assist->expires_ms - now_ms : 0), active,
                 macaddr_str(src, d->source_mac), macaddr_str(dst, d->destination_mac));
        traceEvent(TRACE_NORMAL,
                   "%s group=tx attempts=%llu bytes=%llu submitted=%llu submitted_bytes=%llu failed=%llu failed_bytes=%llu consumed_drop=%llu consumed_drop_bytes=%llu seq_high=%llu",
                   prefix, ASSIST_U64(d->tx_attempts), ASSIST_U64(d->tx_attempt_bytes),
                   ASSIST_U64(d->tx_submitted), ASSIST_U64(d->tx_submitted_bytes),
                   ASSIST_U64(d->tx_failed), ASSIST_U64(d->tx_failed_bytes),
                   ASSIST_U64(d->tx_consumed_drop), ASSIST_U64(d->tx_consumed_drop_bytes), ASSIST_U64(d->tx_sequence_high));
        traceEvent(TRACE_NORMAL,
                   "%s group=rx packets=%llu bytes=%llu validated=%llu validated_bytes=%llu seq_high=%llu valid_seq_high=%llu inner_accepted=%llu inner_bytes=%llu",
                   prefix, ASSIST_U64(d->rx_packets), ASSIST_U64(d->rx_bytes),
                   ASSIST_U64(d->rx_validated), ASSIST_U64(d->rx_validated_bytes),
                   ASSIST_U64(d->rx_sequence_high), ASSIST_U64(d->rx_valid_sequence_high),
                   ASSIST_U64(d->inner_accepted), ASSIST_U64(d->inner_accepted_bytes));
        traceEvent(TRACE_NORMAL, "%s group=tap attempts=%llu bytes=%llu submitted=%llu submitted_bytes=%llu",
                   prefix, ASSIST_U64(d->tap_attempts), ASSIST_U64(d->tap_attempt_bytes),
                   ASSIST_U64(d->tap_submitted), ASSIST_U64(d->tap_submitted_bytes));
        assist_log_rejections(prefix, d->rejected, "reject");
        for(unsigned control = 0; control < 2; control++)
            traceEvent(TRACE_NORMAL, "%s group=%s rx=%llu validated=%llu tx=%llu submitted=%llu failed=%llu consumed_drop=%llu control_rejected=%llu",
                       prefix, control ? "pong" : "ping", ASSIST_U64(d->heartbeat_rx[control]),
                       ASSIST_U64(d->heartbeat_validated[control]), ASSIST_U64(d->heartbeat_tx[control]),
                       ASSIST_U64(d->heartbeat_tx_submitted[control]), ASSIST_U64(d->heartbeat_tx_failed[control]),
                       ASSIST_U64(d->heartbeat_tx_consumed_drop[control]), ASSIST_U64(d->heartbeat_rejected));
    }
    if(assist->unassigned_data || assist->unassigned_control) {
        snprintf(prefix, sizeof(prefix), "MikuN2N assistdiag event=%s local=%s role=%s circuit=%s sample=%llu mono_ms=%llu created_ms=%llu lease_ms=%llu active=%d direction=unassigned",
                 event, macaddr_str(local, eee->device.mac_addr), role, circuit,
                 ASSIST_U64(assist->diagnostic_revision), ASSIST_U64(now_ms), ASSIST_U64(assist->created_ms),
                 ASSIST_U64(assist->expires_ms > now_ms ? assist->expires_ms - now_ms : 0), active);
        traceEvent(TRACE_NORMAL, "%s group=rx data=%llu bytes=%llu control=%llu", prefix,
                   ASSIST_U64(assist->unassigned_data), ASSIST_U64(assist->unassigned_data_bytes), ASSIST_U64(assist->unassigned_control));
        assist_log_rejections(prefix, assist->unassigned_rejected, "reject");
        assist_log_rejections(prefix, assist->unassigned_control_rejected, "control_reject");
    }
    assist->last_log_ms = now_ms;
    assist->last_logged_revision = assist->diagnostic_revision;
}

static int assist_set (struct n3n_runtime_data *eee, const uint8_t *circuit,
                       uint8_t role, const n2n_mac_t first, const n2n_mac_t second,
                       int enabled, uint64_t now_ms) {
    mikun2n_assist_t *entry = NULL, *free_entry = NULL;
    if(!eee->conf.is_edge || role < MIKUN2N_ASSIST_ENDPOINT || role > MIKUN2N_ASSIST_STANDBY ||
       is_null_mac(first) || is_null_mac(second) || is_multi_broadcast(first) || is_multi_broadcast(second) ||
       !memcmp(first, second, N2N_MAC_SIZE) || !memcmp(first, eee->device.mac_addr, N2N_MAC_SIZE) ||
       !memcmp(second, eee->device.mac_addr, N2N_MAC_SIZE)) return -1;
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *scan = &eee->mikun2n_assists[i];
        if(scan->role && now_ms >= scan->expires_ms) {
            assist_log(eee, scan, "expire", 0, now_ms);
            memset(scan, 0, sizeof(*scan));
        }
        if(!scan->role) { if(!free_entry) free_entry = scan; continue; }
        if(!memcmp(scan->circuit, circuit, 16)) entry = scan;
        else if(enabled && role != MIKUN2N_ASSIST_RELAY && scan->role != MIKUN2N_ASSIST_RELAY &&
                !memcmp(scan->first_mac, first, N2N_MAC_SIZE)) return -1;
    }
    if(!enabled) {
        if(entry) { assist_log(eee, entry, "remove", 0, now_ms); memset(entry, 0, sizeof(*entry)); }
        return 1;
    }
    if(entry && (memcmp(entry->first_mac, first, N2N_MAC_SIZE) || memcmp(entry->second_mac, second, N2N_MAC_SIZE) ||
                  ((entry->role == MIKUN2N_ASSIST_RELAY) != (role == MIKUN2N_ASSIST_RELAY)))) return -1;
    int created = !entry;
    if(!entry) {
        if(!free_entry) return -2;
        entry = free_entry;
        memset(entry, 0, sizeof(*entry));
        memcpy(entry->circuit, circuit, 16);
        memcpy(entry->first_mac, first, N2N_MAC_SIZE);
        memcpy(entry->second_mac, second, N2N_MAC_SIZE);
        entry->created_ms = now_ms;
        const uint8_t *a = role == MIKUN2N_ASSIST_RELAY ? first : eee->device.mac_addr;
        const uint8_t *b = role == MIKUN2N_ASSIST_RELAY ? second : first;
        if(memcmp(a, b, N2N_MAC_SIZE) > 0) { const uint8_t *swap = a; a = b; b = swap; }
        memcpy(entry->directions[0].source_mac, a, N2N_MAC_SIZE);
        memcpy(entry->directions[0].destination_mac, b, N2N_MAC_SIZE);
        memcpy(entry->directions[1].source_mac, b, N2N_MAC_SIZE);
        memcpy(entry->directions[1].destination_mac, a, N2N_MAC_SIZE);
    } else if(entry->role != role) {
        assist_log(eee, entry, "rolechange", role, now_ms);
    }
    int role_changed = entry->role != role;
    entry->role = role;
    entry->expires_ms = now_ms + ASSIST_LEASE_MS;
    if(created || role_changed) assist_changed(entry, now_ms);
    if(created) assist_log(eee, entry, "create", role, now_ms);
    return 1;
}

int mikun2n_assist_active (struct n3n_runtime_data *eee, const mikun2n_assist_t *assist, uint64_t now_ms) {
    if(!assist->role || now_ms >= assist->expires_ms || !eee->conf.allow_p2p) return 0;
    if(assist->role == MIKUN2N_ASSIST_RELAY)
        return mikun2n_assist_direct_ready(eee, assist->first_mac, now_ms) &&
               mikun2n_assist_direct_ready(eee, assist->second_mac, now_ms);
    return now_ms < assist->ready_until_ms && !mikun2n_assist_forced_relay(eee, assist->first_mac) &&
           mikun2n_assist_direct_ready(eee, assist->second_mac, now_ms);
}

static void assist_tick (struct n3n_runtime_data *eee, uint64_t now_ms) {
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(!assist->role) continue;
        if(now_ms >= assist->expires_ms) {
            assist_log(eee, assist, "expire", 0, now_ms);
            memset(assist, 0, sizeof(*assist));
            continue;
        }
        if(now_ms - assist->last_log_ms >= ASSIST_LOG_MS &&
           assist->diagnostic_revision != assist->last_logged_revision) assist_log(eee, assist, "periodic", assist->role, now_ms);
        if(assist->role == MIKUN2N_ASSIST_RELAY || now_ms < assist->next_probe_ms ||
           mikun2n_assist_forced_relay(eee, assist->first_mac)) continue;
        assist->next_probe_ms = now_ms + ASSIST_PROBE_MS;
        uint8_t probe[ASSIST_HEADER];
        unsigned slot = assist->next_challenge++ % 8;
        assist->challenges[slot] = n3n_rand() | 1;
        assist->challenge_sent_ms[slot] = now_ms;
        assist_header(probe, assist, eee->device.mac_addr, assist->first_mac, ASSIST_PING, assist->challenges[slot]);
        int sent = mikun2n_assist_direct_send(eee, assist->second_mac, probe, sizeof(probe), now_ms);
        int direction = assist_direction(assist, eee->device.mac_addr, assist->first_mac);
        assist_tx_result(assist, direction, ASSIST_PING, 0, sent, now_ms);
    }
}

static int assist_send (struct n3n_runtime_data *eee, const n2n_mac_t dest,
                        const uint8_t *data, size_t size, uint64_t now_ms) {
    if(is_multi_broadcast(dest)) return 0;
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(assist->role != MIKUN2N_ASSIST_ENDPOINT || memcmp(assist->first_mac, dest, N2N_MAC_SIZE)) continue;
        int direction = assist_direction(assist, eee->device.mac_addr, dest);
        mikun2n_assist_direction_t *diag = &assist->directions[direction];
        diag->tx_attempts++;
        diag->tx_attempt_bytes += size;
        assist_changed(assist, now_ms);
        if(!size || size > N2N_PKT_BUF_SIZE - ASSIST_HEADER) {
            assist_reject(assist, direction, ASSIST_DATA, MIKUN2N_ASSIST_DROP_HEADER, now_ms);
            assist_tx_result(assist, direction, ASSIST_DATA, size, 0, now_ms);
            return 0;
        }
        if(!mikun2n_assist_active(eee, assist, now_ms)) {
            assist_reject(assist, direction, ASSIST_DATA,
                          mikun2n_assist_forced_relay(eee, dest) ? MIKUN2N_ASSIST_DROP_FORCED_RELAY : MIKUN2N_ASSIST_DROP_NOT_READY, now_ms);
            assist_tx_result(assist, direction, ASSIST_DATA, size, 0, now_ms);
            continue;
        }
        uint8_t packet[N2N_PKT_BUF_SIZE];
        uint64_t sequence = ++assist->tx_sequence;
        if(!sequence) {
            assist_reject(assist, direction, ASSIST_DATA, MIKUN2N_ASSIST_DROP_INVALID_SEQUENCE, now_ms);
            assist_tx_result(assist, direction, ASSIST_DATA, size, 0, now_ms);
            return 0;
        }
        if(sequence > diag->tx_sequence_high) diag->tx_sequence_high = sequence;
        assist_header(packet, assist, eee->device.mac_addr, dest, ASSIST_DATA, sequence);
        memcpy(packet + ASSIST_HEADER, data, size);
        int sent = mikun2n_assist_direct_send(eee, assist->second_mac, packet, size + ASSIST_HEADER, now_ms);
        assist_tx_result(assist, direction, ASSIST_DATA, size, sent, now_ms);
        if(!sent) {
            assist->ready_until_ms = 0;
            return 0;
        }
        return 1;
    }
    return 0;
}

static int assist_sequence (mikun2n_assist_t *assist, unsigned direction, uint64_t sequence) {
    if(!sequence) return MIKUN2N_ASSIST_DROP_INVALID_SEQUENCE;
    uint64_t *highest = &assist->rx_sequence[direction], *window = &assist->rx_window[direction];
    if(sequence > *highest) {
        uint64_t delta = sequence - *highest;
        *window = delta >= 64 ? 1 : (*window << delta) | 1;
        *highest = sequence;
        return -1;
    }
    uint64_t delta = *highest - sequence;
    if(delta >= 64) return MIKUN2N_ASSIST_DROP_WINDOW;
    if(*window & ((uint64_t)1 << delta)) return MIKUN2N_ASSIST_DROP_DUPLICATE;
    *window |= (uint64_t)1 << delta;
    return -1;
}

static int assist_receive (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                           SOCKET socket_fd, uint8_t *data, size_t *size,
                           n2n_mac_t source, mikun2n_assist_rx_context_t *context, uint64_t now_ms) {
    if(*size < 3 || memcmp(data, assist_magic, 3)) return 0;
    /* A short or unknown circuit cannot safely be attributed to a session. */
    if(*size < 24) return -1;
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(!assist->role || now_ms >= assist->expires_ms || memcmp(assist->circuit, data + 8, 16)) continue;
        uint8_t kind = data[4];
        size_t inner_bytes = *size > ASSIST_HEADER ? *size - ASSIST_HEADER : 0;
        int direction = *size >= ASSIST_HEADER ? assist_direction(assist, data + 24, data + 30) : -1;
        mikun2n_assist_direction_t *diag = direction < 0 ? NULL : &assist->directions[direction];
        uint64_t sequence = *size >= ASSIST_HEADER ? assist_read64(data + 36) : 0;
        if(!diag) {
            if(kind == ASSIST_DATA) { assist->unassigned_data++; assist->unassigned_data_bytes += inner_bytes; }
            else assist->unassigned_control++;
        } else if(kind == ASSIST_DATA) {
            diag->rx_packets++;
            diag->rx_bytes += inner_bytes;
            if(sequence > diag->rx_sequence_high) diag->rx_sequence_high = sequence;
        } else if(kind == ASSIST_PING || kind == ASSIST_PONG) diag->heartbeat_rx[kind - ASSIST_PING]++;
        assist_changed(assist, now_ms);
        if(*size < ASSIST_HEADER || *size > N2N_PKT_BUF_SIZE || memcmp(data, assist_magic, 4) || data[6] || data[7]) {
            assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_HEADER, now_ms);
            return -1;
        }
        if(data[5] > 1) { assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_HOP, now_ms); return -1; }
        if(!eee->conf.allow_p2p) { assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_NOT_READY, now_ms); return -1; }
        if((kind != ASSIST_DATA && kind != ASSIST_PING && kind != ASSIST_PONG) ||
           (kind == ASSIST_DATA ? *size == ASSIST_HEADER : *size != ASSIST_HEADER)) {
            assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_HEADER, now_ms);
            return -1;
        }
        if(direction < 0) { assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_DIRECTION, now_ms); return -1; }
        const uint8_t *src = data + 24, *dst = data + 30;
        if(assist->role == MIKUN2N_ASSIST_RELAY) {
            if(data[5]) { assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_HOP, now_ms); return -1; }
            if(!mikun2n_assist_direct_accept(eee, src, sender, socket_fd, now_ms)) {
                assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_SOURCE, now_ms); return -1;
            }
            if(!mikun2n_assist_direct_ready(eee, dst, now_ms)) {
                assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_NOT_READY, now_ms); return -1;
            }
            if(kind == ASSIST_DATA) {
                unsigned sequence_direction = memcmp(src, assist->first_mac, N2N_MAC_SIZE) ? 1 : 0;
                int rejected = assist_sequence(assist, sequence_direction, sequence);
                if(rejected >= 0) { assist_reject(assist, direction, kind, rejected, now_ms); return -1; }
                diag->rx_validated++;
                diag->rx_validated_bytes += inner_bytes;
                if(sequence > diag->rx_valid_sequence_high) diag->rx_valid_sequence_high = sequence;
                diag->tx_attempts++;
                diag->tx_attempt_bytes += inner_bytes;
                if(sequence > diag->tx_sequence_high) diag->tx_sequence_high = sequence;
                assist->rx_packets++;
            } else diag->heartbeat_validated[kind - ASSIST_PING]++;
            data[5] = 1;
            int sent = mikun2n_assist_direct_send(eee, dst, data, *size, now_ms);
            assist_tx_result(assist, direction, kind, inner_bytes, sent, now_ms);
            return -1;
        }
        if(data[5] != 1) { assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_HOP, now_ms); return -1; }
        if(memcmp(src, assist->first_mac, N2N_MAC_SIZE) || memcmp(dst, eee->device.mac_addr, N2N_MAC_SIZE)) {
            assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_DIRECTION, now_ms); return -1;
        }
        if(mikun2n_assist_forced_relay(eee, src)) {
            assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_FORCED_RELAY, now_ms); return -1;
        }
        if(!mikun2n_assist_direct_accept(eee, assist->second_mac, sender, socket_fd, now_ms)) {
            assist_reject(assist, direction, kind, MIKUN2N_ASSIST_DROP_SOURCE, now_ms); return -1;
        }
        struct peer_info *peer;
        HASH_FIND_PEER(eee->known_peers, src, peer);
        if(!peer) HASH_FIND_PEER(eee->pending_peers, src, peer);
        if(peer) peer->last_seen = time(NULL);
        if(kind == ASSIST_PING) {
            diag->heartbeat_validated[0]++;
            uint8_t reply[ASSIST_HEADER];
            assist_header(reply, assist, eee->device.mac_addr, assist->first_mac, ASSIST_PONG, sequence);
            int sent = mikun2n_assist_direct_send(eee, assist->second_mac, reply, sizeof(reply), now_ms);
            assist_tx_result(assist, 1 - direction, ASSIST_PONG, 0, sent, now_ms);
            return -1;
        }
        if(kind == ASSIST_PONG) {
            int matched = 0;
            for(unsigned slot = 0; slot < 8; slot++) {
                if(sequence && sequence == assist->challenges[slot] &&
                   now_ms - assist->challenge_sent_ms[slot] < ASSIST_READY_MS) {
                    uint64_t ready_until = assist->challenge_sent_ms[slot] + ASSIST_READY_MS;
                    if(ready_until > assist->ready_until_ms) assist->ready_until_ms = ready_until;
                    assist->challenges[slot] = 0;
                    matched = 1;
                    break;
                }
            }
            if(matched) diag->heartbeat_validated[1]++;
            else diag->heartbeat_rejected++;
            return -1;
        }
        int rejected = assist_sequence(assist, 0, sequence);
        if(rejected >= 0) { assist_reject(assist, direction, kind, rejected, now_ms); return -1; }
        diag->rx_validated++;
        diag->rx_validated_bytes += inner_bytes;
        if(sequence > diag->rx_valid_sequence_high) diag->rx_valid_sequence_high = sequence;
        if(context) {
            context->valid = 1;
            memcpy(context->circuit, assist->circuit, 16);
            memcpy(context->source, src, N2N_MAC_SIZE);
            memcpy(context->destination, dst, N2N_MAC_SIZE);
            context->sequence = sequence;
            context->created_ms = assist->created_ms;
            context->inner_bytes = inner_bytes;
        }
        memcpy(source, src, N2N_MAC_SIZE);
        *size -= ASSIST_HEADER;
        memmove(data, data + ASSIST_HEADER, *size);
        assist->rx_packets++;
        return 1;
    }
    return -1;
}

int mikun2n_assist_set (struct n3n_runtime_data *eee, const uint8_t *circuit,
                       uint8_t role, const n2n_mac_t first, const n2n_mac_t second,
                       int enabled, uint64_t now_ms) {
    assist_lock(eee);
    int result = assist_set(eee, circuit, role, first, second, enabled, now_ms);
    assist_unlock(eee);
    return result;
}

void mikun2n_assist_snapshot (struct n3n_runtime_data *eee, unsigned index, mikun2n_assist_t *snapshot) {
    assist_lock(eee);
    if(index < MIKUN2N_ASSIST_MAX) *snapshot = eee->mikun2n_assists[index];
    else memset(snapshot, 0, sizeof(*snapshot));
    assist_unlock(eee);
}

int mikun2n_assist_any (struct n3n_runtime_data *eee) {
    int result = 0;
    assist_lock(eee);
    for(unsigned i = 0; i < MIKUN2N_ASSIST_MAX; i++)
        if(eee->mikun2n_assists[i].role) { result = 1; break; }
    assist_unlock(eee);
    return result;
}

int mikun2n_assist_peer_active (struct n3n_runtime_data *eee, const n2n_mac_t mac, uint64_t now_ms) {
    int result = 0;
    assist_lock(eee);
    for(unsigned i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(assist->role == MIKUN2N_ASSIST_ENDPOINT && !memcmp(assist->first_mac, mac, N2N_MAC_SIZE) &&
           mikun2n_assist_active(eee, assist, now_ms)) { result = 1; break; }
    }
    assist_unlock(eee);
    return result;
}

void mikun2n_assist_tick (struct n3n_runtime_data *eee, uint64_t now_ms) {
    assist_lock(eee);
    assist_tick(eee, now_ms);
    assist_unlock(eee);
}

int mikun2n_assist_send (struct n3n_runtime_data *eee, const n2n_mac_t dest,
                        const uint8_t *data, size_t size, uint64_t now_ms) {
    assist_lock(eee);
    int result = assist_send(eee, dest, data, size, now_ms);
    assist_unlock(eee);
    return result;
}

int mikun2n_assist_receive (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                           SOCKET socket_fd, uint8_t *data, size_t *size,
                           n2n_mac_t source, mikun2n_assist_rx_context_t *context, uint64_t now_ms) {
    if(context) memset(context, 0, sizeof(*context));
    assist_lock(eee);
    int result = assist_receive(eee, sender, socket_fd, data, size, source, context, now_ms);
    assist_unlock(eee);
    return result;
}

void mikun2n_assist_delivery (struct n3n_runtime_data *eee,
                             const mikun2n_assist_rx_context_t *context,
                             mikun2n_assist_delivery_result_t result, size_t bytes, uint64_t now_ms) {
    if(!context || !context->valid) return;
    assist_lock(eee);
    for(unsigned i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(!assist->role || assist->created_ms != context->created_ms || memcmp(assist->circuit, context->circuit, 16)) continue;
        int direction = assist_direction(assist, context->source, context->destination);
        if(direction < 0) break;
        mikun2n_assist_direction_t *diag = &assist->directions[direction];
        switch(result) {
            case MIKUN2N_ASSIST_INNER_ACCEPTED:
                diag->inner_accepted++;
                diag->inner_accepted_bytes += context->inner_bytes;
                break;
            case MIKUN2N_ASSIST_TAP_ATTEMPT:
                diag->tap_attempts++;
                diag->tap_attempt_bytes += bytes;
                break;
            case MIKUN2N_ASSIST_TAP_SUBMITTED:
                diag->tap_submitted++;
                diag->tap_submitted_bytes += bytes;
                break;
            case MIKUN2N_ASSIST_REJECT_HEADER: diag->rejected[MIKUN2N_ASSIST_DROP_INNER_HEADER]++; break;
            case MIKUN2N_ASSIST_REJECT_COMMON: diag->rejected[MIKUN2N_ASSIST_DROP_COMMON]++; break;
            case MIKUN2N_ASSIST_REJECT_TYPE: diag->rejected[MIKUN2N_ASSIST_DROP_TYPE]++; break;
            case MIKUN2N_ASSIST_REJECT_COMMUNITY: diag->rejected[MIKUN2N_ASSIST_DROP_COMMUNITY]++; break;
            case MIKUN2N_ASSIST_REJECT_PACKET: diag->rejected[MIKUN2N_ASSIST_DROP_PACKET]++; break;
            case MIKUN2N_ASSIST_REJECT_MAC: diag->rejected[MIKUN2N_ASSIST_DROP_MAC]++; break;
            case MIKUN2N_ASSIST_REJECT_TIMESTAMP: diag->rejected[MIKUN2N_ASSIST_DROP_TIMESTAMP]++; break;
            case MIKUN2N_ASSIST_REJECT_UNREGISTERED: diag->rejected[MIKUN2N_ASSIST_DROP_UNREGISTERED]++; break;
            case MIKUN2N_ASSIST_REJECT_TRANSFORM: diag->rejected[MIKUN2N_ASSIST_DROP_TRANSFORM]++; break;
            case MIKUN2N_ASSIST_REJECT_PAYLOAD: diag->rejected[MIKUN2N_ASSIST_DROP_PAYLOAD]++; break;
            case MIKUN2N_ASSIST_REJECT_FILTER: diag->rejected[MIKUN2N_ASSIST_DROP_FILTER]++; break;
            case MIKUN2N_ASSIST_REJECT_TAP: diag->rejected[MIKUN2N_ASSIST_DROP_TAP]++; break;
            default: assist_unlock(eee); return;
        }
        assist_changed(assist, now_ms);
        break;
    }
    assist_unlock(eee);
}
