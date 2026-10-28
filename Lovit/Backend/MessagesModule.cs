using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Core.Protocol;
using Lovit.Core.Storage;
using Lovit.Models;
using Windows.Storage;

namespace Lovit.Backend
{
    /// <summary>История и отправка. Порт MAXMessagesModule.</summary>
    public class MessagesModule
    {
        private static readonly MessagesModule _instance = new MessagesModule();
        public static MessagesModule Instance { get { return _instance; } }

        private long _lastTypingSent;

        private MessagesModule() { }

        #region История

        /// <summary>
        /// CHAT_HISTORY. `fromTime` = 0 означает «с этого мгновения»: сервер
        /// отдаёт то, что строго раньше указанного времени, поэтому за хвостом
        /// истории надо проситься из будущего — отсюда сутки вперёд.
        /// </summary>
        public async Task<List<MaxMessage>> HistoryAsync(long chatId, long fromTime, int count)
        {
            return await HistoryAsync(chatId, fromTime, 0, count);
        }

        /// <summary>
        /// То же, но с обеих сторон от точки отсчёта.
        ///
        /// Нужно при переходе к найденному сообщению или к источнику
        /// пересылки: с одним лишь `backward` открывается хвост,
        /// обрывающийся на этом сообщении, и вниз прокрутить нечего.
        /// </summary>
        public async Task<List<MaxMessage>> HistoryAsync(long chatId, long fromTime,
                                                         int forward, int backward)
        {
            long from = fromTime > 0 ? fromTime : UnixTime.Now + 86400000L;

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "from", from },
                { "forward", (long)forward },
                { "backward", (long)backward },
                { "getMessages", true }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatHistory, payload);

            var messages = new List<MaxMessage>();

            if (packet == null || packet.IsError)
            {
                Log.Write("chat", "история чата " + chatId + " не пришла: "
                                  + (packet == null ? "нет ответа" : "отказ сервера"));
                return messages;
            }

            IList<object> list = MessagePack.List(packet.Map, "messages");
            if (list == null)
            {
                // Ответ есть, а сообщений в нём нет вовсе — это другое, чем
                // молчание, и путать их нельзя.
                Log.Write("chat", "история чата " + chatId + ": в ответе нет `messages`, есть {"
                                  + Log.Keys(packet.Map) + "}");
                return messages;
            }

            var senders = new List<long>();

            foreach (object item in list)
            {
                MaxMessage message = MaxMessage.FromMap(item as IDictionary<string, object>, chatId);
                if (message == null) continue;

                messages.Add(message);
                senders.Add(message.SenderId);
            }

            Log.Write("chat", "история чата " + chatId + ": " + messages.Count
                              + " из " + list.Count + " записей, from=" + from
                              + ", вперёд " + forward + ", назад " + backward);

            // Имена отправителей запрашиваются пачкой: по одному на каждое
            // сообщение — это полсотни запросов на открытие чата.
            ChatsModule.Instance.EnsureContacts(senders);

            return messages;
        }

        /// <summary>
        /// CHAT_MEDIA — вложения чата одним списком. `attachTypes` это
        /// `PHOTO`, `VIDEO`, `FILE`, `SHARE`.
        /// </summary>
        public async Task<List<MaxMessage>> MediaAsync(long chatId, string anchorMessageId,
                                                       IList<object> attachTypes)
        {
            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "count", 50L },
                { "attachTypes", attachTypes ?? new List<object> { "PHOTO", "VIDEO" } },
                { "forward", 0L },
                { "backward", 50L }
            };

            if (!string.IsNullOrEmpty(anchorMessageId)) payload["messageId"] = anchorMessageId;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.ChatMedia, payload);

            var messages = new List<MaxMessage>();
            if (packet == null || packet.IsError) return messages;

            IList<object> list = MessagePack.List(packet.Map, "messages");
            if (list == null) return messages;

            foreach (object item in list)
            {
                MaxMessage message = MaxMessage.FromMap(item as IDictionary<string, object>, chatId);
                if (message != null) messages.Add(message);
            }

            return messages;
        }

        #endregion

        #region Отправка

        /// <summary>
        /// MSG_SEND. `cid` — отрицательная метка времени: так делает
        /// официальный клиент, и по ней он же узнаёт своё сообщение,
        /// вернувшееся пушем.
        /// </summary>
        public async Task<MaxMessage> SendAsync(long chatId, string text, string replyToMessageId)
        {
            return await SendAsync(chatId, text, replyToMessageId, null);
        }

        /// <summary>
        /// То же с разметкой: `elements` — отрезки жирного, наклонного и
        /// ссылок. Пустой список означает «просто текст».
        /// </summary>
        public async Task<MaxMessage> SendAsync(long chatId, string text, string replyToMessageId,
                                                IList<object> elements)
        {
            var message = new Dictionary<string, object>
            {
                { "cid", -UnixTime.Now },
                { "text", text ?? "" },
                { "elements", elements ?? new List<object>() },
                { "attaches", new List<object>() }
            };

            // Ответ — это ссылка на другое сообщение того же вида, каким
            // приходит разбор входящих ответов.
            if (!string.IsNullOrEmpty(replyToMessageId))
            {
                // Номер числом, а не строкой: со строкой сервер ссылки на
                // сообщение не видит, и ответ уходит обычной репликой.
                long replyTo;
                if (long.TryParse(replyToMessageId, out replyTo) && replyTo != 0)
                {
                    message["link"] = new Dictionary<string, object>
                    {
                        { "type", "REPLY" },
                        { "chatId", chatId },
                        { "messageId", replyTo }
                    };
                }
            }

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "message", message },
                { "notify", true }
            };

            LastError = null;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgSend, payload);
            if (packet == null) { LastError = "Нет связи с сервером."; return null; }
            if (packet.IsError) { LastError = Refusal(packet); return null; }

            IDictionary<string, object> sent = MessagePack.Map(packet.Map, "message");
            return MaxMessage.FromMap(sent, chatId);
        }

        /// <summary>
        /// Чем сервер объяснил последний отказ. Отказы бывают осмысленные —
        /// бот, закрытый для писем, или чат, где писать запрещено, — и
        /// человеку их надо показывать, а не проглатывать.
        /// </summary>
        public string LastError { get; private set; }

        private static string Refusal(Packet packet)
        {
            string explained = MessagePack.String(packet.Map, "message");
            if (!string.IsNullOrEmpty(explained)) return explained;

            string code = MessagePack.String(packet.Map, "error");
            return string.IsNullOrEmpty(code) ? "Сообщение не отправлено." : code;
        }

        /// <summary>
        /// Фотография: сперва заливка на CDN, потом обычное MSG_SEND с
        /// вложением. Подпись уезжает текстом того же сообщения — отдельным
        /// сообщением её слать не надо.
        /// </summary>
        public async Task<MaxMessage> SendPhotoAsync(long chatId, byte[] data, string fileName,
                                                     string caption, string replyToMessageId,
                                                     IList<object> elements,
                                                     MediaHttp.Progress progress)
        {
            LastError = null;

            UploadResult upload = await Uploader.UploadPhotoAsync(data, fileName, progress);
            if (!upload.Ok) return null;

            var attach = new Dictionary<string, object>
            {
                { "_type", "PHOTO" },
                { "photoToken", upload.Token }
            };

            return await SendWithAttachAsync(chatId, caption, attach, replyToMessageId, elements);
        }

        /// <summary>
        /// Файл. Сервер принимает либо токен, либо номер файла — шлём то,
        /// что дал FILE_UPLOAD.
        /// </summary>
        public async Task<MaxMessage> SendFileAsync(long chatId, StorageFile file, long size,
                                                    string fileName,
                                                    string caption, string replyToMessageId,
                                                    IList<object> elements,
                                                    MediaHttp.Progress progress)
        {
            LastError = null;

            UploadResult upload = await Uploader.UploadFileAsync(file, size, fileName, progress);
            if (!upload.Ok) return null;

            var attach = new Dictionary<string, object>
            {
                { "_type", "FILE" },
                { "name", fileName ?? "" }
            };

            if (!string.IsNullOrEmpty(upload.Token)) attach["token"] = upload.Token;
            else attach["fileId"] = upload.FileId;

            return await SendWithAttachAsync(chatId, caption, attach, replyToMessageId, elements);
        }

        /// <summary>Что грузится: голосовое или кружок. Числа — серверные.</summary>
        private const int VoiceKind = 2;
        private const int NoteKind = 1;

        /// <summary>
        /// Голосовое. Внутри — Opus в контейнере Ogg; ничего другого сервер
        /// голосовым не считает.
        ///
        /// График громкости уезжает вместе с записью: посчитать его по звуку
        /// собеседник не сможет — он получит не сам звук, а ссылку на него.
        /// </summary>
        public async Task<MaxMessage> SendVoiceAsync(long chatId, byte[] data, int durationMs,
                                                     byte[] wave, string replyToMessageId,
                                                     MediaHttp.Progress progress)
        {
            LastError = null;

            UploadResult upload = await Uploader.UploadMediaAsync(data, VoiceKind, progress);
            if (!upload.Ok) return null;

            var attach = new Dictionary<string, object>
            {
                { "_type", "AUDIO" },
                { "token", upload.Token },
                { "duration", (long)durationMs },
                { "wave", wave ?? new byte[80] }
            };

            return await SendWithAttachAsync(chatId, "", attach, replyToMessageId, null);
        }

        /// <summary>
        /// Видео-кружок. От обычного ролика отличается одним полем: `videoType`
        /// со значением единица. По нему собеседник и показывает его в круге.
        /// </summary>
        public async Task<MaxMessage> SendVideoNoteAsync(long chatId, byte[] data, int durationMs,
                                                         string replyToMessageId,
                                                         MediaHttp.Progress progress)
        {
            LastError = null;

            UploadResult upload = await Uploader.UploadMediaAsync(data, NoteKind, progress);
            if (!upload.Ok) return null;

            var attach = new Dictionary<string, object>
            {
                { "_type", "VIDEO" },
                { "token", upload.Token },
                { "duration", (long)durationMs },
                { "videoType", 1L },
                { "wave", new byte[80] }
            };

            return await SendWithAttachAsync(chatId, "", attach, replyToMessageId, null);
        }

        /// <summary>
        /// Стикер. Заливать нечего — он уже на сервере, посылается один его
        /// номер. Порт sendStickerMessage из Komet.
        /// </summary>
        public async Task<MaxMessage> SendStickerAsync(long chatId, long stickerId)
        {
            LastError = null;

            var attach = new Dictionary<string, object>
            {
                { "_type", "STICKER" },
                { "stickerId", stickerId }
            };

            return await SendWithAttachAsync(chatId, "", attach, null, null);
        }

        private async Task<MaxMessage> SendWithAttachAsync(long chatId, string text,
                                                           IDictionary<string, object> attach,
                                                           string replyToMessageId,
                                                           IList<object> elements)
        {
            var message = new Dictionary<string, object>
            {
                { "cid", -UnixTime.Now },
                { "text", text ?? "" },
                { "elements", elements ?? new List<object>() },
                { "attaches", new List<object> { attach } }
            };

            if (!string.IsNullOrEmpty(replyToMessageId))
            {
                // Номер числом, а не строкой: со строкой сервер ссылки на
                // сообщение не видит, и ответ уходит обычной репликой.
                long replyTo;
                if (long.TryParse(replyToMessageId, out replyTo) && replyTo != 0)
                {
                    message["link"] = new Dictionary<string, object>
                    {
                        { "type", "REPLY" },
                        { "chatId", chatId },
                        { "messageId", replyTo }
                    };
                }
            }

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "message", message },
                { "notify", true }
            };

            // Залитое становится пригодным не сразу: CDN его ещё обрабатывает,
            // и сервер до этого отвечает отказом с `not.ready`. Komet на такой
            // отказ ждёт секунду и шлёт заново, до тридцати раз
            // (_sendWithNotReadyRetry) — здесь то же самое. Остальные отказы
            // повторять незачем: они не про время.
            LastError = null;

            for (int attempt = 0; attempt < 30; attempt++)
            {
                Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgSend, payload);
                if (packet == null) { LastError = "Нет связи с сервером."; return null; }

                if (!packet.IsError)
                {
                    IDictionary<string, object> sent = MessagePack.Map(packet.Map, "message");
                    return MaxMessage.FromMap(sent, chatId);
                }

                string reason = MessagePack.String(packet.Map, "error") ?? "";
                if (reason.IndexOf("not.ready", StringComparison.Ordinal) < 0)
                {
                    LastError = Refusal(packet);
                    Log.Write("chat", "вложение отвергнуто: " + reason
                                      + " — " + MessagePack.String(packet.Map, "message"));
                    return null;
                }

                Log.Write("chat", "вложение ещё не готово, попытка " + (attempt + 1));
                await Task.Delay(1000);
            }

            LastError = "Вложение так и не подготовилось на сервере.";
            return null;
        }

        /// <summary>
        /// Нажатие кнопки под сообщением бота. Порт sendButtonCallback из
        /// Komet: бот отвечает либо новым сообщением, либо всплывающим
        /// текстом в поле `alert`.
        /// </summary>
        public async Task<string> SendCallbackAsync(long chatId, string messageId,
                                                    string callbackId, string payload)
        {
            LastError = null;

            long id;
            if (!long.TryParse(messageId, out id) || id == 0) return null;

            var request = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "messageId", id },
                { "callbackId", callbackId ?? "" }
            };

            if (!string.IsNullOrEmpty(payload)) request["payload"] = payload;

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgSendCallback, request);
            if (packet == null) { LastError = "Нет связи с сервером."; return null; }
            if (packet.IsError) { LastError = Refusal(packet); return null; }

            // Бот вправе ничего не отвечать: тогда показывать нечего, и это
            // не ошибка.
            return MessagePack.String(packet.Map, "alert")
                   ?? MessagePack.String(packet.Map, "message");
        }

        /// <summary>
        /// Адрес видео. Скачиванием файла его не взять: номер видео живёт в
        /// своём счёте, и FILE_DOWNLOAD на него отвечает «файл не найден».
        ///
        /// VIDEO_PLAY отдаёт не одну ссылку, а набор качеств — от 144p до
        /// 1080p. Порт requestVideoPlay из Komet.
        /// </summary>
        public async Task<string> ResolveVideoUrlAsync(long videoId, string token,
                                                       long chatId, string messageId)
        {
            LastError = null;

            long id;
            long.TryParse(messageId, out id);

            var payload = new Dictionary<string, object>
            {
                { "messageId", id },
                { "chatId", chatId },
                { "token", token ?? "" },
                { "videoId", videoId }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.VideoPlay, payload);
            if (packet == null) { LastError = "Нет связи с сервером."; return null; }
            if (packet.IsError) { LastError = Refusal(packet); return null; }

            // Берём среднее качество, а не лучшее: у телефона и экран мельче,
            // и связь медленнее, а 1080p он к тому же может не потянуть.
            string[] order = { "MP4_480", "MP4_360", "MP4_240", "MP4_144", "MP4_720", "MP4_1080" };

            foreach (string key in order)
            {
                string url = MessagePack.String(packet.Map, key);
                if (!string.IsNullOrEmpty(url))
                {
                    Log.Write("chat", "видео: берём " + key);
                    return url;
                }
            }

            LastError = "Сервер не дал ни одного качества видео.";
            return null;
        }

        /// <summary>
        /// MSG_EDIT — правка своего сообщения.
        ///
        /// Номер числом, как при удалении и в ответах: строку сервер за
        /// ссылку на сообщение не принимает. Разметку приходится посылать
        /// заново — сервер заменяет её целиком, и без неё правленое
        /// сообщение осталось бы без жирного и ссылок.
        ///
        /// Вложения не упоминаем вовсе: перечисли мы их пустым списком, и
        /// правка подписи снесла бы саму фотографию.
        /// </summary>
        public async Task<bool> EditAsync(long chatId, string messageId, string text,
                                          IList<object> elements)
        {
            LastError = null;

            long edited;
            if (!long.TryParse(messageId, out edited) || edited == 0)
            {
                Log.Write("chat", "правка отменена: номер «" + messageId + "» не число");
                LastError = "Не разобрать номер сообщения.";
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "messageId", edited },
                { "text", text ?? "" },
                { "elements", elements ?? new List<object>() }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgEdit, payload);

            if (packet == null) { LastError = "Нет связи с сервером."; return false; }
            if (packet.IsError) { LastError = Refusal(packet); return false; }

            return true;
        }

        /// <summary>Чем кончился запрос о реакции и что стало с реакциями.</summary>
        public class ReactionResult
        {
            public bool Ok { get; set; }

            /// <summary>
            /// Ответил ли сервер вообще. Отказ и молчание — разные вещи:
            /// на отказ есть смысл попробовать иначе, на молчание — нет.
            /// </summary>
            public bool Answered { get; set; }

            /// <summary>`reactionInfo` из ответа — счётчики и своя реакция.</summary>
            public IDictionary<string, object> Info { get; set; }
        }

        /// <summary>
        /// MSG_REACTION — поставить реакцию или сменить её на другую.
        ///
        /// Лицо пробуется в двух видах. У сервера свой список разрешённых
        /// реакций, и что в нём записано — снаружи не видно: большой палец
        /// в простом виде он не признал вовсе, ответив
        /// `error.message.like.unknown.like`, тогда как остальные лица
        /// принял. Виды отличаются невидимым знаком U+FE0F — «показывать
        /// картинкой»; какой из них в списке, узнаём попыткой.
        /// </summary>
        public async Task<ReactionResult> SetReactionAsync(long chatId, string messageId, string emoji)
        {
            ReactionResult last = new ReactionResult();

            foreach (string form in Forms(emoji))
            {
                var reaction = new Dictionary<string, object>
                {
                    { "reactionType", "EMOJI" },
                    { "id", form }
                };

                last = await ReactAsync(Opcodes.MsgReaction, chatId, messageId, reaction);
                if (last.Ok) return last;

                Log.Write("chat", "реакция " + Spell(form) + " не принята: " + LastError);

                // Сервер не ответил — вторая попытка ничего не изменит.
                if (!last.Answered) return last;
            }

            return last;
        }

        /// <summary>
        /// Виды одного лица: как есть и с обратным знаком показа. Второй
        /// нужен ровно на случай, когда сервер знает лицо в другом виде.
        /// </summary>
        private static IEnumerable<string> Forms(string emoji)
        {
            const char Picture = '\uFE0F';

            if (string.IsNullOrEmpty(emoji)) yield break;

            yield return emoji;

            yield return emoji[emoji.Length - 1] == Picture
                ? emoji.Substring(0, emoji.Length - 1)
                : emoji + Picture;
        }

        /// <summary>
        /// Лицо номерами знаков — для журнала. В самом журнале лицо
        /// выглядит вопросиками, и по нему не понять, что именно ушло.
        /// </summary>
        private static string Spell(string emoji)
        {
            var made = new System.Text.StringBuilder();

            foreach (char sign in emoji ?? "")
            {
                if (made.Length > 0) made.Append(' ');
                made.Append("U+").Append(((int)sign).ToString("X4"));
            }

            return made.ToString();
        }

        /// <summary>MSG_CANCEL_REACTION — снять свою реакцию.</summary>
        public async Task<ReactionResult> CancelReactionAsync(long chatId, string messageId)
        {
            return await ReactAsync(Opcodes.MsgCancelReaction, chatId, messageId, null);
        }

        private async Task<ReactionResult> ReactAsync(int opcode, long chatId, string messageId,
                                                      IDictionary<string, object> reaction)
        {
            LastError = null;

            // Номер сообщения числом — как и везде, где сервер ждёт ссылку.
            long id;
            if (!long.TryParse(messageId, out id) || id == 0)
            {
                LastError = "Не разобрать номер сообщения.";
                return new ReactionResult();
            }

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "messageId", id }
            };

            if (reaction != null) payload["reaction"] = reaction;

            Packet packet = await MaxApi.Instance.RequestAsync(opcode, payload);

            if (packet == null) { LastError = "Нет связи с сервером."; return new ReactionResult(); }

            if (packet.IsError)
            {
                LastError = Refusal(packet);
                return new ReactionResult { Answered = true };
            }

            // Сервер отвечает всеми реакциями сообщения разом, а не одной
            // своей: считать их самим не нужно.
            return new ReactionResult
            {
                Ok = true,
                Info = MessagePack.Map(packet.Map, "reactionInfo")
            };
        }

        /// <summary>MSG_DELETE. `forEveryone` — удалить у всех, иначе у себя.</summary>
        public async Task<bool> DeleteAsync(long chatId, IList<object> messageIds, bool forEveryone)
        {
            // Номера числами, как и везде. Строками сервер отвечал
            // «Expected number at 26» — то есть спотыкался на первом же
            // номере в списке, — и удаление молча не происходило.
            var numeric = new List<object>();

            foreach (object item in messageIds ?? new List<object>())
            {
                long id;
                if (long.TryParse(item != null ? item.ToString() : "", out id) && id != 0)
                {
                    numeric.Add(id);
                }
            }

            if (numeric.Count == 0)
            {
                Log.Write("chat", "удаление отменено: не разобрать номера сообщений");
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "messageIds", numeric },
                { "forMe", !forEveryone }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgDelete, payload);
            return packet != null && !packet.IsError;
        }

        /// <summary>
        /// Пересылка: MSG_SEND с пустым сообщением и ссылкой типа FORWARD —
        /// то же, что показывает разбор входящих пересылок.
        /// </summary>
        public async Task<bool> ForwardAsync(string messageId, long sourceChatId, long targetChatId)
        {
            // Номер числом, как и в ответе: строку сервер за ссылку на
            // сообщение не принимает, и пересылка молча не проходит.
            long forwarded;
            if (!long.TryParse(messageId, out forwarded) || forwarded == 0)
            {
                Log.Write("chat", "пересылка отменена: номер «" + messageId + "» не число");
                return false;
            }

            var message = new Dictionary<string, object>
            {
                { "cid", -UnixTime.Now },
                { "text", "" },
                { "elements", new List<object>() },
                { "attaches", new List<object>() },
                {
                    "link", new Dictionary<string, object>
                    {
                        { "type", "FORWARD" },
                        { "chatId", sourceChatId },
                        { "messageId", forwarded }
                    }
                }
            };

            var payload = new Dictionary<string, object>
            {
                { "chatId", targetChatId },
                { "message", message },
                { "notify", true }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.MsgSend, payload);
            return packet != null && !packet.IsError;
        }

        /// <summary>
        /// MSG_TYPING. Не отправляется в режиме «не показывать набор», и не
        /// чаще раза в три секунды: сервер и так держит признак несколько
        /// секунд, а на каждую букву слать его незачем.
        /// </summary>
        public void SendTyping(long chatId)
        {
            if (AppSettings.AntiTyping) return;

            long now = UnixTime.Now;
            if (now - _lastTypingSent < 3000) return;
            _lastTypingSent = now;

            var payload = new Dictionary<string, object>
            {
                { "chatId", chatId },
                { "type", "TEXT" }
            };

            MaxApi.Instance.Post(Opcodes.MsgTyping, payload);
        }

        #endregion

        #region Файлы

        /// <summary>
        /// FILE_DOWNLOAD по идентификатору файла: `fileId` вместе с чатом
        /// и сообщением, в ответе поле `url`. Это основной путь для
        /// вложений-файлов.
        /// </summary>
        public async Task<string> ResolveFileUrlAsync(long fileId, long chatId, string messageId)
        {
            if (fileId == 0) return null;

            long numericId;
            long.TryParse(messageId, out numericId);

            // Номер сообщения числом и `itemType` — как в оригинале. Со
            // строкой и без этого поля сервер ссылки не даёт.
            var payload = new Dictionary<string, object>
            {
                { "fileId", fileId },
                { "chatId", chatId },
                { "messageId", numericId },
                { "itemType", "REGULAR" }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.FileDownload, payload);
            if (packet == null || packet.IsError) return null;

            return MessagePack.String(packet.Map, "url");
        }

        #endregion

        #region Отметки

        /// <summary>CHAT_MARK — отметка о прочтении.</summary>
        public async Task MarkReadAsync(long chatId, MaxMessage last)
        {
            if (last == null) return;
            await ChatsModule.Instance.MarkReadAsync(chatId, last.MessageId, last.Time);
        }

        /// <summary>CHAT_SUBSCRIBE — подписка на обновления открытого чата.</summary>
        public async Task SubscribeAsync(long chatId, bool subscribe)
        {
            await ChatsModule.Instance.SubscribeAsync(chatId, subscribe);
        }

        #endregion
    }
}
