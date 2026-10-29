using System;
using Lovit.Backend;
using Lovit.Backend.Calls;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Models;
using Lovit.Ui;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Разговор: кто, сколько идёт, микрофон, громкая связь и отбой.
    ///
    /// Сам звонок — CallSession в CallsModule; экран только показывает его и
    /// даёт свой MediaElement для звука. Поэтому уйти с экрана «назад» посреди
    /// разговора нельзя: без элемента на экране собеседника не будет слышно.
    /// Кончился звонок — экран ещё пару секунд показывает почему и уходит.
    /// </summary>
    public sealed partial class CallPage : Page, IHandlesBack
    {
        private static readonly TimeSpan LeaveAfter = TimeSpan.FromSeconds(2.5);

        private CallSession _session;
        private DispatcherTimer _clock;
        private DisplayRequest _awake;

        public CallPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Экран входящего своё дело сделал: «назад» из разговора должно
            // вести туда, откуда звонок застал, а не к нему.
            if (Frame.BackStack.Count > 0
                && Frame.BackStack[Frame.BackStack.Count - 1].SourcePageType == typeof(IncomingCallPage))
            {
                Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
            }

            _session = CallsModule.Instance.Current;
            if (_session == null)
            {
                Leave();
                return;
            }

            _session.Changed += OnChanged;
            _session.AttachPlayer(Player);

            ShowPeer();
            ShowState();

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (sender, argument) => ShowState();
            _clock.Start();

            // Пока идёт разговор, экран не гаснет: заблокированный телефон
            // усыпляет приложение, а с ним и звонок.
            try
            {
                _awake = new DisplayRequest();
                _awake.RequestActive();
            }
            catch (Exception)
            {
                _awake = null;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (_session != null) _session.Changed -= OnChanged;
            if (_clock != null) _clock.Stop();

            if (_awake != null)
            {
                try { _awake.RequestRelease(); }
                catch (Exception) { }
                _awake = null;
            }

        }

        public bool HandleBack()
        {
            // Посреди разговора «назад» ничего не делает — см. описание класса.
            return _session != null && _session.State != CallState.Ended;
        }

        private void OnChanged(CallSession session)
        {
            ShowState();

            if (session.State == CallState.Ended)
            {
                MuteButton.IsEnabled = false;
                SpeakerButton.IsEnabled = false;
                HangupButton.IsEnabled = false;

                var leave = new DispatcherTimer { Interval = LeaveAfter };
                leave.Tick += (sender, argument) =>
                {
                    leave.Stop();
                    Leave();
                };
                leave.Start();
            }
        }

        private void ShowState()
        {
            if (_session == null) return;

            switch (_session.State)
            {
                case CallState.Calling:
                    Status.Text = "вызов…";
                    break;
                case CallState.Connecting:
                    Status.Text = "соединение…";
                    break;
                case CallState.Active:
                    TimeSpan length = DateTime.Now - _session.ActiveSince;
                    Status.Text = ((int)length.TotalMinutes).ToString() + ":" + length.Seconds.ToString("00");
                    break;
                case CallState.Ended:
                    Status.Text = "звонок окончен" + (string.IsNullOrEmpty(_session.EndReason)
                        ? "" : "\n" + _session.EndReason);
                    break;
            }
        }

        private async void ShowPeer()
        {
            MaxContact contact = ChatsModule.Instance.Contact(_session.PeerUserId);
            string name = contact != null ? contact.DisplayName : "Звонок";

            PeerName.Text = name;
            Initials.Text = InitialsOf(name);

            if (contact == null || string.IsNullOrEmpty(contact.AvatarUrl)) return;

            ImageSource picture = ImageCache.Cached(contact.AvatarUrl, 320)
                                  ?? await ImageCache.LoadAsync(contact.AvatarUrl, 320);
            if (picture == null) return;

            AvatarBrush.ImageSource = picture;
            Avatar.Visibility = Visibility.Visible;
        }

        private static string InitialsOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            string made = "";
            foreach (string word in name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (char.IsLetter(word[0])) made += char.ToUpper(word[0]);
                if (made.Length == 2) break;
            }
            return made;
        }

        #region Кнопки

        private void OnMute(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            _session.SetMuted(MuteButton.IsChecked == true);
        }

        private void OnSpeaker(object sender, RoutedEventArgs e)
        {
            if (_session != null) _session.SetLoud(SpeakerButton.IsChecked == true);
        }

        private async void OnHangup(object sender, RoutedEventArgs e)
        {
            HangupButton.IsEnabled = false;
            if (_session != null) await _session.HangupAsync();
        }

        #endregion

        private void Leave()
        {
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }
    }
}
