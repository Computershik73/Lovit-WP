using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Lovit.Core.Storage;

namespace Lovit.Core
{
    /// <summary>
    /// Заводит и снимает фоновое обновление плитки.
    ///
    /// Сама задача живёт в отдельном компоненте (`Lovit.Background`) — здесь
    /// только просьба к системе будить его. Просить надо при каждом запуске:
    /// заведённое переживает и перезапуск, и перезагрузку телефона, но не
    /// переустановку приложения, — а перезаводить уже заведённое нельзя,
    /// система на это отвечает отказом.
    ///
    /// Задач две, потому что у одной задачи может быть только один повод её
    /// разбудить: одну будит время, другую — появление сети. Дело они делают
    /// общее, и класс у них один и тот же.
    /// </summary>
    public static class TileAgent
    {
        /// <summary>Имя класса задачи целиком — по нему система её и находит.</summary>
        private const string EntryPoint = "Lovit.Background.TileTask";

        private const string ByClock = "ЛовитПлиткаЧасы";
        private const string ByNetwork = "ЛовитПлиткаСеть";

        /// <summary>
        /// Как часто будить по времени. Полчаса — не выбор, а предел
        /// системы: чаще она задачи по времени не поднимает.
        /// </summary>
        private const uint EveryMinutes = 30;

        /// <summary>
        /// Привести заведённое в согласие с настройкой. Зовётся при запуске
        /// и при каждом переключении.
        /// </summary>
        public static async Task ApplyAsync()
        {
            try
            {
                Forget();

                if (!AppSettings.TileAgent)
                {
                    Log.Write("app", "фоновая плитка выключена");
                    return;
                }

                // Разрешение на фоновую работу. На телефоне оно даётся само,
                // но спросить надо: без спроса заведение иногда отвергается.
                try { await BackgroundExecutionManager.RequestAccessAsync(); }
                catch (Exception) { }

                Register(ByClock, new TimeTrigger(EveryMinutes, false));
                Register(ByNetwork, new SystemTrigger(SystemTriggerType.InternetAvailable, false));

                Log.Write("app", "фоновая плитка заведена");
            }
            catch (Exception error)
            {
                // Плитка — не то, ради чего стоит падать при запуске.
                Log.Fail("app", "фоновая плитка", error);
            }
        }

        private static void Register(string name, IBackgroundTrigger when)
        {
            try
            {
                var builder = new BackgroundTaskBuilder
                {
                    Name = name,
                    TaskEntryPoint = EntryPoint
                };

                builder.SetTrigger(when);
                builder.Register();
            }
            catch (Exception error)
            {
                // Повод разбудить бывает не на всякой сборке системы —
                // тогда остаётся другой.
                Log.Write("app", "задача «" + name + "» не завелась: " + error.Message);
            }
        }

        /// <summary>Снять заведённое — и перед новым заведением тоже.</summary>
        private static void Forget()
        {
            foreach (var pair in BackgroundTaskRegistration.AllTasks)
            {
                string name = pair.Value.Name;
                if (name != ByClock && name != ByNetwork) continue;

                pair.Value.Unregister(true);
            }
        }
    }
}
