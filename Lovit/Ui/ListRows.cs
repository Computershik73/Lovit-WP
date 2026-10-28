using System.Collections.Generic;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Models;
using Windows.UI.Xaml.Media;

namespace Lovit.Ui
{
    /// <summary>
    /// Сборка строк списков из моделей — одинаково для всех экранов.
    ///
    /// Строки не пересоздаются, а обновляются: у каждой из них есть аватарка,
    /// и новая строка означала бы новую загрузку картинки. Ровно поэтому
    /// список мигал при каждом возвращении к нему.
    /// </summary>
    public static class ListRows
    {
        private static readonly Dictionary<long, ListRow> _chatRows = new Dictionary<long, ListRow>();
        private static readonly Dictionary<long, ListRow> _contactRows = new Dictionary<long, ListRow>();

        public static ListRow FromChat(MaxChat chat)
        {
            ChatsModule chats = ChatsModule.Instance;

            string preview = chat.LastMessageText ?? "";

            // В группе видно, кто написал: без имени переписка выглядит
            // разговором с самим собой.
            if (!chat.IsDialog && chat.LastMessageSenderId != 0 && preview.Length > 0)
            {
                if (chat.LastMessageSenderId == chats.CurrentUserId)
                {
                    preview = "Вы: " + preview;
                }
                else
                {
                    MaxContact sender = chats.Contact(chat.LastMessageSenderId);
                    if (sender != null) preview = sender.DisplayName + ": " + preview;
                }
            }

            ListRow row = Existing(_chatRows, chat.ChatId);

            row.Title = chats.Title(chat);
            row.Preview = Formats.OneLine(preview);
            row.Time = Formats.RelativeTime(chat.LastMessageTime);
            row.UnreadCount = chat.UnreadCount;
            row.Dimmed = chat.IsMuted;

            Fill(row, chats.AvatarUrl(chat));
            return row;
        }

        public static ListRow FromContact(MaxContact contact)
        {
            string presence = ChatsModule.PresenceText(
                ChatsModule.Instance.Presence(contact.ContactId));

            ListRow row = Existing(_contactRows, contact.ContactId);

            row.Title = contact.DisplayName;
            row.Preview = string.IsNullOrEmpty(presence) ? contact.PhoneText : presence;

            Fill(row, contact.AvatarUrl);
            return row;
        }

        /// <summary>Хвост адреса — по нему картинки различимы, а строка коротка.</summary>
        private static string Tail(string url)
        {
            if (string.IsNullOrEmpty(url)) return "нет";
            return url.Length <= 24 ? url : "…" + url.Substring(url.Length - 24);
        }

        private static ListRow Existing(Dictionary<long, ListRow> cache, long id)
        {
            ListRow row;
            if (cache.TryGetValue(id, out row)) return row;

            row = new ListRow { Id = id };
            cache[id] = row;

            return row;
        }

        /// <summary>
        /// Аватарка: если она уже в памяти — сразу, иначе догрузится и
        /// подставится сама. Строка списка при этом не ждёт: без картинки
        /// показываются буквы на цветном квадрате.
        /// </summary>
        /// <summary>
        /// Подставить аватарку в строку, собранную не здесь, — например в
        /// выдачу поиска, где строки разнородные.
        /// </summary>
        public static void Load(ListRow row, string url)
        {
            Fill(row, url);
        }

        private static async void Fill(ListRow row, string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            // Уже стоит, и адрес тот же — не трогаем: повторное присваивание
            // той же картинки заставило бы разметку перерисовать её. А вот
            // сменившийся адрес перерисовать надо: поставленная однажды
            // аватарка держалась до перезапуска, и попавшая не туда
            // оставалась на месте навсегда.
            if (row.Avatar != null && row.AvatarUrl == url) return;

            // Смена адреса у строки, где аватарка уже стояла, — редкость:
            // человек поменял снимок. Записываем: жаловались, что после
            // пересылки сообщения от бота все чаты в списке взяли себе его
            // аватарку, а по чему это случилось — пока неизвестно.
            if (row.Avatar != null)
            {
                Log.Write("mem", "аватарка " + row.Id + ": " + Tail(row.AvatarUrl)
                                 + " → " + Tail(url));
            }

            row.AvatarUrl = url;

            ImageSource ready = ImageCache.Cached(url, ImageCache.AvatarSide);
            if (ready != null)
            {
                row.Avatar = ready;
                return;
            }

            ImageSource loaded = await ImageCache.LoadAsync(url, ImageCache.AvatarSide);
            if (loaded != null) row.Avatar = loaded;
        }

        public static List<ListRow> FromChats(IEnumerable<MaxChat> chats)
        {
            var rows = new List<ListRow>();
            foreach (MaxChat chat in chats) rows.Add(FromChat(chat));
            return rows;
        }

        public static List<ListRow> FromContacts(IEnumerable<MaxContact> contacts)
        {
            var rows = new List<ListRow>();
            foreach (MaxContact contact in contacts) rows.Add(FromContact(contact));
            return rows;
        }

        /// <summary>Смена аккаунта: чужие строки и чужие лица показывать нельзя.</summary>
        public static void Reset()
        {
            _chatRows.Clear();
            _contactRows.Clear();
        }
    }
}
