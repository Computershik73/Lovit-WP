using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Выбор файла с телефона.
    ///
    /// На Windows Phone 8.1 выбрать файл и тут же получить его нельзя:
    /// PickSingleFileAsync здесь не работает вовсе. Система показывает
    /// свой выбор поверх, приложение при этом уходит с экрана, а
    /// выбранное возвращается уже новой активацией — тем же способом,
    /// каким приложение открывают из плитки. Отсюда и ожидание,
    /// разорванное надвое: здесь оно снова сшито в одну задачу.
    ///
    /// Задача переживает уход с экрана, но не выгрузку из памяти: если
    /// телефону стало тесно и приложение закрыли, возвращаться уже некуда,
    /// и выбор пропадает. Сделать с этим ничего нельзя — так устроена сама
    /// система.
    /// </summary>
    public static class FilePicking
    {
        private static TaskCompletionSource<StorageFile> _pending;

        /// <summary>Картинки: только то, что мы умеем и показать, и залить.</summary>
        public static Task<StorageFile> PickPhotoAsync()
        {
            return PickAsync(PickerLocationId.PicturesLibrary, PickerViewMode.Thumbnail,
                             new[] { ".jpg", ".jpeg", ".png", ".gif" });
        }

        /// <summary>
        /// Что система умеет: да — «десятка», нет — 8.1, null — ещё не знаем.
        /// </summary>
        private static bool? _modern;

        /// <summary>
        /// Выбор файла на «десятке» — тем самым способом, которого на 8.1 нет.
        ///
        /// Windows 10 Mobile отдаёт файл прямо из PickSingleFileAsync и
        /// показывает настоящий проводник: всю память телефона, карту, папку
        /// «Загрузки», OneDrive. На 8.1 этот же вызов есть в наборе, но при
        /// обращении отказывает — на том и различаем, потому что спросить
        /// систему о её собственной версии из приложения 8.1 нечем: ни
        /// ApiInformation, ни AnalyticsInfo в этом наборе не существует.
        ///
        /// Отказ приходит сразу и без окон, так что запасной путь включается
        /// незаметно. Ответ запоминается: система под ногами не меняется.
        /// </summary>
        private static async Task<StorageFile> PickModernAsync()
        {
            if (_modern == false) return null;

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };

            picker.FileTypeFilter.Add("*");

            try
            {
                StorageFile chosen = await picker.PickSingleFileAsync();

                if (_modern == null) Log.Write("app", "выбор файла: способ «десятки» работает");
                _modern = true;

                return chosen;
            }
            catch (Exception error)
            {
                Log.Write("app", "выбор файла по-новому не вышел, остаётся свой обход: "
                                 + error.Message);
                _modern = false;
                return null;
            }
        }

        /// <summary>
        /// Файл. Сперва пробуем способ «десятки», а на 8.1 остаётся свой
        /// обход папок — системный выбор там до файлов не доходит вовсе.
        /// </summary>
        public static async Task<StorageFile> PickFileAsync()
        {
            StorageFile modern = await PickModernAsync();
            if (modern != null) return modern;

            // Вернулась пустота при работающем новом способе — человек просто
            // ничего не выбрал, и свой обход показывать поверх не надо.
            if (_modern == true) return null;

            return await Ui.FileBrowser.ShowAsync();
        }

        private static Task<StorageFile> PickAsync(PickerLocationId start,
                                                   PickerViewMode view,
                                                   string[] extensions)
        {
            // Незавершённый прошлый выбор снимается: две задачи сразу
            // система всё равно не отдаст, а брошенное ожидание висело бы
            // вечно.
            Complete((FileOpenPickerContinuationEventArgs)null);

            var picker = new FileOpenPicker
            {
                ViewMode = view,
                SuggestedStartLocation = start
            };

            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);

            _pending = new TaskCompletionSource<StorageFile>();
            picker.PickSingleFileAndContinue();

            return _pending.Task;
        }

        /// <summary>
        /// Приложение вернулось с выбранным файлом. Зовётся из App при
        /// активации продолжением.
        /// </summary>
        public static void Complete(FileOpenPickerContinuationEventArgs arguments)
        {
            TaskCompletionSource<StorageFile> waiting = _pending;
            if (waiting == null) return;

            _pending = null;

            StorageFile file = arguments != null && arguments.Files != null
                               && arguments.Files.Count > 0
                ? arguments.Files[0]
                : null;

            waiting.TrySetResult(file);
        }

        #region Сохранение

        private static TaskCompletionSource<bool> _saving;
        private static string _savingText;

        /// <summary>
        /// Отдать текст наружу — туда, куда укажет пользователь.
        ///
        /// Своей папки «Документы» у приложений Windows Phone 8.1 нет вовсе:
        /// в `KnownFolders` её не существует, и записать файл в общее место
        /// приложение не может. Единственный путь наружу — этот выбор: куда
        /// именно ляжет файл, решает система и выбранное в ней хранилище.
        /// </summary>
        public static Task<bool> SaveAsync(string suggestedName, string text)
        {
            Complete((FileSavePickerContinuationEventArgs)null);

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName
            };

            picker.FileTypeChoices.Add("Текст", new List<string> { ".txt" });

            _saving = new TaskCompletionSource<bool>();
            _savingText = text ?? "";

            picker.PickSaveFileAndContinue();

            return _saving.Task;
        }

        /// <summary>Приложение вернулось с выбранным местом для записи.</summary>
        public static async void Complete(FileSavePickerContinuationEventArgs arguments)
        {
            TaskCompletionSource<bool> waiting = _saving;
            if (waiting == null) return;

            _saving = null;

            string text = _savingText;
            _savingText = null;

            StorageFile file = arguments != null ? arguments.File : null;
            if (file == null)
            {
                waiting.TrySetResult(false);
                return;
            }

            try
            {
                await FileIO.WriteTextAsync(file, text);
                waiting.TrySetResult(true);
            }
            catch (Exception)
            {
                waiting.TrySetResult(false);
            }
        }

        #endregion

        /// <summary>Содержимое файла целиком — его и заливаем.</summary>
        public static async Task<byte[]> ReadAsync(StorageFile file)
        {
            if (file == null) return null;

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(await FileIO.ReadBufferAsync(file), out bytes);

            return bytes;
        }
    }
}
