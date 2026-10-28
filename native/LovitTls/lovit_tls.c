/*
 * lovit_tls.c — TLS для «Ловита» на OpenSSL 1.0.2.
 *
 * Подробности — в lovit_tls.h.
 */

#define _CRT_RAND_S

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/rand.h>
#include <openssl/x509v3.h>

#include "lovit_tls.h"
#include "roots.h"

/* Рукопожатие и запись не ждут дольше этого.
 *
 * Для записи это срок застоя, а не всей передачи: пока байты уходят, отсчёт
 * начинается заново. Иначе вложение, которое честно передаётся дольше срока,
 * обрывалось бы на середине — а полтора мегабайта по мобильной связи дольше
 * пятнадцати секунд идут почти всегда. */
#define LOVIT_WRITE_TIMEOUT_MS 15000

/* За раз отдаём OpenSSL не больше этого.
 *
 * Без ограничения SSL_write на полутора мегабайтах возвращает успех ровно
 * один раз — в самом конце, — а до тех пор только просит подождать. Из такого
 * ответа не отличить работающую передачу от намертво вставшей, и срок застоя
 * считать не по чему. Кусками же каждый успех виден сразу.
 *
 * Размер выбран по записи TLS: больше одной записи всё равно не уйдёт. */
#define LOVIT_WRITE_CHUNK 16384

typedef struct {
    SOCKET sock;
    SSL *ssl;
    CRITICAL_SECTION lock;
    volatile LONG refs;
    volatile LONG closing;
    char error[256];
} lovit_conn;

static SSL_CTX *g_ctx;
static LONG g_prepared;
static char g_error[256];
static CRITICAL_SECTION *g_locks;

/* ------------------------------------------------------------------ */
/* Замки OpenSSL                                                       */
/* ------------------------------------------------------------------ */

/*
 * OpenSSL 1.0.2 сам о потоках не заботится: разделяемые части — очередь
 * ошибок, кэш сеансов, генератор случайных чисел — защищаются замками,
 * которые обязано завести приложение. Без них библиотека рано или поздно
 * падает, и падает не там, где виновата.
 *
 * Нам это нужно даже при единственном соединении: вызовы уходят в рабочие
 * потоки, и каждый следующий может оказаться на другом.
 *
 * С 1.1.0 библиотека сама себя бережёт, а этих вызовов в ней просто не
 * стало. Телефон собирается с 1.0.2 и идёт первой дорогой, настольная
 * Windows — с нынешней, и ей заводить нечего.
 */
#if OPENSSL_VERSION_NUMBER < 0x10100000L

static void locking_callback(int mode, int type, const char *file, int line)
{
    (void)file;
    (void)line;

    if (g_locks == NULL) return;

    if (mode & CRYPTO_LOCK) {
        EnterCriticalSection(&g_locks[type]);
    } else {
        LeaveCriticalSection(&g_locks[type]);
    }
}

static void threadid_callback(CRYPTO_THREADID *id)
{
    CRYPTO_THREADID_set_numeric(id, (unsigned long)GetCurrentThreadId());
}

static int start_locks(void)
{
    int count = CRYPTO_num_locks();
    int i;

    g_locks = (CRITICAL_SECTION *)calloc((size_t)count, sizeof(CRITICAL_SECTION));
    if (g_locks == NULL) return 0;

    for (i = 0; i < count; i++) InitializeCriticalSectionEx(&g_locks[i], 0, 0);

    CRYPTO_THREADID_set_callback(threadid_callback);
    CRYPTO_set_locking_callback(locking_callback);

    return 1;
}

#else

/* Новой библиотеке замки не нужны — и завести их нечем. */
static int start_locks(void)
{
    return 1;
}

#endif

/* ------------------------------------------------------------------ */
/* Ошибки                                                              */
/* ------------------------------------------------------------------ */

static void set_error(lovit_conn *conn, const char *what)
{
    char detail[160];
    unsigned long code;
    char *target = conn != NULL ? conn->error : g_error;

    detail[0] = 0;

    /* Из очереди берётся первая ошибка: последующие — обычно её же
     * пересказ на других уровнях библиотеки. */
    code = ERR_get_error();
    if (code != 0) {
        ERR_error_string_n(code, detail, sizeof(detail));
        ERR_clear_error();
    }

    if (detail[0] != 0) {
        _snprintf_s(target, 256, _TRUNCATE, "%s: %s", what, detail);
    } else {
        _snprintf_s(target, 256, _TRUNCATE, "%s (код %d)", what, WSAGetLastError());
    }
}

/* ------------------------------------------------------------------ */
/* Подготовка                                                          */
/* ------------------------------------------------------------------ */

/*
 * Затравка для генератора случайных чисел.
 *
 * Своего источника у OpenSSL в песочнице телефона нет: путей к /dev/urandom
 * не существует, а к системному хранилищу энтропии из контейнера приложения
 * не достучаться. `rand_s` — это RtlGenRandom, он в песочнице разрешён и
 * годится ровно для этого.
 */
static void seed_random(void)
{
    unsigned int chunk;
    int i;

    for (i = 0; i < 16; i++) {
        if (rand_s(&chunk) != 0) continue;
        RAND_seed(&chunk, (int)sizeof(chunk));
    }
}

static int load_roots(SSL_CTX *ctx)
{
    X509_STORE *store = SSL_CTX_get_cert_store(ctx);
    int loaded = 0;
    int i;

    if (store == NULL) return 0;

    for (i = 0; i < LOVIT_ROOTS_COUNT; i++) {
        const unsigned char *cursor = lovit_roots[i].data;
        X509 *cert = d2i_X509(NULL, &cursor, (long)lovit_roots[i].length);

        if (cert == NULL) continue;

        if (X509_STORE_add_cert(store, cert)) loaded++;
        X509_free(cert);
    }

    return loaded;
}

int __cdecl lovit_tls_prepare(void)
{
    WSADATA data;
    int roots;

    if (InterlockedCompareExchange(&g_prepared, 1, 0) != 0) {
        return g_ctx != NULL ? 1 : -1;
    }

    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
        _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "не поднялся Winsock");
        return -1;
    }

    SSL_library_init();
    SSL_load_error_strings();
    OpenSSL_add_all_algorithms();

    if (!start_locks()) {
        _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "не завелись замки");
        return -1;
    }

    seed_random();

    /* Метод берётся общий, а версии отсекаются ключами: в 1.0.2 отдельного
     * «только TLS 1.2» нет, а предлагать серверу протоколы девяностых —
     * значит самому себя объявить подозрительным. */
    g_ctx = SSL_CTX_new(SSLv23_client_method());
    if (g_ctx == NULL) {
        set_error(NULL, "не создался контекст");
        return -1;
    }

    SSL_CTX_set_options(g_ctx,
                        SSL_OP_NO_SSLv2 | SSL_OP_NO_SSLv3 |
                        SSL_OP_NO_TLSv1 | SSL_OP_NO_TLSv1_1 |
                        SSL_OP_NO_COMPRESSION);

    SSL_CTX_set_verify(g_ctx, SSL_VERIFY_PEER, NULL);
    SSL_CTX_set_verify_depth(g_ctx, 6);

    /* Порядок тот же, что в версии для iOS. Сервер MAX из шифров, доступных
     * старым системам, принимает считанные единицы — ECDHE-RSA-AES128-SHA256
     * держится в списке именно поэтому, хотя по нынешним меркам он и слаб. */
    SSL_CTX_set_cipher_list(g_ctx,
        "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:"
        "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:"
        "ECDHE-RSA-AES128-SHA256:ECDHE-RSA-AES128-SHA:AES128-GCM-SHA256");

    roots = load_roots(g_ctx);
    if (roots == 0) {
        _snprintf_s(g_error, sizeof(g_error), _TRUNCATE, "не загрузился ни один корень");
        return -1;
    }

    return roots;
}

/* ------------------------------------------------------------------ */
/* Ожидание сокета                                                     */
/* ------------------------------------------------------------------ */

/* Ноль — не дождались, единица — готов, минус один — отказ. */
static int wait_socket(SOCKET sock, int for_write, int timeout_ms)
{
    fd_set set;
    struct timeval limit;
    int ready;

    FD_ZERO(&set);
    FD_SET(sock, &set);

    limit.tv_sec = timeout_ms / 1000;
    limit.tv_usec = (timeout_ms % 1000) * 1000;

    ready = select(0,
                   for_write ? NULL : &set,
                   for_write ? &set : NULL,
                   NULL,
                   &limit);

    if (ready == 0) return 0;
    return ready > 0 ? 1 : -1;
}

/* ------------------------------------------------------------------ */
/* Подключение                                                         */
/* ------------------------------------------------------------------ */

static SOCKET connect_socket(const char *host, int port, int timeout_ms)
{
    struct addrinfo hints;
    struct addrinfo *found = NULL;
    struct addrinfo *step;
    char service[16];
    SOCKET sock = INVALID_SOCKET;

    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    _snprintf_s(service, sizeof(service), _TRUNCATE, "%d", port);

    if (getaddrinfo(host, service, &hints, &found) != 0 || found == NULL) {
        _snprintf_s(g_error, sizeof(g_error), _TRUNCATE,
                    "имя %s не разрешилось (код %d)", host, WSAGetLastError());
        return INVALID_SOCKET;
    }

    for (step = found; step != NULL; step = step->ai_next) {
        u_long nonblocking = 1;
        int outcome;

        sock = socket(step->ai_family, step->ai_socktype, step->ai_protocol);
        if (sock == INVALID_SOCKET) continue;

        /* Неблокирующий сокет с самого начала: только так у подключения
         * появляется свой срок, а не тот, что назначит система. */
        ioctlsocket(sock, FIONBIO, &nonblocking);

        outcome = connect(sock, step->ai_addr, (int)step->ai_addrlen);
        if (outcome == 0) break;

        if (WSAGetLastError() == WSAEWOULDBLOCK && wait_socket(sock, 1, timeout_ms) == 1) {
            int failure = 0;
            int size = (int)sizeof(failure);

            /* select сказал «готов» и об удаче, и об отказе — какой именно
             * случай, знает только сам сокет. */
            if (getsockopt(sock, SOL_SOCKET, SO_ERROR, (char *)&failure, &size) == 0 &&
                failure == 0) {
                break;
            }
        }

        closesocket(sock);
        sock = INVALID_SOCKET;
    }

    freeaddrinfo(found);

    if (sock == INVALID_SOCKET) {
        _snprintf_s(g_error, sizeof(g_error), _TRUNCATE,
                    "сокет до %s:%d не открылся (код %d)", host, port, WSAGetLastError());
    }

    return sock;
}

/* GetTickCount64, а не GetTickCount: в песочнице телефона второго просто
 * нет — он остался в настольной части Windows. */
static int shake_hands(lovit_conn *conn, int timeout_ms)
{
    ULONGLONG deadline = GetTickCount64() + (ULONGLONG)timeout_ms;

    for (;;) {
        int outcome;
        int reason;
        int remaining;

        ERR_clear_error();
        outcome = SSL_connect(conn->ssl);

        if (outcome == 1) return 1;

        reason = SSL_get_error(conn->ssl, outcome);

        if (reason != SSL_ERROR_WANT_READ && reason != SSL_ERROR_WANT_WRITE) {
            long verdict = SSL_get_verify_result(conn->ssl);

            if (verdict != X509_V_OK) {
                _snprintf_s(conn->error, sizeof(conn->error), _TRUNCATE,
                            "сертификат не принят: %s (%ld)",
                            X509_verify_cert_error_string(verdict), verdict);
            } else {
                set_error(conn, "рукопожатие не состоялось");
            }
            return 0;
        }

        remaining = (int)(deadline - GetTickCount64());
        if (remaining <= 0) {
            _snprintf_s(conn->error, sizeof(conn->error), _TRUNCATE,
                        "рукопожатие не уложилось в срок");
            return 0;
        }

        if (wait_socket(conn->sock, reason == SSL_ERROR_WANT_WRITE ? 1 : 0, remaining) < 0) {
            set_error(conn, "обрыв при рукопожатии");
            return 0;
        }
    }
}

void * __cdecl lovit_tls_open(const char *host, int port, int timeout_ms)
{
    lovit_conn *conn;
    SOCKET sock;

    if (g_ctx == NULL && lovit_tls_prepare() < 0) return NULL;

    sock = connect_socket(host, port, timeout_ms);
    if (sock == INVALID_SOCKET) return NULL;

    conn = (lovit_conn *)calloc(1, sizeof(lovit_conn));
    if (conn == NULL) {
        closesocket(sock);
        return NULL;
    }

    conn->sock = sock;
    conn->refs = 1;
    InitializeCriticalSectionEx(&conn->lock, 0, 0);

    conn->ssl = SSL_new(g_ctx);
    if (conn->ssl == NULL) {
        set_error(NULL, "не создалось соединение");
        DeleteCriticalSection(&conn->lock);
        closesocket(sock);
        free(conn);
        return NULL;
    }

    SSL_set_fd(conn->ssl, (int)sock);

    /* Имя узла нужно дважды и для разного: в расширении SNI сервер по нему
     * выбирает, каким сертификатом отвечать, а в проверке — мы по нему
     * решаем, тот ли это сертификат. Безупречная цепочка, выписанная на
     * чужой домен, без второй проверки прошла бы насквозь. */
    SSL_set_tlsext_host_name(conn->ssl, host);

    {
        X509_VERIFY_PARAM *param = SSL_get0_param(conn->ssl);
        X509_VERIFY_PARAM_set_hostflags(param, X509_CHECK_FLAG_NO_PARTIAL_WILDCARDS);
        X509_VERIFY_PARAM_set1_host(param, host, 0);
    }

    if (!shake_hands(conn, timeout_ms)) {
        /* Текст ошибки нужен вызывающему, а соединения уже нет: переносим
         * его в общий, откуда его заберут с нулевым handle. */
        memcpy(g_error, conn->error, sizeof(g_error));

        SSL_free(conn->ssl);
        DeleteCriticalSection(&conn->lock);
        closesocket(sock);
        free(conn);
        return NULL;
    }

    return conn;
}

/* ------------------------------------------------------------------ */
/* Счёт ссылок                                                         */
/* ------------------------------------------------------------------ */

/*
 * Читающий поток сидит в ожидании, когда другой поток закрывает соединение,
 * — и освобождать память под ним нельзя. Поэтому у соединения есть счёт
 * ссылок: закрытие лишь будит сокет и отпускает свою долю, а освобождает
 * тот, кто ушёл последним.
 */
static void release(lovit_conn *conn)
{
    if (InterlockedDecrement(&conn->refs) != 0) return;

    if (conn->ssl != NULL) SSL_free(conn->ssl);
    if (conn->sock != INVALID_SOCKET) closesocket(conn->sock);

    DeleteCriticalSection(&conn->lock);
    free(conn);
}

/* ------------------------------------------------------------------ */
/* Обмен                                                               */
/* ------------------------------------------------------------------ */

int __cdecl lovit_tls_read(void *handle, unsigned char *buffer, int length, int timeout_ms)
{
    lovit_conn *conn = (lovit_conn *)handle;
    int outcome;
    int reason;

    if (conn == NULL || buffer == NULL || length <= 0) return LOVIT_ERROR;

    InterlockedIncrement(&conn->refs);

    if (conn->closing) {
        release(conn);
        return LOVIT_ERROR;
    }

    EnterCriticalSection(&conn->lock);
    /* Очередь ошибок общая на поток: чужая ошибка, оставшаяся от прошлого
     * вызова, иначе выдалась бы за нашу. */
    ERR_clear_error();
    outcome = SSL_read(conn->ssl, buffer, length);
    reason = outcome > 0 ? SSL_ERROR_NONE : SSL_get_error(conn->ssl, outcome);
    LeaveCriticalSection(&conn->lock);

    if (outcome > 0) {
        release(conn);
        return outcome;
    }

    switch (reason) {
    case SSL_ERROR_WANT_READ:
    case SSL_ERROR_WANT_WRITE:
        /* Ожидание — вне замка: иначе отправка ждала бы приходящего, а
         * приходить может нечему часами. */
        if (!conn->closing) {
            wait_socket(conn->sock, reason == SSL_ERROR_WANT_WRITE ? 1 : 0, timeout_ms);
        }
        release(conn);
        return LOVIT_TIMEOUT;

    case SSL_ERROR_ZERO_RETURN:
        release(conn);
        return LOVIT_CLOSED;

    case SSL_ERROR_SYSCALL:
        /* Ноль здесь — не ошибка, а невежливо закрытое соединение: сервер
         * оборвал сокет, не прислав уведомления TLS. */
        if (outcome == 0) {
            release(conn);
            return LOVIT_CLOSED;
        }
        /* дальше как обычная ошибка */

    default:
        set_error(conn, "чтение не удалось");
        release(conn);
        return LOVIT_ERROR;
    }
}

int __cdecl lovit_tls_write(void *handle, const unsigned char *buffer, int length)
{
    lovit_conn *conn = (lovit_conn *)handle;
    ULONGLONG deadline;
    int sent = 0;

    if (conn == NULL || buffer == NULL || length <= 0) return LOVIT_ERROR;

    InterlockedIncrement(&conn->refs);

    if (conn->closing) {
        release(conn);
        return LOVIT_ERROR;
    }

    deadline = GetTickCount64() + LOVIT_WRITE_TIMEOUT_MS;

    while (sent < length) {
        int outcome;
        int reason;
        int remaining;
        int piece = length - sent;

        if (piece > LOVIT_WRITE_CHUNK) piece = LOVIT_WRITE_CHUNK;

        EnterCriticalSection(&conn->lock);
        ERR_clear_error();
        /* Повтор после «подожди» обязан повторить и доводы: OpenSSL сверяет
         * их с прошлым разом. Здесь они считаются от sent, который на неудаче
         * не меняется, — значит совпадут. */
        outcome = SSL_write(conn->ssl, buffer + sent, piece);
        reason = outcome > 0 ? SSL_ERROR_NONE : SSL_get_error(conn->ssl, outcome);
        LeaveCriticalSection(&conn->lock);

        if (outcome > 0) {
            sent += outcome;

            /* Кусок ушёл — связь жива, и срок отсчитывается заново. */
            deadline = GetTickCount64() + LOVIT_WRITE_TIMEOUT_MS;
            continue;
        }

        if (reason != SSL_ERROR_WANT_READ && reason != SSL_ERROR_WANT_WRITE) {
            set_error(conn, "отправка не удалась");
            release(conn);
            return LOVIT_ERROR;
        }

        remaining = (int)(deadline - GetTickCount64());
        if (remaining <= 0 || conn->closing) {
            _snprintf_s(conn->error, sizeof(conn->error), _TRUNCATE,
                        "отправка встала: за %d с не ушло ни байта",
                        LOVIT_WRITE_TIMEOUT_MS / 1000);
            release(conn);
            return LOVIT_ERROR;
        }

        wait_socket(conn->sock, reason == SSL_ERROR_WANT_WRITE ? 1 : 0, remaining);
    }

    release(conn);
    return sent;
}

void __cdecl lovit_tls_close(void *handle)
{
    lovit_conn *conn = (lovit_conn *)handle;

    if (conn == NULL) return;

    InterlockedExchange(&conn->closing, 1);

    /* Разрыв сокета будит того, кто ждёт данных: без этого читающий поток
     * простоял бы в ожидании до самого тайм-аута. */
    if (conn->sock != INVALID_SOCKET) shutdown(conn->sock, SD_BOTH);

    release(conn);
}

/* ------------------------------------------------------------------ */
/* Справки                                                             */
/* ------------------------------------------------------------------ */

int __cdecl lovit_tls_error(void *handle, char *buffer, int length)
{
    lovit_conn *conn = (lovit_conn *)handle;
    const char *source = conn != NULL ? conn->error : g_error;

    if (buffer == NULL || length <= 0) return 0;

    _snprintf_s(buffer, (size_t)length, _TRUNCATE, "%s", source);
    return (int)strlen(buffer);
}

int __cdecl lovit_tls_session(void *handle, char *buffer, int length)
{
    lovit_conn *conn = (lovit_conn *)handle;
    const SSL_CIPHER *cipher;

    if (buffer == NULL || length <= 0) return 0;

    if (conn == NULL || conn->ssl == NULL) {
        buffer[0] = 0;
        return 0;
    }

    cipher = SSL_get_current_cipher(conn->ssl);

    _snprintf_s(buffer, (size_t)length, _TRUNCATE, "%s %s",
                SSL_get_version(conn->ssl),
                cipher != NULL ? SSL_CIPHER_get_name(cipher) : "?");

    return (int)strlen(buffer);
}
