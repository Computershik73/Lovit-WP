using System;
using System.Collections.Generic;
using Lovit.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Картинки: загрузка, разбор и кэш. Порт MAXImageLoader.
    ///
    /// Кэш двухслойный. В памяти — готовые картинки, чтобы прокрутка списка
    /// не перерисовывала одно и то же; на диске — сами байты, чтобы после
    /// перезапуска не качать заново. Аватарки меняются редко, а трафик на
    /// телефоне десятилетней давности обычно считают.
    /// </summary>
    public static class ImageCache
    {
        /// <summary>
        /// Сколько памяти отдать картинкам — в байтах, а не в штуках.
        ///
        /// Считать штуками было ошибкой. Предел в сто двадцать картинок
        /// писался под аватарки: они маленькие, их сотни, и сто двадцать
        /// таких — пара мегабайт. Но в тот же кэш ложатся фотографии из
        /// переписки, а снимок 1080×1920 после разбора занимает восемь
        /// мегабайт: полтора десятка их — уже больше сотни мегабайт, и это
        /// весь предел, который телефон отводит приложению. Список чатов
        /// при этом ведёт себя смирно, а переписка с фотографиями убивает
        /// приложение — причём тем вернее, чем дольше её листают.
        ///
        /// В байтах разница пропадает: аватарок по-прежнему помещаются
        /// сотни, а фотографии вытесняют друг друга, не трогая остальных.
        /// Качество не страдает — картинки хранятся как есть, просто реже.
        /// </summary>
        private const long MemoryBudget = 24 * 1024 * 1024;

        /// <summary>
        /// Во что обходится картинка, о размере которой сказать нечего.
        ///
        /// Четыре мегабайта, а не считанные сотни килобайт, и это нарочно.
        /// Прежняя оценка была вчетверо меньше правды, и с ней кэш считал,
        /// что держит двадцать три мегабайта, тогда как на деле в нём лежали
        /// сотни: неизвестные размером картинки копились, ничего не вытесняя,
        /// потому что по счёту почти ничего не весили. Ошибаться тут надо в
        /// сторону лишнего вытеснения — пересчитать картинку дешевле, чем
        /// быть закрытым за память.
        /// </summary>
        private const long UnknownCost = 4 * 1024 * 1024;

        private class Entry
        {
            public ImageSource Image { get; set; }
            public long Bytes { get; set; }
        }

        private static readonly Dictionary<string, Entry> _memory =
            new Dictionary<string, Entry>();

        /// <summary>Порядок обращения: в конце то, что трогали последним.</summary>
        private static readonly List<string> _order = new List<string>();

        private static long _bytes;

        /// <summary>
        /// Кэш трогают из двух мест: разметка при показе картинок и система,
        /// когда сообщает о нехватке памяти, — а сообщает она это с чужого
        /// потока. Без замка два таких обращения рвали бы словарь.
        /// </summary>
        private static readonly object _lock = new object();

        /// <summary>Очередь на разбор: разом разбирается только одна картинка.</summary>
        private static readonly System.Threading.SemaphoreSlim _decoding =
            new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Что уже качается: два одинаковых запроса ни к чему.</summary>
        private static readonly Dictionary<string, Task<ImageSource>> _running =
            new Dictionary<string, Task<ImageSource>>();

        private static StorageFolder _folder;

        /// <summary>
        /// Насколько крупно разбирать. Больше показанного не берём: в
        /// списке чатов плитка в семьдесят две точки, и полторы тысячи
        /// точек с CDN в ней всё равно негде показать.
        /// </summary>
        public const int AvatarSide = 192;

        /// <summary>Фотография в пузыре — по ширине пузыря с запасом.</summary>
        public const int PhotoSide = 800;

        /// <summary>На весь экран, с оглядкой на увеличение щипком.</summary>
        public const int ScreenSide = 1600;

        public static ImageSource Cached(string url)
        {
            return Cached(url, 0);
        }

        public static ImageSource Cached(string url, int maxSide)
        {
            if (string.IsNullOrEmpty(url)) return null;

            string key = Key(url, maxSide);

            lock (_lock)
            {
                Entry found;
                if (!_memory.TryGetValue(key, out found)) return null;

                Touch(key);
                return found.Image;
            }
        }

        public static Task<ImageSource> LoadAsync(string url)
        {
            return LoadAsync(url, 0);
        }

        public static Task<ImageSource> LoadAsync(string url, int maxSide)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<ImageSource>(null);

            string key = Key(url, maxSide);

            lock (_lock)
            {
                Entry ready;
                if (_memory.TryGetValue(key, out ready))
                {
                    Touch(key);
                    return Task.FromResult(ready.Image);
                }

                Task<ImageSource> running;
                if (_running.TryGetValue(key, out running)) return running;

                Task<ImageSource> task = FetchAsync(url, key, maxSide);
                _running[key] = task;

                return task;
            }
        }

        /// <summary>
        /// Одна и та же картинка в разных размерах — разные записи: в списке
        /// нужна мелкая, на весь экран крупная, и подменять одну другой
        /// нельзя. Скачанное на диске при этом общее — там лежат байты как
        /// пришли.
        /// </summary>
        private static string Key(string url, int maxSide)
        {
            return maxSide > 0 ? url + "@" + maxSide : url;
        }

        /// <summary>
        /// Картинку показали — она уходит в конец очереди на вытеснение.
        ///
        /// Без этого вылетало то, что открыли раньше, а не то, к чему
        /// не возвращаются: аватарка из верхнего чата вытеснялась просто
        /// потому, что список открыли первым.
        /// </summary>
        private static void Touch(string url)
        {
            if (_order.Remove(url)) _order.Add(url);
        }

        private static async Task<ImageSource> FetchAsync(string url, string key, int maxSide)
        {
            try
            {
                byte[] data = await FromDiskAsync(url);

                if (data == null)
                {
                    data = await MediaHttp.GetAsync(url);
                    if (data != null && data.Length > 0) await ToDiskAsync(url, data);
                }

                if (data == null || data.Length == 0) return null;

                ImageSource image = await ToImageAsync(data, maxSide);
                if (image != null) Remember(key, image, maxSide);

                return image;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                lock (_lock) _running.Remove(key);
            }
        }

        /// <summary>
        /// Сначала свой разбор WebP, потом системный. Порядок именно такой:
        /// CDN мессенджера отдаёт WebP почти всегда, а системный декодер
        /// на нём просто выбросит исключение.
        /// </summary>
        /// <summary>
        /// Картинка из готовых байтов — кадр, вложенный прямо в сообщение.
        /// Своего адреса у него нет, поэтому и в запасе ему не место.
        /// </summary>
        public static Task<ImageSource> FromBytesAsync(byte[] data, int maxSide)
        {
            if (data == null || data.Length == 0) return Task.FromResult<ImageSource>(null);
            return ToImageAsync(data, maxSide);
        }

        private static async Task<ImageSource> ToImageAsync(byte[] data, int maxSide)
        {
            if (WebPImage.IsWebP(data))
            {
                // По одной картинке за раз. Разбор — единственное место, где
                // на короткое время появляется целая несжатая картинка, и
                // десяток таких разом (список чатов открывается сразу весь)
                // и есть тот всплеск, за который телефон закрывает
                // приложение.
                await _decoding.WaitAsync();

                // Отметки вокруг разбора: он нативный, и падение в нём уносит
                // приложение без следа. По тому, легла ли вторая запись,
                // видно, вышли мы обратно или умерли внутри.
                Log.Write("mem", "разбираем WebP, " + data.Length + " байт, до " + maxSide);

                WebPImage.Pixels pixels;
                try
                {
                    pixels = await Task.Run(() => WebPImage.Decode(data, maxSide));
                }
                finally
                {
                    _decoding.Release();
                }

                Log.Write("mem", "WebP разобран");

                return await WebPImage.ToBitmapAsync(pixels);
            }

            // Не WebP. Так приходит заметная часть аватарок — обычными JPEG
            // и PNG, — и до сих пор этот путь разворачивал их целиком: про
            // нужный размер он попросту не знал.
            //
            // В журнал это больше не пишется: строк выходило по сотне в
            // секунду, а запись идёт сразу на диск — сам журнал становился
            // заметной частью работы. Вопрос, ради которого строка
            // заводилась, уже отвечен: таких картинок много.
            var bitmap = new BitmapImage();

            if (maxSide > 0)
            {
                // Физические точки, а не условные: условные система умножит
                // на плотность экрана, и на телефоне с большой плотностью
                // предел вырос бы вдвое.
                bitmap.DecodePixelType = DecodePixelType.Physical;

                // Задаём одну сторону — вторая считается сама, соотношение
                // сохраняется.
                bitmap.DecodePixelWidth = maxSide;
            }

            using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);

                await bitmap.SetSourceAsync(stream);
            }

            return bitmap;
        }

        /// <summary>
        /// Размер спрашивается до замка нарочно: PixelWidth принадлежит
        /// разметке, и читать его можно только из её потока, а под замок
        /// приходит и система со своим.
        /// </summary>
        private static void Remember(string url, ImageSource image, int maxSide)
        {
            long cost = CostOf(image, maxSide);

            // Тяжёлые картинки — на виду. Порог высокий нарочно: после
            // уменьшения таких почти не бывает, и каждая такая строка
            // означает, что где-то размер не задан.
            if (cost > 4 * 1024 * 1024)
            {
                Log.Write("mem", "крупная картинка на " + (cost / (1024 * 1024))
                                 + " МБ, в кэше " + (_bytes / (1024 * 1024)) + " МБ");
            }

            lock (_lock)
            {
                Forget(url);

                _memory[url] = new Entry { Image = image, Bytes = cost };
                _order.Add(url);
                _bytes += cost;

                Trim(MemoryBudget);
            }
        }

        /// <summary>
        /// Во что обходится картинка. Разобранная — это четыре байта на
        /// точку, и никакой другой памяти за ней не стоит.
        ///
        /// У системной картинки PixelWidth показывает размер **исходника**,
        /// а не разобранного: мы просили разобрать до 192 точек, а она
        /// честно отвечает «полторы тысячи». Считать по этому числу нельзя —
        /// кэш верил, что забит под завязку двадцатью тремя мегабайтами, и
        /// без конца выбрасывал то, что весило доли от заявленного. Оттуда и
        /// вечная перезагрузка картинок, и работа процессора впустую.
        /// Поэтому размер ограничивается тем, что было заказано.
        /// </summary>
        private static long CostOf(ImageSource image, int maxSide)
        {
            var writeable = image as WriteableBitmap;
            if (writeable != null) return (long)writeable.PixelWidth * writeable.PixelHeight * 4;

            var bitmap = image as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0)
            {
                long width = bitmap.PixelWidth;
                long height = bitmap.PixelHeight;

                if (maxSide > 0 && width > maxSide)
                {
                    // Соотношение сохраняется: заказана была ширина.
                    height = height * maxSide / width;
                    width = maxSide;

                    if (height < 1) height = 1;
                }

                return width * height * 4;
            }

            return UnknownCost;
        }

        /// <summary>
        /// Вытеснение до нужного объёма, начиная с самого давнего.
        ///
        /// Последняя картинка остаётся всегда, даже если она одна больше
        /// всего предела: её прямо сейчас показывают, и выбросить её
        /// значило бы получить пустое место на экране.
        /// </summary>
        private static void Trim(long budget)
        {
            while (_bytes > budget && _order.Count > 1)
            {
                Forget(_order[0]);
            }
        }

        private static void Forget(string url)
        {
            Entry entry;
            if (!_memory.TryGetValue(url, out entry)) return;

            _memory.Remove(url);
            _order.Remove(url);
            _bytes -= entry.Bytes;

            if (_bytes < 0) _bytes = 0;
        }

        /// <summary>
        /// Забыть картинку одного размера.
        ///
        /// Нужно просмотрщику: снимок на весь экран весит столько же, сколько
        /// весь остальной кэш, и, оставаясь в нём после закрытия, вытесняет
        /// всё, что было в переписке. Со стороны это выглядит так, будто чат
        /// перезагружает картинки заново — он их и правда перезагружает.
        /// </summary>
        public static void Forget(string url, int maxSide)
        {
            if (string.IsNullOrEmpty(url)) return;
            lock (_lock) Forget(Key(url, maxSide));
        }

        /// <summary>
        /// Телефону стало тесно. Половина кэша уходит сразу: пересчитать
        /// картинку заново дешевле, чем быть закрытым системой, — а закрывают
        /// её именно за память.
        /// </summary>
        public static void TrimForMemoryPressure()
        {
            lock (_lock) Trim(_bytes / 2);
        }

        #region Диск

        private static async Task<StorageFolder> FolderAsync()
        {
            if (_folder != null) return _folder;

            _folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                "media", CreationCollisionOption.OpenIfExists);

            return _folder;
        }

        private static async Task<byte[]> FromDiskAsync(string url)
        {
            try
            {
                StorageFolder folder = await FolderAsync();
                StorageFile file = await folder.GetFileAsync(FileName(url));

                Windows.Storage.Streams.IBuffer buffer = await FileIO.ReadBufferAsync(file);
                return buffer.ToArray();
            }
            catch (Exception)
            {
                // Файла просто нет — обычное дело при первом показе.
                return null;
            }
        }

        private static async Task ToDiskAsync(string url, byte[] data)
        {
            try
            {
                StorageFolder folder = await FolderAsync();
                StorageFile file = await folder.CreateFileAsync(FileName(url),
                    CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, data);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Сколько скачанного держать на диске. Байты там лежат сжатыми,
        /// как их отдал CDN, и качества это не касается вовсе — только
        /// того, что придётся скачать заново.
        /// </summary>
        private const long DiskBudget = 40 * 1024 * 1024;

        private static bool _prunedOnce;

        /// <summary>
        /// Уборка на диске: папка `media` не чистилась никогда, и за месяцы
        /// переписки с фотографиями она росла безостановочно. Делается один
        /// раз за запуск и в стороне от всего — торопиться тут некуда.
        /// </summary>
        public static async void PruneDiskAsync()
        {
            if (_prunedOnce) return;
            _prunedOnce = true;

            try
            {
                StorageFolder folder = await FolderAsync();
                IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();

                var known = new List<KeyValuePair<DateTimeOffset, StorageFile>>();
                long total = 0;

                foreach (StorageFile file in files)
                {
                    Windows.Storage.FileProperties.BasicProperties properties =
                        await file.GetBasicPropertiesAsync();

                    total += (long)properties.Size;
                    known.Add(new KeyValuePair<DateTimeOffset, StorageFile>(
                        properties.DateModified, file));
                }

                if (total <= DiskBudget) return;

                // Сначала самое давнее: то, к чему не возвращались.
                known.Sort((first, second) => first.Key.CompareTo(second.Key));

                foreach (var pair in known)
                {
                    if (total <= DiskBudget) return;

                    Windows.Storage.FileProperties.BasicProperties properties =
                        await pair.Value.GetBasicPropertiesAsync();

                    await pair.Value.DeleteAsync();
                    total -= (long)properties.Size;
                }
            }
            catch (Exception)
            {
                // Уборка — не то, ради чего стоит падать.
            }
        }

        /// <summary>
        /// Имя файла из адреса: сам адрес в имя не годится — в нём косые
        /// черты и вопросительные знаки.
        /// </summary>
        private static string FileName(string url)
        {
            uint hash = 2166136261;

            foreach (char symbol in url)
            {
                hash = (hash ^ symbol) * 16777619;
            }

            return hash.ToString("x8") + "_" + url.Length;
        }

        #endregion
    }
}
