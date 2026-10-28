using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Transport;
using Windows.Data.Json;

namespace Lovit.Backend.Calls
{
    /// <summary>
    /// Сигнализация звонка: WebSocket `ws2`, по которому ходит всё, что
    /// внутри звонка, — принять, сбросить, обменяться SDP и кандидатами.
    ///
    /// Системный MessageWebSocket здесь не годится по той же причине, что и
    /// системный TLS для основного сокета: сервер звонков говорит на том, чего
    /// Windows Phone 8.1 не умеет. Поэтому WebSocket свой, поверх нашего
    /// OpenSSL (NativeTlsStream), — по RFC 6455, ровно в том объёме, что
    /// нужен: текстовые кадры, склейка продолжений, ping/pong и закрытие.
    ///
    /// Сообщения — JSON строкой (протокол описан в Komet, calls-protocol.md):
    /// запрос `{"command": …, "sequence": N}`, ответ
    /// `{"type": "response", "sequence": N}`, ошибка `{"type": "error", …}`,
    /// уведомление `{"notification": …}`. Держать сокет живым сервер просит
    /// прикладным текстом `ping`, на него отвечаем `pong`.
    /// </summary>
    public sealed class CallSignaling
    {
        /// <summary>Сколько ждать ответа на команду.</summary>
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

        private ITlsStream _stream;
        private long _sequence;
        private bool _closed;

        private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _waiting =
            new Dictionary<long, TaskCompletionSource<JsonObject>>();

        /// <summary>
        /// Уведомление сервера (`notification`). Приходит на потоке чтения —
        /// кому нужен поток разметки, тот переходит на него сам.
        /// </summary>
        public event Action<JsonObject> Notification;

        /// <summary>Сокет закрылся — сам или по нашей просьбе.</summary>
        public event Action Closed;

        public bool IsOpen { get { return _stream != null && !_closed; } }

        #region Подключение

        public async Task<bool> ConnectAsync(string url)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address)) return Fail("не разобрался адрес");

            string host = address.Host;
            int port = address.Port > 0 ? address.Port : 443;

            ITlsStream stream = NativeTlsStream.IsAvailable
                ? (ITlsStream)new NativeTlsStream()
                : new SystemTlsStream();

            if (!await stream.ConnectAsync(host, port)) return Fail("TLS: " + stream.Error);

            // Ключ — шестнадцать случайных байт в base64; сервер отвечает
            // производной от него, но проверять её незачем: нам важно лишь,
            // что ответ «101».
            var key = new byte[16];
            new Random().NextBytes(key);

            string request = "GET " + address.PathAndQuery + " HTTP/1.1\r\n"
                             + "Host: " + host + "\r\n"
                             + "Upgrade: websocket\r\n"
                             + "Connection: Upgrade\r\n"
                             + "Sec-WebSocket-Key: " + Convert.ToBase64String(key) + "\r\n"
                             + "Sec-WebSocket-Version: 13\r\n"
                             + "User-Agent: okhttp/4.12.0\r\n"
                             + "\r\n";

            if (!await stream.WriteAsync(Encoding.UTF8.GetBytes(request)))
            {
                stream.Close();
                return Fail("запрос не ушёл");
            }

            HandshakeReply reply = await ReadHeadAsync(stream);
            if (reply == null)
            {
                stream.Close();
                return Fail("сервер не ответил на рукопожатие");
            }

            string status = reply.Head.Split('\n')[0].Trim();
            if (status.IndexOf(" 101", StringComparison.Ordinal) < 0)
            {
                stream.Close();
                return Fail("сервер не перешёл на WebSocket: " + status);
            }

            _stream = stream;
            Log.Write("call", "сигнализация открыта: " + host);

            var ignored = ReadLoopAsync(reply.Rest);
            return true;
        }

        private static bool Fail(string why)
        {
            Log.Write("call", "сигнализация не открылась — " + why);
            return false;
        }

        /// <summary>Ответ на рукопожатие: заголовок и то, что пришло следом.</summary>
        private sealed class HandshakeReply
        {
            public string Head;
            public byte[] Rest;
        }

        /// <summary>
        /// Заголовок ответа на рукопожатие. Всё, что пришло после пустой
        /// строки, — уже первые кадры; их не теряем, а отдаём дальше.
        /// </summary>
        private static async Task<HandshakeReply> ReadHeadAsync(ITlsStream stream)
        {
            var collected = new List<byte>();
            var buffer = new byte[4096];
            DateTime deadline = DateTime.UtcNow + CommandTimeout;

            while (DateTime.UtcNow < deadline)
            {
                int got = await stream.ReadAsync(buffer, buffer.Length);
                if (got == -2) continue;
                if (got <= 0) return null;

                for (int i = 0; i < got; i++) collected.Add(buffer[i]);

                int end = FindHeadEnd(collected);
                if (end < 0) continue;

                byte[] all = collected.ToArray();
                var rest = new byte[all.Length - end];
                Array.Copy(all, end, rest, 0, rest.Length);

                return new HandshakeReply { Head = Encoding.UTF8.GetString(all, 0, end), Rest = rest };
            }
            return null;
        }

        private static int FindHeadEnd(List<byte> data)
        {
            for (int i = 3; i < data.Count; i++)
            {
                if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                    return i + 1;
            }
            return -1;
        }

        #endregion

        #region Команды

        /// <summary>
        /// Отправить команду и дождаться ответа. null — ответа не было, связь
        /// оборвалась или сервер ответил ошибкой (она уходит в журнал).
        /// </summary>
        public async Task<JsonObject> SendAsync(string command, JsonObject extra)
        {
            if (!IsOpen) return null;

            long sequence = Interlocked.Increment(ref _sequence);

            var message = extra ?? new JsonObject();
            message["command"] = JsonValue.CreateStringValue(command);
            message["sequence"] = JsonValue.CreateNumberValue(sequence);

            var answer = new TaskCompletionSource<JsonObject>();
            lock (_waiting) _waiting[sequence] = answer;

            if (!await SendTextAsync(message.Stringify()))
            {
                lock (_waiting) _waiting.Remove(sequence);
                return null;
            }

            Task finished = await Task.WhenAny(answer.Task, Task.Delay(CommandTimeout));
            lock (_waiting) _waiting.Remove(sequence);

            if (finished != answer.Task)
            {
                Log.Write("call", "сигнализация: на «" + command + "» ответа нет");
                return null;
            }
            return answer.Task.Result;
        }

        /// <summary>Сбросить звонок. `reason` — HUNGUP, REJECTED и прочие.</summary>
        public Task<JsonObject> HangupAsync(string reason)
        {
            var extra = new JsonObject();
            extra["reason"] = JsonValue.CreateStringValue(reason ?? "HUNGUP");
            return SendAsync("hangup", extra);
        }

        #endregion

        #region Кадры

        private Task<bool> SendTextAsync(string text)
        {
            return SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Кадр от клиента обязан быть замаскирован — сервер без маски
        /// соединение рвёт. Маска — четыре случайных байта, которыми по кругу
        /// ксорится содержимое.
        /// </summary>
        private async Task<bool> SendFrameAsync(int opcode, byte[] payload)
        {
            ITlsStream stream = _stream;
            if (stream == null || _closed) return false;

            var frame = new List<byte>(payload.Length + 14);
            frame.Add((byte)(0x80 | opcode));     // FIN и вид кадра

            if (payload.Length < 126)
            {
                frame.Add((byte)(0x80 | payload.Length));
            }
            else if (payload.Length <= 0xFFFF)
            {
                frame.Add(0x80 | 126);
                frame.Add((byte)(payload.Length >> 8));
                frame.Add((byte)payload.Length);
            }
            else
            {
                frame.Add(0x80 | 127);
                long length = payload.Length;
                for (int shift = 56; shift >= 0; shift -= 8) frame.Add((byte)(length >> shift));
            }

            var mask = new byte[4];
            new Random().NextBytes(mask);
            frame.AddRange(mask);

            for (int i = 0; i < payload.Length; i++) frame.Add((byte)(payload[i] ^ mask[i & 3]));

            return await stream.WriteAsync(frame.ToArray()).ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(byte[] first)
        {
            var pending = new List<byte>(first);
            var message = new List<byte>();
            var buffer = new byte[16384];

            try
            {
                while (!_closed)
                {
                    // Сперва разобрать то, что уже накопилось: кадры часто
                    // приходят пачкой в одном чтении.
                    while (TryTakeFrame(pending, message)) { }

                    if (_closed) break;

                    ITlsStream stream = _stream;
                    if (stream == null) break;

                    int got = await stream.ReadAsync(buffer, buffer.Length).ConfigureAwait(false);
                    if (got == -2) continue;
                    if (got <= 0) break;

                    for (int i = 0; i < got; i++) pending.Add(buffer[i]);
                }
            }
            catch (Exception error)
            {
                Log.Write("call", "сигнализация: чтение оборвалось — " + error.Message);
            }

            Shutdown();
        }

        /// <summary>
        /// Снять один кадр из накопленного. false — кадра целиком ещё нет.
        /// Сообщение может прийти несколькими кадрами-продолжениями — они
        /// складываются в `message`, пока не придёт последний.
        /// </summary>
        private bool TryTakeFrame(List<byte> pending, List<byte> message)
        {
            if (pending.Count < 2) return false;

            bool final = (pending[0] & 0x80) != 0;
            int opcode = pending[0] & 0x0F;
            bool masked = (pending[1] & 0x80) != 0;
            long length = pending[1] & 0x7F;
            int at = 2;

            if (length == 126)
            {
                if (pending.Count < 4) return false;
                length = (pending[2] << 8) | pending[3];
                at = 4;
            }
            else if (length == 127)
            {
                if (pending.Count < 10) return false;
                length = 0;
                for (int i = 2; i < 10; i++) length = (length << 8) | pending[i];
                at = 10;
            }

            var mask = new byte[4];
            if (masked)
            {
                if (pending.Count < at + 4) return false;
                for (int i = 0; i < 4; i++) mask[i] = pending[at + i];
                at += 4;
            }

            if (pending.Count < at + length) return false;

            var payload = new byte[length];
            for (int i = 0; i < length; i++)
            {
                byte value = pending[at + i];
                payload[i] = masked ? (byte)(value ^ mask[i & 3]) : value;
            }
            pending.RemoveRange(0, at + (int)length);

            switch (opcode)
            {
                case 0x0:   // продолжение
                case 0x1:   // текст
                case 0x2:   // двоичный — сервер присылает и JSON так
                    message.AddRange(payload);
                    if (final)
                    {
                        string text = Encoding.UTF8.GetString(message.ToArray(), 0, message.Count);
                        message.Clear();
                        OnText(text);
                    }
                    break;

                case 0x8:   // закрытие
                    Log.Write("call", "сигнализация: сервер закрыл сокет");
                    _closed = true;
                    break;

                case 0x9:   // ping на уровне WebSocket — отвечаем pong с тем же
                    var ignored = SendFrameAsync(0xA, payload);
                    break;
            }
            return true;
        }

        private void OnText(string text)
        {
            // Прикладной keepalive: сервер шлёт голое «ping» текстом.
            if (text == "ping")
            {
                var ignored = SendTextAsync("pong");
                return;
            }

            JsonObject map;
            if (!JsonObject.TryParse(text, out map)) return;

            string type = Text(map, "type");
            IJsonValue sequenceValue;

            if ((type == "response" || type == "error")
                && map.TryGetValue("sequence", out sequenceValue)
                && sequenceValue.ValueType == JsonValueType.Number)
            {
                long sequence = (long)sequenceValue.GetNumber();

                TaskCompletionSource<JsonObject> answer;
                lock (_waiting) _waiting.TryGetValue(sequence, out answer);

                if (type == "error")
                {
                    Log.Write("call", "сигнализация: отказ — " + (Text(map, "error") ?? "?"));
                    if (answer != null) answer.TrySetResult(null);
                }
                else if (answer != null)
                {
                    answer.TrySetResult(map);
                }
                return;
            }

            string notification = Text(map, "notification");
            if (notification == null) return;

            Log.Write("call", "сигнализация: уведомление «" + notification + "»");

            Action<JsonObject> handler = Notification;
            if (handler != null) handler(map);
        }

        #endregion

        /// <summary>
        /// Закрыть сокет. Сперва — вежливо попрощаться кадром закрытия: сервер
        /// тогда не ждёт нашего возвращения. Не дошло за секунду — не беда,
        /// закрываем всё равно.
        /// </summary>
        public async Task CloseAsync()
        {
            if (_stream == null) return;

            if (!_closed)
            {
                Task<bool> farewell = SendFrameAsync(0x8, new byte[] { 0x03, 0xE8 });
                await Task.WhenAny(farewell, Task.Delay(1000));
            }

            _closed = true;
            Shutdown();
        }

        private void Shutdown()
        {
            ITlsStream stream = Interlocked.Exchange(ref _stream, null);
            if (stream == null) return;

            _closed = true;
            stream.Close();

            lock (_waiting)
            {
                foreach (TaskCompletionSource<JsonObject> answer in _waiting.Values)
                    answer.TrySetResult(null);
                _waiting.Clear();
            }

            Action handler = Closed;
            if (handler != null) handler();
        }

        private static string Text(JsonObject map, string key)
        {
            IJsonValue value;
            if (!map.TryGetValue(key, out value) || value.ValueType != JsonValueType.String) return null;
            return value.GetString();
        }
    }
}
