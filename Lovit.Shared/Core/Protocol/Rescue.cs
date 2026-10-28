using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace Lovit.Core.Protocol
{
    /// <summary>
    /// Складывает на телефон пакет, который не удалось разобрать.
    ///
    /// Понять, чем именно он плох, по журналу нельзя: в журнале только длина
    /// и слово об ошибке, а беда — в самих байтах. Поэтому непонятое
    /// кладётся туда, откуда его достанут проводом, и разбирается уже на
    /// столе, со всеми средствами под рукой.
    ///
    /// Место выбирается по тому же правилу, что и для непринятых кружков:
    /// какие библиотеки телефон отдаёт приложению, заранее не известно —
    /// обращение к закрытой само по себе и есть отказ, поэтому пробуем по
    /// очереди, а в конце остаётся своя папка.
    /// </summary>
    public static class Rescue
    {
        /// <summary>Больше одного за запуск не спасаем: беда всегда одна и та же.</summary>
        private static bool _saved;

        public static async Task KeepAsync(byte[] header, byte[] body, string why)
        {
            if (_saved) return;
            _saved = true;

            // Заголовок и тело — одним куском, как пришли из сети: разбирать
            // их потом будем тем же порядком.
            var whole = new byte[header.Length + body.Length];

            Buffer.BlockCopy(header, 0, whole, 0, header.Length);
            Buffer.BlockCopy(body, 0, whole, header.Length, body.Length);

            string name = "lovit-paket-" + ((header[4] << 8) | header[5])
                          + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";

            StorageFile kept = await WriteAsync(() => KnownFolders.DocumentsLibrary, name, whole)
                               ?? await WriteAsync(() => KnownFolders.VideosLibrary, name, whole)
                               ?? await WriteAsync(() => KnownFolders.CameraRoll, name, whole)
                               ?? await WriteAsync(() => ApplicationData.Current.LocalFolder,
                                                   name, whole);

            if (kept != null)
            {
                Log.Write("tls", "непонятый пакет сохранён (" + why + ") — " + kept.Path);
            }
            else
            {
                Log.Write("tls", "непонятый пакет сохранить некуда");
            }
        }

        private static async Task<StorageFile> WriteAsync(Func<StorageFolder> where, string name,
                                                          byte[] data)
        {
            try
            {
                StorageFolder folder = where();

                StorageFile file = await folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, data);
                return file;
            }
            catch (Exception)
            {
                // Папка закрыта этому приложению — пробуем следующую.
                return null;
            }
        }
    }
}
