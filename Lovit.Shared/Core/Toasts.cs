using System;
using Lovit.Core.Storage;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Lovit.Core
{
    /// <summary>
    /// Всплывающие уведомления о сообщениях — одни и те же у приложения и у
    /// фонового агента.
    ///
    /// По уведомлению на чат: новое сообщение заменяет прежнее того же чата,
    /// а не копится стопкой в центре уведомлений. Нажатие запускает
    /// приложение с аргументом `chat:номер` — и открывается этот чат.
    /// </summary>
    public static class Toasts
    {
        /// <summary>Префикс аргумента запуска: за ним номер чата.</summary>
        public const string LaunchPrefix = "chat:";

        private const string Group = "chats";

        public static void Show(long chatId, string title, string text)
        {
            try
            {
                XmlDocument xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);

                XmlNodeList lines = xml.GetElementsByTagName("text");
                lines[0].AppendChild(xml.CreateTextNode(title ?? ""));
                lines[1].AppendChild(xml.CreateTextNode(text ?? ""));

                var toast = (XmlElement)xml.SelectSingleNode("/toast");
                toast.SetAttribute("launch", LaunchPrefix + chatId);

                // Звук по умолчанию — системный звук уведомлений; без звука
                // уведомление приходит молча, одной надписью.
                if (!AppSettings.NotificationSound)
                {
                    XmlElement audio = xml.CreateElement("audio");
                    audio.SetAttribute("silent", "true");
                    toast.AppendChild(audio);
                }

                var notification = new ToastNotification(xml)
                {
                    Tag = TagOf(chatId),
                    Group = Group
                };

                ToastNotificationManager.CreateToastNotifier().Show(notification);
                Log.Write("app", "уведомление о чате " + chatId);
            }
            catch (Exception exception)
            {
                // Уведомления выключены в настройках телефона — или система
                // не дала. Работе приложения это не мешает.
                Log.Write("app", "уведомление не показано: " + exception.Message);
            }
        }

        /// <summary>Чат открыт — его уведомления в центре уведомлений больше не нужны.</summary>
        public static void Forget(long chatId)
        {
            try
            {
                ToastNotificationManager.History.Remove(TagOf(chatId), Group);
            }
            catch (Exception)
            {
                // Уведомлений этого чата и не было.
            }
        }

        /// <summary>Номер чата из аргумента запуска, 0 — запуск не из уведомления.</summary>
        public static long ChatFromLaunch(string arguments)
        {
            if (string.IsNullOrEmpty(arguments) || !arguments.StartsWith(LaunchPrefix)) return 0;

            long chatId;
            return long.TryParse(arguments.Substring(LaunchPrefix.Length), out chatId) ? chatId : 0;
        }

        /// <summary>
        /// Метка уведомления. Больше шестнадцати знаков система не принимает,
        /// а номер чата бывает и девятнадцатизначным, — берём хвост.
        /// </summary>
        private static string TagOf(long chatId)
        {
            string digits = Math.Abs(chatId % 1000000000000000L).ToString();
            return (chatId < 0 ? "m" : "c") + digits;
        }

        /// <summary>
        /// Подпись к сообщению без текста — по первому вложению. Короче, чем
        /// в самом приложении: агенту разбирать вложения целиком нечем.
        /// </summary>
        public static string AttachmentLabel(string type)
        {
            switch (type)
            {
                case "PHOTO": return "Фото";
                case "VIDEO": return "Видео";
                case "AUDIO": return "Голосовое сообщение";
                case "FILE": return "Файл";
                case "STICKER": return "Стикер";
                case "CONTACT": return "Контакт";
                case "LOCATION": return "Геопозиция";
                case "SHARE": return "Ссылка";
                case "POLL": return "Опрос";
                default: return "Вложение";
            }
        }
    }
}
