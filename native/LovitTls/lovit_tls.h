/*
 * lovit_tls.h — защищённое соединение на своей криптографии.
 *
 * Зачем это вообще есть. Windows Phone 8.1 умеет TLS 1.2 и сама, но
 * разговаривает так, как решили в 2014 году: свой список шифров, свой
 * набор кривых и своё хранилище корней, которое пополнить нечем — система
 * снята с поддержки. Сервер MAX выдан НУЦ Минцифры (этого корня на телефоне
 * нет) и из шифров эпохи Windows Phone принимает считанные единицы. Свою
 * библиотеку можно настроить как надо, и разговор выглядит так, как задали
 * мы, а не так, как решил телефон.
 *
 * То же самое и по той же причине сделано в версии для iOS 6 — там это
 * src/Core/Transport/MAXTls.h.
 *
 * Работает синхронно и блокирующе: асинхронность есть у вызывающего, и
 * второй такой же здесь была бы лишней. Чтение и запись можно звать из
 * разных потоков — внутри они разведены.
 */

#ifndef LOVIT_TLS_H
#define LOVIT_TLS_H

#ifdef __cplusplus
extern "C" {
#endif

/* Возвраты чтения и записи. */
#define LOVIT_CLOSED     0   /* сервер закрыл соединение */
#define LOVIT_ERROR    (-1)  /* обрыв или отказ, подробности — lovit_tls_error */
#define LOVIT_TIMEOUT  (-2)  /* данных пока нет, соединение живо */

/* Один раз за запуск: инициализация библиотеки и вшитые корни.
 * Возвращает число загруженных корней или отрицательное при отказе. */
__declspec(dllexport) int __cdecl lovit_tls_prepare(void);

/* Подключается и проводит рукопожатие. Ноль при отказе. */
__declspec(dllexport) void * __cdecl lovit_tls_open(const char *host, int port, int timeout_ms);

/* Больше нуля — прочитано; LOVIT_CLOSED, LOVIT_TIMEOUT или LOVIT_ERROR. */
__declspec(dllexport) int __cdecl lovit_tls_read(void *handle, unsigned char *buffer, int length, int timeout_ms);

/* Записывает всё целиком. Возвращает записанное или LOVIT_ERROR. */
__declspec(dllexport) int __cdecl lovit_tls_write(void *handle, const unsigned char *buffer, int length);

/* Закрывает и освобождает. После вызова обращаться к handle нельзя. */
__declspec(dllexport) void __cdecl lovit_tls_close(void *handle);

/* Текст последней ошибки. Если handle нулевой — ошибка последнего открытия. */
__declspec(dllexport) int __cdecl lovit_tls_error(void *handle, char *buffer, int length);

/* О чём договорились: «TLSv1.2 ECDHE-RSA-AES128-SHA256». Первое, что стоит
 * смотреть при разборе отказов. */
__declspec(dllexport) int __cdecl lovit_tls_session(void *handle, char *buffer, int length);

#ifdef __cplusplus
}
#endif

#endif
