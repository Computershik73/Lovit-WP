/*
 * Медиаядро звонка MAX: ICE, STUN/TURN, DTLS-SRTP и упаковка RTP.
 *
 * Звонок 1:1 в MAX — обычное WebRTC-соединение с телефоном собеседника. Готового
 * WebRTC под Windows Phone 8.1 и iOS 6 нет, поэтому здесь ровно та его часть,
 * без которой не обойтись в голосовом звонке:
 *
 *   ICE    — найти путь до собеседника: свои адреса, адрес снаружи (STUN) и
 *            ретранслятор (TURN), проверки связности. Отвечая на звонок, мы
 *            подчиняемся (controlled); звоня сами — ведём (controlling).
 *   DTLS   — рукопожатие поверх найденного пути: отвечая — клиент, звоня —
 *            как скажет собеседник (обычно сервер).
 *   SRTP   — ключи из DTLS, шифрование AES-CM-128 и подпись HMAC-SHA1-80.
 *   RTP    — пакеты Opus туда и обратно.
 *
 * Всё остальное — SDP, сигнализация, звук и кодек — у вызывающего кода.
 *
 * Ядро однопоточное: все вызовы — с одного потока, который и крутит
 * lovit_call_poll. Своих потоков ядро не заводит: на Windows Phone 8.1 у
 * родного кода их нет, а iOS-версии так проще.
 */

#ifndef LOVIT_CALL_H
#define LOVIT_CALL_H

#ifdef _WIN32
#define LOVIT_CALL_API __declspec(dllexport)
#define LOVIT_CALL_CC __cdecl
#else
#define LOVIT_CALL_API
#define LOVIT_CALL_CC
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Заводит звонок: сокет, свои ICE-логин и пароль, сертификат DTLS. NULL — не
 * вышло (причина — в журнале событий не появится, только код возврата). */
LOVIT_CALL_API void * LOVIT_CALL_CC lovit_call_create(void);

/* Свои параметры для SDP одной строкой: "ufrag pwd fingerprint ssrc".
 * Отпечаток — sha-256, большими буквами через двоеточие. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_local(void *handle, char *out, int capacity);

/* Свой адрес в локальной сети — из системы, ядро само его не узнает. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_add_host(void *handle, const char *ip);

/* STUN — узнать свой адрес снаружи. Имя или IPv4. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_add_stun(void *handle, const char *host, int port);

/* TURN по UDP — ретранслятор, когда прямой путь закрыт. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_add_turn(void *handle, const char *host, int port,
                                                     const char *user, const char *password);

/* Параметры собеседника из его SDP. fingerprint — sha-256 как в SDP
 * ("AB:CD:..."); opus_pt — номер Opus в его предложении. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_set_remote(void *handle, const char *ufrag,
                                                       const char *password,
                                                       const char *fingerprint, int opus_pt);

/* Роли. По умолчанию — отвечающая сторона: ICE подчиняется (controlled),
 * DTLS — клиент. Звоним сами — ICE ведёт (controlling), а DTLS-роль задаёт
 * ответ собеседника: a=setup:active у него значит, что мы сервер. Менять
 * можно до начала рукопожатия. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_set_roles(void *handle, int controlling, int dtls_client);

/* Кандидат собеседника: UDP, IPv4. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_add_remote(void *handle, const char *ip, int port,
                                                       unsigned int priority);

/* Крутить сеть до timeout_ms. Возвращает состояние: 0 — ищем путь,
 * 1 — путь есть, идёт DTLS, 2 — звук идёт, -1 — всё, звонок развалился. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_poll(void *handle, int timeout_ms);

/* Очередное событие строкой, 0 — событий нет. Виды:
 *   cand host|srflx|relay <ip> <port> <priority> [<raddr> <rport>]
 *   path direct|relay <ip>:<port>
 *   ready
 *   failed <причина>
 *   log <что угодно> */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_event(void *handle, char *out, int capacity);

/* Отправить кадр Opus. timestamp — в отсчётах 48 кГц. 0 — ушёл, -1 — пути ещё нет. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_send_audio(void *handle, const unsigned char *opus,
                                                       int length, unsigned int timestamp);

/* Принятый кадр Opus: длина, 0 — пусто. seq и timestamp — из заголовка RTP. */
LOVIT_CALL_API int LOVIT_CALL_CC lovit_call_receive_audio(void *handle, unsigned char *out,
                                                          int capacity, unsigned int *timestamp,
                                                          unsigned int *sequence);

LOVIT_CALL_API void LOVIT_CALL_CC lovit_call_destroy(void *handle);

#ifdef __cplusplus
}
#endif

#endif
