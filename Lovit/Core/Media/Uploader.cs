using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Protocol;
using Windows.Storage;

namespace Lovit.Core.Media
{
    /// <summary>Что получилось из заливки — этим потом описывается вложение.</summary>
    public class UploadResult
    {
        /// <summary>Токен CDN: им сервер узнаёт залитое.</summary>
        public string Token { get; set; }

        /// <summary>Номер файла; сервер принимает либо его, либо токен.</summary>
        public long FileId { get; set; }

        public string Error { get; set; }

        public bool Ok
        {
            get { return string.IsNullOrEmpty(Error); }
        }
    }

    /// <summary>
    /// Заливка вложений. Порт MAXUploader.
    ///
    /// Дорога у всех вложений одна и в два шага: протоколом спрашивается
    /// адрес на CDN, а тело уезжает обычным POST мимо протокола. А вот
    /// способ отправки тела у фотографии и файла разный, и перепутать их
    /// нельзя — сервер молча не примет.
    /// </summary>
    public static class Uploader
    {
        /// <summary>
        /// Фотография уходит формой multipart с полем «file», и в ответ
        /// приезжает JSON с токеном.
        /// </summary>
        public static Task<UploadResult> UploadPhotoAsync(byte[] data, string fileName)
        {
            return UploadPhotoAsync(data, fileName, null);
        }

        public static async Task<UploadResult> UploadPhotoAsync(byte[] data, string fileName,
                                                                MediaHttp.Progress progress)
        {
            LastError = null;

            if (data == null || data.Length == 0) return Fail("Пустой файл");

            Packet packet = await MaxApi.Instance.RequestAsync(
                Opcodes.PhotoUpload, new Dictionary<string, object> { { "count", 1L } });

            if (packet == null || packet.IsError) return Fail("Сервер не дал адрес для загрузки");

            string url = MessagePack.String(packet.Map, "url");
            if (string.IsNullOrEmpty(url)) return Fail("Сервер не дал адрес для загрузки");

            string name = string.IsNullOrEmpty(fileName) ? "photo.jpg" : fileName;

            byte[] answer = await PostFormAsync(url, data, name, MimeType(name), progress);
            if (answer == null) return Fail("CDN не принял фотографию");

            string body = Encoding.UTF8.GetString(answer, 0, answer.Length);
            string token = FindToken(body);

            // Ответ CDN — в журнал, но обрезанный и без самого токена.
            // Сервер отвергает вложение молча, одним «Invalid attachment», и
            // без этой строки не отличить «токен не тот» от «токена не нашли
            // в ответе непонятного вида».
            Log.Write("mem", "CDN ответил " + body.Length + " знаками, токен "
                             + (string.IsNullOrEmpty(token) ? "не найден" : "найден")
                             + ": " + Shorten(body));

            if (string.IsNullOrEmpty(token)) return Fail("CDN не вернул токен фото");

            return new UploadResult { Token = token };
        }

        /// <summary>
        /// Файл уходит сырым телом, а не формой: один POST с Content-Range
        /// на весь объём и именем в Content-Disposition. Формой — той же,
        /// что у фотографии, — сервер файл не принимает.
        /// </summary>
        public static Task<UploadResult> UploadFileAsync(byte[] data, string fileName)
        {
            return UploadFileAsync(data, fileName, null);
        }

        public static Task<UploadResult> UploadFileAsync(byte[] data, string fileName,
                                                         MediaHttp.Progress progress)
        {
            if (data == null || data.Length == 0) return Task.FromResult(Fail("Пустой файл"));

            return UploadFileAsync(null, data, data.Length, fileName, progress);
        }

        /// <summary>
        /// То же, но не читая файл в память. Ролик на полтораста мегабайт
        /// прочитать целиком телефон не в силах — раньше приложение на этом
        /// просто исчезало.
        /// </summary>
        public static Task<UploadResult> UploadFileAsync(StorageFile file, long size,
                                                         string fileName,
                                                         MediaHttp.Progress progress)
        {
            if (file == null || size <= 0) return Task.FromResult(Fail("Пустой файл"));

            return UploadFileAsync(file, null, size, fileName, progress);
        }

        private static async Task<UploadResult> UploadFileAsync(StorageFile file, byte[] data,
                                                                long size, string fileName,
                                                                MediaHttp.Progress progress)
        {
            LastError = null;

            Packet packet = await MaxApi.Instance.RequestAsync(
                Opcodes.FileUpload, new Dictionary<string, object> { { "count", 1L } });

            if (packet == null || packet.IsError) return Fail("Сервер не дал адрес для загрузки");

            // Адрес файла лежит не в корне ответа, а первым в списке `info`.
            IList<object> list = MessagePack.List(packet.Map, "info");
            IDictionary<string, object> info = list != null && list.Count > 0
                ? list[0] as IDictionary<string, object>
                : null;

            string url = MessagePack.String(info, "url");
            if (string.IsNullOrEmpty(url)) return Fail("Сервер не дал адрес для загрузки");

            string name = string.IsNullOrEmpty(fileName) ? "file.bin" : fileName;

            byte[] answer = file != null
                ? await PostRawFileAsync(url, file, size, name, progress)
                : await PostRawAsync(url, data, name, progress);

            if (answer == null) return Fail("CDN не принял файл");

            return new UploadResult
            {
                Token = MessagePack.String(info, "token"),
                FileId = MessagePack.Long(info, "fileId")
            };
        }

        /// <summary>
        /// Голосовое и кружок. Адрес берётся тем же запросом, что у видео, а
        /// что именно грузится, сервер узнаёт по паре чисел: голосовое — это
        /// тип 2, кружок — тип 1.
        ///
        /// Тело уходит так же, как у файла, — сырым POST. Имя ему даётся
        /// числом: своего имени у записанного нет, а без имени CDN тело не
        /// принимает.
        /// </summary>
        public static async Task<UploadResult> UploadMediaAsync(byte[] data, int kind,
                                                                MediaHttp.Progress progress)
        {
            LastError = null;

            if (data == null || data.Length == 0) return Fail("Пустая запись");

            var request = new Dictionary<string, object>
            {
                { "uploaderType", 1L },
                { "type", (long)kind },
                { "count", 1L }
            };

            Packet packet = await MaxApi.Instance.RequestAsync(Opcodes.VideoUpload, request);
            if (packet == null || packet.IsError) return Fail("Сервер не дал адрес для загрузки");

            IList<object> list = MessagePack.List(packet.Map, "info");
            IDictionary<string, object> info = list != null && list.Count > 0
                ? list[0] as IDictionary<string, object>
                : null;

            string url = MessagePack.String(info, "url");
            if (string.IsNullOrEmpty(url)) return Fail("Сервер не дал адрес для загрузки");

            string name = (DateTime.UtcNow.Ticks & 0x7FFFFFFF).ToString();

            // Тип части здесь другой, чем у файла: голосовое и кружок уходят
            // просто двоичным потоком. Назвать её «video/mp4» было бы
            // естественно, но на такую CDN отвечает «BAD_REQUEST» — проверено.
            byte[] answer = await MediaHttp.PostAsync(
                url, data, RawHeaders(name, data.Length, "application/octet-stream"), progress);

            if (answer == null) return Fail("CDN не принял запись");

            // Ответ CDN — в журнал целиком, насколько он короток. Сервер потом
            // отвечает одним «вложение в обработке», и без этой строки не
            // отличить «CDN отверг тело» от «CDN не успел его обработать».
            string said = Encoding.UTF8.GetString(answer, 0, answer.Length);
            Log.Write("mem", "CDN о записи (" + answer.Length + " б): " + Shorten(said));

            if (said.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Fail("CDN отверг запись: " + Shorten(said));
            }

            string token = MessagePack.String(info, "token");
            long videoId = MessagePack.Long(info, "videoId");

            if (string.IsNullOrEmpty(token) && videoId == 0)
            {
                return Fail("Сервер не вернул токен записи");
            }

            return new UploadResult { Token = token, FileId = videoId };
        }

        #region Отправка тела

        private static async Task<byte[]> PostRawAsync(string url, byte[] data, string fileName,
                                                       MediaHttp.Progress progress)
        {
            return await MediaHttp.PostAsync(url, data,
                                             RawHeaders(fileName, data.Length, null), progress);
        }

        private static async Task<byte[]> PostRawFileAsync(string url, StorageFile file, long size,
                                                           string fileName,
                                                           MediaHttp.Progress progress)
        {
            return await MediaHttp.PostFileAsync(url, file, size,
                                                 RawHeaders(fileName, size, null), progress);
        }

        private static Dictionary<string, string> RawHeaders(string fileName, long size,
                                                             string contentType)
        {
            long last = size > 0 ? size - 1 : 0;

            return new Dictionary<string, string>
            {
                { "User-Agent", MaxApi.Instance.HttpUserAgent },
                { "Content-Type", contentType ?? "application/x-binary; charset=x-user-defined" },
                { "Content-Disposition", "attachment; filename=" + Escape(fileName) },
                { "Content-Range", "bytes 0-" + last + "/" + size }
            };
        }

        /// <summary>
        /// Имя файла для заголовка — знаками из первой сотни таблицы.
        ///
        /// Имя чата и подпись едут в теле запроса, а вот имя файла сервер
        /// берёт только отсюда, из заголовка. Заголовки же по своему
        /// устройству однобайтовые: русское имя, отправленное как есть,
        /// сервер прочитывал побайтно и сохранял кракозябрами — «СВО» на той
        /// стороне становилось «Ð¡ÐÐ».
        ///
        /// Записываем его так же, как это делает любой браузер: каждый байт
        /// сверх первой сотни — знаком процента и двумя цифрами.
        /// </summary>
        private static string Escape(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "file.bin";

            byte[] utf8 = Encoding.UTF8.GetBytes(fileName);
            var made = new StringBuilder(utf8.Length);

            foreach (byte value in utf8)
            {
                bool plain = (value >= 'a' && value <= 'z')
                             || (value >= 'A' && value <= 'Z')
                             || (value >= '0' && value <= '9')
                             || value == '.' || value == '-' || value == '_' || value == '~';

                if (plain) made.Append((char)value);
                else made.Append('%').Append(value.ToString("X2"));
            }

            return made.ToString();
        }

        private static async Task<byte[]> PostFormAsync(string url, byte[] data,
                                                        string fileName, string mimeType,
                                                        MediaHttp.Progress progress)
        {
            string boundary = "----MaxBoundary" + Guid.NewGuid().ToString("N");

            byte[] head = Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\n"
                + "Content-Disposition: form-data; name=\"file\"; filename=\"" + fileName + "\"\r\n"
                + "Content-Type: " + mimeType + "\r\n\r\n");

            byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            var body = new byte[head.Length + data.Length + tail.Length];
            Buffer.BlockCopy(head, 0, body, 0, head.Length);
            Buffer.BlockCopy(data, 0, body, head.Length, data.Length);
            Buffer.BlockCopy(tail, 0, body, head.Length + data.Length, tail.Length);

            var headers = new Dictionary<string, string>
            {
                { "User-Agent", MaxApi.Instance.HttpUserAgent },
                { "Content-Type", "multipart/form-data; boundary=" + boundary }
            };

            return await MediaHttp.PostAsync(url, body, headers, progress);
        }

        #endregion

        #region Разбор ответа

        /// <summary>
        /// Первое значение при ключе token или photoToken на любой глубине.
        ///
        /// Разбирать ответ полноценным чтением JSON здесь не за чем: нужно
        /// ровно одно строковое поле, а ни списков, ни вложенности имён у
        /// него не бывает.
        /// </summary>
        private static string FindToken(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            // Порядок как в Komet (_parsePhotoToken): сперва токен внутри
            // `photos`, и только потом `photoToken` в корне. Настоящий ответ
            // CDN — это `{"photos":{"<номер>":{"token":"…"}}}`, и корневого
            // `photoToken` в нём обычно нет вовсе.
            int photos = json.IndexOf("\"photos\"", StringComparison.Ordinal);
            if (photos >= 0)
            {
                string nested = ValueAfter(json.Substring(photos), "\"token\"");
                if (!string.IsNullOrEmpty(nested)) return nested;
            }

            foreach (string key in new[] { "photoToken", "token" })
            {
                string value = ValueAfter(json, "\"" + key + "\"");
                if (!string.IsNullOrEmpty(value)) return value;
            }

            return null;
        }

        /// <summary>Начало ответа для журнала — без хвоста, где лежит токен.</summary>
        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "пусто";

            string line = text.Replace('\n', ' ').Replace('\r', ' ');
            return line.Length <= 120 ? line : line.Substring(0, 120) + "…";
        }

        private static string ValueAfter(string json, string key)
        {
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + key.Length);
            if (colon < 0) return null;

            int open = json.IndexOf('"', colon + 1);
            if (open < 0) return null;

            int close = json.IndexOf('"', open + 1);
            if (close < 0) return null;

            return Unescape(json.Substring(open + 1, close - open - 1));
        }

        /// <summary>
        /// Развернуть экранирование JSON. Без этого токен уезжает испорченным.
        ///
        /// Токен — это base64, и хвост у него из знаков равенства. CDN
        /// записывает их как `=`, и мы отдавали серверу строку с этими
        /// шестью знаками вместо одного. Сервер отвечал «Invalid attachment»
        /// — совершенно справедливо: такого токена он не выдавал.
        /// </summary>
        private static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text;

            var result = new StringBuilder(text.Length);

            for (int at = 0; at < text.Length; at++)
            {
                if (text[at] != '\\' || at + 1 >= text.Length)
                {
                    result.Append(text[at]);
                    continue;
                }

                char kind = text[++at];

                switch (kind)
                {
                    case 'u':
                        if (at + 4 < text.Length)
                        {
                            int code = Convert.ToInt32(text.Substring(at + 1, 4), 16);
                            result.Append((char)code);
                            at += 4;
                        }
                        break;

                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;

                    // Обратная косая, кавычка и косая — сами собой.
                    default: result.Append(kind); break;
                }
            }

            return result.ToString();
        }

        #endregion

        /// <summary>Тип части по расширению — как content_type_for_filename в ядре.</summary>
        private static string MimeType(string fileName)
        {
            string name = fileName ?? "";

            int dot = name.LastIndexOf('.');
            string extension = dot >= 0 ? name.Substring(dot + 1).ToLower() : "";

            switch (extension)
            {
                case "jpg":
                case "jpeg": return "image/jpeg";
                case "png": return "image/png";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                case "mp4": return "video/mp4";
                case "pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }

        /// <summary>
        /// Почему сорвалась последняя заливка. Раньше причина оставалась
        /// только в логе, а человек видел одно «не удалось отправить» на все
        /// случаи — от пустого файла до молчащего CDN.
        /// </summary>
        public static string LastError { get; private set; }

        private static UploadResult Fail(string error)
        {
            LastError = error;
            Log.Write("mem", "заливка не удалась: " + error);

            return new UploadResult { Error = error };
        }
    }
}
