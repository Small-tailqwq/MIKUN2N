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
static const uint8_t ipv6_magic[4] = {'M', 'K', '6', MIKUN2N_IPV6_WIRE_VERSION};

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
    // The launcher resolves optional observer hostnames outside the edge loop.
    while(setting && *setting && count < 2) {
        const char *end = strchr(setting, ';');
        size_t len = end ? (size_t)(end - setting) : strlen(setting);
        char text[64];
        if(len && len < sizeof(text)) {
            memcpy(text, setting, len);
            text[len] = 0;
            n2n_sock_t address = {0};
            address.family = AF_INET6;
            address.port = 3478;
            if(inet_pton(AF_INET6, text, address.addr.v6) == 1 && mikun2n_ipv6_public(&address))
                eee->mikun2n_ipv6_stun_servers[count++] = address;
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
        n2n_sock_str_t text;
        V6_DIAG("mapping_timeout observer=%s", sock_to_cstr(text,
                &eee->mikun2n_ipv6_stun_servers[eee->mikun2n_ipv6_stun_index]));
        eee->mikun2n_ipv6_stun_pending = false;
        if(eee->mikun2n_ipv6_stun_servers[1].family)
            eee->mikun2n_ipv6_stun_index ^= 1;
    }
    if(eee->mikun2n_ipv6_stun_pending || now_ms < eee->mikun2n_ipv6_stun_next_ms)
        return;
    uint8_t request[20] = {0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42};
    if(!ipv6_stun_random(request + 8, 12)) {
        eee->mikun2n_ipv6_stun_next_ms = now_ms + 10000;
        V6_DIAG("mapping_request_failed reason=random_unavailable");
        return;
    }
    memcpy(eee->mikun2n_ipv6_stun_transaction, request + 8, 12);
    n2n_sock_t *observer = &eee->mikun2n_ipv6_stun_servers[eee->mikun2n_ipv6_stun_index];
    ssize_t sent = ipv6_send_raw(eee, observer, request, sizeof(request));
    int error = sent < 0 ? ipv6_socket_error() : 0;
    eee->mikun2n_ipv6_stun_pending = sent == (ssize_t)sizeof(request);
    eee->mikun2n_ipv6_stun_sent_ms = now_ms;
    eee->mikun2n_ipv6_stun_next_ms = now_ms + 10000;
    n2n_sock_str_t text;
    V6_DIAG("mapping_request observer=%s sent=%d bytes=%u error=%d",
            sock_to_cstr(text, observer), eee->mikun2n_ipv6_stun_pending, (unsigned)sizeof(request), error);
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


static n2n_sock_t ipv6_local_address (const n2n_sock_t *current, uint16_t port) {
    n2n_sock_t best = {0};
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
    ULONG metric = (ULONG)-1;
    V6_DIAG("address_scan result=%lu port=%u", (unsigned long)result, port);
    if(result == NO_ERROR) {
        for(IP_ADAPTER_ADDRESSES *adapter = adapters; adapter; adapter = adapter->Next) {
            if(adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK)
                continue;
            int has_gateway = 0;
            for(IP_ADAPTER_GATEWAY_ADDRESS *gateway = adapter->FirstGatewayAddress; gateway; gateway = gateway->Next)
                if(gateway->Address.lpSockaddr && gateway->Address.lpSockaddr->sa_family == AF_INET6)
                    has_gateway = 1;
            if(!has_gateway)
                continue;
            for(IP_ADAPTER_UNICAST_ADDRESS *item = adapter->FirstUnicastAddress; item; item = item->Next) {
                if(!item->Address.lpSockaddr || item->Address.lpSockaddr->sa_family != AF_INET6 ||
                   item->DadState != IpDadStatePreferred || item->PreferredLifetime < 30)
                    continue;
                n2n_sock_t candidate = {0};
                fill_n2nsock(&candidate, item->Address.lpSockaddr);
                candidate.port = port;
                int rank = ipv6_address_rank(&candidate), best_rank = ipv6_address_rank(&best);
                if(rank && (rank > best_rank ||
                   (rank == best_rank && (adapter->Ipv6Metric < metric ||
                    (adapter->Ipv6Metric == metric && sock_equal(&candidate, current)))))) {
                    best = candidate;
                    metric = adapter->Ipv6Metric;
                    n2n_sock_str_t text;
                    V6_DIAG("local_candidate address=%s if_index=%lu metric=%lu preferred_lifetime=%lu link_mtu=%lu kind=%s",
                            sock_to_cstr(text, &candidate), (unsigned long)adapter->Ipv6IfIndex,
                            (unsigned long)metric, (unsigned long)item->PreferredLifetime, (unsigned long)adapter->Mtu,
                            rank == 2 ? "global" : "ula_nat66");
                }
            }
        }
    }
    free(adapters);
#else
    struct ifaddrs *addresses = NULL;
    if(getifaddrs(&addresses) == 0) {
        for(struct ifaddrs *item = addresses; item; item = item->ifa_next) {
            if(!item->ifa_addr || item->ifa_addr->sa_family != AF_INET6 ||
               !(item->ifa_flags & IFF_UP) || (item->ifa_flags & IFF_LOOPBACK))
                continue;
            n2n_sock_t candidate = {0};
            fill_n2nsock(&candidate, item->ifa_addr);
            candidate.port = port;
            int rank = ipv6_address_rank(&candidate), best_rank = ipv6_address_rank(&best);
            if(rank && (rank > best_rank || (rank == best_rank && sock_equal(&candidate, current))))
                best = candidate;
        }
        freeifaddrs(addresses);
    }
#endif
    return best;
}

void mikun2n_ipv6_close (struct n3n_runtime_data *eee) {
    if(eee->mikun2n_ipv6_socket != MIKUN2N_INVALID_SOCKET)
        closesocket(eee->mikun2n_ipv6_socket);
    eee->mikun2n_ipv6_socket = MIKUN2N_INVALID_SOCKET;
    eee->mikun2n_ipv6_token = 0;
    eee->mikun2n_ipv6_mapped_ms = 0;
    eee->mikun2n_ipv6_stun_pending = false;
    eee->mikun2n_ipv6_stun_next_ms = 0;
    memset(&eee->mikun2n_ipv6_mapped_address, 0, sizeof(eee->mikun2n_ipv6_mapped_address));
    memset(&eee->mikun2n_ipv6_address, 0, sizeof(eee->mikun2n_ipv6_address));
}

static void ipv6_refresh (struct n3n_runtime_data *eee) {
    struct sockaddr_in v4;
    socklen_t size = sizeof(v4);
    if(getsockname(eee->sock, (struct sockaddr *)&v4, &size) != 0)
        return;
    n2n_sock_t address = ipv6_local_address(&eee->mikun2n_ipv6_address, ntohs(v4.sin_port));
    if(!address.family)
        V6_DIAG("no_usable_address port=%u; need global or routed ULA IPv6, using IPv4", ntohs(v4.sin_port));
    if(sock_equal(&address, &eee->mikun2n_ipv6_address))
        return;
    n2n_sock_str_t old_text, new_text;
    V6_DIAG("local_address_changed old=%s new=%s; invalidating paths",
            sock_to_cstr(old_text, &eee->mikun2n_ipv6_address), sock_to_cstr(new_text, &address));
    mikun2n_ipv6_close(eee);
    struct peer_info *peer, *tmp;
    HASH_ITER(hh, eee->pending_peers, peer, tmp) {
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
        memset(&peer->mikun2n_ipv6_path_address, 0, sizeof(peer->mikun2n_ipv6_path_address));
        memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
    }
    HASH_ITER(hh, eee->known_peers, peer, tmp) {
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
        memset(&peer->mikun2n_ipv6_path_address, 0, sizeof(peer->mikun2n_ipv6_path_address));
        memset(peer->mikun2n_ipv6_probes, 0, sizeof(peer->mikun2n_ipv6_probes));
    }
    if(address.family == AF_INET6) {
        SOCKET fd = socket(AF_INET6, SOCK_DGRAM, 0);
        if(fd != MIKUN2N_INVALID_SOCKET) {
            int one = 1;
            struct sockaddr_in6 local;
            memset(&local, 0, sizeof(local));
            fill_sockaddr((struct sockaddr *)&local, sizeof(local), &address);
            setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, (const char *)&one, sizeof(one));
#ifdef IPV6_DONTFRAG
            if(setsockopt(fd, IPPROTO_IPV6, IPV6_DONTFRAG, (const char *)&one, sizeof(one)) != 0) {
                int error = ipv6_socket_error();
                traceEvent(TRACE_WARNING, "MikuN2N IPv6 disabled: IPV6_DONTFRAG failed (error=%d); using IPv4", error);
                V6_DIAG("dontfrag_failed error=%d; size discovery unavailable, using IPv4", error);
                closesocket(fd);
                return;
            }
#else
            traceEvent(TRACE_WARNING, "MikuN2N IPv6 disabled: IPV6_DONTFRAG unavailable; using IPv4");
            V6_DIAG("dontfrag_unavailable; size discovery unavailable, using IPv4");
            closesocket(fd);
            return;
#endif
#ifdef _WIN32
            u_long nonblocking = 1;
            ioctlsocket(fd, FIONBIO, &nonblocking);
#else
            fcntl(fd, F_SETFL, O_NONBLOCK);
#endif
            if(bind(fd, (struct sockaddr *)&local, sizeof(local)) == 0) {
                eee->mikun2n_ipv6_socket = fd;
                eee->mikun2n_ipv6_address = address;
                eee->mikun2n_ipv6_token = n3n_rand() | 1;
                traceEvent(TRACE_NORMAL, "MikuN2N experimental IPv6 transport ready");
                V6_DIAG("socket_ready address=%s mtu=%u probe_bytes=%u path_ttl_ms=%u nat66_supported=1 kind=%s",
                        sock_to_cstr(new_text, &address), eee->conf.mtu, eee->conf.mtu + 128, IPV6_PATH_TTL_MS,
                        mikun2n_ipv6_public(&address) ? "global" : "ula_nat66");
            } else {
                int error = ipv6_socket_error();
                closesocket(fd);
                traceEvent(TRACE_WARNING, "MikuN2N IPv6 bind failed; IPv4 remains available");
                V6_DIAG("bind_failed error=%d address=%s", error, sock_to_cstr(new_text, &address));
            }
        } else
            V6_DIAG("socket_failed error=%d", ipv6_socket_error());
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
        if(is_null_mac(peer->mac_addr) || !memcmp(peer->mac_addr, eee->device.mac_addr, N2N_MAC_SIZE))
            continue;
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
            V6_DIAG("query peer=%s active=%d candidate_age_ms=%llu", macaddr_str(mac, peer->mac_addr),
                    mikun2n_ipv6_active(eee, peer, now_ms),
                    (unsigned long long)(peer->mikun2n_ipv6_seen_ms ? now_ms - peer->mikun2n_ipv6_seen_ms : 0));
            peer->mikun2n_ipv6_query_ms = now_ms + 3000;
            send_query_peer(eee, peer->mac_addr);
        }
        if(!peer->mikun2n_ipv6_token ||
           now_ms - peer->mikun2n_ipv6_seen_ms >= IPV6_REPORT_TTL_MS)
            continue;
        for(int i = 0; i < MIKUN2N_IPV6_PROBES; i++) {
            mikun2n_ipv6_probe_t *probe = &peer->mikun2n_ipv6_probes[i];
            if(probe->challenge && now_ms - probe->sent_ms > IPV6_PATH_TTL_MS) {
                probe->challenge = 0;
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
        peer->mikun2n_ipv6_next_probe_ms = now_ms + 500;
        if(!ipv6_probe_send(eee, peer, destination, now_ms, IPV6_SAFE_UDP_SIZE, "base_keepalive"))
            continue;
        peer->mikun2n_ipv6_next_probe_ms = now_ms +
            (ipv6_receive_ready(eee, peer, now_ms) ? 2000 : peer->mikun2n_ipv6_attempts < 6 ? 500 : 10000);
        if(peer->mikun2n_ipv6_attempts < 6)
            peer->mikun2n_ipv6_attempts++;
    }
}

void mikun2n_ipv6_tick (struct n3n_runtime_data *eee, uint64_t now_ms) {
    if(!eee->conf.mikun2n_ipv6 || !eee->conf.allow_p2p || eee->conf.connect_tcp || !eee->last_sup)
        return;
    if(now_ms >= eee->mikun2n_ipv6_refresh_ms) {
        V6_DIAG("summary monotonic_ms=%llu tx_packets=%llu tx_bytes=%llu rx_packets=%llu rx_bytes=%llu reject_header=%llu reject_peer=%llu reject_pong=%llu reject_inactive=%llu tx_fragments=%llu rx_fragments=%llu reassembled=%llu transient_drops=%llu",
                (unsigned long long)now_ms, (unsigned long long)diag_tx_data, (unsigned long long)diag_tx_bytes,
                (unsigned long long)diag_rx_data, (unsigned long long)diag_rx_bytes,
                (unsigned long long)diag_reject_header, (unsigned long long)diag_reject_peer,
                (unsigned long long)diag_reject_pong, (unsigned long long)diag_reject_inactive,
                (unsigned long long)diag_tx_fragments, (unsigned long long)diag_rx_fragments,
                (unsigned long long)diag_reassembled, (unsigned long long)diag_transient_drops);
        eee->mikun2n_ipv6_refresh_ms = now_ms + 10000;
        ipv6_refresh(eee);
    }
    if(eee->mikun2n_ipv6_socket == MIKUN2N_INVALID_SOCKET)
        return;
    ipv6_stun_tick(eee, now_ms);
    ipv6_tick_peers(eee, eee->pending_peers, now_ms);
    ipv6_tick_peers(eee, eee->known_peers, now_ms);
}

int mikun2n_ipv6_send (struct n3n_runtime_data *eee, struct peer_info *peer,
                      const uint8_t *data, size_t size, uint64_t now_ms) {
    uint8_t packet[N2N_PKT_BUF_SIZE];
    if(!mikun2n_ipv6_active(eee, peer, now_ms)) return 0;
    if(!size || size > N2N_PKT_BUF_SIZE) return 0;
    unsigned budget = min(peer->mikun2n_ipv6_path_bytes, IPV6_RX_LIMIT);
    if(budget < IPV6_SAFE_UDP_SIZE) return 0;
    uint64_t id = ++eee->mikun2n_ipv6_datagram_id;
    if(!id) id = ++eee->mikun2n_ipv6_datagram_id;
    size_t offset = 0;
    int fragmented = size + IPV6_FRAME_SIZE > budget;
    if(fragmented && now_ms >= peer->mikun2n_ipv6_oversize_log_ms) {
        macstr_t mac;
        V6_DIAG("data_fragmented peer=%s n3n_bytes=%u tx_limit=%u; IPv6 path preserved",
                macaddr_str(mac, peer->mac_addr), (unsigned)size, budget);
        peer->mikun2n_ipv6_oversize_log_ms = now_ms + 10000;
    }
    while(offset < size) {
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
            if(ipv6_message_too_large(error) && budget > IPV6_SAFE_UDP_SIZE) {
                ipv6_lower_probe(peer, now_ms, "data_message_too_large");
                if(!offset) { budget = IPV6_SAFE_UDP_SIZE; fragmented = 1; continue; }
            }
            macstr_t mac;
            V6_DIAG("data_send_failed peer=%s bytes=%u error=%d partial=%d transient=%d",
                    macaddr_str(mac, peer->mac_addr), (unsigned)size, error, offset != 0, ipv6_transient_error(error));
            // A transient queue failure is UDP loss, not evidence of a dead path.
            // Do not replay a partly emitted datagram through a different route.
            if(ipv6_transient_error(error)) { diag_transient_drops++; return 1; }
            if(!ipv6_message_too_large(error) || budget <= IPV6_SAFE_UDP_SIZE)
                peer->mikun2n_ipv6_valid_until_ms = 0;
            return offset ? 1 : 0;
        }
        if(fragmented) diag_tx_fragments++;
        offset += chunk;
    }
    diag_tx_data++;
    diag_tx_bytes += size;
    return 1;
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
        return 0;
    }
    struct peer_info *peer = ipv6_peer(eee, data + 5);
    if(!peer || peer->force_relay || peer->local || !peer->mikun2n_ipv6_token ||
       ipv6_get_u64(data + 17) != peer->mikun2n_ipv6_token ||
       now_ms - peer->mikun2n_ipv6_seen_ms >= IPV6_REPORT_TTL_MS) {
        diag_reject_peer++;
        return 0;
    }
    macstr_t mac;
    n2n_sock_str_t source;
    peer->mikun2n_ipv6_wire_version = data[3];
    if(data[3] != ipv6_magic[3]) {
        if(now_ms >= peer->mikun2n_ipv6_legacy_log_ms) {
            V6_DIAG("version_mismatch peer=%s remote=%u local=%u; preserving IPv4",
                    macaddr_str(mac, peer->mac_addr), data[3], ipv6_magic[3]);
            peer->mikun2n_ipv6_legacy_log_ms = now_ms + 10000;
        }
        return 0;
    }
    uint8_t kind = data[4];
    uint64_t challenge = ipv6_get_u64(data + 33);
    if(kind == IPV6_PING && challenge && size >= IPV6_CONTROL_SIZE) {
        if(size > IPV6_RX_LIMIT || ipv6_read_u16(data + 43) < IPV6_SAFE_UDP_SIZE) return 0;
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
            diag_reject_pong++;
            V6_DIAG("pong_rejected peer=%s from=%s reason=challenge_size_age_or_endpoint",
                    macaddr_str(mac, peer->mac_addr), sock_to_cstr(source, sender));
            return 0;
        }
        int was_active = mikun2n_ipv6_active(eee, peer, now_ms);
        uint16_t ready_ms = ipv6_read_u16(data + 41);
        uint16_t rx_limit = ipv6_read_u16(data + 43);
        if(ready_ms > IPV6_PATH_TTL_MS || rx_limit < IPV6_SAFE_UDP_SIZE || rx_limit > IPV6_RX_LIMIT) {
            diag_reject_pong++;
            return 0;
        }
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
        uint64_t next_check = now_ms + (active ? 2000 : 500);
        if(peer->mikun2n_ipv6_next_probe_ms > next_check)
            peer->mikun2n_ipv6_next_probe_ms = next_check;
        peer->last_seen = time(NULL);
    } else if(kind == IPV6_FRAGMENT && size > IPV6_FRAGMENT_HEADER &&
              ipv6_receive_ready(eee, peer, now_ms) && sock_equal(sender, &peer->mikun2n_ipv6_path_address)) {
        diag_rx_fragments++;
        uint16_t total = ipv6_read_u16(data + 41), offset = ipv6_read_u16(data + 43);
        if(!challenge || !total || total > N2N_PKT_BUF_SIZE || offset >= total ||
           size - IPV6_FRAGMENT_HEADER > (size_t)(total - offset)) return 0;
        if(!peer->mikun2n_ipv6_reassembly) {
            peer->mikun2n_ipv6_reassembly = calloc(1, sizeof(*peer->mikun2n_ipv6_reassembly));
            if(!peer->mikun2n_ipv6_reassembly) return 0;
        }
        size_t assembled = mikun2n_ipv6_reassemble(peer->mikun2n_ipv6_reassembly, now_ms,
                challenge, ipv6_read_u16(data + 41), ipv6_read_u16(data + 43),
                data + IPV6_FRAGMENT_HEADER, size - IPV6_FRAGMENT_HEADER, data);
        if(assembled) { diag_reassembled++; diag_rx_data++; diag_rx_bytes += assembled; }
        return assembled;
    } else if(kind == IPV6_DATA && ipv6_receive_ready(eee, peer, now_ms) &&
              sock_equal(sender, &peer->mikun2n_ipv6_path_address)) {
        size -= IPV6_FRAME_SIZE;
        diag_rx_data++;
        diag_rx_bytes += size;
        memmove(data, data + IPV6_FRAME_SIZE, size);
        return size;
    }
    else
        diag_reject_inactive++;
    return 0;
}

int mikun2n_ipv6_accept (struct n3n_runtime_data *eee, const n2n_mac_t mac,
                        const n2n_sock_t *sender, uint64_t now_ms) {
    struct peer_info *peer = ipv6_peer(eee, mac);
    if(!ipv6_receive_ready(eee, peer, now_ms) || !sock_equal(sender, &peer->mikun2n_ipv6_path_address))
        return 0;
    peer->last_seen = time(NULL);
    return 1;
}
