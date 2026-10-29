/*
 * Медиаядро звонка MAX. Что и зачем — в lovit_call.h.
 *
 * Порядок жизни звонка:
 *   1. create — сокет UDP, ICE-логин и пароль, свой сертификат DTLS;
 *   2. add_host / add_stun / add_turn — свои кандидаты: адреса в сети,
 *      адрес снаружи, ретранслятор. Каждый уходит событием «cand», и
 *      вызывающий пересылает его собеседнику;
 *   3. set_remote / add_remote — что прислал собеседник;
 *   4. проверки ICE: отвечаем на чужие, шлём свои. Звонящий помечает
 *      выбранный путь флагом USE-CANDIDATE — по нему и пойдёт звук;
 *   5. DTLS по выбранному пути, из него ключи SRTP;
 *   6. RTP с Opus в обе стороны.
 *
 * Один сокет на всё: и свои адреса, и STUN, и TURN. Пакеты разбираются по
 * первому байту (RFC 7983): 0–3 — STUN, 20–63 — DTLS, 128–191 — RTP/RTCP.
 */

#include "lovit_call.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef SOCKET sock_t;
#define BAD_SOCKET INVALID_SOCKET
#define close_socket closesocket
#else
#include <sys/types.h>
#include <sys/socket.h>
#include <sys/select.h>
#include <sys/time.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <fcntl.h>
#include <unistd.h>
#include <time.h>
#include <mach/mach_time.h>
typedef int sock_t;
#define BAD_SOCKET (-1)
#define close_socket close
#endif

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/md5.h>
#include <openssl/rand.h>
#include <openssl/x509.h>
#include <openssl/ec.h>
#include <openssl/srtp.h>

/* В VS 2013 — а телефону нужна именно она — snprintf ещё нет, есть лишь
 * vsnprintf, и тот не ставит ноль в конце при обрезке. */
#if defined(_MSC_VER) && _MSC_VER < 1900
static int lovit_snprintf(char *out, size_t size, const char *format, ...)
{
    va_list args;
    int written;
    if (size == 0) return 0;
    va_start(args, format);
    written = vsnprintf(out, size, format, args);
    va_end(args);
    out[size - 1] = 0;
    return written < 0 ? (int)size - 1 : written;
}
#define snprintf lovit_snprintf
#endif

/* ------------------------------------------------------------------ */
/* Постоянные                                                          */
/* ------------------------------------------------------------------ */

#define MAGIC_COOKIE 0x2112A442u

#define STUN_BINDING_REQUEST      0x0001
#define STUN_BINDING_SUCCESS      0x0101
#define STUN_BINDING_ERROR        0x0111
#define TURN_ALLOCATE_REQUEST     0x0003
#define TURN_ALLOCATE_SUCCESS     0x0103
#define TURN_ALLOCATE_ERROR       0x0113
#define TURN_REFRESH_REQUEST      0x0004
#define TURN_REFRESH_SUCCESS      0x0104
#define TURN_REFRESH_ERROR        0x0114
#define TURN_SEND_INDICATION      0x0016
#define TURN_DATA_INDICATION      0x0017
#define TURN_PERMISSION_REQUEST   0x0008
#define TURN_PERMISSION_SUCCESS   0x0108
#define TURN_PERMISSION_ERROR     0x0118

#define ATTR_MAPPED_ADDRESS       0x0001
#define ATTR_USERNAME             0x0006
#define ATTR_MESSAGE_INTEGRITY    0x0008
#define ATTR_ERROR_CODE           0x0009
#define ATTR_LIFETIME             0x000D
#define ATTR_XOR_PEER_ADDRESS     0x0012
#define ATTR_DATA                 0x0013
#define ATTR_REALM                0x0014
#define ATTR_NONCE                0x0015
#define ATTR_XOR_RELAYED_ADDRESS  0x0016
#define ATTR_REQUESTED_TRANSPORT  0x0019
#define ATTR_XOR_MAPPED_ADDRESS   0x0020
#define ATTR_PRIORITY             0x0024
#define ATTR_USE_CANDIDATE        0x0025
#define ATTR_FINGERPRINT          0x8028
#define ATTR_ICE_CONTROLLED       0x8029
#define ATTR_ICE_CONTROLLING      0x802A

#define MAX_PACKET 1600
#define MAX_PAIRS 48
#define MAX_PERMISSIONS 24
#define MAX_EVENTS 96
#define EVENT_SIZE 256
#define RX_SLOTS 64

/* Сколько ждать и сколько раз повторять. */
#define CHECK_PACE_MS 20.0          /* между двумя проверками ICE */
#define CHECK_RETRY_MS 400.0        /* повтор одной проверки */
#define CHECK_TRIES 60              /* ~25 с, дальше пара считается мёртвой */
#define CONSENT_MS 4000.0           /* проверка выбранного пути, чтобы NAT не закрылся */
#define SERVER_RETRY_MS 500.0       /* первый повтор запроса к STUN/TURN */
#define SERVER_RETRY_MOST 3000.0    /* дальше промежуток растёт до этого */
#define SERVER_TRIES 7              /* ~15 с: TURN у MAX бывает отвечает через четыре */
#define PERMISSION_REFRESH_MS 240000.0
#define SILENCE_FAIL_MS 30000.0     /* столько молчания на выбранном пути — звонок развалился */
#define FALLBACK_SELECT_MS 4000.0   /* ждать выбора от звонящего, потом выбрать самим */
#define SR_EVERY_MS 2000.0

/* ------------------------------------------------------------------ */
/* Типы                                                                */
/* ------------------------------------------------------------------ */

typedef struct {
    uint32_t ip;     /* порядок узла */
    uint16_t port;
} addr4;

enum { PAIR_WAITING = 0, PAIR_SUCCEEDED = 1, PAIR_FAILED = 2 };

typedef struct {
    addr4 remote;
    int relayed;            /* через TURN, а не прямо */
    int state;
    int tries;
    double next;
    uint8_t txid[12];
    int nominated;
    double last_seen;       /* когда с этого пути что-то приходило */
} pair_t;

typedef struct {
    uint8_t key[16];        /* шифрования */
    uint8_t salt[14];
    uint8_t auth[20];
    uint8_t rtcp_key[16];
    uint8_t rtcp_salt[14];
    uint8_t rtcp_auth[20];
    uint32_t roc;
    uint16_t last_seq;
    int started;
    uint32_t rtcp_index;
} srtp_side;

typedef struct {
    uint8_t data[MAX_PACKET];
    int length;
    uint32_t timestamp;
    uint32_t sequence;
} rx_slot;

typedef struct {
    sock_t sock;
    uint16_t local_port;

    char local_ufrag[16];
    char local_pwd[40];
    char remote_ufrag[128];
    char remote_pwd[256];
    uint8_t remote_fp[32];
    int have_remote_fp;
    int have_remote;
    uint8_t tiebreaker[8];

    /* STUN */
    addr4 stun;
    int stun_state;          /* 0 нет, 1 спрашиваем, 2 готово, 3 не вышло */
    uint8_t stun_tx[12];
    double stun_next;
    int stun_tries;

    /* TURN */
    addr4 turn;
    char turn_user[256];
    char turn_pass[256];
    char realm[256];
    uint8_t nonce[256];
    int nonce_length;
    uint8_t turn_key[16];
    int turn_state;          /* 0 нет, 1 без подписи, 2 с подписью, 3 готово, 4 не вышло */
    uint8_t turn_tx[12];
    double turn_next;
    int turn_tries;
    addr4 relay;
    addr4 mapped;            /* адрес снаружи, каким его видит TURN */
    double turn_refresh_at;
    uint32_t lifetime;

    addr4 permissions[MAX_PERMISSIONS];
    int permission_count;
    int permissions_sent;    /* сколько из них уже заявлено */
    double permission_refresh_at;
    uint8_t permission_tx[12];
    double permission_next;
    int permission_tries;

    /* ICE */
    pair_t pairs[MAX_PAIRS];
    int pair_count;
    double next_check;
    int selected;            /* индекс пары или -1 */
    double first_success_at;
    double last_receive;
    double consent_next;

    /* DTLS */
    SSL_CTX *ctx;
    SSL *ssl;
    BIO *rbio;
    BIO *wbio;
    EVP_PKEY *key;
    X509 *cert;
    char fingerprint[100];
    int dtls_state;          /* 0 ещё нет, 1 рукопожатие, 2 готово, 3 не вышло */
    int controlling;         /* ICE: мы ведём (звоним сами) или подчиняемся (отвечаем) */
    int dtls_client;         /* DTLS: мы клиент (a=setup:active) или сервер (passive) */

    /* SRTP и RTP */
    srtp_side out;
    srtp_side in;
    uint32_t ssrc;
    uint16_t seq;
    int opus_pt;
    uint32_t sent_packets;
    uint32_t sent_octets;
    uint32_t last_timestamp;
    double sr_next;
    uint32_t received_packets;

    /* Для отчётов получателя (RTCP RR): что пришло от собеседника. */
    uint32_t remote_ssrc;
    uint32_t base_seq;
    uint32_t max_seq;        /* сквозной, с числом переполнений */
    int have_rtp;
    uint32_t expected_prior;
    uint32_t received_prior;
    uint32_t last_sr;        /* средние 32 бита NTP из его последнего SR */
    double last_sr_at;

    rx_slot rx[RX_SLOTS];
    int rx_head;
    int rx_tail;

    char events[MAX_EVENTS][EVENT_SIZE];
    int event_head;
    int event_tail;

    int failed;
    int stage;               /* что ядро делало в последний миг — для отчёта о сбое */
} call_t;

/* Шаги для отчёта о сбое: если ядро упадёт, в журнале будет, на чём. */
enum {
    STAGE_IDLE = 0, STAGE_RECEIVE, STAGE_STUN, STAGE_TURN, STAGE_ICE, STAGE_DTLS,
    STAGE_RTP_IN, STAGE_RTCP_IN, STAGE_RTP_OUT, STAGE_RTCP_OUT, STAGE_TIMERS
};

/* ------------------------------------------------------------------ */
/* Мелочи                                                              */
/* ------------------------------------------------------------------ */

static double now_ms(void)
{
#ifdef _WIN32
    return (double)GetTickCount64();
#else
    static mach_timebase_info_data_t base;
    if (base.denom == 0) mach_timebase_info(&base);
    return (double)mach_absolute_time() * base.numer / base.denom / 1000000.0;
#endif
}

static void put16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v >> 8); p[1] = (uint8_t)v; }
static void put32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16); p[2] = (uint8_t)(v >> 8); p[3] = (uint8_t)v;
}
static uint16_t get16(const uint8_t *p) { return (uint16_t)((p[0] << 8) | p[1]); }
static uint32_t get32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}

static void emit(call_t *c, const char *format, ...)
{
    va_list args;
    int next = (c->event_tail + 1) % MAX_EVENTS;
    if (next == c->event_head) return;   /* очередь полна — лишнее теряем */

    va_start(args, format);
    vsnprintf(c->events[c->event_tail], EVENT_SIZE, format, args);
    va_end(args);
    c->events[c->event_tail][EVENT_SIZE - 1] = 0;
    c->event_tail = next;
}

static void addr_text(addr4 a, char *out, size_t size)
{
    snprintf(out, size, "%u.%u.%u.%u:%u",
             (a.ip >> 24) & 255, (a.ip >> 16) & 255, (a.ip >> 8) & 255, a.ip & 255, a.port);
}

static void ip_text(uint32_t ip, char *out, size_t size)
{
    snprintf(out, size, "%u.%u.%u.%u", (ip >> 24) & 255, (ip >> 16) & 255, (ip >> 8) & 255, ip & 255);
}

static int addr_equal(addr4 a, addr4 b) { return a.ip == b.ip && a.port == b.port; }

static int parse_ip(const char *text, uint32_t *ip)
{
    unsigned int a, b, c, d;
    char tail;
    if (sscanf(text, "%u.%u.%u.%u%c", &a, &b, &c, &d, &tail) != 4) return 0;
    if (a > 255 || b > 255 || c > 255 || d > 255) return 0;
    *ip = (a << 24) | (b << 16) | (c << 8) | d;
    return 1;
}

/* Имя в IPv4. Ждёт ответа DNS — зовётся при подготовке, не в разговоре. */
static int resolve(const char *host, int port, addr4 *out)
{
    struct addrinfo hints, *found = NULL;
    uint32_t ip;

    if (parse_ip(host, &ip)) {
        out->ip = ip;
        out->port = (uint16_t)port;
        return 1;
    }

    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    if (getaddrinfo(host, NULL, &hints, &found) != 0 || found == NULL) return 0;

    out->ip = ntohl(((struct sockaddr_in *)found->ai_addr)->sin_addr.s_addr);
    out->port = (uint16_t)port;
    freeaddrinfo(found);
    return 1;
}

static void send_to(call_t *c, addr4 to, const uint8_t *data, int length)
{
    struct sockaddr_in sa;
    memset(&sa, 0, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(to.ip);
    sa.sin_port = htons(to.port);
    sendto(c->sock, (const char *)data, length, 0, (struct sockaddr *)&sa, sizeof(sa));
}

static void random_text(char *out, int length)
{
    static const char alphabet[] = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/";
    uint8_t raw[64];
    int i;
    RAND_bytes(raw, length);
    for (i = 0; i < length; i++) out[i] = alphabet[raw[i] % 64];
    out[length] = 0;
}

/* ------------------------------------------------------------------ */
/* CRC32 — для атрибута FINGERPRINT                                    */
/* ------------------------------------------------------------------ */

static uint32_t crc_table[256];

static uint32_t crc32_of(const uint8_t *data, int length)
{
    uint32_t crc = 0xFFFFFFFFu;
    int i;

    if (crc_table[1] == 0) {
        uint32_t n, k, v;
        for (n = 0; n < 256; n++) {
            v = n;
            for (k = 0; k < 8; k++) v = (v & 1) ? 0xEDB88320u ^ (v >> 1) : v >> 1;
            crc_table[n] = v;
        }
    }

    for (i = 0; i < length; i++) crc = crc_table[(crc ^ data[i]) & 255] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}

/* ------------------------------------------------------------------ */
/* STUN: сборка                                                        */
/* ------------------------------------------------------------------ */

typedef struct {
    uint8_t data[MAX_PACKET];
    int length;
} msg_t;

static void msg_begin(msg_t *m, uint16_t type, const uint8_t *txid)
{
    put16(m->data, type);
    put16(m->data + 2, 0);
    put32(m->data + 4, MAGIC_COOKIE);
    memcpy(m->data + 8, txid, 12);
    m->length = 20;
}

static void msg_attr(msg_t *m, uint16_t type, const void *value, int length)
{
    int padded = (length + 3) & ~3;
    if (m->length + 4 + padded > MAX_PACKET) return;
    put16(m->data + m->length, type);
    put16(m->data + m->length + 2, (uint16_t)length);
    if (length > 0) memcpy(m->data + m->length + 4, value, length);
    memset(m->data + m->length + 4 + length, 0, padded - length);
    m->length += 4 + padded;
    put16(m->data + 2, (uint16_t)(m->length - 20));
}

static void msg_attr32(msg_t *m, uint16_t type, uint32_t value)
{
    uint8_t raw[4];
    put32(raw, value);
    msg_attr(m, type, raw, 4);
}

static void msg_xor_addr(msg_t *m, uint16_t type, addr4 a)
{
    uint8_t raw[8];
    raw[0] = 0;
    raw[1] = 1;   /* IPv4 */
    put16(raw + 2, (uint16_t)(a.port ^ (MAGIC_COOKIE >> 16)));
    put32(raw + 4, a.ip ^ MAGIC_COOKIE);
    msg_attr(m, type, raw, 8);
}

/* Подпись: HMAC-SHA1 по сообщению, в длине которого она уже учтена. */
static void msg_integrity(msg_t *m, const uint8_t *key, int key_length)
{
    uint8_t mac[20];
    unsigned int mac_length = 20;
    put16(m->data + 2, (uint16_t)(m->length - 20 + 24));
    HMAC(EVP_sha1(), key, key_length, m->data, m->length, mac, &mac_length);
    msg_attr(m, ATTR_MESSAGE_INTEGRITY, mac, 20);
}

static void msg_fingerprint(msg_t *m)
{
    uint32_t crc;
    put16(m->data + 2, (uint16_t)(m->length - 20 + 8));
    crc = crc32_of(m->data, m->length) ^ 0x5354554Eu;
    msg_attr32(m, ATTR_FINGERPRINT, crc);
}

/* ------------------------------------------------------------------ */
/* STUN: разбор                                                        */
/* ------------------------------------------------------------------ */

static int is_stun(const uint8_t *p, int length)
{
    return length >= 20 && (p[0] & 0xC0) == 0 && get32(p + 4) == MAGIC_COOKIE
           && get16(p + 2) + 20 <= length;
}

/* Найти атрибут: указатель на значение и длину, NULL — нет такого. */
static const uint8_t *find_attr(const uint8_t *p, int length, uint16_t type, int *value_length)
{
    int at = 20;
    int end = 20 + get16(p + 2);
    if (end > length) end = length;

    while (at + 4 <= end) {
        uint16_t t = get16(p + at);
        int l = get16(p + at + 2);
        if (at + 4 + l > end) break;
        if (t == type) {
            if (value_length) *value_length = l;
            return p + at + 4;
        }
        at += 4 + ((l + 3) & ~3);
    }
    return NULL;
}

static int read_xor_addr(const uint8_t *p, int length, uint16_t type, addr4 *out)
{
    int l;
    const uint8_t *v = find_attr(p, length, type, &l);
    if (v == NULL || l < 8 || v[1] != 1) return 0;
    out->port = (uint16_t)(get16(v + 2) ^ (MAGIC_COOKIE >> 16));
    out->ip = get32(v + 4) ^ MAGIC_COOKIE;
    return 1;
}

/* Проверить подпись входящего: пересчитать по началу сообщения до неё. */
static int check_integrity(const uint8_t *p, int length, const uint8_t *key, int key_length)
{
    int l;
    const uint8_t *mi = find_attr(p, length, ATTR_MESSAGE_INTEGRITY, &l);
    uint8_t copy[MAX_PACKET];
    uint8_t mac[20];
    unsigned int mac_length = 20;
    int before;

    if (mi == NULL || l != 20) return 0;
    before = (int)(mi - 4 - p);
    if (before > MAX_PACKET) return 0;

    memcpy(copy, p, before);
    put16(copy + 2, (uint16_t)(before - 20 + 24));
    HMAC(EVP_sha1(), key, key_length, copy, before, mac, &mac_length);
    return memcmp(mac, mi, 20) == 0;
}

static int error_code(const uint8_t *p, int length)
{
    int l;
    const uint8_t *v = find_attr(p, length, ATTR_ERROR_CODE, &l);
    if (v == NULL || l < 4) return 0;
    return (v[2] & 7) * 100 + v[3];
}

/* ------------------------------------------------------------------ */
/* Отправка: прямо или через TURN                                      */
/* ------------------------------------------------------------------ */

static void send_via_relay(call_t *c, addr4 peer, const uint8_t *data, int length)
{
    msg_t m;
    uint8_t txid[12];
    RAND_bytes(txid, 12);
    msg_begin(&m, TURN_SEND_INDICATION, txid);
    msg_xor_addr(&m, ATTR_XOR_PEER_ADDRESS, peer);
    msg_attr(&m, ATTR_DATA, data, length);
    send_to(c, c->turn, m.data, m.length);
}

static void send_path(call_t *c, int relayed, addr4 to, const uint8_t *data, int length)
{
    if (relayed) send_via_relay(c, to, data, length);
    else send_to(c, to, data, length);
}

static void send_selected(call_t *c, const uint8_t *data, int length)
{
    pair_t *p;
    if (c->selected < 0) return;
    p = &c->pairs[c->selected];
    send_path(c, p->relayed, p->remote, data, length);
}

/* ------------------------------------------------------------------ */
/* Кандидаты и пары                                                    */
/* ------------------------------------------------------------------ */

static uint32_t priority_of(int type_preference, int local_preference)
{
    return ((uint32_t)type_preference << 24) | ((uint32_t)local_preference << 8) | 255u;
}

static int find_pair(call_t *c, addr4 remote, int relayed)
{
    int i;
    for (i = 0; i < c->pair_count; i++) {
        if (c->pairs[i].relayed == relayed && addr_equal(c->pairs[i].remote, remote)) return i;
    }
    return -1;
}

static int add_pair(call_t *c, addr4 remote, int relayed)
{
    int at = find_pair(c, remote, relayed);
    pair_t *p;

    if (at >= 0) return at;
    if (c->pair_count >= MAX_PAIRS) return -1;

    at = c->pair_count++;
    p = &c->pairs[at];
    memset(p, 0, sizeof(*p));
    p->remote = remote;
    p->relayed = relayed;
    p->state = PAIR_WAITING;
    p->next = 0;
    return at;
}

static void add_permission(call_t *c, uint32_t ip)
{
    int i;
    for (i = 0; i < c->permission_count; i++) {
        if (c->permissions[i].ip == ip) return;
    }
    if (c->permission_count >= MAX_PERMISSIONS) return;
    c->permissions[c->permission_count].ip = ip;
    c->permissions[c->permission_count].port = 0;
    c->permission_count++;
}

/* Чужой адрес стал известен (из SDP или из пришедшей проверки) — пары с ним. */
static void learn_remote(call_t *c, addr4 remote)
{
    add_pair(c, remote, 0);
    add_permission(c, remote.ip);
    if (c->turn_state == 3) add_pair(c, remote, 1);
}

/* ------------------------------------------------------------------ */
/* STUN-сервер: свой адрес снаружи                                     */
/* ------------------------------------------------------------------ */

/* Промежуток перед очередным повтором: 0,5 — 1 — 2 — 3 — 3 … секунды. */
static double server_retry(int tries)
{
    double wait = SERVER_RETRY_MS;
    while (--tries > 0 && wait < SERVER_RETRY_MOST) wait *= 2;
    return wait < SERVER_RETRY_MOST ? wait : SERVER_RETRY_MOST;
}

static void stun_send(call_t *c)
{
    msg_t m;
    msg_begin(&m, STUN_BINDING_REQUEST, c->stun_tx);
    send_to(c, c->stun, m.data, m.length);
    c->stun_tries++;
    c->stun_next = now_ms() + server_retry(c->stun_tries);
}

static void stun_tick(call_t *c, double now)
{
    if (c->stun_state != 1 || now < c->stun_next) return;
    if (c->stun_tries >= SERVER_TRIES) {
        c->stun_state = 3;
        emit(c, "log stun: нет ответа");
        return;
    }
    stun_send(c);
}

static void announce_srflx(call_t *c, addr4 mapped, const char *source)
{
    char ip[32];
    ip_text(mapped.ip, ip, sizeof(ip));
    emit(c, "cand srflx %s %u %u 0.0.0.0 %u", ip, mapped.port, priority_of(100, 65535),
         c->local_port);
    emit(c, "log %s: снаружи мы %s:%u", source, ip, mapped.port);
}

/* ------------------------------------------------------------------ */
/* TURN: ретранслятор                                                  */
/* ------------------------------------------------------------------ */

static void turn_key_make(call_t *c)
{
    char joined[800];
    snprintf(joined, sizeof(joined), "%s:%s:%s", c->turn_user, c->realm, c->turn_pass);
    MD5((const unsigned char *)joined, strlen(joined), c->turn_key);
}

static void turn_sign(call_t *c, msg_t *m)
{
    msg_attr(m, ATTR_USERNAME, c->turn_user, (int)strlen(c->turn_user));
    msg_attr(m, ATTR_REALM, c->realm, (int)strlen(c->realm));
    msg_attr(m, ATTR_NONCE, c->nonce, c->nonce_length);
    msg_integrity(m, c->turn_key, 16);
}

static void turn_allocate(call_t *c)
{
    msg_t m;
    uint8_t transport[4] = { 17, 0, 0, 0 };   /* UDP */

    RAND_bytes(c->turn_tx, 12);
    msg_begin(&m, TURN_ALLOCATE_REQUEST, c->turn_tx);
    msg_attr(&m, ATTR_REQUESTED_TRANSPORT, transport, 4);
    msg_attr32(&m, ATTR_LIFETIME, 600);
    if (c->turn_state == 2) turn_sign(c, &m);

    send_to(c, c->turn, m.data, m.length);
    c->turn_tries++;
    c->turn_next = now_ms() + server_retry(c->turn_tries);
}

static void turn_refresh(call_t *c)
{
    msg_t m;
    RAND_bytes(c->turn_tx, 12);
    msg_begin(&m, TURN_REFRESH_REQUEST, c->turn_tx);
    msg_attr32(&m, ATTR_LIFETIME, 600);
    turn_sign(c, &m);
    send_to(c, c->turn, m.data, m.length);
}

/* Разрешения: TURN пропускает к нам только с тех адресов, о которых его
 * предупредили. Заявляем все известные чужие адреса одним запросом. */
static void turn_permissions(call_t *c)
{
    msg_t m;
    int i;

    if (c->turn_state != 3 || c->permission_count == 0) return;

    RAND_bytes(c->permission_tx, 12);
    msg_begin(&m, TURN_PERMISSION_REQUEST, c->permission_tx);
    for (i = 0; i < c->permission_count; i++) msg_xor_addr(&m, ATTR_XOR_PEER_ADDRESS, c->permissions[i]);
    turn_sign(c, &m);
    send_to(c, c->turn, m.data, m.length);

    c->permissions_sent = c->permission_count;
    c->permission_tries++;
    c->permission_next = now_ms() + SERVER_RETRY_MS * 2;
}

static void turn_tick(call_t *c, double now)
{
    c->stage = STAGE_TURN;
    if (c->turn_state == 1 || c->turn_state == 2) {
        if (now < c->turn_next) return;
        if (c->turn_tries >= SERVER_TRIES) {
            c->turn_state = 4;
            emit(c, "log turn: нет ответа");
            return;
        }
        turn_allocate(c);
        return;
    }

    if (c->turn_state != 3) return;

    if (now >= c->turn_refresh_at) {
        turn_refresh(c);
        c->turn_refresh_at = now + (c->lifetime > 120 ? (c->lifetime - 60) : 60) * 1000.0;
    }

    /* Новые адреса — заявить сразу; старые — подтверждать до истечения. */
    if (c->permission_count > c->permissions_sent
        || (c->permission_count > 0 && now >= c->permission_refresh_at)) {
        if (now >= c->permission_next || c->permission_count > c->permissions_sent) {
            turn_permissions(c);
            c->permission_refresh_at = now + PERMISSION_REFRESH_MS;
        }
    }
}

static void on_turn_message(call_t *c, const uint8_t *p, int length);
static void on_payload(call_t *c, const uint8_t *p, int length, addr4 from, int relayed);

/* ------------------------------------------------------------------ */
/* ICE: свои проверки                                                  */
/* ------------------------------------------------------------------ */

static void send_check(call_t *c, pair_t *p, int is_consent)
{
    msg_t m;
    char username[300];
    uint8_t controlled[8];

    if (!c->have_remote) return;

    RAND_bytes(p->txid, 12);
    msg_begin(&m, STUN_BINDING_REQUEST, p->txid);
    snprintf(username, sizeof(username), "%s:%s", c->remote_ufrag, c->local_ufrag);
    msg_attr(&m, ATTR_USERNAME, username, (int)strlen(username));
    msg_attr32(&m, ATTR_PRIORITY, priority_of(110, 65535));
    memcpy(controlled, c->tiebreaker, 8);
    if (c->controlling) {
        /* Ведущий помечает путь сразу в проверке: какой первым ответит, тот и
         * выбран (aggressive nomination). libwebrtc это понимает. */
        msg_attr(&m, ATTR_ICE_CONTROLLING, controlled, 8);
        msg_attr(&m, ATTR_USE_CANDIDATE, NULL, 0);
    } else {
        msg_attr(&m, ATTR_ICE_CONTROLLED, controlled, 8);
    }
    msg_integrity(&m, (const uint8_t *)c->remote_pwd, (int)strlen(c->remote_pwd));
    msg_fingerprint(&m);

    send_path(c, p->relayed, p->remote, m.data, m.length);
    (void)is_consent;
}

static void select_pair(call_t *c, int index, const char *why)
{
    char text[40];
    pair_t *p = &c->pairs[index];

    if (c->selected == index) return;
    c->selected = index;
    addr_text(p->remote, text, sizeof(text));
    emit(c, "path %s %s", p->relayed ? "relay" : "direct", text);
    emit(c, "log ice: путь выбран (%s)", why);
    c->last_receive = now_ms();
}

static void start_dtls(call_t *c);

/* Переходить ли на путь, который звонящий только что пометил выбранным.
 *
 * Помечать он может и несколько путей подряд — aiortc, например, помечает
 * каждый рабочий. Прыгать за каждым незачем: звук и так приходит с любого.
 * Переходим, когда пути ещё нет, когда новый прямой, а нынешний через
 * ретранслятор, или когда нынешний замолчал — звонящий перешёл на другой
 * (сменил сеть, например). */
#define PATH_SILENT_MS 1500.0

static int should_switch(call_t *c, int index)
{
    pair_t *current;
    if (c->selected < 0) return 1;
    if (c->selected == index) return 0;
    current = &c->pairs[c->selected];
    if (current->relayed && !c->pairs[index].relayed) return 1;
    return now_ms() - current->last_seen > PATH_SILENT_MS;
}

static void follow_peer(call_t *c, double now)
{
    int i, best = -1;
    pair_t *current;

    if (c->selected < 0) return;
    current = &c->pairs[c->selected];
    if (now - current->last_seen < PATH_SILENT_MS * 2) return;

    /* Нынешний путь молчит — переходим на помеченный, по которому идёт
     * свежее всего. */
    for (i = 0; i < c->pair_count; i++) {
        if (!c->pairs[i].nominated || i == c->selected) continue;
        if (now - c->pairs[i].last_seen > PATH_SILENT_MS) continue;
        if (best < 0 || c->pairs[i].last_seen > c->pairs[best].last_seen) best = i;
    }
    if (best >= 0) select_pair(c, best, "прежний путь замолчал");
}

static void checks_tick(call_t *c, double now)
{
    c->stage = STAGE_ICE;
    int i;

    if (!c->have_remote) return;

    follow_peer(c, now);

    /* Держим выбранный путь живым: и NAT не закроет, и собеседник видит,
     * что мы на месте (consent freshness). */
    if (c->selected >= 0 && now >= c->consent_next) {
        send_check(c, &c->pairs[c->selected], 1);
        c->consent_next = now + CONSENT_MS;
    }

    /* Звонящий так и не выбрал — выбираем сами из проверенных. Прямой путь
     * лучше ретранслятора. */
    if (!c->controlling && c->selected < 0 && c->first_success_at > 0
        && now - c->first_success_at > FALLBACK_SELECT_MS) {
        int best = -1;
        for (i = 0; i < c->pair_count; i++) {
            if (c->pairs[i].state != PAIR_SUCCEEDED) continue;
            if (best < 0 || (c->pairs[best].relayed && !c->pairs[i].relayed)) best = i;
        }
        if (best >= 0) {
            select_pair(c, best, "сами, звонящий молчит");
            start_dtls(c);
        }
    }

    if (now < c->next_check) return;

    for (i = 0; i < c->pair_count; i++) {
        pair_t *p = &c->pairs[i];
        if (p->state != PAIR_WAITING || now < p->next) continue;
        if (p->relayed && c->turn_state != 3) continue;

        if (p->tries >= CHECK_TRIES) {
            p->state = PAIR_FAILED;
            continue;
        }

        send_check(c, p, 0);
        p->tries++;
        p->next = now + CHECK_RETRY_MS;
        c->next_check = now + CHECK_PACE_MS;
        return;
    }
}

/* ------------------------------------------------------------------ */
/* ICE: чужие проверки и ответы на наши                                */
/* ------------------------------------------------------------------ */

static void answer_check(call_t *c, const uint8_t *p, int length, addr4 from, int relayed)
{
    msg_t m;
    int l;
    const uint8_t *user = find_attr(p, length, ATTR_USERNAME, &l);
    size_t ufrag_length = strlen(c->local_ufrag);
    int index;

    /* Проверка адресована нам: имя начинается с нашего логина. */
    if (user == NULL || l <= (int)ufrag_length || memcmp(user, c->local_ufrag, ufrag_length) != 0
        || user[ufrag_length] != ':') {
        emit(c, "log ice: чужая проверка, не отвечаем");
        return;
    }
    if (!check_integrity(p, length, (const uint8_t *)c->local_pwd, (int)strlen(c->local_pwd))) {
        emit(c, "log ice: проверка с неверной подписью");
        return;
    }

    msg_begin(&m, STUN_BINDING_SUCCESS, p + 8);
    msg_xor_addr(&m, ATTR_XOR_MAPPED_ADDRESS, from);
    msg_integrity(&m, (const uint8_t *)c->local_pwd, (int)strlen(c->local_pwd));
    msg_fingerprint(&m);
    send_path(c, relayed, from, m.data, m.length);

    /* Адрес, которого не было в кандидатах, — peer-reflexive: тоже путь. */
    if (find_pair(c, from, relayed) < 0) {
        char text[40];
        addr_text(from, text, sizeof(text));
        emit(c, "log ice: новый адрес собеседника %s (%s)", text, relayed ? "через turn" : "прямо");
    }
    learn_remote(c, from);
    index = find_pair(c, from, relayed);
    if (index < 0) return;

    c->last_receive = now_ms();

    /* Встречная проверка — тот же путь в другую сторону (triggered check). */
    if (c->pairs[index].state == PAIR_WAITING && c->have_remote) {
        c->pairs[index].next = 0;
    }

    c->pairs[index].last_seen = now_ms();

    if (find_attr(p, length, ATTR_USE_CANDIDATE, NULL) != NULL) {
        c->pairs[index].nominated = 1;
        if (should_switch(c, index)) {
            select_pair(c, index, "звонящий выбрал");
            start_dtls(c);
        }
    }
}

static void on_check_response(call_t *c, const uint8_t *p, int length, addr4 from, int relayed)
{
    int i;
    for (i = 0; i < c->pair_count; i++) {
        pair_t *pair = &c->pairs[i];
        if (memcmp(pair->txid, p + 8, 12) != 0) continue;

        if (get16(p) == STUN_BINDING_ERROR) {
            emit(c, "log ice: отказ на проверку, код %d", error_code(p, length));
            return;
        }
        if (pair->state != PAIR_SUCCEEDED) {
            char text[40];
            addr_text(pair->remote, text, sizeof(text));
            emit(c, "log ice: путь до %s %s работает", text, pair->relayed ? "через turn" : "прямо");
            pair->state = PAIR_SUCCEEDED;
            if (c->first_success_at == 0) c->first_success_at = now_ms();
        }
        c->last_receive = now_ms();
        pair->last_seen = now_ms();

        /* Ведущий выбирает сам: первый ответивший путь, а прямой — лучше
         * ретранслятора. */
        if (c->controlling) {
            pair->nominated = 1;
            if (c->selected < 0 || (c->pairs[c->selected].relayed && !pair->relayed)) {
                select_pair(c, i, "мы ведём, путь ответил");
                start_dtls(c);
            }
        }
        (void)from;
        (void)relayed;
        return;
    }
}

/* ------------------------------------------------------------------ */
/* DTLS                                                                */
/* ------------------------------------------------------------------ */

static int accept_any(int ok, X509_STORE_CTX *store)
{
    /* Сертификат собеседника самоподписанный — проверяем его по отпечатку из
     * SDP после рукопожатия, а не цепочкой. */
    (void)ok;
    (void)store;
    return 1;
}

static int make_certificate(call_t *c)
{
    EC_KEY *ec = EC_KEY_new_by_curve_name(NID_X9_62_prime256v1);
    X509_NAME *name;
    uint8_t serial[8];
    unsigned char digest[32];
    unsigned int digest_length = 32;
    unsigned int i;
    char *at;

    if (ec == NULL || !EC_KEY_generate_key(ec)) return 0;
    EC_KEY_set_asn1_flag(ec, OPENSSL_EC_NAMED_CURVE);

    c->key = EVP_PKEY_new();
    if (c->key == NULL || !EVP_PKEY_assign_EC_KEY(c->key, ec)) return 0;

    c->cert = X509_new();
    if (c->cert == NULL) return 0;
    X509_set_version(c->cert, 2);
    RAND_bytes(serial, sizeof(serial));
    serial[0] &= 0x7F;
    ASN1_INTEGER_set(X509_get_serialNumber(c->cert), (long)get32(serial) & 0x7FFFFFFF);
    X509_gmtime_adj(X509_get_notBefore(c->cert), -86400);
    X509_gmtime_adj(X509_get_notAfter(c->cert), 30L * 86400);
    X509_set_pubkey(c->cert, c->key);
    name = X509_get_subject_name(c->cert);
    X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, (const unsigned char *)"lovit", -1, -1, 0);
    X509_set_issuer_name(c->cert, name);
    if (!X509_sign(c->cert, c->key, EVP_sha256())) return 0;

    if (!X509_digest(c->cert, EVP_sha256(), digest, &digest_length)) return 0;
    at = c->fingerprint;
    for (i = 0; i < digest_length; i++) {
        sprintf(at, i == 0 ? "%02X" : ":%02X", digest[i]);
        at += i == 0 ? 2 : 3;
    }
    return 1;
}

static int make_dtls(call_t *c)
{
    c->ctx = SSL_CTX_new(DTLS_method());
    if (c->ctx == NULL) return 0;

    if (SSL_CTX_use_certificate(c->ctx, c->cert) != 1) return 0;
    if (SSL_CTX_use_PrivateKey(c->ctx, c->key) != 1) return 0;

    SSL_CTX_set_verify(c->ctx, SSL_VERIFY_PEER | SSL_VERIFY_FAIL_IF_NO_PEER_CERT, accept_any);
    SSL_CTX_set_cipher_list(c->ctx,
        "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:"
        "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:"
        "ECDHE-ECDSA-AES128-SHA:ECDHE-RSA-AES128-SHA");
#if OPENSSL_VERSION_NUMBER < 0x10100000L
    SSL_CTX_set_ecdh_auto(c->ctx, 1);
#endif
    /* Ноль на успех — так у этой функции заведено. */
    if (SSL_CTX_set_tlsext_use_srtp(c->ctx, "SRTP_AES128_CM_SHA1_80") != 0) return 0;

    c->ssl = SSL_new(c->ctx);
    if (c->ssl == NULL) return 0;

    c->rbio = BIO_new(BIO_s_mem());
    c->wbio = BIO_new(BIO_s_mem());
    BIO_set_mem_eof_return(c->rbio, -1);
    BIO_set_mem_eof_return(c->wbio, -1);
    SSL_set_bio(c->ssl, c->rbio, c->wbio);

    /* Своей MTU у памяти нет — задаём сами, с запасом под TURN. */
    SSL_set_options(c->ssl, SSL_OP_NO_QUERY_MTU);
    SSL_set_mtu(c->ssl, 1100);
    return 1;
}

/* Отправить то, что DTLS сложил в выходной буфер. */
static void flush_dtls(call_t *c)
{
    uint8_t buffer[MAX_PACKET];
    int got;

    while ((got = BIO_read(c->wbio, buffer, sizeof(buffer))) > 0) {
        send_selected(c, buffer, got);
    }
}

static void derive_srtp(call_t *c);

static void dtls_step(call_t *c)
{
    int result;

    if (c->dtls_state != 1) return;

    result = SSL_do_handshake(c->ssl);
    flush_dtls(c);

    if (result == 1) {
        X509 *peer = SSL_get_peer_certificate(c->ssl);
        unsigned char digest[32];
        unsigned int digest_length = 32;
        SRTP_PROTECTION_PROFILE *profile = SSL_get_selected_srtp_profile(c->ssl);

        if (peer == NULL || !X509_digest(peer, EVP_sha256(), digest, &digest_length)) {
            c->dtls_state = 3;
            emit(c, "failed dtls: нет сертификата собеседника");
            if (peer) X509_free(peer);
            return;
        }
        X509_free(peer);

        if (c->have_remote_fp && memcmp(digest, c->remote_fp, 32) != 0) {
            c->dtls_state = 3;
            emit(c, "failed dtls: отпечаток не совпал с SDP");
            return;
        }
        if (profile == NULL) {
            c->dtls_state = 3;
            emit(c, "failed dtls: собеседник не согласился на SRTP");
            return;
        }

        derive_srtp(c);
        c->dtls_state = 2;
        emit(c, "log dtls: готово, %s, %s", SSL_get_cipher(c->ssl), profile->name);
        emit(c, "ready");
        return;
    }

    {
        int reason = SSL_get_error(c->ssl, result);
        if (reason != SSL_ERROR_WANT_READ && reason != SSL_ERROR_WANT_WRITE) {
            char text[160];
            ERR_error_string_n(ERR_get_error(), text, sizeof(text));
            c->dtls_state = 3;
            emit(c, "failed dtls: %s", text);
        }
    }
}

static void start_dtls(call_t *c)
{
    if (c->dtls_state != 0) {
        /* Путь сменился посреди звонка — продолжаем по новому, ничего не
         * начиная заново: DTLS живёт поверх пути, а не на нём. */
        return;
    }
    c->dtls_state = 1;
    if (c->dtls_client) {
        SSL_set_connect_state(c->ssl);
        emit(c, "log dtls: начинаем рукопожатие, мы клиент");
    } else {
        /* Сервер ждёт приветствия клиента — первый же его пакет и начнёт. */
        SSL_set_accept_state(c->ssl);
        emit(c, "log dtls: ждём рукопожатия, мы сервер");
    }
    dtls_step(c);
}

static void on_dtls(call_t *c, const uint8_t *p, int length)
{
    c->stage = STAGE_DTLS;
    if (c->dtls_state == 0 || c->ssl == NULL) return;

    BIO_write(c->rbio, p, length);

    if (c->dtls_state == 1) {
        dtls_step(c);
    } else if (c->dtls_state == 2) {
        uint8_t sink[MAX_PACKET];
        /* После рукопожатия по DTLS может прийти разве что прощание. */
        int got = SSL_read(c->ssl, sink, sizeof(sink));
        if (got <= 0 && SSL_get_error(c->ssl, got) == SSL_ERROR_ZERO_RETURN) {
            emit(c, "log dtls: собеседник закрыл соединение");
        }
        flush_dtls(c);
    }
}

/* ------------------------------------------------------------------ */
/* SRTP (RFC 3711): AES-CM-128, HMAC-SHA1-80                           */
/* ------------------------------------------------------------------ */

/* Ключевой поток AES в режиме счётчика с начальным вектором iv. */
static void aes_ctr(const uint8_t *key, const uint8_t *iv, uint8_t *data, int length)
{
    EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
    int out = 0;
    if (ctx == NULL) return;
    EVP_EncryptInit_ex(ctx, EVP_aes_128_ctr(), NULL, key, iv);
    EVP_EncryptUpdate(ctx, data, &out, data, length);
    EVP_CIPHER_CTX_free(ctx);
}

/* Вывод сеансового ключа: AES-CM(мастер-ключ, (соль ^ (метка << 48)) << 16). */
static void kdf(const uint8_t *master_key, const uint8_t *master_salt, uint8_t label,
                uint8_t *out, int length)
{
    uint8_t iv[16];
    memset(iv, 0, 16);
    memcpy(iv, master_salt, 14);
    iv[7] ^= label;
    memset(out, 0, length);
    aes_ctr(master_key, iv, out, length);
}

static void srtp_init(srtp_side *s, const uint8_t *master_key, const uint8_t *master_salt)
{
    memset(s, 0, sizeof(*s));
    kdf(master_key, master_salt, 0, s->key, 16);
    kdf(master_key, master_salt, 1, s->auth, 20);
    kdf(master_key, master_salt, 2, s->salt, 14);
    kdf(master_key, master_salt, 3, s->rtcp_key, 16);
    kdf(master_key, master_salt, 4, s->rtcp_auth, 20);
    kdf(master_key, master_salt, 5, s->rtcp_salt, 14);
}

static void derive_srtp(call_t *c)
{
    uint8_t material[60];
    static const char label[] = "EXTRACTOR-dtls_srtp";

    SSL_export_keying_material(c->ssl, material, sizeof(material), label, strlen(label), NULL, 0, 0);

    /* Порядок: ключ клиента, ключ сервера, соль клиента, соль сервера. Мы —
     * клиент DTLS. */
    if (c->dtls_client) {
        srtp_init(&c->out, material, material + 32);
        srtp_init(&c->in, material + 16, material + 46);
    } else {
        srtp_init(&c->out, material + 16, material + 46);
        srtp_init(&c->in, material, material + 32);
    }
}

static void srtp_iv(uint8_t *iv, const uint8_t *salt, uint32_t ssrc, uint64_t index)
{
    int i;
    memset(iv, 0, 16);
    memcpy(iv, salt, 14);
    iv[4] ^= (uint8_t)(ssrc >> 24);
    iv[5] ^= (uint8_t)(ssrc >> 16);
    iv[6] ^= (uint8_t)(ssrc >> 8);
    iv[7] ^= (uint8_t)ssrc;
    for (i = 0; i < 6; i++) iv[13 - i] ^= (uint8_t)(index >> (8 * i));
}

static int rtp_header_length(const uint8_t *p, int length)
{
    int header;
    if (length < 12) return -1;
    header = 12 + (p[0] & 0x0F) * 4;
    if (p[0] & 0x10) {
        if (header + 4 > length) return -1;
        header += 4 + get16(p + header + 2) * 4;
    }
    return header <= length ? header : -1;
}

static void tag_of(const uint8_t *auth, const uint8_t *data, int length, uint32_t tail, uint8_t *tag)
{
    uint8_t buffer[MAX_PACKET + 8];
    uint8_t mac[20];
    unsigned int mac_length = 20;
    memcpy(buffer, data, length);
    put32(buffer + length, tail);
    HMAC(EVP_sha1(), auth, 20, buffer, length + 4, mac, &mac_length);
    memcpy(tag, mac, 10);
}

/* Зашифровать и подписать RTP на месте; возвращает новую длину. */
static int srtp_protect(srtp_side *s, uint8_t *p, int length)
{
    uint8_t iv[16];
    uint16_t seq = get16(p + 2);
    uint32_t ssrc = get32(p + 8);
    int header = rtp_header_length(p, length);
    uint64_t index;

    if (header < 0) return -1;
    if (s->started && seq < s->last_seq && s->last_seq - seq > 32768) s->roc++;
    s->started = 1;
    s->last_seq = seq;
    index = ((uint64_t)s->roc << 16) | seq;

    srtp_iv(iv, s->salt, ssrc, index);
    aes_ctr(s->key, iv, p + header, length - header);
    tag_of(s->auth, p, length, s->roc, p + length);
    return length + 10;
}

/* Проверить и расшифровать RTP на месте; возвращает длину без подписи или -1. */
static int srtp_unprotect(srtp_side *s, uint8_t *p, int length)
{
    uint8_t iv[16];
    uint8_t tag[10];
    uint16_t seq;
    uint32_t ssrc, v;
    int header;
    uint64_t index;

    if (length < 12 + 10) return -1;
    length -= 10;
    seq = get16(p + 2);
    ssrc = get32(p + 8);

    /* Оценка счётчика переполнений — по RFC 3711, приложение A. */
    if (!s->started) {
        v = 0;
    } else if (s->last_seq < 32768) {
        v = (seq - s->last_seq > 32768) ? s->roc - 1 : s->roc;
    } else {
        v = (s->last_seq - 32768 > seq) ? s->roc + 1 : s->roc;
    }

    tag_of(s->auth, p, length, v, tag);
    if (memcmp(tag, p + length, 10) != 0) return -1;

    if (!s->started) {
        s->started = 1;
        s->roc = 0;
        s->last_seq = seq;
    } else if (v == s->roc + 1) {
        s->roc = v;
        s->last_seq = seq;
    } else if (v == s->roc && seq > s->last_seq) {
        s->last_seq = seq;
    }

    header = rtp_header_length(p, length);
    if (header < 0) return -1;
    index = ((uint64_t)v << 16) | seq;
    srtp_iv(iv, s->salt, ssrc, index);
    aes_ctr(s->key, iv, p + header, length - header);
    return length;
}

static int srtcp_protect(srtp_side *s, uint8_t *p, int length)
{
    uint8_t iv[16];
    uint32_t ssrc = get32(p + 4);
    uint32_t index = ++s->rtcp_index & 0x7FFFFFFF;

    srtp_iv(iv, s->rtcp_salt, ssrc, index);
    aes_ctr(s->rtcp_key, iv, p + 8, length - 8);
    put32(p + length, 0x80000000u | index);
    length += 4;
    {
        uint8_t mac[20];
        unsigned int mac_length = 20;
        HMAC(EVP_sha1(), s->rtcp_auth, 20, p, length, mac, &mac_length);
        memcpy(p + length, mac, 10);
    }
    return length + 10;
}

static int srtcp_unprotect(srtp_side *s, uint8_t *p, int length)
{
    uint8_t iv[16];
    uint8_t mac[20];
    unsigned int mac_length = 20;
    uint32_t trailer, index;

    if (length < 8 + 4 + 10) return -1;
    HMAC(EVP_sha1(), s->rtcp_auth, 20, p, length - 10, mac, &mac_length);
    if (memcmp(mac, p + length - 10, 10) != 0) return -1;

    trailer = get32(p + length - 14);
    index = trailer & 0x7FFFFFFF;
    length -= 14;
    if (trailer & 0x80000000u) {
        srtp_iv(iv, s->rtcp_salt, get32(p + 4), index);
        aes_ctr(s->rtcp_key, iv, p + 8, length - 8);
    }
    return length;
}

/* ------------------------------------------------------------------ */
/* RTP / RTCP                                                          */
/* ------------------------------------------------------------------ */

/* Запомнить номер пришедшего пакета: из них в отчёте — сколько потеряно. */
static void note_received(call_t *c, uint32_t ssrc, uint16_t seq)
{
    if (!c->have_rtp || ssrc != c->remote_ssrc) {
        c->have_rtp = 1;
        c->remote_ssrc = ssrc;
        c->base_seq = seq;
        c->max_seq = seq;
        c->expected_prior = 0;
        c->received_prior = 0;
        c->received_packets = 0;
        return;
    }
    {
        uint32_t cycle = c->max_seq & 0xFFFF0000u;
        uint32_t candidate = cycle | seq;
        if ((int32_t)(candidate - c->max_seq) < -32768) candidate += 0x10000;
        else if ((int32_t)(candidate - c->max_seq) > 32768) candidate -= 0x10000;
        if ((int32_t)(candidate - c->max_seq) > 0) c->max_seq = candidate;
    }
}

static void on_rtp(call_t *c, uint8_t *p, int length)
{
    int pt = p[1] & 0x7F;
    c->stage = STAGE_RTP_IN;

    /* RTCP: типы 200–207 во втором байте целиком. */
    if (p[1] >= 192 && p[1] <= 223) {
        c->stage = STAGE_RTCP_IN;
        length = srtcp_unprotect(&c->in, p, length);
        if (length < 0) {
            emit(c, "log srtcp: не сошлась подпись");
        } else if (p[1] == 200 && length >= 20) {
            c->last_sr = (get32(p + 8) << 16) | (get32(p + 12) >> 16);
            c->last_sr_at = now_ms();
        }
        c->last_receive = now_ms();
        return;
    }

    length = srtp_unprotect(&c->in, p, length);
    if (length < 0) {
        static int complained = 0;
        if (complained++ < 5) emit(c, "log srtp: не сошлась подпись пакета");
        return;
    }
    c->last_receive = now_ms();

    note_received(c, get32(p + 8), get16(p + 2));
    if (pt != c->opus_pt) return;

    {
        int header = rtp_header_length(p, length);
        int padding = 0;
        int next = (c->rx_tail + 1) % RX_SLOTS;
        rx_slot *slot;

        if (header < 0) return;
        if (p[0] & 0x20) padding = p[length - 1];
        if (header + padding >= length) return;

        if (next == c->rx_head) c->rx_head = (c->rx_head + 1) % RX_SLOTS;   /* не забирают — старое долой */
        slot = &c->rx[c->rx_tail];
        slot->length = length - header - padding;
        memcpy(slot->data, p + header, slot->length);
        slot->timestamp = get32(p + 4);
        slot->sequence = get16(p + 2);
        c->rx_tail = next;

        if (c->received_packets++ == 0) emit(c, "log rtp: пошёл звук от собеседника");
    }
}

/* Отчёт раз в пару секунд: SR, если мы говорим, иначе RR, — и в любом
 * случае блок о том, что приходит от собеседника, и CNAME.
 *
 * Без отчётов сервер MAX, похоже, считает прямое соединение мёртвым: в
 * первом живом звонке он через семь секунд перевёл разговор на свой
 * медиасервер — от нас тогда не шло ни звука, ни RTCP. */
static int report_block(call_t *c, uint8_t *p)
{
    uint32_t expected, lost, expected_interval, received_interval, lost_interval;
    uint8_t fraction = 0;
    double now = now_ms();

    if (!c->have_rtp) return 0;

    expected = c->max_seq - c->base_seq + 1;
    lost = expected > c->received_packets ? expected - c->received_packets : 0;
    expected_interval = expected - c->expected_prior;
    received_interval = c->received_packets - c->received_prior;
    lost_interval = expected_interval > received_interval ? expected_interval - received_interval : 0;
    if (expected_interval > 0) fraction = (uint8_t)((lost_interval << 8) / expected_interval);
    c->expected_prior = expected;
    c->received_prior = c->received_packets;

    put32(p, c->remote_ssrc);
    put32(p + 4, ((uint32_t)fraction << 24) | (lost & 0xFFFFFF));
    put32(p + 8, c->max_seq);
    put32(p + 12, 0);                                    /* дрожание не считаем */
    put32(p + 16, c->last_sr);
    put32(p + 20, c->last_sr_at > 0 ? (uint32_t)((now - c->last_sr_at) * 65.536) : 0);
    return 24;
}

static void send_report(call_t *c)
{
    uint8_t p[MAX_PACKET];
    int at, block_length;
    double now = now_ms();
    c->stage = STAGE_RTCP_OUT;

    if (c->sent_packets > 0) {
        uint32_t seconds = (uint32_t)(now / 1000.0) + 2208988800u;
        uint32_t fraction = (uint32_t)(((now / 1000.0) - (uint32_t)(now / 1000.0)) * 4294967296.0);
        put32(p + 4, c->ssrc);
        put32(p + 8, seconds);
        put32(p + 12, fraction);
        put32(p + 16, c->last_timestamp);
        put32(p + 20, c->sent_packets);
        put32(p + 24, c->sent_octets);
        block_length = report_block(c, p + 28);
        p[0] = (uint8_t)(0x80 | (block_length ? 1 : 0));
        p[1] = 200;
        at = 28 + block_length;
    } else {
        put32(p + 4, c->ssrc);
        block_length = report_block(c, p + 8);
        p[0] = (uint8_t)(0x80 | (block_length ? 1 : 0));
        p[1] = 201;
        at = 8 + block_length;
    }
    put16(p + 2, (uint16_t)(at / 4 - 1));

    /* SDES с CNAME — RFC 3550 требует его в каждом составном пакете. */
    {
        int sdes = at;
        p[at] = 0x81;
        p[at + 1] = 202;
        put32(p + at + 4, c->ssrc);
        p[at + 8] = 1;           /* CNAME */
        p[at + 9] = 5;
        memcpy(p + at + 10, "lovit", 5);
        p[at + 15] = 0;          /* конец списка */
        at += 16;
        put16(p + sdes + 2, (uint16_t)(16 / 4 - 1));
    }

    at = srtcp_protect(&c->out, p, at);
    send_selected(c, p, at);
}

/* ------------------------------------------------------------------ */
/* Разбор пришедшего                                                   */
/* ------------------------------------------------------------------ */

static void on_payload(call_t *c, const uint8_t *p, int length, addr4 from, int relayed)
{
    uint8_t first;
    if (length < 1) return;
    first = p[0];

    if (first <= 3 && is_stun(p, length)) {
        uint16_t type = get16(p);
        if (type == STUN_BINDING_REQUEST) answer_check(c, p, length, from, relayed);
        else if (type == STUN_BINDING_SUCCESS || type == STUN_BINDING_ERROR) on_check_response(c, p, length, from, relayed);
        return;
    }

    {
        int index = find_pair(c, from, relayed);
        if (index >= 0) c->pairs[index].last_seen = now_ms();
    }

    if (first >= 20 && first <= 63) {
        if (c->selected < 0 || c->dtls_state == 0) {
            int index = find_pair(c, from, relayed);
            if (index < 0) {
                learn_remote(c, from);
                index = find_pair(c, from, relayed);
            }
            if (index >= 0 && c->selected < 0) select_pair(c, index, "оттуда пришёл DTLS");
            if (c->dtls_state == 0 && !c->dtls_client) start_dtls(c);
        }
        on_dtls(c, p, length);
        return;
    }

    if (first >= 128 && first <= 191 && c->dtls_state == 2) {
        uint8_t copy[MAX_PACKET];
        if (length > MAX_PACKET) return;
        memcpy(copy, p, length);
        on_rtp(c, copy, length);
    }
}

static void on_turn_message(call_t *c, const uint8_t *p, int length)
{
    uint16_t type = get16(p);

    /* ChannelData сюда не приходит: каналы мы не заводим. */
    if (type == TURN_DATA_INDICATION) {
        addr4 peer;
        int l;
        const uint8_t *data = find_attr(p, length, ATTR_DATA, &l);
        if (data == NULL || !read_xor_addr(p, length, ATTR_XOR_PEER_ADDRESS, &peer)) return;
        on_payload(c, data, l, peer, 1);
        return;
    }

    if (memcmp(p + 8, c->turn_tx, 12) == 0) {
        if (type == TURN_ALLOCATE_ERROR) {
            int code = error_code(p, length);
            int l;
            const uint8_t *v;

            if ((code == 401 || code == 438) && (c->turn_state <= 2 || (c->turn_state == 4 && c->nonce_length == 0))) {
                v = find_attr(p, length, ATTR_REALM, &l);
                if (v != NULL && l < (int)sizeof(c->realm)) {
                    memcpy(c->realm, v, l);
                    c->realm[l] = 0;
                }
                v = find_attr(p, length, ATTR_NONCE, &l);
                if (v != NULL && l <= (int)sizeof(c->nonce)) {
                    memcpy(c->nonce, v, l);
                    c->nonce_length = l;
                }
                turn_key_make(c);
                if (c->turn_state == 2 && code == 401) {
                    c->turn_state = 4;
                    emit(c, "log turn: не пустил с нашим логином");
                    return;
                }
                c->turn_state = 2;
                c->turn_tries = 0;
                turn_allocate(c);
                return;
            }

            c->turn_state = 4;
            emit(c, "log turn: отказ %d", code);
            return;
        }

        if (type == TURN_ALLOCATE_SUCCESS) {
            int l;
            const uint8_t *v;
            char ip[32], base[32];
            int i;

            if (!read_xor_addr(p, length, ATTR_XOR_RELAYED_ADDRESS, &c->relay)) return;
            read_xor_addr(p, length, ATTR_XOR_MAPPED_ADDRESS, &c->mapped);
            v = find_attr(p, length, ATTR_LIFETIME, &l);
            c->lifetime = (v != NULL && l == 4) ? get32(v) : 600;
            c->turn_state = 3;
            c->turn_refresh_at = now_ms() + (c->lifetime > 120 ? (c->lifetime - 60) : 60) * 1000.0;

            ip_text(c->relay.ip, ip, sizeof(ip));
            ip_text(c->mapped.ip, base, sizeof(base));
            emit(c, "cand relay %s %u %u %s %u", ip, c->relay.port, priority_of(0, 65535),
                 base, c->mapped.port);
            emit(c, "log turn: ретранслятор %s:%u", ip, c->relay.port);

            /* Адрес снаружи TURN тоже знает — пригодится, если STUN молчит. */
            if (c->mapped.ip != 0 && c->stun_state != 2) {
                c->stun_state = 2;
                announce_srflx(c, c->mapped, "turn");
            }

            /* Пары через ретранслятор для уже известных адресов. */
            for (i = 0; i < c->permission_count; i++) {
                int j;
                for (j = 0; j < c->pair_count; j++) {
                    if (!c->pairs[j].relayed && c->pairs[j].remote.ip == c->permissions[i].ip) {
                        add_pair(c, c->pairs[j].remote, 1);
                    }
                }
            }
            c->permissions_sent = 0;
            return;
        }

        if (type == TURN_REFRESH_ERROR) {
            int code = error_code(p, length);
            if (code == 438) {
                int l;
                const uint8_t *v = find_attr(p, length, ATTR_NONCE, &l);
                if (v != NULL && l <= (int)sizeof(c->nonce)) {
                    memcpy(c->nonce, v, l);
                    c->nonce_length = l;
                    turn_refresh(c);
                }
            } else {
                emit(c, "log turn: продление отвергнуто, код %d", code);
            }
        }
        return;
    }

    if (memcmp(p + 8, c->permission_tx, 12) == 0) {
        if (type == TURN_PERMISSION_SUCCESS) {
            c->permission_tries = 0;
            emit(c, "log turn: разрешения на %d адрес(ов)", c->permissions_sent);
            c->permission_next = now_ms() + PERMISSION_REFRESH_MS;
        } else if (type == TURN_PERMISSION_ERROR) {
            int code = error_code(p, length);
            if (code == 438) {
                int l;
                const uint8_t *v = find_attr(p, length, ATTR_NONCE, &l);
                if (v != NULL && l <= (int)sizeof(c->nonce)) {
                    memcpy(c->nonce, v, l);
                    c->nonce_length = l;
                }
                c->permissions_sent = 0;
            } else {
                emit(c, "log turn: разрешения отвергнуты, код %d", code);
            }
        }
    }
}

static void on_datagram(call_t *c, const uint8_t *p, int length, addr4 from)
{
    c->stage = STAGE_RECEIVE;
    /* Ответ STUN-сервера про наш адрес. Узнаётся по номеру запроса, а не по
     * адресу: у MAX STUN и TURN бывают одним и тем же сервером. */
    if (c->stun_state != 0 && addr_equal(from, c->stun) && is_stun(p, length)
        && memcmp(p + 8, c->stun_tx, 12) == 0) {
        if (get16(p) == STUN_BINDING_SUCCESS && c->stun_state == 1) {
            addr4 mapped;
            if (read_xor_addr(p, length, ATTR_XOR_MAPPED_ADDRESS, &mapped)) {
                c->stun_state = 2;
                announce_srflx(c, mapped, "stun");
            }
        }
        return;
    }

    if (c->turn_state != 0 && addr_equal(from, c->turn)) {
        if (is_stun(p, length)) on_turn_message(c, p, length);
        return;
    }

    on_payload(c, p, length, from, 0);
}

/* ------------------------------------------------------------------ */
/* Внешний интерфейс                                                   */
/* ------------------------------------------------------------------ */

void * LOVIT_CALL_CC lovit_call_create(void)
{
    call_t *c;
    struct sockaddr_in sa;
    socklen_t sa_length = sizeof(sa);

#ifdef _WIN32
    {
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
    }
    {
        static int ready = 0;
        if (!ready) {
            SSL_library_init();
            SSL_load_error_strings();
            ready = 1;
        }
    }
#endif

    c = (call_t *)calloc(1, sizeof(call_t));
    if (c == NULL) return NULL;

    c->selected = -1;
    c->sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (c->sock == BAD_SOCKET) {
        free(c);
        return NULL;
    }

    memset(&sa, 0, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_addr.s_addr = htonl(INADDR_ANY);
    sa.sin_port = 0;
    if (bind(c->sock, (struct sockaddr *)&sa, sizeof(sa)) != 0
        || getsockname(c->sock, (struct sockaddr *)&sa, &sa_length) != 0) {
        close_socket(c->sock);
        free(c);
        return NULL;
    }
    c->local_port = ntohs(sa.sin_port);

#ifdef _WIN32
    {
        u_long nonblocking = 1;
        ioctlsocket(c->sock, FIONBIO, &nonblocking);
    }
#else
    fcntl(c->sock, F_SETFL, fcntl(c->sock, F_GETFL, 0) | O_NONBLOCK);
#endif

    random_text(c->local_ufrag, 8);
    random_text(c->local_pwd, 24);
    RAND_bytes(c->tiebreaker, 8);
    RAND_bytes((uint8_t *)&c->ssrc, 4);
    RAND_bytes((uint8_t *)&c->seq, 2);
    c->seq &= 0x7FFF;
    c->opus_pt = 111;
    c->controlling = 0;
    c->dtls_client = 1;

    if (!make_certificate(c) || !make_dtls(c)) {
        lovit_call_destroy(c);
        return NULL;
    }
    return c;
}

int LOVIT_CALL_CC lovit_call_local(void *handle, char *out, int capacity)
{
    call_t *c = (call_t *)handle;
    if (c == NULL || out == NULL) return -1;
    return snprintf(out, capacity, "%s %s %s %u", c->local_ufrag, c->local_pwd, c->fingerprint, c->ssrc);
}

int LOVIT_CALL_CC lovit_call_add_host(void *handle, const char *ip)
{
    call_t *c = (call_t *)handle;
    uint32_t parsed;
    if (c == NULL || ip == NULL || !parse_ip(ip, &parsed)) return -1;
    emit(c, "cand host %s %u %u", ip, c->local_port, priority_of(126, 65535));
    return 0;
}

int LOVIT_CALL_CC lovit_call_add_stun(void *handle, const char *host, int port)
{
    call_t *c = (call_t *)handle;
    if (c == NULL || host == NULL || c->stun_state != 0) return -1;
    if (!resolve(host, port, &c->stun)) {
        emit(c, "log stun: не нашёлся %s", host);
        return -1;
    }
    RAND_bytes(c->stun_tx, 12);
    c->stun_state = 1;
    stun_send(c);
    return 0;
}

int LOVIT_CALL_CC lovit_call_add_turn(void *handle, const char *host, int port,
                                      const char *user, const char *password)
{
    call_t *c = (call_t *)handle;
    if (c == NULL || host == NULL || c->turn_state != 0) return -1;
    if (!resolve(host, port, &c->turn)) {
        emit(c, "log turn: не нашёлся %s", host);
        return -1;
    }
    snprintf(c->turn_user, sizeof(c->turn_user), "%s", user ? user : "");
    snprintf(c->turn_pass, sizeof(c->turn_pass), "%s", password ? password : "");
    c->turn_state = 1;
    turn_allocate(c);
    return 0;
}

static int hex_digit(char ch)
{
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

int LOVIT_CALL_CC lovit_call_set_remote(void *handle, const char *ufrag, const char *password,
                                        const char *fingerprint, int opus_pt)
{
    call_t *c = (call_t *)handle;
    int i, n = 0;

    if (c == NULL || ufrag == NULL || password == NULL) return -1;
    snprintf(c->remote_ufrag, sizeof(c->remote_ufrag), "%s", ufrag);
    snprintf(c->remote_pwd, sizeof(c->remote_pwd), "%s", password);
    if (opus_pt > 0) c->opus_pt = opus_pt;

    if (fingerprint != NULL) {
        for (i = 0; fingerprint[i] != 0 && n < 32; i++) {
            int hi = hex_digit(fingerprint[i]);
            int lo;
            if (hi < 0) continue;
            lo = hex_digit(fingerprint[i + 1]);
            if (lo < 0) break;
            c->remote_fp[n++] = (uint8_t)(hi * 16 + lo);
            i++;
        }
        c->have_remote_fp = (n == 32);
    }

    c->have_remote = 1;
    return 0;
}

int LOVIT_CALL_CC lovit_call_set_roles(void *handle, int controlling, int dtls_client)
{
    call_t *c = (call_t *)handle;
    if (c == NULL || c->dtls_state != 0) return -1;
    c->controlling = controlling ? 1 : 0;
    c->dtls_client = dtls_client ? 1 : 0;
    return 0;
}

int LOVIT_CALL_CC lovit_call_add_remote(void *handle, const char *ip, int port, unsigned int priority)
{
    call_t *c = (call_t *)handle;
    addr4 remote;
    (void)priority;
    if (c == NULL || ip == NULL || !parse_ip(ip, &remote.ip) || port <= 0 || port > 65535) return -1;
    remote.port = (uint16_t)port;
    learn_remote(c, remote);
    return 0;
}

static int poll_inner(void *handle, int timeout_ms)
{
    call_t *c = (call_t *)handle;
    double until, now;

    if (c == NULL) return -1;
    if (c->failed) return -1;

    until = now_ms() + (timeout_ms > 0 ? timeout_ms : 0);

    for (;;) {
        fd_set reading;
        struct timeval wait;
        double left;
        int ready;

        now = now_ms();
        stun_tick(c, now);
        turn_tick(c, now);
        checks_tick(c, now);

        if (c->dtls_state == 1) {
            if (DTLSv1_handle_timeout(c->ssl) > 0) flush_dtls(c);
        }
        if (c->dtls_state == 3 && !c->failed) c->failed = 1;

        if (c->dtls_state == 2 && now >= c->sr_next) {
            send_report(c);
            c->sr_next = now + SR_EVERY_MS;
        }

        if (c->selected >= 0 && now - c->last_receive > SILENCE_FAIL_MS) {
            emit(c, "failed тишина: собеседник пропал");
            c->failed = 1;
        }
        if (c->failed) return -1;

        left = until - now;
        if (left < 0) left = 0;
        if (left > CHECK_PACE_MS) left = CHECK_PACE_MS;

        FD_ZERO(&reading);
        FD_SET(c->sock, &reading);
        wait.tv_sec = 0;
        wait.tv_usec = (long)(left * 1000.0);
        ready = select((int)c->sock + 1, &reading, NULL, NULL, &wait);

        if (ready > 0) {
            uint8_t buffer[2048];
            struct sockaddr_in from;
            socklen_t from_length;
            int got;

            for (;;) {
                from_length = sizeof(from);
                got = recvfrom(c->sock, (char *)buffer, sizeof(buffer), 0,
                               (struct sockaddr *)&from, &from_length);
                if (got <= 0) break;
                {
                    addr4 source;
                    source.ip = ntohl(from.sin_addr.s_addr);
                    source.port = ntohs(from.sin_port);
                    on_datagram(c, buffer, got, source);
                }
            }
        }

        if (now_ms() >= until) break;
    }

    if (c->dtls_state == 2) return 2;
    if (c->selected >= 0) return 1;
    return 0;
}

int LOVIT_CALL_CC lovit_call_event(void *handle, char *out, int capacity)
{
    call_t *c = (call_t *)handle;
    int length;
    if (c == NULL || out == NULL || capacity <= 0) return 0;
    if (c->event_head == c->event_tail) return 0;
    length = snprintf(out, capacity, "%s", c->events[c->event_head]);
    c->event_head = (c->event_head + 1) % MAX_EVENTS;
    return length > 0 ? length : 0;
}

static int send_audio_inner(void *handle, const unsigned char *opus, int length,
                            unsigned int timestamp)
{
    call_t *c = (call_t *)handle;
    uint8_t p[MAX_PACKET];
    int total;

    if (c == NULL || opus == NULL || length <= 0 || length > 1200) return -1;
    if (c->dtls_state != 2 || c->selected < 0) return -1;

    c->stage = STAGE_RTP_OUT;
    p[0] = 0x80;
    p[1] = (uint8_t)(c->opus_pt & 0x7F);
    put16(p + 2, c->seq++);
    put32(p + 4, timestamp);
    put32(p + 8, c->ssrc);
    memcpy(p + 12, opus, length);

    total = srtp_protect(&c->out, p, 12 + length);
    if (total < 0) return -1;
    send_selected(c, p, total);

    c->sent_packets++;
    c->sent_octets += (uint32_t)length;
    c->last_timestamp = timestamp;
    return 0;
}

static int receive_audio_inner(void *handle, unsigned char *out, int capacity,
                               unsigned int *timestamp, unsigned int *sequence)
{
    call_t *c = (call_t *)handle;
    rx_slot *slot;
    int length;

    if (c == NULL || out == NULL) return 0;
    if (c->rx_head == c->rx_tail) return 0;

    slot = &c->rx[c->rx_head];
    length = slot->length < capacity ? slot->length : capacity;
    memcpy(out, slot->data, length);
    if (timestamp) *timestamp = slot->timestamp;
    if (sequence) *sequence = slot->sequence;
    c->rx_head = (c->rx_head + 1) % RX_SLOTS;
    return length;
}

/* ------------------------------------------------------------------ */
/* Ловушка сбоев                                                       */
/* ------------------------------------------------------------------ */

/* Сбой внутри ядра (обращение мимо памяти и прочее) валил бы приложение
 * целиком, без следа в журнале. На Windows его можно поймать: ядро
 * объявляет звонок развалившимся и говорит, на каком шаге упало. */
#ifdef _MSC_VER
static void guard_failed(void *handle, const char *where, unsigned long code)
{
    call_t *c = (call_t *)handle;
    if (c == NULL) return;
    c->failed = 1;
    emit(c, "failed сбой в ядре: %s, шаг %d, код 0x%08lX", where, c->stage, code);
}
#endif

int LOVIT_CALL_CC lovit_call_poll(void *handle, int timeout_ms)
{
#ifdef _MSC_VER
    __try {
        return poll_inner(handle, timeout_ms);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        guard_failed(handle, "poll", GetExceptionCode());
        return -1;
    }
#else
    return poll_inner(handle, timeout_ms);
#endif
}

int LOVIT_CALL_CC lovit_call_send_audio(void *handle, const unsigned char *opus, int length,
                                        unsigned int timestamp)
{
#ifdef _MSC_VER
    __try {
        return send_audio_inner(handle, opus, length, timestamp);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        guard_failed(handle, "send_audio", GetExceptionCode());
        return -1;
    }
#else
    return send_audio_inner(handle, opus, length, timestamp);
#endif
}

int LOVIT_CALL_CC lovit_call_receive_audio(void *handle, unsigned char *out, int capacity,
                                           unsigned int *timestamp, unsigned int *sequence)
{
#ifdef _MSC_VER
    __try {
        return receive_audio_inner(handle, out, capacity, timestamp, sequence);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        guard_failed(handle, "receive_audio", GetExceptionCode());
        return 0;
    }
#else
    return receive_audio_inner(handle, out, capacity, timestamp, sequence);
#endif
}

void LOVIT_CALL_CC lovit_call_destroy(void *handle)
{
    call_t *c = (call_t *)handle;
    if (c == NULL) return;

    if (c->ssl != NULL) {
        if (c->dtls_state == 2) {
            SSL_shutdown(c->ssl);
            flush_dtls(c);
        }
        SSL_free(c->ssl);   /* освобождает и оба BIO */
    }
    if (c->ctx != NULL) SSL_CTX_free(c->ctx);
    if (c->cert != NULL) X509_free(c->cert);
    if (c->key != NULL) EVP_PKEY_free(c->key);

    /* Ретранслятор отпустить сразу, а не ждать, пока истечёт сам. */
    if (c->turn_state == 3) {
        msg_t m;
        uint8_t txid[12];
        RAND_bytes(txid, 12);
        msg_begin(&m, TURN_REFRESH_REQUEST, txid);
        msg_attr32(&m, ATTR_LIFETIME, 0);
        turn_sign(c, &m);
        send_to(c, c->turn, m.data, m.length);
    }

    if (c->sock != BAD_SOCKET) close_socket(c->sock);
    free(c);
}
