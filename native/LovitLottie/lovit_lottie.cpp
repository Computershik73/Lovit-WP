/* Отрисовщик движущихся стикеров для «Ловита».
 *
 * Lottie — это не картинка, а описание векторной сцены во времени: слои,
 * контуры, превращения, ключевые кадры. Показать его можно только отрисовав,
 * и делает это rlottie — та же библиотека, что рисует такие стикеры в
 * Telegram.
 *
 * Наружу вынесено самое малое: открыть, спросить размеры и число кадров,
 * нарисовать кадр в готовый буфер. Кадры готовятся по одному, по требованию:
 * держать их все разом на телефоне негде.
 *
 * Собирается VS 2015, а не VS 2013 как остальное: rlottie написана на C++14,
 * которого компилятор 2013 года не понимает.
 */

#include <windows.h>
#include <string>
#include "rlottie.h"

extern "C" {

struct lovit_lottie
{
    std::unique_ptr<rlottie::Animation> animation;

};

/* Открывает стикер из его описания. Ключ нужен самой библиотеке для её
 * внутреннего запаса разобранных сцен; берём номер стикера. */
__declspec(dllexport) void * __cdecl lovit_lottie_open(const char *json, const char *key)
{
    if (json == nullptr) return nullptr;

    std::unique_ptr<rlottie::Animation> made =
        rlottie::Animation::loadFromData(std::string(json),
                                         key == nullptr ? std::string() : std::string(key));

    if (!made) return nullptr;

    lovit_lottie *handle = new lovit_lottie();
    handle->animation = std::move(made);

    return handle;
}

__declspec(dllexport) int __cdecl lovit_lottie_frames(void *handle)
{
    lovit_lottie *own = (lovit_lottie *)handle;
    if (own == nullptr) return 0;

    return (int)own->animation->totalFrame();
}

/* Кадров в секунду, умноженное на сто: целым числом его проще передать. */
__declspec(dllexport) int __cdecl lovit_lottie_rate(void *handle)
{
    lovit_lottie *own = (lovit_lottie *)handle;
    if (own == nullptr) return 0;

    return (int)(own->animation->frameRate() * 100.0);
}

/* Код последнего аппаратного сбоя. Ноль — сбоя не было. */
static unsigned long g_fault = 0;

/* Собственно отрисовка. Вынесена отдельно нарочно: перехват аппаратных
 * сбоев нельзя ставить в той же функции, где живут объекты C++ с
 * разрушителями, — компилятор такого не допускает. */
static int render_inner(lovit_lottie *own, int frame, unsigned char *buffer,
                        int width, int height)
{
    rlottie::Surface surface((uint32_t *)buffer,
                             (size_t)width, (size_t)height, (size_t)(width * 4));

    own->animation->renderSync((size_t)frame, surface);
    return 1;
}

/* Рисует кадр в буфер BGRA заданного размера. Возвращает 1 при удаче.
 *
 * Обёрнута перехватом аппаратных сбоев. Причина простая: переделанная под
 * старый компилятор библиотека может обратиться не туда, и тогда всё
 * приложение исчезает мгновенно, не оставив следа. Здесь же сбой
 * превращается в обычный отказ: движение не пойдёт, стикер останется
 * картинкой, а код сбоя ляжет в журнал. */
struct render_args
{
    lovit_lottie *own;
    int frame;
    unsigned char *buffer;
    int width;
    int height;
    int result;
};

static DWORD WINAPI render_thread(LPVOID raw)
{
    render_args *args = (render_args *)raw;

    __try
    {
        args->result = render_inner(args->own, args->frame, args->buffer,
                                    args->width, args->height);
    }
    __except (g_fault = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
    {
        args->result = 0;
    }

    return 0;
}

__declspec(dllexport) int __cdecl lovit_lottie_render(void *handle, int frame,
                                                      unsigned char *buffer,
                                                      int width, int height)
{
    lovit_lottie *own = (lovit_lottie *)handle;
    if (own == nullptr || buffer == nullptr || width <= 0 || height <= 0) return 0;

    /* Поток заводится на каждый кадр, и это намеренно.
     *
     * Постоянный поток был бы дешевле, но при нём отрисовка снова начала
     * срываться — а этот способ проверен и работает. Восемь мегабайт стопки
     * отводятся, а не занимаются: настоящая память берётся по мере
     * надобности, и заведение потока обходится дешевле, чем кажется. */
    render_args args;
    args.own = own;
    args.frame = frame;
    args.buffer = buffer;
    args.width = width;
    args.height = height;
    args.result = 0;

    HANDLE worker = CreateThread(NULL, 8 * 1024 * 1024, render_thread, &args,
                                 STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);

    if (worker == NULL) return 0;

    WaitForSingleObjectEx(worker, INFINITE, FALSE);
    CloseHandle(worker);

    return args.result;
}

/* Код последнего сбоя: 0xC0000005 — обращение по неверному адресу,
 * 0xC00000FD — переполнение стека. */
__declspec(dllexport) unsigned long __cdecl lovit_lottie_fault(void)
{
    return g_fault;
}

__declspec(dllexport) void __cdecl lovit_lottie_close(void *handle)
{
    lovit_lottie *own = (lovit_lottie *)handle;
    if (own != nullptr) delete own;
}

}
