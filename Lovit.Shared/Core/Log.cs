using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Lovit.Core
{
    /// <summary>
    /// Запись в файл. Порт MAXLog, но со своей причиной существования.
    ///
    /// На телефоне отладчик доступен не всегда, а половина здешних бед
    /// проявляется как раз без него: соединение рвётся молча, приложение
    /// закрывает система, чат открывается пустым. Ни одно из этого не видно
    /// постфактум, если не записать. Поэтому пишем всегда, а не только в
    /// отладочной сборке.
    ///
    /// Пишется в `LocalFolder\lovit.log`; смотреть можно прямо в приложении,
    /// в настройках. Строки копятся в памяти и уходят на диск пачкой: на
    /// каждый чих открывать файл — это заметно медленнее самой работы.
    ///
    /// «Всегда» — это про отладочную сборку. В отправляемой (конфигурация
    /// Release) пишутся только падения: в остальных строках — переписка,
    /// имена собеседников, адреса вложений, а людям, которые просто
    /// пользуются приложением, держать это у себя в файле незачем. Так же
    /// устроено в версии для iOS (MAX_LOG_TO_FILE в MAXLog.h).
    /// </summary>
    public static class Log
    {
#if DEBUG
        /// <summary>Отладочная сборка: пишется всё.</summary>
        private static readonly bool Everything = true;
#else
        /// <summary>
        /// Отправляемая сборка: только падения и заголовок запуска — по нему
        /// видно, какая сборка упала. Переписки ни в том, ни в другом нет.
        /// </summary>
        private static readonly bool Everything = false;
#endif

        /// <summary>Пишется ли журнал целиком — для страницы журнала.</summary>
        public static bool WritesEverything { get { return Everything; } }

        /// <summary>Больше этого файл не растёт: старое уезжает в `.old`.</summary>
        private const long MaxFileBytes = 512 * 1024;

        /// <summary>Сколько строк держать для показа в приложении.</summary>
        private const int RecentLimit = 400;

        private const string FileName = "lovit.log";
        private const string OldFileName = "lovit.old.log";

        private static readonly object _lock = new object();
        private static readonly List<string> _waiting = new List<string>();
        private static readonly Queue<string> _recent = new Queue<string>();

        private static bool _flushing;
        private static bool _begun;
        private static DateTime _started = DateTime.Now;

        /// <summary>
        /// Заголовок запуска — первым делом, один раз.
        ///
        /// Файл переживает и перезапуск, и обновление приложения, а время в
        /// строках отсчитывается от начала работы, то есть каждый запуск
        /// начинается с нулей. Без этой отметки записи нескольких запусков
        /// сливаются в одну ленту, неотличимо: открываешь журнал, видишь
        /// сверху «00:00:00» — и читаешь позавчерашний.
        ///
        /// Отсюда же и версия сборки: по ней сразу видно, та ли она, о
        /// которой идёт речь.
        /// </summary>
        private static void Begin()
        {
            lock (_lock)
            {
                if (_begun) return;
                _begun = true;
            }

            PackageVersion version = Package.Current.Id.Version;

            Put("app", "──── запуск "
                         + _started.ToString("yyyy-MM-dd HH:mm:ss")
                         + ", сборка " + version.Major + "." + version.Minor
                         + "." + version.Build + "." + version.Revision
                         + " ────");
        }

        /// <summary>
        /// Строка журнала. `area` — откуда она: «tls», «api», «chat».
        /// Разделение по областям важнее подробностей: в потоке из сотен
        /// строк ищут именно область.
        /// </summary>
        public static void Write(string area, string message)
        {
            // В отправляемой сборке — только падения. Отбор здесь, на входе:
            // отброшенная строка не попадает ни в файл, ни в память для
            // страницы журнала.
            if (!Everything && area != "crash") return;

            Begin();
            Put(area, message);
        }

        /// <summary>Сама запись — уже без отбора.</summary>
        private static void Put(string area, string message)
        {
            TimeSpan since = DateTime.Now - _started;

            string line = string.Format("{0:hh\\:mm\\:ss\\.fff} [{1}] {2}",
                                        since, area ?? "?", message ?? "");

            lock (_lock)
            {
                _waiting.Add(line);

                _recent.Enqueue(line);
                while (_recent.Count > RecentLimit) _recent.Dequeue();
            }

            Schedule();
        }

        /// <summary>
        /// Сколько памяти занимает само приложение — и сколько ему отведено.
        ///
        /// Именно своё, а не общесистемное: в Device Portal видно всю память
        /// телефона разом, и по тем числам о приложении судить нельзя.
        /// </summary>
        /// <summary>Сколько было занято в прошлый раз, в мегабайтах.</summary>
        private static long _lastMemory = -1;

        /// <summary>
        /// Отметка о памяти, но не на каждый чих: пинг ходит раз в десять
        /// секунд, и такие строки вытесняли из журнала всё остальное — при
        /// пределе в полмегабайта до настоящей причины сбоя дело не доходило.
        /// Пишем, когда расход заметно сдвинулся; редкую отметку «всё как
        /// было» оставляем, чтобы цепочка не рвалась совсем.
        /// </summary>
        public static void MemoryIfChanged(string note)
        {
            long used = (long)(Windows.System.MemoryManager.AppMemoryUsage / (1024 * 1024));

            if (_lastMemory >= 0 && Math.Abs(used - _lastMemory) < 8)
            {
                if (++_quiet < 30) return;
            }

            _quiet = 0;
            _lastMemory = used;

            Memory(note);
        }

        private static int _quiet;

        public static void Memory(string note)
        {
            ulong used = Windows.System.MemoryManager.AppMemoryUsage;
            ulong limit = Windows.System.MemoryManager.AppMemoryUsageLimit;

            Write("mem", (note ?? "") + ": занято "
                         + (used / (1024 * 1024)) + " МБ из "
                         + (limit / (1024 * 1024)) + " МБ, ступень "
                         + Windows.System.MemoryManager.AppMemoryUsageLevel);
        }

        /// <summary>Сбой: пишется так же, но заметно.</summary>
        public static void Fail(string area, string message, Exception error)
        {
            Write(area, "ОШИБКА " + message
                        + (error != null ? " — " + error.GetType().Name + ": " + error.Message : ""));
        }

        /// <summary>Последние строки для показа в приложении, новые внизу.</summary>
        public static string Recent()
        {
            lock (_lock)
            {
                if (_recent.Count == 0) return "Пока пусто.";
                return string.Join(Environment.NewLine, _recent.ToArray());
            }
        }

        #region Тайное

        private static readonly string[] SensitiveParts =
        {
            "password", "token", "secret", "auth"
        };

        private static readonly string[] SensitiveWhole =
        {
            "code", "verifycode", "smscode", "otp", "hint", "pin", "qrlink",
            "text", "deviceid", "mt_instanceid", "instanceid", "webappdata"
        };

        /// <summary>
        /// Что писать вместо значения. Список тот же, что в Komet
        /// (log_redact.dart): журнал попадает в чужие руки чаще, чем кажется,
        /// а токен сессии в нём — это чужой аккаунт целиком.
        ///
        /// Текст сообщений прячется тоже, и это не перестраховка: переписку
        /// показывают вместе с журналом, не задумываясь.
        /// </summary>
        public static string Safe(string key, object value)
        {
            string name = (key ?? "").ToLower();

            if (name.Contains("phone") || name == "msisdn")
            {
                string digits = value != null ? value.ToString() : "";
                return digits.Length <= 3 ? digits : digits.Substring(0, 3) + "***";
            }

            foreach (string whole in SensitiveWhole)
            {
                if (name == whole) return "***";
            }

            foreach (string part in SensitiveParts)
            {
                if (name.Contains(part)) return "***";
            }

            var bytes = value as byte[];
            if (bytes != null) return "<" + bytes.Length + " байт>";

            return value != null ? value.ToString() : "null";
        }

        /// <summary>
        /// Строка номерами знаков — когда сама строка в журнале ничего не
        /// объясняет.
        ///
        /// Жаловались на имена из одних вопросительных знаков. Вопрос —
        /// обычная примета потерянной кодировки: так выглядит буква,
        /// которую при переводе не сумели передать. Но бывает и что сервер
        /// прислал именно её. Различить это можно только по номерам.
        /// </summary>
        public static string Spell(string text)
        {
            if (string.IsNullOrEmpty(text)) return "пусто";

            var made = new StringBuilder();

            foreach (char sign in text)
            {
                if (made.Length > 0) made.Append(' ');
                made.Append("U+").Append(((int)sign).ToString("X4"));

                if (made.Length > 160) { made.Append(" …"); break; }
            }

            return made.ToString();
        }

        /// <summary>Ключи содержимого одной строкой — уже безопасно.</summary>
        public static string Keys(IDictionary<string, object> map)
        {
            if (map == null) return "нет";

            var text = new StringBuilder();
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(pair.Key).Append('=').Append(Safe(pair.Key, pair.Value));

                if (text.Length > 400) { text.Append(", …"); break; }
            }

            return text.Length > 0 ? text.ToString() : "пусто";
        }

        #endregion

        #region Файл

        /// <summary>
        /// Дописывает на диск сразу, а не по таймеру.
        ///
        /// Отложенная запись здесь бессмысленна: журнал заводился ради
        /// падений, а падение уносит с собой всё, что не успело лечь на
        /// диск, — то есть ровно последние секунды, единственно интересные.
        /// Пачками уходит только то, что накопилось за время самой записи.
        /// </summary>
        private static async void Schedule()
        {
            lock (_lock)
            {
                if (_flushing) return;
                _flushing = true;
            }

            try
            {
                while (true)
                {
                    await FlushAsync();

                    lock (_lock)
                    {
                        if (_waiting.Count == 0)
                        {
                            _flushing = false;
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Журнал не та вещь, ради которой стоит падать.
                lock (_lock) _flushing = false;
            }
        }

        /// <summary>
        /// Дописать накопленное, не отпуская поток.
        ///
        /// Нужно ровно в одном месте — там, где приложение уже падает.
        /// Обычная запись откладывается на потом, а «потом» в этот миг
        /// не наступит: строки о самом падении уйдут вместе с приложением,
        /// и в журнале останется обрыв на полуслове.
        ///
        /// Ждать можно только потому, что внутри записи нет возврата на
        /// поток окна: иначе ожидание встало бы намертво.
        /// </summary>
        public static void Settle()
        {
            try { FlushAsync().Wait(3000); }
            catch (Exception) { }
        }

        /// <summary>Дописать накопленное сейчас же — перед закрытием, например.</summary>
        public static async Task FlushAsync()
        {
            string[] batch;

            lock (_lock)
            {
                if (_waiting.Count == 0) return;

                batch = _waiting.ToArray();
                _waiting.Clear();
            }

            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;

                StorageFile file = await folder.CreateFileAsync(
                    FileName, CreationCollisionOption.OpenIfExists).AsTask()
                    .ConfigureAwait(false);

                await RotateAsync(folder, file).ConfigureAwait(false);

                await FileIO.AppendLinesAsync(file, batch).AsTask()
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Диск занят или кончился — записанное в памяти всё равно
                // видно в настройках.
            }
        }

        /// <summary>
        /// Разросшийся файл уезжает в `.old`, на его место встаёт пустой.
        /// Двух поколений хватает: беда обычно видна в последних минутах.
        /// </summary>
        private static async Task RotateAsync(StorageFolder folder, StorageFile file)
        {
            // Возврата на поток окна здесь нет нарочно: этой записи ждут
            // из обработчика падения, не отпуская поток, и возврат встал бы
            // намертво.
            Windows.Storage.FileProperties.BasicProperties properties =
                await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);

            if ((long)properties.Size < MaxFileBytes) return;

            await file.RenameAsync(OldFileName, NameCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);

            await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
        }

        /// <summary>
        /// Записанное на диске — вместе с тем, что осталось от прошлого
        /// запуска.
        ///
        /// Показывать надо именно файл, а не память: после падения память
        /// пуста, а причина падения записана как раз в файл, и она —
        /// единственное, ради чего сюда заходят.
        /// </summary>
        public static async Task<string> ReadAsync()
        {
            await FlushAsync();

            string current = await ReadFileAsync(FileName);
            if (current.Length > 0) return current;

            // Свежий файл пуст только сразу после уборки — тогда прошлое
            // поколение и есть всё, что у нас есть.
            string older = await ReadFileAsync(OldFileName);
            if (older.Length > 0) return older;

            return Recent();
        }

        private static async Task<string> ReadFileAsync(string name)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(name);
                return await FileIO.ReadTextAsync(file);
            }
            catch (Exception)
            {
                // Файла ещё нет — обычное дело при первом запуске.
                return "";
            }
        }

        /// <summary>Стереть записанное — и в памяти, и на диске.</summary>
        public static async Task ClearAsync()
        {
            lock (_lock)
            {
                _waiting.Clear();
                _recent.Clear();
                _started = DateTime.Now;
            }

            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            }
            catch (Exception)
            {
            }
        }

        #endregion
    }
}
