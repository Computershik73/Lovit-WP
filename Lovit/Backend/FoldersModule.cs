using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Models;

namespace Lovit.Backend
{
    /// <summary>
    /// Папка чатов. Порт MAXFolder.
    ///
    /// Папка — это не список чатов, а правило отбора: набор признаков
    /// (непрочитанные, каналы, группы, контакты, боты) плюс поимённый список
    /// тех, кого включили вручную. Поэтому состав считается на месте, по тем
    /// же чатам, что уже лежат в памяти, а с сервера приходит только правило.
    /// </summary>
    public class MaxFolder
    {
        /// <summary>Признаки отбора. Числа взяты из Komet один в один.</summary>
        private const int FilterUnread = 0;
        private const int FilterChannel = 2;
        private const int FilterGroup = 3;
        private const int FilterContact = 8;
        private const int FilterNotContact = 9;
        private const int FilterBot = 10;

        public string FolderId { get; set; }
        public string Title { get; set; }

        /// <summary>Чаты, включённые поимённо.</summary>
        public HashSet<long> Include { get; set; }

        public List<int> Filters { get; set; }

        public MaxFolder()
        {
            Include = new HashSet<long>();
            Filters = new List<int>();
        }

        /// <summary>
        /// Папка «Все чаты».
        ///
        /// Определяется по идентификатору и по устройству самой папки, но не
        /// по названию: заголовки приходят с сервера переведёнными под язык
        /// устройства, и на испанском телефоне «Todos» мимо списка названий
        /// проходит. Папка без единого признака отбора и так означает «всё
        /// подряд» — этот признак от языка не зависит.
        /// </summary>
        public bool IsAllChats
        {
            get
            {
                if (FolderId == "all.chat.folder") return true;
                return Filters.Count == 0 && Include.Count == 0;
            }
        }

        /// <summary>
        /// Ровно один признак — непрочитанные, и никого вручную. Такая папка
        /// повторяет нашу вкладку «новые», и показывать обе незачем.
        /// </summary>
        public bool IsUnreadOnly
        {
            get
            {
                if (Include.Count > 0 || Filters.Count != 1) return false;
                return Filters[0] == FilterUnread;
            }
        }

        public static MaxFolder FromMap(IDictionary<string, object> map)
        {
            if (map == null) return null;

            var folder = new MaxFolder
            {
                FolderId = Text(map, "id"),
                Title = MessagePack.String(map, "title")
            };

            IList<object> include = MessagePack.List(map, "include");
            if (include != null)
            {
                foreach (object item in include)
                {
                    long id = ToLong(item);
                    if (id != 0) folder.Include.Add(id);
                }
            }

            IList<object> filters = MessagePack.List(map, "filters");
            if (filters != null)
            {
                foreach (object item in filters) folder.Filters.Add(FilterCode(item));
            }

            return folder;
        }

        /// <summary>
        /// Признак числом, каким бы его ни прислали: сервер шлёт их числами,
        /// но в старых записях попадаются и словами.
        /// </summary>
        private static int FilterCode(object raw)
        {
            if (raw is long) return (int)(long)raw;

            var name = raw as string;
            if (name == null) return -1;

            switch (name)
            {
                case "UNREAD": return FilterUnread;
                case "CHANNEL": return FilterChannel;
                case "GROUP":
                case "CHAT": return FilterGroup;
                case "CONTACT": return FilterContact;
                case "NOT_CONTACT": return FilterNotContact;
                case "BOT": return FilterBot;
            }

            int parsed;
            return int.TryParse(name, out parsed) ? parsed : -1;
        }

        public bool Matches(MaxChat chat, long currentUserId, HashSet<long> contactIds)
        {
            // Включённые вручную попадают в папку независимо от признаков.
            if (Include.Contains(chat.ChatId)) return true;
            if (Filters.Count == 0) return false;

            bool isDialog = chat.IsDialog;
            bool isBot = isDialog && chat.IsBot;

            // Собеседник диалога выводится из его номера — тем же способом,
            // каким номер и складывался.
            long peerId = isDialog ? (chat.ChatId ^ currentUserId) : 0;
            bool isSelf = isDialog && peerId == currentUserId;
            bool isContact = isDialog && !isSelf && contactIds.Contains(peerId);

            foreach (int filter in Filters)
            {
                switch (filter)
                {
                    case FilterUnread:
                        if (chat.UnreadCount > 0) return true;
                        break;
                    case FilterChannel:
                        if (chat.IsChannel) return true;
                        break;
                    case FilterGroup:
                        if (chat.Type == "CHAT" || chat.Type == "GROUP") return true;
                        break;
                    case FilterContact:
                        if (isDialog && !isBot && isContact) return true;
                        break;
                    case FilterNotContact:
                        if (isDialog && !isBot && !isSelf && !isContact) return true;
                        break;
                    case FilterBot:
                        if (isBot) return true;
                        break;
                }
            }

            return false;
        }

        private static string Text(IDictionary<string, object> map, string key)
        {
            string text = MessagePack.String(map, key);
            if (!string.IsNullOrEmpty(text)) return text;

            long number = MessagePack.Long(map, key);
            return number != 0 ? number.ToString() : "";
        }

        private static long ToLong(object value)
        {
            if (value is long) return (long)value;

            long parsed;
            return value != null && long.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }
    }

    /// <summary>Папки чатов. Порт MAXFoldersModule.</summary>
    public class FoldersModule
    {
        private static readonly FoldersModule _instance = new FoldersModule();
        public static FoldersModule Instance { get { return _instance; } }

        private List<MaxFolder> _folders = new List<MaxFolder>();

        /// <summary>Папки обновились — список чатов пересобирает вкладки.</summary>
        public event Action Changed;

        private FoldersModule()
        {
            // Сервер шлёт изменения сам — например, когда папки правят
            // с телефона.
            MaxApi.Instance.PushReceived += packet =>
            {
                if (packet.Opcode == Opcodes.NotifFolders) ApplyPayload(packet.Map);
            };
        }

        /// <summary>Папки в порядке, заданном сервером.</summary>
        public IList<MaxFolder> Folders { get { return _folders; } }

        /// <summary>
        /// FOLDERS_GET. Ошибки не показываются: папок у аккаунта может не
        /// быть вовсе, и это не повод тревожить пользователя.
        /// </summary>
        public async void Fetch()
        {
            // Под перехватом: метод без возврата, и сорвавшееся в нём унесло
            // бы приложение целиком.
            try
            {
                var payload = new Dictionary<string, object> { { "folderSync", 0L } };

                Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.FoldersGet, payload);
                if (packet == null || packet.IsError) return;

                ApplyPayload(packet.Map);
            }
            catch (Exception error)
            {
                Log.Fail("api", "папки", error);
            }
        }

        public void ApplyPayload(IDictionary<string, object> payload)
        {
            IList<object> raw = MessagePack.List(payload, "folders");
            if (raw == null) return;

            var folders = new List<MaxFolder>();
            foreach (object item in raw)
            {
                MaxFolder folder = MaxFolder.FromMap(item as IDictionary<string, object>);
                if (folder != null && !string.IsNullOrEmpty(folder.FolderId)) folders.Add(folder);
            }

            // Порядок задаётся отдельным списком идентификаторов: в самих
            // папках его нет, а показывать их надо так же, как в официальном
            // клиенте.
            IList<object> order = MessagePack.List(payload, "foldersOrder");
            if (order != null && order.Count > 0)
            {
                var positions = new Dictionary<string, int>();
                for (int i = 0; i < order.Count; i++)
                {
                    string key = order[i] == null ? "" : order[i].ToString();
                    if (!positions.ContainsKey(key)) positions[key] = i;
                }

                folders.Sort((first, second) =>
                {
                    int a, b;
                    bool hasA = positions.TryGetValue(first.FolderId, out a);
                    bool hasB = positions.TryGetValue(second.FolderId, out b);

                    // Неупомянутые уходят в конец.
                    if (!hasA && !hasB) return 0;
                    if (!hasA) return 1;
                    if (!hasB) return -1;
                    return a.CompareTo(b);
                });
            }

            _folders = folders;

            Action handler = Changed;
            if (handler != null) MaxApi.RunOnUi(handler);
        }

        public void Reset()
        {
            _folders = new List<MaxFolder>();
        }
    }
}
