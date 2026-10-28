using System;
using Lovit.Backend;
using Lovit.Backend.Calls;
using Lovit.Core.Media;
using Lovit.Models;
using Windows.Phone.Devices.Notification;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Экран входящего звонка: кто звонит, и две кнопки — отклонить или
    /// скрыть. Ответить отсюда пока нельзя (см. CallsModule).
    ///
    /// Пока звонит — вибрация, раз в две секунды. Своей мелодии звонка у
    /// приложения нет, а системную обычному приложению Windows Phone 8.1
    /// сыграть не дают.
    ///
    /// Звонок кончился как угодно — отклонили, скрыли, сбросил звонящий,
    /// вышло время, — экран уходит сам.
    /// </summary>
    public sealed partial class IncomingCallPage : Page
    {
        private IncomingCall _call;
        private DispatcherTimer _buzz;

        public IncomingCallPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _call = CallsModule.Instance.Ringing;
            if (_call == null)
            {
                // Звонок кончился раньше, чем открылся экран.
                Leave();
                return;
            }

            CallsModule.Instance.IncomingEnded += OnEnded;
            ChatsModule.Instance.Changed += OnContactsChanged;

            Kind.Text = _call.IsVideo ? "входящий видеозвонок" : "входящий звонок";
            ShowCaller();
            StartBuzz();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            CallsModule.Instance.IncomingEnded -= OnEnded;
            ChatsModule.Instance.Changed -= OnContactsChanged;
            StopBuzz();

            // Ушли с экрана «назад», ничего не решив, — это то же «скрыть».
            if (CallsModule.Instance.Ringing == _call) CallsModule.Instance.Hide();
        }

        #region Кто звонит

        /// <summary>
        /// Имя и лицо звонящего. Если карточки ещё нет, её запросили при
        /// приходе звонка — когда приедет, покажем ещё раз.
        /// </summary>
        private async void ShowCaller()
        {
            MaxContact contact = ChatsModule.Instance.Contact(_call.CallerId);
            string name = contact != null ? contact.DisplayName : "Звонок";

            CallerName.Text = name;
            Initials.Text = InitialsOf(name);

            if (contact == null || string.IsNullOrEmpty(contact.AvatarUrl)) return;

            ImageSource picture = ImageCache.Cached(contact.AvatarUrl, 320)
                                  ?? await ImageCache.LoadAsync(contact.AvatarUrl, 320);
            if (picture == null) return;

            AvatarBrush.ImageSource = picture;
            Avatar.Visibility = Visibility.Visible;
        }

        private void OnContactsChanged()
        {
            if (_call != null && Avatar.Visibility != Visibility.Visible) ShowCaller();
        }

        private static string InitialsOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            string[] words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string made = "";
            foreach (string word in words)
            {
                if (char.IsLetter(word[0])) made += char.ToUpper(word[0]);
                if (made.Length == 2) break;
            }
            return made;
        }

        #endregion

        #region Вибрация

        private void StartBuzz()
        {
            _buzz = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _buzz.Tick += (sender, argument) => Buzz();
            _buzz.Start();
            Buzz();
        }

        private static void Buzz()
        {
            try
            {
                VibrationDevice.GetDefault().Vibrate(TimeSpan.FromMilliseconds(900));
            }
            catch (Exception)
            {
                // На эмуляторе и на части прошивок вибрации нет — звоним молча.
            }
        }

        private void StopBuzz()
        {
            if (_buzz != null) _buzz.Stop();
            _buzz = null;

            try { VibrationDevice.GetDefault().Cancel(); }
            catch (Exception) { }
        }

        #endregion

        #region Кнопки

        private async void OnDecline(object sender, RoutedEventArgs e)
        {
            DeclineButton.IsEnabled = false;
            HideButton.IsEnabled = false;
            StopBuzz();

            await CallsModule.Instance.DeclineAsync();
        }

        private void OnHide(object sender, RoutedEventArgs e)
        {
            CallsModule.Instance.Hide();
        }

        #endregion

        private void OnEnded(IncomingCall call)
        {
            if (call != _call) return;
            Leave();
        }

        private void Leave()
        {
            StopBuzz();
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }
    }
}
