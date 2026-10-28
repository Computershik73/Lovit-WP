using System.Collections.Generic;
using Lovit.Core.Protocol;

namespace Lovit.Models
{
    /// <summary>Чат: диалог, группа или канал. Порт MAXChat.</summary>
    public class MaxChat
    {
        public long ChatId { get; set; }
        public string Type { get; set; }          // DIALOG / CHAT / CHANNEL
        public string Title { get; set; }
        public string IconUrl { get; set; }
        public string Link { get; set; }
        public string Description { get; set; }

        public long LastMessageId { get; set; }
        public long LastMessageTime { get; set; }
        public string LastMessageText { get; set; }
        public long LastMessageSenderId { get; set; }

        public int UnreadCount { get; set; }
        public long LastEventTime { get; set; }
        public int ParticipantsCount { get; set; }
        public long OwnerId { get; set; }

        /// <summary>Участники: идентификатор → время входа.</summary>
        public Dictionary<long, long> Participants { get; set; }

        public HashSet<long> AdminIds { get; set; }

        /// <summary>Признаки чата: OFFICIAL, BOT и прочие.</summary>
        public HashSet<string> Options { get; set; }

        /// <summary>
        /// До какого момента чат «молчит»: 0 — уведомления включены,
        /// отрицательное — выключены навсегда. Приходит не в самом чате,
        /// а в `config.chats` ответа на вход.
        /// </summary>
        public long DontDisturbUntil { get; set; }

        public long PinnedMessageId { get; set; }
        public string PinnedMessageText { get; set; }
        public long PinnedMessageTime { get; set; }

        /// <summary>Состояние чата у сервера: ACTIVE, HIDDEN и прочие.</summary>
        public string Status { get; set; }

        /// <summary>
        /// Спрятанный чат. Так помечены подсобные чаты — заведённые под
        /// групповой звонок в том числе: показывать их в списке незачем.
        /// </summary>
        public bool IsHidden { get { return Status == "HIDDEN"; } }

        public bool IsDialog { get { return Type == "DIALOG"; } }

        public bool IsChannel { get { return Type == "CHANNEL"; } }

        public bool IsMuted { get { return DontDisturbUntil != 0; } }

        public bool IsOfficial { get { return Options.Contains("OFFICIAL"); } }

        public bool IsBot { get { return Options.Contains("BOT"); } }

        public MaxChat()
        {
            Type = "CHAT";
            Participants = new Dictionary<long, long>();
            AdminIds = new HashSet<long>();
            Options = new HashSet<string>();
        }

        /// <summary>
        /// Второй участник диалога. У «Избранного» его нет — это переписка
        /// с самим собой, и там возвращается ноль.
        /// </summary>
        public long OtherParticipant(long currentUserId)
        {
            foreach (long id in Participants.Keys)
            {
                if (id != currentUserId) return id;
            }
            return 0;
        }

        /// <summary>Владелец или администратор. Порт `iAmAdmin` из Komet.</summary>
        public bool IsAdmin(long userId)
        {
            if (userId == 0) return false;
            return OwnerId == userId || AdminIds.Contains(userId);
        }

        /// <summary>
        /// Можно ли писать в этот чат. В канале — только администраторам:
        /// у остальных строка ввода заменяется кнопкой подписки.
        /// </summary>
        public bool CanWrite(long userId)
        {
            if (!IsChannel) return true;
            return IsAdmin(userId);
        }

        public static MaxChat FromMap(IDictionary<string, object> map,
                                      long currentUserId,
                                      IDictionary<long, MaxContact> contactsById)
        {
            if (map == null) return null;

            long chatId = MessagePack.Long(map, "id");

            // Ноль — это «Избранное», переписка с самим собой, а не порченая
            // запись. Отбрасывая её как негодную, мы теряли из списка целый
            // чат; в пушах он приходит тем же нулём.
            if (chatId == 0 && !map.ContainsKey("id")) return null;

            var chat = new MaxChat
            {
                ChatId = chatId,
                Type = MessagePack.String(map, "type") ?? "DIALOG",
                UnreadCount = (int)MessagePack.Long(map, "newMessages"),
                LastEventTime = MessagePack.Long(map, "lastEventTime"),
                ParticipantsCount = (int)MessagePack.Long(map, "participantsCount"),
                OwnerId = MessagePack.Long(map, "owner"),
                Link = MessagePack.String(map, "link"),
                Description = MessagePack.String(map, "description"),
                Status = MessagePack.String(map, "status")
            };

            ParseParticipants(chat, MessagePack.Map(map, "participants"));
            ParseAdmins(chat, map);

            if (chat.IsDialog)
            {
                // У диалога своего названия и значка нет: и то, и другое
                // берётся из карточки собеседника.
                long otherId = chat.OtherParticipant(currentUserId);

                MaxContact contact;
                if (contactsById != null && contactsById.TryGetValue(otherId, out contact))
                {
                    chat.Title = contact.Name;
                    chat.IconUrl = contact.AvatarUrl;
                    foreach (string option in contact.Options) chat.Options.Add(option);
                }
            }
            else
            {
                chat.Title = MessagePack.String(map, "title");
                chat.IconUrl = MessagePack.String(map, "baseIconUrl");

                // У групп признаки приходят словарём «имя → включено».
                IDictionary<string, object> options = MessagePack.Map(map, "options");
                if (options != null)
                {
                    foreach (var pair in options)
                    {
                        if (pair.Value is bool && (bool)pair.Value) chat.Options.Add(pair.Key);
                    }
                }
            }

            // Закреп лежит рядом с последним сообщением и устроен так же.
            IDictionary<string, object> pinned = MessagePack.Map(map, "pinnedMessage");
            if (pinned != null)
            {
                chat.PinnedMessageId = MessagePack.Long(pinned, "id");
                chat.PinnedMessageTime = MessagePack.Long(pinned, "time");
                chat.PinnedMessageText = MaxMessage.PreviewText(pinned);
            }

            IDictionary<string, object> last = MessagePack.Map(map, "lastMessage");
            if (last != null)
            {
                chat.LastMessageId = MessagePack.Long(last, "id");
                chat.LastMessageTime = MessagePack.Long(last, "time");
                chat.LastMessageSenderId = MessagePack.Long(last, "sender");
                chat.LastMessageText = MaxMessage.PreviewText(last);
            }

            return chat;
        }

        /// <summary>
        /// Участники приходят картой «идентификатор → время входа»: ключи
        /// здесь и есть люди, значения нам не нужны.
        /// </summary>
        private static void ParseParticipants(MaxChat chat, IDictionary<string, object> raw)
        {
            if (raw == null) return;

            foreach (var pair in raw)
            {
                long id;
                if (!long.TryParse(pair.Key, out id) || id == 0) continue;

                chat.Participants[id] = pair.Value is long ? (long)pair.Value : 0;
            }
        }

        /// <summary>
        /// Администраторы приходят двумя способами: списком `admins` либо
        /// словарём `adminParticipants`, где они лежат ключами. По одному
        /// чату не угадать, какой пришлёт сервер.
        /// </summary>
        private static void ParseAdmins(MaxChat chat, IDictionary<string, object> map)
        {
            IList<object> list = MessagePack.List(map, "admins");
            if (list != null)
            {
                foreach (object item in list)
                {
                    long id = ToLong(item);
                    if (id != 0) chat.AdminIds.Add(id);
                }
                return;
            }

            IDictionary<string, object> participants = MessagePack.Map(map, "adminParticipants");
            if (participants == null) return;

            foreach (string key in participants.Keys)
            {
                long id;
                if (long.TryParse(key, out id) && id != 0) chat.AdminIds.Add(id);
            }
        }

        private static long ToLong(object value)
        {
            if (value is long) return (long)value;

            long parsed;
            return value != null && long.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }
    }
}
