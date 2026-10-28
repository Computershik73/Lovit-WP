using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Pickers;
using Lovit.Core;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Выбор файла — настольный.
    ///
    /// Единственный файл приложения, переписанный здесь заново, и вот
    /// почему. На Windows Phone 8.1 выбрать файл и тут же получить его
    /// нельзя: система уводит приложение с экрана целиком, а выбранное
    /// возвращает новой активацией. Весь тамошний код — про то, как сшить
    /// это ожидание обратно, плюс свой обход папок на случай, когда
    /// системный выбор до файлов не доходит.
    ///
    /// На настольной Windows ничего этого нет: выбор — обычное окно поверх
    /// своего, приложение никуда не девается, файл приходит прямо в ответе.
    /// Здесь поэтому пусто и просто.
    ///
    /// Снаружи всё выглядит так же, как на телефоне: те же вызовы с теми же
    /// именами, — поэтому остальной код подключается ссылками и не знает,
    /// на чём он работает.
    /// </summary>
    public static class FilePicking
    {
        /// <summary>Картинки: только то, что мы умеем и показать, и залить.</summary>
        public static async Task<StorageFile> PickPhotoAsync()
        {
            return await PickAsync(PickerLocationId.PicturesLibrary, PickerViewMode.Thumbnail,
                                   new[] { ".jpg", ".jpeg", ".png", ".gif" });
        }

        /// <summary>Любой файл — своего обхода папок здесь не нужно.</summary>
        public static async Task<StorageFile> PickFileAsync()
        {
            return await PickAsync(PickerLocationId.DocumentsLibrary, PickerViewMode.List,
                                   new[] { "*" });
        }

        private static async Task<StorageFile> PickAsync(PickerLocationId start,
                                                         PickerViewMode view,
                                                         string[] extensions)
        {
            var picker = new FileOpenPicker
            {
                ViewMode = view,
                SuggestedStartLocation = start
            };

            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);

            try
            {
                return await picker.PickSingleFileAsync();
            }
            catch (Exception error)
            {
                Log.Write("app", "выбор файла не открылся: " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Отдать текст наружу — туда, куда укажет пользователь.
        /// </summary>
        public static async Task<bool> SaveAsync(string suggestedName, string text)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName
            };

            picker.FileTypeChoices.Add("Текст", new List<string> { ".txt" });

            try
            {
                StorageFile file = await picker.PickSaveFileAsync();
                if (file == null) return false;

                await FileIO.WriteTextAsync(file, text ?? "");
                return true;
            }
            catch (Exception error)
            {
                Log.Write("app", "не записать выбранное: " + error.Message);
                return false;
            }
        }

        /// <summary>Содержимое файла целиком — его и заливаем.</summary>
        public static async Task<byte[]> ReadAsync(StorageFile file)
        {
            if (file == null) return null;

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(await FileIO.ReadBufferAsync(file), out bytes);

            return bytes;
        }

        /// <summary>
        /// Возвращения из выбора здесь не бывает — приложение никуда не
        /// уходило. Но общий код зовёт это при запуске по любой активации,
        /// и пусть зовёт: делать тут просто нечего.
        /// </summary>
        public static void Complete(FileOpenPickerContinuationEventArgs arguments)
        {
        }

        public static void Complete(FileSavePickerContinuationEventArgs arguments)
        {
        }
    }
}
