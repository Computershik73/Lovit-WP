using System;
using System.Collections.Generic;
using System.Text;
using Lovit.Core.Protocol;
using Windows.Data.Json;

namespace Lovit.Backend.Calls
{
    /// <summary>
    /// Параметры подключения к звонку — `vcp` из пуша о входящем (опкод 137).
    ///
    /// Звонок живёт в двух протоколах. Основной сокет MAX только сообщает,
    /// что звонят, и отдаёт эти параметры; всё остальное — принять, сбросить,
    /// договориться о медиа — идёт уже по отдельному WebSocket сигнализации
    /// (CallSignaling), адрес и токен которого лежат здесь.
    ///
    /// Формат: `&lt;длина&gt;:&lt;base64(LZ4-блок(JSON))&gt;`, ключи в JSON короткие.
    /// Расшифровка повторяет kolibri (KometTeam/kolibri,
    /// kolibri-net/src/calls/vcp.rs) — ядро, на котором звонки держит Komet.
    /// </summary>
    public sealed class CallParams
    {
        /// <summary>Токен в сигнализации звонка.</summary>
        public string Token { get; private set; }

        /// <summary>WebSocket сигнализации, например wss://videowebrtc.okcdn.ru/ws2.</summary>
        public string WsEndpoint { get; private set; }

        public string Stun { get; private set; }
        public List<string> Turn { get; private set; }
        public string TurnUser { get; private set; }
        public string TurnPassword { get; private set; }

        /// <summary>Когда параметры перестают действовать, unix-секунды; 0 — неизвестно.</summary>
        public long ExpiresAt { get; private set; }

        public bool IsVideo { get; private set; }

        /// <summary>
        /// Свой номер в системе звонков — он не совпадает с номером в MAX.
        /// Лежит хвостом в имени для TURN, после последнего двоеточия.
        /// </summary>
        public long CallsUserId
        {
            get
            {
                if (string.IsNullOrEmpty(TurnUser)) return 0;
                string tail = TurnUser.Substring(TurnUser.LastIndexOf(':') + 1);

                long id;
                return long.TryParse(tail, out id) ? id : 0;
            }
        }

        /// <summary>null — строка не разобралась.</summary>
        public static CallParams Decode(string vcp)
        {
            if (string.IsNullOrEmpty(vcp)) return null;

            int colon = vcp.IndexOf(':');
            if (colon <= 0) return null;

            int rawLength;
            if (!int.TryParse(vcp.Substring(0, colon), out rawLength) || rawLength <= 0) return null;

            try
            {
                byte[] packed = Convert.FromBase64String(vcp.Substring(colon + 1));
                byte[] raw = Lz4.Decompress(packed, rawLength);
                if (raw == null || raw.Length == 0) return null;

                int length = Math.Min(raw.Length, rawLength);
                JsonObject map = JsonObject.Parse(Encoding.UTF8.GetString(raw, 0, length));

                var made = new CallParams
                {
                    Token = Text(map, "tkn"),
                    WsEndpoint = Text(map, "wse"),
                    Stun = Text(map, "stne"),
                    TurnUser = Text(map, "trnu"),
                    TurnPassword = Text(map, "trnp"),
                    Turn = new List<string>()
                };

                // TURN-серверы приходят одной строкой через запятую.
                string turn = Text(map, "trne");
                if (!string.IsNullOrEmpty(turn))
                {
                    foreach (string item in turn.Split(','))
                    {
                        string trimmed = item.Trim();
                        if (trimmed.Length > 0) made.Turn.Add(trimmed);
                    }
                }

                IJsonValue expires;
                if (map.TryGetValue("et", out expires) && expires.ValueType == JsonValueType.Number)
                    made.ExpiresAt = (long)expires.GetNumber();

                IJsonValue video;
                if (map.TryGetValue("iv", out video) && video.ValueType == JsonValueType.Boolean)
                    made.IsVideo = video.GetBoolean();

                if (string.IsNullOrEmpty(made.Token) || string.IsNullOrEmpty(made.WsEndpoint)) return null;
                return made;
            }
            catch (Exception)
            {
                // Кривой base64, LZ4 или JSON — вызывающему всё равно, какой
                // именно: звонок без параметров принять нельзя.
                return null;
            }
        }

        /// <summary>
        /// Адрес сигнализации для входящего звонка.
        ///
        /// Клиентом называемся тем же, кем Komet: платформа ANDROID, клиент
        /// ONE_ME. С другими значениями сервер сокет принимает, но что он
        /// станет с таким участником делать дальше — не проверено, а эти
        /// испытаны на живых звонках.
        /// </summary>
        public string Ws2Url(string conversationId)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                Pair("userId", CallsUserId.ToString()),
                Pair("entityType", "USER"),
                Pair("conversationId", conversationId ?? ""),
                Pair("token", Token),
                Pair("version", "5"),
                Pair("capabilities", "3c02f"),
                Pair("device", "Android/Unknown"),
                Pair("platform", "ANDROID"),
                Pair("clientType", "ONE_ME"),
                Pair("appVersion", "sdk-0.2.1.3"),
                Pair("osVersion", "34")
            };

            int question = WsEndpoint.IndexOf('?');
            string path = question < 0 ? WsEndpoint : WsEndpoint.Substring(0, question);

            var built = new StringBuilder(path).Append('?');
            for (int i = 0; i < query.Count; i++)
            {
                if (i > 0) built.Append('&');
                built.Append(query[i].Key).Append('=').Append(Escape(query[i].Value));
            }
            return built.ToString();
        }

        /// <summary>Для журнала: откуда и через что, без токенов и паролей.</summary>
        public string Describe()
        {
            return "сигнализация " + HostOf(WsEndpoint)
                   + ", STUN " + (string.IsNullOrEmpty(Stun) ? "нет" : HostOf(Stun))
                   + ", TURN " + Turn.Count
                   + (IsVideo ? ", с видео" : ", голос");
        }

        private static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "?";
            int start = url.IndexOf("//", StringComparison.Ordinal);
            string rest = start >= 0 ? url.Substring(start + 2) : url;
            if (rest.StartsWith("stun:") || rest.StartsWith("turn:")) rest = rest.Substring(5);
            int end = rest.IndexOfAny(new[] { '/', '?' });
            return end >= 0 ? rest.Substring(0, end) : rest;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? "");
        }

        /// <summary>Процентами всё, кроме латиницы, цифр и «-._~».</summary>
        private static string Escape(string text)
        {
            var made = new StringBuilder();
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                bool plain = (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z')
                             || (value >= '0' && value <= '9')
                             || value == '-' || value == '.' || value == '_' || value == '~';

                if (plain) made.Append((char)value);
                else made.Append('%').Append(value.ToString("X2"));
            }
            return made.ToString();
        }

        private static string Text(JsonObject map, string key)
        {
            IJsonValue value;
            if (!map.TryGetValue(key, out value) || value.ValueType != JsonValueType.String) return null;
            return value.GetString();
        }
    }
}
