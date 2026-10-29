using System;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core.Storage;
using Lovit.Models;

namespace Lovit.Core
{
    /// <summary>
    /// Уведомления о новых сообщениях, пока приложение открыто. Порт
    /// MAXNotifier из версии для iOS.
    ///
    /// Своих пушей у неофициального клиента нет — уведомляем о том, что
    /// пришло по нашему же соединению. Пока приложение закрыто, то же самое
    /// делает фоновый агент (`Lovit.Background.TileTask`), раз в полчаса.
    ///
    /// Молчат: чат, открытый на экране, чаты без звука, каналы, правки и
    /// свои сообщения с других устройств.
    /// </summary>
    public static class Notifier
    {
        /// <summary>Сколько ждать имя незнакомого отправителя, прежде чем уведомить без него.</summary>
        private static readonly TimeSpan NameWait = TimeSpan.FromSeconds(1.5);

        private static bool _started;

        /// <summary>Видно ли приложение. Ставит App по VisibilityChanged.</summary>
        public static bool IsVisible { get; set; }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            IsVisible = true;

            ChatsModule.Instance.MessageReceived += OnMessage;
            ChatsModule.Instance.Changed += OnChatsChanged;
        }

        public static void Forget(long chatId)
        {
            Toasts.Forget(chatId);
        }

        /// <summary>
        /// Всё, что видно в списке чатов, человек уже видел, — фоновому агенту
        /// о нём напоминать незачем.
        /// </summary>
        private static void OnChatsChanged()
        {
            long newest = 0;
            foreach (MaxChat chat in ChatsModule.Instance.Chats)
            {
                if (chat.LastMessageTime > newest) newest = chat.LastMessageTime;
            }
            if (newest > AppSettings.NotifiedUpTo) AppSettings.NotifiedUpTo = newest;
        }

        private static async void OnMessage(MaxMessage message)
        {
            ChatsModule chats = ChatsModule.Instance;

            if (message.Time > AppSettings.NotifiedUpTo) AppSettings.NotifiedUpTo = message.Time;

            if (!AppSettings.Notifications) return;

            // Своё — с другого устройства; ноль — служебное без автора.
            if (message.SenderId == 0 || message.SenderId == chats.CurrentUserId) return;

            // Тем же опкодом приходят правки, а «участник вышел» и прочее
            // служебное — не реплика.
            if (message.IsEdited) return;
            if (message.Attachments != null
                && message.Attachments.Exists(attachment => attachment.Type == "CONTROL")) return;

            MaxChat chat = chats.Chat(message.ChatId);
            if (chat != null && (chat.IsMuted || chat.IsChannel)) return;

            if (IsVisible && chats.VisibleChatId == message.ChatId) return;

            // Первое сообщение от незнакомого: карточку запросили вместе с
            // сообщением, чуть подождём — без имени уведомление мало что говорит.
            if (chats.Contact(message.SenderId) == null) await Task.Delay(NameWait);

            if (IsVisible && chats.VisibleChatId == message.ChatId) return;

            string title;
            string prefix = "";
            MaxContact sender = chats.Contact(message.SenderId);
            chat = chats.Chat(message.ChatId);

            if (chat == null || chat.IsDialog)
            {
                title = sender != null ? sender.DisplayName : "Новое сообщение";
            }
            else
            {
                // В группе заголовок — сам чат, а кто пишет — перед текстом.
                title = chats.Title(chat);
                if (sender != null) prefix = sender.DisplayName + ": ";
            }

            string text = AppSettings.NotificationText
                ? prefix + (string.IsNullOrEmpty(message.DisplayText) ? "Сообщение" : message.DisplayText)
                : "Новое сообщение";

            Toasts.Show(message.ChatId, title, text);
        }
    }
}
