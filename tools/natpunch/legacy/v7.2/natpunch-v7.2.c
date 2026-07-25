/*
 * natpunch v7.2 - adaptive UDP NAT4 hole-punch experiment for Windows.
 *
 * Build:
 *   gcc -std=gnu17 -O2 -Wall -Wextra -o natpunch-v7.2.exe natpunch.c -lws2_32
 */
#define _WIN32_WINNT 0x0600
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <direct.h>
#include <errno.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define VERSION "7.2.0"
#define PORT_A 21001
#define PORT_B 21002
#define DEFAULT_WORKERS 25
#define MAX_WORKERS 48
#define MAX_TARGETS 1200
#define MAX_LINE 1900
#define CALIBRATION_TIMEOUT_MS 1800
#define SPRAY_MS 7000
#define TICK_MS 100

typedef struct {
    FILE *human;
    FILE *jsonl;
    ULONGLONG started_ms;
    char human_path[MAX_PATH * 2];
    char jsonl_path[MAX_PATH * 2];
} Logger;

typedef struct {
    SOCKET socket;
    int local_port;
    char ip_a[64];
    char ip_b[64];
    int mapped_a;
    int mapped_b;
    int rtt_a_ms;
    int rtt_b_ms;
    int seq_a;
    int seq_b;
    ULONGLONG sent_a_ms;
    ULONGLONG sent_b_ms;
} Worker;

typedef struct {
    char mode[16];
    int direction;
    int confidence;
    int rate;
    int bank1;
    int bank2;
    int banks;
    int spread;
    int step;
    int valid_a;
    int valid_b;
    int reuse;
    int ip_changes;
    int volatile_dual;
    int fast_cycle;
    int inherited_history;
    int median_rtt;
    ULONGLONG measured_ms;
} Model;

typedef struct {
    int attempt;
    char ip[64];
    int control_port;
    Model model;
    int workers;
} PeerModel;

typedef struct {
    int attempted;
    int sent;
    int errors;
} SendStats;

typedef struct {
    int port;
    int bank;
    int offset;
    char lane[12];
} Target;

typedef struct {
    Target low[128];
    Target mid[384];
    Target predicted[260];
    Target tail[256];
    int low_count;
    int mid_count;
    int predicted_count;
    int tail_count;
    int unique_count;
    int range_hi;
} TargetBuckets;

static Logger g_log;

static ULONGLONG mono_ms(void) {
    return GetTickCount64();
}

static void wall_time(char *out, size_t size) {
    SYSTEMTIME st;
    GetLocalTime(&st);
    snprintf(out, size, "%04d-%02d-%02dT%02d:%02d:%02d.%03d",
             (int)st.wYear, (int)st.wMonth, (int)st.wDay, (int)st.wHour,
             (int)st.wMinute, (int)st.wSecond, (int)st.wMilliseconds);
}

static void say(const char *fmt, ...) {
    va_list ap, copy;
    char ts[64];
    wall_time(ts, sizeof(ts));
    va_start(ap, fmt);
    va_copy(copy, ap);
    printf("[%s +%llums] ", ts, (unsigned long long)(mono_ms() - g_log.started_ms));
    vprintf(fmt, ap);
    printf("\n");
    fflush(stdout);
    if (g_log.human) {
        fprintf(g_log.human, "[%s +%llums] ", ts,
                (unsigned long long)(mono_ms() - g_log.started_ms));
        vfprintf(g_log.human, fmt, copy);
        fprintf(g_log.human, "\n");
        fflush(g_log.human);
    }
    va_end(copy);
    va_end(ap);
}

static void event_json(const char *event, const char *fields_fmt, ...) {
    char ts[64];
    va_list ap;
    if (!g_log.jsonl) return;
    wall_time(ts, sizeof(ts));
    fprintf(g_log.jsonl, "{\"ts\":\"%s\",\"elapsed_ms\":%llu,\"event\":\"%s\"",
            ts, (unsigned long long)(mono_ms() - g_log.started_ms), event);
    if (fields_fmt && fields_fmt[0]) {
        fputc(',', g_log.jsonl);
        va_start(ap, fields_fmt);
        vfprintf(g_log.jsonl, fields_fmt, ap);
        va_end(ap);
    }
    fprintf(g_log.jsonl, "}\n");
    fflush(g_log.jsonl);
}

static int mkdirs(char *path) {
    char copy[MAX_PATH * 2];
    char *p;
    snprintf(copy, sizeof(copy), "%s", path);
    for (p = copy + 3; *p; ++p) {
        if (*p == '\\' || *p == '/') {
            char saved = *p;
            *p = 0;
            if (_mkdir(copy) != 0 && errno != EEXIST) return 0;
            *p = saved;
        }
    }
    return _mkdir(copy) == 0 || errno == EEXIST;
}

static int logger_init(const char *requested_dir) {
    char base[MAX_PATH * 2];
    char stamp[40];
    SYSTEMTIME st;
    DWORD pid = GetCurrentProcessId();
    memset(&g_log, 0, sizeof(g_log));
    g_log.started_ms = mono_ms();
    if (requested_dir && requested_dir[0]) {
        snprintf(base, sizeof(base), "%s", requested_dir);
    } else {
        DWORD n = GetEnvironmentVariableA("LOCALAPPDATA", base, (DWORD)sizeof(base));
        if (n == 0 || n >= sizeof(base)) snprintf(base, sizeof(base), ".");
        strncat(base, "\\MikuN2N\\logs\\natpunch", sizeof(base) - strlen(base) - 1);
    }
    if (!mkdirs(base)) {
        fprintf(stderr, "无法创建日志目录 %s, Win32/errno=%lu/%d\n", base, GetLastError(), errno);
        snprintf(base, sizeof(base), ".");
    }
    GetLocalTime(&st);
    snprintf(stamp, sizeof(stamp), "%04u%02u%02u-%02u%02u%02u",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    snprintf(g_log.human_path, sizeof(g_log.human_path), "%s\\natpunch-v7.2-%s-%lu.log",
             base, stamp, (unsigned long)pid);
    snprintf(g_log.jsonl_path, sizeof(g_log.jsonl_path), "%s\\natpunch-v7.2-%s-%lu.jsonl",
             base, stamp, (unsigned long)pid);
    g_log.human = fopen(g_log.human_path, "wb");
    g_log.jsonl = fopen(g_log.jsonl_path, "wb");
    return g_log.human != NULL && g_log.jsonl != NULL;
}

static void logger_close(void) {
    if (g_log.human) fclose(g_log.human);
    if (g_log.jsonl) fclose(g_log.jsonl);
    g_log.human = NULL;
    g_log.jsonl = NULL;
}

static uint32_t fnv1a(const char *text) {
    uint32_t h = 2166136261u;
    while (*text) {
        h ^= (unsigned char)*text++;
        h *= 16777619u;
    }
    return h;
}

static void random_id(char *out, size_t size) {
    LARGE_INTEGER counter;
    uint64_t x;
    QueryPerformanceCounter(&counter);
    x = ((uint64_t)counter.QuadPart << 17) ^ mono_ms() ^
        ((uint64_t)GetCurrentProcessId() << 32) ^ (uintptr_t)out;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    snprintf(out, size, "%08lx%08lx", (unsigned long)(x >> 32), (unsigned long)x);
}

static SOCKET make_udp(void) {
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in addr;
    if (s == INVALID_SOCKET) return INVALID_SOCKET;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = 0;
    if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        closesocket(s);
        return INVALID_SOCKET;
    }
    return s;
}

static int socket_local_port(SOCKET s) {
    struct sockaddr_in addr;
    int len = sizeof(addr);
    memset(&addr, 0, sizeof(addr));
    if (getsockname(s, (struct sockaddr *)&addr, &len) != 0) return -1;
    return ntohs(addr.sin_port);
}

static int endpoint(struct sockaddr_in *out, const char *ip, int port) {
    memset(out, 0, sizeof(*out));
    out->sin_family = AF_INET;
    out->sin_port = htons((unsigned short)port);
    return InetPtonA(AF_INET, ip, &out->sin_addr) == 1;
}

static int send_addr(SOCKET s, const char *text, const struct sockaddr_in *addr, SendStats *stats) {
    int rc;
    if (stats) stats->attempted++;
    rc = sendto(s, text, (int)strlen(text), 0, (const struct sockaddr *)addr, sizeof(*addr));
    if (rc == SOCKET_ERROR) {
        if (stats) stats->errors++;
        return 0;
    }
    if (stats) stats->sent++;
    return 1;
}

static int send_text(SOCKET s, const char *text, const char *ip, int port, SendStats *stats) {
    struct sockaddr_in addr;
    if (!endpoint(&addr, ip, port)) return 0;
    return send_addr(s, text, &addr, stats);
}

static int recv_any(SOCKET control, Worker *workers, int count, int timeout_ms,
                    char *buf, int buflen, struct sockaddr_in *from, int *worker_index) {
    fd_set rfds;
    struct timeval tv;
    SOCKET max_socket = control;
    int i, from_len = sizeof(*from), n;
    FD_ZERO(&rfds);
    FD_SET(control, &rfds);
    for (i = 0; i < count; ++i) {
        if (workers[i].socket != INVALID_SOCKET) {
            FD_SET(workers[i].socket, &rfds);
            if (workers[i].socket > max_socket) max_socket = workers[i].socket;
        }
    }
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;
    if (select((int)max_socket + 1, &rfds, NULL, NULL, &tv) <= 0) return -1;
    *worker_index = -1;
    if (FD_ISSET(control, &rfds)) {
        n = recvfrom(control, buf, buflen - 1, 0, (struct sockaddr *)from, &from_len);
    } else {
        n = -1;
        for (i = 0; i < count; ++i) {
            if (workers[i].socket != INVALID_SOCKET && FD_ISSET(workers[i].socket, &rfds)) {
                *worker_index = i;
                n = recvfrom(workers[i].socket, buf, buflen - 1, 0,
                             (struct sockaddr *)from, &from_len);
                break;
            }
        }
    }
    if (n == SOCKET_ERROR || n < 0) return -1;
    buf[n] = 0;
    return n;
}

static int cmp_int(const void *a, const void *b) {
    int aa = *(const int *)a, bb = *(const int *)b;
    return (aa > bb) - (aa < bb);
}

static int median_int(int *values, int count) {
    int copy[MAX_WORKERS * 2];
    if (count <= 0) return 0;
    memcpy(copy, values, (size_t)count * sizeof(int));
    qsort(copy, (size_t)count, sizeof(int), cmp_int);
    if (count & 1) return copy[count / 2];
    return (copy[count / 2 - 1] + copy[count / 2]) / 2;
}

static int circular_forward(int a, int b) {
    int d = (b - a) & 0xffff;
    return d;
}

static int circular_distance(int a, int b) {
    int d = circular_forward(a, b);
    return d > 32768 ? 65536 - d : d;
}

static void fit_model(Worker *workers, int count, int control_port,
                      const Model *previous, Model *out) {
    int ports[MAX_WORKERS], deltas[MAX_WORKERS], rtts[MAX_WORKERS * 2];
    int sorted[MAX_WORKERS], valid = 0, nd = 0, nrtt = 0, reuse = 0;
    int i, largest_gap = 0, split = -1, good_forward = 0, instant_rate = 0;
    char first_ip[64] = "";
    ULONGLONG now = mono_ms();
    memset(out, 0, sizeof(*out));
    out->direction = 1;
    out->step = 1;
    out->measured_ms = now;
    for (i = 0; i < count; ++i) {
        if (workers[i].mapped_a > 0) {
            out->valid_a++;
            if (!first_ip[0]) snprintf(first_ip, sizeof(first_ip), "%s", workers[i].ip_a);
            else if (strcmp(first_ip, workers[i].ip_a)) out->ip_changes++;
            if (workers[i].rtt_a_ms >= 0) rtts[nrtt++] = workers[i].rtt_a_ms;
        }
        if (workers[i].mapped_b > 0) {
            out->valid_b++;
            if (!first_ip[0]) snprintf(first_ip, sizeof(first_ip), "%s", workers[i].ip_b);
            else if (strcmp(first_ip, workers[i].ip_b)) out->ip_changes++;
            if (workers[i].rtt_b_ms >= 0) rtts[nrtt++] = workers[i].rtt_b_ms;
        }
        if (workers[i].mapped_a > 0 && workers[i].mapped_a == workers[i].mapped_b) reuse++;
        if (workers[i].mapped_b > 0) ports[valid++] = workers[i].mapped_b;
        else if (workers[i].mapped_a > 0) ports[valid++] = workers[i].mapped_a;
    }
    out->reuse = reuse;
    out->median_rtt = median_int(rtts, nrtt);
    if (valid < 4) {
        if (previous && (previous->fast_cycle || previous->volatile_dual)) {
            strcpy(out->mode, previous->fast_cycle ? "fast" : "volatile");
            out->confidence = previous->fast_cycle ? 500 : 450;
            out->rate = previous->rate;
            out->bank1 = previous->bank1;
            out->bank2 = previous->bank2;
            out->banks = previous->banks;
            out->spread = previous->spread < 64 ? 64 : previous->spread;
            out->step = previous->step;
            out->volatile_dual = previous->volatile_dual;
            out->fast_cycle = previous->fast_cycle;
            out->inherited_history = 1;
            out->measured_ms = previous->measured_ms;
            return;
        }
        strcpy(out->mode, "hard");
        out->confidence = 100;
        out->bank1 = control_port;
        out->bank2 = control_port;
        out->banks = 1;
        out->spread = 256;
        return;
    }
    if (out->ip_changes == 0 && out->valid_a > 0 && reuse * 100 >= out->valid_a * 80) {
        strcpy(out->mode, "cone");
        out->confidence = (reuse * 1000) / out->valid_a;
        out->bank1 = control_port;
        out->bank2 = control_port;
        out->banks = 1;
        out->spread = 4;
        return;
    }
    for (i = 1; i < valid; ++i) {
        int d = circular_forward(ports[i - 1], ports[i]);
        if (d > 0 && d <= 4096) good_forward++;
        if (d > 0 && d <= 128) deltas[nd++] = d;
    }
    memcpy(sorted, ports, (size_t)valid * sizeof(int));
    qsort(sorted, (size_t)valid, sizeof(int), cmp_int);
    for (i = 1; i < valid; ++i) {
        int gap = sorted[i] - sorted[i - 1];
        if (gap > largest_gap) {
            largest_gap = gap;
            split = i;
        }
    }
    out->step = median_int(deltas, nd);
    if (out->step <= 0 || out->step > 64) out->step = 1;
    if (largest_gap >= 128 && split >= 3 && valid - split >= 3) {
        out->banks = 2;
        out->bank1 = sorted[split - 1];
        out->bank2 = sorted[valid - 1];
        out->spread = out->step * 3 + 12;
    } else {
        out->banks = 1;
        out->bank1 = sorted[valid - 1];
        out->bank2 = out->bank1;
        out->spread = out->step * 3 + 12;
    }
    if (good_forward * 100 < (valid - 1) * 35 && out->banks == 1) {
        strcpy(out->mode, "hard");
        out->confidence = 600;
        out->spread = 384;
    } else {
        strcpy(out->mode, "sym");
        out->confidence = out->banks == 2 ? 820 : 760;
    }
    if (out->ip_changes > 0) {
        strcpy(out->mode, "hard");
        out->confidence = 900;
        out->spread = 512;
    }
    if (previous && previous->measured_ms > 0 && now > previous->measured_ms) {
        double seconds = (double)(now - previous->measured_ms) / 1000.0;
        int rates[2], nrate = 0;
        int prev1 = previous->bank1, prev2 = previous->bank2;
        if (out->banks == 2 && previous->banks == 2) {
            int direct = circular_distance(previous->bank1, out->bank1) +
                         circular_distance(previous->bank2, out->bank2);
            int swapped = circular_distance(previous->bank1, out->bank2) +
                          circular_distance(previous->bank2, out->bank1);
            int gap_now = abs(out->bank2 - out->bank1);
            int gap_previous = abs(previous->bank2 - previous->bank1);
            if (swapped < direct) {
                prev1 = previous->bank2;
                prev2 = previous->bank1;
            }
            if ((direct < swapped ? direct : swapped) > 1024 ||
                abs(gap_now - gap_previous) > 256)
                out->volatile_dual = 1;
        } else if (out->banks == 2 || previous->banks == 2) {
            out->volatile_dual = 1;
        }
        {
            int d1 = circular_forward(prev1, out->bank1);
            int d2 = circular_forward(prev2, out->bank2);
            if (d1 > 0 && d1 < 4096) rates[nrate++] = (int)(d1 / seconds);
            if (out->banks == 2 && previous->banks == 2 && d2 > 0 && d2 < 4096)
                rates[nrate++] = (int)(d2 / seconds);
        }
        if (nrate) {
            instant_rate = median_int(rates, nrate);
            if (previous->fast_cycle && previous->rate > 0)
                out->rate = (previous->rate * 2 + instant_rate) / 3;
            else
                out->rate = instant_rate;
        }
    }
    /*
     * A load-balanced CGNAT can briefly collapse two observed banks into one.
     * Keep the volatile classification until a genuinely endpoint-independent
     * (cone) round is observed, otherwise the scan policy oscillates.
     */
    if (previous && previous->volatile_dual && strcmp(out->mode, "cone"))
        out->volatile_dual = 1;
    if (previous && previous->fast_cycle && strcmp(out->mode, "cone")) {
        out->fast_cycle = 1;
        if (out->rate <= 0) out->rate = previous->rate;
    }
    if (out->rate >= 120 && out->banks == 1 &&
        !(previous && previous->volatile_dual))
        out->fast_cycle = 1;
    if (out->fast_cycle && strcmp(out->mode, "cone")) {
        strcpy(out->mode, "fast");
        out->confidence = 720;
    } else if (out->volatile_dual && !strcmp(out->mode, "sym")) {
        strcpy(out->mode, "volatile");
        out->confidence = 680;
    }
}

static int model_self_test(void) {
    Worker workers[12];
    Model model, previous;
    int i, failures = 0;
    memset(workers, 0, sizeof(workers));
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = 10000 + i;
        workers[i].mapped_b = 10020 + i;
    }
    fit_model(workers, 12, 9999, NULL, &model);
    if (strcmp(model.mode, "sym") || model.banks != 1 || model.bank1 != 10031) failures++;
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = 20000 + i;
        workers[i].mapped_b = (i & 1) ? 21800 + i : 20100 + i;
    }
    fit_model(workers, 12, 19999, NULL, &model);
    if (strcmp(model.mode, "sym") || model.banks != 2) failures++;
    previous = model;
    previous.measured_ms = mono_ms() - 1000;
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = 23000 + i;
        workers[i].mapped_b = (i & 1) ? 25200 + i : 23100 + i;
    }
    fit_model(workers, 12, 22999, &previous, &model);
    if (strcmp(model.mode, "volatile") || !model.volatile_dual) failures++;
    previous = model;
    previous.banks = 1;
    previous.bank2 = previous.bank1;
    previous.measured_ms = mono_ms() - 1000;
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = 26000 + i;
        workers[i].mapped_b = 26100 + i;
    }
    fit_model(workers, 12, 25999, &previous, &model);
    if (strcmp(model.mode, "volatile") || !model.volatile_dual) failures++;
    memset(&previous, 0, sizeof(previous));
    strcpy(previous.mode, "sym");
    previous.direction = 1;
    previous.bank1 = previous.bank2 = 54043;
    previous.banks = 1;
    previous.spread = 30;
    previous.step = 6;
    previous.measured_ms = mono_ms() - 8000;
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = 56550 + i * 4;
        workers[i].mapped_b = 56576 + i * 4;
    }
    fit_model(workers, 12, 53958, &previous, &model);
    if (strcmp(model.mode, "fast") || !model.fast_cycle ||
        model.rate < 250 || model.rate > 400) failures++;
    previous = model;
    memset(workers, 0, sizeof(workers));
    fit_model(workers, 12, 53958, &previous, &model);
    if (strcmp(model.mode, "fast") || !model.inherited_history ||
        model.rate != previous.rate || model.bank1 != previous.bank1) failures++;
    for (i = 0; i < 12; ++i) {
        workers[i].mapped_a = workers[i].mapped_b = 30000 + i;
    }
    fit_model(workers, 12, 30000, NULL, &model);
    if (strcmp(model.mode, "cone")) failures++;
    printf("model self-test: %s (%d failure(s))\n", failures ? "FAIL" : "PASS", failures);
    return failures == 0 ? 0 : 1;
}

static int create_workers(Worker *workers, int count) {
    int i;
    memset(workers, 0, (size_t)count * sizeof(*workers));
    for (i = 0; i < count; ++i) {
        workers[i].socket = make_udp();
        workers[i].rtt_a_ms = workers[i].rtt_b_ms = -1;
        if (workers[i].socket == INVALID_SOCKET) {
            int j;
            for (j = 0; j < i; ++j) closesocket(workers[j].socket);
            return 0;
        }
        workers[i].local_port = socket_local_port(workers[i].socket);
    }
    return 1;
}

static void close_workers(Worker *workers, int count, int keep) {
    int i;
    for (i = 0; i < count; ++i) {
        if (i != keep && workers[i].socket != INVALID_SOCKET) closesocket(workers[i].socket);
        workers[i].socket = INVALID_SOCKET;
    }
}

static void collect_probes(SOCKET control, Worker *workers, int count,
                           const char *client_id, int base_seq, int is_b) {
    ULONGLONG deadline = mono_ms() + CALIBRATION_TIMEOUT_MS;
    char buf[MAX_LINE];
    struct sockaddr_in from;
    int received = 0;
    while (mono_ms() < deadline && received < count) {
        int wi, seq, port;
        char cid[64], ip[64], label[16];
        long long server_ms;
        int n = recv_any(control, workers, count, 80, buf, sizeof(buf), &from, &wi);
        if (n <= 0 || wi < 0) continue;
        if (sscanf(buf, "PROBED7 %63s %d %63s %d %15s %lld",
                   cid, &seq, ip, &port, label, &server_ms) != 6) continue;
        if (strcmp(cid, client_id) || seq < base_seq || seq >= base_seq + count) continue;
        wi = seq - base_seq;
        if (is_b) {
            if (workers[wi].mapped_b <= 0) received++;
            snprintf(workers[wi].ip_b, sizeof(workers[wi].ip_b), "%s", ip);
            workers[wi].mapped_b = port;
            workers[wi].rtt_b_ms = (int)(mono_ms() - workers[wi].sent_b_ms);
        } else {
            if (workers[wi].mapped_a <= 0) received++;
            snprintf(workers[wi].ip_a, sizeof(workers[wi].ip_a), "%s", ip);
            workers[wi].mapped_a = port;
            workers[wi].rtt_a_ms = (int)(mono_ms() - workers[wi].sent_a_ms);
        }
    }
}

static void calibrate(SOCKET control, Worker *workers, int count,
                      const char *server_ip, const char *client_id, int generation) {
    char msg[128];
    int i, base_a = generation * 1000, base_b = generation * 1000 + 100;
    for (i = 0; i < count; ++i) {
        workers[i].seq_a = base_a + i;
        workers[i].sent_a_ms = mono_ms();
        snprintf(msg, sizeof(msg), "PROBE7 %s %d", client_id, workers[i].seq_a);
        send_text(workers[i].socket, msg, server_ip, PORT_A, NULL);
    }
    collect_probes(control, workers, count, client_id, base_a, 0);
    for (i = 0; i < count; ++i) {
        workers[i].seq_b = base_b + i;
        workers[i].sent_b_ms = mono_ms();
        snprintf(msg, sizeof(msg), "PROBE7 %s %d", client_id, workers[i].seq_b);
        send_text(workers[i].socket, msg, server_ip, PORT_B, NULL);
    }
    collect_probes(control, workers, count, client_id, base_b, 1);
}

static int shift_port(int base, int direction, int offset) {
    int value = base + direction * offset;
    while (value < 1) value += 65535;
    while (value > 65535) value -= 65535;
    return value;
}

static void make_peer_target(const PeerModel *peer, int worker, int offset,
                             const char *lane, Target *target) {
    int use_bank2 = peer->model.bank2 != peer->model.bank1 && (worker & 1);
    int base = use_bank2 ? peer->model.bank2 : peer->model.bank1;
    target->bank = use_bank2 ? 2 : 1;
    target->offset = offset;
    target->port = shift_port(base, peer->model.direction, offset);
    snprintf(target->lane, sizeof(target->lane), "%s", lane);
}

static int clamp_fast_rate(int rate) {
    if (rate < 120) return 120;
    if (rate > 2000) return 2000;
    return rate;
}

static int fast_horizon(int rate) {
    int horizon;
    rate = clamp_fast_rate(rate);
    horizon = (rate * 8) / 10 + rate * (SPRAY_MS / 1000) + 256;
    return horizon > 8192 ? 8192 : horizon;
}

static int fast_center_offset(int rate, int tick) {
    int phase = tick / 5;
    rate = clamp_fast_rate(rate);
    return (rate * 8) / 10 + (rate * phase) / 2;
}

static void add_bucket(Target *bucket, int *count, int capacity, int port,
                       int bank, int offset, const char *lane) {
    Target *target;
    if (port < 1 || port > 65535 || *count >= capacity) return;
    target = &bucket[(*count)++];
    target->port = port;
    target->bank = bank;
    target->offset = offset;
    snprintf(target->lane, sizeof(target->lane), "%s", lane);
}

static void add_offset_for_banks(Target *bucket, int *count, int capacity,
                                 const PeerModel *peer, int offset, const char *lane) {
    add_bucket(bucket, count, capacity,
               shift_port(peer->model.bank1, peer->model.direction, offset),
               1, offset, lane);
    if (peer->model.bank2 != peer->model.bank1)
        add_bucket(bucket, count, capacity,
                   shift_port(peer->model.bank2, peer->model.direction, offset),
                   2, offset, lane);
}

static void build_targets(const PeerModel *peer, TargetBuckets *buckets) {
    int offset, drift, predicted_lo, predicted_hi, banks;
    memset(buckets, 0, sizeof(*buckets));
    if (!strcmp(peer->model.mode, "cone")) {
        buckets->unique_count = 1;
        return;
    }
    banks = peer->model.bank2 == peer->model.bank1 ? 1 : 2;
    if (!strcmp(peer->model.mode, "fast")) {
        buckets->range_hi = fast_horizon(peer->model.rate);
        buckets->unique_count = 1 + banks * buckets->range_hi;
        return;
    }
    for (offset = 1; offset <= 64; ++offset)
        add_offset_for_banks(buckets->low, &buckets->low_count,
                             (int)(sizeof(buckets->low) / sizeof(buckets->low[0])),
                             peer, offset, "low");
    for (offset = 65; offset <= 256; ++offset)
        add_offset_for_banks(buckets->mid, &buckets->mid_count,
                             (int)(sizeof(buckets->mid) / sizeof(buckets->mid[0])),
                             peer, offset, "mid");
    drift = peer->model.rate > 0 ? (peer->model.rate * 8) / 10 : 32;
    predicted_lo = drift - peer->model.spread - 64;
    predicted_hi = drift + peer->model.spread + 64;
    if (predicted_lo < 1) predicted_lo = 1;
    if (predicted_hi > 512) predicted_hi = 512;
    for (offset = predicted_lo; offset <= predicted_hi; ++offset)
        add_offset_for_banks(buckets->predicted, &buckets->predicted_count,
                             (int)(sizeof(buckets->predicted) / sizeof(buckets->predicted[0])),
                             peer, offset, "predicted");
    if (!strcmp(peer->model.mode, "volatile") || !strcmp(peer->model.mode, "hard")) {
        for (offset = 257; offset <= 384; ++offset)
            add_offset_for_banks(buckets->tail, &buckets->tail_count,
                                 (int)(sizeof(buckets->tail) / sizeof(buckets->tail[0])),
                                 peer, offset, "tail");
    }
    buckets->range_hi = buckets->tail_count ? 384 : 256;
    if (predicted_hi > buckets->range_hi) buckets->range_hi = predicted_hi;
    buckets->unique_count = 1 + banks * buckets->range_hi;
}

static int scheduler_self_test(void) {
    PeerModel peer;
    TargetBuckets buckets;
    Target target;
    int failures = 0;
    memset(&peer, 0, sizeof(peer));
    strcpy(peer.model.mode, "fast");
    peer.model.direction = 1;
    peer.model.rate = 320;
    peer.model.bank1 = peer.model.bank2 = 64250;
    build_targets(&peer, &buckets);
    if (buckets.range_hi != 2752 || fast_center_offset(320, 0) != 256 ||
        fast_center_offset(320, 10) != 576)
        failures++;
    make_peer_target(&peer, 0, 1400, "fast-move", &target);
    if (target.port != 115 || target.offset != 1400) failures++;
    printf("scheduler self-test: %s (%d failure(s))\n",
           failures ? "FAIL" : "PASS", failures);
    return failures == 0 ? 0 : 1;
}

static const Target *pick_target(const Target *bucket, int count, int tick,
                                 int worker, int multiplier, int salt) {
    unsigned int index;
    if (count <= 0) return NULL;
    index = (unsigned int)(tick * multiplier + worker * (multiplier + 2) + salt);
    return &bucket[index % (unsigned int)count];
}

static void send_p7(SOCKET socket, const char *peer_ip, const Target *target,
                    const char *room_tag, int attempt, const char *client_id,
                    int sender_worker, int tick, int *sequence, SendStats *stats) {
    char packet[256];
    snprintf(packet, sizeof(packet), "P7 %s %d %s %d %d %d %s %d %d",
             room_tag, attempt, client_id, (*sequence)++, sender_worker,
             target->port, target->lane, target->offset, tick);
    send_text(socket, packet, peer_ip, target->port, stats);
}

static int handle_peer_packet(const char *buf, const struct sockaddr_in *from, int wi,
                              SOCKET control, Worker *workers, const char *tag,
                              int attempt, const char *client_id, int *have_peer,
                              int *have_ack,
                              struct sockaddr_in *peer_real, int *winning_worker,
                              SendStats *stats) {
    char command[8], packet_tag[16], sender[64], reply[256], ip[64], lane[16] = "legacy";
    int packet_attempt, seq, sender_worker = -99, target_port = 0, offset = 0, tick = -1;
    int fields;
    SOCKET reply_socket;
    fields = sscanf(buf, "%7s %15s %d %63s %d %d %d %15s %d %d",
                    command, packet_tag, &packet_attempt, sender, &seq,
                    &sender_worker, &target_port, lane, &offset, &tick);
    if (fields < 5) return 0;
    if (strcmp(packet_tag, tag) || packet_attempt != attempt || !strcmp(sender, client_id)) return 0;
    InetNtopA(AF_INET, (void *)&from->sin_addr, ip, sizeof(ip));
    if (!strcmp(command, "P7")) {
        reply_socket = wi >= 0 ? workers[wi].socket : control;
        snprintf(reply, sizeof(reply), "A7 %s %d %s %d %d %d %s %d %d",
                 tag, attempt, client_id, seq, sender_worker, target_port,
                 lane, offset, tick);
        send_addr(reply_socket, reply, from, stats);
        if (!*have_peer) {
            *have_peer = 1;
            *peer_real = *from;
            *winning_worker = wi;
            if (wi < 0)
                say("[命中] 首个 P7，local_socket=control local_port=%d，真实源=%s:%d，"
                    "peer_worker=%d target=%d lane=%s offset=%d tick=%d",
                    socket_local_port(control), ip, ntohs(from->sin_port),
                    sender_worker, target_port, lane, offset, tick);
            else
                say("[命中] 首个 P7，local_socket=worker#%d local_port=%d，真实源=%s:%d，"
                    "peer_worker=%d target=%d lane=%s offset=%d tick=%d",
                    wi, workers[wi].local_port, ip, ntohs(from->sin_port),
                    sender_worker, target_port, lane, offset, tick);
            event_json("peer_packet",
                       "\"kind\":\"P7\",\"attempt\":%d,\"worker\":%d,\"local_port\":%d,"
                       "\"source\":\"%s:%d\",\"seq\":%d,\"peer_worker\":%d,"
                       "\"target_port\":%d,\"lane\":\"%s\",\"offset\":%d,\"tick\":%d",
                       attempt, wi, wi < 0 ? socket_local_port(control) : workers[wi].local_port,
                       ip, ntohs(from->sin_port), seq, sender_worker, target_port,
                       lane, offset, tick);
        }
        return 1;
    }
    if (!strcmp(command, "A7")) {
        if (*have_ack) return 0;
        *have_ack = 1;
        *have_peer = 1;
        *peer_real = *from;
        *winning_worker = wi;
        event_json("peer_packet",
                   "\"kind\":\"A7\",\"attempt\":%d,\"worker\":%d,\"local_port\":%d,"
                   "\"source\":\"%s:%d\",\"seq\":%d,\"acked_worker\":%d,"
                   "\"target_port\":%d,\"lane\":\"%s\",\"offset\":%d,\"tick\":%d",
                   attempt, wi, wi < 0 ? socket_local_port(control) : workers[wi].local_port,
                   ip, ntohs(from->sin_port), seq, sender_worker, target_port,
                   lane, offset, tick);
        return 2;
    }
    return 0;
}

static int spray_attempt(SOCKET control, Worker *workers, int worker_count,
                         const Model *self, const PeerModel *peer, const char *room_tag,
                         const char *client_id) {
    int tick = 0, sequence = 0, success = 0, have_peer = 0, have_ack = 0, winning = -2;
    int peer_fast = !strcmp(peer->model.mode, "fast");
    int self_fast = !strcmp(self->mode, "fast");
    int low_fanout = peer_fast || self_fast;
    int fast_rate = peer->model.rate;
    char packet[180], buf[MAX_LINE], real_ip[64];
    struct sockaddr_in from, peer_real;
    ULONGLONG start, deadline, success_ms = 0;
    SendStats stats = {0};
    TargetBuckets buckets;
    Target control_target = {0};
    build_targets(peer, &buckets);
    control_target.port = peer->control_port;
    snprintf(control_target.lane, sizeof(control_target.lane), "control");
    fast_rate = clamp_fast_rate(fast_rate);
    if (low_fanout)
        say("[GO#%d] peer_mode=%s self_mode=%s peer control=%d banks=[%d,%d] "
            "rate=%d/s，策略=%s phase=500ms horizon=%d",
            peer->attempt, peer->model.mode, self->mode, peer->control_port,
            peer->model.bank1, peer->model.bank2, peer->model.rate,
            peer_fast ? "fast-target" : "fast-sender",
            buckets.range_hi);
    else
        say("[GO#%d] mode=%s 候选=%d，peer control=%d，banks=[%d,%d]，"
            "固定覆盖=1..%d low=%d mid=%d predicted=%d tail=%d",
            peer->attempt, peer->model.mode, buckets.unique_count, peer->control_port,
            peer->model.bank1, peer->model.bank2, buckets.range_hi,
            buckets.low_count, buckets.mid_count, buckets.predicted_count, buckets.tail_count);
    event_json("go",
               "\"attempt\":%d,\"peer_ip\":\"%s\",\"peer_control\":%d,"
               "\"mode\":\"%s\",\"self_mode\":\"%s\",\"banks\":%d,"
               "\"bank1\":%d,\"bank2\":%d,"
               "\"rate\":%d,\"range_lo\":1,\"range_hi\":%d,\"targets\":%d,"
               "\"low\":%d,\"mid\":%d,\"predicted\":%d,\"tail\":%d,"
               "\"strategy\":\"%s\",\"phase_ms\":%d",
               peer->attempt, peer->ip, peer->control_port, peer->model.mode, self->mode,
               peer->model.banks, peer->model.bank1, peer->model.bank2,
               peer->model.rate, buckets.range_hi, buckets.unique_count,
               buckets.low_count, buckets.mid_count,
               buckets.predicted_count, buckets.tail_count,
               peer_fast ? "fast-target" : (self_fast ? "fast-sender" : "layered"),
               low_fanout ? 500 : 0);
    start = mono_ms();
    deadline = start + SPRAY_MS;
    while ((!success && mono_ms() < deadline) ||
           (success && mono_ms() < success_ms + 3000)) {
        int i, wi, result;
        ULONGLONG tick_start = mono_ms();
        if (!success) {
            send_p7(control, peer->ip, &control_target, room_tag, peer->attempt,
                    client_id, -1, tick, &sequence, &stats);
            if (low_fanout) {
                int phase = tick / 5;
                int center = peer_fast ? fast_center_offset(fast_rate, tick) : 1;
                for (i = 0; i < worker_count; ++i) {
                    int moving_offset;
                    Target anchor, moving;
                    if (peer_fast) {
                        int jitter = ((i * 37 + phase * 53) % 385) - 192;
                        moving_offset = center + jitter;
                    } else {
                        int horizon = buckets.range_hi > 0 ? buckets.range_hi : 384;
                        moving_offset = 1 +
                            ((phase * worker_count + i) * 37) % horizon;
                    }
                    if (moving_offset < 1) moving_offset = 1;
                    if (moving_offset > buckets.range_hi)
                        moving_offset = buckets.range_hi;
                    make_peer_target(peer, i, 1 + (i * 17) % 64,
                                     "fast-anchor", &anchor);
                    make_peer_target(peer, i, moving_offset, "fast-move", &moving);
                    send_p7(workers[i].socket, peer->ip, &control_target, room_tag,
                            peer->attempt, client_id, i, tick, &sequence, &stats);
                    send_p7(workers[i].socket, peer->ip, &anchor, room_tag,
                            peer->attempt, client_id, i, tick, &sequence, &stats);
                    send_p7(workers[i].socket, peer->ip, &moving, room_tag,
                            peer->attempt, client_id, i, tick, &sequence, &stats);
                }
            } else {
                for (i = 0; i < worker_count; ++i) {
                    const Target *low1, *low2, *mid, *fourth;
                    send_p7(workers[i].socket, peer->ip, &control_target, room_tag,
                            peer->attempt, client_id, i, tick, &sequence, &stats);
                    low1 = pick_target(buckets.low, buckets.low_count, tick, i, 37, 0);
                    mid = pick_target(buckets.mid, buckets.mid_count, tick, i, 53, 11);
                    if (low1)
                        send_p7(workers[i].socket, peer->ip, low1, room_tag,
                                peer->attempt, client_id, i, tick, &sequence, &stats);
                    if (mid)
                        send_p7(workers[i].socket, peer->ip, mid, room_tag,
                                peer->attempt, client_id, i, tick, &sequence, &stats);
                    if (tick < 30) {
                        low2 = pick_target(buckets.low, buckets.low_count, tick, i, 71, 43);
                        fourth = (tick & 1)
                            ? pick_target(buckets.predicted, buckets.predicted_count,
                                          tick, i, 97, 17)
                            : pick_target(buckets.tail, buckets.tail_count, tick, i, 89, 29);
                        if (!fourth)
                            fourth = pick_target(buckets.predicted, buckets.predicted_count,
                                                 tick, i, 97, 17);
                        if (low2)
                            send_p7(workers[i].socket, peer->ip, low2, room_tag,
                                    peer->attempt, client_id, i, tick, &sequence, &stats);
                        if (fourth)
                            send_p7(workers[i].socket, peer->ip, fourth, room_tag,
                                    peer->attempt, client_id, i, tick, &sequence, &stats);
                    }
                }
            }
            if (tick % 10 == 9)
                event_json("spray_progress",
                           "\"attempt\":%d,\"elapsed_ms\":%d,\"attempted\":%d,"
                           "\"sent\":%d,\"errors\":%d",
                           peer->attempt, (tick + 1) * TICK_MS,
                           stats.attempted, stats.sent, stats.errors);
        } else {
            SOCKET winner_socket = winning >= 0 ? workers[winning].socket : control;
            snprintf(packet, sizeof(packet), "A7 %s %d %s %d",
                     room_tag, peer->attempt, client_id, sequence++);
            for (i = 0; i < 4; ++i) send_addr(winner_socket, packet, &peer_real, &stats);
        }
        do {
            int wait = (int)(TICK_MS - (mono_ms() - tick_start));
            if (wait < 0) wait = 0;
            result = recv_any(control, workers, worker_count, wait, buf, sizeof(buf), &from, &wi);
            if (result > 0) {
                int kind = handle_peer_packet(buf, &from, wi, control, workers, room_tag,
                                              peer->attempt, client_id, &have_peer,
                                              &have_ack,
                                              &peer_real, &winning, &stats);
                if (kind == 2 && !success) {
                    success = 1;
                    success_ms = mono_ms();
                    InetNtopA(AF_INET, (void *)&from.sin_addr, real_ip, sizeof(real_ip));
                    if (wi < 0)
                        say("[成功] 双向确认，获胜 socket=control，真实端点=%s:%d，耗时=%llums",
                            real_ip, ntohs(from.sin_port),
                            (unsigned long long)(success_ms - start));
                    else
                        say("[成功] 双向确认，获胜 socket=worker#%d，真实端点=%s:%d，耗时=%llums",
                            wi, real_ip, ntohs(from.sin_port),
                            (unsigned long long)(success_ms - start));
                    event_json("success",
                               "\"attempt\":%d,\"worker\":%d,\"source\":\"%s:%d\","
                               "\"latency_ms\":%llu",
                               peer->attempt, wi, real_ip, ntohs(from.sin_port),
                               (unsigned long long)(success_ms - start));
                }
            }
        } while (result > 0 && mono_ms() - tick_start < TICK_MS);
        tick++;
    }
    event_json("spray_summary",
               "\"attempt\":%d,\"ticks\":%d,\"attempted\":%d,\"sent\":%d,"
               "\"errors\":%d,\"one_way\":%s,\"success\":%s,\"winner\":%d",
               peer->attempt, tick, stats.attempted, stats.sent, stats.errors,
               have_peer ? "true" : "false", success ? "true" : "false", winning);
    say("[轮次汇总] attempted=%d sent=%d errors=%d one_way=%s success=%s",
        stats.attempted, stats.sent, stats.errors,
        have_peer ? "yes" : "no", success ? "yes" : "no");
    return success;
}

static int parse_peer_report(const char *line, PeerModel *peer) {
    char mode[16];
    int matched = sscanf(line,
        "PEERREPORT7 %d %63s %d %15s %d %d %d %d %d %d %d %d",
        &peer->attempt, peer->ip, &peer->control_port, mode,
        &peer->model.direction, &peer->model.confidence, &peer->model.rate,
        &peer->model.bank1, &peer->model.bank2, &peer->model.spread,
        &peer->model.step, &peer->workers);
    if (matched != 12) return 0;
    snprintf(peer->model.mode, sizeof(peer->model.mode), "%s", mode);
    peer->model.banks = peer->model.bank1 == peer->model.bank2 ? 1 : 2;
    peer->model.fast_cycle = !strcmp(mode, "fast");
    peer->model.volatile_dual = !strcmp(mode, "volatile");
    return peer->control_port > 0 && peer->model.bank1 > 0;
}

static int wait_pair(SOCKET control, const char *server_ip, const char *room,
                     const char *client_id, char *peer_id, char *peer_ip,
                     int *peer_control, int *my_control, ULONGLONG deadline) {
    ULONGLONG last_join = 0;
    char line[MAX_LINE], message[160];
    struct sockaddr_in from;
    while (mono_ms() < deadline && !peer_ip[0]) {
        int wi;
        if (mono_ms() - last_join >= 1500) {
            snprintf(message, sizeof(message), "JOIN7 %s %s", room, client_id);
            send_text(control, message, server_ip, PORT_A, NULL);
            last_join = mono_ms();
        }
        if (recv_any(control, NULL, 0, 250, line, sizeof(line), &from, &wi) <= 0) continue;
        if (!strncmp(line, "JOIN7ACK ", 9)) {
            char cid[64], ip[64];
            int port;
            if (sscanf(line, "JOIN7ACK %63s %63s %d", cid, ip, &port) == 3 &&
                !strcmp(cid, client_id)) *my_control = port;
        } else if (!strncmp(line, "PEER7 ", 6)) {
            if (sscanf(line, "PEER7 %63s %63s %d", peer_id, peer_ip, peer_control) == 3) {
                say("[配对] peer=%s %s:%d，我方 control 映射=%d",
                    peer_id, peer_ip, *peer_control, *my_control);
                event_json("paired",
                           "\"peer_id\":\"%s\",\"peer_ip\":\"%s\","
                           "\"peer_control\":%d,\"my_control\":%d",
                           peer_id, peer_ip, *peer_control, *my_control);
            }
        }
    }
    return peer_ip[0] != 0;
}

static int wait_go(SOCKET control, const char *server_ip, const char *room,
                   const char *client_id, int generation, const Model *model,
                   int workers, PeerModel *peer) {
    char report[320], line[MAX_LINE];
    struct sockaddr_in from;
    ULONGLONG deadline = mono_ms() + 7000, last_report = 0;
    int have_report = 0, go_attempt = 0, go_delay = 0;
    int logged_attempt = -1, report_duplicates = 0;
    snprintf(report, sizeof(report),
             "REPORT7 %s %s %d %s %d %d %d %d %d %d %d %d",
             room, client_id, generation, model->mode, model->direction,
             model->confidence, model->rate, model->bank1, model->bank2,
             model->spread, model->step, workers);
    while (mono_ms() < deadline) {
        int wi;
        if (mono_ms() - last_report >= 1000) {
            send_text(control, report, server_ip, PORT_A, NULL);
            last_report = mono_ms();
        }
        if (recv_any(control, NULL, 0, 200, line, sizeof(line), &from, &wi) <= 0) continue;
        if (!strncmp(line, "PEERREPORT7 ", 12)) {
            have_report = parse_peer_report(line, peer);
            if (have_report) {
                if (peer->attempt == logged_attempt) {
                    report_duplicates++;
                } else {
                    logged_attempt = peer->attempt;
                    say("[对端模型] attempt=%d mode=%s conf=%.1f%% rate=%d/s "
                        "banks=[%d,%d] spread=%d step=%d workers=%d",
                        peer->attempt, peer->model.mode, peer->model.confidence / 10.0,
                        peer->model.rate, peer->model.bank1, peer->model.bank2,
                        peer->model.spread, peer->model.step, peer->workers);
                    event_json("peer_model",
                               "\"attempt\":%d,\"mode\":\"%s\",\"confidence\":%d,"
                               "\"rate\":%d,\"bank1\":%d,\"bank2\":%d,"
                               "\"spread\":%d,\"step\":%d,\"workers\":%d",
                               peer->attempt, peer->model.mode, peer->model.confidence,
                               peer->model.rate, peer->model.bank1, peer->model.bank2,
                               peer->model.spread, peer->model.step, peer->workers);
                }
            }
        } else {
            int parsed_attempt, parsed_delay;
            if (sscanf(line, "GO7 %d %d", &parsed_attempt, &parsed_delay) == 2) {
                go_attempt = parsed_attempt;
                go_delay = parsed_delay;
            }
        }
        /* UDP can reorder the back-to-back model and GO datagrams. Keep an
         * early GO and act as soon as the matching peer model arrives. */
        if (have_report && go_attempt == peer->attempt) {
            if (go_delay < 0) go_delay = 0;
            if (go_delay > 3000) go_delay = 3000;
            event_json("go_scheduled",
                       "\"attempt\":%d,\"delay_ms\":%d,\"report_duplicates\":%d",
                       go_attempt, go_delay, report_duplicates);
            Sleep((DWORD)go_delay);
            return 1;
        }
    }
    return 0;
}

static int run_client(const char *server_ip, const char *room, int worker_count,
                      int duration_sec, const char *log_dir) {
    WSADATA wsa;
    SOCKET control;
    Worker workers[MAX_WORKERS];
    Model previous, model;
    PeerModel peer;
    char client_id[32], peer_id[64] = "", peer_ip[64] = "", room_tag[16];
    int peer_control = -1, my_control = -1, generation = 0, success = 0;
    ULONGLONG overall_deadline;
    uint32_t room_hash = fnv1a(room);
    SetConsoleOutputCP(CP_UTF8);
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "WSAStartup 失败\n");
        return 0;
    }
    logger_init(log_dir);
    random_id(client_id, sizeof(client_id));
    snprintf(room_tag, sizeof(room_tag), "%08lx", (unsigned long)room_hash);
    control = make_udp();
    if (control == INVALID_SOCKET) {
        say("无法创建 control UDP socket, WSA=%d", WSAGetLastError());
        logger_close();
        WSACleanup();
        return 0;
    }
    memset(&previous, 0, sizeof(previous));
    say("=== natpunch v%s / 自适应 NAT4↔NAT4 ===", VERSION);
    say("client=%s room_tag=%s control_local=%d workers=%d duration=%ds",
        client_id, room_tag, socket_local_port(control), worker_count, duration_sec);
    say("日志: %s", g_log.human_path);
    say("结构化日志: %s", g_log.jsonl_path);
    event_json("session_start",
               "\"version\":\"%s\",\"client_id\":\"%s\",\"room_tag\":\"%s\","
               "\"control_local\":%d,\"workers\":%d,\"duration_sec\":%d,"
               "\"server\":\"%s\"",
               VERSION, client_id, room_tag, socket_local_port(control),
               worker_count, duration_sec, server_ip);
    overall_deadline = mono_ms() + (ULONGLONG)duration_sec * 1000;
    if (!wait_pair(control, server_ip, room, client_id, peer_id, peer_ip,
                   &peer_control, &my_control, overall_deadline)) {
        say("[失败] 在总时限内没有配对到对端");
        event_json("session_end", "\"result\":\"pair_timeout\"");
        closesocket(control);
        logger_close();
        WSACleanup();
        return 0;
    }
    while (mono_ms() < overall_deadline && !success) {
        generation++;
        if (!create_workers(workers, worker_count)) {
            say("[失败] 无法创建 %d 个工作 socket, WSA=%d", worker_count, WSAGetLastError());
            break;
        }
        say("[校准#%d] 开始，保持 %d 个 socket 到本轮结束", generation, worker_count);
        calibrate(control, workers, worker_count, server_ip, client_id, generation);
        fit_model(workers, worker_count, my_control, generation > 1 ? &previous : NULL, &model);
        {
            int sample_index;
            for (sample_index = 0; sample_index < worker_count; ++sample_index) {
                event_json("probe_sample",
                           "\"generation\":%d,\"worker\":%d,\"local_port\":%d,"
                           "\"ip_a\":\"%s\",\"mapped_a\":%d,\"rtt_a\":%d,"
                           "\"ip_b\":\"%s\",\"mapped_b\":%d,\"rtt_b\":%d",
                           generation, sample_index, workers[sample_index].local_port,
                           workers[sample_index].ip_a, workers[sample_index].mapped_a,
                           workers[sample_index].rtt_a_ms, workers[sample_index].ip_b,
                           workers[sample_index].mapped_b, workers[sample_index].rtt_b_ms);
            }
        }
        say("[校准#%d] mode=%s conf=%.1f%% A=%d/%d B=%d/%d reuse=%d "
            "IP切换=%d volatile=%d fast=%d inherited=%d banks=[%d,%d] "
            "step=%d spread=%d rate=%d/s rtt=%dms",
            generation, model.mode, model.confidence / 10.0, model.valid_a, worker_count,
            model.valid_b, worker_count, model.reuse, model.ip_changes,
            model.volatile_dual, model.fast_cycle, model.inherited_history,
            model.bank1, model.bank2,
            model.step, model.spread, model.rate, model.median_rtt);
        event_json("calibration",
                   "\"generation\":%d,\"mode\":\"%s\",\"confidence\":%d,"
                   "\"valid_a\":%d,\"valid_b\":%d,\"reuse\":%d,\"ip_changes\":%d,"
                   "\"volatile\":%s,\"fast_cycle\":%s,\"inherited\":%s,\"banks\":%d,"
                   "\"bank1\":%d,\"bank2\":%d,\"step\":%d,\"spread\":%d,"
                   "\"rate\":%d,\"median_rtt\":%d",
                   generation, model.mode, model.confidence, model.valid_a,
                   model.valid_b, model.reuse, model.ip_changes,
                   model.volatile_dual ? "true" : "false",
                   model.fast_cycle ? "true" : "false",
                   model.inherited_history ? "true" : "false", model.banks,
                   model.bank1, model.bank2,
                   model.step, model.spread, model.rate, model.median_rtt);
        memset(&peer, 0, sizeof(peer));
        if (!wait_go(control, server_ip, room, client_id, generation, &model,
                     worker_count, &peer)) {
            say("[校准#%d] 对端报告/GO 超时，重新校准", generation);
            event_json("attempt_failed",
                       "\"generation\":%d,\"reason\":\"coordination_timeout\"", generation);
            previous = model;
            close_workers(workers, worker_count, -1);
            continue;
        }
        success = spray_attempt(control, workers, worker_count, &model,
                                &peer, room_tag, client_id);
        previous = model;
        if (!success)
            event_json("attempt_failed",
                       "\"generation\":%d,\"attempt\":%d,\"reason\":\"no_confirmation\"",
                       generation, peer.attempt);
        close_workers(workers, worker_count, -1);
    }
    say("============================================");
    say("结果: %s", success ? "打洞成功 [OK]" : "未打通 [X]");
    say("============================================");
    event_json("session_end", "\"result\":\"%s\",\"generations\":%d",
               success ? "success" : "failed", generation);
    closesocket(control);
    logger_close();
    WSACleanup();
    return success;
}

static void usage(void) {
    printf("natpunch v%s\n", VERSION);
    printf("用法: natpunch-v7.2.exe client <server_ip> <room> "
           "[--sockets N] [--duration SEC] [--log-dir PATH] [--no-pause]\n");
    printf("      natpunch-v7.2.exe --self-test\n");
}

int main(int argc, char **argv) {
    int workers = DEFAULT_WORKERS, duration = 180, no_pause = 0, i, rc;
    const char *log_dir = NULL;
    setvbuf(stdout, NULL, _IONBF, 0);
    if (argc == 2 && !strcmp(argv[1], "--self-test")) {
        int failed = 0;
        if (model_self_test() != 0) failed = 1;
        if (scheduler_self_test() != 0) failed = 1;
        return failed;
    }
    if (argc < 4 || strcmp(argv[1], "client")) {
        usage();
        return 2;
    }
    for (i = 4; i < argc; ++i) {
        if (!strcmp(argv[i], "--sockets") && i + 1 < argc) workers = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--duration") && i + 1 < argc) duration = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--log-dir") && i + 1 < argc) log_dir = argv[++i];
        else if (!strcmp(argv[i], "--no-pause")) no_pause = 1;
        else {
            fprintf(stderr, "未知参数: %s\n", argv[i]);
            return 2;
        }
    }
    if (workers < 4 || workers > MAX_WORKERS || duration < 10 || duration > 1800) {
        fprintf(stderr, "--sockets 范围 4..%d，--duration 范围 10..1800\n", MAX_WORKERS);
        return 2;
    }
    rc = run_client(argv[2], argv[3], workers, duration, log_dir) ? 0 : 1;
    if (!no_pause) {
        printf("\n(按回车退出)\n");
        getchar();
    }
    return rc;
}
