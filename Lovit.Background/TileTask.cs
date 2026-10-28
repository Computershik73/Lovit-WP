using System;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Lovit.Core;

namespace Lovit.Background
{
    /// <summary>
    /// Фоновое обновление плитки: раз в получас и при появлении сети.
    ///
    /// Систему просят завести эту задачу по имени класса, и заводит она её
    /// сама — приложение в это время не работает вовсе. Отсюда все
    /// строгости: чужого состояния нет, времени считанные секунды, а любое
    /// необработанное исключение выглядит для системы как негодная задача,
    /// и она перестаёт её будить.
    ///
    /// Чаще получаса система будить не станет: это её предел для задач по
    /// времени. Мгновенной плитки на Windows Phone без службы самого MAX не
    /// бывает — уведомления его сервер рассылает через свои каналы, а нас в
    /// них нет.
    /// </summary>
    public sealed class TileTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Имя у выданного нам обращения английское и не наше: так его
            // назвали в описании самой задачи, а Windows Runtime требует,
            // чтобы имена совпадали слово в слово.
            //
            // Отсрочка обязательна: без неё система считает задачу
            // законченной на первом же ожидании и гасит процесс посреди
            // разговора с сервером.
            BackgroundTaskDeferral hold = taskInstance.GetDeferral();

            try
            {
                int count;

                using (var client = new SlimClient())
                {
                    count = await client.CountUnreadAsync();
                }

                // Меньше нуля — не дозвонились. Плитку тогда не трогаем:
                // прежнее число ближе к правде, чем ноль от неудачи.
                if (count >= 0)
                {
                    Show(count);
                    Log.Write("app", "плитка в фоне: непрочитанных чатов " + count);
                }
                else
                {
                    Log.Write("app", "плитка в фоне: до сервера не дошли");
                }

                await Log.FlushAsync();
            }
            catch (Exception error)
            {
                // Ни одно исключение отсюда не должно выйти наружу: система
                // сочтёт задачу негодной и больше будить её не станет.
                try
                {
                    Log.Fail("app", "плитка в фоне", error);
                    await Log.FlushAsync();
                }
                catch (Exception) { }
            }
            finally
            {
                hold.Complete();
            }
        }

        /// <summary>
        /// Поставить число на плитку. То же, что делает приложение, — но
        /// своё: до его кода отсюда не дотянуться, он живёт в другом
        /// хозяйстве.
        /// </summary>
        private static void Show(int count)
        {
            if (count > 99) count = 99;

            BadgeUpdater tile = BadgeUpdateManager.CreateBadgeUpdaterForApplication();

            if (count <= 0)
            {
                tile.Clear();
                return;
            }

            XmlDocument shape = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);

            var badge = (XmlElement)shape.SelectSingleNode("/badge");
            badge.SetAttribute("value", count.ToString());

            tile.Update(new BadgeNotification(shape));
        }
    }
}
