using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Windows.Data.Json;
using Lovit.Models;
using Windows.UI.Xaml;

namespace Lovit.Backend.Calls
{
    /// <summary>Входящий звонок: кто, какой и с чем к нему подключаться.</summary>
    public sealed class IncomingCall
    {
        public string ConversationId { get; set; }
        public long CallerId { get; set; }
        public bool IsVideo { get; set; }
        public CallParams Params { get; set; }
    }

    /// <summary>
    /// Звонки. Пока — только входящие: видно, кто звонит; звонок можно
    /// принять (голосом — видео нет), отклонить или скрыть. Сам разговор —
    /// CallSession: своё медиаядро вместо libwebrtc.
    ///
    /// Устроено как в Komet (lib/core/calls/call_controller.dart): пуш
    /// NOTIF_CALL_START по основному сокету приносит кто звонит и `vcp`;
    /// отклонить — значит открыть сигнализацию звонка и сказать там
    /// `hangup` с причиной REJECTED. По основному сокету отказа не передать.
    /// </summary>
    public sealed class CallsModule
    {
        private static readonly CallsModule _instance = new CallsModule();
        public static CallsModule Instance { get { return _instance; } }

        /// <summary>
        /// Сколько звонить, если никто ничего не решил. Сервер сам бросает
        /// неотвеченный звонок примерно через минуту; ждать дольше незачем.
        /// </summary>
        private static readonly TimeSpan RingLimit = TimeSpan.FromSeconds(60);

        private DispatcherTimer _ringTimer;
        private bool _started;

        /// <summary>Звонок, который сейчас звонит; null — никто не звонит.</summary>
        public IncomingCall Ringing { get; private set; }

        /// <summary>Идущий разговор; null — его нет.</summary>
        public CallSession Current { get; private set; }

        /// <summary>Позвонили. Приходит на потоке разметки.</summary>
        public event Action<IncomingCall> IncomingStarted;

        /// <summary>Звонить перестали — отклонили, скрыли, сбросил звонящий или вышло время.</summary>
        public event Action<IncomingCall> IncomingEnded;

        private CallsModule() { }

        /// <summary>Подписаться на пуши. Зовётся один раз при запуске.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;

            MaxApi.Instance.PushReceived += OnPush;
            ChatsModule.Instance.MessageReceived += OnMessage;
        }

        #region Пуши

        private void OnPush(Packet packet)
        {
            if (packet.Opcode != Opcodes.NotifCallStart) return;

            string vcp = MessagePack.String(packet.Map, "vcp");
            string conversationId = MessagePack.String(packet.Map, "conversationId");
            long callerId = MessagePack.Long(packet.Map, "callerId");

            if (string.IsNullOrEmpty(vcp) || string.IsNullOrEmpty(conversationId) || callerId == 0)
            {
                Log.Write("call", "входящий без нужного: {" + Log.Keys(packet.Map) + "}");
                return;
            }

            // Тот же звонок сервер вправе прислать повторно — второй раз не
            // звоним.
            if (Ringing != null && Ringing.ConversationId == conversationId) return;

            CallParams parameters = CallParams.Decode(vcp);
            if (parameters == null)
            {
                Log.Write("call", "входящий: vcp не разобрался");
                return;
            }

            var call = new IncomingCall
            {
                ConversationId = conversationId,
                CallerId = callerId,
                IsVideo = MessagePack.String(packet.Map, "type") == "VIDEO" || parameters.IsVideo,
                Params = parameters
            };

            Log.Write("call", "входящий " + (call.IsVideo ? "видеозвонок" : "звонок")
                              + " от " + callerId + ": " + parameters.Describe());

            // Звонят, пока звонит другой или идёт разговор, — второго не
            // показываем: занято.
            if (Ringing != null || Current != null) return;

            Ringing = call;
            ChatsModule.Instance.EnsureContact(callerId);
            StartRingTimer();

            Action<IncomingCall> handler = IncomingStarted;
            if (handler != null) handler(call);
        }

        /// <summary>
        /// Звонящий передумал. Отдельного пуша об этом нет: сервер просто
        /// кладёт в переписку сообщение о звонке («пропущенный»). По нему и
        /// понимаем, что звонить больше некому.
        /// </summary>
        private void OnMessage(MaxMessage message)
        {
            IncomingCall call = Ringing;
            if (call == null || message == null || message.SenderId != call.CallerId) return;

            foreach (MaxAttachment attachment in message.Attachments)
            {
                if (attachment.Type != "CALL") continue;

                Log.Write("call", "звонящий сбросил: " + (attachment.HangupType ?? "?"));
                Finish(call);
                return;
            }
        }

        #endregion

        #region Решения

        /// <summary>
        /// Ответить. Звонок перестаёт звонить и становится разговором;
        /// дальше им занимается CallSession, а экран разговора его показывает.
        /// </summary>
        public CallSession Answer()
        {
            IncomingCall call = Ringing;
            if (call == null) return null;

            Finish(call);

            var session = new CallSession(call);
            Current = session;
            session.Changed += OnSessionChanged;

            Log.Write("call", "отвечаем");
            var ignored = session.StartAsync();
            return session;
        }

        /// <summary>Почему последний звонок не начался — для сообщения на экране.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Позвонить. Порт `initiateCall` из Komet (lib/backend/modules/calls.dart):
        /// VIDEO_CHAT_START_ACTIVE с собеседником в `calleeIds`; в ответе, в
        /// строке `internalCallerParams`, — адрес сигнализации и наш номер в
        /// системе звонков. Дальше — CallSession.
        ///
        /// null — не вышло: идёт другой разговор или сервер отказал (причина
        /// в LastError).
        /// </summary>
        public async Task<CallSession> CallAsync(long userId)
        {
            LastError = null;

            if (Current != null || Ringing != null)
            {
                LastError = "Уже идёт другой звонок.";
                return null;
            }

            string conversationId = Guid.NewGuid().ToString();

            var payload = new Dictionary<string, object>
            {
                { "conversationId", conversationId },
                { "calleeIds", new List<object> { userId } },
                { "internalParams", InternalParams() },
                { "isVideo", false }
            };

            Log.Write("call", "звоним " + userId);
            Packet answer = await MaxApi.Instance.RequestAsync(Opcodes.VideoChatStartActive, payload);

            if (answer == null || answer.IsError)
            {
                string reason = answer != null ? MessagePack.String(answer.Map, "localizedMessage")
                                                 ?? MessagePack.String(answer.Map, "error") : null;
                Log.Write("call", "звонок не создался: " + (reason ?? "нет ответа"));
                LastError = string.IsNullOrEmpty(reason) ? "Сервер не ответил." : reason;
                return null;
            }

            string caller = MessagePack.String(answer.Map, "internalCallerParams");
            JsonObject parsed;
            if (string.IsNullOrEmpty(caller) || !JsonObject.TryParse(caller, out parsed))
            {
                Log.Write("call", "в ответе нет internalCallerParams: {" + Log.Keys(answer.Map) + "}");
                LastError = "Сервер не дал куда подключаться.";
                return null;
            }

            string endpoint = parsed.ContainsKey("endpoint") && parsed["endpoint"].ValueType == JsonValueType.String
                ? parsed["endpoint"].GetString() : null;

            long myCallsId = 0;
            IJsonValue id;
            if (parsed.TryGetValue("id", out id) && id.ValueType == JsonValueType.Object)
            {
                IJsonValue inner;
                if (id.GetObject().TryGetValue("internal", out inner) && inner.ValueType == JsonValueType.Number)
                    myCallsId = (long)inner.GetNumber();
            }

            if (string.IsNullOrEmpty(endpoint))
            {
                LastError = "Сервер не дал куда подключаться.";
                return null;
            }

            string served = MessagePack.String(answer.Map, "conversationId");
            if (!string.IsNullOrEmpty(served)) conversationId = served;

            var session = new CallSession(conversationId, userId, Ws2Url(endpoint), myCallsId);
            Current = session;
            session.Changed += OnSessionChanged;
            ChatsModule.Instance.EnsureContact(userId);

            var ignored = session.StartAsync();
            return session;
        }

        /// <summary>То, чем Komet представляется системе звонков.</summary>
        private static string InternalParams()
        {
            var map = new JsonObject();
            map["platform"] = JsonValue.CreateStringValue("ANDROID");
            map["sdkVersion"] = JsonValue.CreateStringValue("0.2.1.3");
            map["clientAppKey"] = JsonValue.CreateStringValue("CGPGAGLGDIHBABABA");
            map["deviceId"] = JsonValue.CreateStringValue(DeviceIdentity.DeviceId ?? "");
            map["protocolVersion"] = JsonValue.CreateNumberValue(5);
            map["onlyAdminCanRecord"] = JsonValue.CreateBooleanValue(false);
            map["isWaitForAdminEnabled"] = JsonValue.CreateBooleanValue(false);
            map["hexCapability"] = JsonValue.CreateStringValue("3c02f");
            return map.Stringify();
        }

        /// <summary>
        /// Адрес сигнализации исходящего: свои параметры адреса сохраняем,
        /// остальное — как у Komet (Ws2Config.fromEndpoint).
        /// </summary>
        private static string Ws2Url(string endpoint)
        {
            int question = endpoint.IndexOf('?');
            string path = question < 0 ? endpoint : endpoint.Substring(0, question);

            var query = new List<KeyValuePair<string, string>>();
            if (question >= 0)
            {
                foreach (string part in endpoint.Substring(question + 1).Split('&'))
                {
                    if (part.Length == 0) continue;
                    int equals = part.IndexOf('=');
                    query.Add(equals < 0
                        ? new KeyValuePair<string, string>(part, "")
                        : new KeyValuePair<string, string>(part.Substring(0, equals), part.Substring(equals + 1)));
                }
            }

            var ours = new[]
            {
                new KeyValuePair<string, string>("version", "5"),
                new KeyValuePair<string, string>("capabilities", "3c02f"),
                new KeyValuePair<string, string>("device", "Android%2FUnknown"),
                new KeyValuePair<string, string>("platform", "ANDROID"),
                new KeyValuePair<string, string>("clientType", "ONE_ME"),
                new KeyValuePair<string, string>("appVersion", "sdk-0.2.1.3"),
                new KeyValuePair<string, string>("osVersion", "34"),
                new KeyValuePair<string, string>("tgt", "start")
            };
            foreach (KeyValuePair<string, string> pair in ours)
            {
                query.RemoveAll(existing => existing.Key == pair.Key);
                query.Add(pair);
            }

            var built = new StringBuilder(path).Append('?');
            for (int i = 0; i < query.Count; i++)
            {
                if (i > 0) built.Append('&');
                built.Append(query[i].Key).Append('=').Append(query[i].Value);
            }
            return built.ToString();
        }

        private void OnSessionChanged(CallSession session)
        {
            if (session.State != CallState.Ended || Current != session) return;
            session.Changed -= OnSessionChanged;
            Current = null;
        }

        /// <summary>
        /// Отклонить: звонящий сразу услышит отбой, а не будет ждать, пока
        /// звонок кончится сам.
        /// </summary>
        public async Task DeclineAsync()
        {
            IncomingCall call = Ringing;
            if (call == null) return;

            Finish(call);

            var signaling = new CallSignaling();
            try
            {
                if (await signaling.ConnectAsync(call.Params.Ws2Url(call.ConversationId)))
                {
                    bool done = await signaling.HangupAsync("REJECTED") != null;
                    Log.Write("call", done ? "отклонён" : "отказ не подтвердился");
                }
            }
            catch (Exception error)
            {
                // Не вышло сказать «нет» — звонок просто дозвонится до конца.
                // Падать из-за этого незачем.
                Log.Write("call", "отклонить не вышло: " + error.Message);
            }
            finally
            {
                await signaling.CloseAsync();
            }
        }

        /// <summary>
        /// Скрыть, ничего не отвечая: звонок продолжает звонить на других
        /// устройствах, и там на него можно ответить.
        /// </summary>
        public void Hide()
        {
            IncomingCall call = Ringing;
            if (call == null) return;

            Log.Write("call", "входящий скрыт");
            Finish(call);
        }

        #endregion

        private void StartRingTimer()
        {
            if (_ringTimer == null)
            {
                _ringTimer = new DispatcherTimer { Interval = RingLimit };
                _ringTimer.Tick += (sender, argument) =>
                {
                    IncomingCall call = Ringing;
                    if (call == null) return;

                    Log.Write("call", "входящий: время вышло");
                    Finish(call);
                };
            }

            _ringTimer.Stop();
            _ringTimer.Start();
        }

        private void Finish(IncomingCall call)
        {
            if (Ringing != call) return;

            Ringing = null;
            if (_ringTimer != null) _ringTimer.Stop();

            Action<IncomingCall> handler = IncomingEnded;
            if (handler != null) handler(call);
        }
    }
}
