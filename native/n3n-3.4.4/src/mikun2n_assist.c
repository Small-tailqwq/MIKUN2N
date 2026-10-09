/* SPDX-License-Identifier: GPL-3.0-only */
#ifdef _WIN32
#include "win32/defs.h"
#endif
#include "mikun2n_assist.h"
#include "peer_info.h"
#include <n2n.h>
#include <n3n/ethernet.h>
#include <n3n/random.h>
#include <string.h>

#define ASSIST_HEADER 44
#define ASSIST_DATA 1
#define ASSIST_PING 2
#define ASSIST_PONG 3
#define ASSIST_LEASE_MS 30000
#define ASSIST_READY_MS 6000
#define ASSIST_PROBE_MS 1000

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
        if(scan->role && now_ms >= scan->expires_ms) memset(scan, 0, sizeof(*scan));
        if(!scan->role) { if(!free_entry) free_entry = scan; continue; }
        if(!memcmp(scan->circuit, circuit, 16)) entry = scan;
        else if(enabled && role != MIKUN2N_ASSIST_RELAY && scan->role != MIKUN2N_ASSIST_RELAY &&
                !memcmp(scan->first_mac, first, N2N_MAC_SIZE)) return -1;
    }
    if(!enabled) { if(entry) memset(entry, 0, sizeof(*entry)); return 1; }
    if(entry && (memcmp(entry->first_mac, first, N2N_MAC_SIZE) || memcmp(entry->second_mac, second, N2N_MAC_SIZE) ||
                  ((entry->role == MIKUN2N_ASSIST_RELAY) != (role == MIKUN2N_ASSIST_RELAY)))) return -1;
    if(!entry) {
        if(!free_entry) return -2;
        entry = free_entry;
        memset(entry, 0, sizeof(*entry));
        memcpy(entry->circuit, circuit, 16);
        memcpy(entry->first_mac, first, N2N_MAC_SIZE);
        memcpy(entry->second_mac, second, N2N_MAC_SIZE);
    }
    entry->role = role;
    entry->expires_ms = now_ms + ASSIST_LEASE_MS;
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
        if(now_ms >= assist->expires_ms) { memset(assist, 0, sizeof(*assist)); continue; }
        if(assist->role == MIKUN2N_ASSIST_RELAY || now_ms < assist->next_probe_ms ||
           mikun2n_assist_forced_relay(eee, assist->first_mac)) continue;
        assist->next_probe_ms = now_ms + ASSIST_PROBE_MS;
        uint8_t probe[ASSIST_HEADER];
        unsigned slot = assist->next_challenge++ % 8;
        assist->challenges[slot] = n3n_rand() | 1;
        assist->challenge_sent_ms[slot] = now_ms;
        assist_header(probe, assist, eee->device.mac_addr, assist->first_mac, ASSIST_PING, assist->challenges[slot]);
        mikun2n_assist_direct_send(eee, assist->second_mac, probe, sizeof(probe), now_ms);
    }
}

static int assist_send (struct n3n_runtime_data *eee, const n2n_mac_t dest,
                        const uint8_t *data, size_t size, uint64_t now_ms) {
    if(is_multi_broadcast(dest) || !size || size > N2N_PKT_BUF_SIZE - ASSIST_HEADER) return 0;
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(assist->role != MIKUN2N_ASSIST_ENDPOINT || memcmp(assist->first_mac, dest, N2N_MAC_SIZE) ||
           !mikun2n_assist_active(eee, assist, now_ms)) continue;
        uint8_t packet[N2N_PKT_BUF_SIZE];
        uint64_t sequence = ++assist->tx_sequence;
        if(!sequence) return 0;
        assist_header(packet, assist, eee->device.mac_addr, dest, ASSIST_DATA, sequence);
        memcpy(packet + ASSIST_HEADER, data, size);
        if(!mikun2n_assist_direct_send(eee, assist->second_mac, packet, size + ASSIST_HEADER, now_ms)) {
            assist->ready_until_ms = 0;
            return 0;
        }
        assist->tx_packets++;
        return 1;
    }
    return 0;
}

static int assist_sequence (mikun2n_assist_t *assist, unsigned direction, uint64_t sequence) {
    if(!sequence) return 0;
    uint64_t *highest = &assist->rx_sequence[direction], *window = &assist->rx_window[direction];
    if(sequence > *highest) {
        uint64_t delta = sequence - *highest;
        *window = delta >= 64 ? 1 : (*window << delta) | 1;
        *highest = sequence;
        return 1;
    }
    uint64_t delta = *highest - sequence;
    if(delta >= 64 || (*window & ((uint64_t)1 << delta))) return 0;
    *window |= (uint64_t)1 << delta;
    return 1;
}

static int assist_receive (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                           SOCKET socket_fd, uint8_t *data, size_t *size,
                           n2n_mac_t source, uint64_t now_ms) {
    if(*size < 3 || memcmp(data, assist_magic, 3)) return 0;
    if(*size < ASSIST_HEADER || *size > N2N_PKT_BUF_SIZE || memcmp(data, assist_magic, 4) ||
       data[5] > 1 || data[6] || data[7] || !eee->conf.allow_p2p) return -1;
    uint8_t kind = data[4];
    if((kind != ASSIST_DATA && kind != ASSIST_PING && kind != ASSIST_PONG) ||
       (kind == ASSIST_DATA ? *size == ASSIST_HEADER : *size != ASSIST_HEADER)) return -1;
    for(int i = 0; i < MIKUN2N_ASSIST_MAX; i++) {
        mikun2n_assist_t *assist = &eee->mikun2n_assists[i];
        if(!assist->role || now_ms >= assist->expires_ms || memcmp(assist->circuit, data + 8, 16)) continue;
        const uint8_t *src = data + 24, *dst = data + 30;
        uint64_t sequence = assist_read64(data + 36);
        if(assist->role == MIKUN2N_ASSIST_RELAY) {
            unsigned direction;
            if(!memcmp(src, assist->first_mac, N2N_MAC_SIZE) && !memcmp(dst, assist->second_mac, N2N_MAC_SIZE)) direction = 0;
            else if(!memcmp(src, assist->second_mac, N2N_MAC_SIZE) && !memcmp(dst, assist->first_mac, N2N_MAC_SIZE)) direction = 1;
            else return -1;
            if(data[5] || !mikun2n_assist_direct_accept(eee, src, sender, socket_fd, now_ms) ||
               !mikun2n_assist_direct_ready(eee, dst, now_ms)) return -1;
            if(kind == ASSIST_DATA && !assist_sequence(assist, direction, sequence)) return -1;
            data[5] = 1;
            if(kind == ASSIST_DATA) assist->rx_packets++;
            if(mikun2n_assist_direct_send(eee, dst, data, *size, now_ms) && kind == ASSIST_DATA) assist->tx_packets++;
            return -1;
        }
        if(data[5] != 1 || memcmp(src, assist->first_mac, N2N_MAC_SIZE) ||
           memcmp(dst, eee->device.mac_addr, N2N_MAC_SIZE) ||
           mikun2n_assist_forced_relay(eee, src) ||
           !mikun2n_assist_direct_accept(eee, assist->second_mac, sender, socket_fd, now_ms)) return -1;
        struct peer_info *peer;
        HASH_FIND_PEER(eee->known_peers, src, peer);
        if(!peer) HASH_FIND_PEER(eee->pending_peers, src, peer);
        if(peer) peer->last_seen = time(NULL);
        if(kind == ASSIST_PING) {
            uint8_t reply[ASSIST_HEADER];
            assist_header(reply, assist, eee->device.mac_addr, assist->first_mac, ASSIST_PONG, sequence);
            mikun2n_assist_direct_send(eee, assist->second_mac, reply, sizeof(reply), now_ms);
            return -1;
        }
        if(kind == ASSIST_PONG) {
            for(unsigned slot = 0; slot < 8; slot++) {
                if(sequence && sequence == assist->challenges[slot] &&
                   now_ms - assist->challenge_sent_ms[slot] < ASSIST_READY_MS) {
                    uint64_t ready_until = assist->challenge_sent_ms[slot] + ASSIST_READY_MS;
                    if(ready_until > assist->ready_until_ms) assist->ready_until_ms = ready_until;
                    assist->challenges[slot] = 0;
                    break;
                }
            }
            return -1;
        }
        if(!assist_sequence(assist, 0, sequence)) return -1;
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
                           n2n_mac_t source, uint64_t now_ms) {
    assist_lock(eee);
    int result = assist_receive(eee, sender, socket_fd, data, size, source, now_ms);
    assist_unlock(eee);
    return result;
}
