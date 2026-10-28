using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Скачанные вложения на диске.
    ///
    /// Проигрывать прямо по ссылке нельзя: у CDN мессенджера свой
    /// удостоверяющий центр, которого в хранилище Windows Phone 8.1 нет, и
    /// системный проигрыватель до него не доходит — как не доходил и
    /// системный HttpClient. Поэтому голосовое, музыка и видео сперва
    /// скачиваются своей криптографией, а играются уже с диска.
    ///
    /// Заодно это и запас: повторное нажатие на то же голосовое ничего не
    /// качает. Ссылки от сервера временные, поэтому именем служит не адрес,
    /// а номер вложения — он постоянный.
    /// </summary>
    public static class MediaStore
    {
        /// <summary>Больше этого на диске не держим — чистим самое старое.</summary>
        private const long Budget = 60 * 1024 * 1024;

        private const string FolderName = "media";

        /// <summary>Одновременно качаем одно: телефону хватает и этого.</summary>
        private static readonly System.Threading.SemaphoreSlim _gate
            = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>
        /// Крупное вложение — видео. Пишется прямо на диск, минуя память:
        /// собрать ролик целиком в памяти телефон не даст.
        /// </summary>
        public static async Task<StorageFile> FetchLargeAsync(string url, string name,
                                                              MediaHttp.Progress progress)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) return null;

            name = Clean(name);

            // По одной закачке за раз: нажатий по одному и тому же вложению
            // бывает много подряд, и без этого телефон качал бы его во
            // столько же потоков.
            await _gate.WaitAsync();
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

                StorageFile ready = await FindAsync(folder, name);
                if (ready != null)
                {
                    if (progress != null) progress(1, 1);
                    return ready;
                }

                StorageFile partial = await folder.CreateFileAsync(
                    name + ".part", CreationCollisionOption.ReplaceExisting);

                bool done;

                using (Windows.Storage.Streams.IRandomAccessStream file =
                           await partial.OpenAsync(FileAccessMode.ReadWrite))
                using (Windows.Storage.Streams.IOutputStream sink = file.GetOutputStreamAt(0))
                {
                    done = await MediaHttp.GetToStreamAsync(url, sink, progress);
                }

                if (!done)
                {
                    try { await partial.DeleteAsync(); } catch (Exception) { }
                    return null;
                }

                await partial.RenameAsync(name, NameCollisionOption.ReplaceExisting);

                Log.Write("mem", "видео скачано: " + name);

                await PruneAsync(folder);
                return partial;
            }
            catch (Exception error)
            {
                Log.Write("mem", "видео не сохранить: " + error.Message);
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// То же для звука и видео, но имя подбирается по содержимому.
        ///
        /// Расширение здесь решает всё: по нему система выбирает, каким
        /// разбирателем взяться за файл, а угадать его неоткуда — в ссылке
        /// его обычно нет. Ошибёшься — получишь отказ «формат не
        /// поддерживается» даже на том, что телефон умеет.
        /// </summary>
        public static async Task<StorageFile> FetchMediaAsync(string url, string key,
                                                              string fallback,
                                                              MediaHttp.Progress progress)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return null;

            key = Clean(key);

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

                // Ищем по началу имени: расширение стало известно только после
                // прошлой закачки, и заранее его не назвать.
                foreach (StorageFile kept in await folder.GetFilesAsync())
                {
                    if (!kept.Name.StartsWith(key + ".", StringComparison.Ordinal)) continue;

                    Windows.Storage.FileProperties.BasicProperties about =
                        await kept.GetBasicPropertiesAsync();

                    if (about.Size == 0) break;

                    if (progress != null) progress(1, 1);
                    return kept;
                }
            }
            catch (Exception) { }

            byte[] data = await MediaHttp.GetAsync(url, progress);
            if (data == null || data.Length == 0)
            {
                Log.Write("mem", "вложение не скачалось: " + key);
                return null;
            }

            string extension = Sniff(data, fallback);

            Log.Write("mem", "вложение " + key + ": " + data.Length + " байт, "
                             + Head(data) + " → " + extension);

            return await SaveAsync(key + extension, data);
        }

        /// <summary>
        /// Что это на самом деле — видно по первым байтам. Название файла и
        /// заголовки CDN тут не помощники: у голосовых MAX ни того, ни
        /// другого толком нет.
        /// </summary>
        private static string Sniff(byte[] data, string fallback)
        {
            if (data.Length < 12) return fallback;

            if (Match(data, 0, "OggS")) return ".ogg";
            if (Match(data, 0, "ID3")) return ".mp3";
            if (Match(data, 0, "RIFF")) return ".wav";
            if (Match(data, 0, "fLaC")) return ".flac";
            if (Match(data, 0, "#!AMR")) return ".amr";

            // Кадр MPEG: одиннадцать единиц подряд в начале.
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return ".mp3";

            if (Match(data, 4, "ftyp"))
            {
                string brand = Ascii(data, 8, 4);

                if (brand.StartsWith("M4A", StringComparison.Ordinal)) return ".m4a";
                if (brand.StartsWith("qt", StringComparison.Ordinal)) return ".mov";

                return ".mp4";
            }

            return fallback;
        }

        private static bool Match(byte[] data, int at, string mark)
        {
            if (at + mark.Length > data.Length) return false;

            for (int i = 0; i < mark.Length; i++)
            {
                if (data[at + i] != (byte)mark[i]) return false;
            }
            return true;
        }

        private static string Ascii(byte[] data, int at, int length)
        {
            var text = new System.Text.StringBuilder(length);

            for (int i = 0; i < length && at + i < data.Length; i++)
            {
                byte value = data[at + i];
                text.Append(value >= 32 && value < 127 ? (char)value : '.');
            }

            return text.ToString();
        }

        /// <summary>Первые байты числами — по ним и опознаётся формат.</summary>
        private static string Head(byte[] data)
        {
            var text = new System.Text.StringBuilder();

            for (int i = 0; i < 12 && i < data.Length; i++)
            {
                text.Append(data[i].ToString("X2")).Append(' ');
            }

            return text.ToString().Trim() + " «" + Ascii(data, 0, 12) + "»";
        }

        private static async Task<StorageFile> SaveAsync(string name, byte[] data)
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

                StorageFile partial = await folder.CreateFileAsync(
                    name + ".part", CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(partial, data);
                await partial.RenameAsync(name, NameCollisionOption.ReplaceExisting);

                await PruneAsync(folder);
                return partial;
            }
            catch (Exception error)
            {
                Log.Write("mem", "вложение не сохранить: " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Отдаёт вложение файлом на диске, скачивая при надобности.
        /// Возвращает null, если не вышло.
        ///
        /// Идёт той же дорогой, что и видео, — прямо на диск. В памяти
        /// собирать нечего: присланным файлом бывает и снятый на телефон
        /// ролик, а он легко весит полсотни мегабайт.
        /// </summary>
        public static Task<StorageFile> FetchAsync(string url, string name,
                                                   MediaHttp.Progress progress)
        {
            return FetchLargeAsync(url, name, progress);
        }

        /// <summary>
        /// Имя приходит от собеседника, и в нём бывает что угодно — вплоть до
        /// косых черт, которыми файл увело бы в чужую папку.
        /// </summary>
        private static string Clean(string name)
        {
            var safe = new System.Text.StringBuilder(name.Length);

            foreach (char symbol in name)
            {
                bool bad = symbol < ' '
                           || symbol == '\\' || symbol == '/' || symbol == ':'
                           || symbol == '*' || symbol == '?' || symbol == '"'
                           || symbol == '<' || symbol == '>' || symbol == '|';

                safe.Append(bad ? '_' : symbol);
            }

            string result = safe.ToString().Trim();

            // Слишком длинное имя система не примёт: оставляем хвост с
            // расширением, начало не так важно.
            if (result.Length > 96) result = result.Substring(result.Length - 96);

            return result.Length == 0 ? "attachment" : result;
        }

        private static async Task<StorageFile> FindAsync(StorageFolder folder, string name)
        {
            try
            {
                var found = await folder.GetFileAsync(name) as StorageFile;
                if (found == null) return null;

                Windows.Storage.FileProperties.BasicProperties about =
                    await found.GetBasicPropertiesAsync();

                // Пустой файл — след прошлого обрыва, качаем заново.
                if (about.Size == 0) return null;

                return found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Чистка по объёму: самое давнее уходит первым. Считается по времени
        /// изменения — им же отмечается и последнее обращение, потому что
        /// скачивание и есть обращение.
        /// </summary>
        private static async Task PruneAsync(StorageFolder folder)
        {
            try
            {
                IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();

                long total = 0;
                var sized = new List<KeyValuePair<StorageFile, ulong>>();

                foreach (StorageFile file in files)
                {
                    Windows.Storage.FileProperties.BasicProperties about =
                        await file.GetBasicPropertiesAsync();

                    total += (long)about.Size;
                    sized.Add(new KeyValuePair<StorageFile, ulong>(file, about.Size));
                }

                if (total <= Budget) return;

                sized.Sort((first, second) =>
                    first.Key.DateCreated.CompareTo(second.Key.DateCreated));

                foreach (KeyValuePair<StorageFile, ulong> pair in sized)
                {
                    if (total <= Budget) break;

                    try
                    {
                        await pair.Key.DeleteAsync();
                        total -= (long)pair.Value;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception error)
            {
                Log.Write("mem", "чистка скачанного не удалась: " + error.Message);
            }
        }
    }
}
