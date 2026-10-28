/*
 * lovit_webp.c — разбор картинок WebP.
 *
 * Зачем. CDN мессенджера MAX отдаёт изображения только в WebP: и аватарки,
 * и фотографии, и стикеры. Windows Phone 8.1 этот формат не знает вовсе —
 * ни BitmapDecoder, ни BitmapImage его не открывают, и без своего разбора
 * в приложении не видно ни одного лица.
 *
 * Отдаём пиксели в BGRA с домноженной прозрачностью: ровно в таком виде их
 * ждёт WriteableBitmap, и перекладывать их потом в C# было бы лишней работой
 * над каждой картинкой.
 */

#include <stdlib.h>
#include <string.h>

#include "webp/decode.h"

#include "lovit_webp.h"

int __cdecl lovit_webp_is_webp(const unsigned char *data, int size)
{
    if (data == NULL || size < 12) return 0;

    return data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';
}

int __cdecl lovit_webp_size(const unsigned char *data, int size, int *width, int *height)
{
    if (!lovit_webp_is_webp(data, size)) return 0;
    return WebPGetInfo(data, (size_t)size, width, height);
}

unsigned char * __cdecl lovit_webp_decode(const unsigned char *data, int size,
                                          int *width, int *height)
{
    uint8_t *pixels;
    int w = 0;
    int h = 0;
    int count;
    int i;

    if (width != NULL) *width = 0;
    if (height != NULL) *height = 0;

    if (!lovit_webp_is_webp(data, size)) return NULL;

    pixels = WebPDecodeBGRA(data, (size_t)size, &w, &h);
    if (pixels == NULL || w <= 0 || h <= 0) {
        if (pixels != NULL) WebPFree(pixels);
        return NULL;
    }

    /*
     * Прозрачность домножается здесь же. WriteableBitmap считает пиксели уже
     * домноженными, и полупрозрачные края стикеров без этого светятся белым
     * ореолом — заметнее всего на тёмной теме.
     */
    count = w * h;
    for (i = 0; i < count; i++) {
        uint8_t *pixel = pixels + i * 4;
        unsigned int alpha = pixel[3];

        if (alpha == 255) continue;

        pixel[0] = (uint8_t)((pixel[0] * alpha + 127) / 255);
        pixel[1] = (uint8_t)((pixel[1] * alpha + 127) / 255);
        pixel[2] = (uint8_t)((pixel[2] * alpha + 127) / 255);
    }

    if (width != NULL) *width = w;
    if (height != NULL) *height = h;

    return pixels;
}

/*
 * Разбор сразу в нужный размер.
 *
 * Зачем отдельно от lovit_webp_decode. CDN отдаёт аватарку в полторы тысячи
 * точек, а показывается она квадратиком в семьдесят две. Развернуть её
 * целиком — это восемь мегабайт в памяти и сотня миллисекунд работы на
 * каждую; сорок чатов складывались в секунды ожидания и в расход, за который
 * телефон закрывал приложение.
 *
 * Здесь этого не происходит вовсе: libwebp умеет уменьшать по ходу разбора,
 * и полной картинки не существует ни мгновения. Уменьшение при этом честное,
 * с усреднением, а не выбрасыванием точек.
 *
 * max_side — предел по большей стороне; ноль или размер меньше предела
 * означают «как есть».
 */
unsigned char * __cdecl lovit_webp_decode_scaled(const unsigned char *data, int size,
                                                 int max_side, int *width, int *height)
{
    WebPDecoderConfig config;
    uint8_t *pixels;
    size_t stride;
    size_t total;
    int w;
    int h;
    int side;

    if (width != NULL) *width = 0;
    if (height != NULL) *height = 0;

    if (!lovit_webp_is_webp(data, size)) return NULL;
    if (!WebPInitDecoderConfig(&config)) return NULL;

    if (WebPGetFeatures(data, (size_t)size, &config.input) != VP8_STATUS_OK) return NULL;

    w = config.input.width;
    h = config.input.height;
    if (w <= 0 || h <= 0) return NULL;

    side = (w > h) ? w : h;

    if (max_side > 0 && side > max_side) {
        /* Пропорции сохраняем; сторона меньше единицы не бывает. */
        if (w >= h) {
            h = (int)(((long long)h * max_side + w / 2) / w);
            w = max_side;
        } else {
            w = (int)(((long long)w * max_side + h / 2) / h);
            h = max_side;
        }

        if (w < 1) w = 1;
        if (h < 1) h = 1;

        config.options.use_scaling = 1;
        config.options.scaled_width = w;
        config.options.scaled_height = h;
    }

    stride = (size_t)w * 4;
    total = stride * (size_t)h;

    /*
     * Обычный malloc: WebPFree в этой версии библиотеки — это и есть free,
     * так что освобождение через lovit_webp_free остаётся верным. WebPMalloc
     * здесь ещё нет, он появился позже.
     */
    pixels = (uint8_t *)malloc(total);
    if (pixels == NULL) return NULL;

    /*
     * MODE_bgrA — сразу BGRA с домноженной прозрачностью, в том виде, в
     * каком её ждёт WriteableBitmap. Домножать вручную, как в разборе
     * целиком, здесь уже не нужно.
     */
    config.output.colorspace = MODE_bgrA;
    config.output.is_external_memory = 1;
    config.output.u.RGBA.rgba = pixels;
    config.output.u.RGBA.stride = (int)stride;
    config.output.u.RGBA.size = total;

    if (WebPDecode(data, (size_t)size, &config) != VP8_STATUS_OK) {
        WebPFree(pixels);
        return NULL;
    }

    if (width != NULL) *width = w;
    if (height != NULL) *height = h;

    return pixels;
}

void __cdecl lovit_webp_free(unsigned char *pixels)
{
    /* Своим free() освобождать нельзя: libwebp выделяет распределителем. */
    if (pixels != NULL) WebPFree(pixels);
}
