using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lovit.Backend;
using Lovit.Models;
using Lovit.Ui;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Выбор чата для пересылки. Порт MAXChatPickerViewController.
    /// </summary>
    public sealed partial class ChatPickerPage : Page
    {
        /// <summary>Что пересылаем: сообщение и чат, откуда оно взято.</summary>
        public class Target
        {
            public long SourceChatId { get; set; }
            public string MessageId { get; set; }
        }

        private readonly ObservableCollection<ListRow> _rows = new ObservableCollection<ListRow>();
        private readonly List<MaxChat> _shown = new List<MaxChat>();

        private Target _target;
        private bool _sending;

        public ChatPickerPage()
        {
            InitializeComponent();
            Chats.ItemsSource = _rows;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _target = e.Parameter as Target;
            if (_target == null)
            {
                if (Frame.CanGoBack) Frame.GoBack();
                return;
            }

            Reload();
        }

        /// <summary>
        /// Чаты, куда вообще можно писать. Чужие каналы отсеиваются сразу:
        /// туда всё равно нельзя, и предлагать их значило бы обещать
        /// невозможное.
        /// </summary>
        private void Reload()
        {
            ChatsModule chats = ChatsModule.Instance;
            string query = (QueryBox.Text ?? "").Trim().ToLower();

            _shown.Clear();

            foreach (MaxChat chat in chats.Chats)
            {
                if (!chat.CanWrite(chats.CurrentUserId)) continue;

                if (query.Length > 0)
                {
                    string title = (chats.Title(chat) ?? "").ToLower();
                    if (!title.Contains(query)) continue;
                }

                _shown.Add(chat);
            }

            _rows.Clear();
            foreach (MaxChat chat in _shown) _rows.Add(ListRows.FromChat(chat));

            EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnQueryChanged(object sender, TextChangedEventArgs e)
        {
            Reload();
        }

        private async void OnChatClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as ListRow;
            if (row == null || _target == null || _sending) return;

            _sending = true;
            Progress.Visibility = Visibility.Visible;

            bool done = await MessagesModule.Instance.ForwardAsync(
                _target.MessageId, _target.SourceChatId, row.Id);

            Progress.Visibility = Visibility.Collapsed;
            _sending = false;

            if (!done)
            {
                var dialog = new MessageDialog("Не удалось переслать сообщение.");
                await dialog.ShowAsync();
                return;
            }

            // Открываем чат, куда переслали: иначе после выбора экран просто
            // возвращается назад, и понять, ушло ли что-нибудь, нельзя.
            //
            // Сам выбор из истории при этом убирается — назад из нового чата
            // человек ждёт возвращения к переписке, а не к списку, который
            // уже сделал своё дело.
            Frame.Navigate(typeof(ChatPage), new ChatPage.Target { ChatId = row.Id });

            if (Frame.BackStack.Count > 0) Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
        }
    }
}
