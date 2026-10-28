using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Прослойка между проигрывателем и сетью: снаружи это обычный файл, а
    /// внутри — докачка кусками по мере надобности.
    ///
    /// Зачем. Отдать проигрывателю ссылку нельзя: до CDN он не доходит своей
    /// проверкой сертификатов. Качать ролик целиком перед первым кадром —
    /// долго, а на телефоне ещё и негде. Выход между этими двумя — притвориться
    /// файлом: проигрыватель сам просит те отрезки, которые ему нужны, мы их
    /// берём своей криптографией, а разбор MP4 остаётся на нём.
    ///
    /// Так же решается и то, ради чего обычно берут DASH: воспроизведение без
    /// полной закачки и перемотка. Настоящий DASH здесь не сделать — в наборе
    /// Windows Phone 8.1 нет ничего, что его играет (AdaptiveMediaSource
    /// появился только в Windows 10), и пришлось бы самим разбирать манифест,
    /// отдельные дорожки и фрагменты на кадры. Здесь же всё это делает сам
    /// проигрыватель, потому что видит перед собой простой MP4.
    ///
    /// Держится на том, что CDN отвечает на запросы диапазонов: без
    /// `Accept-Ranges` затея не работает вовсе.
    /// </summary>
    public sealed class RemoteStream : IRandomAccessStream
    {
        /// <summary>
        /// Сколько берём за один раз. Каждый кусок — это новое соединение с
        /// рукопожатием, поэтому мелкие куски дороги; слишком крупные же
        /// задерживают начало показа.
        /// </summary>
        private const int Block = 512 * 1024;

        private readonly string _url;
        private ulong _size;
        private ulong _position;

        /// <summary>Последний взятый кусок: подряд идущие чтения бьют в него.</summary>
        private byte[] _window;
        private ulong _windowAt;

        private RemoteStream(string url, ulong size)
        {
            _url = url;
            _size = size;
        }

        /// <summary>
        /// Открыть. Первым же запросом узнаётся длина файла — без неё
        /// проигрыватель не возьмётся за дело.
        /// </summary>
        public static async Task<RemoteStream> OpenAsync(string url)
        {
            MediaHttp.Piece head = await MediaHttp.GetRangeAsync(url, 0, Block);
            if (head == null || head.Total <= 0) return null;

            Log.Write("mem", "поток открыт: " + head.Total + " байт");

            var stream = new RemoteStream(url, (ulong)head.Total);

            // Начало уже в руках — с него проигрыватель и начнёт.
            stream._window = head.Data;
            stream._windowAt = 0;

            return stream;
        }

        #region Свойства потока

        public bool CanRead { get { return true; } }

        public bool CanWrite { get { return false; } }

        public ulong Position { get { return _position; } }

        public ulong Size
        {
            get { return _size; }
            set { _size = value; }
        }

        public void Seek(ulong position)
        {
            _position = position;
        }

        /// <summary>
        /// Проигрыватель вправе читать из нескольких мест сразу. Запас кусков
        /// у каждого свой, а вот длина и адрес общие.
        /// </summary>
        public IRandomAccessStream CloneStream()
        {
            return new RemoteStream(_url, _size) { _position = _position };
        }

        public IInputStream GetInputStreamAt(ulong position)
        {
            IRandomAccessStream copy = CloneStream();
            copy.Seek(position);
            return copy;
        }

        public IOutputStream GetOutputStreamAt(ulong position)
        {
            throw new NotSupportedException("Поток только для чтения.");
        }

        public void Dispose()
        {
            _window = null;
        }

        #endregion

        #region Чтение

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(
            IBuffer buffer, uint count, InputStreamOptions options)
        {
            return AsyncInfo.Run<IBuffer, uint>(async (token, report) =>
            {
                byte[] data = await PullAsync(_position, count);
                if (data == null) return new byte[0].AsBuffer();

                _position += (ulong)data.Length;

                return data.AsBuffer();
            });
        }

        /// <summary>
        /// Отдать отрезок, при надобности сходив за ним в сеть. Запрошенное
        /// за концом файла обрезается: читать дальше нечего.
        /// </summary>
        private async Task<byte[]> PullAsync(ulong at, uint count)
        {
            if (at >= _size) return new byte[0];

            ulong left = _size - at;
            if (count > left) count = (uint)left;
            if (count == 0) return new byte[0];

            // Уже в запасе — в сеть не ходим.
            byte[] ready = FromWindow(at, count);
            if (ready != null) return ready;

            // Берём с запасом вперёд: дальше почти наверняка попросят
            // продолжение, и лишнее соединение не понадобится.
            long size = Math.Max(count, (uint)Block);
            if (at + (ulong)size > _size) size = (long)(_size - at);

            MediaHttp.Piece piece = await MediaHttp.GetRangeAsync(_url, (long)at, size);
            if (piece == null || piece.Data == null || piece.Data.Length == 0)
            {
                Log.Write("mem", "поток: не дали кусок с " + at);
                return null;
            }

            _window = piece.Data;
            _windowAt = at;

            return FromWindow(at, count) ?? piece.Data;
        }

        private byte[] FromWindow(ulong at, uint count)
        {
            if (_window == null) return null;

            ulong end = _windowAt + (ulong)_window.Length;
            if (at < _windowAt || at >= end) return null;

            int offset = (int)(at - _windowAt);
            int have = _window.Length - offset;
            int give = (int)Math.Min(count, (uint)have);

            var slice = new byte[give];
            System.Buffer.BlockCopy(_window, offset, slice, 0, give);

            return slice;
        }

        #endregion

        #region Запись — её нет

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer)
        {
            throw new NotSupportedException("Поток только для чтения.");
        }

        public IAsyncOperation<bool> FlushAsync()
        {
            throw new NotSupportedException("Поток только для чтения.");
        }

        #endregion
    }
}
