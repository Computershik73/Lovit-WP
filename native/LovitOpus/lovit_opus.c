/* Декодер Opus для «Ловита».
 *
 * Голосовые в MAX приходят в Opus, а этого кодека в Windows Phone 8.1 нет
 * вовсе — система отказывает с «формат не поддерживается». Здесь libopus
 * превращает их в обычные звуковые отсчёты, а разбор контейнера Ogg и
 * заворачивание в WAV остаются на стороне приложения: там это делается
 * несколькими десятками строк и без нативного кода.
 *
 * Наружу вынесено ровно три действия. Состояние декодера обязано жить между
 * пакетами: Opus сжимает с оглядкой на предыдущий звук, и на каждый пакет
 * заводить новый декодер нельзя — выйдет треск на стыках.
 */

#include <stdlib.h>
#include "opus.h"
#include "lovit_opus.h"

/* Больше этого за один пакет Opus не отдаёт: 120 мс при 48 кГц. */
#define LOVIT_OPUS_MAX_FRAME 5760

void * __cdecl lovit_opus_create(int sample_rate, int channels)
{
    int error = OPUS_OK;
    OpusDecoder *decoder;

    if (channels < 1 || channels > 2) return NULL;

    decoder = opus_decoder_create(sample_rate, channels, &error);
    if (error != OPUS_OK) return NULL;

    return decoder;
}

int __cdecl lovit_opus_decode(void *handle, const unsigned char *packet, int length,
                              short *pcm, int max_samples)
{
    OpusDecoder *decoder = (OpusDecoder *)handle;
    int decoded;

    if (decoder == NULL || pcm == NULL || max_samples <= 0) return -1;

    /* Пустой пакет — не ошибка, а потеря: Opus умеет достроить пропущенное
     * по предыдущему звуку, для этого ему и передаётся пустота. */
    decoded = opus_decode(decoder, packet, length, pcm, max_samples, 0);

    return decoded < 0 ? -1 : decoded;
}

void __cdecl lovit_opus_destroy(void *handle)
{
    if (handle == NULL) return;
    opus_decoder_destroy((OpusDecoder *)handle);
}

int __cdecl lovit_opus_max_frame(void)
{
    return LOVIT_OPUS_MAX_FRAME;
}

/* Запись голосового: та же библиотека, обратный ход.
 *
 * Своего Opus у телефона нет ни на раскодирование, ни на кодирование, а
 * сервер принимает голосовые только в нём. Микрофон отдаёт обычные отсчёты,
 * и превратить их в то, что примут на той стороне, больше нечем. */
void * __cdecl lovit_opus_encoder_create(int sample_rate, int channels, int bitrate)
{
    int error = OPUS_OK;
    OpusEncoder *encoder;

    if (channels < 1 || channels > 2) return NULL;

    /* Речь, а не музыка: при OPUS_APPLICATION_VOIP кодек бережёт разборчивость
     * голоса, а не общую верность звучания. */
    encoder = opus_encoder_create(sample_rate, channels, OPUS_APPLICATION_VOIP, &error);
    if (error != OPUS_OK || encoder == NULL) return NULL;

    if (bitrate > 0) opus_encoder_ctl(encoder, OPUS_SET_BITRATE(bitrate));

    return encoder;
}

int __cdecl lovit_opus_encode(void *handle, const short *pcm, int samples,
                              unsigned char *packet, int max_length)
{
    OpusEncoder *encoder = (OpusEncoder *)handle;
    int written;

    if (encoder == NULL || pcm == NULL || packet == NULL) return -1;
    if (samples <= 0 || max_length <= 0) return -1;

    written = opus_encode(encoder, pcm, samples, packet, max_length);

    return written < 0 ? -1 : written;
}

void __cdecl lovit_opus_encoder_destroy(void *handle)
{
    if (handle == NULL) return;
    opus_encoder_destroy((OpusEncoder *)handle);
}
