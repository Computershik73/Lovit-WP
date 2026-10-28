using System;

namespace Lovit.Core
{
    /// <summary>
    /// Время протокола — миллисекунды с 1 января 1970 года.
    ///
    /// В .NET для Windows Phone 8.1 нет ни `ToUnixTimeMilliseconds`, ни
    /// обратного преобразования: они появились только в .NET 4.6, а телефон
    /// остался на более раннем наборе. Пересчёт здесь и делается вручную.
    /// </summary>
    public static class UnixTime
    {
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Now
        {
            get { return FromDateTime(DateTime.UtcNow); }
        }

        public static long FromDateTime(DateTime moment)
        {
            return (long)(moment.ToUniversalTime() - Epoch).TotalMilliseconds;
        }

        /// <summary>Мгновение в поясе устройства: показывать время надо в нём.</summary>
        public static DateTime ToLocal(long milliseconds)
        {
            return Epoch.AddMilliseconds(milliseconds).ToLocalTime();
        }

        /// <summary>
        /// Часовой пояс в том виде, в каком его ждёт сервер. Имён вроде
        /// «Europe/Moscow» телефон не отдаёт, поэтому пояс называется своим
        /// смещением — сервер использует его только для времени доставки.
        /// </summary>
        public static string TimeZoneName
        {
            get
            {
                TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);

                // В именах зоны Etc знак обратный привычному: Etc/GMT-3 — это
                // пояс на три часа восточнее Гринвича, то есть UTC+3.
                int hours = (int)Math.Round(offset.TotalHours);
                if (hours == 0) return "Etc/GMT";

                return hours > 0
                    ? "Etc/GMT-" + hours
                    : "Etc/GMT+" + (-hours);
            }
        }
    }
}
