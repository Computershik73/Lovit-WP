using System;
using System.Collections.Generic;
using Lovit.Core.Protocol;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Lovit.Ui
{
    /// <summary>
    /// Разметка внутри сообщения: ссылки, жирное, наклонное и прочее.
    /// Порт lib/core/utils/text_format.dart из Komet.
    ///
    /// Сервер шлёт её не тегами внутри текста, а отдельным списком отрезков
    /// в `elements`: у каждого вид, начало и длина. Отрезки накладываются
    /// друг на друга — жирное внутри ссылки обычное дело, — поэтому текст
    /// сперва режется по всем их границам, и у каждого куска выходит свой
    /// набор признаков.
    /// </summary>
    public class TextRange
    {
        public string Type { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }

        /// <summary>Адрес у ссылки; у остального пусто.</summary>
        public string Url { get; set; }

        public int End { get { return Start + Length; } }

        public static List<TextRange> Parse(IList<object> elements)
        {
            var result = new List<TextRange>();
            if (elements == null) return result;

            foreach (object item in elements)
            {
                var map = item as IDictionary<string, object>;
                if (map == null) continue;

                string type = MessagePack.String(map, "type");
                int length = (int)MessagePack.Long(map, "length");
                if (string.IsNullOrEmpty(type) || length <= 0) continue;

                var range = new TextRange
                {
                    Type = type,
                    Start = (int)MessagePack.Long(map, "from"),
                    Length = length
                };

                IDictionary<string, object> attributes = MessagePack.Map(map, "attributes");
                range.Url = MessagePack.String(attributes, "url");

                result.Add(range);
            }

            return result;
        }
    }

    /// <summary>
    /// Сборка разметки для отправки: обратная сторона [TextRange.Parse].
    ///
    /// Отрезки хранятся вместе с куском текста, который они покрывают.
    /// Живого пересчёта при правке нет нарочно: слежение за границами при
    /// каждом нажатии клавиши — это отдельный редактор, а ошибётся оно так,
    /// что жирным окажется не то слово. Вместо этого перед отправкой каждый
    /// отрезок сверяется со своим куском и отбрасывается, если тот уехал.
    /// </summary>
    public static class Markup
    {
        /// <summary>Разметка, наложенная на выделение в поле ввода.</summary>
        public class Applied
        {
            public string Type { get; set; }
            public int Start { get; set; }
            public string Covered { get; set; }
        }

        /// <summary>
        /// Ссылки в наборанном тексте. Komet их сам не ищет — там разметку
        /// накладывают руками, — но без этого набранная ссылка уходит
        /// простым текстом и у собеседника ссылкой не становится.
        ///
        /// Ищется только со схемой или с «www»: «сходи в ок.ру» ссылкой не
        /// является, и принимать за неё любое слово с точкой нельзя.
        /// </summary>
        public static List<TextRange> DetectLinks(string text)
        {
            var found = new List<TextRange>();
            if (string.IsNullOrEmpty(text)) return found;

            int at = 0;

            while (at < text.Length)
            {
                int start = NextLinkStart(text, at);
                if (start < 0) break;

                int end = start;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

                // Точка или скобка в конце — это конец предложения, а не
                // часть адреса.
                while (end > start && ".,;:!?)»\"'".IndexOf(text[end - 1]) >= 0) end--;

                if (end - start > 4)
                {
                    string address = text.Substring(start, end - start);

                    found.Add(new TextRange
                    {
                        Type = "LINK",
                        Start = start,
                        Length = end - start,
                        Url = address.Contains("://") ? address : "https://" + address
                    });
                }

                at = end > start ? end : start + 1;
            }

            return found;
        }

        private static int NextLinkStart(string text, int from)
        {
            int best = -1;

            foreach (string prefix in new[] { "http://", "https://", "www." })
            {
                int at = text.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                // Внутри слова не считается: «xxxwww.» ссылкой не является.
                if (at > 0 && !char.IsWhiteSpace(text[at - 1])) continue;

                if (best < 0 || at < best) best = at;
            }

            return best;
        }

        /// <summary>
        /// Готовый список для поля `elements`. Наложенное руками сверяется с
        /// текстом, найденные ссылки добавляются к нему.
        /// </summary>
        public static List<object> Build(string text, IEnumerable<Applied> applied)
        {
            var ranges = new List<TextRange>();

            if (applied != null)
            {
                foreach (Applied mark in applied)
                {
                    int start = Locate(text, mark);
                    if (start < 0) continue;

                    ranges.Add(new TextRange
                    {
                        Type = mark.Type,
                        Start = start,
                        Length = mark.Covered.Length
                    });
                }
            }

            ranges.AddRange(DetectLinks(text));

            var elements = new List<object>();

            foreach (TextRange range in ranges)
            {
                var element = new Dictionary<string, object>
                {
                    { "type", range.Type },
                    { "from", (long)range.Start },
                    { "length", (long)range.Length }
                };

                if (range.Type == "LINK" && !string.IsNullOrEmpty(range.Url))
                {
                    element["attributes"] = new Dictionary<string, object> { { "url", range.Url } };
                }

                elements.Add(element);
            }

            return elements;
        }

        /// <summary>
        /// Где сейчас кусок, который размечали. Сперва там же, где был, —
        /// а если текст правили выше, то поиском.
        /// </summary>
        private static int Locate(string text, Applied mark)
        {
            if (mark == null || string.IsNullOrEmpty(mark.Covered)) return -1;

            if (mark.Start >= 0
                && mark.Start + mark.Covered.Length <= text.Length
                && text.Substring(mark.Start, mark.Covered.Length) == mark.Covered)
            {
                return mark.Start;
            }

            return text.IndexOf(mark.Covered, StringComparison.Ordinal);
        }
    }

    /// <summary>Текст сообщения вместе с его разметкой.</summary>
    public class RichString
    {
        public string Text { get; set; }
        public List<TextRange> Ranges { get; set; }

        public RichString(string text, List<TextRange> ranges)
        {
            Text = text ?? "";
            Ranges = ranges ?? new List<TextRange>();
        }
    }

    /// <summary>
    /// Присоединяемое свойство: кладёт размеченный текст в TextBlock.
    ///
    /// Обычная привязка к Text здесь не годится — разметка живёт не в
    /// строке, а в наборе Inline внутри блока, и собрать их можно только
    /// кодом.
    /// </summary>
    public static class RichTextBlock
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.RegisterAttached(
                "Source", typeof(object), typeof(RichTextBlock),
                new PropertyMetadata(null, OnSourceChanged));

        public static object GetSource(DependencyObject target)
        {
            return target.GetValue(SourceProperty);
        }

        public static void SetSource(DependencyObject target, object value)
        {
            target.SetValue(SourceProperty, value);
        }

        private static void OnSourceChanged(DependencyObject target,
                                            DependencyPropertyChangedEventArgs arguments)
        {
            var block = target as TextBlock;
            if (block == null) return;

            block.Inlines.Clear();

            var rich = arguments.NewValue as RichString;
            if (rich == null || rich.Text.Length == 0) return;

            if (rich.Ranges.Count == 0)
            {
                block.Inlines.Add(new Run { Text = rich.Text });
                return;
            }

            foreach (Inline inline in Build(rich)) block.Inlines.Add(inline);
        }

        /// <summary>
        /// Режет текст по границам всех отрезков и собирает куски. Порт
        /// segmentizeFormats.
        /// </summary>
        private static IEnumerable<Inline> Build(RichString rich)
        {
            string text = rich.Text;
            int length = text.Length;

            var points = new List<int> { 0, length };

            foreach (TextRange range in rich.Ranges)
            {
                int start = Clamp(range.Start, length);
                int end = Clamp(range.End, length);
                if (end <= start) continue;

                if (!points.Contains(start)) points.Add(start);
                if (!points.Contains(end)) points.Add(end);
            }

            points.Sort();

            var result = new List<Inline>();

            for (int i = 0; i < points.Count - 1; i++)
            {
                int start = points[i];
                int end = points[i + 1];
                if (end <= start) continue;

                var kinds = new HashSet<string>();
                string url = null;

                foreach (TextRange range in rich.Ranges)
                {
                    int rangeStart = Clamp(range.Start, length);
                    int rangeEnd = Clamp(range.End, length);

                    if (rangeStart > start || rangeEnd < end) continue;

                    kinds.Add(range.Type);
                    if (range.Type == "LINK" && url == null) url = range.Url;
                }

                result.Add(Piece(text.Substring(start, end - start), kinds, url));
            }

            return result;
        }

        /// <summary>
        /// Кусок текста со своими признаками.
        ///
        /// Зачёркивание в наборе Windows Phone 8.1 нарисовать нечем — среди
        /// Inline его попросту нет, — поэтому такой кусок остаётся обычным.
        /// Так же поступлено с ANIMOJI: это картинка, а не начертание.
        /// </summary>
        private static Inline Piece(string text, ICollection<string> kinds, string url)
        {
            var run = new Run { Text = text };

            if (kinds.Contains("STRONG") || kinds.Contains("HEADING"))
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (kinds.Contains("EMPHASIZED") || kinds.Contains("QUOTE"))
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (kinds.Contains("MONOSPACED"))
            {
                run.FontFamily = new FontFamily("Courier New");
            }

            if (kinds.Contains("USER_MENTION"))
            {
                run.Foreground = Application.Current.Resources["PhoneAccentBrush"] as Brush;
            }

            // Ссылка — не начертание, а поведение: по ней должно открываться.
            if (kinds.Contains("LINK"))
            {
                Uri target = Absolute(url ?? text);
                if (target != null)
                {
                    var link = new Hyperlink { NavigateUri = target };
                    link.Inlines.Add(run);
                    return link;
                }

                // Адрес не разобрался — пусть хотя бы подчёркнутым.
                var underline = new Underline();
                underline.Inlines.Add(run);
                return underline;
            }

            if (kinds.Contains("UNDERLINE"))
            {
                var underline = new Underline();
                underline.Inlines.Add(run);
                return underline;
            }

            return run;
        }

        /// <summary>
        /// Адрес без схемы — обычное дело в переписке: «ok.ru» пишут без
        /// «https://», а Hyperlink такого не принимает.
        /// </summary>
        private static Uri Absolute(string address)
        {
            string text = (address ?? "").Trim();
            if (text.Length == 0) return null;

            if (!text.Contains("://")) text = "https://" + text;

            try
            {
                return new Uri(text, UriKind.Absolute);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int Clamp(int value, int length)
        {
            if (value < 0) return 0;
            return value > length ? length : value;
        }
    }
}
