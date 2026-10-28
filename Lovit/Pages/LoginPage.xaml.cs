using System;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Вход: номер → код → пароль второго фактора либо создание профиля.
    /// Порт четырёх экранов iOS-версии (MAXPhone/MAXCode/MAXPassword/
    /// MAXRegister), сведённых в один: на телефоне переход между ними — это
    /// одна и та же страница с другим полем, и отдельные экраны добавляли бы
    /// только шагов назад.
    /// </summary>
    public sealed partial class LoginPage : Page
    {
        private enum Step { Phone, Code, Password, Register }

        private Step _step = Step.Phone;
        private string _phone;
        private string _authToken;
        private string _registerToken;
        private string _trackId;

        /// <summary>
        /// В какой сессии выдан `_trackId`. Сервер держит незаконченный вход
        /// при своей сессии: оборвись связь между кодом и паролем — и он
        /// ответит «недопустимое состояние сессии», сколько пароль ни вводи.
        /// </summary>
        private int _trackSession;

        private bool _busy;

        public LoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            PhoneBox.Focus(FocusState.Programmatic);
        }

        private async void OnContinue(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            switch (_step)
            {
                case Step.Phone: await RequestCodeAsync(false); break;
                case Step.Code: await VerifyCodeAsync(); break;
                case Step.Password: await CheckPasswordAsync(); break;
                case Step.Register: await RegisterAsync(); break;
            }
        }

        /// <summary>
        /// Вторая кнопка. На шаге кода она высылает код заново, дальше —
        /// начинает вход сначала.
        ///
        /// Без такого выхода застрять можно намертво: сервер способен
        /// отвергнуть пароль не из-за пароля, а потому что незаконченный
        /// вход у него уже протух, — и повторное нажатие «войти» будет
        /// раз за разом посылать тот же негодный ключ.
        /// </summary>
        private async void OnResend(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_step == Step.Code)
            {
                await RequestCodeAsync(true);
                return;
            }

            StartOver(null);
        }

        /// <summary>Вернуться к номеру, забыв всё, что успели получить.</summary>
        private void StartOver(string why)
        {
            _authToken = null;
            _registerToken = null;
            _trackId = null;
            _trackSession = 0;

            PasswordBox.Password = "";

            ShowStep(Step.Phone, "вход", "Номер телефона с кодом страны", "выслать код");

            if (!string.IsNullOrEmpty(why)) StatusText.Text = why;
        }

        #region Номер

        private async Task RequestCodeAsync(bool again)
        {
            string phone = again ? _phone : PhoneBox.Text.Trim();
            if (phone.Length < 5)
            {
                StatusText.Text = "Введите номер телефона";
                return;
            }

            SetBusy(true, "Соединяемся…");
            if (!await MaxApi.Instance.EnsureOnlineAsync())
            {
                // С причиной, а не просто «нет связи»: отказ рукопожатия и
                // отказ сети чинятся совершенно по-разному.
                string reason = MaxApi.Instance.LastError;
                SetBusy(false, string.IsNullOrEmpty(reason)
                    ? "Нет связи с сервером"
                    : "Нет связи с сервером: " + reason);
                return;
            }

            SetBusy(true, again ? "Отправляем код повторно…" : "Запрашиваем код…");

            AuthResult result = again
                ? await AccountModule.Instance.ResendCodeAsync(phone)
                : await AccountModule.Instance.RequestCodeAsync(phone);

            SetBusy(false, null);

            if (result.Step != AuthStep.NeedCode)
            {
                StatusText.Text = result.Error;
                return;
            }

            _phone = phone;
            _authToken = result.Token;

            ShowStep(Step.Code, "код", "Код отправлен на " + phone, "войти");
        }

        #endregion

        #region Код

        private async Task VerifyCodeAsync()
        {
            string code = CodeBox.Text.Trim();
            if (code.Length < 4)
            {
                StatusText.Text = "Введите код из SMS";
                return;
            }

            SetBusy(true, "Проверяем код…");
            AuthResult result = await AccountModule.Instance.VerifyCodeAsync(code, _authToken);
            SetBusy(false, null);

            switch (result.Step)
            {
                case AuthStep.NeedPassword:
                    _trackId = result.TrackId;
                    _trackSession = MaxApi.Instance.Session;

                    ShowStep(Step.Password, "пароль", string.IsNullOrEmpty(result.Hint)
                        ? "У аккаунта включён пароль для входа"
                        : "Подсказка: " + result.Hint, "войти");
                    return;

                case AuthStep.NeedRegistration:
                    // Номер серверу незнаком — это не отказ, а предложение
                    // завести профиль.
                    _registerToken = result.Token;
                    ShowStep(Step.Register, "профиль",
                        "Этого номера в MAX ещё нет. Как вас зовут?", "создать профиль");
                    return;

                case AuthStep.LoggedIn:
                    await FinishAsync(result.Token);
                    return;

                default:
                    // Протухший вход на этом шаге чинится сам: номер мы
                    // помним, значит просто просим новый код. Так же
                    // поступает Komet — сбрасывать человека к вводу номера
                    // из-за того, что связь моргнула, незачем.
                    if (result.IsStale)
                    {
                        StatusText.Text = "Вход устарел — высылаем код заново";
                        await RequestCodeAsync(true);
                        return;
                    }

                    StatusText.Text = result.Error;
                    return;
            }
        }

        #endregion

        #region Пароль и профиль

        private async Task CheckPasswordAsync()
        {
            string password = PasswordBox.Password;
            if (password.Length == 0)
            {
                StatusText.Text = "Введите пароль";
                return;
            }

            // Связь могла оборваться и подняться заново, пока набирали
            // пароль. Незаконченный вход при этом остаётся в прошлой сессии,
            // и посылать туда пароль бессмысленно: ответом будет отказ,
            // одинаковый на любой пароль. Лучше честно начать сначала.
            if (_trackSession != 0 && MaxApi.Instance.Session != _trackSession)
            {
                StartOver("Связь прерывалась — вход придётся начать заново");
                return;
            }

            SetBusy(true, "Проверяем пароль…");
            AuthResult result = await AccountModule.Instance.CheckPasswordAsync(password, _trackId);
            SetBusy(false, null);

            if (result.Step != AuthStep.LoggedIn)
            {
                // Сервер держит незаконченный вход недолго. Протух — тот же
                // ключ не оживёт, и повторное «войти» будет отвечать одним и
                // тем же, сколько ни нажимай.
                if (result.IsStale) StartOver("Вход устарел — начните заново");
                else StatusText.Text = result.Error;

                return;
            }

            await FinishAsync(result.Token);
        }

        private async Task RegisterAsync()
        {
            string firstName = FirstNameBox.Text.Trim();
            if (firstName.Length == 0)
            {
                StatusText.Text = "Введите имя";
                return;
            }

            SetBusy(true, "Создаём профиль…");
            AuthResult result = await AccountModule.Instance.CompleteRegistrationAsync(
                _registerToken, firstName, LastNameBox.Text.Trim());
            SetBusy(false, null);

            if (result.Step != AuthStep.LoggedIn)
            {
                StatusText.Text = result.Error;
                return;
            }

            await FinishAsync(result.Token);
        }

        #endregion

        /// <summary>
        /// Токен получен — сразу вход по нему: в ответе LOGIN приезжают чаты
        /// и контакты, без них список открылся бы пустым.
        /// </summary>
        private async Task FinishAsync(string token)
        {
            AppSettings.Token = token;

            SetBusy(true, "Загружаем чаты…");
            AuthResult login = await AccountModule.Instance.LoginAsync(token);
            SetBusy(false, null);

            if (login.Step != AuthStep.LoggedIn)
            {
                StatusText.Text = login.Error;
                return;
            }

            ChatsModule.Instance.ApplyLoginPayload(login.LoginPayload);

            Frame.Navigate(typeof(MainPage));

            // Возвращаться кнопкой «назад» к вводу номера некуда: вход уже
            // состоялся, и повторять его нечем.
            Frame.BackStack.Clear();
        }

        private void ShowStep(Step step, string title, string hint, string action)
        {
            _step = step;

            StepTitle.Text = title;
            HintText.Text = hint;
            ContinueButton.Content = action;
            StatusText.Text = "";

            PhoneBox.Visibility = Visible(step == Step.Phone);
            CodeBox.Visibility = Visible(step == Step.Code);
            PasswordBox.Visibility = Visible(step == Step.Password);
            FirstNameBox.Visibility = Visible(step == Step.Register);
            LastNameBox.Visibility = Visible(step == Step.Register);

            // Вторая кнопка нужна везде, кроме самого начала: на шаге кода
            // ею высылают код заново, дальше — начинают вход сначала.
            ResendButton.Content = step == Step.Code
                ? "отправить код повторно"
                : "начать заново";

            ResendButton.Visibility = Visible(step != Step.Phone);

            if (step == Step.Code) CodeBox.Focus(FocusState.Programmatic);
            if (step == Step.Password) PasswordBox.Focus(FocusState.Programmatic);
            if (step == Step.Register) FirstNameBox.Focus(FocusState.Programmatic);
        }

        private static Visibility Visible(bool shown)
        {
            return shown ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;

            ContinueButton.IsEnabled = !busy;
            ResendButton.IsEnabled = !busy;
            Progress.Visibility = Visible(busy);

            if (status != null) StatusText.Text = status;
        }
    }
}
