using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Windows.Storage;

namespace Lovit.Background
{
    /// <summary>
    /// Уведомления о сообщениях, пришедших, пока приложение было закрыто.
    ///
    /// Агент просыпается раз в полчаса и входит на сервер ради плитки; в
    /// ответе на вход у каждого чата уже лежит последнее сообщение. О нём и
    /// уведомляем — если оно новее всего, о чём человек уже знает
    /// (`AppSettings.NotifiedUpTo`), чужое и чат не молчит.
    ///
    /// Это не мгновенные уведомления: пуши MAX рассылает только официальным
    /// приложениям. Зато сообщение не потеряется, даже если приложение
    /// неделю не открывали.
    /// </summary>
    internal static class NewMessages
    {
        /// <summary>Больше за раз не показываем: после долгой тишины вышла бы лента.</summary>
        private const int MostAtOnce = 5;

        public static async Task PostAsync(IDictionary<string, object> login)
        {
            IList<object> chats = MessagePack.List(login, "chats");
            if (chats == null) return;

            long me = MessagePack.Long(MessagePack.Map(MessagePack.Map(login, "profile"), "contact"), "id");
            IDictionary<string, object> quiet = MessagePack.Map(MessagePack.Map(login, "config"), "chats");

            long upTo = AppSettings.NotifiedUpTo;
            long newest = upTo;

            var fresh = new List<IDictionary<string, object>>();

            foreach (object item in chats)
            {
                var chat = item as IDictionary<string, object>;
                IDictionary<string, object> last = MessagePack.Map(chat, "lastMessage");
                if (chat == null || last == null) continue;

                long time = MessagePack.Long(last, "time");
                if (time > newest) newest = time;

                if (time <= upTo) continue;
                if (MessagePack.Long(chat, "newMessages") <= 0) continue;

                long sender = MessagePack.Long(last, "sender");
                if (sender == 0 || sender == me) continue;
                if (MessagePack.String(chat, "type") == "CHANNEL") continue;
                if (IsQuiet(quiet, MessagePack.Long(chat, "id"))) continue;
                if (FirstAttachment(last) == "CONTROL") continue;

                fresh.Add(chat);
            }

            AppSettings.NotifiedUpTo = newest;

            // Первый запуск агента: отсчёта ещё нет, и «новым» оказалось бы
            // всё непрочитанное за годы. Только запоминаем, с чего считать.
            if (upTo == 0 || fresh.Count == 0) return;

            Dictionary<long, string> names = await NamesAsync(login);

            fresh.Sort((a, b) => MessagePack.Long(MessagePack.Map(b, "lastMessage"), "time")
                                 .CompareTo(MessagePack.Long(MessagePack.Map(a, "lastMessage"), "time")));

            for (int i = 0; i < fresh.Count && i < MostAtOnce; i++)
            {
                Post(fresh[i], me, names);
            }

            Log.Write("app", "уведомления в фоне: " + Math.Min(fresh.Count, MostAtOnce));
        }

        private static void Post(IDictionary<string, object> chat, long me, Dictionary<long, string> names)
        {
            IDictionary<string, object> last = MessagePack.Map(chat, "lastMessage");
            long chatId = MessagePack.Long(chat, "id");
            long sender = MessagePack.Long(last, "sender");
            long count = MessagePack.Long(chat, "newMessages");

            string senderName;
            names.TryGetValue(sender, out senderName);

            string title;
            string prefix = "";

            if (MessagePack.String(chat, "type") == "DIALOG")
            {
                title = string.IsNullOrEmpty(senderName) ? "Новое сообщение" : senderName;
            }
            else
            {
                title = MessagePack.String(chat, "title");
                if (string.IsNullOrEmpty(title)) title = "Чат";
                if (!string.IsNullOrEmpty(senderName)) prefix = senderName + ": ";
            }

            string text;
            if (AppSettings.NotificationText)
            {
                string body = MessagePack.String(last, "text");
                if (string.IsNullOrEmpty(body)) body = Toasts.AttachmentLabel(FirstAttachment(last));
                text = prefix + body.Replace('\n', ' ');

                // Видно только последнее — остальные хотя бы сосчитаем.
                if (count > 1) text += " (+" + (count - 1) + ")";
            }
            else
            {
                text = "Новое сообщение";
            }

            Toasts.Show(chatId, title, text);
        }

        private static bool IsQuiet(IDictionary<string, object> quiet, long chatId)
        {
            if (quiet == null) return false;

            object entry;
            return quiet.TryGetValue(chatId.ToString(), out entry)
                   && MessagePack.Long(entry as IDictionary<string, object>, "dontDisturbUntil") != 0;
        }

        private static string FirstAttachment(IDictionary<string, object> message)
        {
            IList<object> attaches = MessagePack.List(message, "attaches");
            if (attaches == null || attaches.Count == 0) return null;
            return MessagePack.String(attaches[0] as IDictionary<string, object>, "_type");
        }

        /// <summary>
        /// Имена: сперва карточки, сохранённые приложением (`contacts.txt`), —
        /// там и те, кто не записан в книжке; поверх — контакты из ответа на
        /// вход, они свежее.
        /// </summary>
        private static async Task<Dictionary<long, string>> NamesAsync(IDictionary<string, object> login)
        {
            var names = new Dictionary<long, string>();

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync("contacts.txt");
                foreach (string line in await FileIO.ReadLinesAsync(file))
                {
                    string[] parts = line.Split('\t');
                    long id;
                    if (parts.Length >= 2 && long.TryParse(parts[0], out id) && parts[1].Length > 0)
                    {
                        names[id] = parts[1];
                    }
                }
            }
            catch (Exception)
            {
                // Приложение ещё не сохраняло карточек.
            }

            IList<object> contacts = MessagePack.List(login, "contacts");
            if (contacts == null) return names;

            foreach (object item in contacts)
            {
                var contact = item as IDictionary<string, object>;
                if (contact == null) continue;

                string name = NameOf(contact);
                if (!string.IsNullOrEmpty(name)) names[MessagePack.Long(contact, "id")] = name;
            }

            return names;
        }

        /// <summary>
        /// Своё имя человека (`ONEME`), а не то, под которым он записан, —
        /// так же, как MaxContact в приложении.
        /// </summary>
        private static string NameOf(IDictionary<string, object> contact)
        {
            IDictionary<string, object> chosen = null;

            IList<object> list = MessagePack.List(contact, "names");
            if (list != null)
            {
                foreach (object item in list)
                {
                    var entry = item as IDictionary<string, object>;
                    if (entry == null) continue;
                    if (chosen == null) chosen = entry;
                    if (MessagePack.String(entry, "type") == "ONEME") { chosen = entry; break; }
                }
            }

            if (chosen == null) return MessagePack.String(contact, "name");

            string name = MessagePack.String(chosen, "name");
            if (!string.IsNullOrEmpty(name)) return name;

            return (MessagePack.String(chosen, "firstName") + " "
                    + MessagePack.String(chosen, "lastName")).Trim();
        }
    }
}
