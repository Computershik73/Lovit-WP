using System;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;

namespace Lovit.Core.Transport
{
    /// <summary>
    /// Соединение с сервером MAX: защищённый поток и разбор его на пакеты.
    ///
    /// Криптография своя — OpenSSL в [NativeTlsStream]; системный стек
    /// остаётся запасным путём для сборок, где своей библиотеки нет.
    /// Почему не наоборот — написано там же.
    /// </summary>
    public class MaxConnection
    {
        private ITlsStream _stream;
        private bool _closed;

        public bool IsConnected { get; private set; }

        /// <summary>Чем разговариваем: «OpenSSL TLSv1.2 ECDHE-RSA-AES128-SHA256».</summary>
        public string TransportDescription { get; private set; }

        /// <summary>Причина последнего отказа — её показывает экран входа.</summary>
        public string LastError { get; private set; }

        /// <summary>Пришёл разобранный пакет.</summary>
        public event Action<Packet> PacketReceived;

        /// <summary>Соединение оборвалось — само или по нашей воле.</summary>
        public event Action Closed;

        public MaxConnection()
        {
            TransportDescription = "";
            LastError = "";
        }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            Close();
            _closed = false;

            bool own = NativeTlsStream.IsAvailable;
            Log.Write("tls", "соединяемся с " + host + ":" + port
                             + (own ? " своей криптографией" : " системной"));

            ITlsStream stream = own
                ? (ITlsStream)new NativeTlsStream()
                : new SystemTlsStream();

            if (!await stream.ConnectAsync(host, port))
            {
                LastError = stream.Error;
                Log.Write("tls", "не соединились: " + LastError);

                stream.Close();
                return false;
            }

            _stream = stream;
            TransportDescription = stream.Description;
            IsConnected = true;

            Log.Write("tls", "соединились, " + TransportDescription);

            var ignored = ReadLoopAsync();
            return true;
        }

        /// <summary>
        /// Отправить кадр.
        ///
        /// Всё внутри — под перехватом: у метода без возврата исключение
        /// подхватить некому, и оно уносит приложение целиком, не оставив
        /// следа. А случиться тут может всякое: спящий телефон рвёт сокет
        /// молча, и первая же отправка после сна попадает в обломки.
        /// </summary>
        /// <summary>
        /// Сколько байт прочитано из потока за всю жизнь соединения.
        ///
        /// Нужно, чтобы отличить молчащий сервер от медленного. Ответ на
        /// вход бывает в мегабайты; на неспешной сети он идёт дольше, чем
        /// запрос готов ждать, и со стороны это неотличимо от тишины —
        /// а лечится совсем иначе.
        /// </summary>
        public long BytesRead { get; private set; }

        /// <summary>
        /// Ответ пришёл, но разобрать его не вышло. Номер — того запроса,
        /// который его ждал: пусть узнает сразу, а не через полминуты.
        /// </summary>
        public event Action<int> PacketLost;

        public async void Send(byte[] frame)
        {
            try
            {
                ITlsStream stream = _stream;
                if (!IsConnected || stream == null) return;

                if (await stream.WriteAsync(frame)) return;

                LastError = stream.Error;
                Log.Write("tls", "отправка не прошла (" + frame.Length + " б): " + LastError);

                HandleBroken();
            }
            catch (Exception error)
            {
                Log.Fail("tls", "отправка сорвалась", error);
                HandleBroken();
            }
        }

        /// <summary>
        /// Чтение потока. Пакет приходит не целиком и не по одному: заголовок
        /// может разорваться между двумя чтениями, а в одном чтении может
        /// приехать полтора пакета. Поэтому сначала добираем ровно десять байт
        /// заголовка, по нему узнаём длину и добираем тело.
        /// </summary>
        private async Task ReadLoopAsync()
        {
            try
            {
                while (IsConnected)
                {
                    byte[] header = await ReadExactlyAsync(Packet.HeaderSize);
                    if (header == null)
                    {
                        Log.Write("tls", "поток кончился на заголовке");
                        break;
                    }

                    int length = Packet.BodyLength(header);

                    // Большое тело читается долго, и это стоит видеть: по
                    // одной строке в журнале понятно, ждём мы сервер или
                    // качаем от него мегабайт.
                    DateTime began = DateTime.UtcNow;
                    if (length > 65536)
                    {
                        Log.Write("tls", "принимаем тело " + (length / 1024) + " КБ");
                    }

                    byte[] body = length > 0 ? await ReadExactlyAsync(length) : new byte[0];

                    if (length > 65536)
                    {
                        Log.Write("tls", "тело " + (length / 1024) + " КБ принято за "
                                         + (int)(DateTime.UtcNow - began).TotalMilliseconds + " мс");
                    }
                    if (body == null)
                    {
                        Log.Write("tls", "поток кончился на теле, ждали " + length + " б");
                        break;
                    }

                    Packet packet;
                    try
                    {
                        packet = Packet.Unpack(header, body);
                    }
                    catch (Exception error)
                    {
                        // Один нечитаемый пакет — не повод рвать соединение:
                        // следующий начинается с нового заголовка. Но знать о
                        // нём надо: потерянный так ответ выглядит потом просто
                        // как молчание сервера.
                        Log.Fail("tls", "пакет не разобрался, тело " + length
                                        + " б, сжатие " + header[6]
                                        + ", опкод " + ((header[4] << 8) | header[5])
                                        + ", номер " + ((header[2] << 8) | header[3]), error);

                        // Сам пакет — на телефон: по журналу его не
                        // разобрать, а по байтам разберём.
                        var kept = Rescue.KeepAsync(header, body, error.Message);

                        // И тому, кто ждёт ответа, говорим сразу. Раньше он
                        // ждал впустую целых полминуты и объявлял, что
                        // сервер не ответил, — хотя ответ пришёл и был
                        // отброшен здесь же.
                        Action<int> lost = PacketLost;
                        if (lost != null) lost((header[2] << 8) | header[3]);
                        continue;
                    }

                    var handler = PacketReceived;
                    if (handler != null) handler(packet);
                }
            }
            catch (Exception error)
            {
                Log.Fail("tls", "чтение прервалось", error);
            }

            HandleBroken();
        }

        /// <summary>
        /// Дочитывает ровно `count` байт. Меньше нельзя: заголовок пакета
        /// разорвётся между двумя чтениями, и дальше по потоку поедет всё.
        /// Ноль означает закрытое соединение, -2 — что данных пока нет.
        /// </summary>
        private async Task<byte[]> ReadExactlyAsync(int count)
        {
            var buffer = new byte[count];
            var chunk = new byte[count];
            int filled = 0;

            while (filled < count)
            {
                ITlsStream stream = _stream;
                if (stream == null || _closed) return null;

                int loaded = await stream.ReadAsync(chunk, count - filled);

                // Тишина в сети — обычное дело: между сообщениями проходят
                // минуты, и каждый такой шаг лишь даёт очередь отправке.
                if (loaded == TlsTimedOut) continue;
                if (loaded <= 0) return null;

                System.Buffer.BlockCopy(chunk, 0, buffer, filled, loaded);
                filled += loaded;

                BytesRead += loaded;
            }

            return buffer;
        }

        /// <summary>Возврат «данных пока нет» из защищённого потока.</summary>
        private const int TlsTimedOut = -2;

        private void HandleBroken()
        {
            if (_closed) return;

            Log.Write("tls", "соединение оборвалось");
            Close();

            var handler = Closed;
            if (handler != null) handler();
        }

        public void Close()
        {
            _closed = true;
            IsConnected = false;

            ITlsStream stream = _stream;
            _stream = null;

            if (stream != null) stream.Close();
        }
    }
}
