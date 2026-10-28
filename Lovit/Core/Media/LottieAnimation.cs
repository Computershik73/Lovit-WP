using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Движущийся стикер.
    ///
    /// Lottie — не картинка, а описание векторной сцены во времени. Кадры
    /// рисует rlottie (та же библиотека, что и в Telegram), собранная под
    /// наш телефон; здесь она заворачивается в обычную картинку, которую
    /// понимает разметка.
    ///
    /// Кадры готовятся по одному, по требованию: держать их все разом
    /// негде. Один кадр 128×128 — это 64 КБ, и он всё время один и тот же:
    /// поверхность не пересоздаётся, а перерисовывается.
    /// </summary>
    public sealed class LottieAnimation : IDisposable
    {
        private const string Library = "LovitLottie.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_lottie_open(byte[] json, string key);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_lottie_frames(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_lottie_rate(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_lottie_render(IntPtr handle, int frame,
                                                      byte[] buffer, int width, int height);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_lottie_close(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint lovit_lottie_fault();

        private IntPtr _handle;
        private readonly int _side;
        private readonly byte[] _buffer;
        private readonly WriteableBitmap _bitmap;

        private int _frame;
        private bool _drawn;
        private bool _drawing;

        /// <summary>Когда пошло движение — от неё считается нужный кадр.</summary>
        private DateTime _startedAt = DateTime.MinValue;

        /// <summary>
        /// Видна ли строка со стикером сейчас. Невидимые не рисуются: их
        /// движение всё равно никто не увидит, а время и батарею оно тратит.
        /// </summary>
        public bool Shown { get; set; }

        public int Frames { get; private set; }

        /// <summary>Сколько миллисекунд держится один кадр.</summary>
        public int FrameMs { get; private set; }

        public ImageSource Picture { get { return _bitmap; } }

        private LottieAnimation(IntPtr handle, int side)
        {
            _handle = handle;
            _side = side;

            _buffer = new byte[side * side * 4];
            _bitmap = new WriteableBitmap(side, side);

            Frames = lovit_lottie_frames(handle);

            // Частота приходит умноженной на сто, чтобы не терять дробную
            // часть на целых числах.
            int rate = lovit_lottie_rate(handle);
            FrameMs = rate > 0 ? (int)(100000.0 / rate) : 40;

            // Нижнего предела здесь быть не должно. Раньше стоял в 25 мс, и
            // стикер, снятый на шестидесяти кадрах, шёл на две трети своей
            // скорости — просто потому, что мы отказывались показывать его
            // быстрее сорока.
            if (FrameMs < 1) FrameMs = 1;
        }

        /// <summary>
        /// Скачать описание и открыть. Возвращает null, если не вышло, —
        /// тогда стикер остаётся неподвижной картинкой.
        /// </summary>
        public static async Task<LottieAnimation> OpenAsync(string url, string key, int side)
        {
            if (string.IsNullOrEmpty(url)) return null;

            try
            {
                byte[] data = await MediaHttp.GetAsync(url);
                if (data == null || data.Length == 0) return null;

                // MAX присылает описания сжатыми — всегда, а не изредка.
                // Узнаём по первым двум байтам и распаковываем.
                if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
                {
                    data = Inflate(data);
                    if (data == null) return null;
                }

                // Библиотека ждёт строку, оканчивающуюся нулём.
                var text = new byte[data.Length + 1];
                Buffer.BlockCopy(data, 0, text, 0, data.Length);

                // Записи вокруг каждого нативного шага. Падение внутри
                // нативного кода уносит приложение мгновенно и ничего после
                // себя не оставляет — узнать место можно только по тому,
                // какая запись успела лечь последней.
                Log.Write("chat", "стикер: разбираем описание, " + data.Length + " знаков");

                IntPtr handle = lovit_lottie_open(text, key ?? "");
                if (handle == IntPtr.Zero)
                {
                    Log.Write("chat", "стикер: описание не разобралось");
                    return null;
                }

                Log.Write("chat", "стикер: описание разобрано");

                var made = new LottieAnimation(handle, side);

                Log.Write("chat", "стикер: кадров " + made.Frames
                                  + ", по " + made.FrameMs + " мс");

                // Пробный кадр: если сцена почему-то не рисуется, лучше
                // выяснить это сразу и оставить стикер картинкой, чем
                // заводить отсчёт кадров впустую.
                var probe = new byte[8 * 8 * 4];

                if (lovit_lottie_render(handle, 0, probe, 8, 8) == 0)
                {
                    // Приложение при этом цело: сбой перехвачен в нативной
                    // части и превращён в отказ.
                    Log.Write("chat", "стикер: отрисовка сорвалась, код сбоя 0x"
                                      + lovit_lottie_fault().ToString("X8"));

                    made.Dispose();
                    return null;
                }

                return made;
            }
            catch (Exception error)
            {
                Log.Write("chat", "стикер не открыть: " + error.Message);
                return null;
            }
        }

        /// <summary>Распаковать сжатое описание. Null, если не вышло.</summary>
        private static byte[] Inflate(byte[] packed)
        {
            try
            {
                using (var source = new System.IO.MemoryStream(packed))
                using (var gzip = new System.IO.Compression.GZipStream(
                           source, System.IO.Compression.CompressionMode.Decompress))
                using (var target = new System.IO.MemoryStream())
                {
                    var chunk = new byte[16 * 1024];

                    while (true)
                    {
                        int got = gzip.Read(chunk, 0, chunk.Length);
                        if (got <= 0) break;

                        target.Write(chunk, 0, got);

                        // Описание сцены на телефоне не бывает огромным;
                        // если оно таково — это не стикер.
                        if (target.Length > 4 * 1024 * 1024) return null;
                    }

                    return target.ToArray();
                }
            }
            catch (Exception error)
            {
                Log.Write("chat", "стикер: описание не распаковалось — " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Нарисовать следующий кадр. Дойдя до конца, начинает сначала —
        /// стикеры кружатся без остановки.
        /// </summary>
        public bool Advance()
        {
            if (_handle == IntPtr.Zero || Frames <= 0) return false;

            // Повторный вход недопустим.
            //
            // Отрисовщик к нему не готов, а войти повторно легко: ожидание с
            // блокировкой в потоке разметки прокачивает очередь сообщений, и
            // отсчёт кадров успевает вызвать нас поверх незавершённой
            // отрисовки. В журнале это было видно как два «рисуем первый
            // кадр» подряд, а кончалось обращением по неверному адресу.
            if (_drawing) return true;
            _drawing = true;

            try
            {
                if (!_drawn) Log.Write("chat", "стикер: рисуем первый кадр");

                // Какой кадр показывать — считаем по настоящему времени, а не
                // «следующий за предыдущим».
                //
                // Отрисовка вектора на этом телефоне не всегда поспевает за
                // частотой стикера, и при простом переходе к следующему кадру
                // движение идёт замедленно ровно во столько раз, во сколько мы
                // не успеваем. По времени же неуспевание превращается в
                // пропуск кадров, а темп остаётся верным.
                if (_startedAt == DateTime.MinValue) _startedAt = DateTime.UtcNow;

                double passed = (DateTime.UtcNow - _startedAt).TotalMilliseconds;

                _frame = (int)(passed / FrameMs);
                if (Frames > 0) _frame %= Frames;

                if (lovit_lottie_render(_handle, _frame, _buffer, _side, _side) == 0)
                {
                    Log.Write("chat", "стикер: отрисовка сорвалась, код сбоя 0x"
                                      + lovit_lottie_fault().ToString("X8"));
                    return false;
                }
            }
            finally
            {
                _drawing = false;
            }

            if (!_drawn)
            {
                _drawn = true;
                Log.Write("chat", "стикер: первый кадр нарисован");
            }

            try
            {
                using (System.IO.Stream target = _bitmap.PixelBuffer.AsStream())
                {
                    target.Seek(0, System.IO.SeekOrigin.Begin);
                    target.Write(_buffer, 0, _buffer.Length);
                }

                _bitmap.Invalidate();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;

            lovit_lottie_close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
