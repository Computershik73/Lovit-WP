using System;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
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
    /// Звонки. Пока — только входящие: видно, кто звонит, и звонок можно
    /// отклонить. Ответить Ловит ещё не умеет: для этого нужен свой медиастек
    /// (ICE, DTLS-SRTP, RTP с Opus), и он — следующая ступень.
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

            // Звонят, пока звонит другой, — второго не показываем: занято.
            if (Ringing != null) return;

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
