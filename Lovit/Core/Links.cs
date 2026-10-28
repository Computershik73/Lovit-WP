using System;

namespace Lovit.Core
{
    /// <summary>
    /// Ссылки на чаты и людей.
    ///
    /// Сервер отдаёт их по-разному: то одним именем, то полным адресом вида
    /// https://max.ru/имя. Показывать надо всегда одинаково — коротким
    /// именем, — а открывать умеет и то, и другое.
    /// </summary>
    public static class Links
    {
        /// <summary>Схема приложения: max://max.ru/имя.</summary>
        public const string Scheme = "max://";

        /// <summary>
        /// Короткое имя из ссылки, каким её ни пришли.
        ///
        /// Из «https://max.ru/max_channels» выходит «max_channels», из
        /// «max://max.ru/max_channels» — оно же, а уже короткое имя остаётся
        /// собой. Раньше в поиске стояло «@https://max.ru/max_channels»:
        /// собачку приписывали к чему угодно.
        /// </summary>
        public static string Name(string link)
        {
            if (string.IsNullOrEmpty(link)) return "";

            string text = link.Trim();

            // Отрезаем всё до последней косой черты — вместе со схемой и
            // именем узла, если они есть.
            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) text = text.Substring(scheme + 3);

            int slash = text.LastIndexOf('/');
            if (slash >= 0) text = text.Substring(slash + 1);

            // Запрос и якорь к имени не относятся.
            int cut = text.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) text = text.Substring(0, cut);

            return text.TrimStart('@');
        }

        /// <summary>Имя с собачкой — как показывают в оригинальном клиенте.</summary>
        public static string Tagged(string link)
        {
            string name = Name(link);
            return name.Length == 0 ? "" : "@" + name;
        }

        /// <summary>Наша ли это ссылка: max://, max.ru или oneme.ru.</summary>
        public static bool IsOurs(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;

            string text = uri.Trim().ToLower();

            return text.StartsWith(Scheme, StringComparison.Ordinal)
                   || text.Contains("max.ru/")
                   || text.Contains("oneme.ru/");
        }

        /// <summary>
        /// Приводит к тому виду, который понимает сервер: своя схема
        /// заменяется обычной. Всё остальное остаётся как есть.
        /// </summary>
        public static string ForServer(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return "";

            string text = uri.Trim();

            if (text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(Scheme.Length);

                // max://max_channels — без имени узла — тоже должно работать.
                if (text.IndexOf('/') < 0) text = "max.ru/" + text;

                text = "https://" + text;
            }

            return text;
        }
    }
}
