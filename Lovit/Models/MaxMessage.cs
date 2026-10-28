using System.Collections.Generic;
using System.Text;
using Lovit.Core.Protocol;

namespace Lovit.Models
{
    /// <summary>Сообщение переписки. Порт MAXMessage.</summary>
    public class MaxMessage
    {
        public string MessageId { get; set; }
        public long ChatId { get; set; }
        public long SenderId { get; set; }
        public long Time { get; set; }
        public string Text { get; set; }

        public List<MaxAttachment> Attachments { get; set; }

        /// <summary>Пересланное: у него всё содержимое лежит внутри `link`.</summary>
        public string ForwardedFrom { get; set; }
        public long ForwardedChatId { get; set; }

        /// <summary>
        /// Кто написал пересланное. Названия у источника может не быть вовсе
        /// — пересылают чаще всего из переписки с человеком, а у неё имя
        /// берётся из карточки, — и тогда подпись строится по этому номеру.
        /// </summary>
        public long ForwardedSenderId { get; set; }
        public string ForwardedMessageId { get; set; }
        public long ForwardedTime { get; set; }

        /// <summary>Ответ: строка, которую показывает полоска над текстом.</summary>
        public string ReplyPreview { get; set; }
        public string ReplyMessageId { get; set; }

        /// <summary>Реакции одной строкой: «👍 3 ❤ 1».</summary>
        public string ReactionsSummary { get; set; }

        /// <summary>Чем ответили сами — пусто, если не реагировали.</summary>
        public string YourReaction { get; set; }

        /// <summary>
        /// Разметка текста: ссылки, жирное, наклонное. Сервер шлёт её не
        /// тегами внутри строки, а отдельным списком отрезков.
        /// </summary>
        public IList<object> Elements { get; set; }

        public bool IsEdited { get; set; }

        /// <summary>Пересланное — со ссылкой на источник в `link`.</summary>
        public bool IsForwarded { get; set; }

        public MaxMessage()
        {
            Attachments = new List<MaxAttachment>();
        }

        /// <summary>
        /// Служебное сообщение: кто вошёл, кто вышел, что закрепили, звонок.
        /// Такое рисуется строкой посередине, а не пузырём.
        /// </summary>
        public bool IsService
        {
            get
            {
                MaxAttachment first = FirstShown;
                return first != null && first.IsService;
            }
        }

        /// <summary>Файл, который есть что открыть.</summary>
        public MaxAttachment File
        {
            get
            {
                foreach (MaxAttachment attachment in Attachments)
                {
                    if (attachment.IsFile) return attachment;
                }
                return null;
            }
        }

        /// <summary>Голосовое, музыка или видео — если в сообщении такое есть.</summary>
        public MaxAttachment Playable
        {
            get
            {
                foreach (MaxAttachment attachment in Attachments)
                {
                    if (attachment.IsPlayable) return attachment;
                }
                return null;
            }
        }

        /// <summary>Кнопки под сообщением — если бот их прислал.</summary>
        public MaxAttachment Keyboard
        {
            get
            {
                foreach (MaxAttachment attachment in Attachments)
                {
                    if (attachment.IsInlineKeyboard && attachment.Buttons != null) return attachment;
                }
                return null;
            }
        }

        /// <summary>Стикер: рисуется без пузыря и мельче фотографии.</summary>
        public bool IsSticker
        {
            get
            {
                MaxAttachment first = FirstShown;
                return first != null && first.Type == "STICKER";
            }
        }

        /// <summary>
        /// Первое вложение, которое вообще показывают. Кнопки бота
        /// пропускаются: они рисуются отдельно, под пузырём, а подписью
        /// «Вложение» перекрывали бы настоящее содержимое сообщения.
        /// </summary>
        public MaxAttachment FirstShown
        {
            get
            {
                foreach (MaxAttachment attachment in Attachments)
                {
                    if (!attachment.IsInlineKeyboard) return attachment;
                }
                return null;
            }
        }

        /// <summary>
        /// Что показать в пузыре, когда текста нет: вложение само за себя
        /// не скажет, а пустой пузырь выглядит поломкой.
        /// </summary>
        public string DisplayText
        {
            get
            {
                if (!string.IsNullOrEmpty(Text)) return Text;

                MaxAttachment first = FirstShown;
                if (first != null) return first.Placeholder;

                if (!string.IsNullOrEmpty(ForwardedFrom)) return "Пересланное сообщение";
                return "";
            }
        }

        public static MaxMessage FromMap(IDictionary<string, object> map, long chatId)
        {
            if (map == null) return null;

            var message = new MaxMessage
            {
                ChatId = chatId,
                MessageId = MessagePack.String(map, "id"),
                SenderId = MessagePack.Long(map, "sender"),
                Time = MessagePack.Long(map, "time"),
                Text = MessagePack.String(map, "text"),
                Elements = MessagePack.List(map, "elements"),
                IsEdited = MessagePack.Long(map, "updateTime") > 0
            };

            if (string.IsNullOrEmpty(message.MessageId))
            {
                message.MessageId = MessagePack.Long(map, "id").ToString();
            }

            AddAttachments(message, MessagePack.List(map, "attaches"));

            // Ссылка на другое сообщение бывает двух видов: пересылка и ответ.
            IDictionary<string, object> link = MessagePack.Map(map, "link");
            if (link != null)
            {
                string kind = MessagePack.String(link, "type");
                IDictionary<string, object> inner = MessagePack.Map(link, "message");

                if (kind == "FORWARD")
                {
                    // У пересылки своих полей нет вовсе: и текст, и вложения
                    // лежат в link.message. Ровно на этом в версии для iOS
                    // пересланные фотографии рисовались пустым пузырём.
                    if (inner != null)
                    {
                        if (string.IsNullOrEmpty(message.Text))
                        {
                            // Разметка идёт вместе со своим текстом: отрезки
                            // отсчитываются от него, и взять её от чужого
                            // значило бы раскрасить не те буквы.
                            message.Text = MessagePack.String(inner, "text");
                            message.Elements = MessagePack.List(inner, "elements");
                        }
                        AddAttachments(message, MessagePack.List(inner, "attaches"));

                        message.ForwardedMessageId = MessagePack.String(inner, "id");
                        message.ForwardedTime = MessagePack.Long(inner, "time");
                        message.ForwardedSenderId = MessagePack.Long(inner, "sender");
                    }

                    message.ForwardedFrom = MessagePack.String(link, "chatName");
                    message.ForwardedChatId = MessagePack.Long(link, "chatId");

                    // Признак пересылки — сама ссылка, а не название
                    // источника: у переписки с человеком названия нет, и по
                    // нему пересылка выглядела бы обычным сообщением.
                    message.IsForwarded = true;
                }
                else if (kind == "REPLY")
                {
                    message.ReplyPreview = PreviewText(inner) ?? "Сообщение";
                    message.ReplyMessageId = MessagePack.String(inner, "id");
                }
            }

            message.ApplyReactions(MessagePack.Map(map, "reactionInfo"));

            return message;
        }

        /// <summary>
        /// Однострочное превью — для списка чатов, закрепа и ответов.
        /// Вложение называется словом, а у пересылки берётся её содержимое.
        /// </summary>
        public static string PreviewText(IDictionary<string, object> map)
        {
            if (map == null) return null;

            string text = MessagePack.String(map, "text");
            if (!string.IsNullOrEmpty(text)) return text;

            string label = AttachmentLabel(MessagePack.List(map, "attaches"));
            if (label != null) return label;

            IDictionary<string, object> link = MessagePack.Map(map, "link");
            if (link != null && MessagePack.String(link, "type") == "FORWARD")
            {
                IDictionary<string, object> inner = MessagePack.Map(link, "message");
                string forwarded = MessagePack.String(inner, "text");
                if (!string.IsNullOrEmpty(forwarded)) return forwarded;

                return AttachmentLabel(MessagePack.List(inner, "attaches")) ?? "Пересланное сообщение";
            }

            return null;
        }

        /// <summary>
        /// Подпись по первому показываемому вложению. Разметка кнопок бота
        /// пропускается: у сообщения с кнопками первым в списке лежит именно
        /// она, и превью такого сообщения выходило словом «Вложение» вместо
        /// содержимого.
        /// </summary>
        private static string AttachmentLabel(IList<object> list)
        {
            if (list == null) return null;

            foreach (object item in list)
            {
                MaxAttachment attachment = MaxAttachment.FromMap(item as IDictionary<string, object>);
                if (attachment == null || attachment.IsInlineKeyboard) continue;

                return attachment.Placeholder;
            }

            return null;
        }

        /// <summary>
        /// Принять реакции такими, как их прислал сервер в ответ на запрос:
        /// там и своя реакция, и общие счётчики.
        /// </summary>
        public void ApplyReactions(IDictionary<string, object> info)
        {
            ReactionsSummary = Reactions(MessagePack.List(info, "counters"));
            YourReaction = MessagePack.String(info, "yourReaction");
        }

        /// <summary>
        /// То же по пушу об изменении реакций. Своей реакции в нём нет —
        /// приходит он и на чужие тоже, — поэтому она остаётся прежней.
        /// Опустевшие счётчики означают, что сняли последнюю: тогда и своей
        /// больше нет.
        /// </summary>
        public void ApplyCounters(IList<object> counters)
        {
            ReactionsSummary = Reactions(counters);
            if (string.IsNullOrEmpty(ReactionsSummary)) YourReaction = null;
        }

        /// <summary>
        /// Реакции приходят списком пар «эмодзи, счётчик». Отдельной панели
        /// с лицами в порте нет — одна строка.
        /// </summary>
        private static string Reactions(IList<object> counters)
        {
            if (counters == null || counters.Count == 0) return null;

            var line = new StringBuilder();
            foreach (object item in counters)
            {
                var counter = item as IDictionary<string, object>;
                if (counter == null) continue;

                string emoji = MessagePack.String(counter, "reaction");
                long count = MessagePack.Long(counter, "count");
                if (string.IsNullOrEmpty(emoji)) continue;

                if (line.Length > 0) line.Append("  ");
                line.Append(emoji);
                if (count > 1) line.Append(" ").Append(count);
            }

            return line.Length > 0 ? line.ToString() : null;
        }

        private static void AddAttachments(MaxMessage message, IList<object> list)
        {
            if (list == null) return;

            foreach (object item in list)
            {
                MaxAttachment attachment = MaxAttachment.FromMap(item as IDictionary<string, object>);
                if (attachment != null) message.Attachments.Add(attachment);
            }
        }
    }
}
