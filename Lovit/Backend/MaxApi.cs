using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Config;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Lovit.Core.Transport;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Lovit.Backend
{
    public enum SessionState { Disconnected, Connecting, Online }

    /// <summary>
    /// Сессия с сервером: соединение, рукопожатие, запросы и пуши.
    /// Порт MAXApi.
    ///
    /// Каждый запрос уходит со своим порядковым номером, и ответ находится
    /// по нему же — сервер отвечает вразнобой. Пакеты с чужим номером или
    /// нулевым — это пуши, они уходят подписчикам.
    /// </summary>
    public class MaxApi
    {
        private static readonly MaxApi _instance = new MaxApi();
        public static MaxApi Instance { get { return _instance; } }

        public const string Host = "api2.oneme.ru";
        public const int Port = 443;

        private readonly MaxConnection _connection = new MaxConnection();
        private readonly Dictionary<int, TaskCompletionSource<Packet>> _pending =
            new Dictionary<int, TaskCompletionSource<Packet>>();

        /// <summary>
        /// Номера отправленного без ожидания. Ответа на такое не ждут, но
        /// если он вдруг придёт — это не «ничей ответ», а именно наш, и
        /// пугать им журнал незачем.
        /// </summary>
        private readonly HashSet<int> _posted = new HashSet<int>();

        private DispatcherTimer _pingTimer;
        private DispatcherTimer _reconnectTimer;
        private int _seq;
        private bool _connecting;
        private bool _autoReconnect;
        private int _reconnectAttempts;
        private string _sessionError;

        public SessionState State { get; private set; }

        /// <summary>
        /// Счёт сессий: растёт с каждым рукопожатием.
        ///
        /// Сервер привязывает многошаговые разговоры к сессии, и после
        /// обрыва со мгновенным восстановлением всё выданное в прошлой
        /// становится негодным — но со стороны клиента связь при этом
        /// выглядит целой. По этому числу и видно, та ли она.
        /// </summary>
        public int Session { get; private set; }

        /// <summary>
        /// Сервер считает версию клиента устаревшей. Лечится не здесь, а
        /// в [ClientVersion]: числа надо поднять до нынешних.
        /// </summary>
        public bool IsOutdated { get; private set; }

        /// <summary>Из ответа на приветствие; вместе с deviceId даёт отпечаток сборки.</summary>
        public long CallsSeed { get; private set; }

        public bool HasCallsSeed { get; private set; }

        /// <summary>Признак устройства, ушедший в приветствии.</summary>
        public string DeviceId { get { return DeviceIdentity.DeviceId; } }

        /// <summary>
        /// Подпись для заливки вложений. Собирается из тех же полей, что
        /// ушли в приветствии, — иначе сессия и загрузка выглядели бы для
        /// сервера разными устройствами. Порт httpUserAgent из MAXUploader.
        /// </summary>
        public string HttpUserAgent
        {
            get
            {
                DevicePreset device = DevicePresets.Named(AppSettings.DeviceName);

                return "OKMessages/" + ClientVersion.AppVersion
                       + " (" + device.OsVersion
                       + "; " + device.DeviceName
                       + "; " + device.Screen + ")";
            }
        }

        /// <summary>
        /// Отпечаток сборки для запроса кода и входа. Пусто, пока не пришёл
        /// ответ на приветствие.
        /// </summary>
        public byte[] Fingerprint
        {
            get
            {
                if (!HasCallsSeed || string.IsNullOrEmpty(DeviceId)) return null;
                return ChatCacheFingerprint.Compute(CallsSeed, DeviceId);
            }
        }

        /// <summary>Чем поднято соединение — показывается в настройках.</summary>
        public string TransportDescription
        {
            get { return _connection.TransportDescription; }
        }

        /// <summary>Почему не поднялось — показывает экран входа.</summary>
        public string LastError
        {
            get
            {
                // Отказ сервера важнее обрыва связи: если сессию отклонили,
                // соединение как раз было в полном порядке.
                return !string.IsNullOrEmpty(_sessionError) ? _sessionError : _connection.LastError;
            }
        }

        /// <summary>Состояние изменилось — экраны обновляют заголовок.</summary>
        public event Action StateChanged;

        /// <summary>Пуш от сервера: новое сообщение, изменение чата и прочее.</summary>
        public event Action<Packet> PushReceived;

        /// <summary>Сессия поднялась заново — пора повторить вход.</summary>
        public event Action Reconnected;

        private MaxApi()
        {
            _connection.PacketReceived += OnPacket;
            _connection.Closed += OnClosed;
            _connection.PacketLost += OnPacketLost;
        }

        public async Task ConnectAsync()
        {
            if (_connecting || State == SessionState.Online) return;

            // С этого мгновения обрывы чинятся сами: пока пользователь не
            // вышел из аккаунта, приложение обязано возвращаться в сеть.
            _autoReconnect = true;

            _connecting = true;
            SetState(SessionState.Connecting);

            bool connected = await _connection.ConnectAsync(Host, Port);
            _connecting = false;

            if (!connected)
            {
                SetState(SessionState.Disconnected);
                ScheduleReconnect();
                return;
            }

            await HandshakeAsync();
        }

        #region Переподключение

        /// <summary>
        /// Завести переподключение.
        ///
        /// Обрыв связи на телефоне — не исключение, а обычное дело: метро,
        /// лифт, блокировка экрана. Порт scheduleReconnect из iOS-версии:
        /// задержка удваивается с каждой неудачей, но не растёт дальше
        /// пятнадцати секунд — иначе после долгого отсутствия сети
        /// приложение просыпалось бы минутами.
        ///
        /// Заводится только с потока окна: таймер разметки заводится, запускается и
        /// останавливается там, где живёт окно, а из чужого потока каждое из
        /// этих действий — исключение. Поймать его некому: обрыв замечает
        /// поток чтения сокета, а исключение в чужом потоке уносит с собой
        /// всё приложение, не оставив в журнале ни строчки. Так и выглядело
        /// «выключил телефон, включил — и вылетело».
        /// </summary>
        private void ScheduleReconnect()
        {
            RunOnUi(ScheduleReconnectHere);
        }

        private void ScheduleReconnectHere()
        {
            if (!_autoReconnect || _reconnectTimer != null) return;

            int shift = _reconnectAttempts < 3 ? _reconnectAttempts : 3;
            int delay = 2 << shift;
            if (delay > 15) delay = 15;

            _reconnectAttempts++;

            _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            _reconnectTimer.Tick += async (sender, argument) =>
            {
                StopReconnectTimer();

                if (!_autoReconnect || State != SessionState.Disconnected) return;
                await ConnectAsync();
            };

            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            RunOnUi(() =>
            {
                if (_reconnectTimer == null) return;

                _reconnectTimer.Stop();
                _reconnectTimer = null;
            });
        }

        /// <summary>
        /// Приложение вернулось на экран. Спящий телефон рвёт сокет молча —
        /// со стороны клиента соединение выглядит живым, пока в него не
        /// напишут. Поэтому здесь либо сразу подключаемся, либо проверяем
        /// живость пингом.
        /// </summary>
        public async void WakeUp()
        {
            if (!_autoReconnect) return;

            // Возвращений приходит два разом: система шлёт и Resuming, и
            // появление окна. Второму здесь делать нечего — он застанет
            // соединение на полпути.
            if (_waking) return;
            _waking = true;

            try
            {
                // След в журнале нарочно подробный: до сих пор о пробуждении
                // была одна строка, и падение сразу после неё не оставляло
                // ни малейшего намёка, на каком шаге всё кончилось.
                Log.Write("api", "пробуждение: состояние " + State);

                if (State == SessionState.Disconnected)
                {
                    _reconnectAttempts = 0;
                    StopReconnectTimer();

                    await ConnectAsync();

                    Log.Write("api", "пробуждение: соединялись заново, вышло " + State);
                    return;
                }

                if (State == SessionState.Online)
                {
                    await ProbeAsync();
                    Log.Write("api", "пробуждение: пинг прошёл, состояние " + State);
                }
            }
            catch (Exception error)
            {
                // Поймать исключение отсюда больше некому: у метода без
                // возврата его подхватывает система и закрывает приложение.
                // А случиться тут может всякое — спящий телефон рвёт сокет
                // молча, и первое обращение после сна натыкается на обломки.
                Log.Fail("api", "пробуждение", error);
            }
            finally
            {
                _waking = false;
            }
        }

        /// <summary>Идёт ли возвращение в сеть прямо сейчас.</summary>
        private bool _waking;

        /// <summary>Пробный пинг: не ответил — значит соединения уже нет.</summary>
        private async Task ProbeAsync()
        {
            if (State != SessionState.Online) return;

            var payload = new Dictionary<string, object>
            {
                { "interactive", !AppSettings.GhostMode }
            };

            Packet answer = await RequestAsync(Opcodes.Ping, payload);
            if (answer != null || State != SessionState.Online) return;

            _connection.Close();
            OnClosed();
        }

        #endregion

        /// <summary>
        /// Дождаться готовой сессии. Соединение поднимается при запуске
        /// приложения, но экран может открыться раньше, чем оно встанет, и
        /// тогда любой запрос молча вернёт пустоту.
        /// </summary>
        public async Task<bool> EnsureOnlineAsync()
        {
            if (State == SessionState.Online) return true;

            await ConnectAsync();

            // ConnectAsync возвращается сразу, если соединение уже поднимает
            // кто-то другой, — тогда остаётся только ждать его рукопожатия.
            for (int attempt = 0; attempt < 100 && State != SessionState.Online; attempt++)
            {
                await Task.Delay(100);
            }

            return State == SessionState.Online;
        }

        /// <summary>
        /// Разрыв по своей воле: смена устройства в настройках, выход из
        /// аккаунта. Автоматических попыток после него быть не должно —
        /// иначе приложение полезло бы обратно в сеть само.
        /// </summary>
        public void Disconnect()
        {
            _autoReconnect = false;

            StopReconnectTimer();
            StopPing();

            _connection.Close();
            SetState(SessionState.Disconnected);
        }

        /// <summary>
        /// SESSION_INIT — первое, что уходит после рукопожатия TLS. До ответа
        /// сервер не примет ни одного другого запроса.
        ///
        /// Приложение представляется Android-клиентом: для iOS у MAX свои
        /// проверки версии, а под Windows Phone клиента нет вовсе, и любой
        /// честный ответ здесь означал бы отказ.
        /// </summary>
        private async Task HandshakeAsync()
        {
            DevicePreset device = DevicePresets.Named(AppSettings.DeviceName);

            // Версия — не для порядка: сервер сверяет её со своей и отвечает
            // «приложение устарело, обновитесь», если сборка старая. Всё
            // остальное — приметы того телефона, которым мы притворяемся.
            //
            // Оба поля с языком русские, и это не забывчивость: часть строк
            // сервер отдаёт уже переведёнными — названия системных папок,
            // например, — и переводит их по языку устройства. Испанский
            // телефон приносил в русское окно папку «Canales».
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

            Packet response = await RequestAsync(Opcodes.SessionInit, payload);
            if (response == null)
            {
                _sessionError = "сервер не ответил на приветствие";
                Disconnect();
                return;
            }

            // Отказ приветствия — это отдельная беда, и её нельзя проглатывать:
            // именно так сервер говорит «приложение устарело». Без разбора
            // ответа сессия считалась бы поднятой, а вход потом падал бы
            // непонятно от чего.
            if (response.IsError)
            {
                _sessionError = MessagePack.String(response.Map, "localizedMessage")
                                ?? MessagePack.String(response.Map, "message")
                                ?? "сервер отклонил приветствие";
                Disconnect();
                return;
            }

            _sessionError = null;
            Log.Write("api", "приветствие принято, устройство «" + device.DeviceName
                             + "», сборка " + ClientVersion.AppVersion
                             + " (" + ClientVersion.BuildNumber + ")");

            // Сервер не отказывает устаревшему клиенту сразу: он отдаёт
            // урезанную настройку с `app-update-type`, а отказывает потом —
            // при входе, словами «приложение устарело, обновитесь». Ловим
            // признак здесь, чтобы причина была видна до того, как всё
            // сломается.
            IsOutdated = MessagePack.Long(response.Map, "app-update-type") != 0;

            // callsSeed из ответа вместе с признаком устройства даёт отпечаток
            // сборки — без него сервер считает клиента незнакомым и, например,
            // не отправляет код в SMS.
            if (response.Map != null && response.Map.ContainsKey("callsSeed"))
            {
                CallsSeed = MessagePack.Long(response.Map, "callsSeed");
                HasCallsSeed = true;
            }

            // Каждое рукопожатие — новая сессия. Тот, кто ведёт разговор в
            // несколько шагов (вход по коду и паролю — как раз такой),
            // должен уметь заметить, что сессия под ним сменилась: всё,
            // что сервер выдал в прошлой, в новой уже недействительно.
            Session++;

            SetState(SessionState.Online);
            StartPing();

            Action handler = Reconnected;
            if (handler != null) handler();
        }

        /// <summary>
        /// Запрос с ожиданием ответа.
        ///
        /// Номер выдаётся под замком, и список ожидающих тоже: экран
        /// переписки шлёт историю, подписку и сведения почти одновременно,
        /// а читает ответы поток сокета. Без замка два запроса иногда
        /// получали один номер, и один из ответов терялся — история при
        /// открытии чата не приходила через раз.
        /// </summary>
        /// <summary>
        /// Отправить и не ждать ответа.
        ///
        /// На отметку о наборе текста и на подписку сервер не отвечает
        /// вовсе. Ждали мы их наравне со всеми — и каждая полминуты висела
        /// в очереди, а потом ложилась в журнал строкой «ответа нет за
        /// 30 с». Строк этих набиралось больше, чем всего остального, и
        /// разбирать по такому журналу настоящую беду становилось нечем.
        ///
        /// В Komet так же: набор уходит без ожидания и с проглоченной
        /// ошибкой, подписка помечена «молча».
        /// </summary>
        public void Post(int opcode, IDictionary<string, object> payload)
        {
            if (!_connection.IsConnected) return;

            int seq;

            lock (_pending)
            {
                _seq = (_seq + 1) & 0xFFFF;
                if (_seq == 0) _seq = 1;

                seq = _seq;

                // Список нужен только чтобы узнать нежданный ответ. Держать
                // его целиком незачем: номера идут подряд, и старое в нём
                // просто копилось бы.
                if (_posted.Count > 64) _posted.Clear();
                _posted.Add(seq);
            }

            Log.Write("api", "→ опкод " + opcode + " №" + seq
                             + " {" + Log.Keys(payload) + "} — ответа не ждём");

            _connection.Send(Packet.Pack(opcode, payload, seq));
        }

        public async Task<Packet> RequestAsync(int opcode, IDictionary<string, object> payload)
        {
            if (!_connection.IsConnected)
            {
                // Числимся в сети, а соединения уже нет — значит, обрыв
                // прошёл мимо. Раньше такой рассинхрон не лечился ничем:
                // каждый запрос возвращал пустоту сразу, чат открывался
                // пустым, и помогал только перезапуск. Считаем это обрывом
                // и поднимаемся заново.
                Log.Write("api", "запрос " + opcode + " некуда слать: связи нет, состояние " + State);

                if (State == SessionState.Online) OnClosed();
                return null;
            }

            var completion = new TaskCompletionSource<Packet>();
            int seq;

            lock (_pending)
            {
                _seq = (_seq + 1) & 0xFFFF;

                // Ноль означает «пуш», и своим запросам он не достаётся.
                if (_seq == 0) _seq = 1;

                seq = _seq;
                _pending[seq] = completion;
            }

            // Пинг уходит каждые десять секунд и в журнале только мешает:
            // за полчаса он вытеснил бы всё, ради чего журнал заводился.
            // Про него пишем, лишь когда он не удался.
            bool quiet = opcode == Opcodes.Ping;

            if (!quiet)
            {
                Log.Write("api", "→ опкод " + opcode + " №" + seq + " {" + Log.Keys(payload) + "}");
            }

            _connection.Send(Packet.Pack(opcode, payload, seq));

            // Ответа может не быть вовсе — сервер молчит, соединение висит.
            // Тогда ждать вечно нельзя: экран так и останется с крутилкой.
            //
            // Но молчание и медленный ответ — разные вещи, и отличать их
            // надо. Ответ на вход у большого аккаунта идёт мегабайтами, и
            // на неспешной сети он не успевает за отведённые полминуты:
            // приложение считало это тишиной и показывало «сервер не
            // ответил», хотя данные всё это время шли.
            //
            // Поэтому ждём кусками и продлеваем ожидание, пока из потока
            // идут байты. Совсем без предела нельзя — за ним стоит человек
            // с крутилкой на экране.
            Packet answer = await WaitAsync(completion.Task, opcode, seq);

            lock (_pending) _pending.Remove(seq);

            if (answer == null) return null;

            if (answer == null) Log.Write("api", "✗ опкод " + opcode + " №" + seq + " — сессия закрылась");
            else if (answer.IsError) Log.Write("api", "✗ опкод " + opcode + " №" + seq
                                                      + " — отказ {" + Log.Keys(answer.Map) + "}");
            else if (!quiet) Log.Write("api", "← опкод " + opcode + " №" + seq
                                             + " {" + Log.Keys(answer.Map) + "}");

            return answer;
        }

        /// <summary>Сколько ждать ответа, пока из сети ничего не идёт.</summary>
        private static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Общий предел ожидания. Дальше ждать бессмысленно: человек уже
        /// решил, что приложение сломалось.
        /// </summary>
        private static readonly TimeSpan Longest = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Дождаться ответа, продлевая ожидание, пока идут данные.
        ///
        /// Проверяем счётчик прочитанного: растёт — значит сервер отвечает,
        /// просто ответ длинный; стоит на месте полминуты — значит тишина,
        /// и ждать больше нечего.
        /// </summary>
        private async Task<Packet> WaitAsync(Task<Packet> answer, int opcode, int seq)
        {
            DateTime began = DateTime.UtcNow;
            long seen = _connection.BytesRead;

            while (true)
            {
                Task finished = await Task.WhenAny(answer, Task.Delay(Silence));
                if (finished == answer) return answer.Result;

                long now = _connection.BytesRead;
                bool moving = now != seen;

                if (moving)
                {
                    Log.Write("api", "опкод " + opcode + " №" + seq + " — ответа ещё нет, но с сети пришло "
                                     + ((now - seen) / 1024) + " КБ, ждём дальше");
                }

                seen = now;

                if (!moving || DateTime.UtcNow - began > Longest)
                {
                    Log.Write("api", "✗ опкод " + opcode + " №" + seq
                                     + " — ответа нет за " + (int)(DateTime.UtcNow - began).TotalSeconds
                                     + " с, всего с сети " + (_connection.BytesRead / 1024)
                                     + " КБ, в очереди ещё " + PendingCount);
                    return null;
                }
            }
        }

        /// <summary>Сколько запросов ещё ждут ответа — видно в журнале.</summary>
        private int PendingCount
        {
            get { lock (_pending) return _pending.Count; }
        }

        private void OnPacket(Packet packet)
        {
            TaskCompletionSource<Packet> completion = null;

            if (packet.Seq != 0)
            {
                lock (_pending)
                {
                    if (_pending.TryGetValue(packet.Seq, out completion)) _pending.Remove(packet.Seq);
                }
            }

            if (completion != null)
            {
                completion.TrySetResult(packet);
                return;
            }

            // Ответ, которого никто не ждёт, — это либо пуш, либо
            // разъехавшаяся нумерация. Второе объясняло бы тишину в ответ на
            // запрос, поэтому различаем их в журнале. Признак пуша — вид
            // пакета, а не нулевой номер: сервер шлёт пуши и с номером.
            bool ours;
            lock (_pending) ours = _posted.Remove(packet.Seq);

            if (packet.Seq != 0 && !packet.IsPush && !ours)
            {
                Log.Write("api", "ничей ответ: опкод " + packet.Opcode
                                 + " №" + packet.Seq + ", вид " + packet.Cmd);
            }
            else
            {
                Log.Write("push", "опкод " + packet.Opcode + " {" + Log.Keys(packet.Map) + "}");
            }

            Action<Packet> handler = PushReceived;
            if (handler != null) RunOnUi(() => handler(packet));
        }

        /// <summary>
        /// Сессия кончилась: разбудить всех, кто ждал ответа, и вернуться
        /// в сеть.
        ///
        /// Сюда попадают по нескольку раз подряд, и это не случайность.
        /// Ожидающий просыпается прямо здесь же: `TrySetResult` продолжает
        /// его на этом самом потоке — тот видит пустой ответ, считает связь
        /// оборванной и зовёт нас снова. Второй заход заставал список
        /// ожидающих посреди перебора, и приложение падало на «Collection
        /// was modified»; в журнале это видно тремя строками «сессия
        /// закрылась» подряд — с двумя ожидающими, с одним и ни с одним.
        ///
        /// Поэтому порядок такой: сперва состояние — по нему второй заход
        /// и разворачивается на первой же строке; потом список забирается
        /// целиком и очищается под замком; и только после этого раздаются
        /// ответы — уже вне замка и вне всякого перебора.
        /// </summary>
        /// <summary>
        /// Ответ пришёл и не разобрался. Ждать его дальше бессмысленно:
        /// второго такого же не будет.
        /// </summary>
        private void OnPacketLost(int seq)
        {
            TaskCompletionSource<Packet> waiting = null;

            lock (_pending)
            {
                if (_pending.TryGetValue(seq, out waiting)) _pending.Remove(seq);
            }

            if (waiting == null) return;

            Log.Write("api", "✗ №" + seq + " — ответ пришёл, но не разобрался");
            waiting.TrySetResult(null);
        }

        private void OnClosed()
        {
            if (State == SessionState.Disconnected) return;

            Log.Write("api", "сессия закрылась, не дождались ответов: " + PendingCount);

            SetState(SessionState.Disconnected);
            StopPing();

            TaskCompletionSource<Packet>[] waiting;

            lock (_pending)
            {
                waiting = new TaskCompletionSource<Packet>[_pending.Count];
                _pending.Values.CopyTo(waiting, 0);
                _pending.Clear();
            }

            foreach (TaskCompletionSource<Packet> completion in waiting) completion.TrySetResult(null);

            // Обрыв не по своей воле — возвращаемся в сеть сами.
            ScheduleReconnect();
        }

        #region Пинг

        /// <summary>
        /// Пинг раз в десять секунд. Он не только держит соединение живым:
        /// в нём же уезжает признак «пользователь у экрана», а режим невидимки
        /// как раз им и управляет.
        /// </summary>
        private void StartPing()
        {
            RunOnUi(StartPingHere);
        }

        private void StartPingHere()
        {
            StopPing();

            _reconnectAttempts = 0;

            _pingTimer = new DispatcherTimer();
            _pingTimer.Interval = TimeSpan.FromSeconds(10);
            _pingTimer.Tick += async (sender, argument) =>
            {
                var payload = new Dictionary<string, object>
                {
                    { "interactive", !AppSettings.GhostMode }
                };

                // Пинг ходит раз в десять секунд — удобный случай отметить,
                // сколько памяти занято. По одной точке ничего не понять, а
                // по такой цепочке видно, растёт ли расход и когда.
                Log.MemoryIfChanged("пинг");

                // И заодно — что приложение на ходу. По этой отметке фоновый
                // агент понимает, что лезть на сервер сейчас незачем: вход
                // по тому же токену оборвал бы сессию, которая на экране.
                AppSettings.AliveAt = UnixTime.Now;

                Packet answer = await RequestAsync(Opcodes.Ping, payload);

                // Молчание в ответ на пинг — тот случай, когда сокет для нас
                // ещё жив, а на самом деле его давно нет: телефон уснул,
                // сеть сменилась. Рвём сами, чтобы включилось переподключение.
                if (answer == null && State == SessionState.Online)
                {
                    _connection.Close();
                    OnClosed();
                }
            };
            _pingTimer.Start();
        }

        private void StopPing()
        {
            RunOnUi(() =>
            {
                if (_pingTimer == null) return;

                _pingTimer.Stop();
                _pingTimer = null;
            });
        }

        #endregion

        private void SetState(SessionState state)
        {
            if (State == state) return;

            Log.Write("api", "состояние: " + State + " → " + state);
            State = state;

            Action handler = StateChanged;
            if (handler != null) RunOnUi(handler);
        }

        /// <summary>
        /// Поток окна, запомненный при запуске.
        ///
        /// Спросить его через `Window.Current` можно только из него самого:
        /// в чужом потоке там пусто. А звать сюда приходится как раз из
        /// чужих — из чтения сокета и из пробуждения приложения, — и без
        /// этой памятки перевод на поток окна молча не срабатывал.
        /// </summary>
        private static CoreDispatcher _ui;

        internal static void RememberUi(CoreDispatcher dispatcher)
        {
            _ui = dispatcher;
        }

        /// <summary>
        /// Чтение сокета идёт не в потоке интерфейса, а подписчики почти
        /// всегда трогают экран. Проще перевести всё сюда один раз, чем
        /// помнить об этом в каждом обработчике.
        /// </summary>
        internal static void RunOnUi(Action action)
        {
            CoreDispatcher dispatcher = _ui
                                        ?? (Window.Current != null
                                                ? Window.Current.Dispatcher
                                                : null);

            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
