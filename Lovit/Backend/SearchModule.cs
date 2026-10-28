using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Models;

namespace Lovit.Backend
{
    /// <summary>Найденное сообщение. Порт MAXMessageHit.</summary>
    public class MessageHit
    {
        public long ChatId { get; set; }
        public string MessageId { get; set; }
        public string Text { get; set; }
        public long Time { get; set; }
        public long SenderId { get; set; }
    }

    /// <summary>Что стоит за ссылкой: чат либо человек.</summary>
    public class LinkTarget
    {
        public long ChatId { get; set; }
        public MaxChat Stub { get; set; }

        /// <summary>Ссылка вела на человека — с ним нужен диалог.</summary>
        public long ContactId { get; set; }
    }

    /// <summary>Найденный чужой чат: канал или открытая группа.</summary>
    public class PublicHit
    {
        public long ChatId { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string AvatarUrl { get; set; }

        /// <summary>Ссылка вида `@name` либо последнее сообщение — что нашлось.</summary>
        public string Subtitle { get; set; }

        /// <summary>Заготовка чата, чтобы открыть найденное.</summary>
        public MaxChat ChatStub()
        {
            return new MaxChat
            {
                ChatId = ChatId,
                Type = string.IsNullOrEmpty(Type) ? "CHAT" : Type,
                Title = Title,
                IconUrl = AvatarUrl
            };
        }
    }

    /// <summary>
    /// Поиск: по своему и по всему MAX. Порт MAXSearchModule.
    ///
    /// Ищется в четырёх местах сразу, и три из них разные по природе:
    ///
    ///   * контакты и свои чаты — на месте, по кэшу в памяти. Ответ мгновенный
    ///     и не зависит от связи;
    ///   * сообщения — на сервере, CHAT_SEARCH: по всей переписке, а не по
    ///     одному чату, и локально этого не повторить — история хранится
    ///     не целиком;
    ///   * каналы и группы — на сервере, PUBLIC_SEARCH: то, чего у нас нет вовсе.
    /// </summary>
    public class SearchModule
    {
        /// <summary>
        /// Сколько просить у сервера. Значения из Komet: сообщений больше,
        /// потому что они короткие и часто повторяются по одному чату.
        /// </summary>
        private const int MessageCount = 50;
        private const int PublicCount = 20;

        private static readonly SearchModule _instance = new SearchModule();
        public static SearchModule Instance { get { return _instance; } }

        private SearchModule() { }

        /// <summary>
        /// Что стоит за ссылкой. Порт LinkModule.resolve из Komet: сервер
        /// отвечает либо чатом, либо человеком.
        ///
        /// Своя схема приводится к обычной: max://max.ru/имя сервер не знает,
        /// а https://max.ru/имя — знает.
        /// </summary>
        public async Task<LinkTarget> ResolveLinkAsync(string link)
        {
            string address = Lovit.Core.Links.ForServer(link);
            if (string.IsNullOrEmpty(address)) return null;

            Log.Write("chat", "разбираем ссылку " + address);

            Packet packet = await MaxApi.Instance.RequestAsync(
                Opcodes.LinkInfo, new Dictionary<string, object> { { "link", address } });

            if (packet == null || packet.IsError)
            {
                Log.Write("chat", "ссылку не разобрать: "
                                  + (packet == null ? "нет связи"
                                     : MessagePack.String(packet.Map, "message")));
                return null;
            }

            IDictionary<string, object> chat = MessagePack.Map(packet.Map, "chat");
            if (chat != null)
            {
                var target = new LinkTarget { ChatId = MessagePack.Long(chat, "id") };

                target.Stub = MaxChat.FromMap(chat, ChatsModule.Instance.CurrentUserId, null);
                return target;
            }

            IDictionary<string, object> user = MessagePack.Map(packet.Map, "user");
            IDictionary<string, object> contact = MessagePack.Map(user, "contact");

            if (contact != null)
            {
                return new LinkTarget { ContactId = MessagePack.Long(contact, "id") };
            }

            return null;
        }

        #region По кэшу

        /// <summary>Контакты по имени и номеру — без запроса.</summary>
        public List<MaxContact> LocalContacts(string query)
        {
            var found = new List<MaxContact>();

            string term = (query ?? "").Trim();
            if (term.Length == 0) return found;

            foreach (MaxContact contact in ChatsModule.Instance.Contacts)
            {
                string phone = contact.Phone > 0 ? contact.Phone.ToString() : null;

                if (Matches(contact.Name, term) || Matches(phone, term)) found.Add(contact);
            }

            return found;
        }

        /// <summary>
        /// Свои чаты по названию — тоже по кэшу. Диалоги ищутся по имени
        /// собеседника: своего названия у них нет.
        /// </summary>
        public List<MaxChat> LocalChats(string query)
        {
            var found = new List<MaxChat>();

            string term = (query ?? "").Trim();
            if (term.Length == 0) return found;

            ChatsModule chats = ChatsModule.Instance;

            foreach (MaxChat chat in chats.Chats)
            {
                if (Matches(chats.Title(chat), term)) found.Add(chat);
            }

            return found;
        }

        /// <summary>
        /// Сравнение без учёта регистра и в любом месте строки: искать по
        /// началу слова недостаточно — фамилию в списке контактов набирают
        /// чаще имени.
        /// </summary>
        private static bool Matches(string text, string query)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.ToLower().Contains(query.ToLower());
        }

        #endregion

        #region На сервере

        /// <summary>CHAT_SEARCH: сообщения по всей переписке.</summary>
        public async Task<List<MessageHit>> SearchMessagesAsync(string query)
        {
            var hits = new List<MessageHit>();

            string term = (query ?? "").Trim();
            if (term.Length == 0) return hits;

            var payload = new Dictionary<string, object>
            {
                { "count", (long)MessageCount },
                { "query", term }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatSearch, payload);
            if (packet == null || packet.IsError) return hits;

            IList<object> result = MessagePack.List(packet.Map, "result");
            if (result == null) return hits;

            foreach (object item in result)
            {
                var entry = item as IDictionary<string, object>;
                if (entry == null) continue;

                IDictionary<string, object> message = MessagePack.Map(entry, "message");
                if (message == null) continue;

                // Чат с нулевым номером — сохранённые сообщения, их сервер
                // отдаёт отдельно и открывать по этому пути нечего.
                long chatId = MessagePack.Long(entry, "chatId");
                if (chatId == 0) continue;

                hits.Add(new MessageHit
                {
                    ChatId = chatId,
                    MessageId = MessagePack.String(message, "id"),
                    Text = MaxMessage.PreviewText(message),
                    Time = MessagePack.Long(message, "time"),
                    SenderId = MessagePack.Long(message, "sender")
                });
            }

            return hits;
        }

        /// <summary>PUBLIC_SEARCH: каналы и группы по всему MAX.</summary>
        public async Task<List<PublicHit>> SearchPublicAsync(string query)
        {
            var hits = new List<PublicHit>();

            string term = (query ?? "").Trim();
            if (term.Length == 0) return hits;

            var payload = new Dictionary<string, object>
            {
                { "type", "ALL" },
                { "count", (long)PublicCount },
                { "query", term }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.PublicSearch, payload);
            if (packet == null || packet.IsError) return hits;

            IList<object> result = MessagePack.List(packet.Map, "result");
            if (result == null) return hits;

            foreach (object item in result)
            {
                var entry = item as IDictionary<string, object>;
                if (entry == null) continue;

                IDictionary<string, object> map = MessagePack.Map(entry, "chat");
                if (map == null) continue;

                long chatId = MessagePack.Long(map, "id");
                if (chatId == 0) continue;

                var hit = new PublicHit
                {
                    ChatId = chatId,
                    Type = MessagePack.String(map, "type") ?? "CHAT",
                    Title = MessagePack.String(map, "title"),
                    AvatarUrl = MessagePack.String(map, "baseIconUrl")
                };

                // Подпись: ссылка вида @name, если она есть, иначе последнее
                // сообщение — так канал отличают от одноимённого.
                string link = MessagePack.String(map, "link");
                if (!string.IsNullOrEmpty(link))
                {
                    // Сервер шлёт то короткое имя, то полный адрес — показываем
                    // всегда короткое.
                    hit.Subtitle = Lovit.Core.Links.Tagged(link);
                }
                else
                {
                    hit.Subtitle = MaxMessage.PreviewText(MessagePack.Map(map, "lastMessage"));
                }

                hits.Add(hit);
            }

            return hits;
        }

        #endregion
    }
}
