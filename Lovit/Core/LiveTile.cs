using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Lovit.Core
{
    /// <summary>
    /// Число на плитке приложения — сколько чатов ждут прочтения.
    ///
    /// Считаем именно чаты, а не сообщения: на плитке умещается двузначное
    /// число, и «сколько людей вам написали» человеку понятнее, чем «сколько
    /// всего сообщений накопилось». Так же поступают и почта, и телефон.
    ///
    /// Приглушённые чаты в счёт не идут: приглушают ровно затем, чтобы они
    /// не напоминали о себе, а число на плитке — самое настойчивое
    /// напоминание, какое есть.
    ///
    /// Пока приложение не работает, число не меняется: своей службы у
    /// клиента нет, а живой плиткой в этом смысле правит только сам MAX со
    /// своими уведомлениями. Зато при каждом возвращении в приложение оно
    /// становится верным, и после прочтения чатов плитка гаснет.
    /// </summary>
    public static class LiveTile
    {
        /// <summary>Больше этого плитка всё равно покажет «99+».</summary>
        private const int Most = 99;

        /// <summary>Что стоит на плитке сейчас — чтобы не трогать её попусту.</summary>
        private static int _shown = -1;

        /// <summary>
        /// Поставить число на плитку. Ноль — снять его совсем: плитка с
        /// нулём выглядит так, будто приложение сломалось.
        /// </summary>
        public static void Show(int count)
        {
            if (count < 0) count = 0;
            if (count > Most) count = Most;

            if (count == _shown) return;
            _shown = count;

            try
            {
                BadgeUpdater tile = BadgeUpdateManager.CreateBadgeUpdaterForApplication();

                if (count == 0)
                {
                    tile.Clear();
                    return;
                }

                // Разметку берём готовую у системы и подставляем в неё
                // число: собирать её строкой нельзя — на телефоне разбор
                // чужой разметки для плитки запрещён.
                XmlDocument shape = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);

                var badge = (XmlElement)shape.SelectSingleNode("/badge");
                badge.SetAttribute("value", count.ToString());

                tile.Update(new BadgeNotification(shape));
            }
            catch (Exception error)
            {
                // Плитка — не то, ради чего стоит падать: её может не быть
                // на начальном экране вовсе.
                Log.Write("app", "плитка не обновилась — " + error.Message);
            }
        }

        /// <summary>Снять число — при выходе из аккаунта.</summary>
        public static void Clear()
        {
            Show(0);
        }

        /// <summary>
        /// Забыть, что стоит на плитке. Нужно после выхода и входа заново:
        /// число могло не измениться, а поставить его надо всё равно.
        /// </summary>
        public static void Forget()
        {
            _shown = -1;
        }
    }
}
