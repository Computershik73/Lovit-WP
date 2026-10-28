using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lovit.Core.Transport
{
    /// <summary>
    /// Своя криптография: OpenSSL в LovitTls.dll.
    ///
    /// Системный `StreamSocket` умеет TLS 1.2, но разговаривает так, как
    /// решили в 2014 году: свой список шифров, свой набор кривых и хранилище
    /// корней, которое пополнить нечем — система снята с поддержки. Сервер
    /// MAX выдан НУЦ Минцифры, и этого корня на телефоне нет, а из шифров
    /// той поры сервер принимает считанные единицы. То же самое и по той же
    /// причине сделано в версии для iOS 6.
    ///
    /// Вызовы библиотеки блокирующие, поэтому каждый уезжает в рабочий поток:
    /// на потоке разметки нельзя ждать даже миллисекунду.
    /// </summary>
    public class NativeTlsStream : ITlsStream
    {
        private const string Library = "LovitTls.dll";

        /// <summary>
        /// Шаг чтения. Это не задержка получения: столько ждут, когда данных
        /// нет вовсе, — пришедшее забирается сразу. Ждать бесконечно нельзя,
        /// иначе закрытие соединения не заметят до самого конца ожидания.
        ///
        /// Шаг заодно ограничивает и ожидание отправки: пока идёт чтение,
        /// запись ждёт своей очереди (см. [_gate]), и дольше одного шага
        /// это ожидание не длится. Секунда для этого была слишком долгой —
        /// отправленное сообщение заметно подвисало.
        /// </summary>
        private const int ReadStepMs = 250;

        private const int ConnectTimeoutMs = 15000;

        private IntPtr _handle;
        private string _description = "OpenSSL";
        private string _error = "";

        /// <summary>
        /// Очередь к соединению: одновременно с ним работает только кто-то
        /// один.
        ///
        /// OpenSSL не позволяет трогать одно соединение из двух потоков
        /// сразу, а именно это здесь и выходило: чтение почти всё время
        /// сидит внутри своего вызова в рабочем потоке, и каждая отправка
        /// приходилась ровно на это окно. Виду сломанного так не подать —
        /// сокет цел, отправка проходит, — но записи TLS перемешиваются, и
        /// сервер перестаёт отвечать. Со стороны приложения это выглядит
        /// как «чат открылся пустым», причём насовсем.
        ///
        /// Тем же замком закрыто и закрытие: освобождать соединение, пока
        /// в нём читают, — обращение к уже освобождённой памяти, а такое на
        /// телефоне заканчивается молчаливой смертью всего приложения.
        /// </summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        #region Библиотека

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_tls_prepare();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_tls_open(
            [MarshalAs(UnmanagedType.LPStr)] string host, int port, int timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_tls_read(IntPtr handle, byte[] buffer, int length, int timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_tls_write(IntPtr handle, byte[] buffer, int length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_tls_close(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_tls_error(IntPtr handle, byte[] buffer, int length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_tls_session(IntPtr handle, byte[] buffer, int length);

        #endregion

        /// <summary>
        /// Есть ли своя криптография в этой сборке. Библиотека собрана только
        /// под ARM: под эмулятор её нет, и там остаётся системный стек.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (_available.HasValue) return _available.Value;

                try
                {
                    _available = lovit_tls_prepare() > 0;
                }
                catch (Exception)
                {
                    // Библиотеки в пакете нет или она не загрузилась —
                    // остаётся системный стек. Отдельного исключения на этот
                    // случай в наборе Windows Phone нет, отсюда общий перехват.
                    _available = false;
                }

                return _available.Value;
            }
        }

        private static bool? _available;

        public string Description { get { return _description; } }

        public string Error { get { return _error; } }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            IntPtr handle = await Task.Run(() => lovit_tls_open(host, port, ConnectTimeoutMs));

            if (handle == IntPtr.Zero)
            {
                _error = ReadText(IntPtr.Zero, lovit_tls_error);
                if (string.IsNullOrEmpty(_error)) _error = "соединение не открылось";
                return false;
            }

            _handle = handle;
            _description = "OpenSSL " + ReadText(handle, lovit_tls_session);
            return true;
        }

        public async Task<int> ReadAsync(byte[] buffer, int length)
        {
            if (_handle == IntPtr.Zero) return 0;

            await _gate.WaitAsync();
            try
            {
                // Ещё раз, уже под замком: пока стояли в очереди, соединение
                // могли закрыть, и старый handle указывал бы в никуда.
                IntPtr handle = _handle;
                if (handle == IntPtr.Zero) return 0;

                int outcome = await Task.Run(() => lovit_tls_read(handle, buffer, length, ReadStepMs));

                if (outcome == -1) _error = ReadText(handle, lovit_tls_error);
                return outcome;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> WriteAsync(byte[] data)
        {
            if (_handle == IntPtr.Zero) return false;

            await _gate.WaitAsync();
            try
            {
                IntPtr handle = _handle;
                if (handle == IntPtr.Zero) return false;

                int outcome = await Task.Run(() => lovit_tls_write(handle, data, data.Length));

                if (outcome == data.Length) return true;

                _error = ReadText(handle, lovit_tls_error);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Закрытие не ждёт на месте: звать его могут из потока разметки, а
        /// впереди в очереди стоит чтение со своим шагом. Само освобождение
        /// уходит в очередь и случится, когда никто больше не читает.
        /// </summary>
        public void Close()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle == IntPtr.Zero) return;

            var ignored = CloseAsync(handle);
        }

        private async Task CloseAsync(IntPtr handle)
        {
            await _gate.WaitAsync();
            try
            {
                lovit_tls_close(handle);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Строки из библиотеки приходят в UTF-8 — не в кодировке системы:
        /// в них русский текст, и на телефоне с другой раскладкой он иначе
        /// превратился бы в кашу.
        /// </summary>
        private static string ReadText(IntPtr handle, Func<IntPtr, byte[], int, int> reader)
        {
            var buffer = new byte[256];
            int length = reader(handle, buffer, buffer.Length);

            if (length <= 0) return "";
            return Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length));
        }
    }
}
