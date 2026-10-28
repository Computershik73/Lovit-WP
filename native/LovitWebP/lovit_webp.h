/*
 * lovit_webp.h — разбор картинок WebP для «Ловита».
 *
 * CDN мессенджера MAX отдаёт изображения только в WebP, а Windows Phone 8.1
 * такого формата не знает. Подробности — в lovit_webp.c.
 */

#ifndef LOVIT_WEBP_H
#define LOVIT_WEBP_H

#ifdef __cplusplus
extern "C" {
#endif

/* Похоже ли это на WebP: заголовок RIFF….WEBP. */
__declspec(dllexport) int __cdecl lovit_webp_is_webp(const unsigned char *data, int size);

/* Размеры без разбора самой картинки. */
__declspec(dllexport) int __cdecl lovit_webp_size(const unsigned char *data, int size,
                                                  int *width, int *height);

/* Пиксели BGRA с домноженной прозрачностью. Освобождать lovit_webp_free. */
__declspec(dllexport) unsigned char * __cdecl lovit_webp_decode(const unsigned char *data, int size,
                                                                int *width, int *height);

/*
 * То же, но не крупнее max_side по большей стороне: уменьшение идёт по ходу
 * разбора, и полной картинки в памяти не появляется. Ноль означает «как
 * есть». Освобождать так же — lovit_webp_free.
 */
__declspec(dllexport) unsigned char * __cdecl lovit_webp_decode_scaled(
    const unsigned char *data, int size, int max_side, int *width, int *height);

__declspec(dllexport) void __cdecl lovit_webp_free(unsigned char *pixels);

#ifdef __cplusplus
}
#endif

#endif
