using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core.Protocol;

namespace Lovit.Backend
{
    /// <summary>Состояние пароля на сервере.</summary>
    public class TwoFactorStatus
    {
        public bool Enabled { get; set; }
        public string Email { get; set; }
        public string Hint { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Пароль для входа (двухфакторная аутентификация). Порт
    /// MAXTwoFactorModule.
    ///
    /// Зачем он нужен: сервер MAX перестаёт слать код в SMS, когда у аккаунта
    /// уже есть сессия, и вместо этого присылает в переписку «Установите
    /// пароль, чтобы войти в MAX на новом устройстве». Без пароля второе
    /// устройство подключить нельзя вовсе, а поставить его можно только там,
    /// где есть действующая сессия, — то есть в самом клиенте.
    ///
    /// Все шаги идут внутри одного «трека»: сервер выдаёт его на
    /// AUTH_CREATE_TRACK и по нему связывает проверку пароля, подсказки и
    /// почты с итоговым AUTH_SET_2FA. Трек живёт недолго, поэтому получать
    /// его надо в начале каждой операции, а не хранить.
    /// </summary>
    public class TwoFactorModule
    {
        private static readonly TwoFactorModule _instance = new TwoFactorModule();
        public static TwoFactorModule Instance { get { return _instance; } }

        private TwoFactorModule() { }

        /// <summary>
        /// AUTH_CREATE_TRACK. С него начинается любая операция.
        ///
        /// `type` 0 — тот же тип трека, что заводит официальный клиент,
        /// заходя в раздел пароля. Оба исходника его шлют
        /// (two_factor_module.dart, MAXTwoFactorModule.m), пустой запрос
        /// здесь был недосмотром.
        /// </summary>
        public async Task<string> CreateTrackAsync()
        {
            LastError = null;

            Packet packet = await MaxApi.Instance.RequestAsync(
                Opcodes.AuthCreateTrack, new Dictionary<string, object> { { "type", 0L } });

            if (packet == null || packet.IsError)
            {
                // Причину сохраняем: сервер объясняет отказ словами и
                // по-русски — «установить пароль можно через 24 часа после
                // входа в профиль», например. Молчать о ней и показывать
                // вместо неё «сервер не выдал трек» — значит прятать от
                // человека единственное, что ему тут поможет.
                LastError = ErrorText(packet);
                return null;
            }

            return MessagePack.String(packet.Map, "trackId");
        }

        /// <summary>Чем сервер объяснил последний отказ при заведении трека.</summary>
        public string LastError { get; private set; }

        /// <summary>AUTH_2FA_DETAILS: есть ли пароль и что о нём известно.</summary>
        public async Task<TwoFactorStatus> FetchStatusAsync()
        {
            string trackId = await CreateTrackAsync();
            if (string.IsNullOrEmpty(trackId))
            {
                return new TwoFactorStatus { Error = LastError ?? "Сервер не выдал трек" };
            }

            var payload = new Dictionary<string, object> { { "trackId", trackId } };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.Auth2faDetails, payload);
            if (packet == null || packet.IsError)
            {
                return new TwoFactorStatus { Error = ErrorText(packet) };
            }

            // Признак «пароль уже стоит» приходит по-разному: то полем
            // `passwordSet`, то наличием подсказки или почты.
            bool enabled = MessagePack.Long(packet.Map, "passwordSet") != 0;

            string email = MessagePack.String(packet.Map, "email");
            string hint = MessagePack.String(packet.Map, "hint");

            if (!enabled && (!string.IsNullOrEmpty(email) || !string.IsNullOrEmpty(hint)))
            {
                enabled = true;
            }

            return new TwoFactorStatus { Enabled = enabled, Email = email, Hint = hint };
        }

        /// <summary>
        /// AUTH_VALIDATE_PASSWORD — сервер проверяет пароль на пригодность
        /// (длину и словарные варианты), но ещё не устанавливает его.
        /// </summary>
        public async Task<string> ValidatePasswordAsync(string password, string trackId)
        {
            return await StepAsync(Opcodes.AuthValidatePassword, new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "password", password ?? "" }
            });
        }

        public async Task<string> ValidateHintAsync(string hint, string trackId)
        {
            return await StepAsync(Opcodes.AuthValidateHint, new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "hint", hint ?? "" }
            });
        }

        /// <summary>AUTH_VERIFY_EMAIL — на почту уходит код подтверждения.</summary>
        public async Task<string> VerifyEmailAsync(string email, string trackId)
        {
            return await StepAsync(Opcodes.AuthVerifyEmail, new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "email", email ?? "" }
            });
        }

        public async Task<string> CheckEmailCodeAsync(string code, string trackId)
        {
            // Поле называется verifyCode, а не code: под именем `code`
            // сервер кода не видит и проверить его не может.
            return await StepAsync(Opcodes.AuthCheckEmail, new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "verifyCode", code ?? "" }
            });
        }

        /// <summary>
        /// AUTH_SET_2FA: пароль устанавливается только этим вызовом, после
        /// всех проверок. Набор возможностей описывает, из чего пароль
        /// состоит: 0 — установка, 3 — подсказка, 4 — почта.
        /// </summary>
        public async Task<string> ConfirmAsync(string trackId, string password, string hint, bool withEmail)
        {
            var capabilities = new List<object> { 0L };
            if (!string.IsNullOrEmpty(hint)) capabilities.Add(3L);
            if (withEmail) capabilities.Add(4L);

            var payload = new Dictionary<string, object>
            {
                { "expectedCapabilities", capabilities },
                { "trackId", trackId ?? "" },
                { "password", password ?? "" }
            };

            if (!string.IsNullOrEmpty(hint)) payload["hint"] = hint;

            return await StepAsync(Opcodes.AuthSet2fa, payload);
        }

        /// <summary>AUTH_CHECK_PASSWORD — подтверждение текущего пароля.</summary>
        public async Task<string> CheckCurrentPasswordAsync(string password, string trackId)
        {
            return await StepAsync(Opcodes.AuthCheckPassword, new Dictionary<string, object>
            {
                { "trackId", trackId ?? "" },
                { "password", password ?? "" }
            });
        }

        /// <summary>Смена пароля: проверка нового, подсказка и AUTH_SET_2FA.</summary>
        public async Task<string> UpdatePasswordAsync(string newPassword, string hint, string trackId)
        {
            string failure = await ValidatePasswordAsync(newPassword, trackId);
            if (failure != null) return failure;

            if (!string.IsNullOrEmpty(hint))
            {
                failure = await ValidateHintAsync(hint, trackId);
                if (failure != null) return failure;
            }

            // Возможность 1 — именно смена: с нулём сервер отвечает отказом,
            // потому что пароль уже стоит.
            var capabilities = new List<object> { 1L };
            if (!string.IsNullOrEmpty(hint)) capabilities.Add(3L);

            var payload = new Dictionary<string, object>
            {
                { "expectedCapabilities", capabilities },
                { "trackId", trackId ?? "" },
                { "password", newPassword ?? "" }
            };

            if (!string.IsNullOrEmpty(hint)) payload["hint"] = hint;

            return await StepAsync(Opcodes.AuthSet2fa, payload);
        }

        /// <summary>AUTH_SET_2FA с `remove2fa` — пароль снимается.</summary>
        public async Task<string> RemovePasswordAsync(string trackId)
        {
            return await StepAsync(Opcodes.AuthSet2fa, new Dictionary<string, object>
            {
                { "expectedCapabilities", new List<object> { 5L } },
                { "trackId", trackId ?? "" },
                { "remove2fa", true }
            });
        }

        /// <summary>Ноль ошибок — пустой ответ; иначе текст для показа.</summary>
        private static async Task<string> StepAsync(int opcode, IDictionary<string, object> payload)
        {
            Packet packet = await MaxApi.Instance.RequestAsync(opcode, payload);
            if (packet == null) return "Сервер не ответил";
            if (packet.IsError) return ErrorText(packet);

            // Отказ приходит и обычным ответом с полем error — так сервер
            // сообщает, например, о слишком простом пароле.
            string error = MessagePack.String(packet.Map, "localizedMessage")
                           ?? MessagePack.String(packet.Map, "error");

            return string.IsNullOrEmpty(error) ? null : error;
        }

        private static string ErrorText(Packet packet)
        {
            if (packet == null) return "Сервер не ответил";

            string message = MessagePack.String(packet.Map, "localizedMessage")
                             ?? MessagePack.String(packet.Map, "message");

            return string.IsNullOrEmpty(message) ? "Сервер отклонил запрос" : message;
        }
    }
}
