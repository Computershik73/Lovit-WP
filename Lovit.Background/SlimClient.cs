using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Config;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Lovit.Core.Transport;

namespace Lovit.Background
{
    /// <summary>
    /// Тонкий разговор с сервером — только ради числа на плитке.
    ///
    /// Это не второй клиент: связь, разбор пакетов и приметы устройства
    /// здесь те же самые, что у приложения, — из общей библиотеки. Своего
    /// тут ровно два шага: поздороваться и войти по сохранённому токену. В
    /// ответе на вход уже лежит список чатов со счётчиками непрочитанного,
    /// и больше ничего не нужно.
    ///
    /// Слушать сообщения, качать историю и держать связь фоновому агенту
    /// незачем: система будит его на считанные секунды, и всё, что он
    /// должен успеть, — сказать «сколько».
    /// </summary>
    internal sealed class SlimClient : IDisposable
    {
        private readonly MaxConnection _connection = new MaxConnection();

        private readonly Dictionary<int, TaskCompletionSource<Packet>> _waiting =
            new Dictionary<int, TaskCompletionSource<Packet>>();

        private readonly object _lock = new object();
        private int _seq;

        /// <summary>Дольше этого ждать нельзя: у фоновой задачи считанные секунды.</summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

        public SlimClient()
        {
            _connection.PacketReceived += OnPacket;
        }

        /// <summary>
        /// Сколько чатов ждут прочтения. Отрицательное — узнать не вышло, и
        /// плитку трогать не надо: лучше прежнее число, чем ноль от неудачи.
        /// </summary>
        public async Task<int> CountUnreadAsync()
        {
            string token = AppSettings.Token;
            if (string.IsNullOrEmpty(token)) return 0;

            // Приложение на ходу — не мешаем. Мы вошли бы по тому же токену,
            // и сервер вправе счесть это заменой сессии: у человека на
            // экране всё бы оборвалось. Да и плитку он там обновляет сам.
            long silence = Now() - AppSettings.AliveAt;
            if (silence >= 0 && silence < Waiting)
            {
                Log.Write("app", "плитка в фоне: приложение на ходу, не мешаем");
                return -1;
            }

            if (!await _connection.ConnectAsync(MaxHost, MaxPort)) return -1;

            if (!await HelloAsync()) return -1;

            Packet login = await LoginAsync(token);
            if (login == null || login.IsError) return -1;

            return Count(login.Map);
        }

        /// <summary>
        /// Насколько свежей должна быть отметка приложения, чтобы счесть его
        /// живым. Минуты хватает: отметку оно ставит каждые десять секунд.
        /// </summary>
        private const long Waiting = 60 * 1000;

        private static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        /// <summary>Адрес тот же, что у приложения, — держим его при себе.</summary>
        private const string MaxHost = "api2.oneme.ru";
        private const int MaxPort = 443;

        private async Task<bool> HelloAsync()
        {
            DevicePreset device = DevicePresets.Named(AppSettings.DeviceName);

            var userAgent = new Dictionary<string, object>
            {
                { "deviceType", "ANDROID" },
                { "appVersion", ClientVersion.AppVersion },
                { "osVersion", device.OsVersion },
                { "timezone", device.Timezone },
                { "screen", device.Screen },
                { "pushDeviceType", ClientVersion.PushDeviceType },
                { "arch", ClientVersion.Architecture },
                { "locale", "ru" },
                { "buildNumber", ClientVersion.BuildNumber },
                { "deviceName", device.DeviceName },
                { "deviceLocale", "ru" }
            };

            var payload = new Dictionary<string, object>
            {
                { "mt_instanceid", DeviceIdentity.InstanceId },
                { "userAgent", userAgent },
                { "clientSessionId", DeviceIdentity.ClientSessionId },
                { "deviceId", DeviceIdentity.DeviceId }
            };

            Packet answer = await AskAsync(Opcodes.SessionInit, payload);
            return answer != null && !answer.IsError;
        }

        private async Task<Packet> LoginAsync(string token)
        {
            // Заходим невидимкой всегда, чем бы ни были настройки: человек
            // сейчас не у телефона, и показывать его в сети — обманывать
            // собеседников. Историю и присутствие не просим вовсе.
            var payload = new Dictionary<string, object>
            {
                { "token", token },
                { "interactive", false },
                { "presenceSync", -1L },
                { "chatsSync", -1L }
            };

            return await AskAsync(Opcodes.Login, payload);
        }

        /// <summary>
        /// Сосчитать непрочитанные чаты в ответе на вход.
        ///
        /// Приглушённые не в счёт, и узнаются они не по самому чату:
        /// молчание задаётся отдельно, в настройках, где на каждый чат
        /// записан час, до которого он молчит.
        /// </summary>
        private static int Count(IDictionary<string, object> login)
        {
            IList<object> chats = MessagePack.List(login, "chats");
            if (chats == null) return 0;

            IDictionary<string, object> quiet =
                MessagePack.Map(MessagePack.Map(login, "config"), "chats");

            int count = 0;

            foreach (object item in chats)
            {
                var chat = item as IDictionary<string, object>;
                if (chat == null) continue;

                if (MessagePack.Long(chat, "newMessages") <= 0) continue;

                if (quiet != null)
                {
                    object entry;
                    if (quiet.TryGetValue(MessagePack.Long(chat, "id").ToString(), out entry)
                        && MessagePack.Long(entry as IDictionary<string, object>,
                                            "dontDisturbUntil") != 0)
                    {
                        continue;
                    }
                }

                count++;
            }

            return count;
        }

        private async Task<Packet> AskAsync(int opcode, IDictionary<string, object> payload)
        {
            if (!_connection.IsConnected) return null;

            var waiter = new TaskCompletionSource<Packet>();
            int seq;

            lock (_lock)
            {
                _seq = (_seq + 1) & 0xFFFF;
                if (_seq == 0) _seq = 1;

                seq = _seq;
                _waiting[seq] = waiter;
            }

            _connection.Send(Packet.Pack(opcode, payload, seq));

            Task done = await Task.WhenAny(waiter.Task, Task.Delay(Patience));

            lock (_lock) _waiting.Remove(seq);

            return done == waiter.Task ? waiter.Task.Result : null;
        }

        private void OnPacket(Packet packet)
        {
            TaskCompletionSource<Packet> waiter = null;

            lock (_lock)
            {
                if (packet.Seq != 0 && _waiting.TryGetValue(packet.Seq, out waiter))
                {
                    _waiting.Remove(packet.Seq);
                }
            }

            if (waiter != null) waiter.TrySetResult(packet);
        }

        public void Dispose()
        {
            _connection.PacketReceived -= OnPacket;

            try { _connection.Close(); }
            catch (Exception) { }

            // Всех, кто ещё ждёт, отпускаем пустотой: иначе ожидание
            // досидит до своего срока уже после конца задачи.
            lock (_lock)
            {
                foreach (TaskCompletionSource<Packet> waiter in _waiting.Values)
                {
                    waiter.TrySetResult(null);
                }

                _waiting.Clear();
            }
        }
    }
}
