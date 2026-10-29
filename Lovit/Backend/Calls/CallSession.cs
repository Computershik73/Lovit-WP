using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Windows.Data.Json;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Lovit.Backend.Calls
{
    public enum CallState
    {
        /// <summary>Звоним сами — у собеседника звонит, он ещё не ответил.</summary>
        Calling,
        /// <summary>Приняли, ищем путь до собеседника.</summary>
        Connecting,
        /// <summary>Звук идёт.</summary>
        Active,
        /// <summary>Всё.</summary>
        Ended
    }

    /// <summary>
    /// Принятый звонок от начала до отбоя. Порт того, что в Komet делает
    /// CallSession (lib/core/calls/call_session.dart) для отвечающей стороны,
    /// но без libwebrtc: медиа — своё ядро (CallCore), звук — CallAudio.
    ///
    /// Порядок, как у Komet и бота kolibri:
    ///   1. открыть сигнализацию (ws2) и сказать `accept-call`;
    ///   2. `connection` — кто собеседник в системе звонков;
    ///   3. `transmitted-data` с предложением SDP — ответить своим SDP, затем
    ///      своими кандидатами ICE; чужие кандидаты приходят туда же;
    ///   4. ядро находит путь, договаривается по DTLS — пошёл звук;
    ///   5. отбой: свой — `hangup`, чужой — уведомление `hungup`.
    ///
    /// Потоки: сигнализация зовёт на своём потоке чтения, ядро живёт на
    /// своём, экран — на потоке разметки. Ядро трогает только свой поток:
    /// всё остальное передаёт ему поручения через очередь.
    /// </summary>
    public sealed class CallSession
    {
        /// <summary>
        /// Сколько ждать, пока собеседник ответит. Сервер и сам бросает
        /// неотвеченный звонок примерно через минуту.
        /// </summary>
        private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(60);

        private readonly CallParams _params;     // входящий: параметры из пуша
        private readonly string _ws2Url;         // исходящий: сигнализация из ответа на звонок
        private readonly long _myCallsId;
        private readonly CallSignaling _signaling = new CallSignaling();
        private bool _offerSent;
        private bool _accepted;

        private readonly Queue<Action<CallCore>> _commands = new Queue<Action<CallCore>>();
        private readonly Queue<byte[]> _outgoing = new Queue<byte[]>();
        private readonly List<string> _pendingCandidates = new List<string>();
        private readonly object _lock = new object();

        private volatile bool _stopping;
        private bool _answered;
        private int _candidateCount;
        private string _localUfrag;

        private long _peerId;
        private string _peerType = "USER";
        private int _peerDeviceIdx;

        private JitterBuffer _jitter;
        private CallMicrophone _microphone;
        private CallSpeaker _speaker;
        private MediaElement _player;
        private bool _mediaStarted;

        public string ConversationId { get; private set; }
        /// <summary>Собеседник в MAX — для имени и аватарки.</summary>
        public long PeerUserId { get; private set; }
        public bool IsVideo { get; private set; }

        public CallState State { get; private set; }
        public DateTime ActiveSince { get; private set; }
        public string EndReason { get; private set; }
        public bool Muted { get; private set; }

        /// <summary>Что-то поменялось — на потоке разметки.</summary>
        public event Action<CallSession> Changed;

        /// <summary>Звоним сами.</summary>
        public bool IsOutgoing { get; private set; }

        internal CallSession(IncomingCall call)
        {
            _params = call.Params;
            _myCallsId = call.Params.CallsUserId;
            ConversationId = call.ConversationId;
            PeerUserId = call.CallerId;
            IsVideo = call.IsVideo;
            State = CallState.Connecting;
        }

        /// <summary>
        /// Исходящий. `ws2Url` и свой номер в системе звонков — из ответа на
        /// VIDEO_CHAT_START_ACTIVE; адреса STUN и TURN придут позже, в
        /// уведомлении `connection`.
        /// </summary>
        internal CallSession(string conversationId, long peerUserId, string ws2Url, long myCallsId)
        {
            _ws2Url = ws2Url;
            _myCallsId = myCallsId;
            ConversationId = conversationId;
            PeerUserId = peerUserId;
            IsOutgoing = true;
            State = CallState.Calling;
        }

        #region Начало

        public async Task StartAsync()
        {
            _signaling.Notification += OnNotification;
            _signaling.Closed += OnSignalingClosed;

            string url = IsOutgoing ? _ws2Url : _params.Ws2Url(ConversationId);
            if (!await _signaling.ConnectAsync(url))
            {
                End("нет связи с сервером звонков");
                return;
            }

            _jitter = new JitterBuffer();

            List<string> hosts = LocalAddresses();
            var loop = Task.Factory.StartNew(() => Run(hosts), TaskCreationOptions.LongRunning);

            if (IsOutgoing)
            {
                // Звоним: принимаем «свой» звонок после того, как отправим
                // предложение, — так у Komet. Ответа ждём не дольше минуты.
                StartAnswerTimer();
                return;
            }

            await AcceptAsync();
        }

        /// <summary>`accept-call`: голос — да, камера — нет: видео пока не умеем.</summary>
        private async Task AcceptAsync()
        {
            var media = new JsonObject();
            media["isVideoEnabled"] = JsonValue.CreateBooleanValue(false);
            media["isAudioEnabled"] = JsonValue.CreateBooleanValue(true);
            media["isScreenSharingEnabled"] = JsonValue.CreateBooleanValue(false);
            media["isAnimojiEnabled"] = JsonValue.CreateBooleanValue(false);

            var extra = new JsonObject();
            extra["mediaSettings"] = media;

            JsonObject accepted = await _signaling.SendAsync("accept-call", extra);
            Log.Write("call", accepted != null ? "звонок принят" : "accept-call без ответа");
        }

        /// <summary>
        /// Свои адреса в сети — из системы: ядро само их не узнает. Только
        /// IPv4: остальное ядро не умеет.
        /// </summary>
        private static List<string> LocalAddresses()
        {
            var made = new List<string>();
            try
            {
                foreach (HostName name in NetworkInformation.GetHostNames())
                {
                    if (name.Type != HostNameType.Ipv4 || name.IPInformation == null) continue;
                    string ip = name.CanonicalName;
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    if (!made.Contains(ip)) made.Add(ip);
                }
            }
            catch (Exception error)
            {
                Log.Write("call", "свои адреса не узнались: " + error.Message);
            }
            return made;
        }

        #endregion

        #region Поток ядра

        private void Run(List<string> hosts)
        {
            CallCore core = CallCore.Create();
            if (core == null)
            {
                End("медиаядро не завелось");
                return;
            }

            _localUfrag = core.Ufrag;
            Log.Write("call", "ядро: свои адреса " + string.Join(", ", hosts));

            foreach (string ip in hosts) core.AddHost(ip);

            if (_params != null)
            {
                AddServers(core, _params.Stun, _params.Turn, _params.TurnUser, _params.TurnPassword);
            }

            var buffer = new byte[2000];
            uint timestamp = 0;
            int lastState = CallCore.Searching;
            int sent = 0, refused = 0, received = 0;
            DateTime lastLoop = DateTime.UtcNow;
            double longestLoop = 0;
            int collections = GC.CollectionCount(2);
            DateTime nextReport = DateTime.UtcNow.AddSeconds(5);

            try
            {
                while (!_stopping)
                {
                    while (true)
                    {
                        Action<CallCore> command;
                        lock (_lock)
                        {
                            if (_commands.Count == 0) break;
                            command = _commands.Dequeue();
                        }
                        command(core);
                    }

                    while (true)
                    {
                        byte[] frame;
                        lock (_lock)
                        {
                            if (_outgoing.Count == 0) break;
                            frame = _outgoing.Dequeue();
                        }
                        if (core.SendAudio(frame, frame.Length, timestamp)) sent++;
                        else refused++;
                        timestamp += CallMicrophone.FrameSamples;
                    }

                    DateTime pollAt = DateTime.UtcNow;
                    double loopGap = (pollAt - lastLoop).TotalMilliseconds;
                    if (loopGap > longestLoop) longestLoop = loopGap;
                    lastLoop = pollAt;

                    int state = core.Poll(10);

                    string text;
                    while ((text = core.NextEvent()) != null) OnCoreEvent(text);

                    uint packetTime, sequence;
                    int length;
                    while ((length = core.ReceiveAudio(buffer, out packetTime, out sequence)) > 0)
                    {
                        var payload = new byte[length];
                        Buffer.BlockCopy(buffer, 0, payload, 0, length);
                        _jitter.Add(sequence, payload);
                        received++;
                    }

                    if (DateTime.UtcNow >= nextReport)
                    {
                        nextReport = DateTime.UtcNow.AddSeconds(5);
                        // Без ожидания диска: этот поток отправляет звук, и
                        // каждая его остановка — пауза у собеседника.
                        CallMicrophone microphone = _microphone;
                        int gcNow = GC.CollectionCount(2);
                        Log.Write("call", "звук: отправлено " + sent + " (не ушло " + refused
                                          + "), принято " + received + ", достроено " + _jitter.Lost
                                          + ", состояние " + state
                                          + "; паузы: цикл " + (int)longestLoop + " мс, микрофон "
                                          + (microphone != null ? (int)microphone.TakeLongestGap() : 0) + " мс"
                                          + ", сборок мусора " + (gcNow - collections));
                        collections = gcNow;
                        longestLoop = 0;
                    }

                    if (state == CallCore.Broken)
                    {
                        End("соединение развалилось");
                        break;
                    }
                    lastState = state;
                }
            }
            catch (Exception error)
            {
                Log.Fail("call", "поток ядра", error);
                End("сбой в ядре");
            }
            finally
            {
                core.Dispose();
                Log.Write("call", "ядро закрыто (последнее состояние " + lastState
                                  + ", принято кадров " + (_jitter != null ? _jitter.Received : 0)
                                  + ", достроено " + (_jitter != null ? _jitter.Lost : 0) + ")");
            }
        }

        /// <summary>STUN и первый TURN по UDP — ядро ходит к TURN только так.</summary>
        private static void AddServers(CallCore core, string stun, IEnumerable<string> turns,
                                       string user, string password)
        {
            string host;
            int port;
            if (ServerOf(stun, 3478, out host, out port)) core.AddStun(host, port);

            foreach (string turn in turns)
            {
                if (turn.Contains("transport=tcp") || turn.StartsWith("turns:")) continue;
                if (!ServerOf(turn, 3478, out host, out port)) continue;
                core.AddTurn(host, port, user, password);
                Log.Write("call", "ядро: TURN " + host + ":" + port);
                break;
            }
        }

        private void Post(Action<CallCore> command)
        {
            lock (_lock) _commands.Enqueue(command);
        }

        /// <summary>Событие ядра — на его потоке.</summary>
        private void OnCoreEvent(string text)
        {
            if (text.StartsWith("log "))
            {
                Log.WriteNow("call", "ядро: " + text.Substring(4));
            }
            else if (text.StartsWith("cand "))
            {
                Log.Write("call", "свой кандидат: " + text.Substring(5));
                SendCandidate(CandidateLine(text));
            }
            else if (text.StartsWith("path "))
            {
                Log.Write("call", "путь: " + text.Substring(5));
            }
            else if (text == "ready")
            {
                Log.WriteNow("call", "ядро готово, запускаем звук");
                MaxApi.RunOnUi(StartMedia);
            }
            else if (text.StartsWith("failed "))
            {
                Log.WriteNow("call", "ядро: " + text);
                End(text.Substring(7));
            }
        }

        /// <summary>
        /// Событие ядра «cand host 1.2.3.4 5000 2130706431 [raddr rport]» —
        /// в строку кандидата, как её пишет libwebrtc.
        /// </summary>
        private string CandidateLine(string text)
        {
            string[] parts = text.Split(' ');
            string type = parts[1];
            int foundation = type == "host" ? 1 : type == "srflx" ? 2 : 3;

            string line = "candidate:" + foundation + " 1 udp " + parts[4] + " " + parts[2] + " "
                          + parts[3] + " typ " + type;
            if (parts.Length >= 7) line += " raddr " + parts[5] + " rport " + parts[6];
            return line + " generation 0 ufrag " + _localUfrag + " network-id 1";
        }

        #endregion

        #region Сигнализация

        private void OnNotification(JsonObject message)
        {
            string name = Text(message, "notification");

            switch (name)
            {
                case "connection":
                    OnConnection(message);
                    break;
                case "transmitted-data":
                    OnTransmittedData(message);
                    break;
                case "participant-joined":
                case "participant-added":
                    OnParticipantJoined(message);
                    break;
                case "accepted-call":
                    if (IsOutgoing && !_accepted)
                    {
                        _accepted = true;
                        Log.WriteNow("call", "собеседник ответил");
                        MaxApi.RunOnUi(() =>
                        {
                            if (State == CallState.Calling) State = CallState.Connecting;
                            Notify();
                        });
                    }
                    break;
                case "hungup":
                case "closed-conversation":
                    if (IsOutgoing && !_accepted && State != CallState.Active)
                    {
                        string reason = Text(message, "reason");
                        End(reason == "REJECTED" ? "собеседник отклонил звонок"
                            : reason == "BUSY" ? "собеседник занят"
                            : "собеседник не ответил");
                    }
                    else
                    {
                        End("собеседник завершил звонок");
                    }
                    break;
                case "topology-changed":
                    Log.WriteNow("call", "сигнализация: " + Shorten(message.Stringify()));

                    // Топология лежит то сверху (так читает Komet), то в
                    // conversation (так — kolibri).
                    string topology = Text(message, "topology");
                    IJsonValue conversation;
                    if (topology == null && message.TryGetValue("conversation", out conversation)
                        && conversation.ValueType == JsonValueType.Object)
                    {
                        topology = Text(conversation.GetObject(), "topology");
                    }

                    if (topology == "SERVER")
                    {
                        // Через медиасервер — другой протокол (allocate-consumer
                        // и прочее), его у нас пока нет. Молча висеть без звука
                        // хуже, чем честно закончить.
                        End("звонок перешёл на медиасервер MAX, это пока не поддерживается");
                    }
                    break;
                case "session-state":
                case "settings-update":
                case "feature-set-changed":
                    Log.Write("call", "сигнализация: " + Shorten(message.Stringify()));
                    break;
            }
        }

        private void OnConnection(JsonObject message)
        {
            IJsonValue value;
            if (!message.TryGetValue("conversation", out value) || value.ValueType != JsonValueType.Object) return;
            JsonObject conversation = value.GetObject();

            string topology = Text(conversation, "topology");
            long me = _myCallsId;

            IJsonValue list;
            if (conversation.TryGetValue("participants", out list) && list.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in list.GetArray())
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    JsonObject participant = item.GetObject();

                    long id = Number(participant, "id");
                    if (id == 0 || id == me) continue;

                    _peerId = id;

                    IJsonValue types;
                    if (participant.TryGetValue("responderTypes", out types) && types.ValueType == JsonValueType.Array
                        && types.GetArray().Count > 0 && types.GetArray()[0].ValueType == JsonValueType.String)
                    {
                        _peerType = types.GetArray()[0].GetString();
                    }

                    IJsonValue devices;
                    if (participant.TryGetValue("responderDeviceIdxs", out devices) && devices.ValueType == JsonValueType.Array
                        && devices.GetArray().Count > 0 && devices.GetArray()[0].ValueType == JsonValueType.Number)
                    {
                        _peerDeviceIdx = (int)devices.GetArray()[0].GetNumber();
                    }
                    break;
                }
            }

            Log.Write("call", "connection: собеседник " + _peerId + ", топология " + (topology ?? "?"));

            if (!IsOutgoing || _offerSent) return;

            // Звоним сами: STUN и TURN — из conversationParams (у входящего
            // они были в пуше).
            string stun = null;
            var turns = new List<string>();
            string user = null, password = null;

            IJsonValue paramsValue;
            if (message.TryGetValue("conversationParams", out paramsValue) && paramsValue.ValueType == JsonValueType.Object)
            {
                JsonObject cp = paramsValue.GetObject();
                List<string> stuns = Urls(cp, "stun");
                if (stuns.Count > 0) stun = stuns[0];

                IJsonValue turnValue;
                if (cp.TryGetValue("turn", out turnValue) && turnValue.ValueType == JsonValueType.Object)
                {
                    turns = Urls(cp, "turn");
                    user = Text(turnValue.GetObject(), "username");
                    password = Text(turnValue.GetObject(), "credential");
                }
            }
            Log.Write("call", "серверы: STUN " + (stun ?? "нет") + ", TURN " + turns.Count);
            Post(core => AddServers(core, stun, turns, user, password));

            if (_peerId == 0)
            {
                // Собеседника среди участников ещё нет — предложение уйдёт,
                // когда сервер скажет, что он появился.
                Log.Write("call", "собеседника в разговоре пока нет — ждём");
                return;
            }
            var ignored = OfferAsync();
        }

        /// <summary>Участник появился — если это собеседник, которого ждали, шлём предложение.</summary>
        private void OnParticipantJoined(JsonObject message)
        {
            if (!IsOutgoing || _offerSent || _peerId != 0) return;

            JsonObject source = message;
            IJsonValue nested;
            if (message.TryGetValue("participant", out nested) && nested.ValueType == JsonValueType.Object)
                source = nested.GetObject();

            long id = Number(source, "id");
            if (id == 0) id = Number(source, "participantId");
            if (id == 0) id = Number(message, "participantId");
            if (id == 0 || id == _myCallsId) return;

            _peerId = id;
            Log.Write("call", "собеседник появился: " + id);
            var ignored = OfferAsync();
        }

        /// <summary>`urls` бывают и строкой, и списком.</summary>
        private static List<string> Urls(JsonObject map, string key)
        {
            var made = new List<string>();
            IJsonValue value;
            if (!map.TryGetValue(key, out value) || value.ValueType != JsonValueType.Object) return made;

            IJsonValue urls;
            if (!value.GetObject().TryGetValue("urls", out urls)) return made;
            if (urls.ValueType == JsonValueType.String) made.Add(urls.GetString());
            else if (urls.ValueType == JsonValueType.Array)
            {
                foreach (IJsonValue item in urls.GetArray())
                    if (item.ValueType == JsonValueType.String) made.Add(item.GetString());
            }
            return made;
        }

        /// <summary>Наше предложение SDP — звоним сами.</summary>
        private async Task OfferAsync()
        {
            if (_offerSent) return;
            _offerSent = true;

            var ready = new TaskCompletionSource<string>();
            Post(core =>
            {
                // Ведём ICE; роль DTLS уточнит ответ собеседника.
                core.SetRoles(true, true);
                ready.TrySetResult(SdpOffer.BuildOffer(core));
            });
            string offer = await ready.Task;

            var sdp = new JsonObject();
            sdp["type"] = JsonValue.CreateStringValue("offer");
            sdp["sdp"] = JsonValue.CreateStringValue(offer);

            var data = new JsonObject();
            data["sdp"] = sdp;

            JsonObject extra = Addressed();
            extra["data"] = data;
            extra["capabilities"] = JsonValue.CreateStringValue("3c02f");

            bool sent = await _signaling.SendAsync("transmit-data", extra) != null;
            Log.WriteNow("call", sent ? "предложение SDP отправлено" : "предложение SDP не принято сервером");

            await FlushCandidatesAsync();
            await AcceptAsync();
        }

        /// <summary>Кандидаты, накопленные до отправки своего SDP.</summary>
        private async Task FlushCandidatesAsync()
        {
            List<string> waiting;
            lock (_lock)
            {
                _answered = true;
                waiting = new List<string>(_pendingCandidates);
                _pendingCandidates.Clear();
            }
            foreach (string line in waiting) await TransmitCandidateAsync(line);
        }

        /// <summary>Ответ собеседника на наше предложение.</summary>
        private void OnAnswer(string text)
        {
            SdpOffer answer = SdpOffer.Parse(text);
            if (answer == null)
            {
                Log.Write("call", "ответ SDP не разобрался");
                End("не договорились о звуке");
                return;
            }

            // a=setup:active у него — значит, сервер DTLS мы.
            bool weAreClient = answer.Setup == "passive";
            Log.WriteNow("call", "ответ собеседника: opus " + answer.OpusPt + ", setup " + (answer.Setup ?? "?")
                                 + ", кандидатов " + answer.Candidates.Count);

            Post(core =>
            {
                core.SetRoles(true, weAreClient);
                core.SetRemote(answer.Ufrag, answer.Password, answer.Fingerprint, answer.OpusPt);
                foreach (RemoteCandidate candidate in answer.Candidates)
                    core.AddRemote(candidate.Ip, candidate.Port, candidate.Priority);
            });
        }

        private DispatcherTimer _answerTimer;

        private void StartAnswerTimer()
        {
            MaxApi.RunOnUi(() =>
            {
                _answerTimer = new DispatcherTimer { Interval = AnswerLimit };
                _answerTimer.Tick += async (sender, argument) =>
                {
                    _answerTimer.Stop();
                    if (_accepted || State == CallState.Ended) return;
                    Log.Write("call", "собеседник не ответил за минуту");
                    await _signaling.HangupAsync("HUNGUP");
                    End("собеседник не ответил");
                };
                _answerTimer.Start();
            });
        }

        private async void OnTransmittedData(JsonObject message)
        {
            IJsonValue value;
            if (!message.TryGetValue("data", out value) || value.ValueType != JsonValueType.Object) return;
            JsonObject data = value.GetObject();

            // Пришло от кого-то — значит, это он и есть, даже если
            // `connection` его ещё не назвал.
            long from = Number(message, "participantId");
            if (_peerId == 0 && from != 0) _peerId = from;

            IJsonValue sdpValue;
            if (data.TryGetValue("sdp", out sdpValue) && sdpValue.ValueType == JsonValueType.Object)
            {
                JsonObject sdp = sdpValue.GetObject();
                string type = Text(sdp, "type");
                if (type == "answer" && IsOutgoing)
                {
                    OnAnswer(Text(sdp, "sdp"));
                    return;
                }
                if (type != "offer") return;
                await AnswerAsync(Text(sdp, "sdp"));
                return;
            }

            IJsonValue candidateValue;
            if (data.TryGetValue("candidate", out candidateValue) && candidateValue.ValueType == JsonValueType.Object)
            {
                RemoteCandidate candidate = RemoteCandidate.Parse(Text(candidateValue.GetObject(), "candidate"));
                if (candidate == null) return;

                Log.Write("call", "кандидат собеседника: " + candidate);
                Post(core => core.AddRemote(candidate.Ip, candidate.Port, candidate.Priority));
            }
        }

        private async Task AnswerAsync(string text)
        {
            if (_answered)
            {
                // Повторное предложение — пересогласование (например, звонящий
                // включил камеру). Звук у нас прежний; молча оставляем как есть.
                Log.Write("call", "повторное предложение SDP — пропускаем");
                return;
            }

            SdpOffer offer = SdpOffer.Parse(text);
            if (offer == null)
            {
                Log.Write("call", "предложение SDP не разобралось");
                End("не договорились о звуке");
                return;
            }

            Log.Write("call", "предложение: opus " + offer.OpusPt + ", секций " + offer.Sections.Count
                              + ", кандидатов " + offer.Candidates.Count);

            var ready = new TaskCompletionSource<string>();
            Post(core =>
            {
                core.SetRemote(offer.Ufrag, offer.Password, offer.Fingerprint, offer.OpusPt);
                foreach (RemoteCandidate candidate in offer.Candidates)
                    core.AddRemote(candidate.Ip, candidate.Port, candidate.Priority);
                ready.TrySetResult(offer.BuildAnswer(core));
            });

            string answer = await ready.Task;

            var sdp = new JsonObject();
            sdp["type"] = JsonValue.CreateStringValue("answer");
            sdp["sdp"] = JsonValue.CreateStringValue(answer);

            var data = new JsonObject();
            data["sdp"] = sdp;

            JsonObject extra = Addressed();
            extra["data"] = data;
            extra["capabilities"] = JsonValue.CreateStringValue("3c02f");

            bool sent = await _signaling.SendAsync("transmit-data", extra) != null;
            Log.Write("call", sent ? "ответ SDP отправлен" : "ответ SDP не принят сервером");

            // Кандидатов — только после ответа: до него звонящему не к чему их
            // приложить, и libwebrtc их выбрасывает.
            await FlushCandidatesAsync();
        }

        private void SendCandidate(string line)
        {
            lock (_lock)
            {
                if (!_answered)
                {
                    _pendingCandidates.Add(line);
                    return;
                }
            }
            var ignored = TransmitCandidateAsync(line);
        }

        private async Task TransmitCandidateAsync(string line)
        {
            var candidate = new JsonObject();
            candidate["candidate"] = JsonValue.CreateStringValue(line);
            candidate["sdpMid"] = JsonValue.CreateStringValue("0");
            candidate["sdpMLineIndex"] = JsonValue.CreateNumberValue(0);

            var data = new JsonObject();
            data["candidate"] = candidate;

            JsonObject extra = Addressed();
            extra["data"] = data;

            _candidateCount++;
            await _signaling.SendAsync("transmit-data", extra);
        }

        /// <summary>Кому: собеседник в системе звонков.</summary>
        private JsonObject Addressed()
        {
            var extra = new JsonObject();
            extra["participantId"] = JsonValue.CreateNumberValue(_peerId);
            extra["participantType"] = JsonValue.CreateStringValue(_peerType);
            extra["deviceIdx"] = JsonValue.CreateNumberValue(_peerDeviceIdx);
            return extra;
        }

        private void OnSignalingClosed()
        {
            if (State != CallState.Ended) End("сервер звонков закрыл соединение");
        }

        #endregion

        #region Звук

        /// <summary>
        /// Экран разговора отдаёт свой MediaElement: играть звук можно только
        /// элементом, который стоит на экране.
        /// </summary>
        public void AttachPlayer(MediaElement player)
        {
            _player = player;
            if (State == CallState.Active && !_mediaStarted) StartMedia();
        }

        private async void StartMedia()
        {
            try
            {
                await StartMediaAsync();
            }
            catch (Exception error)
            {
                Log.WriteNow("call", "звук не запустился: " + error.GetType().Name + ": " + error.Message
                                     + "\n" + error.StackTrace);
            }
        }

        private async Task StartMediaAsync()
        {
            if (State == CallState.Ended) return;

            if (State != CallState.Active)
            {
                State = CallState.Active;
                ActiveSince = DateTime.Now;
                Log.Write("call", "разговор начался");
                Notify();
            }

            if (_mediaStarted || _player == null) return;
            _mediaStarted = true;

            Log.WriteNow("call", "звук: динамик");
            _speaker = new CallSpeaker(_jitter) { Loud = Loud };
            try
            {
                _speaker.Start(_player);
            }
            catch (Exception error)
            {
                Log.WriteNow("call", "динамик не завёлся: " + error.GetType().Name + ": " + error.Message);
            }

            Log.WriteNow("call", "звук: микрофон");

            _microphone = new CallMicrophone { Muted = Muted };
            _microphone.Frame += frame =>
            {
                lock (_lock)
                {
                    // Сеть встала — не копим: старый звук никому не нужен.
                    if (_outgoing.Count > 10) _outgoing.Dequeue();
                    _outgoing.Enqueue(frame);
                }
            };

            if (!await _microphone.StartAsync())
            {
                Log.WriteNow("call", "микрофон: " + _microphone.Error);
            }
            else
            {
                Log.WriteNow("call", "звук: микрофон пишет");
            }
        }

        /// <summary>
        /// Громкая связь. Системный переключатель динамиков
        /// (AudioRoutingManager) обычному приложению закрыт — «доступ
        /// запрещён», он только для VoIP-приложений. Поэтому громкость —
        /// сменой категории звука: «связь» играет в разговорный динамик,
        /// обычный звук — в громкий.
        /// </summary>
        public bool Loud { get; private set; }

        public void SetLoud(bool loud)
        {
            Loud = loud;
            if (_speaker != null)
            {
                try { _speaker.SetLoud(loud); }
                catch (Exception error) { Log.WriteNow("call", "громкая связь: " + error.Message); }
            }
            Notify();
        }

        public void SetMuted(bool muted)
        {
            Muted = muted;
            if (_microphone != null) _microphone.Muted = muted;

            var media = new JsonObject();
            media["isVideoEnabled"] = JsonValue.CreateBooleanValue(false);
            media["isAudioEnabled"] = JsonValue.CreateBooleanValue(!muted);
            media["isScreenSharingEnabled"] = JsonValue.CreateBooleanValue(false);
            media["isAnimojiEnabled"] = JsonValue.CreateBooleanValue(false);
            var extra = new JsonObject();
            extra["mediaSettings"] = media;
            var ignored = _signaling.SendAsync("change-media-settings", extra);

            Notify();
        }

        #endregion

        #region Конец

        public async Task HangupAsync()
        {
            if (State == CallState.Ended) return;
            await _signaling.HangupAsync("HUNGUP");
            End("вы завершили звонок");
        }

        private void End(string reason)
        {
            lock (_lock)
            {
                if (_stopping) return;
                _stopping = true;
            }

            Log.Write("call", "звонок окончен: " + reason + ", своих кандидатов отправлено " + _candidateCount);

            MaxApi.RunOnUi(() =>
            {
                EndReason = reason;
                State = CallState.Ended;

                if (_answerTimer != null) _answerTimer.Stop();
                if (_microphone != null) _microphone.Dispose();
                if (_speaker != null) _speaker.Stop();
                _microphone = null;
                _speaker = null;

                Notify();
            });

            var ignored = CloseLaterAsync();
        }

        private async Task CloseLaterAsync()
        {
            await _signaling.CloseAsync();
            // Ядро уже вышло из цикла — буфер можно отдать.
            await Task.Delay(500);
            if (_jitter != null) _jitter.Dispose();
        }

        private void Notify()
        {
            Action<CallSession> handler = Changed;
            if (handler != null) MaxApi.RunOnUi(() => handler(this));
        }

        #endregion

        #region Мелочи

        /// <summary>«stun:host:port», «turn:host:port?transport=udp» — в имя и порт.</summary>
        private static bool ServerOf(string url, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (string.IsNullOrEmpty(url)) return false;

            string rest = url;
            int scheme = rest.IndexOf(':');
            if (rest.StartsWith("stun:") || rest.StartsWith("turn:") || rest.StartsWith("turns:"))
                rest = rest.Substring(scheme + 1);
            if (rest.StartsWith("//")) rest = rest.Substring(2);

            int query = rest.IndexOf('?');
            if (query >= 0) rest = rest.Substring(0, query);

            int colon = rest.LastIndexOf(':');
            if (colon > 0)
            {
                int parsed;
                if (int.TryParse(rest.Substring(colon + 1), out parsed)) port = parsed;
                rest = rest.Substring(0, colon);
            }

            host = rest;
            return host.Length > 0;
        }

        private static string Shorten(string text)
        {
            if (text == null) return "";
            return text.Length <= 700 ? text : text.Substring(0, 700) + "…";
        }

        private static string Text(JsonObject map, string key)
        {
            IJsonValue value;
            if (map == null || !map.TryGetValue(key, out value) || value.ValueType != JsonValueType.String) return null;
            return value.GetString();
        }

        private static long Number(JsonObject map, string key)
        {
            IJsonValue value;
            if (map == null || !map.TryGetValue(key, out value)) return 0;
            if (value.ValueType == JsonValueType.Number) return (long)value.GetNumber();
            long parsed;
            if (value.ValueType == JsonValueType.String && long.TryParse(value.GetString(), out parsed)) return parsed;
            return 0;
        }

        #endregion
    }
}
