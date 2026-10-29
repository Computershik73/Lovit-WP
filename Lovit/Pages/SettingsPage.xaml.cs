using System;
using System.Collections.Generic;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Config;
using Lovit.Core.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>Настройки. Порт MAXSettingsViewController.</summary>
    public sealed partial class SettingsPage : Page
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Расстановка переключателей сама поднимает Toggled — без этого
            // признака открытие настроек перезаписывало бы их же значения.
            _loading = true;

            GhostSwitch.IsOn = AppSettings.GhostMode;
            AntiReadSwitch.IsOn = AppSettings.AntiRead;
            AntiTypingSwitch.IsOn = AppSettings.AntiTyping;
            TileAgentSwitch.IsOn = AppSettings.TileAgent;
            NotificationsSwitch.IsOn = AppSettings.Notifications;
            NotificationSoundSwitch.IsOn = AppSettings.NotificationSound;
            NotificationTextSwitch.IsOn = AppSettings.NotificationText;
            ShowNotificationDetails();

            var names = new List<string>();
            foreach (DevicePreset preset in DevicePresets.All) names.Add(preset.DeviceName);

            DeviceBox.ItemsSource = names;
            DeviceBox.SelectedItem = DevicePresets.Named(AppSettings.DeviceName).DeviceName;

            _loading = false;

            ShowTransport();
        }

        private void OnGhostToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.GhostMode = GhostSwitch.IsOn;
        }

        private void OnAntiReadToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.AntiRead = AntiReadSwitch.IsOn;
        }

        /// <summary>
        /// Фоновое обновление плитки. Заводится и снимается сразу же: между
        /// переключателем и делом ждать нечего.
        /// </summary>
        private async void OnTileAgentToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.TileAgent = TileAgentSwitch.IsOn;
            await TileAgent.ApplyAsync();
        }

        /// <summary>
        /// Уведомления. Фоновый агент нужен и им, поэтому заводится заново:
        /// плитка может быть выключена, а уведомления — нет.
        /// </summary>
        private async void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.Notifications = NotificationsSwitch.IsOn;
            ShowNotificationDetails();
            await TileAgent.ApplyAsync();
        }

        private void OnNotificationSoundToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.NotificationSound = NotificationSoundSwitch.IsOn;
        }

        private void OnNotificationTextToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.NotificationText = NotificationTextSwitch.IsOn;
        }

        /// <summary>Звук и текст без самих уведомлений ничего не значат — прячем.</summary>
        private void ShowNotificationDetails()
        {
            NotificationDetails.Visibility = AppSettings.Notifications
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnAntiTypingToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.AntiTyping = AntiTypingSwitch.IsOn;
        }

        /// <summary>
        /// Приметы устройства уезжают в приветствии, а оно бывает один раз на
        /// соединение — значит соединение надо поднять заново. Токен при этом
        /// остаётся: список чатов вернётся сам, повторным входом по нему.
        /// </summary>
        private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var chosen = DeviceBox.SelectedItem as string;
            if (string.IsNullOrEmpty(chosen)) return;

            AppSettings.DeviceName = chosen;

            MaxApi.Instance.Disconnect();
            await MaxApi.Instance.EnsureOnlineAsync();

            ShowTransport();
        }

        private void OnSecurity(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SecurityPage));
        }

        private void OnLog(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LogPage));
        }

        private void OnAbout(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AboutPage));
        }

        /// <summary>
        /// Видно, своей ли криптографией поднято соединение: если её нет,
        /// разговор идёт системным стеком, и отказы будут другими. Если
        /// соединения нет вовсе — видно, почему.
        /// </summary>
        private void ShowTransport()
        {
            string transport = MaxApi.Instance.TransportDescription;

            if (MaxApi.Instance.State == SessionState.Online && !string.IsNullOrEmpty(transport))
            {
                TransportText.Text = MaxApi.Instance.IsOutdated
                    ? "Соединение: " + transport + "\nСервер считает версию клиента устаревшей — нужна новая сборка приложения."
                    : "Соединение: " + transport;
                return;
            }

            string reason = MaxApi.Instance.LastError;
            TransportText.Text = string.IsNullOrEmpty(reason)
                ? "Соединение: нет"
                : "Соединение: нет — " + reason;
        }

        private async void OnLogout(object sender, RoutedEventArgs e)
        {
            LogoutButton.IsEnabled = false;

            await AccountModule.Instance.LogoutAsync();

            ChatsModule.Instance.Reset();
            Ui.ListRows.Reset();

            LogoutButton.IsEnabled = true;

            // Возврата к списку чатов быть не должно: там уже пусто, а кнопка
            // «назад» на телефоне ведёт именно туда.
            Frame.Navigate(typeof(LoginPage));
            Frame.BackStack.Clear();
        }
    }
}
