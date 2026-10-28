using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>Карточка человека. Порт MAXContactViewController.</summary>
    public sealed partial class ContactPage : Page
    {
        private long _contactId;
        private MaxContact _contact;
        private bool _blocked;

        public ContactPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _contactId = e.Parameter is long ? (long)e.Parameter : 0;
            _contact = ChatsModule.Instance.Contact(_contactId);

            Show();

            // Присутствие спрашиваем отдельно: в списке контактов его нет,
            // а «в сети» — первое, что хочется знать о собеседнике.
            await ChatsModule.Instance.FetchPresenceAsync(new List<object> { _contactId });

            _contact = ChatsModule.Instance.Contact(_contactId) ?? _contact;
            Show();

            await LoadBlockedAsync();
        }

        private void Show()
        {
            if (_contact == null)
            {
                NameText.Text = "ID " + _contactId;
                return;
            }

            NameText.Text = _contact.DisplayName;
            PhoneText.Text = _contact.PhoneText;

            PresenceText.Text = ChatsModule.PresenceText(ChatsModule.Instance.Presence(_contactId));

            DescriptionText.Text = _contact.Description ?? "";
            DescriptionText.Visibility = string.IsNullOrEmpty(_contact.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // «В контактах» видно по записи CUSTOM в `names` — это имя,
            // которое дали человеку мы, а не он себе. Так определяет и
            // версия для iOS (`isSavedContact`). Прежняя проверка искала
            // признак CONTACT в `options`, которого сервер не присылает.
            bool known = _contact.IsSavedContact;

            SavedText.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
            ContactButton.Content = known ? "убрать из контактов" : "добавить в контакты";
        }

        private async Task LoadBlockedAsync()
        {
            Progress.Visibility = Visibility.Visible;

            HashSet<long> blocked = await ChatsModule.Instance.FetchBlockedAsync();

            Progress.Visibility = Visibility.Collapsed;

            _blocked = blocked.Contains(_contactId);
            BlockButton.Content = _blocked ? "разблокировать" : "заблокировать";
        }

        private void OnWrite(object sender, RoutedEventArgs e)
        {
            // Диалог открывается даже если переписки ещё нет: его номер
            // выводится из двух участников, а на сервере он заведётся с
            // первым сообщением.
            MaxChat dialog = ChatsModule.Instance.DialogWithContact(_contactId);

            Frame.Navigate(typeof(ChatPage), new ChatPage.Target
            {
                ChatId = dialog.ChatId,
                Stub = dialog
            });
        }

        private async void OnToggleContact(object sender, RoutedEventArgs e)
        {
            if (_contact == null) return;

            ContactButton.IsEnabled = false;

            bool known = _contact.IsSavedContact;
            string action = known ? "REMOVE" : "ADD";

            await ChatsModule.Instance.UpdateContactAsync(_contactId, action,
                known ? null : _contact.Name);

            ContactButton.IsEnabled = true;

            _contact = ChatsModule.Instance.Contact(_contactId) ?? _contact;
            Show();
        }

        private async void OnToggleBlock(object sender, RoutedEventArgs e)
        {
            BlockButton.IsEnabled = false;

            await ChatsModule.Instance.UpdateContactAsync(_contactId,
                _blocked ? "UNBLOCK" : "BLOCK", null);

            BlockButton.IsEnabled = true;

            _blocked = !_blocked;
            BlockButton.Content = _blocked ? "разблокировать" : "заблокировать";
        }
    }
}
