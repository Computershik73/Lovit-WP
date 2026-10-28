using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Models;
using Lovit.Ui;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>Сведения о чате: состав, уведомления, действия. Порт MAXChatInfoViewController.</summary>
    public sealed partial class ChatInfoPage : Page
    {
        private readonly List<ListRow> _members = new List<ListRow>();

        private long _chatId;
        private MaxChat _chat;
        private long _marker;
        private bool _loading;

        public ChatInfoPage()
        {
            InitializeComponent();
            MembersList.ItemsSource = _members;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _chatId = e.Parameter is long ? (long)e.Parameter : 0;
            _chat = ChatsModule.Instance.Chat(_chatId);

            Show();

            // Сведения полнее того, что лежит в списке: описание и ссылка
            // приходят только отдельным запросом.
            await ChatsModule.Instance.FetchInfoAsync(_chatId);
            _chat = ChatsModule.Instance.Chat(_chatId) ?? _chat;
            Show();

            if (_chat != null && !_chat.IsDialog) await LoadMembersAsync();
        }

        private void Show()
        {
            if (_chat == null) return;

            ChatsModule chats = ChatsModule.Instance;

            TitleText.Text = chats.Title(_chat);
            SubtitleText.Text = chats.Subtitle(_chat);

            LinkText.Text = Lovit.Core.Links.Tagged(_chat.Link);
            LinkText.Visibility = string.IsNullOrEmpty(_chat.Link)
                ? Visibility.Collapsed
                : Visibility.Visible;

            DescriptionText.Text = _chat.Description ?? "";
            DescriptionText.Visibility = string.IsNullOrEmpty(_chat.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;

            MuteSwitch.IsOn = !_chat.IsMuted;

            PinnedButton.Visibility = _chat.PinnedMessageId != 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            LeaveButton.Content = _chat.IsChannel ? "отписаться от канала" : "выйти из группы";
            LeaveButton.Visibility = _chat.IsDialog ? Visibility.Collapsed : Visibility.Visible;

            MembersHeader.Text = _chat.IsChannel ? "подписчики" : "участники";
            MembersHeader.Visibility = _chat.IsDialog ? Visibility.Collapsed : Visibility.Visible;
        }

        #region Участники

        private async Task LoadMembersAsync()
        {
            if (_loading) return;

            _loading = true;
            Progress.Visibility = Visibility.Visible;

            ChatsModule.MemberPage page = await ChatsModule.Instance.FetchMembersAsync(_chatId, _marker);

            Progress.Visibility = Visibility.Collapsed;
            _loading = false;

            foreach (long id in page.MemberIds)
            {
                MaxContact contact = ChatsModule.Instance.Contact(id);

                _members.Add(new ListRow
                {
                    Id = id,
                    Title = contact != null ? contact.DisplayName : "ID " + id,
                    Preview = contact != null ? contact.PhoneText : ""
                });
            }

            _marker = page.NextMarker;

            MembersList.ItemsSource = null;
            MembersList.ItemsSource = _members;

            // Ноль в маркере означает конец списка: дальше просить нечего.
            MoreButton.Visibility = _marker != 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnMore(object sender, RoutedEventArgs e)
        {
            await LoadMembersAsync();
        }

        private void OnMemberTapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as ListRow : null;
            if (row == null) return;

            Frame.Navigate(typeof(ContactPage), row.Id);
        }

        #endregion

        #region Действия

        private async void OnMuteToggled(object sender, RoutedEventArgs e)
        {
            if (_chat == null) return;

            bool muted = !MuteSwitch.IsOn;
            if (muted == _chat.IsMuted) return;

            // -1 означает «молчать всегда», ноль — вернуть уведомления.
            await ChatsModule.Instance.SetMuteUntilAsync(muted ? -1 : 0, _chatId);
        }

        private async void OnUnpin(object sender, RoutedEventArgs e)
        {
            await ChatsModule.Instance.SetPinnedMessageAsync(0, _chatId, false);

            _chat = ChatsModule.Instance.Chat(_chatId) ?? _chat;
            Show();
        }

        private async void OnClear(object sender, RoutedEventArgs e)
        {
            if (!await Confirm("Очистить историю?", "Сообщения пропадут только у вас.")) return;

            await ChatsModule.Instance.ClearHistoryAsync(_chatId, false);
        }

        private async void OnLeave(object sender, RoutedEventArgs e)
        {
            string question = _chat != null && _chat.IsChannel
                ? "Отписаться от канала?"
                : "Выйти из группы?";

            if (!await Confirm(question, "Чат пропадёт из списка.")) return;

            if (!await ChatsModule.Instance.LeaveChatAsync(_chatId)) return;

            // Возвращаться в переписку, из которой вышли, некуда.
            Frame.Navigate(typeof(MainPage));
            Frame.BackStack.Clear();
        }

        /// <summary>
        /// Подтверждение для того, что не отменить. Порт вопросов из
        /// iOS-версии — там это UIAlertView с двумя кнопками.
        /// </summary>
        private static async Task<bool> Confirm(string title, string text)
        {
            var dialog = new MessageDialog(text, title);
            bool answer = false;

            dialog.Commands.Add(new UICommand("да", command => { answer = true; }));
            dialog.Commands.Add(new UICommand("отмена"));

            await dialog.ShowAsync();
            return answer;
        }

        #endregion
    }
}
