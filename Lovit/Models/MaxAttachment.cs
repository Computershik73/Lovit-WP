using System;
using System.Collections.Generic;
using Lovit.Core.Protocol;

namespace Lovit.Models
{
    /// <summary>Вложение сообщения. Порт MAXAttachment.</summary>
    public class MaxAttachment
    {
        public string Type { get; set; }
        public string Url { get; set; }
        public string PreviewUrl { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }

        /// <summary>
        /// Токен CDN. У каждого типа он зовётся по-своему, и общего `token`
        /// может не быть вовсе: файл присылают с `fileToken`.
        /// </summary>
        public string Token { get; set; }

        /// <summary>Номер файла — с ним берётся ссылка на скачивание.</summary>
        public long FileId { get; set; }

        /// <summary>Размеры картинки: под неё заранее отводится место.</summary>
        public long Width { get; set; }
        public long Height { get; set; }

        /// <summary>Служебное сообщение: что именно произошло в чате.</summary>
        public string Event { get; set; }
        public string Title { get; set; }
        public string ShortMessage { get; set; }

        /// <summary>Звонок: чем был и чем кончился.</summary>
        public string CallType { get; set; }
        public string HangupType { get; set; }
        public long Duration { get; set; }
        public bool HasJoinLink { get; set; }

        /// <summary>Кружок, а не обычное видео.</summary>
        public long VideoType { get; set; }

        /// <summary>
        /// Кнопки под сообщением бота: ряды кнопок. Пусто у всего, кроме
        /// INLINE_KEYBOARD.
        /// </summary>
        public IList<IList<InlineButton>> Buttons { get; set; }

        /// <summary>С чем возвращаться боту при нажатии кнопки.</summary>
        public string CallbackId { get; set; }

        public bool IsImage
        {
            get { return Type == "PHOTO" || Type == "STICKER"; }
        }

        /// <summary>Служебное сообщение рисуется не пузырём, а строкой посередине.</summary>
        public bool IsService
        {
            get { return Type == "CONTROL" || Type == "CALL"; }
        }

        /// <summary>Открывать нечего, и рисовать тоже: это разметка кнопок бота.</summary>
        public bool IsInlineKeyboard
        {
            get { return Type == "INLINE_KEYBOARD"; }
        }

        /// <summary>Голосовое, музыка или видео — то, что можно проиграть.</summary>
        public bool IsPlayable
        {
            get { return Type == "AUDIO" || Type == "VIDEO"; }
        }

        public bool IsVideo { get { return Type == "VIDEO"; } }

        /// <summary>Кадр из ролика — его показывают вместо пустой плашки.</summary>
        public string Thumbnail { get; set; }

        /// <summary>Описание движения — у движущихся стикеров.</summary>
        public string LottieUrl { get; set; }

        /// <summary>
        /// Тот же кадр, но вложенный прямо в сообщение байтами, а не адресом.
        /// Сервер шлёт его, когда отдельной картинки нет; внутри WebP.
        /// </summary>
        public byte[] PreviewBytes { get; set; }

        /// <summary>
        /// График громкости голосового — по столбику на отсчёт. Сервер шлёт
        /// его в поле `wave`.
        /// </summary>
        public byte[] Wave { get; set; }

        /// <summary>
        /// Длительность словами.
        ///
        /// Сервер шлёт её тысячными долями секунды — всегда, а не иногда.
        /// Раньше здесь стояла догадка «больше часа — значит, доли», и
        /// трёхсекундное голосовое подписывалось как сорокасемиминутное:
        /// три тысячи с лишним долей принимались за столько же секунд.
        /// </summary>
        public string DurationText
        {
            get
            {
                if (Duration <= 0) return "";

                long seconds = Duration / 1000;
                return (seconds / 60) + ":" + (seconds % 60).ToString("00");
            }
        }

        /// <summary>Файл, который можно скачать и открыть.</summary>
        public bool IsFile
        {
            get { return Type == "FILE" && (FileId != 0 || !string.IsNullOrEmpty(Url)); }
        }

        /// <summary>
        /// Чем подписать вложение, когда показать его нечем. Порт
        /// MAXAttachmentPreviewLabel.
        /// </summary>
        public string Placeholder
        {
            get
            {
                switch (Type)
                {
                    case "PHOTO": return "Фото";
                    case "VIDEO": return VideoType == 1 ? "Видео-сообщение" : "Видео";
                    case "AUDIO": return "Голосовое сообщение";
                    case "STICKER": return "Стикер";
                    case "LOCATION": return "Геопозиция";
                    case "CONTACT": return "Контакт";

                    case "FILE":
                        return string.IsNullOrEmpty(Name) ? "Файл" : "Файл: " + Name;

                    case "SHARE":
                        return string.IsNullOrEmpty(Title) ? "Ссылка" : "Ссылка: " + Title;

                    case "POLL":
                        return string.IsNullOrEmpty(Title) ? "Опрос" : "Опрос: " + Title;

                    case "CONTROL": return ControlLabel;
                    case "CALL": return CallLabel;

                    default: return "Вложение";
                }
            }
        }

        /// <summary>
        /// Что произошло в чате. Сервер часто присылает готовую строку —
        /// тогда берётся она: там и имя того, кто вошёл, и падежи.
        /// </summary>
        private string ControlLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(Title)) return Title;
                if (!string.IsNullOrEmpty(ShortMessage)) return ShortMessage;

                switch (Event)
                {
                    case "new": return "Чат создан";
                    case "add":
                    case "joinByLink": return "Участник добавлен";
                    case "remove":
                    case "leave": return "Участник вышел";
                    case "title": return "Название изменено";
                    case "icon": return "Аватар изменён";
                    case "pin": return "Сообщение закреплено";
                    default: return "Системное сообщение";
                }
            }
        }

        /// <summary>
        /// Звонок. Нулевая длительность — это не «звонок на ноль секунд», а
        /// несостоявшийся: сброшенный, отклонённый или пропущенный.
        /// </summary>
        private string CallLabel
        {
            get
            {
                bool video = (CallType ?? "").ToUpper() == "VIDEO";

                if (HasJoinLink) return video ? "Групповой видеозвонок" : "Групповой звонок";

                bool failed = Duration == 0
                              || HangupType == "CANCELED"
                              || HangupType == "REJECTED"
                              || HangupType == "MISSED";

                if (failed) return video ? "Пропущенный видеозвонок" : "Пропущенный звонок";

                return video ? "Видеозвонок" : "Звонок";
            }
        }

        /// <summary>Размер человеческими словами — подписью под именем файла.</summary>
        public string SizeText
        {
            get
            {
                if (Size <= 0) return "";
                if (Size < 1024) return Size + " Б";
                if (Size < 1024 * 1024) return (Size / 1024) + " КБ";

                return (Size / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
            }
        }

        /// <summary>
        /// Какой высоты показывать картинку. Размеры приходят не всегда;
        /// без них берётся 4:3, а очень длинные подрезаются, чтобы одна не
        /// занимала весь экран.
        /// </summary>
        public double DisplayHeightFor(double width)
        {
            if (Width <= 0 || Height <= 0) return width * 0.75;

            double height = width * ((double)Height / Width);
            double limit = width * 1.4;

            return height < limit ? height : limit;
        }

        public static MaxAttachment FromMap(IDictionary<string, object> map)
        {
            if (map == null) return null;

            Describe(map);

            // Имена полей у каждого типа свои, общего `url` может не быть
            // вовсе: у фотографии это baseUrl, у файла — url.
            return new MaxAttachment
            {
                Type = (MessagePack.String(map, "_type") ?? "OTHER").ToUpper(),

                Url = MessagePack.String(map, "baseUrl")
                      ?? MessagePack.String(map, "url"),

                PreviewUrl = MessagePack.String(map, "previewUrl")
                             ?? MessagePack.String(map, "url")
                             ?? MessagePack.String(map, "baseUrl"),

                // Кадр для показа до проигрывания. У видео он приходит
                // отдельным полем, а если его нет — им служит baseUrl:
                // у ролика это не сам ролик, а его картинка.
                Thumbnail = MessagePack.String(map, "thumbnail")
                            ?? MessagePack.String(map, "baseUrl"),

                LottieUrl = MessagePack.String(map, "lottieUrl"),

                PreviewBytes = Bytes(map, "previewData", true),

                // Имя поля у графика громкости мы знаем из Komet, но сервер
                // мог назвать его и иначе — пробуем известные написания.
                Wave = Bytes(map, "wave", false)
                       ?? Bytes(map, "waveform", false)
                       ?? Bytes(map, "waves", false),

                Token = MessagePack.String(map, "photoToken")
                        ?? MessagePack.String(map, "fileToken")
                        ?? MessagePack.String(map, "videoToken")
                        ?? MessagePack.String(map, "token"),

                // Номер вложения у каждого вида свой: у файла fileId, у видео
                // videoId, у голосового audioId. Пока читался только первый,
                // видео оставалось без ссылки вовсе — «сервер не дал ссылки».
                FileId = First(MessagePack.Long(map, "fileId"),
                               MessagePack.Long(map, "videoId"),
                               MessagePack.Long(map, "audioId")),

                // Чужие клиенты присылают русские имена испорченными — чиним
                // здесь, при разборе: отсюда имя идёт и в переписку, и в
                // строку списка чатов («Файл: …»).
                Name = BrokenNames.Repair(MessagePack.String(map, "name")),
                Size = MessagePack.Long(map, "size"),
                Width = MessagePack.Long(map, "width"),
                Height = MessagePack.Long(map, "height"),

                Event = MessagePack.String(map, "event"),
                Title = MessagePack.String(map, "title"),
                ShortMessage = MessagePack.String(map, "shortMessage"),

                CallType = MessagePack.String(map, "callType"),
                HangupType = MessagePack.String(map, "hangupType"),
                Duration = MessagePack.Long(map, "duration"),
                HasJoinLink = map.ContainsKey("joinLink") && map["joinLink"] != null,

                VideoType = MessagePack.Long(map, "videoType"),

                CallbackId = MessagePack.String(map, "callbackId"),
                Buttons = ParseButtons(map)
            };
        }

        /// <summary>
        /// Вложенный кадр. Приходит по-разному: готовыми байтами, списком
        /// чисел или строкой — и строка бывает как адресом с приставкой
        /// «data:», так и просто base64. Порт decodeAttachPreview из Komet.
        /// </summary>
        /// <summary>
        /// Один раз перечислить в журнале, из чего состоит голосовое. Имена
        /// полей взяты из Komet, но сервер мог их и переименовать — а по
        /// пустой плашке этого не увидеть.
        /// </summary>
        private static bool _described;

        private static void Describe(IDictionary<string, object> map)
        {
            if (_described) return;

            string type = MessagePack.String(map, "_type");
            if (type != "AUDIO") return;

            _described = true;

            var names = new System.Text.StringBuilder();
            foreach (string key in map.Keys)
            {
                object value = map[key];

                names.Append(key).Append('=')
                     .Append(value == null ? "пусто" : value.GetType().Name)
                     .Append(' ');
            }

            Lovit.Core.Log.Write("chat", "голосовое, поля: " + names);
        }

        private static byte[] Bytes(IDictionary<string, object> map, string key, bool base64)
        {
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return null;

            var bytes = raw as byte[];
            if (bytes != null) return bytes.Length > 0 ? bytes : null;

            var list = raw as IList<object>;
            if (list != null)
            {
                var made = new byte[list.Count];

                for (int i = 0; i < list.Count; i++)
                {
                    made[i] = (byte)(ToLong(list[i]) & 0xFF);
                }

                return made.Length > 0 ? made : null;
            }

            var text = raw as string;
            if (string.IsNullOrEmpty(text)) return null;

            if (!base64)
            {
                // График приходит строкой, где сама буква и есть высота
                // столбика, а не записью в base64.
                var codes = new byte[text.Length];
                for (int i = 0; i < text.Length; i++) codes[i] = (byte)(text[i] & 0xFF);

                return codes;
            }

            // «data:image/webp;base64,…» — берём то, что после запятой.
            int comma = text.IndexOf(',');
            if (text.StartsWith("data:", StringComparison.Ordinal) && comma > 0)
            {
                text = text.Substring(comma + 1);
            }

            try { return Convert.FromBase64String(text); }
            catch (FormatException) { return null; }
        }

        private static long ToLong(object value)
        {
            if (value is long) return (long)value;

            long parsed;
            return value != null && long.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }

        /// <summary>Первое ненулевое из перечисленного.</summary>
        private static long First(params long[] values)
        {
            foreach (long value in values)
            {
                if (value != 0) return value;
            }
            return 0;
        }

        /// <summary>
        /// Кнопки под сообщением бота. Лежат они внутри `keyboard.buttons` и
        /// приходят рядами: внешний список — строки, внутренний — кнопки в
        /// строке. Порт InlineKeyboardAttachment.fromMap из Komet.
        /// </summary>
        private static IList<IList<InlineButton>> ParseButtons(IDictionary<string, object> map)
        {
            IDictionary<string, object> keyboard = MessagePack.Map(map, "keyboard");
            if (keyboard == null) return null;

            IList<object> rows = MessagePack.List(keyboard, "buttons");
            if (rows == null) return null;

            var parsed = new List<IList<InlineButton>>();

            foreach (object rawRow in rows)
            {
                var row = rawRow as IList<object>;
                if (row == null) continue;

                var line = new List<InlineButton>();

                foreach (object rawButton in row)
                {
                    var button = rawButton as IDictionary<string, object>;
                    if (button == null) continue;

                    string text = MessagePack.String(button, "text");
                    if (string.IsNullOrEmpty(text)) continue;

                    line.Add(new InlineButton
                    {
                        Type = (MessagePack.String(button, "type") ?? "").ToUpper(),
                        Text = text,
                        Url = MessagePack.String(button, "url"),
                        Payload = MessagePack.String(button, "payload")
                    });
                }

                if (line.Count != 0) parsed.Add(line);
            }

            return parsed.Count == 0 ? null : parsed;
        }
    }

    /// <summary>Одна кнопка под сообщением бота.</summary>
    public class InlineButton
    {
        /// <summary>CALLBACK, LINK, а бывает и что-то ещё.</summary>
        public string Type { get; set; }
        public string Text { get; set; }
        public string Url { get; set; }
        public string Payload { get; set; }

        /// <summary>
        /// Чьи это кнопки. Проставляется при сборке строки списка: сама
        /// кнопка про своё сообщение из ответа сервера не знает, а нажатие
        /// без него боту не отправить.
        /// </summary>
        public string MessageId { get; set; }
        public string CallbackId { get; set; }

        /// <summary>Ссылка открывается браузером, остальное уходит боту.</summary>
        public bool IsLink
        {
            get { return Type == "LINK" && !string.IsNullOrEmpty(Url); }
        }
    }
}
