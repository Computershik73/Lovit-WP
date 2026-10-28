using System;
using System.Threading.Tasks;
using Lovit.Backend;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Пароль для входа. Порт MAXSecurityViewController и
    /// MAXPasswordSetupViewController, сведённых в один экран: на телефоне
    /// установка и смена отличаются одним лишним полем.
    /// </summary>
    public sealed partial class SecurityPage : Page
    {
        private bool _enabled;

        public SecurityPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadStatusAsync();
        }

        private async Task LoadStatusAsync()
        {
            SetBusy(true, null);

            TwoFactorStatus status = await TwoFactorModule.Instance.FetchStatusAsync();

            SetBusy(false, null);

            if (!string.IsNullOrEmpty(status.Error))
            {
                StateText.Text = "Не удалось узнать: " + status.Error;
                return;
            }

            _enabled = status.Enabled;

            if (_enabled)
            {
                StateText.Text = string.IsNullOrEmpty(status.Hint)
                    ? "Пароль установлен."
                    : "Пароль установлен. Подсказка: " + status.Hint;
            }
            else
            {
                StateText.Text = "Пароль не установлен.";
            }

            // Смена требует подтверждения текущего пароля — установка нет.
            CurrentBox.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
            RemoveButton.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.Content = _enabled ? "сменить пароль" : "установить пароль";
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            string password = PasswordBox.Password;
            string repeat = RepeatBox.Password;
            string hint = HintBox.Text.Trim();

            if (password.Length < 6)
            {
                StatusText.Text = "Пароль короче шести знаков сервер не примет";
                return;
            }

            if (password != repeat)
            {
                StatusText.Text = "Пароли не совпадают";
                return;
            }

            SetBusy(true, "Готовим…");

            // Трек живёт недолго, поэтому берётся в начале каждой операции,
            // а не хранится.
            string trackId = await TwoFactorModule.Instance.CreateTrackAsync();
            if (string.IsNullOrEmpty(trackId))
            {
                // Сервер обычно объясняет отказ сам и по-русски — показываем
                // его слова, а не наши.
                SetBusy(false, TwoFactorModule.Instance.LastError ?? "Сервер не выдал трек");
                return;
            }

            string failure;

            if (_enabled)
            {
                SetBusy(true, "Проверяем текущий пароль…");

                failure = await TwoFactorModule.Instance.CheckCurrentPasswordAsync(
                    CurrentBox.Password, trackId);

                if (failure != null)
                {
                    SetBusy(false, failure);
                    return;
                }

                SetBusy(true, "Меняем пароль…");
                failure = await TwoFactorModule.Instance.UpdatePasswordAsync(password, hint, trackId);
            }
            else
            {
                SetBusy(true, "Проверяем пароль…");

                failure = await TwoFactorModule.Instance.ValidatePasswordAsync(password, trackId);
                if (failure != null)
                {
                    SetBusy(false, failure);
                    return;
                }

                if (hint.Length > 0)
                {
                    failure = await TwoFactorModule.Instance.ValidateHintAsync(hint, trackId);
                    if (failure != null)
                    {
                        SetBusy(false, failure);
                        return;
                    }
                }

                SetBusy(true, "Устанавливаем…");
                failure = await TwoFactorModule.Instance.ConfirmAsync(trackId, password, hint, false);
            }

            SetBusy(false, failure ?? "Готово. Теперь вход на новом устройстве спросит этот пароль.");

            if (failure != null) return;

            PasswordBox.Password = "";
            RepeatBox.Password = "";
            CurrentBox.Password = "";

            await LoadStatusAsync();
        }

        private async void OnRemove(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "Готовим…");

            string trackId = await TwoFactorModule.Instance.CreateTrackAsync();
            if (string.IsNullOrEmpty(trackId))
            {
                // Сервер обычно объясняет отказ сам и по-русски — показываем
                // его слова, а не наши.
                SetBusy(false, TwoFactorModule.Instance.LastError ?? "Сервер не выдал трек");
                return;
            }

            SetBusy(true, "Проверяем текущий пароль…");

            string failure = await TwoFactorModule.Instance.CheckCurrentPasswordAsync(
                CurrentBox.Password, trackId);

            if (failure != null)
            {
                SetBusy(false, failure);
                return;
            }

            SetBusy(true, "Снимаем…");
            failure = await TwoFactorModule.Instance.RemovePasswordAsync(trackId);

            SetBusy(false, failure ?? "Пароль снят.");

            if (failure == null) await LoadStatusAsync();
        }

        private void SetBusy(bool busy, string status)
        {
            SaveButton.IsEnabled = !busy;
            RemoveButton.IsEnabled = !busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (status != null) StatusText.Text = status;
        }
    }
}
