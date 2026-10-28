using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Lovit.Core.Transport;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Загрузка картинок по HTTPS.
    ///
    /// Тем же путём, что и сам протокол, — своей криптографией. Причина та
    /// же: у CDN мессенджера свой удостоверяющий центр, которого в хранилище
    /// Windows Phone 8.1 нет, и системный HttpClient до него не доходит.
    /// Корни у нас вшиты (в том числе ISRG, которым подписан CDN), поэтому
    /// цепочка проверяется полностью.
    ///
    /// Своего HTTP тут ровно столько, сколько нужно, чтобы забрать картинку:
    /// GET, перенаправления и два способа узнать длину тела.
    /// </summary>
    public static class MediaHttp
    {
        private const int MaxRedirects = 3;

        /// <summary>
        /// Больше этого не держим в памяти: картинка размером с фильм — ошибка.
        /// К видео это не относится — оно пишется прямо на диск, минуя память.
        /// </summary>
        private const int MaxBody = 12 * 1024 * 1024;

        /// <summary>А вот на диск можно и много: ролик столько и весит.</summary>
        private const long MaxFile = 256L * 1024 * 1024;

        /// <summary>
        /// Скачать прямо в файл, не собирая целиком в памяти.
        ///
        /// Ролик на 720p весит десятки мегабайт, и на телефоне с четырьмя
        /// сотнями мегабайт под приложение собрать его в памяти нельзя —
        /// раньше на этом всё и обрывалось, причём молча.
        /// </summary>
        public static async Task<bool> GetToStreamAsync(string url,
                                                        Windows.Storage.Streams.IOutputStream sink,
                                                        Progress progress)
        {
            string current = url;

            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                string host;
                int port;
                string path;

                if (!Split(current, out host, out port, out path))
                {
                    Log.Write("mem", "загрузка: не разобрали адрес");
                    return false;
                }

                ITlsStream stream = NativeTlsStream.IsAvailable
                    ? (ITlsStream)new NativeTlsStream()
                    : new SystemTlsStream();

                try
                {
                    if (!await stream.ConnectAsync(host, port))
                    {
                        // С причиной: у чужого CDN может не найтись
                        // удостоверяющего центра в нашем списке корней, и
                        // отличить это от «сеть недоступна» иначе нельзя.
                        Log.Write("mem", "загрузка: не соединились с " + host
                                         + " — " + (stream.Error ?? "причина не названа"));
                        return false;
                    }

                    string request = "GET " + path + " HTTP/1.1\r\n"
                        + "Host: " + host + "\r\n"
                        + "User-Agent: okhttp/4.12.0\r\n"
                        + "Accept: */*\r\n"
                        + "Connection: close\r\n\r\n";

                    if (!await stream.WriteAsync(Encoding.UTF8.GetBytes(request))) return false;

                    Response response = await ReadResponseAsync(stream, progress, sink);
                    if (response == null)
                    {
                        Log.Write("mem", "загрузка: ответа нет от " + host);
                        return false;
                    }

                    if (response.Status >= 300 && response.Status < 400 && response.Location != null)
                    {
                        current = Absolute(current, response.Location);
                        continue;
                    }

                    if (response.Status >= 200 && response.Status < 300) return true;

                    Log.Write("mem", "загрузка: " + host + " ответил " + response.Status);
                    return false;
                }
                catch (Exception error)
                {
                    Log.Write("mem", "загрузка сорвалась: " + error.Message);
                    return false;
                }
                finally
                {
                    stream.Close();
                }
            }

            Log.Write("mem", "загрузка: слишком много перенаправлений");
            return false;
        }

        /// <summary>
        /// Сколько прошло из скольких. Второе бывает нулём: длину сервер
        /// сообщает не всегда, и тогда известно только «идёт».
        /// </summary>
        public delegate void Progress(long done, long total);

        /// <summary>Кусок отправки. Мельче — чаще видно движение полосы.</summary>
        private const int UploadChunk = 64 * 1024;

        public static Task<byte[]> GetAsync(string url)
        {
            return GetAsync(url, null);
        }

        /// <summary>Отрезок файла и его полная длина — для проигрывания на лету.</summary>
        public class Piece
        {
            public byte[] Data { get; set; }
            public long Total { get; set; }
        }

        /// <summary>
        /// Кусок файла по его смещению. На этом держится проигрывание без
        /// полной закачки: проигрыватель сам просит те куски, которые ему
        /// нужны, и разбирает их тоже сам.
        /// </summary>
        public static async Task<Piece> GetRangeAsync(string url, long from, long count)
        {
            string current = url;

            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                string host;
                int port;
                string path;

                if (!Split(current, out host, out port, out path)) return null;

                ITlsStream stream = NativeTlsStream.IsAvailable
                    ? (ITlsStream)new NativeTlsStream()
                    : new SystemTlsStream();

                try
                {
                    if (!await stream.ConnectAsync(host, port)) return null;

                    string request = "GET " + path + " HTTP/1.1\r\n"
                        + "Host: " + host + "\r\n"
                        + "User-Agent: okhttp/4.12.0\r\n"
                        + "Accept: */*\r\n"
                        + "Range: bytes=" + from + "-" + (from + count - 1) + "\r\n"
                        + "Connection: close\r\n\r\n";

                    if (!await stream.WriteAsync(Encoding.UTF8.GetBytes(request))) return null;

                    Response response = await ReadResponseAsync(stream, null, null);
                    if (response == null) return null;

                    if (response.Status >= 300 && response.Status < 400 && response.Location != null)
                    {
                        current = Absolute(current, response.Location);
                        continue;
                    }

                    if (response.Status != 206 && response.Status != 200) return null;

                    return new Piece
                    {
                        Data = response.Body ?? new byte[0],
                        // Полная длина стоит в Content-Range после косой черты;
                        // при ответе целиком её заменяет длина самого тела.
                        Total = response.Status == 206
                            ? TotalOf(response.Range, response.Body)
                            : (response.Body != null ? response.Body.Length : 0)
                    };
                }
                catch (Exception)
                {
                    return null;
                }
                finally
                {
                    stream.Close();
                }
            }

            return null;
        }

        private static long TotalOf(string range, byte[] body)
        {
            if (!string.IsNullOrEmpty(range))
            {
                int slash = range.LastIndexOf('/');

                long total;
                if (slash >= 0 && long.TryParse(range.Substring(slash + 1), out total)) return total;
            }

            return body != null ? body.Length : 0;
        }

        public static async Task<byte[]> GetAsync(string url, Progress progress)
        {
            string current = url;

            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                Response response = await FetchAsync(current, progress);
                if (response == null) return null;

                if (response.Status >= 300 && response.Status < 400 && response.Location != null)
                {
                    current = Absolute(current, response.Location);
                    continue;
                }

                return response.Status == 200 ? response.Body : null;
            }

            return null;
        }

        /// <summary>
        /// POST с телом — этим уезжают вложения.
        ///
        /// Перенаправления здесь не отслеживаются нарочно: адрес заливки
        /// выдаёт сам сервер отдельным запросом, и водить по редиректам
        /// он его не станет. Ответ возвращается как есть — у фотографий в
        /// нём приезжает JSON с токеном.
        /// </summary>
        public static Task<byte[]> PostAsync(string url, byte[] body,
                                             IDictionary<string, string> headers)
        {
            return PostAsync(url, body, headers, null);
        }

        public static Task<byte[]> PostAsync(string url, byte[] body,
                                             IDictionary<string, string> headers,
                                             Progress progress)
        {
            return PostAsync(url, body, null, body != null ? body.Length : 0, headers, progress);
        }

        /// <summary>
        /// То же, но тело читается с диска по мере отправки.
        ///
        /// Большому файлу в памяти места нет: полуторасотенный мегабайтами
        /// ролик, прочитанный целиком, уносил приложение — телефону просто
        /// негде взять столько подряд. С диска же он уходит теми же кусками
        /// по шестьдесят четыре килобайта, и расход не зависит от размера
        /// вовсе.
        /// </summary>
        public static Task<byte[]> PostFileAsync(string url, Windows.Storage.StorageFile file,
                                                 long size, IDictionary<string, string> headers,
                                                 Progress progress)
        {
            return PostAsync(url, null, file, size, headers, progress);
        }

        private static async Task<byte[]> PostAsync(string url, byte[] body,
                                                    Windows.Storage.StorageFile file, long size,
                                                    IDictionary<string, string> headers,
                                                    Progress progress)
        {
            string host;
            int port;
            string path;

            if (!Split(url, out host, out port, out path))
            {
                Log.Write("mem", "заливка: не разобрали адрес CDN");
                return null;
            }

            ITlsStream stream = NativeTlsStream.IsAvailable
                ? (ITlsStream)new NativeTlsStream()
                : new SystemTlsStream();

            bool closed = false;

            try
            {
                Task<byte[]> work = PostOnceAsync(stream, host, port, path,
                                                  body, file, size, headers, progress);

                // Срок нужен обязательно: чтение из потока само по себе не
                // кончается никогда, и молчащий CDN раньше подвешивал отправку
                // навсегда — в логе после запроса адреса не оставалось ни
                // строчки, а человек видел просто «не отправляется».
                Task done = await Task.WhenAny(work, Task.Delay(UploadTimeout));
                if (done != work)
                {
                    Log.Write("mem", "заливка молчит дольше " + (UploadTimeout / 1000)
                                     + " с, обрываем: " + host);

                    // Закрытый поток срывает и само чтение — брошенная задача
                    // не останется висеть.
                    stream.Close();
                    closed = true;
                    return null;
                }

                return await work;
            }
            finally
            {
                if (!closed) stream.Close();
            }
        }

        /// <summary>
        /// Больше этого заливка не длится — обрываем.
        ///
        /// Срок нарочно щедрый: вставшую связь ловит сам поток, отмеряющий
        /// застой, а этот предел нужен лишь на случай, когда не сработало и
        /// это. Скупой предел здесь резал бы честную передачу большого
        /// вложения по медленной мобильной связи.
        /// </summary>
        private const int UploadTimeout = 300 * 1000;

        private static async Task<byte[]> PostOnceAsync(ITlsStream stream, string host, int port,
                                                        string path, byte[] body,
                                                        Windows.Storage.StorageFile file, long size,
                                                        IDictionary<string, string> headers,
                                                        Progress progress)
        {
            try
            {
                if (!await stream.ConnectAsync(host, port))
                {
                    Log.Write("mem", "заливка: не соединились с " + host);
                    return null;
                }

                var request = new StringBuilder();
                request.Append("POST ").Append(path).Append(" HTTP/1.1\r\n");
                request.Append("Host: ").Append(host).Append("\r\n");
                request.Append("Accept: */*\r\n");

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                    }
                }

                request.Append("Content-Length: ").Append(size).Append("\r\n");

                // Соединение закрывается сразу: держать его незачем, а по
                // закрытию видно конец тела, даже когда длины в ответе нет.
                request.Append("Connection: close\r\n\r\n");

                if (!await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString())))
                {
                    Log.Write("mem", "заливка: заголовки не ушли");
                    return null;
                }

                bool sent = file != null
                    ? await WriteFileAsync(stream, file, size, progress)
                    : body == null || body.Length == 0 || await WriteBodyAsync(stream, body, progress);

                if (!sent)
                {
                    Log.Write("mem", "заливка: тело не ушло, " + size + " байт");
                    return null;
                }

                // Ответ CDN на заливку — короткий JSON с токеном: считать в
                // нём нечего, полосу занимает сама отправка.
                Response response = await ReadResponseAsync(stream, null, null);
                if (response == null)
                {
                    Log.Write("mem", "заливка: ответа от CDN нет");
                    return null;
                }

                if (response.Status >= 200 && response.Status < 300) return response.Body;

                Log.Write("mem", "заливка: CDN ответил " + response.Status);
                return null;
            }
            catch (Exception error)
            {
                Log.Write("mem", "заливка сорвалась: " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Тело уезжает кусками, а не целиком: иначе о ходе отправки нечего
        /// сообщить — единственный вызов возвращается уже по её завершении, и
        /// полоса стояла бы на нуле до самого конца.
        /// </summary>
        private static async Task<bool> WriteBodyAsync(ITlsStream stream, byte[] body,
                                                       Progress progress)
        {
            int sent = 0;

            while (sent < body.Length)
            {
                int piece = Math.Min(UploadChunk, body.Length - sent);

                var slice = new byte[piece];
                Buffer.BlockCopy(body, sent, slice, 0, piece);

                if (!await stream.WriteAsync(slice)) return false;

                sent += piece;
                if (progress != null) progress(sent, body.Length);
            }

            return true;
        }

        /// <summary>
        /// То же самое, но кусок берётся с диска, а не из памяти. Больше
        /// одного куска разом здесь не живёт никогда — потому и получается
        /// отправлять то, что в память не помещается.
        /// </summary>
        private static async Task<bool> WriteFileAsync(ITlsStream stream,
                                                       Windows.Storage.StorageFile file,
                                                       long total, Progress progress)
        {
            using (var source = await file.OpenReadAsync())
            using (var reader = new Windows.Storage.Streams.DataReader(source))
            {
                long sent = 0;

                while (sent < total)
                {
                    uint piece = (uint)Math.Min(UploadChunk, total - sent);

                    await reader.LoadAsync(piece);

                    uint got = reader.UnconsumedBufferLength;
                    if (got == 0) return false;

                    var slice = new byte[got];
                    reader.ReadBytes(slice);

                    if (!await stream.WriteAsync(slice)) return false;

                    sent += got;
                    if (progress != null) progress(sent, total);
                }
            }

            return true;
        }

        private class Response
        {
            public int Status { get; set; }
            public string Location { get; set; }
            public byte[] Body { get; set; }

            /// <summary>Content-Range — из него берётся полная длина файла.</summary>
            public string Range { get; set; }
        }

        private static async Task<Response> FetchAsync(string url, Progress progress)
        {
            string host;
            int port;
            string path;

            if (!Split(url, out host, out port, out path)) return null;

            ITlsStream stream = NativeTlsStream.IsAvailable
                ? (ITlsStream)new NativeTlsStream()
                : new SystemTlsStream();

            try
            {
                if (!await stream.ConnectAsync(host, port)) return null;

                string request = "GET " + path + " HTTP/1.1\r\n"
                    + "Host: " + host + "\r\n"
                    + "User-Agent: okhttp/4.12.0\r\n"
                    + "Accept: */*\r\n"
                    + "Connection: close\r\n\r\n";

                if (!await stream.WriteAsync(Encoding.UTF8.GetBytes(request))) return null;

                return await ReadResponseAsync(stream, progress, null);
            }
            finally
            {
                stream.Close();
            }
        }

        /// <summary>
        /// Разбор ответа. Если дан приёмник, тело в память не собирается, а
        /// уходит в него по мере прихода — так качается видео.
        /// </summary>
        private static async Task<Response> ReadResponseAsync(
            ITlsStream stream, Progress progress, Windows.Storage.Streams.IOutputStream sink)
        {
            var buffer = new List<byte>();
            var chunk = new byte[4096];

            int headerEnd = -1;

            // Заголовок дочитывается до пустой строки: где кончается он и
            // начинается тело, известно только по ней.
            while (headerEnd < 0)
            {
                int loaded = await stream.ReadAsync(chunk, chunk.Length);
                if (loaded == -2) continue;
                if (loaded <= 0) return null;

                for (int i = 0; i < loaded; i++) buffer.Add(chunk[i]);
                headerEnd = FindHeaderEnd(buffer);

                if (buffer.Count > 64 * 1024) return null;
            }

            byte[] all = buffer.ToArray();
            string header = Encoding.UTF8.GetString(all, 0, headerEnd);

            var response = new Response { Status = ParseStatus(header) };
            response.Location = HeaderValue(header, "location");
            response.Range = HeaderValue(header, "content-range");

            bool chunked = string.Equals(HeaderValue(header, "transfer-encoding"),
                                         "chunked", StringComparison.OrdinalIgnoreCase);

            int length = 0;
            string lengthText = HeaderValue(header, "content-length");
            if (lengthText != null) int.TryParse(lengthText, out length);

            // Кусками да ещё и в файл — не наш случай: видео CDN отдаёт с
            // объявленной длиной. Если вдруг иначе, собираем как раньше.
            bool toSink = sink != null && !chunked;

            var body = new List<byte>();
            long written = 0;

            if (toSink)
            {
                int tail = all.Length - (headerEnd + 4);
                if (tail > 0)
                {
                    var first = new byte[tail];
                    Buffer.BlockCopy(all, headerEnd + 4, first, 0, tail);

                    await sink.WriteAsync(first.AsBuffer());
                    written = tail;
                }
            }
            else
            {
                for (int i = headerEnd + 4; i < all.Length; i++) body.Add(all[i]);
            }

            // Тело: либо известной длины, либо кусками, либо до закрытия
            // соединения — сервер вправе выбрать любой из трёх способов.
            while (true)
            {
                long got = toSink ? written : body.Count;

                if (!chunked && length > 0 && got >= length) break;

                if (got > (toSink ? MaxFile : MaxBody))
                {
                    Log.Write("mem", "загрузка: слишком большое тело, " + got + " байт");
                    return null;
                }

                int loaded = await stream.ReadAsync(chunk, chunk.Length);
                if (loaded == -2) continue;
                if (loaded <= 0) break;

                if (toSink)
                {
                    var piece = new byte[loaded];
                    Buffer.BlockCopy(chunk, 0, piece, 0, loaded);

                    await sink.WriteAsync(piece.AsBuffer());
                    written += loaded;
                }
                else
                {
                    for (int i = 0; i < loaded; i++) body.Add(chunk[i]);
                }

                if (progress != null) progress(toSink ? written : body.Count, length);
            }

            if (toSink)
            {
                await sink.FlushAsync();

                response.Body = null;
                return response;
            }

            response.Body = chunked ? Unchunk(body.ToArray()) : body.ToArray();

            if (!chunked && length > 0 && response.Body.Length > length)
            {
                var trimmed = new byte[length];
                Buffer.BlockCopy(response.Body, 0, trimmed, 0, length);
                response.Body = trimmed;
            }

            return response;
        }

        /// <summary>Куски: длина шестнадцатеричным числом, потом сами данные.</summary>
        private static byte[] Unchunk(byte[] data)
        {
            var result = new List<byte>();
            int offset = 0;

            while (offset < data.Length)
            {
                int lineEnd = IndexOfLine(data, offset);
                if (lineEnd < 0) break;

                string sizeText = Encoding.UTF8.GetString(data, offset, lineEnd - offset).Trim();

                int semicolon = sizeText.IndexOf(';');
                if (semicolon >= 0) sizeText = sizeText.Substring(0, semicolon);

                int size;
                if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture, out size))
                {
                    break;
                }

                if (size == 0) break;

                offset = lineEnd + 2;
                for (int i = 0; i < size && offset + i < data.Length; i++) result.Add(data[offset + i]);

                offset += size + 2;
            }

            return result.ToArray();
        }

        #region Разбор

        private static int FindHeaderEnd(List<byte> buffer)
        {
            for (int i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int IndexOfLine(byte[] data, int offset)
        {
            for (int i = offset; i + 1 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10) return i;
            }
            return -1;
        }

        private static int ParseStatus(string header)
        {
            string[] parts = header.Split(' ');
            if (parts.Length < 2) return 0;

            int status;
            return int.TryParse(parts[1], out status) ? status : 0;
        }

        private static string HeaderValue(string header, string name)
        {
            foreach (string line in header.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                if (!string.Equals(line.Substring(0, colon).Trim(), name,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line.Substring(colon + 1).Trim();
            }

            return null;
        }

        private static bool Split(string url, out string host, out int port, out string path)
        {
            host = null;
            port = 443;
            path = "/";

            if (string.IsNullOrEmpty(url)) return false;

            string rest;
            if (url.StartsWith("https://")) rest = url.Substring(8);
            else if (url.StartsWith("http://")) { rest = url.Substring(7); port = 80; }
            else rest = url;

            // Открытым HTTP картинки не забираем: у CDN он и не отвечает,
            // а отправлять запрос без шифрования незачем.
            if (port == 80) return false;

            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);
            if (slash >= 0) path = rest.Substring(slash);

            int colon = authority.IndexOf(':');
            if (colon > 0)
            {
                int.TryParse(authority.Substring(colon + 1), out port);
                authority = authority.Substring(0, colon);
            }

            host = authority;
            return host.Length > 0;
        }

        private static string Absolute(string baseUrl, string location)
        {
            if (location.StartsWith("http")) return location;

            string host;
            int port;
            string path;

            if (!Split(baseUrl, out host, out port, out path)) return location;

            return "https://" + host + (location.StartsWith("/") ? location : "/" + location);
        }

        #endregion
    }
}
