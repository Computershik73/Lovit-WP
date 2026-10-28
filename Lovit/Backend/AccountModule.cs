using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;

namespace Lovit.Backend
{
    /// <summary>Что делать дальше после очередного шага входа.</summary>
    public enum AuthStep
    {
        Failed,
        NeedCode,
        NeedPassword,
        NeedRegistration,
        LoggedIn
    }

    public class AuthResult
    {
        public AuthStep Step { get; set; }

        /// <summary>
        /// Отказ оттого, что незаконченный вход у сервера протух: повторять
        /// с тем же ключом бессмысленно, надо начинать сначала.
        /// </summary>
        public bool IsStale { get; set; }

        /// <summary>Временный токен запроса кода либо токен сессии.</summary>
        public string Token { get; set; }

        /// <summary>trackId из passwordChallenge — с ним проверяется пароль.</summary>
        public string TrackId { get; set; }

        /// <summary>Подсказка к паролю, если владелец аккаунта её задал.</summary>
        public string Hint { get; set; }

        public long AccountId { get; set; }
        public string Error { get; set; }
        public IDictionary<string, object> LoginPayload { get; set; }
    }

    /// <summary>
    /// Вход: номер → код из SMS → при необходимости пароль второго фактора
    /// или создание профиля. Порт MAXAccountModule.
    /// </summary>
    public class AccountModule
    {
        private static readonly AccountModule _instance = new AccountModule();
        public static AccountModule Instance { get { return _instance; } }

        /// <summary>Правда после удачного LOGIN, а не просто после приветствия.</summary>
        public bool IsLoggedIn { get; private set; }

        private AccountModule()
        {
            MaxApi.Instance.StateChanged += () =>
            {
                if (MaxApi.Instance.State != SessionState.Online) IsLoggedIn = false;
            };

            // Повторный вход после обрыва — здесь, а не на экране списка
            // чатов.
            //
            // Обрывов много и они не только от плохой связи: сервер рвёт
            // соединение сам после каждого своего отказа. А пока это жило на
            // экране, оно работало только пока экран открыт: стоило уйти в
            // настройки или в переписку — и входить после обрыва становилось
            // некому. Следующий запрос получал «Недопустимое состояние
            // сессии», сервер снова рвал соединение, и так по кругу.
            MaxApi.Instance.Reconnected += OnReconnected;
        }

        /// <summary>
        /// Сессия поднялась заново — повторяем вход по сохранённому токену.
        /// </summary>
        private async void OnReconnected()
        {
            // Под перехватом целиком: метод без возврата, и всё, что здесь
            // сорвётся, унесло бы приложение молча. Зовётся он сразу после
            // обрыва — то есть чаще всего после сна телефона.
            try
            {
                string token = AppSettings.Token;
                if (string.IsNullOrEmpty(token)) return;

                AuthResult result = await LoginAsync(token);

                if (result.Step != AuthStep.LoggedIn)
                {
                    // Токен не трогаем: неудача здесь — почти всегда обрыв, а
                    // не отзыв. Пользователь увидит это на экране списка чатов.
                    Log.Write("api", "повторный вход не удался: " + result.Error);
                    return;
                }

                ChatsModule.Instance.ApplyLoginPayload(result.LoginPayload);
            }
            catch (Exception error)
            {
                Log.Fail("api", "повторный вход", error);
            }
        }

        #region Запрос кода

        /// <summary>AUTH_REQUEST с типом START_AUTH.</summary>
        public async Task<AuthResult> RequestCodeAsync(string phone)
        {
            return await RequestCodeAsync(phone, "START_AUTH");
        }

        /// <summary>AUTH_REQUEST с типом RESEND — отправить код заново.</summary>
        public async Task<AuthResult> ResendCodeAsync(string phone)
        {
            return await RequestCodeAsync(phone, "RESEND");
        }

        private async Task<AuthResult> RequestCodeAsync(string phone, string type)
        {
            if (!IsOnline()) return Fail("Сессия не онлайн");

            var payload = new Dictionary<string, object>
            {
                { "phone", NormalizePhone(phone) },
                { "type", type },
                { "language", "ru" }
            };

            // Отпечаток сборки. Без него сервер отвечает удачей, но кода
            // никуда не отправляет — самый обидный отказ из возможных, потому
            // что выглядит он как успех.
            byte[] fingerprint = MaxApi.Instance.Fingerprint;
            if (fingerprint != null) payload["mode"] = fingerprint;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.AuthRequest, payload);
            if (packet == null) return Fail("Сервер не ответил");
            if (packet.IsError)
            {
                return IsStale(packet) ? Stale(packet) : Fail(ErrorText(packet));
            }

            string token = MessagePack.String(packet.Map, "token");
            if (string.IsNullOrEmpty(token)) return Fail("Сервер не вернул токен");

            return new AuthResult { Step = AuthStep.NeedCode, Token = token };
        }

        #endregion

        #region Проверка кода

        /// <summary>
        /// AUTH: проверка кода.
        ///
        /// Ответов бывает три. Обычный — токен LOGIN в `tokenAttrs`. У аккаунта
        /// с паролем вместо токена приходит `passwordChallenge`, и вход
        /// продолжается паролем. У незнакомого номера приходит токен REGISTER —
        /// это предложение завести новый профиль.
        /// </summary>
        public async Task<AuthResult> VerifyCodeAsync(string code, string token)
        {
            if (!IsOnline()) return Fail("Сессия не онлайн");

            var payload = new Dictionary<string, object>
            {
                { "token", token ?? "" },
                { "verifyCode", code ?? "" },
                { "authTokenType", "CHECK_CODE" }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.Auth, payload);
            if (packet == null) return Fail("Сервер не ответил");
            if (packet.IsError)
            {
                return IsStale(packet) ? Stale(packet) : Fail(ErrorText(packet));
            }

            IDictionary<string, object> data = packet.Map;

            // Двухфакторная — первой: токена в таком ответе нет вовсе.
            IDictionary<string, object> challenge = MessagePack.Map(data, "passwordChallenge");
            if (challenge != null)
            {
                string trackId = MessagePack.String(challenge, "trackId");
                if (string.IsNullOrEmpty(trackId))
                {
                    return Fail("Нужен пароль, но сервер не прислал trackId");
                }

                return new AuthResult
                {
                    Step = AuthStep.NeedPassword,
                    TrackId = trackId,
                    Hint = MessagePack.String(challenge, "hint")
                };
            }

            string loginToken = NestedToken(data, "LOGIN");
            string registerToken = NestedToken(data, "REGISTER");
            long accountId = ExtractAccountId(data);

            if (!string.IsNullOrEmpty(loginToken))
            {
                Remember(loginToken, accountId);
                return new AuthResult
                {
                    Step = AuthStep.LoggedIn,
                    Token = loginToken,
                    AccountId = accountId
                };
            }

            if (!string.IsNullOrEmpty(registerToken))
            {
                return new AuthResult
                {
                    Step = AuthStep.NeedRegistration,
                    Token = registerToken,
                    AccountId = accountId
                };
            }

            return Fail("Сервер не вернул токен сессии");
        }

        #endregion

        #region Пароль второго фактора

        /// <summary>AUTH_LOGIN_CHECK_PASSWORD — второй шаг входа.</summary>
        public async Task<AuthResult> CheckPasswordAsync(string password, string trackId)
        {
            if (!IsOnline()) return Fail("Сессия не онлайн");

            var payload = new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "password", password ?? "" }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.AuthLoginCheckPassword, payload);
            if (packet == null) return Fail("Сервер не ответил");

            if (packet.IsError)
            {
                // На неверный пароль сервер отвечает FAIL_WRONG_PASSWORD — тем
                // же именем ошибки, что и на негодный токен. Здесь опкод
                // известен, значит и смысл однозначен.
                string message = MessagePack.String(packet.Map, "message");
                if (message == "FAIL_WRONG_PASSWORD") return Fail("Неверный пароль");

                return IsStale(packet) ? Stale(packet) : Fail(ErrorText(packet));
            }

            IDictionary<string, object> data = packet.Map;

            // Отказ приходит и вторым способом — обычным ответом с полем error.
            if (data != null && data.ContainsKey("error")) return Fail("Неверный пароль");

            string loginToken = NestedToken(data, "LOGIN");
            if (string.IsNullOrEmpty(loginToken))
            {
                return Fail("Пароль принят, но токен входа не пришёл");
            }

            long accountId = ExtractAccountId(data);
            if (accountId == 0) return Fail("В ответе нет id аккаунта");

            Remember(loginToken, accountId);

            return new AuthResult
            {
                Step = AuthStep.LoggedIn,
                Token = loginToken,
                AccountId = accountId
            };
        }

        #endregion

        #region Создание профиля

        /// <summary>AUTH_CONFIRM — завершение регистрации нового профиля.</summary>
        public async Task<AuthResult> CompleteRegistrationAsync(string token, string firstName, string lastName)
        {
            if (!IsOnline()) return Fail("Сессия не онлайн");

            var payload = new Dictionary<string, object>
            {
                { "token", token ?? "" },
                { "tokenType", "REGISTER" },
                { "firstName", firstName ?? "" }
            };

            if (!string.IsNullOrEmpty(lastName)) payload["lastName"] = lastName;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.AuthConfirm, payload);
            if (packet == null) return Fail("Сервер не ответил");
            if (packet.IsError)
            {
                return IsStale(packet) ? Stale(packet) : Fail(ErrorText(packet));
            }

            IDictionary<string, object> data = packet.Map;

            long accountId = ExtractAccountId(data);
            if (accountId == 0) return Fail("В ответе нет id аккаунта");

            // Профиль создан — сессия продолжается токеном, который сервер
            // выдаёт здесь же либо оставляет прежним, регистрационным.
            string loginToken = NestedToken(data, "LOGIN") ?? token;
            Remember(loginToken, accountId);

            return new AuthResult
            {
                Step = AuthStep.LoggedIn,
                Token = loginToken,
                AccountId = accountId
            };
        }

        #endregion

        #region Вход

        /// <summary>
        /// LOGIN по токену. Поля запроса — те же, что шлёт оригинальный
        /// клиент: `exp.chatsCountGroups` двоичной парой байт, синхронизации
        /// со значением -1 («присылай всё») и отпечаток сборки.
        /// </summary>
        public async Task<AuthResult> LoginAsync(string token)
        {
            if (!IsOnline()) return Fail("Сессия не онлайн");

            // Вход — по одному за раз.
            //
            // Желающих войти двое: этот модуль после каждого поднятия сессии
            // и экран списка чатов при открытии. На запуске они срабатывают
            // одновременно, и LOGIN уходил дважды — в логе это видно как два
            // подряд «опкод 19». Сервер отвечает на оба, оба ответа
            // раскладываются по кэшу, и приложение падает прямо на загрузке
            // списка. Второму желающему отдаём тот же ход, что и первому.
            Task<AuthResult> running = _login;
            if (running != null) return await running;

            Task<AuthResult> mine = LoginOnceAsync(token);
            _login = mine;

            try
            {
                return await mine;
            }
            finally
            {
                _login = null;
            }
        }

        private Task<AuthResult> _login;

        private async Task<AuthResult> LoginOnceAsync(string token)
        {

            var experiment = new Dictionary<string, object>
            {
                { "chatsCountGroups", new byte[] { 0x0b, 0x32 } }
            };

            var payload = new Dictionary<string, object>
            {
                { "token", token ?? "" },
                { "interactive", !AppSettings.GhostMode },
                { "exp", experiment },
                { "presenceSync", -1L },
                { "chatsSync", -1L }
            };

            byte[] fingerprint = MaxApi.Instance.Fingerprint;
            if (fingerprint != null) payload["chatCacheFingerprint"] = fingerprint;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.Login, payload);
            if (packet == null) return Fail("Сервер не ответил");
            if (packet.IsError)
            {
                return IsStale(packet) ? Stale(packet) : Fail(ErrorText(packet));
            }

            IDictionary<string, object> data = packet.Map;

            long accountId = ExtractAccountId(data);
            if (accountId != 0) AppSettings.AccountId = accountId;

            // Сервер может выдать новый токен взамен прежнего — тогда хранить
            // надо его, иначе следующий запуск войдёт по отозванному.
            string updated = MessagePack.String(data, "token");
            if (!string.IsNullOrEmpty(updated)) AppSettings.Token = updated;

            IsLoggedIn = true;

            return new AuthResult
            {
                Step = AuthStep.LoggedIn,
                AccountId = accountId,
                LoginPayload = data
            };
        }

        #endregion

        #region Выход

        public async Task LogoutAsync()
        {
            // Число на плитке — про этот аккаунт, и после выхода оно ни о
            // чём: гасим сразу, не дожидаясь ответа сервера.
            LiveTile.Clear();
            LiveTile.Forget();

            if (MaxApi.Instance.State == SessionState.Online)
            {
                await MaxApi.Instance.RequestAsync(Opcodes.Logout, new Dictionary<string, object>());
            }

            IsLoggedIn = false;

            // Соединение рвётся тоже: сервер отозвал токен, и всё, что по нему
            // придёт дальше, нам уже не принадлежит.
            MaxApi.Instance.Disconnect();
            AppSettings.Clear();
        }

        #endregion

        #region Разбор ответов

        private static bool IsOnline()
        {
            return MaxApi.Instance.State == SessionState.Online;
        }

        private static void Remember(string token, long accountId)
        {
            AppSettings.Token = token;
            if (accountId != 0) AppSettings.AccountId = accountId;
        }

        /// <summary>
        /// Токены лежат не в корне ответа, а в `tokenAttrs`: там LOGIN для
        /// входа и REGISTER для нового профиля.
        /// </summary>
        private static string NestedToken(IDictionary<string, object> payload, string key)
        {
            IDictionary<string, object> attributes = MessagePack.Map(payload, "tokenAttrs");
            IDictionary<string, object> entry = MessagePack.Map(attributes, key);
            return MessagePack.String(entry, "token");
        }

        /// <summary>
        /// Идентификатор аккаунта. Мест, где он бывает, несколько, и разные
        /// ответы кладут его по-разному — отсюда перебор.
        /// </summary>
        public static long ExtractAccountId(IDictionary<string, object> response)
        {
            if (response == null) return 0;

            IDictionary<string, object> profile = MessagePack.Map(response, "profile");
            if (profile != null)
            {
                IDictionary<string, object> contact = MessagePack.Map(profile, "contact");
                long value = FirstNonZero(contact, "id", "contactId", "accountId");
                if (value != 0) return value;

                value = FirstNonZero(profile, "id", "accountId");
                if (value != 0) return value;
            }

            long fromContact = FirstNonZero(MessagePack.Map(response, "contact"), "id", "contactId");
            if (fromContact != 0) return fromContact;

            long fromAccount = FirstNonZero(MessagePack.Map(response, "account"), "id", "accountId");
            if (fromAccount != 0) return fromAccount;

            return FirstNonZero(response, "accountId", "account_id");
        }

        private static long FirstNonZero(IDictionary<string, object> map, params string[] keys)
        {
            if (map == null) return 0;

            foreach (string key in keys)
            {
                long value = MessagePack.Long(map, key);
                if (value != 0) return value;

                // Тот же ключ иногда приходит строкой.
                string text = MessagePack.String(map, key);
                if (!string.IsNullOrEmpty(text) && long.TryParse(text, out value) && value != 0)
                {
                    return value;
                }
            }

            return 0;
        }

        /// <summary>
        /// Отказ ли это оттого, что незаконченный вход у сервера протух.
        ///
        /// Своего кода у такого отказа нет. Машинными словами сервер
        /// отвечает лишь в двух случаях — `FAIL_LOGIN_TOKEN` и
        /// `FAIL_WRONG_PASSWORD`, — а «недопустимое состояние сессии» не
        /// помечается ни тем, ни другим: и Komet, и порт под iOS узнают его
        /// по тексту. Здесь то же самое и по тем же словам.
        ///
        /// Оба машинных слова здесь значат одно и то же — отклонённый
        /// ключ. Странное имя `FAIL_WRONG_PASSWORD` сбивает с толку, но к опечатке
        /// в пароле оно отношения не имеет: неверный пароль сервер отдаёт
        /// обычным ответом с полем `error` внутри, а не отказом. Так же это
        /// разведено и в Komet.
        ///
        /// На самом шаге проверки пароля это слово всё же разбирается
        /// раньше и по-своему: там известен опкод, и смысл однозначен.
        /// </summary>
        private static bool IsStale(Packet packet)
        {
            string code = MessagePack.String(packet.Map, "message");

            if (code == "FAIL_LOGIN_TOKEN" || code == "FAIL_WRONG_PASSWORD") return true;

            string text = (ErrorText(packet) ?? "").ToLower();

            return text.Contains("состояние сессии")
                   || text.Contains("сессия не найдена")
                   || text.Contains("авторизационная сессия");
        }

        private static AuthResult Stale(Packet packet)
        {
            return new AuthResult
            {
                Step = AuthStep.Failed,
                Error = ErrorText(packet),
                IsStale = true
            };
        }

        private static string ErrorText(Packet packet)
        {
            string message = MessagePack.String(packet.Map, "localizedMessage")
                             ?? MessagePack.String(packet.Map, "message");
            return string.IsNullOrEmpty(message) ? "Сервер отклонил запрос" : message;
        }

        private static AuthResult Fail(string error)
        {
            return new AuthResult { Step = AuthStep.Failed, Error = error };
        }

        /// <summary>Номер в том виде, в каком его ждёт сервер: только цифры и плюс.</summary>
        private static string NormalizePhone(string phone)
        {
            var digits = new System.Text.StringBuilder();
            foreach (char symbol in phone ?? "")
            {
                if (symbol >= '0' && symbol <= '9') digits.Append(symbol);
            }
            return "+" + digits;
        }

        #endregion
    }
}
