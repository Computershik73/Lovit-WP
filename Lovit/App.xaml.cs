using System;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Backend.Calls;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Ui;
using Lovit.Core.Storage;
using Lovit.Pages;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Phone.UI.Input;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Lovit
{
    public sealed partial class App : Application
    {
        private TransitionCollection _transitions;

        public App()
        {
            InitializeComponent();

            Suspending += OnSuspending;
            Resuming += OnResuming;

            // Аппаратная кнопка «назад» сама ничего не листает: без этого
            // обработчика она закрывает приложение прямо из открытого чата.
            HardwareButtons.BackPressed += OnBackPressed;

            // Сорвавшаяся задача, которую никто не дождался, молчит до
            // сборки мусора, а там роняет приложение. Отмечаем её
            // замеченной и пишем в журнал: пусть лучше останется след.
            TaskScheduler.UnobservedTaskException += (sender, argument) =>
            {
                argument.SetObserved();
                Log.Fail("app", "незамеченный срыв", argument.Exception);
            };

            // Память на телефоне отмерена, и за выход за предел приложение
            // закрывают без разговоров — со стороны отладчика это выглядит
            // как обрыв связи с ним. Картинки здесь самое тяжёлое, поэтому
            // при нехватке первыми уходят они.
            MemoryManager.AppMemoryUsageIncreased += OnMemoryUsageIncreased;

            // Само падение. Без этого от него не остаётся ничего: приложение
            // просто исчезает с экрана, а без отладчика и подавно.
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Необработанное исключение — записываем и даём упасть дальше.
        ///
        /// Гасить его нельзя: приложение останется на экране, но уже
        /// сломанным изнутри, и следующая беда будет непохожа на первую.
        ///
        /// Записанное дожидаемся здесь же: обычная запись откладывается на
        /// потом, а «потом» в этот миг не наступит — приложение унесёт с
        /// собой ровно те строки, ради которых журнал и заводился.
        /// </summary>
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception error = e.Exception;

            Log.Write("crash", "ПАДЕНИЕ: " + e.Message);

            if (error != null)
            {
                Log.Write("crash", error.GetType().FullName + ": " + error.Message);

                if (!string.IsNullOrEmpty(error.StackTrace))
                {
                    Log.Write("crash", error.StackTrace);
                }

                if (error.InnerException != null)
                {
                    Log.Write("crash", "внутри: " + error.InnerException.GetType().Name
                                       + ": " + error.InnerException.Message);
                }
            }

            Log.Settle();
        }

        private void OnMemoryUsageIncreased(object sender, object e)
        {
            // «Высоко» — единственная ступень тревоги, которую знает
            // Windows Phone 8.1: OverLimit появился уже в десятке, и здесь
            // после High идёт сразу закрытие.
            if (MemoryManager.AppMemoryUsageLevel != AppMemoryUsageLevel.High) return;

            // Если приложение всё-таки закроют за память, в журнале останется
            // след — иначе такое закрытие неотличимо от любого другого.
            Log.Write("mem", "памяти в обрез: занято "
                             + (MemoryManager.AppMemoryUsage / (1024 * 1024)) + " МБ из "
                             + (MemoryManager.AppMemoryUsageLimit / (1024 * 1024)) + " МБ");

            ImageCache.TrimForMemoryPressure();
        }

        private void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null) return;

            // Сперва спрашиваем саму страницу. У неё поверх содержимого может
            // быть открыто своё — видео во весь экран, выбор реакции, — и
            // «назад» должно закрывать сначала его. Иначе одно нажатие
            // закрывало и просмотр, и переписку разом.
            var aware = frame.Content as IHandlesBack;
            if (aware != null && aware.HandleBack())
            {
                e.Handled = true;
                return;
            }

            if (!frame.CanGoBack) return;

            e.Handled = true;
            frame.GoBack();
        }

        /// <summary>
        /// Возвращение из системного выбора файла. На Windows Phone 8.1 это
        /// не событие, а полноценная активация: пока выбор был на экране,
        /// приложения там не было. Выбранное отдаётся тому, кто его ждёт.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);

            EnsureFrame();

            var opened = args as FileOpenPickerContinuationEventArgs;
            if (opened != null) FilePicking.Complete(opened);

            var saved = args as FileSavePickerContinuationEventArgs;
            if (saved != null) FilePicking.Complete(saved);

            // Ссылка max://… Открыть её сразу нельзя: за ней надо сходить к
            // серверу, а связи в этот миг может ещё не быть. Откладываем до
            // списка чатов — он открывается после входа и разберёт её сам.
            var link = args as Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs;
            if (link != null && link.Uri != null)
            {
                PendingLink = link.Uri.OriginalString;
                Log.Write("app", "открыли по ссылке");
            }

            Window.Current.Activate();

            // Поток окна запоминается здесь, пока мы в нём и находимся:
            // из чужого потока его уже не спросить, а переводить работу
            // сюда приходится и из чтения сокета, и из пробуждения.
            MaxApi.RememberUi(Window.Current.Dispatcher);
        }

        /// <summary>
        /// Ссылка, по которой нас запустили и которую ещё предстоит открыть.
        /// Забирается тем, кто её откроет, — и тут же обнуляется.
        /// </summary>
        public static string PendingLink { get; set; }

        /// <summary>
        /// Чат из уведомления, по которому нас запустили, — откроется, когда
        /// главная страница войдёт в аккаунт. Забирается и обнуляется так же.
        /// </summary>
        public static long PendingChatId { get; set; }

        /// <summary>
        /// Окно с рамкой навигации. Заводится и при запуске, и при
        /// возвращении из выбора файла: если приложение успели выгрузить,
        /// возвращаться будет некуда.
        /// </summary>
        private Frame EnsureFrame()
        {
            var frame = Window.Current.Content as Frame;
            if (frame != null) return frame;

            frame = new Frame { CacheSize = 1 };

            _transitions = frame.ContentTransitions;
            frame.ContentTransitions = null;
            frame.Navigated += OnFirstNavigated;

            Window.Current.Content = frame;

            if (frame.Content == null)
            {
                Type start = string.IsNullOrEmpty(AppSettings.Token)
                    ? typeof(LoginPage)
                    : typeof(MainPage);

                frame.Navigate(start);
            }

            return frame;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;

            if (frame == null)
            {
                frame = new Frame();
                frame.CacheSize = 1;

                // Переходы между страницами на Windows Phone — часть языка
                // системы, а не украшение: без них экран просто подменяется,
                // и телефон перестаёт быть похожим на себя.
                _transitions = frame.ContentTransitions;
                frame.ContentTransitions = null;
                frame.Navigated += OnFirstNavigated;

                Window.Current.Content = frame;
            }

            // Нажали уведомление о сообщении — открыть его чат.
            long toastChat = Toasts.ChatFromLaunch(e.Arguments);

            if (frame.Content == null)
            {
                // Токен есть — сразу к чатам; вход показывается только тогда,
                // когда войти иначе нельзя.
                Type start = string.IsNullOrEmpty(AppSettings.Token)
                    ? typeof(LoginPage)
                    : typeof(MainPage);

                PendingChatId = toastChat;
                frame.Navigate(start, e.Arguments);
            }
            else if (toastChat != 0 && !string.IsNullOrEmpty(AppSettings.Token))
            {
                // Приложение уже работало: чат открываем поверх того, что
                // было на экране, — «назад» вернёт туда же.
                frame.Navigate(typeof(ChatPage), new ChatPage.Target { ChatId = toastChat });
            }

            Window.Current.Activate();

            // Поток окна — и при обычном запуске, а не только при повторной
            // активации: без него MaxApi.RunOnUi из чужого потока выполнял
            // работу прямо там же, и экран звонка падал с RPC_E_WRONG_THREAD.
            MaxApi.RememberUi(Window.Current.Dispatcher);

            // Возвращение приложения на экран — повод проверить связь: пока
            // телефон спал, сокет успевал умереть молча, и со стороны клиента
            // соединение выглядело живым до первой отправки.
            Window.Current.VisibilityChanged += (sender, argument) =>
            {
                Notifier.IsVisible = argument.Visible;
                if (argument.Visible) MaxApi.Instance.WakeUp();
            };

            Log.Write("app", "запуск, " + (string.IsNullOrEmpty(AppSettings.Token)
                                           ? "токена нет — на вход"
                                           : "токен есть — сразу к чатам"));

            WireCalls();
            Notifier.Start();

            var ignored = StartAsync();

            // Скачанное за прошлые запуски: лишнее убирается один раз и в
            // стороне, соединению это не мешает.
            ImageCache.PruneDiskAsync();
        }

        private static bool _callsWired;

        /// <summary>
        /// Входящий звонок открывает свой экран поверх того, что сейчас
        /// показано, — откуда бы ни пришёл. Один раз на процесс: запуск
        /// приложения бывает и повторным, а второй подписчик открыл бы
        /// экран звонка дважды.
        /// </summary>
        private static void WireCalls()
        {
            if (_callsWired) return;
            _callsWired = true;

            CallsModule.Instance.Start();
            CallsModule.Instance.IncomingStarted += call =>
            {
                var root = Window.Current.Content as Frame;
                if (root != null) root.Navigate(typeof(IncomingCallPage));
            };
        }

        private void OnFirstNavigated(object sender, NavigationEventArgs e)
        {
            var frame = sender as Frame;
            frame.ContentTransitions = _transitions ?? new TransitionCollection
            {
                new NavigationThemeTransition()
            };
            frame.Navigated -= OnFirstNavigated;
        }

        /// <summary>
        /// Начало работы: сперва поднимаем со склада карточки собеседников,
        /// потом соединяемся.
        ///
        /// Порядок важен. Список чатов приезжает за доли секунды, а карточки
        /// — десятками отдельных запросов и за несколько секунд; у диалога
        /// же своего названия нет, оно берётся из карточки. Не подняв склад
        /// заранее, список показывает номера вместо имён и переписывает их
        /// на глазах. Чтение склада занимает миллисекунды, и соединение от
        /// такой очереди не страдает.
        /// </summary>
        private static async Task StartAsync()
        {
            await ChatsModule.Instance.PrimeAsync();

            // Фоновое обновление плитки просим при каждом запуске: система
            // помнит заведённое между запусками, но не между установками, а
            // проверить это дешевле, чем гадать.
            await TileAgent.ApplyAsync();

            await MaxApi.Instance.ConnectAsync();
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // Соединение не рвём: система усыпит сокет сама, а при
            // возвращении сессия поднимется заново — этим займётся WakeUp.
            SuspendingDeferral deferral = e.SuspendingOperation.GetDeferral();

            // Накопленное в журнале дописывается здесь: если система закроет
            // приложение во сне, это последнее, что о нём останется, — и
            // именно ради таких случаев журнал и заводился.
            Log.Write("app", "уходим в сон");

            // Карточки собеседников тоже: имена из них нужны списку
            // чатов в следующий же запуск, а по ходу работы они пишутся
            // с оглядкой на часы, и последняя пачка могла не успеть.
            await ChatsModule.Instance.RememberNowAsync();
            await Log.FlushAsync();

            deferral.Complete();
        }

        /// <summary>
        /// Возвращение после сна. Событие Resuming приходит не всегда (при
        /// коротком уходе система обходится без него), поэтому связь проверяет
        /// ещё и появление окна.
        /// </summary>
        private void OnResuming(object sender, object e)
        {
            Log.Write("app", "просыпаемся");

            // Это событие приходит не на поток окна, а связь держат таймеры,
            // которые живут именно там: остановить такой таймер из чужого
            // потока — уронить приложение. Отсюда и перевод.
            MaxApi.RunOnUi(() => MaxApi.Instance.WakeUp());
        }
    }
}
