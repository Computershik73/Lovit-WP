using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Lovit.Models;

namespace Lovit.Backend
{
    /// <summary>
    /// Чаты, контакты, присутствие и пуши. Порт MAXChatsModule.
    ///
    /// Кэш в памяти, без базы: список чатов приезжает целиком при каждом
    /// входе, и хранить его между запусками значит показывать заведомо
    /// устаревшее.
    /// </summary>
    public class ChatsModule
    {
        /// <summary>Сколько чатов просить одной страницей и сколько страниц.</summary>
        private const int PageSize = 40;
        private const int MaxPages = 10;

        private static readonly ChatsModule _instance = new ChatsModule();
        public static ChatsModule Instance { get { return _instance; } }

        private readonly Dictionary<long, MaxChat> _chats = new Dictionary<long, MaxChat>();
        private readonly Dictionary<long, MaxContact> _contacts = new Dictionary<long, MaxContact>();
        private readonly Dictionary<long, IDictionary<string, object>> _presence =
            new Dictionary<long, IDictionary<string, object>>();

        /// <summary>Кого уже спрашивали: иначе отрисовка спросит по разу на строку.</summary>
        private readonly HashSet<long> _requestedContacts = new HashSet<long>();

        /// <summary>
        /// Сообщения в чаты, которых ещё нет в списке: ждут, пока приедет сам
        /// чат (CHAT_INFO). Хранится последнее — по нему чат и встанет в
        /// списке.
        /// </summary>
        private readonly Dictionary<long, MaxMessage> _waitingMessages =
            new Dictionary<long, MaxMessage>();

        /// <summary>За какими чатами уже пошли — чтобы не спрашивать дважды.</summary>
        private readonly HashSet<long> _chatsBeingFetched = new HashSet<long>();

        private bool _contactsSynced;
        private bool _paginating;

        public long CurrentUserId { get; set; }

        /// <summary>Чем сервер объяснил последний отказ — его и показываем.</summary>
        public string LastError { get; private set; }

        /// <summary>Список изменился — экраны перечитывают его.</summary>
        public event Action Changed;

        /// <summary>Пришло новое сообщение — открытая переписка добавит его к себе.</summary>
        public event Action<MaxMessage> MessageReceived;

        /// <summary>
        /// Сообщения удалили — открытая переписка уберёт их у себя. Номер
        /// чата и список удалённых.
        /// </summary>
        public event Action<long, IList<string>> MessagesDeleted;

        /// <summary>
        /// Реакции на сообщение изменились: чат, сообщение и новые счётчики.
        /// Своей реакции здесь нет — пуш приходит и на чужие тоже.
        /// </summary>
        public event Action<long, string, IList<object>> ReactionsChanged;

        /// <summary>Кто-то печатает: chatId и имя.</summary>
        public event Action<long, string> Typing;

        private ChatsModule()
        {
            MaxApi.Instance.PushReceived += OnPush;
        }

        #region Кэш

        /// <summary>
        /// Чаты по времени последнего события, свежие сверху.
        ///
        /// Спрятанные сервером пропускаются: MAX помечает так подсобные чаты —
        /// в том числе те, что заводятся под групповой звонок. Технически это
        /// обычная группа, но в списке ей не место. Komet поступает так же
        /// (`status == 'HIDDEN'` → `in_list = 2`, а обычный список берёт
        /// только `in_list = 1`).
        /// </summary>
        public List<MaxChat> Chats
        {
            get
            {
                return _chats.Values
                    .Where(chat => !chat.IsHidden)
                    .OrderByDescending(chat => chat.LastEventTime > 0
                                               ? chat.LastEventTime
                                               : chat.LastMessageTime)
                    .ToList();
            }
        }

        public List<MaxContact> Contacts
        {
            get { return _contacts.Values.OrderBy(contact => contact.DisplayName).ToList(); }
        }

        public HashSet<long> ContactIds
        {
            get { return new HashSet<long>(_contacts.Keys); }
        }

        public MaxChat Chat(long chatId)
        {
            MaxChat chat;
            return _chats.TryGetValue(chatId, out chat) ? chat : null;
        }

        /// <summary>
        /// Сколько чатов ждут прочтения. Приглушённые не в счёт: их
        /// приглушают затем, чтобы они не напоминали о себе.
        /// </summary>
        public int UnreadChats
        {
            get
            {
                int count = 0;

                foreach (MaxChat chat in _chats.Values)
                {
                    if (chat.UnreadCount > 0 && !chat.IsMuted) count++;
                }

                return count;
            }
        }

        public MaxContact Contact(long contactId)
        {
            MaxContact contact;
            return _contacts.TryGetValue(contactId, out contact) ? contact : null;
        }

        #endregion

        #region Заголовки

        /// <summary>
        /// Заголовок чата. У диалога своего названия нет — берётся имя
        /// собеседника, а переписка с самим собой зовётся «Избранное».
        /// </summary>
        public string Title(MaxChat chat)
        {
            if (chat == null) return "";
            if (!string.IsNullOrEmpty(chat.Title)) return chat.Title;

            if (chat.IsDialog)
            {
                long otherId = chat.OtherParticipant(CurrentUserId);
                if (chat.ChatId == 0 || otherId == 0 || otherId == CurrentUserId) return "Избранное";

                MaxContact contact = Contact(otherId);
                if (contact != null) return contact.DisplayName;

                // Карточки ещё нет — просим её и пока показываем номер.
                EnsureContact(otherId);
                return "ID " + otherId;
            }

            return chat.IsChannel ? "Канал " + chat.ChatId : "Чат " + chat.ChatId;
        }

        /// <summary>Аватарка собеседника для диалога, значок чата — для остальных.</summary>
        public string AvatarUrl(MaxChat chat)
        {
            if (chat == null) return null;
            if (!string.IsNullOrEmpty(chat.IconUrl)) return chat.IconUrl;

            if (chat.IsDialog)
            {
                MaxContact contact = Contact(chat.OtherParticipant(CurrentUserId));
                if (contact != null) return contact.AvatarUrl;
            }

            return null;
        }

        /// <summary>
        /// Вторая строка шапки: «12 участников» у группы, «300 подписчиков»
        /// у канала, присутствие или номер — у диалога.
        /// </summary>
        public string Subtitle(MaxChat chat)
        {
            if (chat == null) return "";

            if (chat.IsDialog)
            {
                long otherId = chat.OtherParticipant(CurrentUserId);
                if (otherId == 0 || otherId == CurrentUserId) return "";

                string presence = PresenceText(Presence(otherId));
                if (!string.IsNullOrEmpty(presence)) return presence;

                MaxContact contact = Contact(otherId);
                return contact != null ? contact.PhoneText : "";
            }

            if (chat.ParticipantsCount <= 0) return "";

            return chat.ParticipantsCount + (chat.IsChannel ? " подписчиков" : " участников");
        }

        #endregion

        #region Разбор ответа на вход

        public void ApplyLoginPayload(IDictionary<string, object> payload)
        {
            IDictionary<string, object> profile = MessagePack.Map(payload, "profile");
            IDictionary<string, object> me = MessagePack.Map(profile, "contact");

            long userId = MessagePack.Long(me, "id");
            if (userId != 0) CurrentUserId = userId;

            IList<object> known = MessagePack.List(payload, "contacts");
            IList<object> rooms = MessagePack.List(payload, "chats");

            Log.Write("api", "вход принят: карточек " + (known == null ? 0 : known.Count)
                             + ", чатов " + (rooms == null ? 0 : rooms.Count));

            StoreContacts(known);
            StoreChats(rooms);
            ApplyConfig(MessagePack.Map(payload, "config"));

            Notify();

            // Папки приходят отдельным запросом, но именно здесь: раньше
            // сокет ещё не поднят, и запрос уходил в пустоту.
            FoldersModule.Instance.Fetch();

            var contacts = SyncAllContactsAsync();
            var pages = PaginateAsync(payload);
        }

        /// <summary>
        /// Тишина чатов лежит не в самих чатах, а в настройках: `config.chats`
        /// — это карта «номер чата → dontDisturbUntil».
        /// </summary>
        private void ApplyConfig(IDictionary<string, object> config)
        {
            IDictionary<string, object> chats = MessagePack.Map(config, "chats");
            if (chats == null) return;

            foreach (var pair in chats)
            {
                long chatId;
                if (!long.TryParse(pair.Key, out chatId)) continue;

                MaxChat chat = Chat(chatId);
                if (chat == null) continue;

                var entry = pair.Value as IDictionary<string, object>;
                chat.DontDisturbUntil = MessagePack.Long(entry, "dontDisturbUntil");
            }
        }

        /// <summary>
        /// Догружает оставшиеся чаты по маркеру из ответа на вход: за один
        /// раз сервер отдаёт только первую страницу.
        /// </summary>
        private async Task PaginateAsync(IDictionary<string, object> payload)
        {
            if (_paginating) return;

            long marker = MessagePack.Long(payload, "chatMarker");
            if (marker <= 0) return;

            _paginating = true;

            try
            {
                for (int page = 0; page < MaxPages && marker > 0; page++)
                {
                    var request = new Dictionary<string, object>
                    {
                        { "marker", marker },
                        { "count", (long)PageSize }
                    };

                    Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatsList, request);
                    if (packet == null || packet.IsError) return;

                    IList<object> chats = MessagePack.List(packet.Map, "chats");
                    StoreChats(chats);
                    Notify();

                    long next = MessagePack.Long(packet.Map, "marker");

                    // Тот же маркер или короткая страница означают конец:
                    // иначе цикл ходил бы по кругу.
                    if (next == 0 || next == marker || chats == null || chats.Count < PageSize) return;
                    marker = next;
                }
            }
            finally
            {
                _paginating = false;
            }
        }

        /// <summary>
        /// Весь адресный список отдельным запросом, раз за сессию.
        ///
        /// В ответе на вход приезжают только собеседники диалогов, а имена
        /// нужны ещё и участникам групп — иначе вместо них номера.
        /// </summary>
        private async Task SyncAllContactsAsync()
        {
            if (_contactsSynced) return;
            _contactsSynced = true;

            var payload = new Dictionary<string, object> { { "contactsSync", 0L } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ContactsGet, payload);

            if (packet == null)
            {
                Log.Write("api", "адресный список: сервер не ответил");
                return;
            }

            if (packet.IsError)
            {
                Log.Write("api", "адресный список: отказ {" + Log.Keys(packet.Map) + "}");
                return;
            }

            IList<object> list = MessagePack.List(packet.Map, "contacts");

            Log.Write("api", "адресный список: пришло "
                             + (list == null ? "ничего" : list.Count + " карточек"));

            StoreContacts(list);
            Notify();
        }

        private void StoreChats(IList<object> list)
        {
            if (list == null) return;

            foreach (object item in list)
            {
                MaxChat chat = MaxChat.FromMap(item as IDictionary<string, object>,
                                               CurrentUserId, _contacts);
                if (chat == null) continue;

                // У диалога заголовок приходит только из контактов; если
                // карточка ещё не загружена, сохраняем прежнее имя вместо
                // пустого.
                MaxChat existing = Chat(chat.ChatId);
                if (existing != null && string.IsNullOrEmpty(chat.Title))
                {
                    chat.Title = existing.Title;
                    chat.IconUrl = existing.IconUrl;
                }

                if (existing != null && chat.DontDisturbUntil == 0)
                {
                    chat.DontDisturbUntil = existing.DontDisturbUntil;
                }

                _chats[chat.ChatId] = chat;
            }
        }

        private void StoreContacts(IList<object> list)
        {
            if (list == null) return;

            bool added = false;
            bool spelt = false;

            foreach (object item in list)
            {
                MaxContact contact = MaxContact.FromMap(item as IDictionary<string, object>);
                if (contact == null || contact.ContactId == 0) continue;

                _contacts[contact.ContactId] = contact;
                added = true;

                // Жаловались на имена из вопросительных знаков. Первое такое
                // имя разбираем по знакам: так видно, потерялась ли кодировка
                // по дороге или сервер прислал именно это.
                if (!spelt && !string.IsNullOrEmpty(contact.Name) && contact.Name.Contains("?"))
                {
                    spelt = true;
                    Log.Write("api", "имя с вопросами у " + contact.ContactId
                                     + ": " + Log.Spell(contact.Name));
                }
            }

            // Приехавшее кладём на диск: в следующий раз имена будут в
            // списке сразу, а не через несколько секунд после чатов.
            if (added) Remember();
        }

        /// <summary>
        /// Отложить карточки на диск. Сам склад решает, не слишком ли часто
        /// его просят: карточки приезжают пачками по два-три десятка подряд.
        /// </summary>
        private void Remember()
        {
            var ignored = ContactsCache.SaveAsync(_contacts.Values);
        }

        /// <summary>
        /// Дописать карточки без оглядки на часы — перед уходом в сон.
        ///
        /// Обычное сохранение пропускает то, что пришло вслед за только
        /// что записанным, и последняя пачка могла бы не дойти до диска вовсе.
        /// </summary>
        public Task RememberNowAsync()
        {
            return ContactsCache.ForceSaveAsync(_contacts.Values);
        }

        /// <summary>
        /// Поднять карточки, отложенные в прошлый раз.
        ///
        /// Зовётся до соединения: список чатов приезжает за доли секунды, а
        /// карточки — за секунды, и без этого диалоги стоят в списке
        /// номерами вместо имён. Приехавшее с сервера потом перепишет всё
        /// поверх — здесь лежит не истина, а то, что было в прошлый раз.
        /// </summary>
        public async Task PrimeAsync()
        {
            if (_contacts.Count > 0) return;

            foreach (MaxContact contact in await ContactsCache.LoadAsync())
            {
                if (!_contacts.ContainsKey(contact.ContactId))
                {
                    _contacts[contact.ContactId] = contact;
                }
            }
        }

        #endregion

        #region Догрузка карточек

        /// <summary>
        /// Догружает карточку человека, о котором клиент узнал не из списка
        /// чатов, — автора пересылки или участника группы. Уже известные и
        /// уже запрошенные пропускаются, так что звать можно из отрисовки.
        /// </summary>
        public void EnsureContact(long contactId)
        {
            EnsureContacts(new List<long> { contactId });
        }

        public void EnsureContacts(IEnumerable<long> contactIds)
        {
            var missing = new List<object>();

            foreach (long id in contactIds)
            {
                if (id == 0 || id == CurrentUserId) continue;
                if (_contacts.ContainsKey(id)) continue;
                if (_requestedContacts.Contains(id)) continue;

                _requestedContacts.Add(id);
                missing.Add(id);
            }

            if (missing.Count == 0) return;

            var ignored = RequestContactsAsync(missing);
        }

        private async Task RequestContactsAsync(IList<object> contactIds)
        {
            var payload = new Dictionary<string, object> { { "contactIds", contactIds } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ContactInfo, payload);
            if (packet == null || packet.IsError) return;

            StoreContacts(MessagePack.List(packet.Map, "contacts"));
            Notify();
        }

        #endregion

        #region Диалоги

        /// <summary>
        /// Идентификатор диалога с человеком: существующий либо тот, который
        /// получится при первой отправке. Он выводится из двух участников —
        /// `accountId ^ contactId`, — поэтому переписку можно открыть до того,
        /// как она заведена на сервере.
        /// </summary>
        public long DialogChatId(long contactId)
        {
            foreach (MaxChat chat in _chats.Values)
            {
                if (!chat.IsDialog) continue;
                if (chat.OtherParticipant(CurrentUserId) == contactId) return chat.ChatId;
            }

            return CurrentUserId ^ contactId;
        }

        /// <summary>
        /// Диалог с человеком — существующий или ещё не начатый. Возвращает
        /// всегда: заготовка нужна, чтобы открыть переписку с тем, кому ещё
        /// ни разу не писали.
        /// </summary>
        public MaxChat DialogWithContact(long contactId)
        {
            long chatId = DialogChatId(contactId);

            MaxChat existing = Chat(chatId);
            if (existing != null) return existing;

            MaxContact contact = Contact(contactId);

            var dialog = new MaxChat
            {
                ChatId = chatId,
                Type = "DIALOG",
                Title = contact != null ? contact.Name : null,
                IconUrl = contact != null ? contact.AvatarUrl : null
            };

            dialog.Participants[contactId] = 0;
            dialog.Participants[CurrentUserId] = 0;

            return dialog;
        }

        #endregion

        #region Сведения о чате

        /// <summary>CHAT_INFO — описание, ссылка канала и число подписчиков.</summary>
        public async Task<IDictionary<string, object>> FetchInfoAsync(long chatId)
        {
            var payload = new Dictionary<string, object>
            {
                { "chatIds", new List<object> { chatId } }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatInfo, payload);
            if (packet == null || packet.IsError) return null;

            IList<object> chats = MessagePack.List(packet.Map, "chats");
            if (chats == null || chats.Count == 0) return null;

            var info = chats[0] as IDictionary<string, object>;
            if (info == null) return null;

            // Сведения полнее того, что лежит в списке, — но кладём их к
            // себе, только если чат и правда наш.
            //
            // Иначе от одного открытия чужой канал из поиска попадал в
            // список чатов, а членство здесь определяется именно наличием
            // в нём — и кнопка предлагала отписаться от того, на что мы не
            // подписаны. Версия для iOS сведения не кэширует вовсе
            // (`fetchInfoForChat`), их разбирает тот, кто спрашивал.
            if (Chat(chatId) != null)
            {
                StoreChats(new List<object> { info });

                MaxChat chat = Chat(chatId);
                if (chat != null)
                {
                    chat.Description = MessagePack.String(info, "description") ?? chat.Description;
                    chat.Link = MessagePack.String(info, "link") ?? chat.Link;
                }

                Notify();
            }

            return info;
        }

        /// <summary>
        /// Разобрать сведения в чат, не запоминая его. Нужно для чужого
        /// канала: показать его шапку надо, а попасть в список чатов он
        /// при этом не должен.
        /// </summary>
        public MaxChat Describe(IDictionary<string, object> info)
        {
            return MaxChat.FromMap(info, CurrentUserId, _contacts);
        }

        /// <summary>
        /// CHAT_MEMBERS — состав группы или канала постранично. В ответе
        /// приходят готовые карточки, они же пополняют кэш контактов.
        /// </summary>
        public async Task<MemberPage> FetchMembersAsync(long chatId, long marker)
        {
            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "type", "MEMBER" },
                { "marker", marker },
                { "count", 50L }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatMembers, payload);
            if (packet == null || packet.IsError) return new MemberPage();

            var page = new MemberPage { NextMarker = MessagePack.Long(packet.Map, "marker") };

            IList<object> members = MessagePack.List(packet.Map, "members");
            if (members != null)
            {
                foreach (object item in members)
                {
                    var entry = item as IDictionary<string, object>;
                    if (entry == null) continue;

                    long id = MessagePack.Long(entry, "userId");
                    if (id == 0) id = MessagePack.Long(entry, "id");
                    if (id != 0) page.MemberIds.Add(id);
                }
            }

            StoreContacts(MessagePack.List(packet.Map, "contacts"));
            Notify();

            return page;
        }

        public class MemberPage
        {
            public List<long> MemberIds { get; set; }
            public long NextMarker { get; set; }

            public MemberPage()
            {
                MemberIds = new List<long>();
            }
        }

        #endregion

        #region Каналы

        /// <summary>
        /// CHAT_JOIN — подписка на канал по его ссылке. Отдельного
        /// «подписаться по номеру чата» в MAX нет: вступают по ссылке,
        /// а её отдаёт CHAT_INFO.
        /// </summary>
        public async Task<bool> JoinByLinkAsync(string link)
        {
            if (string.IsNullOrEmpty(link)) return false;

            var payload = new Dictionary<string, object> { { "link", link } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatJoin, payload);
            if (packet == null || packet.IsError) return false;

            IDictionary<string, object> chat = MessagePack.Map(packet.Map, "chat");
            if (chat != null)
            {
                StoreChats(new List<object> { chat });
                Notify();
            }

            return true;
        }

        /// <summary>Подписка, когда известен только номер: сведения → ссылка → вступление.</summary>
        public async Task<bool> SubscribeToChannelAsync(long chatId)
        {
            MaxChat chat = Chat(chatId);
            string link = chat != null ? chat.Link : null;

            if (string.IsNullOrEmpty(link))
            {
                IDictionary<string, object> info = await FetchInfoAsync(chatId);
                link = MessagePack.String(info, "link");
            }

            return await JoinByLinkAsync(link);
        }

        /// <summary>
        /// CHAT_LEAVE — выйти из группы или отписаться от канала. Это один
        /// и тот же вызов: подписка на канал и членство в группе для сервера
        /// одно и то же.
        /// </summary>
        public async Task<bool> LeaveChatAsync(long chatId)
        {
            var payload = new Dictionary<string, object> { { "chatId", chatId } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatLeave, payload);
            if (packet == null || packet.IsError) return false;

            _chats.Remove(chatId);
            Notify();
            return true;
        }

        /// <summary>Подписан ли я на этот канал — по составу участников.</summary>
        public bool IsMember(MaxChat chat)
        {
            if (chat == null) return false;
            if (chat.Participants.ContainsKey(CurrentUserId)) return true;

            // Список участников у канала приходит куцым; для своих каналов
            // сервер всё равно кладёт чат в список — значит мы там есть.
            return _chats.ContainsKey(chat.ChatId) && chat.Participants.Count == 0;
        }

        #endregion

        #region Действия над чатом

        /// <summary>
        /// CHAT_UPDATE с `pinMessageId`. Ноль снимает закреп. `notify` —
        /// предупреждать ли участников: от этого зависит, придёт ли всем
        /// уведомление о новом закрепе.
        /// </summary>
        public async Task<bool> SetPinnedMessageAsync(long messageId, long chatId, bool notify)
        {
            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "notifyPin", notify },
                { "pinMessageId", messageId }
            };

            LastError = null;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatUpdate, payload);
            if (packet == null || packet.IsError)
            {
                LastError = packet == null
                    ? "Сервер не ответил"
                    : MessagePack.String(packet.Map, "localizedMessage")
                      ?? MessagePack.String(packet.Map, "message");

                Log.Write("chat", "закрепление отклонено: " + LastError);
                return false;
            }

            IDictionary<string, object> chat = MessagePack.Map(packet.Map, "chat");
            if (chat != null)
            {
                StoreChats(new List<object> { chat });
            }
            else
            {
                // Сервер иногда отвечает пустым: снимаем закреп у себя сами,
                // иначе полоска останется висеть до следующего входа.
                MaxChat known = Chat(chatId);
                if (known != null && messageId == 0)
                {
                    known.PinnedMessageId = 0;
                    known.PinnedMessageText = null;
                }
            }

            Notify();
            return true;
        }

        /// <summary>CHAT_CLEAR — очистить историю. `forAll` — у всех участников.</summary>
        public async Task<bool> ClearHistoryAsync(long chatId, bool forAll)
        {
            MaxChat chat = Chat(chatId);
            long lastEventTime = chat == null ? 0
                : (chat.LastEventTime > 0 ? chat.LastEventTime : chat.LastMessageTime);

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "lastEventTime", lastEventTime },
                { "forAll", forAll }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatClear, payload);
            return packet != null && !packet.IsError;
        }

        /// <summary>
        /// Уведомления чата. Это не CHAT_UPDATE, а CONFIG:
        /// `settings.chats.&lt;id&gt;.dontDisturbUntil`. 0 — включить,
        /// -1 — выключить навсегда.
        /// </summary>
        public async Task<bool> SetMuteUntilAsync(long until, long chatId)
        {
            // Ключ чата в настройках — строка: MessagePack различает числовые
            // и строковые ключи, а сервер ждёт именно строку.
            var chatSettings = new Dictionary<string, object>
            {
                { chatId.ToString(), new Dictionary<string, object> { { "dontDisturbUntil", until } } }
            };

            var payload = new Dictionary<string, object>
            {
                { "settings", new Dictionary<string, object> { { "chats", chatSettings } } }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.Config, payload);
            if (packet == null || packet.IsError) return false;

            MaxChat chat = Chat(chatId);
            if (chat != null) chat.DontDisturbUntil = until;

            Notify();
            return true;
        }

        /// <summary>CHAT_MEMBERS_UPDATE — `operation` это `add` или `remove`.</summary>
        public async Task<bool> UpdateMembersAsync(IList<object> userIds, long chatId, string operation)
        {
            if (userIds == null || userIds.Count == 0) return true;

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "userIds", userIds },
                { "showHistory", true },
                { "operation", operation }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatMembersUpdate, payload);
            return packet != null && !packet.IsError;
        }

        /// <summary>
        /// Создание группы. Это не отдельный опкод, а MSG_SEND с вложением
        /// CONTROL и событием `new` — так же, как в оригинальном клиенте.
        /// </summary>
        public async Task<MaxChat> CreateGroupAsync(string title, IList<object> userIds)
        {
            var control = new Dictionary<string, object>
            {
                { "_type", "CONTROL" },
                { "event", "new" },
                { "chatType", "CHAT" },
                { "title", title ?? "" },
                { "userIds", userIds ?? new List<object>() }
            };

            var message = new Dictionary<string, object>
            {
                { "cid", Core.UnixTime.Now },
                { "attaches", new List<object> { control } }
            };

            var payload = new Dictionary<string, object>
            {
                { "message", message },
                { "notify", true }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgSend, payload);
            if (packet == null || packet.IsError) return null;

            IDictionary<string, object> chat = MessagePack.Map(packet.Map, "chat");
            if (chat == null) return null;

            StoreChats(new List<object> { chat });
            Notify();

            return Chat(MessagePack.Long(chat, "id"));
        }

        #endregion

        #region Действия над контактом

        /// <summary>CONTACT_UPDATE с действием ADD / REMOVE / BLOCK / UNBLOCK.</summary>
        public async Task<bool> UpdateContactAsync(long contactId, string action, string firstName)
        {
            var payload = new Dictionary<string, object>
            {
                { "contactId", contactId },
                { "action", action }
            };

            if (!string.IsNullOrEmpty(firstName)) payload["firstName"] = firstName;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ContactUpdate, payload);
            if (packet == null || packet.IsError) return false;

            // В ответе приезжает свежая карточка — кладём её в кэш, чтобы имя
            // и признак «в контактах» обновились без отдельного запроса.
            IDictionary<string, object> contact = MessagePack.Map(packet.Map, "contact");
            if (contact != null)
            {
                StoreContacts(new List<object> { contact });
                Notify();
            }

            return true;
        }

        /// <summary>Список заблокированных, одной страницей.</summary>
        public async Task<HashSet<long>> FetchBlockedAsync()
        {
            var payload = new Dictionary<string, object>
            {
                { "status", "BLOCKED" },
                { "count", 100L },

                // Именно `from`, а не `marker`: страницы списка контактов
                // отсчитываются не так, как страницы чатов, и под чужим
                // именем начало списка сервер не понимает.
                { "from", 0L }
            };

            var blocked = new HashSet<long>();

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ContactList, payload);
            if (packet == null || packet.IsError) return blocked;

            IList<object> contacts = MessagePack.List(packet.Map, "contacts");
            if (contacts == null) return blocked;

            StoreContacts(contacts);

            foreach (object item in contacts)
            {
                long id = MessagePack.Long(item as IDictionary<string, object>, "id");
                if (id != 0) blocked.Add(id);
            }

            return blocked;
        }

        #endregion

        #region Присутствие

        public IDictionary<string, object> Presence(long contactId)
        {
            IDictionary<string, object> entry;
            return _presence.TryGetValue(contactId, out entry) ? entry : null;
        }

        /// <summary>
        /// «В сети», «был(-а) 5 мин назад» — из записи присутствия. `status`
        /// 1 — в сети, 2 и 3 — недавно, иначе считаем по `seen` (он в
        /// секундах). Пустая строка, если сказать нечего.
        /// </summary>
        public static string PresenceText(IDictionary<string, object> presence)
        {
            if (presence == null) return "";

            long status = MessagePack.Long(presence, "status");
            if (status == 1) return "В сети";
            if (status == 2 || status == 3) return "Был(-а) недавно";

            long seen = MessagePack.Long(presence, "seen");
            if (seen <= 0) return "";

            DateTime moment = Core.UnixTime.ToLocal(seen * 1000);
            TimeSpan ago = DateTime.Now - moment;

            if (ago.TotalMinutes < 1) return "Только что";
            if (ago.TotalMinutes < 60) return "Был(-а) " + (int)ago.TotalMinutes + " мин назад";
            if (ago.TotalHours < 24) return "Был(-а) " + (int)ago.TotalHours + " ч назад";
            if (moment.Date == DateTime.Now.Date.AddDays(-1)) return "Был(-а) вчера";

            return "Был(-а) " + moment.ToString("d MMMM");
        }

        public async Task FetchPresenceAsync(IList<object> contactIds)
        {
            if (contactIds == null || contactIds.Count == 0) return;

            var payload = new Dictionary<string, object> { { "contactIds", contactIds } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ContactPresence, payload);
            if (packet == null || packet.IsError) return;

            StorePresence(MessagePack.Map(packet.Map, "presence"));
            Notify();
        }

        private void StorePresence(IDictionary<string, object> map)
        {
            if (map == null) return;

            foreach (var pair in map)
            {
                long id;
                if (!long.TryParse(pair.Key, out id)) continue;

                var entry = pair.Value as IDictionary<string, object>;
                if (entry != null) _presence[id] = entry;
            }
        }

        #endregion

        #region Чтение и отметки

        /// <summary>
        /// Обновляет строку списка по сообщению, которое клиент увидел сам.
        /// Полагаться только на пуши нельзя: пока их нет, список показывал бы
        /// то, что приехало при входе.
        /// </summary>
        public void NoteMessage(MaxMessage message)
        {
            if (message == null) return;

            MaxChat chat = Chat(message.ChatId);
            if (chat == null) return;

            chat.LastMessageTime = message.Time;
            chat.LastMessageText = message.DisplayText;
            chat.LastMessageSenderId = message.SenderId;
            chat.LastEventTime = message.Time;

            Notify();
        }

        /// <summary>
        /// CHAT_MARK. Не отправляется в режиме «не отмечать прочитанным».
        ///
        /// Номер сообщения уходит числом, а не строкой. Разница не косметика:
        /// на строку сервер отвечает обычным успехом, но отметку не ставит.
        /// Со стороны это выглядело так, будто чат прочитан только у нас —
        /// счётчик в списке пропадал, а при первом же обновлении сведений о
        /// чате возвращался, потому что на сервере он и не думал меняться.
        /// Числом его шлют и Komet (chats.dart, `msgIdNum`), и версия для iOS
        /// (`markChatRead:`), и обе пропускают запрос, если число не вышло.
        /// </summary>
        public async Task MarkReadAsync(long chatId, string messageId, long time)
        {
            if (time == 0) return;

            MaxChat chat = Chat(chatId);
            if (chat == null) return;

            long numericId;
            bool numeric = long.TryParse(messageId, out numericId) && numericId != 0;

            // Счётчик гасим сразу, не дожидаясь ответа: возвращение в список
            // не должно ждать сети. Если отметка не дойдёт, сервер вернёт
            // своё число сам, и оно снова станет видно.
            if (chat.UnreadCount != 0)
            {
                chat.UnreadCount = 0;
                Notify();
            }

            if (AppSettings.AntiRead || !numeric)
            {
                if (!numeric)
                {
                    Log.Write("chat", "чат " + chatId + ": отметку не шлём, номер «"
                                      + messageId + "» не число");
                }
                return;
            }

            var payload = new Dictionary<string, object>
            {
                { "type", "READ_MESSAGE" },
                { "chatId", chatId },
                { "messageId", numericId },
                { "mark", time }
            };

            await MaxApi.Instance.RequestAsync(Opcodes.ChatMark, payload);
        }

        /// <summary>CHAT_SUBSCRIBE — подписка на обновления открытого чата.</summary>
        public Task SubscribeAsync(long chatId, bool subscribe)
        {
            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "subscribe", subscribe }
            };

            // Ответа на подписку сервер не шлёт — и ждать его незачем.
            MaxApi.Instance.Post(Opcodes.ChatSubscribe, payload);

            return Task.FromResult(true);
        }

        #endregion

        #region Пуши

        private void OnPush(Packet packet)
        {
            switch (packet.Opcode)
            {
                case Opcodes.NotifMessage:
                    HandleMessage(packet);
                    return;

                case Opcodes.NotifChat:
                    IList<object> many = MessagePack.List(packet.Map, "chats");
                    StoreChats(many);

                    // Одиночный чат приходит без списка — например, когда
                    // сменился закреп.
                    IDictionary<string, object> single = MessagePack.Map(packet.Map, "chat");
                    if (single != null) StoreChats(new List<object> { single });

                    // Сервер мог прислать новый чат сам, раньше нашего
                    // ответа на CHAT_INFO, — тогда ждущее сообщение
                    // прикладываем сейчас.
                    if (many != null)
                    {
                        foreach (object item in many)
                        {
                            var map = item as IDictionary<string, object>;
                            if (map != null) ApplyWaitingMessage(MessagePack.Long(map, "id"));
                        }
                    }
                    if (single != null) ApplyWaitingMessage(MessagePack.Long(single, "id"));

                    Notify();
                    return;

                case Opcodes.NotifContact:
                    StoreContacts(MessagePack.List(packet.Map, "contacts"));
                    Notify();
                    return;

                case Opcodes.NotifPresence:
                    StorePresence(MessagePack.Map(packet.Map, "presence"));
                    Notify();
                    return;

                case Opcodes.NotifConfig:
                    ApplyConfig(MessagePack.Map(packet.Map, "settings"));
                    Notify();
                    return;

                case Opcodes.NotifMark:
                    HandleMark(packet);
                    return;

                case Opcodes.NotifTyping:
                    HandleTyping(packet);
                    return;

                case Opcodes.NotifMsgDelete:
                    HandleMessageDelete(packet);
                    return;

                case Opcodes.NotifMsgReactions:
                    HandleReactions(packet);
                    return;
            }
        }

        /// <summary>
        /// Сообщение удалили — у себя или у всех. Порт _handleNotifMsgDelete
        /// из Komet; в версии для iOS этот пуш не разбирается вовсе, и
        /// удалённое остаётся на экране до перезахода в чат.
        ///
        /// Номер чата приходит двумя способами: целым чатом в `chat` — тогда
        /// заодно обновились и его сведения — либо просто числом в `chatId`.
        /// </summary>
        private void HandleMessageDelete(Packet packet)
        {
            long chatId;

            IDictionary<string, object> chatMap = MessagePack.Map(packet.Map, "chat");
            if (chatMap != null)
            {
                chatId = MessagePack.Long(chatMap, "id");
                StoreChats(new List<object> { chatMap });
            }
            else
            {
                chatId = MessagePack.Long(packet.Map, "chatId");

                // Ноль — это «Избранное», а не отсутствие номера: отличаем
                // по самому ключу.
                if (chatId == 0 && !packet.Map.ContainsKey("chatId")) return;
            }

            IList<object> raw = MessagePack.List(packet.Map, "messageIds");
            if (raw == null) return;

            var ids = new List<string>();
            foreach (object item in raw)
            {
                // Номера сообщений ходят и строкой, и числом.
                string id = item as string ?? (item != null ? item.ToString() : null);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }

            if (ids.Count == 0) return;

            Action<long, IList<string>> handler = MessagesDeleted;
            if (handler != null) handler(chatId, ids);

            Notify();
        }

        /// <summary>
        /// Реакции на сообщение изменились. Пересчитывать нечего: сервер
        /// присылает готовые счётчики — остаётся отдать их открытому чату.
        /// </summary>
        private void HandleReactions(Packet packet)
        {
            long chatId = MessagePack.Long(packet.Map, "chatId");

            // Номер сообщения ходит и числом, и строкой — как в удалении.
            object raw;
            packet.Map.TryGetValue("messageId", out raw);

            string messageId = raw != null ? raw.ToString() : null;
            if (string.IsNullOrEmpty(messageId)) return;

            Action<long, string, IList<object>> handler = ReactionsChanged;
            if (handler == null) return;

            handler(chatId, messageId, MessagePack.List(packet.Map, "counters"));
        }

        private void HandleMessage(Packet packet)
        {
            long chatId = MessagePack.Long(packet.Map, "chatId");
            IDictionary<string, object> raw = MessagePack.Map(packet.Map, "message");

            MaxMessage message = MaxMessage.FromMap(raw, chatId);
            if (message == null) return;

            MaxChat chat = Chat(chatId);
            if (chat != null && message.SenderId != CurrentUserId) chat.UnreadCount++;

            EnsureContact(message.SenderId);

            if (chat == null)
            {
                // Сообщение в чат, которого ещё нет в списке, — первое от
                // нового собеседника. Раньше оно просто терялось: NoteMessage
                // без чата ничего не делает, и в списке не появлялось ни
                // строки. Чат надо сперва запросить, а когда приедет —
                // приложить к нему это сообщение, иначе он встанет в список
                // без времени, то есть в самый низ.
                //
                // Так делает Komet (notifMessage в chats.dart), и так же
                // сделано в версии для iOS.
                _waitingMessages[chatId] = message;

                if (!_chatsBeingFetched.Contains(chatId))
                {
                    _chatsBeingFetched.Add(chatId);
                    Log.Write("chat", "сообщение в неизвестный чат " + chatId + " — запрашиваю его");
                    var ignored = FetchUnknownChatAsync(chatId);
                }
            }
            else
            {
                NoteMessage(message);
            }

            Action<MaxMessage> handler = MessageReceived;
            if (handler != null) handler(message);
        }

        /// <summary>
        /// Запросить чат, в который пришло сообщение, а его нет в списке.
        ///
        /// В список кладётся, только если чат наш: присутствие в нём здесь и
        /// значит членство (по нему решается, предлагать ли «выйти»), а
        /// сообщение может прийти и из чужого канала, открытого из поиска, —
        /// пока он открыт, сервер шлёт его посты. Так чужой канал однажды и
        /// попадал в список (см. FetchInfoAsync).
        /// </summary>
        private async Task FetchUnknownChatAsync(long chatId)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "chatIds", new List<object> { chatId } }
                };

                Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatInfo, payload);

                IList<object> chats = packet == null || packet.IsError
                    ? null
                    : MessagePack.List(packet.Map, "chats");

                if (chats == null || chats.Count == 0)
                {
                    Log.Write("chat", "чат " + chatId + " не выдали");
                    _waitingMessages.Remove(chatId);
                    return;
                }

                var ours = new List<object>();
                foreach (object item in chats)
                {
                    var map = item as IDictionary<string, object>;
                    MaxChat probe = map == null ? null : MaxChat.FromMap(map, CurrentUserId, _contacts);

                    if (ChatIsOurs(probe)) ours.Add(item);
                    else if (probe != null)
                        Log.Write("chat", "чат " + probe.ChatId + " не наш (" + probe.Type
                                          + ") — в список не кладём");
                }

                if (ours.Count == 0)
                {
                    _waitingMessages.Remove(chatId);
                    return;
                }

                StoreChats(ours);
                ApplyWaitingMessage(chatId);
                Notify();
            }
            catch (Exception error)
            {
                // async void тут не годился бы: исключение из него уронило бы
                // приложение. Поэтому Task и перехват — неудача запроса не
                // повод падать, следующее сообщение попробует снова.
                Log.Write("chat", "чат " + chatId + " не запросился: " + error.Message);
            }
            finally
            {
                _chatsBeingFetched.Remove(chatId);
            }
        }

        /// <summary>
        /// Наш ли чат. Диалог — всегда: кто-то пишет нам лично. Группа — если
        /// мы среди участников; когда их не прислали, верим самому сообщению.
        /// Канал — никогда: подписки приходят списком чатов и NOTIF_CHAT, а
        /// сообщение из канала почти наверняка значит «открыли из поиска».
        /// </summary>
        private bool ChatIsOurs(MaxChat chat)
        {
            if (chat == null) return false;
            if (chat.IsDialog) return true;
            if (chat.IsChannel) return false;

            if (chat.Participants == null || chat.Participants.Count == 0) return true;
            return chat.Participants.ContainsKey(CurrentUserId);
        }

        /// <summary>
        /// Чат приехал — приложить к нему сообщение, из-за которого за ним
        /// ходили.
        ///
        /// Непрочитанное берётся из самого чата: он запрошен уже после
        /// сообщения, и его счётчик это сообщение учитывает. Прибавить единицу
        /// сверху значило бы посчитать его дважды. Ноль при чужом сообщении —
        /// значит, счётчик устарел: хотя бы одно непрочитанное там есть.
        /// </summary>
        private void ApplyWaitingMessage(long chatId)
        {
            MaxMessage message;
            if (!_waitingMessages.TryGetValue(chatId, out message)) return;
            _waitingMessages.Remove(chatId);

            MaxChat chat = Chat(chatId);
            if (chat == null) return;

            // Сервер мог отдать чат уже с более свежим сообщением — тогда
            // наше старше, и откатывать чат назад незачем.
            if (message.Time < chat.LastMessageTime) return;

            NoteMessage(message);

            if (chat.UnreadCount == 0 && message.SenderId != CurrentUserId) chat.UnreadCount = 1;

            Log.Write("chat", "чат " + chatId + " добавлен в список");
        }

        /// <summary>
        /// Отметка о прочтении приходит и на свои сообщения — тогда сервер
        /// сообщает, что счётчик непрочитанного пора обнулить: чат прочитали
        /// с другого устройства.
        /// </summary>
        private void HandleMark(Packet packet)
        {
            long chatId = MessagePack.Long(packet.Map, "chatId");
            string type = MessagePack.String(packet.Map, "type");

            MaxChat chat = Chat(chatId);
            if (chat == null || type != "READ_MESSAGE") return;

            chat.UnreadCount = 0;
            Notify();
        }

        private void HandleTyping(Packet packet)
        {
            long chatId = MessagePack.Long(packet.Map, "chatId");
            long userId = MessagePack.Long(packet.Map, "userId");
            if (chatId == 0 || userId == CurrentUserId) return;

            MaxContact contact = Contact(userId);
            string name = contact != null ? contact.DisplayName : "Собеседник";

            Action<long, string> handler = Typing;
            if (handler != null) handler(chatId, name);
        }

        #endregion

        public void Reset()
        {
            _chats.Clear();
            _contacts.Clear();
            _presence.Clear();
            _requestedContacts.Clear();
            _waitingMessages.Clear();
            _chatsBeingFetched.Clear();

            _contactsSynced = false;
            CurrentUserId = 0;

            FoldersModule.Instance.Reset();
            Notify();
        }

        private void Notify()
        {
            // Плитка узнаёт о непрочитанном оттуда же, откуда и список
            // чатов: из любого их изменения. Сама она лишнего не сделает —
            // ставит число, только когда оно другое.
            LiveTile.Show(UnreadChats);

            Action handler = Changed;
            if (handler != null) MaxApi.RunOnUi(handler);
        }
    }
}
