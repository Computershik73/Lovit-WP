#ifndef LOVIT_OPUS_H
#define LOVIT_OPUS_H

#ifdef __cplusplus
extern "C" {
#endif

/* Заводит декодер. Частота — 8000, 12000, 16000, 24000 или 48000;
 * каналов один или два. Возвращает NULL, если не вышло. */
__declspec(dllexport) void * __cdecl lovit_opus_create(int sample_rate, int channels);

/* Раскодирует один пакет. Возвращает число отсчётов на канал либо -1.
 * Размер pcm должен быть не меньше max_samples * каналов. */
__declspec(dllexport) int __cdecl lovit_opus_decode(void *handle,
                                                    const unsigned char *packet, int length,
                                                    short *pcm, int max_samples);

__declspec(dllexport) void __cdecl lovit_opus_destroy(void *handle);

/* Наибольшее число отсчётов в одном пакете — под него отводится место. */
__declspec(dllexport) int __cdecl lovit_opus_max_frame(void);

/* Заводит кодер для записи голосового. Частота — из того же ряда, что у
 * декодера; поток задаётся в битах в секунду. NULL, если не вышло. */
__declspec(dllexport) void * __cdecl lovit_opus_encoder_create(int sample_rate, int channels,
                                                               int bitrate);

/* Кодирует один кадр. samples — отсчётов на канал, и число это не любое:
 * Opus принимает 2.5, 5, 10, 20, 40 или 60 мс. Возвращает длину пакета в
 * байтах либо -1. */
__declspec(dllexport) int __cdecl lovit_opus_encode(void *handle,
                                                    const short *pcm, int samples,
                                                    unsigned char *packet, int max_length);

__declspec(dllexport) void __cdecl lovit_opus_encoder_destroy(void *handle);

#ifdef __cplusplus
}
#endif

#endif
