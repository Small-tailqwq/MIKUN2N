/* SPDX-License-Identifier: GPL-3.0-only */
#ifdef _WIN32
#include "win32/defs.h"
#endif
#include <n2n.h>
#include <n3n/edge.h>
#include <n3n/ethernet.h>
#include <n3n/logging.h>
#include <n3n/random.h>
#include <n3n/strings.h>
#include "mikun2n_ipv6.h"
#include "mikun2n_stun.h"
#include "n2n_wire.h"
#include "peer_info.h"
#include "portable_endian.h"
#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#ifdef _WIN32
#include <iphlpapi.h>
#include <wincrypt.h>
#else
#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <unistd.h>
#endif

#define IPV6_SAFE_UDP_SIZE 1232 /* IPv6 minimum MTU minus IPv6/UDP headers. */
#define IPV6_FRAME_SIZE 41
#define IPV6_CONTROL_SIZE 47 /* readiness, receive capacity, acknowledged probe size */
#define IPV6_RX_LIMIT 1452 /* no IP fragmentation on a 1500-byte link */
#define IPV6_FRAGMENT 4
#define IPV6_FRAGMENT_HEADER 45
#define IPV6_PING 1
#define IPV6_PONG 2
#define IPV6_DATA 3
#define IPV6_REPORT_TTL_MS 30000
#define IPV6_PATH_TTL_MS 6500
#define IPV6_KEEPALIVE_MS 2000
#define IPV6_FAST_PROBE_MS 500
#define IPV6_LEASE_MARGIN_MS 3500
#define IPV6_READY_RECENT_MS 15000
#define IPV6_QUERY_MS 3000
#define IPV6_QUERY_ACTIVE_MS 10000
#define IPV6_QUERY_SILENT_MS 15000
#define IPV6_PEER_SILENT_MS 20000
#define IPV6_LOCAL_WARMUP_MS 30000
#define IPV6_LOCAL_FAILURE_MS 20000
#define IPV6_LOCAL_FAILURE_RECENT_MS 15000
static const uint8_t ipv6_magic[4] = {'M', 'K', '6', MIKUN2N_IPV6_WIRE_VERSION};

static int ipv6_message_too_large(int error);
static int ipv6_transient_error(int error);

/* Only completed, source-bound round trips confirm this socket's source address.
 * Failed large MTU searches and temporary send-queue pressure are not evidence. */
static void ipv6_local_confirmed(struct n3n_runtime_data *eee, uint64_t now_ms) {
    eee->mikun2n_ipv6_local_ok_ms = now_ms;
    eee->mikun2n_ipv6_local_fail_ms = 0;
    eee->mikun2n_ipv6_local_last_fail_ms = 0;
    eee->mikun2n_ipv6_local_failures = 0;
}

static void ipv6_local_failed(struct n3n_runtime_data *eee, uint64_t now_ms) {
    if(!eee->mikun2n_ipv6_bound_ms || eee->mikun2n_ipv6_socket == MIKUN2N_INVALID_SOCKET) return;
    if(!eee->mikun2n_ipv6_local_fail_ms ||
       now_ms - eee->mikun2n_ipv6_local_last_fail_ms > IPV6_LOCAL_FAILURE_RECENT_MS) {
        eee->mikun2n_ipv6_local_fail_ms = now_ms;
        eee->mikun2n_ipv6_local_failures = 0;
    }
    eee->mikun2n_ipv6_local_last_fail_ms = now_ms;
    if(eee->mikun2n_ipv6_local_failures < UINT32_MAX) eee->mikun2n_ipv6_local_failures++;
}

static int ipv6_local_retry_due(const struct n3n_runtime_data *eee, uint64_t now_ms) {
    return eee->mikun2n_ipv6_bound_ms && eee->mikun2n_ipv6_local_fail_ms &&
           eee->mikun2n_ipv6_local_failures >= 3 &&
           now_ms - eee->mikun2n_ipv6_bound_ms >= IPV6_LOCAL_WARMUP_MS &&
           now_ms - eee->mikun2n_ipv6_local_fail_ms >= IPV6_LOCAL_FAILURE_MS &&
           now_ms - eee->mikun2n_ipv6_local_last_fail_ms <= IPV6_LOCAL_FAILURE_RECENT_MS;
}

static int ipv6_diagnostics (void) {
    static int enabled = -1;
    if(enabled < 0) {
        const char *value = getenv("MIKUN2N_IPV6_DIAGNOSTICS");
        enabled = value && !strcmp(value, "1");
    }
    return enabled;
}

#define V6_DIAG(format, ...) do { if(ipv6_diagnostics()) \
    traceEvent(TRACE_NORMAL, "MikuN2N v6diag " format, ##__VA_ARGS__); } while(0)
static uint64_t diag_rx_data, diag_tx_data, diag_rx_bytes, diag_tx_bytes;
static uint64_t diag_tx_fragments, diag_rx_fragments, diag_reassembled, diag_transient_drops;
static uint64_t diag_reject_header, diag_reject_peer, diag_reject_pong, diag_reject_inactive;
static uint64_t diag_peer_generation;

#define V6_COUNT(d, field) mikun2n_v6_diag_add(&(d)->field, 1)
#define V6_VALUE(field) ((unsigned long long)mikun2n_v6_diag_read(&d->field))

static void ipv6_diag_summary(struct peer_info *peer, uint64_t now_ms, const char *reason, int force) {
    mikun2n_ipv6_diag_t *d = &peer->mikun2n_ipv6_diag;
    uint64_t generation = mikun2n_v6_diag_read(&d->generation);
    uint64_t revision = mikun2n_v6_diag_read(&d->revision);
    if(!generation || (!force && (now_ms - mikun2n_v6_diag_read(&d->report_ms) < 10000 ||
            revision == mikun2n_v6_diag_read(&d->report_revision)))) return;
    __atomic_store_n(&d->report_ms, now_ms, __ATOMIC_RELAXED);
    __atomic_store_n(&d->report_revision, revision, __ATOMIC_RELAXED);
    uint64_t seq = __atomic_add_fetch(&d->report_seq, 1, __ATOMIC_RELAXED);
    uint64_t packed_local = mikun2n_v6_diag_read(&d->local_mac);
    n2n_mac_t local_mac;
    memcpy(local_mac, &packed_local, N2N_MAC_SIZE);
    macstr_t local_text, peer_text;
    macaddr_str(local_text, local_mac);
    macaddr_str(peer_text, peer->mac_addr);
    unsigned occupied = 0, overdue = 0, missing = 0;
    if(peer->mikun2n_ipv6_reassembly) {
        for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) {
            const mikun2n_ipv6_fragment_t *slot = &peer->mikun2n_ipv6_reassembly->slots[i];
            if(!slot->id) continue;
            ++occupied;
            overdue += slot->expires <= now_ms;
            missing += slot->total - slot->received;
        }
    }
    /* gen/seq are local-only; fragment IDs are sender-session-wide, not contiguous
     * per peer. These ranges support correlation, never a network-loss estimate. */
#define V6_SUMMARY(format, ...) traceEvent(TRACE_NORMAL, \
        "MikuN2N v6peer local=%s peer=%s gen=%llu path_gen=%llu seq=%llu monotonic_ms=%llu reason=%s " format, \
        local_text, peer_text, (unsigned long long)generation, V6_VALUE(path_generation), \
        (unsigned long long)seq, (unsigned long long)now_ms, reason, ##__VA_ARGS__)
    V6_SUMMARY("part=tx attempted=%llu full=%llu full_bytes=%llu failed=%llu partial=%llu consumed_drop=%llu udp_submitted=%llu udp_bytes=%llu fragments=%llu udp_failed=%llu transient=%llu emsgsize=%llu datagram_highwater=%llu udp_highwater=%llu",
            V6_VALUE(tx_attempted), V6_VALUE(tx_full), V6_VALUE(tx_full_bytes), V6_VALUE(tx_failed),
            V6_VALUE(tx_partial), V6_VALUE(tx_consumed_drop), V6_VALUE(tx_udp), V6_VALUE(tx_udp_bytes),
            V6_VALUE(tx_fragments), V6_VALUE(tx_udp_failed), V6_VALUE(tx_transient), V6_VALUE(tx_emsgsize),
            V6_VALUE(tx_datagram_highwater), V6_VALUE(tx_udp_highwater));
    V6_SUMMARY("part=rx udp_received=%llu udp_bytes=%llu delivered=%llu delivered_bytes=%llu fragments=%llu control_received=%llu datagram_highwater=%llu udp_highwater=%llu checked_bytes=%u wire=%u",
            V6_VALUE(rx_udp), V6_VALUE(rx_udp_bytes), V6_VALUE(rx_data), V6_VALUE(rx_bytes),
            V6_VALUE(rx_fragments), V6_VALUE(rx_control), V6_VALUE(rx_datagram_highwater), V6_VALUE(rx_udp_highwater),
            (unsigned)peer->mikun2n_ipv6_path_bytes, (unsigned)peer->mikun2n_ipv6_wire_version);
    V6_SUMMARY("part=reassembly started=%llu completed=%llu waiting=%llu slots_current=%u slots_highwater=%llu overdue_unreaped=%u unfinished_missing_bytes=%u expired=%llu expired_missing_bytes=%llu reset_unfinished=%llu reset_missing_bytes=%llu",
            V6_VALUE(frag.started), V6_VALUE(frag.completed), V6_VALUE(frag.waiting), occupied,
            V6_VALUE(frag.slots_highwater), overdue, missing, V6_VALUE(frag.expired),
            V6_VALUE(frag.expired_missing_bytes), V6_VALUE(frag.reset_unfinished), V6_VALUE(frag.reset_missing_bytes));
    V6_SUMMARY("part=fragment_reject slot_full=%llu duplicate=%llu overlap=%llu conflicting=%llu invalid=%llu retired=%llu retired_window=%llu",
            V6_VALUE(frag.slot_full), V6_VALUE(frag.duplicate), V6_VALUE(frag.overlap),
            V6_VALUE(frag.conflicting), V6_VALUE(frag.invalid), V6_VALUE(frag.retired), V6_VALUE(frag.retired_window));
    V6_SUMMARY("part=reject tx_inactive=%llu tx_invalid_size=%llu tx_budget=%llu header=%llu session=%llu version=%llu endpoint=%llu readiness=%llu policy=%llu candidate=%llu control=%llu pong=%llu allocation=%llu resets=%llu",
            V6_VALUE(tx_inactive), V6_VALUE(tx_invalid_size), V6_VALUE(tx_budget), V6_VALUE(reject_header), V6_VALUE(reject_session),
            V6_VALUE(reject_version), V6_VALUE(reject_endpoint), V6_VALUE(reject_readiness),
            V6_VALUE(reject_policy), V6_VALUE(reject_candidate), V6_VALUE(reject_control),
            V6_VALUE(reject_pong), V6_VALUE(reject_alloc), V6_VALUE(resets));
    V6_SUMMARY("part=range gen_scope=local id_scope=sender_session_global tx_id_min=%llu tx_id_max=%llu rx_id_min=%llu rx_id_max=%llu tx_failed_id=%llu tx_failed_offset=%llu tx_failed_total=%llu tx_last_error=%llu rx_reject_id=%llu rx_reject_offset=%llu rx_reject_length=%llu expired_id=%llu expired_received=%llu expired_total=%llu age_ms=%llu",
            V6_VALUE(tx_id_min), V6_VALUE(tx_id_max), V6_VALUE(rx_id_min), V6_VALUE(rx_id_max),
            V6_VALUE(tx_last_failure_id), V6_VALUE(tx_last_failure_offset), V6_VALUE(tx_last_failure_size), V6_VALUE(tx_last_error),
            V6_VALUE(frag.last_reject_id), V6_VALUE(frag.last_reject_offset), V6_VALUE(frag.last_reject_length),
            V6_VALUE(frag.last_expired_id), V6_VALUE(frag.last_expired_received), V6_VALUE(frag.last_expired_total),
            (unsigned long long)(now_ms - mikun2n_v6_diag_read(&d->started_ms)));
#undef V6_SUMMARY
}

static void ipv6_diag_reset(struct peer_info *peer, uint64_t now_ms, const char *reason) {
    mikun2n_ipv6_diag_t *d = &peer->mikun2n_ipv6_diag;
    if(!mikun2n_v6_diag_read(&d->generation)) return;
    if(peer->mikun2n_ipv6_reassembly) {
        for(unsigned i = 0; i < MIKUN2N_REASSEMBLY_SLOTS; ++i) {
            const mikun2n_ipv6_fragment_t *slot = &peer->mikun2n_ipv6_reassembly->slots[i];
            if(!slot->id) continue;
            V6_COUNT(d, frag.reset_unfinished);
            mikun2n_v6_diag_add(&d->frag.reset_missing_bytes, slot->total - slot->received);
        }
    }
    V6_COUNT(d, resets);
    V6_COUNT(d, revision);
    ipv6_diag_summary(peer, now_ms, reason, 1);
    V6_COUNT(d, path_generation);
    __atomic_store_n(&d->frag.slots_current, 0, __ATOMIC_RELAXED);
}

static void ipv6_diag_close(struct peer_info *peer) {
    ipv6_diag_reset(peer, mikun2n_ipv6_now_ms(), "peer_free");
}

static mikun2n_ipv6_diag_t *ipv6_diag_bind(struct n3n_runtime_data *eee, struct peer_info *peer, uint64_t now_ms) {
    if(!peer) return NULL;
    mikun2n_ipv6_diag_t *d = &peer->mikun2n_ipv6_diag;
    uint64_t packed_local = 0, zero = 0;
    memcpy(&packed_local, eee->device.mac_addr, N2N_MAC_SIZE);
    __atomic_store_n(&d->local_mac, packed_local, __ATOMIC_RELAXED);
    if(!mikun2n_v6_diag_read(&d->generation)) {
        uint64_t generation = __atomic_add_fetch(&diag_peer_generation, 1, __ATOMIC_RELAXED);
        __atomic_compare_exchange_n(&d->started_ms, &zero, now_ms, 0, __ATOMIC_RELAXED, __ATOMIC_RELAXED);
        zero = 0;
        __atomic_compare_exchange_n(&d->report_ms, &zero, now_ms, 0, __ATOMIC_RELAXED, __ATOMIC_RELAXED);
        zero = 0;
        __atomic_compare_exchange_n(&d->generation, &zero, generation, 0, __ATOMIC_RELAXED, __ATOMIC_RELAXED);
        __atomic_store_n(&peer->mikun2n_ipv6_diag_close, ipv6_diag_close, __ATOMIC_RELAXED);
    }
    return d;
}

static int ipv6_socket_error (void) {
#ifdef _WIN32
    return WSAGetLastError();
#else
    return errno;
#endif
}

uint64_t mikun2n_ipv6_now_ms (void) {
#ifdef _WIN32
    return GetTickCount64();
#else
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
#endif
}

int mikun2n_ipv6_public (const n2n_sock_t *address) {
    const uint8_t *ip = address->addr.v6;
    return address->family == AF_INET6 && address->port &&
           (ip[0] & 0xe0) == 0x20 &&
           !(ip[0] == 0x20 && ip[1] == 0x01 && ip[2] == 0x0d && ip[3] == 0xb8);
}

int mikun2n_ipv6_candidate (const n2n_sock_t *address) {
    return mikun2n_ipv6_public(address) ||
           (address->family == AF_INET6 && address->port && (address->addr.v6[0] & 0xfe) == 0xfc);
}

static int ipv6_address_rank (const n2n_sock_t *address) {
    return mikun2n_ipv6_public(address) ? 2 : mikun2n_ipv6_candidate(address) ? 1 : 0;
}

mikun2n_ipv6_status_t mikun2n_ipv6_status (const struct n3n_runtime_data *eee, uint64_t now_ms) {
    mikun2n_ipv6_status_t status = {"disabled", "none", "unknown"};
    if(!eee->conf.mikun2n_ipv6) return status;
    status.state = "unavailable";
    if(eee->mikun2n_ipv6_socket == MIKUN2N_INVALID_SOCKET ||
       !mikun2n_ipv6_candidate(&eee->mikun2n_ipv6_address)) return status;
    status.address_kind = mikun2n_ipv6_public(&eee->mikun2n_ipv6_address) ? "global" : "ula";
    status.state = eee->mikun2n_ipv6_bound_ms && now_ms >= eee->mikun2n_ipv6_bound_ms &&
        now_ms - eee->mikun2n_ipv6_bound_ms < IPV6_LOCAL_WARMUP_MS ? "checking" : "unverified";
    if(ipv6_local_retry_due(eee, now_ms)) { status.state = "unavailable"; return status; }
    if(eee->mikun2n_ipv6_local_ok_ms && now_ms >= eee->mikun2n_ipv6_local_ok_ms &&
       now_ms - eee->mikun2n_ipv6_local_ok_ms < IPV6_REPORT_TTL_MS) status.state = "available";
    if(eee->mikun2n_ipv6_mapped_ms && now_ms >= eee->mikun2n_ipv6_mapped_ms &&
       now_ms - eee->mikun2n_ipv6_mapped_ms < IPV6_REPORT_TTL_MS &&
       mikun2n_ipv6_public(&eee->mikun2n_ipv6_mapped_address)) {
        status.state = "available";
        status.mapping = memcmp(eee->mikun2n_ipv6_address.addr.v6,
                                eee->mikun2n_ipv6_mapped_address.addr.v6, 16) ? "address-translated" :
            eee->mikun2n_ipv6_address.port != eee->mikun2n_ipv6_mapped_address.port ? "port-translated" : "untranslated";
    }
    return status;
}

static struct peer_info *ipv6_peer (struct n3n_runtime_data *eee, const n2n_mac_t mac) {
    struct peer_info *peer;
    HASH_FIND_PEER(eee->known_peers, mac, peer);
    if(!peer)
        HASH_FIND_PEER(eee->pending_peers, mac, peer);
    return peer;
}

static void ipv6_put_u64 (uint8_t *out, uint64_t value) {
    value = htobe64(value);
    memcpy(out, &value, sizeof(value));
}

static uint64_t ipv6_get_u64 (const uint8_t *in) {
    uint64_t value;
    memcpy(&value, in, sizeof(value));
    return be64toh(value);
}

static void ipv6_header (struct n3n_runtime_data *eee, const struct peer_info *peer,
                         uint8_t *packet, uint8_t kind, uint64_t challenge) {
    memcpy(packet, ipv6_magic, sizeof(ipv6_magic));
    packet[4] = kind;
    memcpy(packet + 5, eee->device.mac_addr, N2N_MAC_SIZE);
    memcpy(packet + 11, peer->mac_addr, N2N_MAC_SIZE);
    ipv6_put_u64(packet + 17, eee->mikun2n_ipv6_token);
    ipv6_put_u64(packet + 25, peer->mikun2n_ipv6_token);
    ipv6_put_u64(packet + 33, challenge);
}

static ssize_t ipv6_send_raw (struct n3n_runtime_data *eee, const n2n_sock_t *destination,
                              const uint8_t *packet, size_t size) {
    struct sockaddr_in6 address;
    memset(&address, 0, sizeof(address));
    fill_sockaddr((struct sockaddr *)&address, sizeof(address), destination);
    return sendto(eee->mikun2n_ipv6_socket, (const char *)packet, (int)size, 0,
                  (struct sockaddr *)&address, sizeof(address));
}

/* Discovery uses the same UDP socket as peer traffic. The observer only supplies
 * a candidate; a peer's address-bound PONG is still required before DATA. */
n2n_sock_t mikun2n_ipv6_advertised (const struct n3n_runtime_data *eee, uint64_t now_ms) {
    if(eee->mikun2n_ipv6_mapped_ms &&
       now_ms - eee->mikun2n_ipv6_mapped_ms < IPV6_REPORT_TTL_MS)
        return eee->mikun2n_ipv6_mapped_address;
    return eee->mikun2n_ipv6_address;
}

static uint16_t ipv6_read_u16 (const uint8_t *p) {
    return ((uint16_t)p[0] << 8) | p[1];
}

static int ipv6_stun_random (uint8_t *data, size_t size) {
#ifdef _WIN32
    HCRYPTPROV provider;
    if(!CryptAcquireContext(&provider, NULL, NULL, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT))
        return 0;
    int ok = CryptGenRandom(provider, (DWORD)size, data);
    CryptReleaseContext(provider, 0);
    return ok;
#else
    int fd = open("/dev/urandom", O_RDONLY);
    if(fd < 0)
        return 0;
    ssize_t result = read(fd, data, size);
    close(fd);
    return result == (ssize_t)size;
#endif
}

static void ipv6_stun_init (struct n3n_runtime_data *eee) {
    if(eee->mikun2n_ipv6_stun_initialized)
        return;
    eee->mikun2n_ipv6_stun_initialized = true;
    const char *setting = getenv("MIKUN2N_IPV6_STUN_SERVERS");
    unsigned count = 0;
    // The launcher resolves optional observer hostnames outside the edge loop. An "m:"
    // prefix marks the supernode's own observer, which answers only tagged requests.
    while(setting && *setting && count < 2) {
        const char *end = strchr(setting, ';');
        size_t len = end ? (size_t)(end - setting) : strlen(setting);
        bool tagged = len > 2 && !strncmp(setting, "m:", 2);
        const char *start = tagged ? setting + 2 : setting;
        len -= tagged ? 2 : 0;
        char text[64];
        if(len && len < sizeof(text)) {
            memcpy(text, start, len);
            text[len] = 0;
            n2n_sock_t address = {0};
            address.family = AF_INET6;
            address.port = 3478;
            if(inet_pton(AF_INET6, text, address.addr.v6) == 1 && mikun2n_ipv6_public(&address)) {
                eee->mikun2n_ipv6_stun_tagged[count] = tagged;
                eee->mikun2n_ipv6_stun_servers[count++] = address;
            }
        }
        setting = end ? end + 1 : NULL;
    }
    V6_DIAG("mapping_discovery configured_observers=%u", count);
}

static void ipv6_stun_tick (struct n3n_runtime_data *eee, uint64_t now_ms) {
    ipv6_stun_init(eee);
    if(!eee->mikun2n_ipv6_stun_servers[0].family)
        return;
    if(eee->mikun2n_ipv6_mapped_ms && now_ms - eee->mikun2n_ipv6_mapped_ms >= IPV6_REPORT_TTL_MS) {
        eee->mikun2n_ipv6_mapped_ms = 0;
        V6_DIAG("mapping_expired; advertising local candidate until next reply");
    }
    if(eee->mikun2n_ipv6_stun_pending && now_ms - eee->mikun2n_ipv6_stun_sent_ms > 3500) {
        ipv6_local_failed(eee, now_ms);
        n2n_sock_str_t text;
        V6_DIAG("mapping_timeout observer=%s", sock_to_cstr(text,
                &eee->mikun2n_ipv6_stun_servers[eee->mikun2n_ipv6_stun_index]));
        eee->mikun2n_ipv6_stun_pending = false;
        if(eee->mikun2n_ipv6_stun_servers[1].family)
            eee->mikun2n_ipv6_stun_index ^= 1;
    }
    if(eee->mikun2n_ipv6_stun_pending || now_ms < eee->mikun2n_ipv6_stun_next_ms)
        return;
    uint8_t request[MIKUN2N_STUN_TAGGED_REQUEST_BYTES] = {0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42};
    if(!ipv6_stun_random(request + 8, 12)) {
        eee->mikun2n_ipv6_stun_next_ms = now_ms + 10000;
        V6_DIAG("mapping_request_failed reason=random_unavailable");
        return;
    }
    memcpy(eee->mikun2n_ipv6_stun_transaction, request + 8, 12);
    n2n_sock_t *observer = &eee->mikun2n_ipv6_stun_servers[eee->mikun2n_ipv6_stun_index];
    // Third-party observers get the anonymous 20-byte request; only the supernode's
    // own observer learns which registered edge is asking.
    size_t size = eee->mikun2n_ipv6_stun_tagged[eee->mikun2n_ipv6_stun_index]
        ? mikun2n_stun_tag_request(request, eee->device.mac_addr) : 20;
    ssize_t sent = ipv6_send_raw(eee, observer, request, size);
    int error = sent < 0 ? ipv6_socket_error() : 0;
    if(sent < 0 && !ipv6_transient_error(error) && !ipv6_message_too_large(error))
        ipv6_local_failed(eee, now_ms);
    eee->mikun2n_ipv6_stun_pending = sent == (ssize_t)size;
    eee->mikun2n_ipv6_stun_sent_ms = now_ms;
    eee->mikun2n_ipv6_stun_next_ms = now_ms + 10000;
    n2n_sock_str_t text;
    V6_DIAG("mapping_request observer=%s sent=%d bytes=%u error=%d",
            sock_to_cstr(text, observer), eee->mikun2n_ipv6_stun_pending, (unsigned)size, error);
    if(!eee->mikun2n_ipv6_stun_pending && eee->mikun2n_ipv6_stun_servers[1].family)
        eee->mikun2n_ipv6_stun_index ^= 1;
}

static uint32_t ipv6_stun_fingerprint (const uint8_t *data, size_t size) {
    uint32_t crc = 0xffffffff;
    for(size_t i = 0; i < size; i++) {
        crc ^= data[i];
        for(int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
    }
    return ~crc ^ 0x5354554e;
}

static int ipv6_stun_receive (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                             const uint8_t *data, size_t size, uint64_t now_ms) {
    static const uint8_t cookie[4] = {0x21, 0x12, 0xa4, 0x42};
    if(size < 20 || memcmp(data + 4, cookie, sizeof(cookie)))
        return 0;
    if(!eee->mikun2n_ipv6_stun_pending ||
       now_ms - eee->mikun2n_ipv6_stun_sent_ms > 3500 ||
       !sock_equal(sender, &eee->mikun2n_ipv6_stun_servers[eee->mikun2n_ipv6_stun_index]) ||
       memcmp(data + 8, eee->mikun2n_ipv6_stun_transaction, 12)) {
        V6_DIAG("mapping_reply_rejected reason=transaction_age_or_endpoint");
        return 1;
    }
    if(ipv6_read_u16(data) != 0x0101 || size > 512 ||
       (ipv6_read_u16(data + 2) & 3) || ipv6_read_u16(data + 2) + 20u != size) {
        V6_DIAG("mapping_reply_rejected reason=message_type_or_length");
        return 1;
    }
    n2n_sock_t mapped = {0};
    for(size_t offset = 20; offset < size;) {
        if(offset + 4 > size)
            return 1;
        uint16_t type = ipv6_read_u16(data + offset), len = ipv6_read_u16(data + offset + 2);
        size_t end = offset + 4u + ((len + 3u) & ~3u);
        if(end > size)
            return 1;
        const uint8_t *value = data + offset + 4;
        if(type == 0x0020) {
            if(len != 20 || value[0] || value[1] != 2 || mapped.family)
                return 1;
            mapped.family = AF_INET6;
            mapped.port = ipv6_read_u16(value + 2) ^ 0x2112;
            for(unsigned i = 0; i < 16; i++)
                mapped.addr.v6[i] = value[4 + i] ^ data[4 + i];
        } else if(type == 0x8028) {
            uint32_t fingerprint;
            if(len != 4 || end != size)
                return 1;
            memcpy(&fingerprint, value, sizeof(fingerprint));
            if(ntohl(fingerprint) != ipv6_stun_fingerprint(data, offset))
                return 1;
        } else if(type < 0x8000 && type != 0x0001) {
            return 1;
        }
        offset = end;
    }
    if(!mikun2n_ipv6_public(&mapped)) {
        V6_DIAG("mapping_reply_rejected reason=no_public_ipv6_mapping");
        return 1;
    }
    int changed = !eee->mikun2n_ipv6_mapped_ms || !sock_equal(&mapped, &eee->mikun2n_ipv6_mapped_address);
    eee->mikun2n_ipv6_stun_pending = false;
    eee->mikun2n_ipv6_mapped_address = mapped;
    eee->mikun2n_ipv6_mapped_ms = now_ms;
    ipv6_local_confirmed(eee, now_ms);
    n2n_sock_str_t text;
    V6_DIAG("mapping_observed address=%s changed=%d translated=%d rtt_ms=%llu ttl_ms=%u",
            sock_to_cstr(text, &mapped), changed, !sock_equal(&mapped, &eee->mikun2n_ipv6_address),
            (unsigned long long)(now_ms - eee->mikun2n_ipv6_stun_sent_ms), IPV6_REPORT_TTL_MS);
    if(changed) {
        struct peer_info *peer, *tmp;
        HASH_ITER(hh, eee->known_peers, peer, tmp)
            peer->mikun2n_ipv6_query_ms = 0;
        HASH_ITER(hh, eee->pending_peers, peer, tmp)
            peer->mikun2n_ipv6_query_ms = 0;
    }
    return 1;
}


typedef struct {
    n2n_sock_t address;
    uint32_t if_index, metric;
} ipv6_local_candidate_t;

static int ipv6_interface_cooling(const struct n3n_runtime_data *eee, uint32_t if_index, uint64_t now_ms) {
    for(unsigned i = 0; i < MIKUN2N_IPV6_RECENT_INTERFACES; ++i)
        if(eee->mikun2n_ipv6_interface_retry[i].if_index == if_index &&
           eee->mikun2n_ipv6_interface_retry[i].retry_after_ms > now_ms) return 1;
    return 0;
}

static void ipv6_interface_failed(struct n3n_runtime_data *eee, uint32_t if_index, uint64_t now_ms) {
    if(!if_index) return;
    unsigned oldest = 0;
    for(unsigned i = 0; i < MIKUN2N_IPV6_RECENT_INTERFACES; ++i) {
        if(eee->mikun2n_ipv6_interface_retry[i].if_index == if_index) {
            /* Repeated scans of an unchanged failed interface must not extend
             * its cooldown forever while another interface is being tried. */
            if(eee->mikun2n_ipv6_interface_retry[i].retry_after_ms > now_ms) return;
            oldest = i;
            break;
        }
        if(eee->mikun2n_ipv6_interface_retry[i].retry_after_ms <
           eee->mikun2n_ipv6_interface_retry[oldest].retry_after_ms) oldest = i;
    }
    eee->mikun2n_ipv6_interface_retry[oldest].if_index = if_index;
    eee->mikun2n_ipv6_interface_retry[oldest].retry_after_ms = now_ms + 60000;
}

static void ipv6_consider_local(const struct n3n_runtime_data *eee, const ipv6_local_candidate_t *candidate,
        uint64_t now_ms, int retry, ipv6_local_candidate_t *best, ipv6_local_candidate_t *current) {
    int rank = ipv6_address_rank(&candidate->address), best_rank = ipv6_address_rank(&best->address);
    if(!rank || !candidate->if_index) return;
    if(candidate->if_index == eee->mikun2n_ipv6_if_index &&
       sock_equal(&candidate->address, &eee->mikun2n_ipv6_address)) *current = *candidate;
    if((retry && candidate->if_index == eee->mikun2n_ipv6_if_index) ||
       ipv6_interface_cooling(eee, candidate->if_index, now_ms)) return;
    if(rank > best_rank || (rank == best_rank && candidate->metric < best->metric)) *best = *candidate;
}

static ipv6_local_candidate_t ipv6_local_address(struct n3n_runtime_data *eee, uint16_t port,
        uint64_t now_ms, int retry, int *scan_ok, int *current_present) {
    ipv6_local_candidate_t best = {0}, current = {0};
    *scan_ok = 0;
    *current_present = 0;
#ifdef _WIN32
    ULONG size = 16384;
    IP_ADAPTER_ADDRESSES *adapters = malloc(size);
    if(!adapters)
        return best;
    ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST |
                  GAA_FLAG_SKIP_DNS_SERVER | GAA_FLAG_INCLUDE_GATEWAYS;
    ULONG result = GetAdaptersAddresses(AF_INET6, flags, NULL, adapters, &size);
    if(result == ERROR_BUFFER_OVERFLOW) {
        free(adapters);
        adapters = malloc(size);
        if(!adapters)
            return best;
        result = GetAdaptersAddresses(AF_INET6, flags, NULL, adapters, &size);
    }
    V6_DIAG("address_scan result=%lu port=%u", (unsigned long)result, port);
    *scan_ok = result == NO_ERROR || result == ERROR_NO_DATA;
    if(result == NO_ERROR) {
        for(IP_ADAPTER_ADDRESSES *adapter = adapters; adapter; adapter = adapter->Next) {
            if(adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK ||
               !adapter->Ipv6IfIndex)
                continue;
            int has_gateway = 0;
            for(IP_ADAPTER_GATEWAY_ADDRESS *gateway = adapter->FirstGatewayAddress; gateway; gateway = gateway->Next)
                if(gateway->Address.lpSockaddr && gateway->Address.lpSockaddr->sa_family == AF_INET6)
                    has_gateway = 1;
            if(!has_gateway)
                continue;
            for(IP_ADAPTER_UNICAST_ADDRESS *item = adapter->FirstUnicastAddress; item; item = item->Next) {
                if(!item->Address.lpSockaddr || item->Address.lpSockaddr->sa_family != AF_INET6 ||
                   item->DadState != IpDadStatePreferred || item->PreferredLifetime < 30 ||
                   !item->ValidLifetime)
                    continue;
                ipv6_local_candidate_t candidate = {0};
                fill_n2nsock(&candidate.address, item->Address.lpSockaddr);
                candidate.address.port = port;
                candidate.if_index = adapter->Ipv6IfIndex;
                candidate.metric = adapter->Ipv6Metric;
                ipv6_consider_local(eee, &candidate, now_ms, retry, &best, &current);
                if(best.if_index == candidate.if_index && sock_equal(&best.address, &candidate.address)) {
                    n2n_sock_str_t text;
                    V6_DIAG("local_candidate address=%s if_index=%lu metric=%lu preferred_lifetime=%lu link_mtu=%lu kind=%s",
                            sock_to_cstr(text, &candidate.address), (unsigned long)candidate.if_index,
                            (unsigned long)candidate.metric, (unsigned long)item->PreferredLifetime, (unsigned long)adapter->Mtu,
                            ipv6_address_rank(&candidate.address) == 2 ? "global" : "ula_nat66");
                }
            }
        }
    }
    free(adapters);
#else
    struct ifaddrs *addresses = NULL;
    if(getifaddrs(&addresses) == 0) {
        *scan_ok = 1;
        for(struct ifaddrs *item = addresses; item; item = item->ifa_next) {
            if(!item->ifa_addr || item->ifa_addr->sa_family != AF_INET6 ||
               !(item->ifa_flags & IFF_UP) || (item->ifa_flags & IFF_LOOPBACK))
                continue;
            ipv6_local_candidate_t candidate = {0};
            fill_n2nsock(&candidate.address, item->ifa_addr);
            candidate.address.port = port;
            candidate.if_index = if_nametoindex(item->ifa_name);
            ipv6_consider_local(eee, &candidate, now_ms, retry, &best, &current);
        }
        freeifaddrs(addresses);
    }
#endif
    *current_present = current.address.family == AF_INET6;
    /* A working binding stays selected even if a lower-metric interface returns.
     * When every alternative is cooling, keep probing the existing binding. */
    if(*current_present && (!retry || !best.address.family)) return current;
    return best;
}

void mikun2n_ipv6_close (struct n3n_runtime_data *eee) {
    struct peer_info *peer, *tmp;
    uint64_t now_ms = mikun2n_ipv6_now_ms();
    HASH_ITER(hh, eee->known_peers, peer, tmp) ipv6_diag_summary(peer, now_ms, "socket_close", 1);
    HASH_ITER(hh, eee->pending_peers, peer, tmp) ipv6_diag_summary(peer, now_ms, "socket_close", 1);
    if(eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET)
        closesocket(eee->mikun2n_ipv6_socket);
    eee->mikun2n_ipv6_socket = MIKUN2N_INVALID_SOCKET;
    eee->mikun2n_ipv6_token = 0;
    eee->mikun2n_ipv6_if_index = 0;
    eee->mikun2n_ipv6_bound_ms = 0;
    eee->mikun2n_ipv6_local_ok_ms = 0;
    eee->mikun2n_ipv6_local_fail_ms = 0;
    eee->mikun2n_ipv6_local_last_fail_ms = 0;
    eee->mikun2n_ipv6_local_failures = 0;
    eee->mikun2n_ipv6_mapped_ms = 0;
    eee->mikun2n_ipv6_stun_pending = false;
    eee->mikun2n_ipv6_stun_next_ms = 0;
    memset(&eee->mikun2n_ipv6_mapped_address, 0, sizeof(eee->mikun2n_ipv6_mapped_address));
    memset(&eee->mikun2n_ipv6_address, 0, sizeof(eee->mikun2n_ipv6_address));
}

static SOCKET ipv6_open_local(const ipv6_local_candidate_t *candidate, int *error, const char **reason) {
    *reason = "socket";
    SOCKET fd = socket(AF_INET6, SOCK_DGRAM, 0);
    if(fd == MIKUN2N_INVALID_SOCKET) { *error = ipv6_socket_error(); return fd; }
    int one = 1;
    struct sockaddr_in6 local;
    memset(&local, 0, sizeof(local));
    fill_sockaddr((struct sockaddr *)&local, sizeof(local), &candidate->address);
    *reason = "v6only_option";
    if(setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, (const char *)&one, sizeof(one)) != 0) goto failed;
#ifdef IPV6_DONTFRAG
    *reason = "dontfrag_option";
    if(setsockopt(fd, IPPROTO_IPV6, IPV6_DONTFRAG, (const char *)&one, sizeof(one)) != 0) goto failed;
#else
    *reason = "dontfrag_unavailable";
    *error = 0;
    closesocket(fd);
    return MIKUN2N_INVALID_SOCKET;
#endif
#ifdef _WIN32
#ifdef IPV6_UNICAST_IF
    /* This option takes a host-order IPv6 interface index. Source binding alone
     * is still used on platforms without the explicit egress-interface option. */
    *reason = "interface_option";
    if(setsockopt(fd, IPPROTO_IPV6, IPV6_UNICAST_IF,
                 (const char *)&candidate->if_index, sizeof(candidate->if_index)) != 0) goto failed;
#endif
    u_long nonblocking = 1;
    *reason = "nonblocking";
    if(ioctlsocket(fd, FIONBIO, &nonblocking) != 0) goto failed;
#else
    *reason = "nonblocking";
    if(fcntl(fd, F_SETFL, O_NONBLOCK) != 0) goto failed;
#endif
    *reason = "bind";
    if(bind(fd, (struct sockaddr *)&local, sizeof(local)) != 0) goto failed;
    return fd;
failed:
    *error = ipv6_socket_error();
    closesocket(fd);
    return MIKUN2N_INVALID_SOCKET;
}

static void ipv6_refresh (struct n3n_runtime_data *eee, uint64_t now_ms) {
    struct sockaddr_in v4;
    socklen_t size = sizeof(v4);
    if(getsockname(eee->sock, (struct sockaddr *)&v4, &size) != 0)
        return;
    int retry = ipv6_local_retry_due(eee, now_ms), scan_ok, current_present;
    ipv6_local_candidate_t candidate = ipv6_local_address(eee, ntohs(v4.sin_port), now_ms,
                                                       retry, &scan_ok, &current_present);
    n2n_sock_str_t old_text, new_text;
    if(!scan_ok) {
        traceEvent(TRACE_NORMAL, "MikuN2N IPv6 local_scan_failed monotonic_ms=%llu if_index=%u; existing binding retained",
                   (unsigned long long)now_ms, eee->mikun2n_ipv6_if_index);
        return;
    }
    if(retry) ipv6_interface_failed(eee, eee->mikun2n_ipv6_if_index, now_ms);
    if(candidate.if_index == eee->mikun2n_ipv6_if_index &&
       sock_equal(&candidate.address, &eee->mikun2n_ipv6_address) &&
       (eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET || !candidate.address.family)) return;

    SOCKET replacement = MIKUN2N_INVALID_SOCKET;
    if(candidate.address.family == AF_INET6) {
        int error = 0;
        const char *reason;
        replacement = ipv6_open_local(&candidate, &error, &reason);
        if(replacement == MIKUN2N_INVALID_SOCKET) {
            ipv6_interface_failed(eee, candidate.if_index, now_ms);
            traceEvent(TRACE_NORMAL, "MikuN2N IPv6 local_bind_failed monotonic_ms=%llu if_index=%u address=%s reason=%s error=%d retry_ms=60000; existing binding retained",
                       (unsigned long long)now_ms, candidate.if_index,
                       sock_to_cstr(new_text, &candidate.address), reason, error);
            return;
        }
    }
    const char *reason = !candidate.address.family ? "no_eligible_candidate" :
        !eee->mikun2n_ipv6_address.family ? "initial" : retry ? "sustained_failure" :
        !current_present ? "current_removed" : "address_changed";
    traceEvent(TRACE_NORMAL, "MikuN2N IPv6 local_binding monotonic_ms=%llu reason=%s old_if=%u old=%s new_if=%u new=%s failures=%u failure_age_ms=%llu last_ok_age_ms=%llu",
               (unsigned long long)now_ms, reason, eee->mikun2n_ipv6_if_index,
               sock_to_cstr(old_text, &eee->mikun2n_ipv6_address), candidate.if_index,
               sock_to_cstr(new_text, &candidate.address), eee->mikun2n_ipv6_local_failures,
               (unsigned long long)(eee->mikun2n_ipv6_local_fail_ms ? now_ms - eee->mikun2n_ipv6_local_fail_ms : 0),
               (unsigned long long)(eee->mikun2n_ipv6_local_ok_ms ? now_ms - eee->mikun2n_ipv6_local_ok_ms : 0));
    /* Preparing the replacement first keeps an unusable candidate from revoking
     * a socket that can still carry traffic. Peer proofs belong to the old bind. */
    mikun2n_ipv6_close(eee);
    struct peer_info *peer, *tmp;
    HASH_ITER(hh, eee->pending_peers, peer, tmp) {
        ipv6_diag_reset(peer, now_ms, "local_address_changed");
        peer->mikun2n_ipv6_valid_until_ms = 0;
        peer->mikun2n_ipv6_next_probe_ms = 0;
        peer->mikun2n_ipv6_next_learn_ms = 0;
        peer->mikun2n_ipv6_search_ms = 0;
        peer->mikun2n_ipv6_peer_rx_limit = 0;
        peer->mikun2n_ipv6_large_failures = 0;
        free(peer->mikun2n_ipv6_reassembly);
        peer->mikun2n_ipv6_reassembly = NULL;
        peer->mikun2n_ipv6_probe_bytes = 0;
        peer->mikun2n_ipv6_path_bytes = 0;
        peer->mikun2n_ipv6_peer_ready_until_ms = 0;
        peer->mikun2n_ipv6_ready_report_ms = 0;
        peer->mikun2n_ipv6_attempts = 0;
        peer->mikun2n_ipv6_ready_seen_ms = 0;
        peer->mikun2n_ipv6_query_ms = 0;
        memset(&peer->mikun2n_ipv6_path_address, 0, sizeof(peer->mikun2n_ipv6_path_address));
        memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
    }
    HASH_ITER(hh, eee->known_peers, peer, tmp) {
        ipv6_diag_reset(peer, now_ms, "local_address_changed");
        peer->mikun2n_ipv6_valid_until_ms = 0;
        peer->mikun2n_ipv6_next_probe_ms = 0;
        peer->mikun2n_ipv6_next_learn_ms = 0;
        peer->mikun2n_ipv6_search_ms = 0;
        peer->mikun2n_ipv6_peer_rx_limit = 0;
        peer->mikun2n_ipv6_large_failures = 0;
        free(peer->mikun2n_ipv6_reassembly);
        peer->mikun2n_ipv6_reassembly = NULL;
        peer->mikun2n_ipv6_probe_bytes = 0;
        peer->mikun2n_ipv6_path_bytes = 0;
        peer->mikun2n_ipv6_peer_ready_until_ms = 0;
        peer->mikun2n_ipv6_ready_report_ms = 0;
        peer->mikun2n_ipv6_attempts = 0;
        peer->mikun2n_ipv6_ready_seen_ms = 0;
        peer->mikun2n_ipv6_query_ms = 0;
        memset(&peer->mikun2n_ipv6_path_address, 0, sizeof(peer->mikun2n_ipv6_path_address));
        memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
    }
    if(replacement != MIKUN2N_INVALID_SOCKET) {
        eee->mikun2n_ipv6_socket = replacement;
        eee->mikun2n_ipv6_address = candidate.address;
        eee->mikun2n_ipv6_if_index = candidate.if_index;
        eee->mikun2n_ipv6_bound_ms = now_ms;
        eee->mikun2n_ipv6_token = n3n_rand() | 1;
        traceEvent(TRACE_NORMAL, "MikuN2N experimental IPv6 transport ready");
        V6_DIAG("socket_ready address=%s mtu=%u probe_bytes=%u path_ttl_ms=%u nat66_supported=1 kind=%s",
                sock_to_cstr(new_text, &candidate.address), eee->conf.mtu, eee->conf.mtu + 128, IPV6_PATH_TTL_MS,
                mikun2n_ipv6_public(&candidate.address) ? "global" : "ula_nat66");
    }
    send_query_peer(eee, null_mac);
}

void mikun2n_ipv6_update_peer (struct peer_info *peer, const n2n_PEER_INFO_t *info, uint64_t now_ms) {
    macstr_t mac;
    n2n_sock_str_t candidate, ipv4;
    if(!(info->aflags & N2N_AFLAGS_MIKUN2N_IPV6)) {
        V6_DIAG("candidate_missing peer=%s; peer or supernode may not support IPv6",
                macaddr_str(mac, peer->mac_addr));
        return;
    }
    V6_DIAG("candidate_refresh peer=%s address=%s public=%d session_changed=%d ipv4_preserved=%s candidate_usable=%d token_present=%d",
            macaddr_str(mac, peer->mac_addr), sock_to_cstr(candidate, &info->mikun2n_ipv6_address),
            mikun2n_ipv6_public(&info->mikun2n_ipv6_address),
            peer->mikun2n_ipv6_token != info->mikun2n_ipv6_token, sock_to_cstr(ipv4, &peer->sock),
            mikun2n_ipv6_candidate(&info->mikun2n_ipv6_address), info->mikun2n_ipv6_token != 0);
    uint64_t token = mikun2n_ipv6_candidate(&info->mikun2n_ipv6_address) ? info->mikun2n_ipv6_token : 0;
    // A refreshed rendezvous candidate cannot revoke an independently checked
    // mapping in the same session. A new session must discard all path proof.
    if(peer->mikun2n_ipv6_token != token) {
        ipv6_diag_reset(peer, now_ms, "peer_session_changed");
        peer->mikun2n_ipv6_wire_version = 0;
        peer->mikun2n_ipv6_valid_until_ms = 0;
        memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
        peer->mikun2n_ipv6_next_probe_ms = 0;
        peer->mikun2n_ipv6_next_learn_ms = 0;
        peer->mikun2n_ipv6_search_ms = 0;
        peer->mikun2n_ipv6_peer_rx_limit = 0;
        peer->mikun2n_ipv6_large_failures = 0;
        free(peer->mikun2n_ipv6_reassembly);
        peer->mikun2n_ipv6_reassembly = NULL;
        peer->mikun2n_ipv6_probe_bytes = 0;
        peer->mikun2n_ipv6_path_bytes = 0;
        peer->mikun2n_ipv6_peer_ready_until_ms = 0;
        peer->mikun2n_ipv6_ready_report_ms = 0;
        memset(&peer->mikun2n_ipv6_path_address, 0, sizeof(peer->mikun2n_ipv6_path_address));
        peer->mikun2n_ipv6_attempts = 0;
    }
    peer->mikun2n_ipv6_address = info->mikun2n_ipv6_address;
    peer->mikun2n_ipv6_token = token;
    peer->mikun2n_ipv6_seen_ms = now_ms;
}

static int ipv6_receive_ready (const struct n3n_runtime_data *eee, const struct peer_info *peer, uint64_t now_ms) {
    return eee->conf.mikun2n_ipv6 && eee->conf.allow_p2p &&
           eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET &&
           peer && !peer->force_relay && !peer->local && peer->mikun2n_ipv6_token &&
           mikun2n_ipv6_public(&peer->mikun2n_ipv6_path_address) &&
           peer->mikun2n_ipv6_valid_until_ms > now_ms &&
           now_ms - peer->mikun2n_ipv6_seen_ms < IPV6_REPORT_TTL_MS;
}

int mikun2n_ipv6_active (const struct n3n_runtime_data *eee, const struct peer_info *peer, uint64_t now_ms) {
    return ipv6_receive_ready(eee, peer, now_ms) && peer->mikun2n_ipv6_peer_ready_until_ms > now_ms;
}

/* Scheduled at send time for the case where this probe is lost; a matching PONG
 * reschedules from its own arrival. A fixed 2 s keepalive against a 6.5 s lease
 * that is also chained through the peer's probes tolerated about two consecutive
 * losses: a 15-hour log with ~5% probe loss expired readiness 65 times while the
 * path itself stayed usable. Near the end of either lease probe every 500 ms, so
 * an expiry needs several consecutive losses. Steady-state cadence and the wire
 * format are unchanged, so older generation-3 peers interoperate. */
static uint64_t ipv6_next_keepalive (const struct n3n_runtime_data *eee, const struct peer_info *peer,
                                     uint64_t now_ms) {
    if(!ipv6_receive_ready(eee, peer, now_ms))
        return now_ms + (peer->mikun2n_ipv6_attempts < 6 ? IPV6_FAST_PROBE_MS : 10000);
    uint64_t lease = peer->mikun2n_ipv6_valid_until_ms;
    // A peer that never granted readiness must not keep us probing at the fast rate.
    if(peer->mikun2n_ipv6_ready_seen_ms && now_ms - peer->mikun2n_ipv6_ready_seen_ms < IPV6_READY_RECENT_MS &&
       peer->mikun2n_ipv6_peer_ready_until_ms < lease)
        lease = peer->mikun2n_ipv6_peer_ready_until_ms;
    uint64_t next = now_ms + IPV6_KEEPALIVE_MS;
    if(lease < next + IPV6_LEASE_MARGIN_MS)
        next = max(now_ms + IPV6_FAST_PROBE_MS, lease > IPV6_LEASE_MARGIN_MS ? lease - IPV6_LEASE_MARGIN_MS : 0);
    return next;
}

/* Without an answer from the supernode or any packet from the peer, the entry is
 * most likely an identity that already left (every reconnect registers a new MAC). */
static int ipv6_peer_silent (const struct peer_info *peer, uint64_t now_ms) {
    uint64_t heard = peer->mikun2n_peer_info_ms;
    uint64_t seen_ms = (uint64_t)(time(NULL) - (peer->last_seen ? peer->last_seen : peer->time_alloc)) * 1000;
    if(!heard)
        return seen_ms >= IPV6_PEER_SILENT_MS;
    return now_ms - heard >= IPV6_PEER_SILENT_MS && seen_ms >= IPV6_PEER_SILENT_MS;
}

static int ipv6_local_check_peer(const struct n3n_runtime_data *eee, const struct peer_info *peer,
                                 uint64_t now_ms) {
    return !peer->force_relay && !peer->local && !is_null_mac(peer->mac_addr) &&
           !is_multi_broadcast(peer->mac_addr) && memcmp(peer->mac_addr, eee->device.mac_addr, N2N_MAC_SIZE) &&
           peer->mikun2n_ipv6_token && now_ms - peer->mikun2n_ipv6_seen_ms < IPV6_REPORT_TTL_MS &&
           (!peer->mikun2n_ipv6_wire_version || peer->mikun2n_ipv6_wire_version == MIKUN2N_IPV6_WIRE_VERSION) &&
           !ipv6_peer_silent(peer, now_ms);
}

static int ipv6_message_too_large (int error) {
#ifdef _WIN32
    return error == WSAEMSGSIZE;
#else
    return error == EMSGSIZE;
#endif
}

static int ipv6_transient_error(int error) {
#ifdef _WIN32
    return error == WSAEWOULDBLOCK || error == WSAENOBUFS || error == WSAEINTR;
#else
    return error == EAGAIN || error == EWOULDBLOCK || error == ENOBUFS || error == EINTR;
#endif
}

static void ipv6_lower_probe (struct peer_info *peer, uint64_t now_ms, const char *reason) {
    macstr_t mac;
    V6_DIAG("tx_size_reduced peer=%s old_bytes=%u bytes=%u reason=%s path_preserved=1",
            macaddr_str(mac, peer->mac_addr), peer->mikun2n_ipv6_path_bytes, IPV6_SAFE_UDP_SIZE, reason);
    peer->mikun2n_ipv6_path_bytes = IPV6_SAFE_UDP_SIZE;
    peer->mikun2n_ipv6_large_failures = 0;
    peer->mikun2n_ipv6_search_ms = now_ms + 60000;
    for(int i = 0; i < MIKUN2N_IPV6_PROBES; ++i)
        if(peer->mikun2n_ipv6_probes[i].bytes > IPV6_SAFE_UDP_SIZE)
            peer->mikun2n_ipv6_probes[i].challenge = 0;
}

static int ipv6_probe_send (struct n3n_runtime_data *eee, struct peer_info *peer,
                            const n2n_sock_t *destination, uint64_t now_ms, size_t size, const char *reason) {
    if(!mikun2n_ipv6_public(destination))
        return 0;
    uint8_t packet[N2N_PKT_BUF_SIZE] = {0};
    if(size < IPV6_CONTROL_SIZE || size > sizeof(packet))
        return 0;
    // Triggered checks share the window without replacing an outstanding challenge.
    mikun2n_ipv6_probe_t *probe = NULL;
    for(int i = 0; i < MIKUN2N_IPV6_PROBES; i++) {
        unsigned index = peer->mikun2n_ipv6_probe_index++ % MIKUN2N_IPV6_PROBES;
        if(!peer->mikun2n_ipv6_probes[index].challenge ||
           now_ms - peer->mikun2n_ipv6_probes[index].sent_ms > IPV6_PATH_TTL_MS) {
            probe = &peer->mikun2n_ipv6_probes[index];
            break;
        }
    }
    if(!probe)
        return 0;
    probe->challenge = n3n_rand() | 1;
    probe->sent_ms = now_ms;
    probe->destination = *destination;
    probe->bytes = (uint16_t)size;
    ipv6_header(eee, peer, packet, IPV6_PING, probe->challenge);
    packet[43] = IPV6_RX_LIMIT >> 8;
    packet[44] = IPV6_RX_LIMIT & 0xff;
    ssize_t sent = ipv6_send_raw(eee, destination, packet, size);
    macstr_t mac;
    n2n_sock_str_t text;
    if(sent != (ssize_t)size) {
        int error = sent < 0 ? ipv6_socket_error() : 0;
        probe->challenge = 0;
        if(ipv6_message_too_large(error) && size > IPV6_SAFE_UDP_SIZE)
            ipv6_lower_probe(peer, now_ms, "send_message_too_large");
        else if(!ipv6_transient_error(error) && size <= IPV6_SAFE_UDP_SIZE) {
            if(!ipv6_message_too_large(error) && ipv6_local_check_peer(eee, peer, now_ms))
                ipv6_local_failed(eee, now_ms);
            peer->mikun2n_ipv6_valid_until_ms = 0;
            peer->mikun2n_ipv6_next_probe_ms = now_ms + 10000;
            peer->mikun2n_ipv6_next_learn_ms = now_ms + 10000;
        }
        V6_DIAG("probe_send_failed peer=%s address=%s reason=%s error=%d",
                macaddr_str(mac, peer->mac_addr), sock_to_cstr(text, destination), reason, error);
        return 0;
    }
    V6_DIAG("ping_sent peer=%s address=%s probe_slot=%u bytes=%u reason=%s",
            macaddr_str(mac, peer->mac_addr), sock_to_cstr(text, destination),
            (unsigned)(probe - peer->mikun2n_ipv6_probes), (unsigned)size, reason);
    return 1;
}

static void ipv6_tick_peers (struct n3n_runtime_data *eee, struct peer_info *peers, uint64_t now_ms) {
    struct peer_info *peer, *tmp;
    HASH_ITER(hh, peers, peer, tmp) {
        macstr_t mac;
        if(is_null_mac(peer->mac_addr) || (peer->mac_addr[0] & 0x01) ||
           !memcmp(peer->mac_addr, eee->device.mac_addr, N2N_MAC_SIZE))
            continue;
        ipv6_diag_summary(peer, now_ms, "periodic", 0);
        if(peer->force_relay || peer->local) {
            if(peer->mikun2n_ipv6_valid_until_ms)
                V6_DIAG("path_disabled peer=%s reason=%s", macaddr_str(mac, peer->mac_addr),
                        peer->force_relay ? "forced_relay" : "local_peer");
            peer->mikun2n_ipv6_valid_until_ms = 0;
            continue;
        }
        if(peer->mikun2n_ipv6_valid_until_ms &&
           (now_ms >= peer->mikun2n_ipv6_valid_until_ms || now_ms - peer->mikun2n_ipv6_seen_ms >= IPV6_REPORT_TTL_MS)) {
            V6_DIAG("path_expired peer=%s candidate_age_ms=%llu; using IPv4",
                    macaddr_str(mac, peer->mac_addr), (unsigned long long)(now_ms - peer->mikun2n_ipv6_seen_ms));
            peer->mikun2n_ipv6_valid_until_ms = 0;
        }
        if(peer->mikun2n_ipv6_peer_ready_until_ms && now_ms >= peer->mikun2n_ipv6_peer_ready_until_ms) {
            peer->mikun2n_ipv6_peer_ready_until_ms = 0;
            V6_DIAG("peer_readiness_expired peer=%s; DATA uses IPv4", macaddr_str(mac, peer->mac_addr));
        }
        if(now_ms >= peer->mikun2n_ipv6_query_ms) {
            int active = mikun2n_ipv6_active(eee, peer, now_ms);
            int silent = !active && ipv6_peer_silent(peer, now_ms);
            V6_DIAG("query peer=%s active=%d silent=%d candidate_age_ms=%llu", macaddr_str(mac, peer->mac_addr),
                    active, silent,
                    (unsigned long long)(peer->mikun2n_ipv6_seen_ms ? now_ms - peer->mikun2n_ipv6_seen_ms : 0));
            // An active path still refreshes its 30 s candidate three times per TTL.
            peer->mikun2n_ipv6_query_ms = now_ms +
                (active ? IPV6_QUERY_ACTIVE_MS : silent ? IPV6_QUERY_SILENT_MS : IPV6_QUERY_MS);
            send_query_peer(eee, peer->mac_addr);
        }
        if(!peer->mikun2n_ipv6_token ||
           now_ms - peer->mikun2n_ipv6_seen_ms >= IPV6_REPORT_TTL_MS)
            continue;
        for(int i = 0; i < MIKUN2N_IPV6_PROBES; i++) {
            mikun2n_ipv6_probe_t *probe = &peer->mikun2n_ipv6_probes[i];
            if(probe->challenge && now_ms - probe->sent_ms > IPV6_PATH_TTL_MS) {
                probe->challenge = 0;
                if(probe->bytes <= IPV6_SAFE_UDP_SIZE && ipv6_local_check_peer(eee, peer, now_ms))
                    ipv6_local_failed(eee, now_ms);
                if(probe->bytes > IPV6_SAFE_UDP_SIZE && ++peer->mikun2n_ipv6_large_failures >= 3)
                    ipv6_lower_probe(peer, now_ms, "three_large_probe_losses");
            }
        }
        if(ipv6_receive_ready(eee, peer, now_ms) && now_ms >= peer->mikun2n_ipv6_search_ms) {
            unsigned goal = min(IPV6_RX_LIMIT, eee->conf.mtu + 128);
            if(peer->mikun2n_ipv6_peer_rx_limit) goal = min(goal, peer->mikun2n_ipv6_peer_rx_limit);
            peer->mikun2n_ipv6_search_ms = now_ms + 10000;
            if(goal > IPV6_SAFE_UDP_SIZE)
                ipv6_probe_send(eee, peer, &peer->mikun2n_ipv6_path_address, now_ms, goal, "size_search");
        }
        if(now_ms < peer->mikun2n_ipv6_next_probe_ms)
            continue;
        const n2n_sock_t *destination = ipv6_receive_ready(eee, peer, now_ms) ||
                                      !mikun2n_ipv6_public(&peer->mikun2n_ipv6_address)
                                        ? &peer->mikun2n_ipv6_path_address : &peer->mikun2n_ipv6_address;
        if(!mikun2n_ipv6_public(destination)) {
            V6_DIAG("waiting_public_probe peer=%s; ULA candidate has no checked public mapping",
                    macaddr_str(mac, peer->mac_addr));
            peer->mikun2n_ipv6_next_probe_ms = now_ms + 10000;
            continue;
        }
        peer->mikun2n_ipv6_next_probe_ms = now_ms + IPV6_FAST_PROBE_MS;
        if(!ipv6_probe_send(eee, peer, destination, now_ms, IPV6_SAFE_UDP_SIZE, "base_keepalive"))
            continue;
        peer->mikun2n_ipv6_next_probe_ms = ipv6_next_keepalive(eee, peer, now_ms);
        if(peer->mikun2n_ipv6_attempts < 6)
            peer->mikun2n_ipv6_attempts++;
    }
}

void mikun2n_ipv6_tick (struct n3n_runtime_data *eee, uint64_t now_ms) {
    if(!eee->conf.mikun2n_ipv6 || !eee->conf.allow_p2p || eee->conf.connect_tcp || !eee->last_sup)
        return;
    if(now_ms >= eee->mikun2n_ipv6_refresh_ms) {
        V6_DIAG("summary monotonic_ms=%llu tx_packets=%llu tx_bytes=%llu rx_packets=%llu rx_bytes=%llu reject_header=%llu reject_peer=%llu reject_pong=%llu reject_inactive=%llu tx_fragments=%llu rx_fragments=%llu reassembled=%llu transient_drops=%llu",
                (unsigned long long)now_ms, (unsigned long long)mikun2n_v6_diag_read(&diag_tx_data),
                (unsigned long long)mikun2n_v6_diag_read(&diag_tx_bytes),
                (unsigned long long)diag_rx_data, (unsigned long long)diag_rx_bytes,
                (unsigned long long)diag_reject_header, (unsigned long long)diag_reject_peer,
                (unsigned long long)diag_reject_pong, (unsigned long long)diag_reject_inactive,
                (unsigned long long)mikun2n_v6_diag_read(&diag_tx_fragments), (unsigned long long)diag_rx_fragments,
                (unsigned long long)diag_reassembled, (unsigned long long)mikun2n_v6_diag_read(&diag_transient_drops));
        eee->mikun2n_ipv6_refresh_ms = now_ms + 10000;
        ipv6_refresh(eee, now_ms);
    }
    if(eee->mikun2n_ipv6_socket == MIKUN2N_INVALID_SOCKET)
        return;
    ipv6_stun_tick(eee, now_ms);
    ipv6_tick_peers(eee, eee->pending_peers, now_ms);
    ipv6_tick_peers(eee, eee->known_peers, now_ms);
}

static uint64_t ipv6_next_datagram_id (struct n3n_runtime_data *eee) {
    /* On Windows, TAP sends and assistance forwarding on the receive thread
     * share this session-wide ID space. A duplicate can join unrelated fragments. */
#ifdef _WIN32
    return (uint64_t)InterlockedIncrement64((volatile LONG64 *)&eee->mikun2n_ipv6_datagram_id);
#else
    return ++eee->mikun2n_ipv6_datagram_id;
#endif
}

int mikun2n_ipv6_send_ex (struct n3n_runtime_data *eee, struct peer_info *peer,
                         const uint8_t *data, size_t size, uint64_t now_ms, int *fully_sent) {
    uint8_t packet[N2N_PKT_BUF_SIZE];
    if(fully_sent) *fully_sent = 0;
    mikun2n_ipv6_diag_t *d = ipv6_diag_bind(eee, peer, now_ms);
    if(d) V6_COUNT(d, tx_attempted);
    if(!mikun2n_ipv6_active(eee, peer, now_ms)) {
        if(d) { V6_COUNT(d, tx_inactive); V6_COUNT(d, revision); }
        return 0;
    }
    if(!size || size > N2N_PKT_BUF_SIZE) { V6_COUNT(d, tx_invalid_size); V6_COUNT(d, revision); return 0; }
    mikun2n_v6_diag_max(&d->tx_datagram_highwater, size);
    unsigned budget = min(peer->mikun2n_ipv6_path_bytes, IPV6_RX_LIMIT);
    if(budget < IPV6_SAFE_UDP_SIZE) { V6_COUNT(d, tx_budget); V6_COUNT(d, revision); return 0; }
    uint64_t id = 0;
    size_t offset = 0;
    int fragmented = size + IPV6_FRAME_SIZE > budget;
    if(fragmented && now_ms >= peer->mikun2n_ipv6_oversize_log_ms) {
        macstr_t mac;
        V6_DIAG("data_fragmented peer=%s n3n_bytes=%u tx_limit=%u; IPv6 path preserved",
                macaddr_str(mac, peer->mac_addr), (unsigned)size, budget);
        peer->mikun2n_ipv6_oversize_log_ms = now_ms + 10000;
    }
    while(offset < size) {
        // Allocate only for fragments, including a DATA send retried after EMSGSIZE.
        if(fragmented && !id) {
            id = ipv6_next_datagram_id(eee);
            if(!id) id = ipv6_next_datagram_id(eee);
            mikun2n_v6_diag_min(&d->tx_id_min, id);
            mikun2n_v6_diag_max(&d->tx_id_max, id);
        }
        unsigned header = fragmented ? IPV6_FRAGMENT_HEADER : IPV6_FRAME_SIZE;
        size_t chunk = min(size - offset, budget - header);
        ipv6_header(eee, peer, packet, fragmented ? IPV6_FRAGMENT : IPV6_DATA, fragmented ? id : 0);
        if(fragmented) {
            packet[41] = size >> 8; packet[42] = size & 0xff;
            packet[43] = offset >> 8; packet[44] = offset & 0xff;
        }
        memcpy(packet + header, data + offset, chunk);
        ssize_t sent = ipv6_send_raw(eee, &peer->mikun2n_ipv6_path_address, packet, chunk + header);
        if(sent != (ssize_t)(chunk + header)) {
            int error = ipv6_socket_error();
            V6_COUNT(d, tx_udp_failed);
            __atomic_store_n(&d->tx_last_error, (unsigned)error, __ATOMIC_RELAXED);
            if(ipv6_message_too_large(error)) V6_COUNT(d, tx_emsgsize);
            if(ipv6_message_too_large(error) && budget > IPV6_SAFE_UDP_SIZE) {
                ipv6_lower_probe(peer, now_ms, "data_message_too_large");
                if(!offset) { budget = IPV6_SAFE_UDP_SIZE; fragmented = 1; continue; }
            }
            macstr_t mac;
            V6_DIAG("data_send_failed peer=%s bytes=%u error=%d partial=%d transient=%d",
                    macaddr_str(mac, peer->mac_addr), (unsigned)size, error, offset != 0, ipv6_transient_error(error));
            // A transient queue failure is UDP loss, not evidence of a dead path.
            // Do not replay a partly emitted datagram through a different route.
            V6_COUNT(d, tx_failed);
            __atomic_store_n(&d->tx_last_failure_id, id, __ATOMIC_RELAXED);
            __atomic_store_n(&d->tx_last_failure_offset, offset, __ATOMIC_RELAXED);
            __atomic_store_n(&d->tx_last_failure_size, size, __ATOMIC_RELAXED);
            if(offset) V6_COUNT(d, tx_partial);
            if(offset || ipv6_transient_error(error)) V6_COUNT(d, tx_consumed_drop);
            if(ipv6_transient_error(error)) {
                V6_COUNT(d, tx_transient);
                mikun2n_v6_diag_add(&diag_transient_drops, 1);
                V6_COUNT(d, revision);
                return 1;
            }
            V6_COUNT(d, revision);
            if(!ipv6_message_too_large(error) || budget <= IPV6_SAFE_UDP_SIZE)
                peer->mikun2n_ipv6_valid_until_ms = 0;
            return offset ? 1 : 0;
        }
        V6_COUNT(d, tx_udp);
        mikun2n_v6_diag_add(&d->tx_udp_bytes, chunk + header);
        mikun2n_v6_diag_max(&d->tx_udp_highwater, chunk + header);
        if(fragmented) { mikun2n_v6_diag_add(&diag_tx_fragments, 1); V6_COUNT(d, tx_fragments); }
        offset += chunk;
    }
    mikun2n_v6_diag_add(&diag_tx_data, 1);
    mikun2n_v6_diag_add(&diag_tx_bytes, size);
    V6_COUNT(d, tx_full);
    mikun2n_v6_diag_add(&d->tx_full_bytes, size);
    V6_COUNT(d, revision);
    if(fully_sent) *fully_sent = 1;
    return 1;
}

int mikun2n_ipv6_send (struct n3n_runtime_data *eee, struct peer_info *peer,
                      const uint8_t *data, size_t size, uint64_t now_ms) {
    return mikun2n_ipv6_send_ex(eee, peer, data, size, now_ms, NULL);
}

size_t mikun2n_ipv6_unwrap (struct n3n_runtime_data *eee, const n2n_sock_t *sender,
                           uint8_t *data, size_t size, uint64_t now_ms) {
    if(eee->conf.mikun2n_ipv6 && eee->conf.allow_p2p &&
       ipv6_stun_receive(eee, sender, data, size, now_ms))
        return 0;
    if(!eee->conf.mikun2n_ipv6 || !eee->conf.allow_p2p || !eee->mikun2n_ipv6_token ||
       !mikun2n_ipv6_public(sender) || size < IPV6_FRAME_SIZE ||
       size > IPV6_RX_LIMIT || memcmp(data, ipv6_magic, 3) ||
       memcmp(data + 11, eee->device.mac_addr, N2N_MAC_SIZE) ||
       ipv6_get_u64(data + 25) != eee->mikun2n_ipv6_token) {
        diag_reject_header++;
        /* Only attribute a malformed frame when its claimed MAC and source
         * endpoint already identify a known peer. Never log session tokens. */
        if(size >= IPV6_FRAME_SIZE && !memcmp(data, ipv6_magic, 3)) {
            struct peer_info *claimed = ipv6_peer(eee, data + 5);
            if(claimed && (sock_equal(sender, &claimed->mikun2n_ipv6_path_address) ||
                           sock_equal(sender, &claimed->mikun2n_ipv6_address))) {
                mikun2n_ipv6_diag_t *claimed_diag = ipv6_diag_bind(eee, claimed, now_ms);
                V6_COUNT(claimed_diag, reject_header);
                V6_COUNT(claimed_diag, revision);
            }
        }
        return 0;
    }
    struct peer_info *peer = ipv6_peer(eee, data + 5);
    mikun2n_ipv6_diag_t *d = ipv6_diag_bind(eee, peer, now_ms);
    if(d) V6_COUNT(d, revision);
    if(!peer || peer->force_relay || peer->local || !peer->mikun2n_ipv6_token ||
       ipv6_get_u64(data + 17) != peer->mikun2n_ipv6_token ||
       now_ms - peer->mikun2n_ipv6_seen_ms >= IPV6_REPORT_TTL_MS) {
        diag_reject_peer++;
        if(d) {
            if(peer->force_relay || peer->local) V6_COUNT(d, reject_policy);
            else if(!peer->mikun2n_ipv6_token || ipv6_get_u64(data + 17) != peer->mikun2n_ipv6_token)
                V6_COUNT(d, reject_session);
            else V6_COUNT(d, reject_candidate);
        }
        return 0;
    }
    macstr_t mac;
    n2n_sock_str_t source;
    peer->mikun2n_ipv6_wire_version = data[3];
    if(data[3] != ipv6_magic[3]) {
        V6_COUNT(d, reject_version);
        if(now_ms >= peer->mikun2n_ipv6_legacy_log_ms) {
            V6_DIAG("version_mismatch peer=%s remote=%u local=%u; preserving IPv4",
                    macaddr_str(mac, peer->mac_addr), data[3], ipv6_magic[3]);
            peer->mikun2n_ipv6_legacy_log_ms = now_ms + 10000;
        }
        return 0;
    }
    uint8_t kind = data[4];
    uint64_t challenge = ipv6_get_u64(data + 33);
    if(kind == IPV6_DATA || kind == IPV6_FRAGMENT) {
        V6_COUNT(d, rx_udp);
        mikun2n_v6_diag_add(&d->rx_udp_bytes, size);
        mikun2n_v6_diag_max(&d->rx_udp_highwater, size);
        if(kind == IPV6_FRAGMENT) {
            V6_COUNT(d, rx_fragments);
            if(challenge) {
                mikun2n_v6_diag_min(&d->rx_id_min, challenge);
                mikun2n_v6_diag_max(&d->rx_id_max, challenge);
            }
        }
    } else V6_COUNT(d, rx_control);
    if(kind == IPV6_PING && challenge && size >= IPV6_CONTROL_SIZE) {
        if(size > IPV6_RX_LIMIT || ipv6_read_u16(data + 43) < IPV6_SAFE_UDP_SIZE) {
            V6_COUNT(d, reject_control); return 0;
        }
        uint16_t ready_ms = ipv6_receive_ready(eee, peer, now_ms) &&
                            sock_equal(sender, &peer->mikun2n_ipv6_path_address)
                              ? (uint16_t)(peer->mikun2n_ipv6_valid_until_ms - now_ms) : 0;
        if(ready_ms) {
            uint64_t candidate_ms = IPV6_REPORT_TTL_MS - (now_ms - peer->mikun2n_ipv6_seen_ms);
            if(candidate_ms < ready_ms)
                ready_ms = (uint16_t)candidate_ms;
        }
        ipv6_header(eee, peer, data, IPV6_PONG, challenge);
        data[41] = ready_ms >> 8;
        data[42] = ready_ms & 0xff;
        data[43] = IPV6_RX_LIMIT >> 8;
        data[44] = IPV6_RX_LIMIT & 0xff;
        data[45] = size >> 8;
        data[46] = size & 0xff;
        ssize_t sent = ipv6_send_raw(eee, sender, data, IPV6_CONTROL_SIZE);
        int error = sent < 0 ? ipv6_socket_error() : 0;
        V6_DIAG("ping_received peer=%s from=%s bytes=%u pong_sent=%d error=%d receive_ready_ms=%u",
                macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender), (unsigned)size,
                sent == IPV6_CONTROL_SIZE, error, ready_ms);
        // A session-bound PING can propose a NAT mapping, but only our own
        // address-bound challenge response can make that mapping a data path.
        if(now_ms >= peer->mikun2n_ipv6_next_learn_ms &&
           (!ipv6_receive_ready(eee, peer, now_ms) ||
            !sock_equal(sender, &peer->mikun2n_ipv6_path_address))) {
            int pending = 0;
            for(int i = 0; i < MIKUN2N_IPV6_PROBES; i++)
                if(peer->mikun2n_ipv6_probes[i].challenge &&
                   now_ms - peer->mikun2n_ipv6_probes[i].sent_ms <= IPV6_PATH_TTL_MS &&
                   sock_equal(sender, &peer->mikun2n_ipv6_probes[i].destination))
                    pending = 1;
            peer->mikun2n_ipv6_next_learn_ms = now_ms + 500;
            if(!pending) {
                V6_DIAG("source_check_started peer=%s from=%s differs_from_report=%d",
                        macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender),
                        !sock_equal(sender, &peer->mikun2n_ipv6_address));
                ipv6_probe_send(eee, peer, sender, now_ms, IPV6_SAFE_UDP_SIZE, "peer_reflexive");
            }
        }
    } else if(kind == IPV6_PONG && challenge && size == IPV6_CONTROL_SIZE) {
        mikun2n_ipv6_probe_t *probe = NULL;
        for(int i = 0; i < MIKUN2N_IPV6_PROBES; i++) {
            if(peer->mikun2n_ipv6_probes[i].challenge == challenge &&
               ipv6_read_u16(data + 45) == peer->mikun2n_ipv6_probes[i].bytes &&
               now_ms - peer->mikun2n_ipv6_probes[i].sent_ms <= IPV6_PATH_TTL_MS &&
               sock_equal(sender, &peer->mikun2n_ipv6_probes[i].destination)) {
                probe = &peer->mikun2n_ipv6_probes[i];
                break;
            }
        }
        if(!probe) {
            V6_COUNT(d, reject_pong);
            diag_reject_pong++;
            V6_DIAG("pong_rejected peer=%s from=%s reason=challenge_size_age_or_endpoint",
                    macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender));
            return 0;
        }
        int was_active = mikun2n_ipv6_active(eee, peer, now_ms);
        uint16_t ready_ms = ipv6_read_u16(data + 41);
        uint16_t rx_limit = ipv6_read_u16(data + 43);
        if(ready_ms > IPV6_PATH_TTL_MS || rx_limit < IPV6_SAFE_UDP_SIZE || rx_limit > IPV6_RX_LIMIT) {
            V6_COUNT(d, reject_control);
            diag_reject_pong++;
            return 0;
        }
        ipv6_local_confirmed(eee, now_ms);
        int changed = !sock_equal(sender, &peer->mikun2n_ipv6_path_address);
        peer->mikun2n_ipv6_path_address = *sender;
        peer->mikun2n_ipv6_valid_until_ms = now_ms + IPV6_PATH_TTL_MS;
        peer->mikun2n_ipv6_rtt_ms = (uint32_t)(now_ms - probe->sent_ms);
        if(changed || !peer->mikun2n_ipv6_path_bytes)
            peer->mikun2n_ipv6_path_bytes = probe->bytes;
        else if(probe->bytes > peer->mikun2n_ipv6_path_bytes)
            peer->mikun2n_ipv6_path_bytes = probe->bytes;
        peer->mikun2n_ipv6_peer_rx_limit = rx_limit;
        peer->mikun2n_ipv6_path_bytes = min(peer->mikun2n_ipv6_path_bytes, rx_limit);
        if(probe->bytes > IPV6_SAFE_UDP_SIZE) peer->mikun2n_ipv6_large_failures = 0;
        if(changed || probe->sent_ms >= peer->mikun2n_ipv6_ready_report_ms) {
            peer->mikun2n_ipv6_ready_report_ms = probe->sent_ms;
            // Starting the lease at request-send time conservatively includes RTT.
            peer->mikun2n_ipv6_peer_ready_until_ms = ready_ms ? probe->sent_ms + ready_ms : 0;
            if(ready_ms)
                peer->mikun2n_ipv6_ready_seen_ms = now_ms;
        }
        int active = mikun2n_ipv6_active(eee, peer, now_ms);
        if(active && !was_active)
            V6_DIAG("data_path_ready peer=%s checked_bytes=%u; both endpoints receive DATA",
                    macaddr_str(mac, peer->mac_addr), peer->mikun2n_ipv6_path_bytes);
        V6_DIAG("pong_matched peer=%s from=%s rtt_ms=%u probe_slot=%u ttl_ms=%u checked_bytes=%u peer_ready_ms=%u data_ready=%d",
                macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender), peer->mikun2n_ipv6_rtt_ms,
                (unsigned)(probe - peer->mikun2n_ipv6_probes), IPV6_PATH_TTL_MS, peer->mikun2n_ipv6_path_bytes, ready_ms, active);
        probe->challenge = 0;
        if(changed) {
            ipv6_diag_reset(peer, now_ms, "path_selected");
            if(peer->mikun2n_ipv6_reassembly)
                memset(peer->mikun2n_ipv6_reassembly->slots, 0, sizeof(peer->mikun2n_ipv6_reassembly->slots));
            peer->mikun2n_ipv6_search_ms = now_ms + 1000;
            peer->mikun2n_ipv6_large_failures = 0;
            // Late replies to the old mapping must not switch the route back.
            memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
            V6_DIAG("path_selected peer=%s address=%s kind=%s ttl_ms=%u",
                    macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender),
                    sock_equal(sender, &peer->mikun2n_ipv6_address) ? "advertised" : "peer_reflexive",
                    IPV6_PATH_TTL_MS);
        }
        // A fresh lease replaces the pessimistic send-time schedule.
        uint64_t next_check = active ? ipv6_next_keepalive(eee, peer, now_ms) : now_ms + IPV6_FAST_PROBE_MS;
        if(active || peer->mikun2n_ipv6_next_probe_ms > next_check)
            peer->mikun2n_ipv6_next_probe_ms = next_check;
        peer->last_seen = time(NULL);
    } else if(kind == IPV6_FRAGMENT && size > IPV6_FRAGMENT_HEADER &&
              ipv6_receive_ready(eee, peer, now_ms) && sock_equal(sender, &peer->mikun2n_ipv6_path_address)) {
        diag_rx_fragments++;
        uint16_t total = ipv6_read_u16(data + 41), offset = ipv6_read_u16(data + 43);
        if(!challenge || !total || total > N2N_PKT_BUF_SIZE || offset >= total ||
           size - IPV6_FRAGMENT_HEADER > (size_t)(total - offset)) {
            V6_COUNT(d, frag.invalid);
            mikun2n_ipv6_fragment_diag_reject(&d->frag, challenge, offset, size - IPV6_FRAGMENT_HEADER);
            return 0;
        }
        mikun2n_v6_diag_max(&d->rx_datagram_highwater, total);
        if(!peer->mikun2n_ipv6_reassembly) {
            peer->mikun2n_ipv6_reassembly = calloc(1, sizeof(*peer->mikun2n_ipv6_reassembly));
            if(!peer->mikun2n_ipv6_reassembly) {
                V6_COUNT(d, reject_alloc);
                static uint64_t allocation_warning_ms;
                if(now_ms >= allocation_warning_ms) {
                    traceEvent(TRACE_WARNING, "IPv6 fragment dropped: cannot allocate reassembly buffer");
                    allocation_warning_ms = now_ms + 10000;
                }
                return 0;
            }
        }
        size_t assembled = mikun2n_ipv6_reassemble_ex(peer->mikun2n_ipv6_reassembly, now_ms,
                challenge, ipv6_read_u16(data + 41), ipv6_read_u16(data + 43),
                data + IPV6_FRAGMENT_HEADER, size - IPV6_FRAGMENT_HEADER, data, &d->frag);
        if(assembled) {
            diag_reassembled++; diag_rx_data++; diag_rx_bytes += assembled;
            V6_COUNT(d, rx_data);
            mikun2n_v6_diag_add(&d->rx_bytes, assembled);
        }
        return assembled;
    } else if(kind == IPV6_DATA && ipv6_receive_ready(eee, peer, now_ms) &&
              sock_equal(sender, &peer->mikun2n_ipv6_path_address)) {
        size -= IPV6_FRAME_SIZE;
        diag_rx_data++;
        diag_rx_bytes += size;
        V6_COUNT(d, rx_data);
        mikun2n_v6_diag_add(&d->rx_bytes, size);
        mikun2n_v6_diag_max(&d->rx_datagram_highwater, size);
        memmove(data, data + IPV6_FRAME_SIZE, size);
        return size;
    }
    else {
        diag_reject_inactive++;
        if(kind == IPV6_DATA || kind == IPV6_FRAGMENT) {
            if(!ipv6_receive_ready(eee, peer, now_ms)) V6_COUNT(d, reject_readiness);
            else if(!sock_equal(sender, &peer->mikun2n_ipv6_path_address)) V6_COUNT(d, reject_endpoint);
            else {
                V6_COUNT(d, frag.invalid);
                mikun2n_ipv6_fragment_diag_reject(&d->frag, challenge, 0, size);
            }
        } else V6_COUNT(d, reject_control);
    }
    return 0;
}

int mikun2n_ipv6_accept (struct n3n_runtime_data *eee, const n2n_mac_t mac,
                        const n2n_sock_t *sender, uint64_t now_ms) {
    struct peer_info *peer = ipv6_peer(eee, mac);
    if(!ipv6_receive_ready(eee, peer, now_ms) || !sock_equal(sender, &peer->mikun2n_ipv6_path_address)) {
        mikun2n_ipv6_diag_t *d = ipv6_diag_bind(eee, peer, now_ms);
        if(d) {
            if(!ipv6_receive_ready(eee, peer, now_ms)) V6_COUNT(d, reject_readiness);
            else V6_COUNT(d, reject_endpoint);
            V6_COUNT(d, revision);
        }
        return 0;
    }
    peer->last_seen = time(NULL);
    return 1;
}
