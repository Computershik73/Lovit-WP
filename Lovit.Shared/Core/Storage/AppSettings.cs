using Windows.Storage;

namespace Lovit.Core.Storage
{
    /// <summary>
    /// Настройки и токен. Порт MAXSettings и MAXTokenStorage.
    ///
    /// Всё лежит в `LocalSettings` — хранилище приложения, которое чистится
    /// вместе с ним. Отдельного защищённого хранилища вроде Keychain на
    /// Windows Phone для таких вещей нет: `PasswordVault` есть, но он
    /// рассчитан на пары «логин-пароль» и на телефоне ведёт себя ненадёжно
    /// при переустановке. Токен здесь — не пароль: сервер отзывает его по
    /// выходу из аккаунта.
    /// </summary>
    public static class AppSettings
    {
        private static ApplicationDataContainer Local
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        public static string Token
        {
            get { return Read("token"); }
            set { Write("token", value); }
        }

        public static long AccountId
        {
            get { return ReadLong("accountId"); }
            set { Write("accountId", value.ToString()); }
        }

        public static bool GhostMode
        {
            get { return ReadLong("ghostMode") != 0; }
            set { Write("ghostMode", value ? "1" : "0"); }
        }

        public static bool AntiRead
        {
            get { return ReadLong("antiRead") != 0; }
            set { Write("antiRead", value ? "1" : "0"); }
        }

        public static bool AntiTyping
        {
            get { return ReadLong("antiTyping") != 0; }
            set { Write("antiTyping", value ? "1" : "0"); }
        }

        /// <summary>
        /// Когда приложение в последний раз подавало признаки жизни.
        ///
        /// Нужна фоновому агенту, и только ему. Агент входит на сервер по
        /// тому же токену, что и приложение; делать это, пока приложение
        /// работает, нельзя — сервер вправе счесть новую сессию заменой
        /// прежней и оборвать ту, что на экране. Свежая отметка означает
        /// «хозяин на месте, обойдусь без меня».
        ///
        /// Ставится, пока приложение живо, — и не ставится при уходе в сон:
        /// иначе после каждого закрытия агент считал бы его живым.
        /// </summary>
        public static long AliveAt
        {
            get { return ReadLong("aliveAt"); }
            set { Write("aliveAt", value.ToString()); }
        }

        /// <summary>
        /// Обновлять ли плитку, пока приложение закрыто.
        ///
        /// По умолчанию включено, и записи об этом поначалу нет вовсе —
        /// поэтому считаем «нет записи» за «включено». Выключенное же
        /// записывается прямо, нулём: иначе его нельзя было бы отличить от
        /// первого запуска.
        /// </summary>
        public static bool TileAgent
        {
            get { return Read("tileAgent") != "0"; }
            set { Write("tileAgent", value ? "1" : "0"); }
        }

        /// <summary>
        /// Уведомления о новых сообщениях: пока приложение открыто — сразу,
        /// пока закрыто — от фонового агента, когда он проснётся. По
        /// умолчанию включены — как и звук, и текст; «нет записи» значит
        /// «включено», так же как у плитки.
        /// </summary>
        public static bool Notifications
        {
            get { return Read("notify") != "0"; }
            set { Write("notify", value ? "1" : "0"); }
        }

        /// <summary>Звук у уведомления.</summary>
        public static bool NotificationSound
        {
            get { return Read("notifySound") != "0"; }
            set { Write("notifySound", value ? "1" : "0"); }
        }

        /// <summary>
        /// Текст сообщения в уведомлении. Выключено — только «Новое
        /// сообщение»: уведомление видно и на заблокированном экране.
        /// </summary>
        public static bool NotificationText
        {
            get { return Read("notifyText") != "0"; }
            set { Write("notifyText", value ? "1" : "0"); }
        }

        /// <summary>
        /// Время самого свежего сообщения, о котором уже знают — уведомили
        /// или человек видел его в открытом приложении. Фоновый агент
        /// уведомляет только о том, что новее: иначе каждые полчаса
        /// приходили бы одни и те же сообщения.
        /// </summary>
        public static long NotifiedUpTo
        {
            get { return ReadLong("notifiedUpTo"); }
            set { Write("notifiedUpTo", value.ToString()); }
        }

        /// <summary>
        /// Устройство, которым приложение представляется серверу — имя из
        /// [DevicePresets].
        ///
        /// Выбирается наугад при первом обращении и запоминается навсегда.
        /// Раньше здесь у всех стоял один и тот же телефон: со стороны
        /// сервера это выглядело как толпа людей с одинаковой Xiaomi 14 Ultra
        /// — примета приметнее некуда, а смысл подмены ровно обратный.
        ///
        /// Признак устройства (`deviceId`) при смене имени не трогается
        /// нарочно: для сервера новый признак — это вход с нового телефона,
        /// письмо об этом и, если у аккаунта есть пароль, требование ввести
        /// его заново.
        /// </summary>
        public static string DeviceName
        {
            get
            {
                string kept = Read("deviceName");
                if (!string.IsNullOrEmpty(kept)) return kept;

                string picked = Config.DevicePresets.Random();
                Write("deviceName", picked);

                return picked;
            }
            set { Write("deviceName", value); }
        }

        public static void Clear()
        {
            Token = null;
            AccountId = 0;
        }

        private static string Read(string key)
        {
            object value;
            if (!Local.Values.TryGetValue(key, out value)) return null;
            return value as string;
        }

        private static long ReadLong(string key)
        {
            long parsed;
            return long.TryParse(Read(key) ?? "", out parsed) ? parsed : 0;
        }

        private static void Write(string key, string value)
        {
            if (value == null) Local.Values.Remove(key);
            else Local.Values[key] = value;
        }
    }
}
