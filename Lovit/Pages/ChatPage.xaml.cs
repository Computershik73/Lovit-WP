using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Models;
using Lovit.Ui;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>Открытая переписка. Порт MAXChatViewController.</summary>
    public sealed partial class ChatPage : Page, IHandlesBack
    {
        /// <summary>
        /// Что открыть. Кроме номера чата бывает нужно место в истории —
        /// найденное сообщение или закреп — и заготовка для чужого канала,
        /// которого у нас ещё нет.
        /// </summary>
        public class Target
        {
            public long ChatId { get; set; }
            public string MessageId { get; set; }
            public long MessageTime { get; set; }
            public MaxChat Stub { get; set; }
        }

        private const int PageSize = 40;

        /// <summary>Окно вокруг найденного сообщения — как в iOS-версии.</summary>
        private const int WindowSize = 25;

        private readonly ObservableCollection<MessageRow> _rows = new ObservableCollection<MessageRow>();
        private readonly List<MaxMessage> _messages = new List<MaxMessage>();

        private long _chatId;
        private MaxChat _chat;
        private string _initialMessageId;
        private long _initialMessageTime;
        private string _replyToId;

        /// <summary>
        /// Правим ли сейчас своё сообщение — и какое. Правка занимает ту же
        /// строку ввода и ту же полоску, что и ответ: двух полей ввода в
        /// этом экране нет, а делают они одно и то же — отправляют набранное.
        /// </summary>
        private string _editingId;

        /// <summary>Разметка, наложенная на набираемое сообщение.</summary>
        private readonly List<Markup.Applied> _markup = new List<Markup.Applied>();

        /// <summary>Куда доводить прокрутку после досчёта разметки; пусто — в конец.</summary>
        private MessageRow _settleRow;
        private int _settlePasses;

        /// <summary>
        /// Высоты содержимого и окна на прошлом проходе доводки. Пока они
        /// меняются, разметка ещё не досчиталась.
        /// </summary>
        private double _settleExtent = -1;
        private double _settleViewport = -1;

        /// <summary>Сколько раз всего сработала разметка за одну доводку.</summary>
        private int _settleTicks;

        /// <summary>Куда вернуть прокрутку после пересборки; -1 — не надо.</summary>
        private double _settleOffset = -1;

        /// <summary>Прокрутку ставим мы сами — догрузка старого пока молчит.</summary>
        private bool _positioning;

        /// <summary>
        /// Где стояла прокрутка, когда уходили вперёд — в просмотрщик или в
        /// сведения о чате, — и стояла ли она в самом конце. Возвращение
        /// иначе показывает переписку с начала.
        /// </summary>
        private double _leftOffset = -1;
        private bool _leftAtEnd;

        /// <summary>
        /// Сколько сообщений пришло, пока читали выше по переписке. Их и
        /// показывает счётчик на кнопке «вниз».
        /// </summary>
        private int _missed;

        /// <summary>
        /// Насколько близко к концу надо стоять, чтобы новое сообщение
        /// листало переписку само. Примерно один пузырь: если конец уже
        /// виден, человек читает именно его, и доводка ему не помешает.
        /// </summary>
        private const double NearSlack = 160;

        /// <summary>Где стояла прокрутка, когда кружок ставили в прошлый раз.</summary>
        private double _noteOffset;

        /// <summary>
        /// Насколько может сдвинуться прокрутка между двумя сообщениями о
        /// ней, чтобы кружок ещё успевал за ней. Больше — значит, список
        /// летит, и гнаться бесполезно.
        /// </summary>
        private const double NoteChase = 40;

        private bool _loading;
        private bool _reachedStart;
        private ScrollViewer _scroll;

        public ChatPage()
        {
            InitializeComponent();
            MessagesList.ItemsSource = _rows;

            // Страница остаётся в кэше рамки. Без этого возвращение из
            // просмотрщика фотографий создавало её заново: переписка
            // грузилась с сервера сначала, показывалась полоса, прокрутка
            // уезжала. Видео такого не делает — оно играет прямо здесь,
            // никуда не уходя, — и разница была видна невооружённым глазом.
            //
            // Плата за это — обязанность прибираться руками: тот же объект
            // достаётся и следующей переписке.
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        /// <summary>
        /// Забыть прежнюю переписку перед открытием другой. Страница живёт в
        /// кэше, и всё, что в ней накопилось, само собой не исчезнет.
        /// </summary>
        private void Reset()
        {
            _messages.Clear();
            _rows.Clear();

            _replyToId = null;
            _editingId = null;
            _markup.Clear();

            _reachedStart = false;
            _loading = false;
            _fetching = false;

            _settleRow = null;
            _settleOffset = -1;
            _positioning = false;

            InputBox.Text = "";

            ReplyKind.Text = "Ответ на";
            ReplyBar.Visibility = Visibility.Collapsed;

            HideReactions();
            HideJump();
        }

        #region Открытие

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var target = e.Parameter as Target;
            if (target == null) return;

            // Вернулись в ту же переписку — она уже на экране целиком.
            // Грузить её заново незачем: это и есть та самая «загрузка
            // сначала после просмотра фотографии».
            bool back = e.NavigationMode == NavigationMode.Back
                        && _chatId == target.ChatId
                        && _rows.Count > 0;

            if (!back) Reset();

            _chatId = target.ChatId;
            _initialMessageId = target.MessageId;
            _initialMessageTime = target.MessageTime;

            _chat = ChatsModule.Instance.Chat(_chatId) ?? target.Stub;

            Log.Write("chat", "открываем чат " + _chatId
                              + (_chat == null ? " (сведений нет)" : "")
                              + (string.IsNullOrEmpty(_initialMessageId)
                                 ? "" : " на сообщении " + _initialMessageId));

            ChatsModule.Instance.MessageReceived += OnMessageReceived;
            ChatsModule.Instance.MessagesDeleted += OnMessagesDeleted;
            ChatsModule.Instance.ReactionsChanged += OnReactionsChanged;
            ChatsModule.Instance.Changed += OnChatsChanged;
            ChatsModule.Instance.Typing += OnTyping;
            ChatsModule.Instance.ReadMarkChanged += OnReadMarkChanged;

            // Об этом чате уведомлять не надо: он и так перед глазами. Уже
            // пришедшие о нём уведомления из центра уведомлений убираем.
            ChatsModule.Instance.VisibleChatId = _chatId;
            Notifier.Forget(_chatId);

            InputPane keyboard = InputPane.GetForCurrentView();
            keyboard.Showing += OnKeyboardShowing;
            keyboard.Hiding += OnKeyboardHiding;

            UpdateHeader();
            UpdateComposer();

            if (back)
            {
                // Подписки снимались при уходе — возвращаем. И тихо
                // догружаем то, что пришло, пока нас не было: пуши в это
                // время до нас не доходили.
                // Возвращаем прокрутку туда, где её оставили: список остался
                // прежним, а вот его положение при повторном показе
                // сбрасывается в начало.
                if (_leftAtEnd) ScrollToEnd();
                else if (_leftOffset >= 0) KeepOffset(_leftOffset);

                await MessagesModule.Instance.SubscribeAsync(_chatId, true);
                await CatchUpAsync();
                await RefreshPresenceAsync();

                return;
            }

            await LoadAsync();
            await MessagesModule.Instance.SubscribeAsync(_chatId, true);
            await RefreshPresenceAsync();

            // Сведения — ради числа подписчиков и ссылки канала: в списке
            // чатов их нет, а шапке канала без них сказать нечего.
            // Сведения нужны и о том чате, которого у нас нет вовсе: чужой
            // канал, открытый по пересылке, иначе показывал пустую шапку —
            // ни имени, ни числа подписчиков.
            if (_chat == null || !_chat.IsDialog)
            {
                IDictionary<string, object> info = await ChatsModule.Instance.FetchInfoAsync(_chatId);

                // У чужого канала сведения к себе не попадают — иначе он
                // считался бы нашим. Значит, разбираем их здесь: шапку
                // показать надо.
                if (info != null && ChatsModule.Instance.Chat(_chatId) == null)
                {
                    MaxChat described = ChatsModule.Instance.Describe(info);
                    if (described != null) _chat = described;
                }

                UpdateHeader();
                UpdateComposer();
            }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            ChatsModule.Instance.MessageReceived -= OnMessageReceived;
            ChatsModule.Instance.MessagesDeleted -= OnMessagesDeleted;
            ChatsModule.Instance.ReactionsChanged -= OnReactionsChanged;
            ChatsModule.Instance.Changed -= OnChatsChanged;
            ChatsModule.Instance.Typing -= OnTyping;
            ChatsModule.Instance.ReadMarkChanged -= OnReadMarkChanged;

            if (ChatsModule.Instance.VisibleChatId == _chatId) ChatsModule.Instance.VisibleChatId = 0;

            InputPane keyboard = InputPane.GetForCurrentView();
            keyboard.Showing -= OnKeyboardShowing;
            keyboard.Hiding -= OnKeyboardHiding;

            // Отступ снимаем: страница остаётся в кэше рамки, и с чужим
            // отступом она вернулась бы с полосой снизу.
            LayoutRoot.Margin = new Thickness(0);

            // Проигрывание обрываем: оставленный источник держит за собой и
            // файл, и память, а страница живёт в кэше рамки и сама по себе не
            // умирает.
            StopPlayback();
            StopMoving();

            // Микрофон и камера — тем более: они заняты для всей системы, и
            // оставленные включёнными не дадут снимать больше никому.
            await DropRecordingAsync();

            // Уходим вперёд — в просмотрщик фотографий, в сведения о чате —
            // и почти наверняка вернёмся: содержимое оставляем при себе.
            // Иначе возвращение застаёт пустую страницу и грузит переписку
            // с сервера сначала, а это и есть «после фотографии чат
            // грузится заново».
            //
            // Уходим назад — этот чат закрыт, и держать его строки вместе с
            // разобранными картинками незачем.
            if (e.NavigationMode == NavigationMode.Back)
            {
                Release();

                _leftOffset = -1;
                _leftAtEnd = false;
            }
            else
            {
                ScrollViewer kept = _scroll ?? FindScrollViewer(MessagesList);

                _leftAtEnd = AtEnd();
                _leftOffset = kept != null ? kept.VerticalOffset : -1;
            }

            await MessagesModule.Instance.SubscribeAsync(_chatId, false);
        }

        #endregion

        #region Клавиатура

        /// <summary>
        /// Клавиатура вылезла — страница ужимается на её высоту.
        ///
        /// Само по себе окно под клавиатуру не подстраивается: система лишь
        /// уводит всю страницу вверх, чтобы поле ввода осталось видно, и
        /// шапка переписки уезжает за верхний край вместе с ней.
        /// EnsuredFocusedElementInView говорит системе, что о видимости мы
        /// позаботимся сами, — после этого страница не двигается, а просто
        /// становится ниже, и список сообщений остаётся на экране.
        /// </summary>
        private void OnKeyboardShowing(InputPane pane, InputPaneVisibilityEventArgs arguments)
        {
            arguments.EnsuredFocusedElementInView = true;

            LayoutRoot.Margin = new Thickness(0, 0, 0, arguments.OccludedRect.Height);

            // Последнее сообщение должно остаться последним видимым: без
            // этого оно оказывается ровно под клавиатурой.
            ScrollToEnd();
        }

        private void OnKeyboardHiding(InputPane pane, InputPaneVisibilityEventArgs arguments)
        {
            arguments.EnsuredFocusedElementInView = true;
            LayoutRoot.Margin = new Thickness(0);
        }

        #endregion

        #region Прокрутка

        /// <summary>
        /// Догрузка старого при подходе к началу списка. Отдельной кнопки
        /// «показать ещё» в переписке быть не должно: историю листают, а не
        /// подгружают руками.
        /// </summary>
        private void HookScroll()
        {
            if (_scroll != null) return;

            _scroll = FindScrollViewer(MessagesList);
            if (_scroll == null) return;

            // Отдельным методом, а не безымянным: безымянный не отцепить, а
            // отцепить его надо — вместе со страницей.
            _scroll.ViewChanged += OnScrolled;
        }

        private async void OnScrolled(object sender, ScrollViewerViewChangedEventArgs arguments)
        {
            // Кружок играет на своём месте в переписке, а место это
            // с прокруткой едет — значит, едет и он. Но только пока едет
            // неспешно: на рывке кадр отстаёт от разметки, и от круга
            // остаётся линза.
            if (_playing != null && _playing.IsNote)
            {
                ScrollViewer view = _scroll ?? FindScrollViewer(MessagesList);
                double now = view != null ? view.VerticalOffset : 0;

                bool rushing = arguments != null
                               && arguments.IsIntermediate
                               && Math.Abs(now - _noteOffset) > NoteChase;

                _noteOffset = now;

                ShowNoteRound(rushing);
            }

            // Пока прокрутку ставим мы сами — не считать это за «пользователь
            // добрался до начала».
            //
            // Без этой оговорки выходил разбег: список перестраивается, на
            // миг оказывается в начале, срабатывает догрузка, после неё снова
            // перестроение — и так пока не кончится история. Со стороны это
            // и есть «обновить перематывает в самое начало» и «после отправки
            // чат дёргается рывками».
            if (_positioning) return;

            // Добрались до конца сами — пришедшее уже увидено.
            if (JumpDown.Visibility == Visibility.Visible && NearEnd())
            {
                HideJump();
                await MarkReadAsync();
            }

            if (_scroll == null || _scroll.VerticalOffset > 80) return;
            await LoadOlderAsync();
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            var found = root as ScrollViewer;
            if (found != null) return found;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ScrollViewer child = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (child != null) return child;
            }

            return null;
        }

        /// <summary>
        /// Кому звонить из этой переписки: собеседник диалога. В «Избранном»,
        /// группах, каналах и с ботами — некому, тогда 0.
        /// </summary>
        private long CallTarget()
        {
            if (_chat == null || !_chat.IsDialog || _chat.IsBot || _chatId == 0) return 0;

            long me = ChatsModule.Instance.CurrentUserId;
            long other = _chat.OtherParticipant(me);
            return other != 0 && other != me ? other : 0;
        }

        private async void OnCall(object sender, RoutedEventArgs e)
        {
            long target = CallTarget();
            if (target == 0) return;

            CallButton.IsEnabled = false;
            try
            {
                Backend.Calls.CallSession session = await Backend.Calls.CallsModule.Instance.CallAsync(target);
                if (session == null)
                {
                    var failed = new MessageDialog(Backend.Calls.CallsModule.Instance.LastError ?? "Не удалось позвонить.",
                                                   "Звонок");
                    await failed.ShowAsync();
                    return;
                }
                Frame.Navigate(typeof(CallPage));
            }
            finally
            {
                CallButton.IsEnabled = true;
            }
        }

        private void OnInfo(object sender, RoutedEventArgs e)
        {
            if (_chat == null) return;
            Frame.Navigate(typeof(ChatInfoPage), _chatId);
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            _initialMessageId = null;
            _reachedStart = false;

            await LoadAsync();
        }

        #endregion

        #region Шапка

        private void UpdateHeader()
        {
            ChatsModule chats = ChatsModule.Instance;

            MaxChat known = chats.Chat(_chatId);
            if (known != null) _chat = known;

            TitleText.Text = chats.Title(_chat);

            string subtitle = chats.Subtitle(_chat);
            SubtitleText.Text = subtitle;
            SubtitleText.Visibility = string.IsNullOrEmpty(subtitle)
                ? Visibility.Collapsed
                : Visibility.Visible;

            CallButton.Visibility = CallTarget() != 0 ? Visibility.Visible : Visibility.Collapsed;

            bool pinned = _chat != null && _chat.PinnedMessageId != 0;
            PinnedBar.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            if (pinned) PinnedText.Text = Formats.OneLine(_chat.PinnedMessageText ?? "Сообщение");

            ShowAvatar();
        }

        /// <summary>
        /// Плитка в шапке: та же, что в списке чатов, — картинка либо буквы
        /// на цветном квадрате, пока картинка едет.
        /// </summary>
        private async void ShowAvatar()
        {
            var row = new ListRow { Id = _chatId, Title = TitleText.Text };

            // Начинаем с чистого места — с букв.
            //
            // Без этого в шапке оставалась аватарка прежней переписки:
            // страница живёт в кэше и сама ничего не стирает, а картинка
            // нового чата либо приедет не сразу, либо не приедет вовсе —
            // у многих чатов своей аватарки нет. Тогда чужая так и висела:
            // сперва пару секунд, а если у нового чата аватарки не было —
            // до самого выхода.
            AvatarImage.Source = null;
            InitialsBox.Visibility = Visibility.Visible;

            InitialsText.Text = row.Initials;
            InitialsBox.Background = row.AvatarBrush;

            // Сведений о чате может не быть вовсе — но чистая шапка нужна и
            // тогда: иначе в ней остаётся картинка прошлой переписки.
            if (_chat == null) return;

            string url = ChatsModule.Instance.AvatarUrl(_chat);
            if (string.IsNullOrEmpty(url)) return;

            long wanted = _chatId;

            ImageSource image = ImageCache.Cached(url, ImageCache.AvatarSide)
                                ?? await ImageCache.LoadAsync(url, ImageCache.AvatarSide);

            // Пока качали, могли уйти в другую переписку — и эта картинка
            // уже не к этой шапке.
            if (image == null || _chatId != wanted) return;

            AvatarImage.Source = image;
            InitialsBox.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// В канале писать может только администратор: у остальных строка
        /// ввода заменяется подпиской — так же, как в оригинальном клиенте.
        /// </summary>
        private void UpdateComposer()
        {
            bool canWrite = _chat == null || _chat.CanWrite(ChatsModule.Instance.CurrentUserId);

            InputBox.Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed;
            AttachButton.Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed;
            SubscribeButton.Visibility = canWrite ? Visibility.Collapsed : Visibility.Visible;

            UpdateSendButton();

            if (canWrite) return;

            bool member = ChatsModule.Instance.Chat(_chatId) != null;
            SubscribeButton.Content = member ? "отписаться" : "подписаться";
        }

        /// <summary>
        /// Одна кнопка на два дела: пока не набрано ни слова, отправлять
        /// нечего — и на её месте стоит микрофон. Как только появился текст,
        /// микрофон уступает: писать и говорить разом всё равно не выйдет.
        /// </summary>
        private void UpdateSendButton()
        {
            bool canWrite = _chat == null || _chat.CanWrite(ChatsModule.Instance.CurrentUserId);
            bool typed = InputBox.Text.Trim().Length > 0;

            // Во время правки микрофон не показываем даже на пустом поле:
            // записать голосовое взамен написанного нельзя, а стёртый текст
            // означает лишь, что правку ещё не набрали.
            bool editing = _editingId != null;

            SendButton.Visibility = canWrite && typed ? Visibility.Visible : Visibility.Collapsed;
            RecordButton.Visibility = canWrite && !typed && !editing
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// «Назад» при открытом поверх переписки: закрывает сперва его.
        ///
        /// Раньше нажатие уходило прямо приложению, и оно уводило со
        /// страницы — видео закрывалось заодно с чатом, хотя закрыть хотели
        /// только видео.
        ///
        /// Звук так не перехватываем: голосовое слушают, глядя в переписку,
        /// и ничего поверх неё при этом не открыто.
        /// </summary>
        public bool HandleBack()
        {
            if (ReactionLayer.Visibility == Visibility.Visible)
            {
                HideReactions();
                return true;
            }

            bool over = PlayerControls.Visibility == Visibility.Visible
                        || NoteRound.Visibility == Visibility.Visible;

            if (!over) return false;

            StopPlayback();
            return true;
        }

        private void OnChatsChanged()
        {
            UpdateHeader();
            UpdateComposer();
        }

        private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_chat == null) return;
            Frame.Navigate(typeof(ChatInfoPage), _chatId);
        }

        #endregion

        #region История

        /// <summary>
        /// Догрузить то, что пришло, пока страница была закрыта.
        ///
        /// Не перестраивая ничего попусту: если нового нет — а чаще всего
        /// его нет, — на экране не дрогнет ни строки. Пришло — добавляем в
        /// конец, и только тогда пересобираем список.
        ///
        /// Берём обычный последний десяток: он же и приходит при открытии
        /// чата. Старее того, что уже показано, ничего не вставляем — иначе
        /// сообщения встали бы не по порядку.
        /// </summary>
        private async Task CatchUpAsync()
        {
            if (_messages.Count == 0) return;
            if (!await MaxApi.Instance.EnsureOnlineAsync()) return;

            List<MaxMessage> tail = await MessagesModule.Instance.HistoryAsync(_chatId, 0, PageSize);
            if (tail == null || tail.Count == 0) return;

            tail.Sort((first, second) => first.Time.CompareTo(second.Time));

            long last = _messages[_messages.Count - 1].Time;
            bool added = false;

            foreach (MaxMessage message in tail)
            {
                if (message.Time < last) continue;
                if (Contains(message.MessageId)) continue;

                _messages.Add(message);
                added = true;
            }

            if (!added) return;

            Log.Write("chat", "чат " + _chatId + ": догрузили пропущенное");

            // Куда встать — решаем по тому, где стояли при уходе: сейчас
            // прокрутка ещё едет на своё место, и спрашивать её бесполезно.
            Rebuild();

            if (_leftAtEnd) ScrollToEnd();
            else KeepPosition();

            await MarkReadAsync();
        }

        private async Task LoadAsync()
        {
            if (_loading) return;

            _loading = true;
            Progress.Visibility = Visibility.Visible;

            // Сколько было непрочитано — запоминаем до всего остального:
            // отметка о прочтении в конце этого же метода обнулит счётчик,
            // а встать нам надо там, где чтение прервалось.
            MaxChat counted = ChatsModule.Instance.Chat(_chatId);
            int unread = counted != null ? counted.UnreadCount : 0;

            // Связь проверяется до запроса, а не после.
            //
            // Пока этого не было, открытый на мёртвом сокете чат выглядел
            // пустым: RequestAsync возвращает пустоту сразу, не дожидаясь
            // ничего, история приходила пустым списком, и переписка на
            // экране становилась чистой — без единого намёка, что дело в
            // связи. Списки чатов так делали давно, переписка — нет.
            if (!await MaxApi.Instance.EnsureOnlineAsync())
            {
                Progress.Visibility = Visibility.Collapsed;
                _loading = false;

                ShowStatus("Нет связи с сервером. Потяните «обновить».");
                return;
            }

            List<MaxMessage> history;

            if (!string.IsNullOrEmpty(_initialMessageId) && _initialMessageTime > 0)
            {
                // Вокруг найденного сообщения: с одним лишь `backward`
                // открывается хвост, обрывающийся на нём, и вниз прокрутить
                // нечего.
                history = await MessagesModule.Instance.HistoryAsync(
                    _chatId, _initialMessageTime + 1, WindowSize, WindowSize);
            }
            else
            {
                history = await MessagesModule.Instance.HistoryAsync(_chatId, 0, PageSize);
            }

            Progress.Visibility = Visibility.Collapsed;
            _loading = false;

            // История приезжает от новых к старым, а на экране порядок обратный.
            history.Sort((first, second) => first.Time.CompareTo(second.Time));

            _messages.Clear();
            _messages.AddRange(history);

            Log.Write("chat", "чат " + _chatId + ": показываем " + _messages.Count + " сообщений");

            ShowStatus(_messages.Count == 0 ? "Здесь пока ничего нет" : null);
            Rebuild();

            if (!string.IsNullOrEmpty(_initialMessageId)) ScrollTo(_initialMessageId);
            else ScrollToUnread(unread);

            // Прокрутку цепляем после первой отрисовки: до неё ScrollViewer
            // внутри списка ещё не создан.
            HookScroll();

            await MarkReadAsync();
        }

        /// <summary>Догрузка вверх: следующая порция старее самого старого.</summary>
        private async Task LoadOlderAsync()
        {
            if (_loading || _reachedStart || _messages.Count == 0) return;

            _loading = true;
            Progress.Visibility = Visibility.Visible;

            long oldest = _messages[0].Time;
            List<MaxMessage> older = await MessagesModule.Instance.HistoryAsync(_chatId, oldest, PageSize);

            Progress.Visibility = Visibility.Collapsed;
            _loading = false;

            if (older.Count == 0)
            {
                _reachedStart = true;
                return;
            }

            older.Sort((first, second) => first.Time.CompareTo(second.Time));

            var known = new HashSet<string>();
            foreach (MaxMessage message in _messages) known.Add(message.MessageId ?? "");

            var added = new List<MaxMessage>();
            foreach (MaxMessage message in older)
            {
                if (!known.Contains(message.MessageId ?? "")) added.Add(message);
            }

            if (added.Count == 0)
            {
                _reachedStart = true;
                return;
            }

            _messages.InsertRange(0, added);

            // Место в списке сохраняется само: строки добавляются сверху,
            // а прокрутка стоит на прежней строке.
            MessageRow anchor = _rows.Count > 0 ? _rows[0] : null;
            Rebuild();

            if (anchor != null) ScrollTo(anchor.MessageId);
        }

        /// <summary>
        /// Отпустить всё, что держала переписка, — при уходе с неё.
        ///
        /// Строки пузырей ссылаются на картинки сами, помимо кэша. Кэш свою
        /// запись вытесняет, а пузырь продолжает держать ту же картинку, и
        /// освободиться ей не от чего: сорок сообщений с фотографиями — это
        /// под сотню мегабайт, которые переживают уход с экрана и уходят
        /// только тогда, когда сборщик доберётся до самой страницы. А он не
        /// торопится, пока памяти вдоволь, — и расход растёт с каждым
        /// открытым чатом.
        /// </summary>
        private void Release()
        {
            MessagesList.LayoutUpdated -= OnSettleLayout;
            _settleRow = null;

            if (_scroll != null)
            {
                _scroll.ViewChanged -= OnScrolled;
                _scroll = null;
            }

            MessagesList.ItemsSource = null;

            foreach (MessageRow row in _rows) row.Photo = null;

            _rows.Clear();
            _messages.Clear();

            MessagesList.ItemsSource = _rows;

            Log.Memory("вышли из чата " + _chatId);
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text ?? "";
            StatusText.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// «В сети» и «был(-а) 5 мин назад» в шапке диалога. Порт
        /// refreshPresence из MAXChatViewController: в списке чатов
        /// присутствия нет, его надо спрашивать отдельно — до этого запроса
        /// в шапке остаётся номер телефона.
        /// </summary>
        private async Task RefreshPresenceAsync()
        {
            if (_chat == null || !_chat.IsDialog) return;

            long otherId = _chat.OtherParticipant(ChatsModule.Instance.CurrentUserId);
            if (otherId == 0 || otherId == ChatsModule.Instance.CurrentUserId) return;

            await ChatsModule.Instance.FetchPresenceAsync(new List<object> { otherId });
            UpdateHeader();
        }

        private async Task MarkReadAsync()
        {
            if (_messages.Count == 0) return;
            await MessagesModule.Instance.MarkReadAsync(_chatId, _messages[_messages.Count - 1]);
        }

        #endregion

        #region Пуши

        private async void OnMessageReceived(MaxMessage message)
        {
            if (message == null || message.ChatId != _chatId) return;

            // Своё отправленное возвращается ещё и пушем — без этой проверки
            // оно встало бы в переписке дважды.
            if (Contains(message.MessageId)) return;

            // Читают ли сейчас конец переписки. Спрашиваем до того, как
            // сообщение встало в список: после этого высота уже другая.
            bool follow = NearEnd();

            _messages.Add(message);
            Append(message);

            if (!follow)
            {
                // Читают выше — не дёргаем. Листать переписку под пальцем
                // при каждом входящем значит выдёргивать человека из того
                // места, куда он сам добрался; вместо этого считаем пришедшее
                // и показываем кнопку, как это делают нынешние мессенджеры.
                //
                // Прочитанным такое сообщение тоже не отмечаем: его ещё
                // не видели.
                _missed++;
                ShowJump();

                return;
            }

            ScrollToEnd();

            await MarkReadAsync();
        }

        /// <summary>
        /// Стоит ли прокрутка близко к концу — настолько, что конец виден.
        /// Прокрутки ещё нет — значит, список только открылся и мы у конца.
        /// </summary>
        private bool NearEnd()
        {
            ScrollViewer scroll = _scroll ?? FindScrollViewer(MessagesList);
            if (scroll == null) return true;

            return scroll.ScrollableHeight - scroll.VerticalOffset < NearSlack;
        }

        private void ShowJump()
        {
            JumpCount.Text = _missed > 99 ? "99+" : _missed.ToString();

            JumpBadge.Visibility = _missed > 0 ? Visibility.Visible : Visibility.Collapsed;
            JumpDown.Visibility = Visibility.Visible;
        }

        private void HideJump()
        {
            _missed = 0;

            JumpBadge.Visibility = Visibility.Collapsed;
            JumpDown.Visibility = Visibility.Collapsed;
        }

        private async void OnJumpDown(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;

            HideJump();
            ScrollToEnd();

            await MarkReadAsync();
        }

        /// <summary>
        /// Сообщение удалили с другой стороны. Раньше оно оставалось на
        /// экране до перезахода в чат: пуш об удалении никто не слушал.
        /// </summary>
        private void OnMessagesDeleted(long chatId, IList<string> messageIds)
        {
            if (chatId != _chatId || messageIds == null) return;

            bool removed = false;

            foreach (string id in messageIds)
            {
                MaxMessage message = Find(id);
                if (message == null) continue;

                _messages.Remove(message);
                removed = true;
            }

            if (!removed) return;

            Rebuild();
            KeepPosition();
        }

        /// <summary>
        /// Реакции изменились — свои или чужие. Счётчики приходят готовыми,
        /// своя реакция в пуше не упоминается и остаётся прежней.
        /// </summary>
        private void OnReactionsChanged(long chatId, string messageId, IList<object> counters)
        {
            if (chatId != _chatId) return;

            MaxMessage message = Find(messageId);
            if (message == null) return;

            message.ApplyCounters(counters);

            Refresh(message);
        }

        #region Отметки о прочтении

        /// <summary>
        /// Галочки своего сообщения: одна — дошло, две — прочитано. В
        /// «Избранном» читать некому, в канале отметок не бывает.
        /// </summary>
        private void ShowDelivery(MessageRow row, long readMark)
        {
            bool shown = row.IsOutgoing && _chatId != 0 && (_chat == null || !_chat.IsChannel);
            row.ShowDelivery(shown, readMark > 0 && row.MessageTime <= readMark);
        }

        /// <summary>Собеседник что-то прочитал — перекрашиваем галочки на месте.</summary>
        private void OnReadMarkChanged(long chatId)
        {
            if (chatId != _chatId) return;

            long readMark = ChatsModule.Instance.ReadMarkOfOthers(_chatId);
            foreach (MessageRow row in _rows)
            {
                if (row.IsOutgoing && !string.IsNullOrEmpty(row.Ticks)) ShowDelivery(row, readMark);
            }
        }

        #endregion

        private void OnTyping(long chatId, string name)
        {
            if (chatId != _chatId) return;

            SubtitleText.Text = name + " печатает…";
            SubtitleText.Visibility = Visibility.Visible;

            // Через несколько секунд возвращаем обычную подпись: отдельного
            // уведомления «перестал печатать» сервер не шлёт.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (source, argument) =>
            {
                timer.Stop();
                UpdateHeader();
            };
            timer.Start();
        }

        private bool Contains(string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return false;

            foreach (MaxMessage known in _messages)
            {
                if (known.MessageId == messageId) return true;
            }
            return false;
        }

        #endregion

        #region Отрисовка

        /// <summary>
        /// Пересборка всего списка, а не добавление строки: разделитель дня
        /// принадлежит первому сообщению дня, и появление нового сообщения
        /// может переставить его.
        /// </summary>
        private void Rebuild()
        {
            _rows.Clear();
            _movingRows.Clear();

            long previousTime = 0;

            foreach (MaxMessage message in _messages)
            {
                Compose(message, previousTime);
                previousTime = message.Time;
            }

            // Отметка о сборке списка. Падение внутри неё ничего после себя
            // не оставляет, и только по наличию этой строки видно, дошли ли
            // мы до конца или умерли на полпути.
            Log.Write("chat", "список собран: строк " + _rows.Count);

            // Кому из стикеров двигаться — решается по тому, что видно на
            // экране, а разбирается это отсчётом кадров: разметке нужно время
            // расставить строки, и спрашивать её прямо сейчас бессмысленно.
            if (_movingRows.Count > 0) StartMoving();
        }

        /// <summary>
        /// Дописать в конец списка одно новое сообщение.
        ///
        /// Не пересобирая всё: пересборка опустошает список, прокрутка
        /// сваливается в начало, и возвращать её приходится числом — со
        /// стороны это моргание и съезд на пару сообщений. Дописать же в
        /// конец можно, не потревожив ни строки выше, ни самой прокрутки.
        /// </summary>
        private void Append(MaxMessage message)
        {
            // Разделитель дня считается по предыдущему сообщению, а новое
            // уже стоит в списке последним.
            long previousTime = _messages.Count >= 2
                ? _messages[_messages.Count - 2].Time
                : 0;

            Compose(message, previousTime);
        }

        /// <summary>Собрать строки одного сообщения и поставить их в конец.</summary>
        private void Compose(MaxMessage message, long previousTime)
        {
            bool outgoing = message.SenderId == ChatsModule.Instance.CurrentUserId;

            string day = Formats.SameDay(previousTime, message.Time)
                ? null
                : Formats.DaySeparator(message.Time);

            var row = new MessageRow
            {
                MessageId = message.MessageId,
                Text = message.DisplayText,
                Time = Formats.ShortTime(message.Time) + (message.IsEdited ? " · изм." : ""),
                Author = AuthorName(message, outgoing),
                DayLabel = day,
                IsOutgoing = outgoing,
                MessageTime = message.Time,
                Reactions = ReactionLine(message)
            };
            ShowDelivery(row, ChatsModule.Instance.ReadMarkOfOthers(_chatId));

            if (message.IsForwarded)
            {
                string from = ForwardedName(message);

                row.QuoteTitle = string.IsNullOrEmpty(from)
                    ? "Пересланное сообщение"
                    : "Переслано от " + from;
            }
            else if (!string.IsNullOrEmpty(message.ReplyPreview))
            {
                row.QuoteTitle = "Ответ";
                row.QuoteText = Formats.OneLine(message.ReplyPreview);
            }

            row.IsService = message.IsService;

            // Картинку подставляем отдельно: она приезжает по сети
            // и не должна задерживать саму переписку.
            row.IsSticker = message.IsSticker;

            // Движущийся стикер оживает поверх своей же неподвижной
            // картинки: пока описание движения качается и разбирается,
            // на экране уже что-то есть.
            MaxAttachment moving = message.FirstShown;
            if (row.IsSticker && moving != null && !string.IsNullOrEmpty(moving.LottieUrl))
            {
                // Сама сцена заводится не здесь, а когда строка окажется
                // на экране: в канале со стикерами их сотни, и заводить
                // всё подряд — верный способ израсходовать память впустую.
                row.Source = moving;
                _movingRows.Add(row);
            }

            // Снимков несколько — показываем их сеткой в одном пузыре.
            // Стикеры сюда не попадают: они и приходят по одному, и
            // рисуются иначе.
            List<MaxAttachment> pictures = Pictures(message);

            if (pictures.Count > 1 && !row.IsSticker)
            {
                row.Album = Formats.Album(pictures);
                LoadAlbum(row);
            }
            else
            {
                foreach (MaxAttachment attachment in message.Attachments)
                {
                    if (!attachment.IsImage || string.IsNullOrEmpty(attachment.Url)) continue;

                    row.PhotoUrl = attachment.Url;

                    // Ширина пузыря за вычетом полей — под неё и считается
                    // высота, чтобы место занять заранее. У стикера ширина
                    // своя: он мельче фотографии.
                    row.PhotoHeight = attachment.DisplayHeightFor(row.IsSticker ? 160 : 356);

                    LoadPhoto(row);
                    break;
                }
            }

            // Кнопки бота. Своё сообщение кнопка не знает — проставляем
            // здесь, без этого нажатие некуда отправить.
            MaxAttachment keyboard = message.Keyboard;
            if (keyboard != null)
            {
                foreach (IList<InlineButton> line in keyboard.Buttons)
                {
                    foreach (InlineButton button in line)
                    {
                        button.MessageId = message.MessageId;
                        button.CallbackId = keyboard.CallbackId;
                    }
                }

                row.Buttons = keyboard.Buttons;
            }

            // Голосовое, песня, видео — со своей плашкой и кнопкой.
            MaxAttachment media = message.Playable;
            if (media != null)
            {
                row.Source = media;
                row.IsPlayable = true;
                row.MediaTitle = string.IsNullOrEmpty(media.Name)
                    ? media.Placeholder
                    : media.Name;
                row.MediaGlyph = GlyphPlay;
                row.MediaStatus = media.DurationText;

                // Подпись «Голосовое сообщение» уже стоит на плашке —
                // второй раз тем же текстом её повторять незачем.
                if (row.Text == media.Placeholder) row.Text = "";

                // У ролика сервер даёт кадр — показываем его над плашкой
                // вместо пустого места: так сразу видно, что за видео.
                if (media.IsVideo)
                {
                    row.IsNote = media.VideoType == 1;
                    ShowFrame(row, media);
                }
                else
                {
                    row.WaveBytes = media.Wave;
                    row.WaveShape = Formats.Wave(media.Wave);
                }
            }

            MaxAttachment file = message.File;
            if (file != null)
            {
                if (row.Source == null) row.Source = file;

                row.FileName = string.IsNullOrEmpty(file.Name) ? "Файл" : file.Name;
                row.FileSize = file.SizeText;

                // Имя показано отдельной строкой — подпись «Файл: имя»
                // вместо текста повторяла бы её.
                if (row.Text == file.Placeholder) row.Text = "";
            }

            // Разметка — последней, когда текст строки уже окончателен.
            // Отрезки в ней отсчитываются от текста сообщения, поэтому
            // на подпись вместо текста — «Фото», «Файл: …» — она не
            // накладывается: там они указывали бы мимо.
            row.Formatted = new RichString(row.Text,
                row.Text == message.Text ? TextRange.Parse(message.Elements) : null);

            _rows.Add(row);

            AddExtras(message, row, outgoing);

        }


        /// <summary>
        /// Остальные вложения того же сообщения — своими строками.
        ///
        /// Отправленное разом MAX кладёт в одно сообщение несколькими
        /// вложениями. Мы же брали из них первое и на том останавливались:
        /// послали с компьютера два ролика — на телефоне виден один.
        ///
        /// Добавочные строки идут без имени, времени и отметок: это
        /// продолжение того же сообщения, а не отдельные реплики.
        /// </summary>
        private void AddExtras(MaxMessage message, MessageRow first, bool outgoing)
        {
            var shown = new List<MaxAttachment>();

            // Снимки, ушедшие в сетку, отдельными строками уже не идут:
            // они все показаны в первом же пузыре.
            bool gridded = first.Album != null && first.Album.Count > 0;

            foreach (MaxAttachment attachment in message.Attachments)
            {
                if (attachment.IsInlineKeyboard || attachment.IsService) continue;

                bool picture = attachment.IsImage && !string.IsNullOrEmpty(attachment.Url);
                if (picture && gridded) continue;

                bool drawn = picture || attachment.IsPlayable || attachment.IsFile;

                if (drawn) shown.Add(attachment);
            }

            // Остальное считается по-прежнему: первое из них уже показано
            // в том же пузыре, что и сетка, — плашкой ролика или файла.

            if (shown.Count < 2) return;

            Log.Write("chat", "в сообщении " + message.MessageId + " вложений: " + shown.Count);

            for (int i = 1; i < shown.Count; i++)
            {
                MaxAttachment extra = shown[i];

                var row = new MessageRow
                {
                    MessageId = message.MessageId,
                    Text = "",
                    Time = "",
                    IsOutgoing = outgoing,
                    Formatted = new RichString("", null),
                    Source = extra
                };

                if (extra.IsImage && !string.IsNullOrEmpty(extra.Url))
                {
                    row.IsSticker = extra.Type == "STICKER";
                    row.PhotoUrl = extra.Url;
                    row.PhotoHeight = extra.DisplayHeightFor(row.IsSticker ? 160 : 356);

                    LoadPhoto(row);
                }
                else if (extra.IsPlayable)
                {
                    row.IsPlayable = true;
                    row.MediaTitle = string.IsNullOrEmpty(extra.Name)
                        ? extra.Placeholder
                        : extra.Name;
                    row.MediaGlyph = GlyphPlay;
                    row.MediaStatus = extra.DurationText;

                    if (extra.IsVideo)
                    {
                        row.IsNote = extra.VideoType == 1;
                        ShowFrame(row, extra);
                    }
                }
                else
                {
                    row.FileName = string.IsNullOrEmpty(extra.Name) ? "Файл" : extra.Name;
                    row.FileSize = extra.SizeText;
                }

                _rows.Add(row);
            }
        }

        /// <summary>
        /// Открыть источник пересланного.
        ///
        /// У канала и группы есть своё имя — их и открываем перепиской, как
        /// открывали всегда. А вот переписка человека с кем-то ещё нам не
        /// принадлежит: сервер о ней ничего не даст, и раньше на её месте
        /// открывался пустой чат с «здесь пока ничего нет» и чужой
        /// картинкой в шапке. Вместо этого показываем карточку человека —
        /// оттуда ему можно и написать.
        ///
        /// Отличаем одно от другого по имени источника: у переписки с
        /// человеком его нет, сервер шлёт одну ссылку на сообщение.
        /// </summary>
        private void OpenSource(MaxMessage message)
        {
            bool named = !string.IsNullOrEmpty(message.ForwardedFrom);

            if (named && message.ForwardedChatId != 0)
            {
                Frame.Navigate(typeof(ChatPage), new Target
                {
                    ChatId = message.ForwardedChatId,
                    MessageId = message.ForwardedMessageId,
                    MessageTime = message.ForwardedTime
                });

                return;
            }

            if (message.ForwardedSenderId != 0)
            {
                Frame.Navigate(typeof(ContactPage), message.ForwardedSenderId);
                return;
            }

            // Ни имени, ни отправителя — открывать нечего. Молчим: пустая
            // переписка на этом месте вводила бы в заблуждение.
            var ignored = new MessageDialog("Источник этого сообщения недоступен.").ShowAsync();
        }

        /// <summary>
        /// Откуда пересланное. Сервер называет источник только тогда, когда
        /// у того есть своё имя, — у канала или группы. Пересылают же чаще
        /// из переписки с человеком, и там остаётся один номер отправителя:
        /// по нему и ищем карточку. Не нашли — подпись будет без имени, но
        /// будет: пересылка должна быть видна как пересылка.
        /// </summary>
        private static string ForwardedName(MaxMessage message)
        {
            if (!string.IsNullOrEmpty(message.ForwardedFrom)) return message.ForwardedFrom;

            if (message.ForwardedSenderId == 0) return null;

            MaxContact sender = ChatsModule.Instance.Contact(message.ForwardedSenderId);
            if (sender != null) return sender.DisplayName;

            // Карточки ещё нет — просим её, подпись догонит при пересборке.
            ChatsModule.Instance.EnsureContact(message.ForwardedSenderId);
            return null;
        }

        /// <summary>
        /// Имя над пузырём нужно только в группах: в диалоге собеседник и так
        /// один, и подпись под каждой репликой лишь съедала бы ширину.
        /// </summary>
        private string AuthorName(MaxMessage message, bool outgoing)
        {
            if (outgoing || _chat == null || _chat.IsDialog) return null;

            // В канале пишет сам канал, а не человек: отправителя в таком
            // сообщении нет вовсе, и над каждым постом стояло «ID 0».
            if (message.SenderId == 0) return null;

            MaxContact contact = ChatsModule.Instance.Contact(message.SenderId);
            if (contact != null) return contact.DisplayName;

            // Карточка ещё не приехала — лучше промолчать, чем показывать
            // голый номер: она догрузится и подставится сама.
            ChatsModule.Instance.EnsureContact(message.SenderId);
            return null;
        }

        /// <summary>
        /// Кадр ролика над плашкой. Сперва отдельной картинкой по адресу —
        /// её видно в запасе и место под неё отводится заранее. Если адреса
        /// нет, берётся кадр, вложенный в само сообщение: он приходит байтами,
        /// своего адреса не имеет и потому подставляется напрямую.
        /// </summary>
        private static async void ShowFrame(MessageRow row, MaxAttachment video)
        {
            // Кружок квадратный всегда: в круг его обрежет разметка,
            // а считать высоту по присланным размерам тут нечего.
            row.PhotoHeight = row.IsNote ? MessageRow.NoteSide : video.DisplayHeightFor(356);

            if (!string.IsNullOrEmpty(video.Thumbnail))
            {
                row.PhotoUrl = video.Thumbnail;
                LoadPhoto(row);
                return;
            }

            if (video.PreviewBytes == null) return;

            // Место под кадр — сразу, ещё до разбора: иначе пузырь подрастёт
            // уже после того, как прокрутка доведена до конца.
            row.PhotoExpected = true;

            ImageSource frame = await ImageCache.FromBytesAsync(video.PreviewBytes,
                                                               ImageCache.PhotoSide);

            if (frame != null) row.Photo = frame;
        }

        #region Движущиеся стикеры

        /// <summary>
        /// Сколько разобранных сцен держим в памяти.
        ///
        /// Это не предел движению: двигаются все, кто виден. Предел нужен
        /// другому — сцены остаются жить и после ухода строки за край экрана,
        /// чтобы прокрутка туда-обратно не заводила их заново. Когда их
        /// набирается больше, самые давние из невидимых освобождаются.
        /// </summary>
        private const int MostMoving = 8;

        /// <summary>
        /// Оживление стикеров. Включено с отсрочками записи в журнал: опыт
        /// с выключенным движением показал, что без него всё цело, а значит
        /// падение именно здесь. Осталось узнать, на каком шаге.
        /// </summary>
        private const bool MovingAllowed = true;

        /// <summary>
        /// Сторона кадра.
        ///
        /// Стикер показывается шириной около ста шестидесяти точек разметки, а
        /// на экране с двойной плотностью это втрое больше настоящих. Рисовать
        /// в ста двадцати восьми, как было, значит растягивать вчетверо — оттого
        /// и мыло. Триста двадцать дают чёткую картинку и стоят четыреста
        /// килобайт памяти на стикер.
        /// </summary>
        private const int MovingSide = 320;

        /// <summary>
        /// Уже оживлённые, по номеру сообщения. Список строк пересобирается
        /// целиком при каждом новом сообщении, а сцены переживают это: иначе
        /// каждая пересборка заводила бы их заново.
        /// </summary>
        private readonly Dictionary<string, LottieAnimation> _moving =
            new Dictionary<string, LottieAnimation>();

        /// <summary>Те, что сейчас качаются. Без этого счёта их не удержать.</summary>
        private readonly HashSet<string> _openingMoving = new HashSet<string>();

        /// <summary>Строки со стикерами, которым положено двигаться.</summary>
        private readonly List<MessageRow> _movingRows = new List<MessageRow>();

        private DispatcherTimer _movingTicker;

        /// <summary>Через сколько тиков перебирать список заново.</summary>
        private int _movingScan;

        /// <summary>
        /// Видна ли строка сейчас.
        ///
        /// Список отдаёт разметку только тем строкам, что рядом с экраном;
        /// у остальных её нет вовсе, и это само по себе ответ. Для
        /// оставшихся сверяем их место с окном прокрутки.
        /// </summary>
        private bool IsShown(MessageRow row)
        {
            var container = MessagesList.ContainerFromItem(row) as FrameworkElement;
            if (container == null || container.ActualHeight <= 0) return false;

            ScrollViewer scroll = _scroll ?? FindScrollViewer(MessagesList);
            if (scroll == null) return false;

            try
            {
                Point at = container.TransformToVisual(scroll).TransformPoint(new Point(0, 0));

                return at.Y + container.ActualHeight > 0 && at.Y < scroll.ViewportHeight;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void StartMoving()
        {
            if (!MovingAllowed || _movingRows.Count == 0) return;

            if (_movingTicker == null)
            {
                _movingTicker = new DispatcherTimer();
                _movingTicker.Tick += OnMovingTick;
            }

            // Шаг общий на всех. Свой отсчёт под каждый стикер — это лишние
            // часы в разметке, а нужный кадр всё равно считается по времени,
            // так что от общего шага темп не страдает.
            _movingTicker.Interval = TimeSpan.FromMilliseconds(20);
            _movingTicker.Start();
        }

        private void OnMovingTick(object sender, object e)
        {
            // Перебор списка — не на каждый тик: строк в канале бывают сотни,
            // а разметка успевает переехать заметно медленнее.
            if (--_movingScan <= 0)
            {
                _movingScan = 10;
                ScanMoving();
            }

            var broken = new List<string>();

            foreach (KeyValuePair<string, LottieAnimation> pair in _moving)
            {
                // Ушедшие за край экрана замирают: рисовать невидимое —
                // впустую тратить и время, и батарею.
                if (!pair.Value.Shown) continue;

                if (!pair.Value.Advance()) broken.Add(pair.Key);
            }

            foreach (string key in broken)
            {
                _moving[key].Dispose();
                _moving.Remove(key);
            }

            if (_moving.Count == 0 && _openingMoving.Count == 0 && _movingRows.Count == 0)
            {
                _movingTicker.Stop();
            }
        }

        /// <summary>
        /// Пройти по стикерам и разобраться, кому двигаться.
        ///
        /// Видимые заводятся, ушедшие за край замирают. Сцены при этом не
        /// освобождаются: человек прокручивает переписку туда-обратно, и
        /// заводить их заново на каждый проход было бы дороже, чем держать.
        /// Освобождаются только самые давние, когда их набирается больше
        /// разрешённого.
        /// </summary>
        private void ScanMoving()
        {
            var shown = new List<MessageRow>();

            foreach (MessageRow row in _movingRows)
            {
                if (IsShown(row)) shown.Add(row);
            }

            foreach (KeyValuePair<string, LottieAnimation> pair in _moving)
            {
                pair.Value.Shown = false;
            }

            foreach (MessageRow row in shown)
            {
                LottieAnimation ready;
                if (_moving.TryGetValue(row.MessageId, out ready))
                {
                    ready.Shown = true;

                    // Строки пересобираются целиком, и новой надо снова отдать
                    // ту же картинку.
                    if (row.Photo != ready.Picture) row.Photo = ready.Picture;
                    continue;
                }

                Animate(row);
            }

            Forget();
        }

        /// <summary>
        /// Освободить самые давние сцены, если их набралось больше
        /// разрешённого. Видимые не трогаем никогда.
        /// </summary>
        private void Forget()
        {
            if (_moving.Count <= MostMoving) return;

            var spare = new List<string>();

            foreach (KeyValuePair<string, LottieAnimation> pair in _moving)
            {
                if (!pair.Value.Shown) spare.Add(pair.Key);
            }

            foreach (string key in spare)
            {
                if (_moving.Count <= MostMoving) break;

                _moving[key].Dispose();
                _moving.Remove(key);
            }
        }

        private async void Animate(MessageRow row)
        {
            MaxAttachment sticker = row.Source;
            if (sticker == null || string.IsNullOrEmpty(sticker.LottieUrl)) return;

            // Считаем и уже идущие загрузки, а не только доведённые до конца:
            // иначе на каждый перебор заводилось бы по соединению на каждый
            // стикер разом.
            if (_openingMoving.Count >= 2) return;
            if (!_openingMoving.Add(row.MessageId)) return;

            LottieAnimation made;

            try
            {
                made = await LottieAnimation.OpenAsync(
                    sticker.LottieUrl, sticker.Token ?? row.MessageId, MovingSide);
            }
            finally
            {
                _openingMoving.Remove(row.MessageId);
            }

            if (made == null) return;

            // Пока качали, могли уйти со страницы.
            if (_rows.Count == 0)
            {
                made.Dispose();
                return;
            }

            made.Shown = true;

            _moving[row.MessageId] = made;

            row.Photo = made.Picture;
            made.Advance();

            StartMoving();
        }

        /// <summary>
        /// Остановить и отпустить. Зовётся при уходе со страницы: сцена
        /// держит за собой разобранное описание, а оно немаленькое.
        /// </summary>
        private void StopMoving()
        {
            if (_movingTicker != null) _movingTicker.Stop();

            foreach (LottieAnimation moving in _moving.Values) moving.Dispose();

            _moving.Clear();
            _openingMoving.Clear();
            _movingRows.Clear();
        }

        #endregion

        /// <summary>
        /// Снимки сообщения — те, что показываются картинкой. Стикер сюда не
        /// идёт: он приходит один и рисуется без пузыря.
        /// </summary>
        private static List<MaxAttachment> Pictures(MaxMessage message)
        {
            var found = new List<MaxAttachment>();

            foreach (MaxAttachment attachment in message.Attachments)
            {
                if (attachment.IsInlineKeyboard || attachment.IsService) continue;
                if (!attachment.IsImage || string.IsNullOrEmpty(attachment.Url)) continue;
                if (attachment.Type == "STICKER") continue;

                found.Add(attachment);
            }

            return found;
        }

        private static void LoadAlbum(MessageRow row)
        {
            foreach (AlbumLine line in row.Album)
            {
                foreach (AlbumCell cell in line.Cells) LoadCell(cell);
            }
        }

        private static async void LoadCell(AlbumCell cell)
        {
            // Ячейка мельче пузыря, и разбирать снимок в полную величину
            // ради неё незачем: шесть таких — это несколько десятков
            // мегабайт, которых на телефоне попросту нет. Берём вдвое
            // больше ячейки — с запасом на плотность экрана.
            int side = Math.Min(ImageCache.PhotoSide, (int)(cell.Wide * 2));

            ImageSource ready = ImageCache.Cached(cell.Url, side);
            if (ready != null)
            {
                cell.Photo = ready;
                return;
            }

            ImageSource loaded = await ImageCache.LoadAsync(cell.Url, side);
            if (loaded != null) cell.Photo = loaded;
        }

        /// <summary>Снимок из сетки открывается на весь экран, как и одиночный.</summary>
        private void OnAlbumClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var cell = element != null ? element.DataContext as AlbumCell : null;

            if (cell == null || string.IsNullOrEmpty(cell.Url)) return;

            // Открываем не один снимок, а весь набор сообщения — с того, по
            // которому нажали: дальше по ним листают, не возвращаясь сюда.
            Frame.Navigate(typeof(PhotoPage), new PhotoPage.Album
            {
                Urls = cell.All,
                At = cell.At
            });
        }

        private static async void LoadPhoto(MessageRow row)
        {
            ImageSource ready = ImageCache.Cached(row.PhotoUrl, ImageCache.PhotoSide);
            if (ready != null)
            {
                row.Photo = ready;
                return;
            }

            ImageSource loaded = await ImageCache.LoadAsync(row.PhotoUrl, ImageCache.PhotoSide);
            if (loaded != null) row.Photo = loaded;
        }

        private void ScrollToEnd()
        {
            if (_rows.Count == 0) return;

            MessagesList.ScrollIntoView(_rows[_rows.Count - 1]);
            Settle(null);
        }

        /// <summary>
        /// Довести прокрутку, когда разметка досчитается.
        ///
        /// ScrollIntoView считает по той разметке, что готова на этот миг, а
        /// пузыри в этот миг ещё не измерены: список только что наполнили.
        /// Высота содержимого выходит заниженной, прокрутка встаёт по ней —
        /// и последнее сообщение остаётся наполовину под строкой ввода.
        ///
        /// Поэтому досчёт разметки ловится и прокрутка повторяется по уже
        /// настоящей высоте. Проходов немного и они считаны: каждый из них
        /// сам меняет разметку, и без счётчика это ходило бы по кругу.
        /// </summary>
        private void Settle(MessageRow row)
        {
            _settleRow = row;
            _settleOffset = -1;

            ResetSettle();

            MessagesList.LayoutUpdated -= OnSettleLayout;
            MessagesList.LayoutUpdated += OnSettleLayout;
        }

        /// <summary>
        /// Остаться там же, где стояли. Нужно после пересборки списка: она
        /// опустошает его и прокрутка сваливается в начало — а чат от этого
        /// «прыгает» и вдобавок начинает тянуть старую историю, приняв начало
        /// списка за просьбу догрузить.
        /// </summary>
        /// <summary>
        /// Довести прокрутку до заданного места — того, где её оставили,
        /// уходя со страницы.
        /// </summary>
        private void KeepOffset(double offset)
        {
            _settleRow = null;
            _settleOffset = offset;

            ResetSettle();

            MessagesList.LayoutUpdated -= OnSettleLayout;
            MessagesList.LayoutUpdated += OnSettleLayout;
        }

        private void KeepPosition()
        {
            ScrollViewer scroll = _scroll ?? FindScrollViewer(MessagesList);
            if (scroll == null) return;

            _settleRow = null;

            // Стояли в самом конце — туда и возвращаемся, а не к прежнему
            // числу: высоты пузырей могли измениться, и прежнее число теперь
            // означает уже другое место, чуть выше конца.
            _settleOffset = AtEnd() ? -1 : scroll.VerticalOffset;

            ResetSettle();

            MessagesList.LayoutUpdated -= OnSettleLayout;
            MessagesList.LayoutUpdated += OnSettleLayout;
        }

        private void ResetSettle()
        {
            _settlePasses = 0;
            _settleTicks = 0;
            _settleExtent = -1;
            _settleViewport = -1;
            _positioning = true;
        }

        private void OnSettleLayout(object sender, object e)
        {
            ScrollViewer view = _scroll ?? FindScrollViewer(MessagesList);

            // Пока в разметке меняются высоты, проход не считается
            // потраченным.
            //
            // Меняться им есть отчего: строка ввода досчитывает свою высоту
            // уже после того, как список наполнен, а с нею меняется и высота
            // окна прокрутки. Считанный до этого предел прокрутки занижен
            // ровно на высоту строки ввода — и в прочитанном чате последнее
            // сообщение оставалось под ней. Пять проходов подряд с
            // неизменными высотами означают, что разметка улеглась.
            if (view != null)
            {
                bool moving = view.ExtentHeight != _settleExtent
                              || view.ViewportHeight != _settleViewport;

                _settleExtent = view.ExtentHeight;
                _settleViewport = view.ViewportHeight;

                if (moving) _settlePasses = 0;
            }

            // Общий предел — на случай, если высоты не улягутся никогда:
            // картинки в пузырях приезжают по одной и раздвигают их, и без
            // этого счёта доводка могла бы не кончиться вовсе.
            if (_settlePasses++ > 4 || _settleTicks++ > 40 || _rows.Count == 0)
            {
                MessagesList.LayoutUpdated -= OnSettleLayout;
                _settleOffset = -1;
                _positioning = false;
                return;
            }

            if (_settleOffset >= 0)
            {
                ScrollViewer kept = _scroll ?? FindScrollViewer(MessagesList);
                if (kept != null)
                {
                    kept.ChangeView(null, Math.Min(_settleOffset, kept.ScrollableHeight), null, true);
                }
                return;
            }

            if (_settleRow != null)
            {
                // Повтор по настоящей высоте: если непрочитанных мало, здесь
                // прокрутка и упрётся в конец — как ей и положено.
                MessagesList.ScrollIntoView(_settleRow, ScrollIntoViewAlignment.Leading);
                return;
            }

            // К самому концу — не «покажи последнюю строку», а именно в
            // конец: только так под ней ничего не остаётся.
            ScrollViewer scroll = _scroll ?? FindScrollViewer(MessagesList);
            if (scroll != null) scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        }

        /// <summary>
        /// Куда встать при открытии: на первое непрочитанное.
        ///
        /// Оба случая делает одно и то же действие, и это не хитрость, а
        /// свойство прокрутки — дальше конца списка она не уходит. Когда
        /// непрочитанных много и они не помещаются на экран, первое из них
        /// встаёт под самую шапку. Когда их мало, прокрутка упирается в
        /// конец раньше, чем успевает поднять его наверх, — и оно остаётся
        /// видно целиком, а пустоты под последним сообщением не возникает.
        ///
        /// Непрочитанных нет — идём в самый низ, к последнему сообщению.
        /// </summary>
        private void ScrollToUnread(int unread)
        {
            if (_rows.Count == 0) return;

            if (unread <= 0)
            {
                ScrollToEnd();
                return;
            }

            // Непрочитанные — это хвост истории. Если их больше, чем
            // загружено, начало чтения осталось выше загруженного окна, и
            // ближе к нему первой строки нам не встать.
            int first = _rows.Count - unread;
            if (first < 0) first = 0;

            MessagesList.ScrollIntoView(_rows[first], ScrollIntoViewAlignment.Leading);
            Settle(_rows[first]);
        }

        private void ScrollTo(string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            foreach (MessageRow row in _rows)
            {
                if (row.MessageId != messageId) continue;

                MessagesList.ScrollIntoView(row);

                // Через ту же выдержку: место сохраняется по настоящей
                // высоте, и догрузка на это время не срабатывает.
                Settle(row);
                return;
            }
        }

        #endregion

        #region Действия над сообщением

        private async void OnMessageClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as MessageRow;
            if (row == null) return;

            MaxMessage message = Find(row.MessageId);
            if (message == null) return;

            // Фотография открывается на весь экран. Проверка идёт первой:
            // у пересланного снимка нажатие должно открывать снимок, а не
            // уводить в чужую переписку.
            // У ролика картинка — это его кадр, а не снимок: нажатие должно
            // запускать видео, а не открывать неподвижное изображение.
            if (row.IsPlayable)
            {
                PlayRow(row);
                return;
            }

            if (!string.IsNullOrEmpty(row.PhotoUrl))
            {
                Frame.Navigate(typeof(PhotoPage), row.PhotoUrl);
                return;
            }

            // Присланный файл. Ссылку на скачивание сервер выдаёт отдельным
            // запросом и ненадолго, поэтому спрашиваем её в миг нажатия, а
            // не заранее. Открывает файл система: своего просмотрщика ни для
            // одного из мыслимых видов у нас нет и быть не может.
            MaxAttachment file = row.Source != null && row.Source.IsFile
                ? row.Source
                : message.File;

            if (file != null)
            {
                // Пока качаем, повторные нажатия пропускаем. Без этого каждое
                // из них заново спрашивало у сервера ссылку и начинало свою
                // закачку — а нажимают, пока ничего не происходит, охотно.
                if (_fetching) return;
                _fetching = true;

                try
                {
                    await OpenFileAsync(row, message, file);
                }
                finally
                {
                    _fetching = false;
                }

                return;
            }

            // Переход к источнику отсюда убран: он висит на подписи
            // «Переслано от …», а не на всём пузыре. Нажатие по тексту
            // пересланного поста уводило в чужую переписку — а его чаще
            // всего просто читают. Вдобавок у сообщения с сеткой снимков
            // нажатие приходило и сюда тоже: снимок открывался, но перед
            // ним в истории оказывался чужой канал, и «назад» из
            // просмотрщика приводило туда, где человек не был.
            await Task.FromResult(0);
        }

        /// <summary>
        /// Нажатие по подписи над сообщением. У пересланного она ведёт к
        /// источнику, у ответа — к тому сообщению, на которое отвечали.
        ///
        /// Ответ ищется среди загруженных: сервер отдаёт в ответе только
        /// его текст, без времени, а без времени окно истории вокруг него
        /// не запросить. Не нашли — ничего и не делаем: увести в пустоту
        /// хуже, чем не увести никуда.
        /// </summary>
        private void OnQuoteClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as MessageRow : null;
            if (row == null) return;

            MaxMessage message = Find(row.MessageId);
            if (message == null) return;

            if (message.IsForwarded)
            {
                OpenSource(message);
                return;
            }

            if (!string.IsNullOrEmpty(message.ReplyMessageId)) ScrollTo(message.ReplyMessageId);
        }

        /// <summary>Идёт ли сейчас загрузка присланного файла.</summary>
        private bool _fetching;

        /// <summary>
        /// Присланный файл: ссылку сервер выдаёт отдельным запросом и
        /// ненадолго, поэтому спрашиваем её в миг нажатия, а не заранее.
        /// </summary>
        private async Task OpenFileAsync(MessageRow row, MaxMessage message, MaxAttachment file)
        {
            {
                Progress.Visibility = Visibility.Visible;

                string url = await MessagesModule.Instance.ResolveFileUrlAsync(
                    file.FileId, _chatId, message.MessageId);

                Progress.Visibility = Visibility.Collapsed;

                if (string.IsNullOrEmpty(url)) url = file.Url;

                if (string.IsNullOrEmpty(url))
                {
                    var missing = new MessageDialog("Сервер не дал ссылки на этот файл.");
                    await missing.ShowAsync();
                    return;
                }

                // Качаем сами и открываем уже с диска, а не отдаём ссылку
                // браузеру. Так виден ход загрузки, второй раз то же самое не
                // качается, — и до CDN мы доходим своей криптографией, тогда
                // как браузер на этом телефоне до него не доходит вовсе.
                ShowUpload(0, 0);
                ProgressText.Text = "загрузка…";

                MediaHttp.Progress report = (done, total) =>
                {
                    var ignored = Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Low,
                        () =>
                        {
                            if (total <= 0) return;

                            Progress.IsIndeterminate = false;
                            Progress.Maximum = total;
                            Progress.Value = done;

                            ProgressText.Text = "загрузка " + (done * 100 / total) + "% — "
                                                + Formats.Size(done) + " из " + Formats.Size(total);
                        });
                };

                string name = string.IsNullOrEmpty(file.Name)
                    ? (file.FileId != 0 ? file.FileId.ToString() : row.MessageId)
                    : file.FileId + "-" + file.Name;

                StorageFile saved = await MediaStore.FetchAsync(url, name, report);

                HideUpload();

                if (saved == null)
                {
                    var failed = new MessageDialog("Не удалось загрузить файл.");
                    await failed.ShowAsync();
                    return;
                }

                await Launcher.LaunchFileAsync(saved);
            }
        }

        #region Проигрыватель

        /// <summary>Что сейчас играет. Одновременно — не больше одного.</summary>
        private MessageRow _playing;
        private DispatcherTimer _ticker;

        /// <summary>Чем оказалось то, что играем, — для внятного отказа.</summary>
        private string _playingFormat;

        /// <summary>До какого столбика закрашен график — чтобы не пересобирать зря.</summary>
        private int _wavePart = -1;

        /// <summary>
        /// Куда перемотать, как только звук откроется. Пока он не открыт,
        /// длительность неизвестна, и переставлять некуда.
        /// </summary>
        private double _pendingSeek = -1;

        /// <summary>
        /// Перемотка нажатием или протягиванием по графику. Место нажатия
        /// вдоль него и есть место в записи — так это устроено везде.
        /// </summary>
        private void OnWaveTapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            // Дальше не пускаем: иначе список сочтёт это нажатием по строке и
            // запустит проигрывание с начала.
            e.Handled = true;

            SeekWave(element, e.GetPosition(element).X);
        }

        private void OnWaveDragged(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            e.Handled = true;

            SeekWave(element, e.Position.X);
        }

        private void SeekWave(FrameworkElement element, double x)
        {
            var row = element.DataContext as MessageRow;
            if (row == null || element.ActualWidth <= 0) return;

            double part = x / element.ActualWidth;
            if (part < 0) part = 0;
            if (part > 1) part = 1;

            // По ещё не запущенному тоже перематывают — тогда запускаем с
            // этого места, а не с начала.
            if (_playing != row)
            {
                _pendingSeek = part;
                PlayRow(row);
                return;
            }

            TimeSpan whole = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            if (whole <= TimeSpan.Zero) return;

            Player.Position = TimeSpan.FromMilliseconds(whole.TotalMilliseconds * part);

            // Показываем сразу, не дожидаясь тика: иначе палец уже ушёл, а
            // закраска ещё стоит на прежнем месте.
            _wavePart = -1;
            row.WavePlayed = Formats.Wave(row.WaveBytes, part);
        }

        private const string GlyphPlay = "";
        private const string GlyphPause = "";

        /// <summary>
        /// Нажатие на кнопку у голосового, песни или видео.
        ///
        /// Проигрывать прямо по ссылке нельзя — до CDN системный
        /// проигрыватель не доходит своей проверкой сертификатов, — поэтому
        /// вложение сперва скачивается нашей криптографией на диск, и уже
        /// оттуда играется. Второй раз то же самое не качается.
        /// </summary>
        private void OnPlayTapped(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as MessageRow : null;

            if (row != null) PlayRow(row);
        }

        /// <summary>
        /// Запустить вложение строки. Отдельно от обработчика кнопки: то же
        /// действие вызывает и нажатие по кадру ролика.
        /// </summary>
        private async void PlayRow(MessageRow row)
        {
            // Повторное нажатие по тому же — пауза и продолжение.
            if (_playing == row)
            {
                if (Player.CurrentState == MediaElementState.Playing)
                {
                    Player.Pause();
                    row.MediaGlyph = GlyphPlay;
                }
                else
                {
                    Player.Play();
                    row.MediaGlyph = GlyphPause;
                }
                return;
            }

            StopPlayback();

            MaxMessage message = Find(row.MessageId);

            // У строки своё вложение: в сообщении их бывает несколько, и брать
            // первое попавшееся нельзя.
            MaxAttachment media = row.Source
                                  ?? (message != null ? message.Playable : null);

            if (media == null) return;

            _playing = row;

            row.MediaBusy = true;
            row.MediaPercent = 0;
            row.MediaStatus = "загрузка…";

            // У видео свой путь: номер видео живёт в отдельном счёте, и
            // скачиванием файла его не взять — сервер отвечает «файл не
            // найден». Адрес спрашивается отдельной командой.
            string url = media.IsVideo
                ? await MessagesModule.Instance.ResolveVideoUrlAsync(
                      media.FileId, media.Token, _chatId, row.MessageId)
                : media.Url;

            if (string.IsNullOrEmpty(url) && !media.IsVideo && media.FileId != 0)
            {
                url = await MessagesModule.Instance.ResolveFileUrlAsync(
                    media.FileId, _chatId, row.MessageId);
            }

            if (string.IsNullOrEmpty(url))
            {
                row.ResetMedia(MessagesModule.Instance.LastError ?? "сервер не дал ссылки");
                _playing = null;
                return;
            }

            // Ход скачивания приходит из чужого потока, а трогать разметку
            // можно только из своего.
            MediaHttp.Progress report = (done, total) =>
            {
                double percent = total > 0 ? done * 100.0 / total : 0;

                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    if (_playing != row) return;

                    row.MediaPercent = percent;
                    row.MediaStatus = total > 0
                        ? "загрузка " + (int)percent + "%"
                        : "загрузка…";
                });
            };

            string key = media.FileId != 0 ? media.FileId.ToString() : row.MessageId;

            // Видео играем на лету, не дожидаясь конца закачки: проигрыватель
            // сам просит нужные куски через прослойку. Ролик может весить
            // десятки мегабайт, и ждать их все перед первым кадром незачем.
            if (media.IsVideo)
            {
                row.MediaStatus = "открываем…";

                RemoteStream live = await RemoteStream.OpenAsync(url);
                if (_playing != row) return;

                if (live != null)
                {
                    try
                    {
                        _playingFormat = ".mp4";

                        Player.SetSource(live, "video/mp4");
                        Player.Play();

                        row.MediaGlyph = GlyphPause;
                        row.MediaBusy = false;
                        row.MediaPercent = 0;

                        ShowVideo(true, row.IsNote);
                        StartTicker();
                        return;
                    }
                    catch (Exception error)
                    {
                        Log.Write("mem", "поток не пошёл, качаем целиком: " + error.Message);
                    }
                }

                // Не вышло — старым способом, целиком на диск.
                row.MediaStatus = "загрузка…";
            }

            // Звук мал, и ему важнее верно опознать формат, — для этого нужны
            // сами байты.
            StorageFile saved = media.IsVideo
                ? await MediaStore.FetchLargeAsync(url, key + ".mp4", report)
                : await MediaStore.FetchMediaAsync(url, key, ".m4a", report);

            // Пока качали, могли нажать на другое.
            if (_playing != row) return;

            if (saved == null)
            {
                row.ResetMedia("не удалось загрузить");
                _playing = null;
                return;
            }

            // Opus система не играет — раскодируем сами и подсунем ей обычный
            // звук. Второй раз то же голосовое не считается: готовое остаётся
            // на диске рядом с исходным.
            if (saved.FileType == ".ogg")
            {
                row.MediaStatus = "раскодируем…";

                StorageFile sound = await OpusAudio.ConvertAsync(saved);
                if (_playing != row) return;

                if (sound == null)
                {
                    row.ResetMedia("не удалось раскодировать");
                    _playing = null;
                    return;
                }

                saved = sound;
            }

            try
            {
                Windows.Storage.Streams.IRandomAccessStream source =
                    await saved.OpenReadAsync();

                _playingFormat = saved.FileType;

                Player.SetSource(source, saved.ContentType);
                Player.Play();
            }
            catch (Exception error)
            {
                Log.Write("mem", "проигрывание не началось: " + error.Message);
                row.ResetMedia("не проигрывается");
                _playing = null;
                return;
            }

            row.MediaGlyph = GlyphPause;
            row.MediaPercent = 0;

            // Загрузка позади — полоса с процентами уступает место графику.
            row.MediaBusy = false;

            if (media.IsVideo) ShowVideo(true, row.IsNote);

            StartTicker();
        }

        private void StartTicker()
        {
            if (_ticker == null)
            {
                // Пять раз в секунду, а не два: график закрашивается долями
                // столбика, и на двух тиках это было видно как рывки. Пересбор
                // самой фигуры при этом не участился — он привязан не к тикам,
                // а к тому, сдвинулась ли закраска заметно.
                _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _ticker.Tick += OnTick;
            }

            _ticker.Start();
        }

        private void OnTick(object sender, object e)
        {
            MessageRow row = _playing;
            if (row == null) { StopPlayback(); return; }

            TimeSpan whole = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            // Перемотка, заказанная до открытия звука: длительность стала
            // известна только сейчас.
            if (_pendingSeek >= 0 && whole > TimeSpan.Zero)
            {
                Player.Position = TimeSpan.FromMilliseconds(
                    whole.TotalMilliseconds * _pendingSeek);

                _pendingSeek = -1;
            }

            TimeSpan at = Player.Position;

            row.MediaStatus = whole > TimeSpan.Zero
                ? Clock(at) + " / " + Clock(whole)
                : Clock(at);

            double part = whole > TimeSpan.Zero ? at.TotalMilliseconds / whole.TotalMilliseconds : 0;

            row.MediaPercent = part * 100.0;

            // Закраска идёт долями столбика, поэтому пересобирать её нужно
            // чаще, чем раз в столбик, — но и не на каждый тик подряд: пока
            // сдвиг меньше пятой части столбика, на глаз он неразличим.
            if (row.WaveBytes != null)
            {
                int step = (int)(part * 250);
                if (step != _wavePart)
                {
                    _wavePart = step;
                    row.WavePlayed = Formats.Wave(row.WaveBytes, part);
                }
            }

            // Кружок играет в переписке, без полосы и кнопок: сколько
            // проиграно, видно по белой дуге вокруг него.
            if (NoteRound.Visibility == Visibility.Visible)
            {
                // Круг бывает разного размера: на своём месте в переписке
                // он большой, отставленный в угол — маленький.
                double half = NoteRound.Width / 2;

                NotePlayRing.Data = Arc(part, half, half - 2.5);
            }

            if (PlayerControls.Visibility != Visibility.Visible) return;

            PlayerAt.Text = Clock(at);
            PlayerTotal.Text = whole > TimeSpan.Zero ? Clock(whole) : "—";

            // Полосу двигаем под признаком: иначе это же движение вернулось бы
            // к нам перемоткой и видео стояло бы на месте.
            _sliderSetting = true;
            PlayerSeek.Value = whole > TimeSpan.Zero
                ? at.TotalMilliseconds * 1000.0 / whole.TotalMilliseconds
                : 0;
            _sliderSetting = false;
        }

        private static string Clock(TimeSpan span)
        {
            return (int)span.TotalMinutes + ":" + span.Seconds.ToString("00");
        }

        private void OnPlayerEnded(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void OnPlayerFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Log.Write("mem", "проигрывание сорвалось: " + e.ErrorMessage);

            MessageRow row = _playing;
            _playing = null;

            // Ogg — это Opus, а его в Windows Phone 8.1 нет вовсе. Отказ тут
            // не наша неполадка, и написать об этом стоит прямо: иначе
            // «не проигрывается» выглядит поломкой приложения.
            string why = _playingFormat == ".ogg"
                ? "формат Opus не поддерживается системой"
                : "не проигрывается";

            StopPlayback();
            if (row != null) row.ResetMedia(why);
        }

        private void OnPlayerBackdropTapped(object sender, TappedRoutedEventArgs e)
        {
            ShowChrome();
        }

        #region Управление видео

        /// <summary>Через столько без нажатий кнопки убираются с экрана.</summary>
        private static readonly TimeSpan ChromeIdle = TimeSpan.FromSeconds(4);

        private DispatcherTimer _chromeTimer;

        /// <summary>
        /// Полосу двигаем и мы сами, и человек. Признак отличает одно от
        /// другого: без него наше же обновление воспринималось бы как
        /// перемотка и дёргало бы видео на каждом тике.
        /// </summary>
        private bool _sliderSetting;

        private void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
        {
            if (PlayerChrome.Visibility == Visibility.Visible) HideChrome();
            else ShowChrome();
        }

        /// <summary>
        /// Нажатие по самим кнопкам дальше не идёт: иначе оно тут же свернуло
        /// бы то, по чему нажали.
        /// </summary>
        private void OnChromeTapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            RestartIdle();
        }

        private void ShowChrome()
        {
            PlayerChrome.Visibility = Visibility.Visible;
            RestartIdle();
        }

        private void HideChrome()
        {
            PlayerChrome.Visibility = Visibility.Collapsed;
            if (_chromeTimer != null) _chromeTimer.Stop();
        }

        private void RestartIdle()
        {
            if (_chromeTimer == null)
            {
                _chromeTimer = new DispatcherTimer { Interval = ChromeIdle };
                _chromeTimer.Tick += OnIdle;
            }

            _chromeTimer.Stop();
            _chromeTimer.Start();
        }

        private void OnIdle(object sender, object e)
        {
            _chromeTimer.Stop();

            // На паузе кнопки не прячем: человек их и оставил, чтобы видеть.
            if (Player.CurrentState == MediaElementState.Paused) return;

            HideChrome();
        }

        private void OnPlayerClose(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void OnTogglePlay(object sender, RoutedEventArgs e)
        {
            if (Player.CurrentState == MediaElementState.Playing)
            {
                Player.Pause();
                PlayerToggle.Content = "";
                if (_playing != null) _playing.MediaGlyph = GlyphPlay;
            }
            else
            {
                Player.Play();
                PlayerToggle.Content = "";
                if (_playing != null) _playing.MediaGlyph = GlyphPause;
            }

            RestartIdle();
        }

        private void OnRewind(object sender, RoutedEventArgs e)
        {
            Shift(TimeSpan.FromSeconds(-5));
        }

        private void OnForward(object sender, RoutedEventArgs e)
        {
            Shift(TimeSpan.FromSeconds(10));
        }

        private void Shift(TimeSpan step)
        {
            TimeSpan whole = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            TimeSpan at = Player.Position + step;

            if (at < TimeSpan.Zero) at = TimeSpan.Zero;
            if (whole > TimeSpan.Zero && at > whole) at = whole;

            Player.Position = at;

            RestartIdle();
        }

        private void OnSeekChanged(object sender,
                                   Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Наше же обновление полосы перемоткой не считаем.
            if (_sliderSetting) return;

            TimeSpan whole = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            if (whole <= TimeSpan.Zero) return;

            Player.Position = TimeSpan.FromMilliseconds(
                whole.TotalMilliseconds * e.NewValue / 1000.0);

            RestartIdle();
        }

        #endregion

        /// <summary>
        /// Видео разворачивается на весь экран, звук — нет: у голосового
        /// смотреть нечего, и накрывать переписку чёрным незачем.
        /// </summary>
        /// <summary>
        /// Показать ролик. Кружок — иначе всех прочих: не на весь экран, а
        /// на своём месте в переписке, кругом. Разворачивать его во весь
        /// экран было бы странно: он и снят затем, чтобы быть маленьким.
        /// </summary>
        private void ShowVideo(bool shown, bool note)
        {
            if (shown && note)
            {
                ShowNoteRound();
                return;
            }

            HideNoteRound();

            // Обычному видео обрезка не нужна вовсе, и спрятанным ему быть
            // не с чего.
            if (shown) PlayerBounds.Clip = null;

            PlayerBackdrop.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

            Player.Opacity = shown ? 1 : 0;

            // Во весь экран — растяжкой, а не числами. Числа брались от
            // окна один раз, при открытии, и после поворота оставались от
            // прежней стороны: видео либо не доставало до края, либо
            // вылезало за него. Растяжка же пересчитывается сама, потому
            // что слой проигрывателя занимает страницу целиком.
            Player.Width = shown ? double.NaN : 1;
            Player.Height = shown ? double.NaN : 1;

            Player.HorizontalAlignment = shown
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Center;

            Player.VerticalAlignment = shown
                ? VerticalAlignment.Stretch
                : VerticalAlignment.Center;

            PlayerControls.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

            // Полоса команд чата живёт отдельным слоем поверх страницы, и
            // накрыть её содержимым страницы нельзя — «сведения», «обновить» и
            // «разметка» так и торчали поверх видео. Убираем её на время
            // просмотра.
            if (BottomAppBar != null)
            {
                BottomAppBar.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
            }

            if (shown)
            {
                PlayerToggle.Content = "";
                ShowChrome();
            }
            else
            {
                HideChrome();
            }
        }

        /// <summary>
        /// Остановить и всё за собой убрать. Зовётся и при уходе со страницы:
        /// оставленный источник держит за собой файл и память.
        /// </summary>
        private void StopPlayback()
        {
            if (_ticker != null) _ticker.Stop();

            MessageRow row = _playing;
            _playing = null;
            _wavePart = -1;
            _pendingSeek = -1;

            if (row != null) row.WavePlayed = null;

            try
            {
                Player.Stop();
                Player.Source = null;
            }
            catch (Exception) { }

            ShowVideo(false, false);

            if (row != null) row.ResetMedia(row.MediaTitle == null ? "" : DurationOf(row));
        }

        /// <summary>
        /// Поставить проигрыватель на место кружка и накрыть его кругом.
        ///
        /// Проигрыватель лежит прямо в странице, а не в списке, поэтому
        /// место ему назначается числами: где сейчас пузырь, туда и встаёт.
        /// Прокрутка списка это место сдвигает — отсюда и поправка при
        /// каждом её движении.
        /// </summary>
        private void ShowNoteRound()
        {
            ShowNoteRound(false);
        }

        /// <summary>
        /// Поставить играющий кружок на его место в переписке — или в угол,
        /// если места этого сейчас не видно.
        ///
        /// В углу он оказывается в двух случаях: сообщение с ним ушло за
        /// край экрана и список летит. Второе не прихоть: видео рисуется
        /// своим слоем, помимо разметки, и на рывке отстаёт от неё на
        /// кадр-другой — закраска углов уезжает вперёд, и от круга остаётся
        /// линза. В углу же он стоит на месте, и отставать ему не от чего.
        /// </summary>
        private void ShowNoteRound(bool rushing)
        {
            FrameworkElement frame = NoteFrameOf(_playing);
            Rect seen = ListPlace();

            if (frame == null || frame.ActualWidth <= 0)
            {
                ParkNoteRound(seen);
                return;
            }

            Point at = frame.TransformToVisual(LayoutRoot).TransformPoint(new Point());

            var mine = new Rect(at.X, at.Y, frame.ActualWidth, frame.ActualHeight);
            mine.Intersect(seen);

            // Больше половины кружка на экране — стоит на своём месте.
            // Меньше — лучше целиком в углу, чем срезанным у самого края.
            bool shown = !mine.IsEmpty && mine.Height > frame.ActualHeight / 2;

            if (!shown || rushing)
            {
                ParkNoteRound(seen);
                return;
            }

            _parked = false;
            PlaceNoteRound(at, frame.ActualWidth, frame.ActualHeight, seen);
        }

        /// <summary>Отставить кружок в верхний правый угол переписки.</summary>
        private void ParkNoteRound(Rect seen)
        {
            if (seen.IsEmpty)
            {
                HideNoteRound();
                return;
            }

            _parked = true;

            var at = new Point(seen.Right - ParkSide - 16, seen.Top + 16);
            PlaceNoteRound(at, ParkSide, ParkSide, seen);
        }

        /// <summary>Положить проигрыватель и его закраску по заданному месту.</summary>
        private void PlaceNoteRound(Point at, double wide, double tall, Rect seen)
        {
            var place = new Thickness(at.X, at.Y, 0, 0);

            Player.HorizontalAlignment = HorizontalAlignment.Left;
            Player.VerticalAlignment = VerticalAlignment.Top;
            Player.Margin = place;
            Player.Width = wide;
            Player.Height = tall;
            Player.Opacity = 1;

            NoteRound.Margin = place;
            NoteRound.Width = wide;
            NoteRound.Height = tall;
            NoteRound.Visibility = Visibility.Visible;

            RoundThePlayer(at, wide, tall);

            // За края списка круг вылезать не должен: выше него шапка, ниже
            // строка ввода, и проигрыватель лёг бы поверх обеих.
            var mine = new Rect(at.X, at.Y, wide, tall);
            mine.Intersect(seen);

            if (mine.IsEmpty)
            {
                Player.Opacity = 0;
                PlayerBounds.Clip = Nothing();
                NoteRound.Visibility = Visibility.Collapsed;
                return;
            }

            // Границы списка режет внешний слой, а не сам проигрыватель:
            // тот повёрнут, и его собственная обрезка повернулась бы вместе
            // с ним — не по краю списка, а наискось.
            PlayerBounds.Clip = new Windows.UI.Xaml.Media.RectangleGeometry { Rect = mine };

            NoteRound.Clip = new Windows.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Rect(mine.X - at.X, mine.Y - at.Y, mine.Width, mine.Height)
            };
        }

        /// <summary>Сторона кружка, отставленного в угол.</summary>
        private const double ParkSide = 128;



        /// <summary>Стоит ли кружок в углу, а не на своём месте в переписке.</summary>
        private bool _parked;

        /// <summary>
        /// Обрезать проигрыватель в круг по заданному месту.
        ///
        /// Каждому слою — тот же квадрат обрезкой и поворот на шаг вокруг
        /// общей середины; в пересечении остаётся многоугольник, который
        /// на этих размерах от круга не отличить. Слои поворачивают и
        /// содержимое — накопленный поворот снимается обратным на самом
        /// проигрывателе, вокруг его собственной середины.
        /// </summary>
        private void RoundThePlayer(Point at, double wide, double tall)
        {
            double middleX = at.X + wide / 2;
            double middleY = at.Y + tall / 2;

            Grid[] layers = PlayerLayers();

            for (int layer = 0; layer < layers.Length; layer++)
            {
                layers[layer].Clip = new Windows.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Rect(at.X, at.Y, wide, tall)
                };

                layers[layer].RenderTransform = new Windows.UI.Xaml.Media.RotateTransform
                {
                    CenterX = middleX,
                    CenterY = middleY,
                    Angle = layer == 0 ? 0 : RoundStep
                };
            }

            Player.RenderTransform = new Windows.UI.Xaml.Media.RotateTransform
            {
                CenterX = wide / 2,
                CenterY = tall / 2,
                Angle = -RoundStep * (layers.Length - 1)
            };
        }

        /// <summary>Снять обрезку — под обычным видео она только мешает.</summary>
        private void StraightenThePlayer()
        {
            foreach (Grid layer in PlayerLayers())
            {
                layer.Clip = null;
                layer.RenderTransform = null;
            }

            Player.RenderTransform = null;
            PlayerBounds.Clip = null;
        }

        /// <summary>
        /// Шаг поворота между слоями обрезки. Шестнадцать слоёв через
        /// 5,625° — это шестидесятичетырёхугольник: грань отходит от круга
        /// на 0,1 точки при большом кружке и вдвое меньше при малом.
        /// </summary>
        private const double RoundStep = 90.0 / 16;

        private Grid[] _playerLayers;

        private Grid[] PlayerLayers()
        {
            return _playerLayers ?? (_playerLayers = new[]
            {
                PlayerClip0, PlayerClip1, PlayerClip2, PlayerClip3,
                PlayerClip4, PlayerClip5, PlayerClip6, PlayerClip7,
                PlayerClip8, PlayerClip9, PlayerClip10, PlayerClip11,
                PlayerClip12, PlayerClip13, PlayerClip14, PlayerClip15
            });
        }

        /// <summary>Обрезка, за которой не остаётся ничего.</summary>
        private static Windows.UI.Xaml.Media.RectangleGeometry Nothing()
        {
            return new Windows.UI.Xaml.Media.RectangleGeometry { Rect = new Rect(0, 0, 0, 0) };
        }

        /// <summary>Где на странице лежит сам список сообщений.</summary>
        private Rect ListPlace()
        {
            Point at = MessagesList.TransformToVisual(LayoutRoot).TransformPoint(new Point());

            return new Rect(at.X, at.Y, MessagesList.ActualWidth, MessagesList.ActualHeight);
        }

        private void HideNoteRound()
        {
            Player.Opacity = 0;

            NotePlayRing.Data = null;
            StraightenThePlayer();

            // Картинку убираем обрезкой в ничто, а не прозрачностью: видео
            // рисуется своим слоем, и прозрачности оно не слушается —
            // спрятанный ею кадр так и стоял на экране. Обрезка же
            // делается там же, где рисуется видео, и её оно слушается.
            // Звук при этом не прерывается: замолкает проигрыватель только
            // свёрнутый.
            PlayerBounds.Clip = Nothing();

            NoteRound.Visibility = Visibility.Collapsed;
            NoteRound.Clip = null;

            Player.Clip = null;
            Player.Margin = new Thickness();
            Player.HorizontalAlignment = HorizontalAlignment.Center;
            Player.VerticalAlignment = VerticalAlignment.Center;
        }

        /// <summary>Рамка кадра у строки — по ней и ставится круг.</summary>
        private FrameworkElement NoteFrameOf(MessageRow row)
        {
            if (row == null || !row.IsNote) return null;

            var box = MessagesList.ContainerFromItem(row) as DependencyObject;

            return box != null ? Named(box, "PhotoFrame") : null;
        }

        /// <summary>
        /// Найти в дереве часть с таким именем. Внутри списка имена не
        /// ищутся обычным способом: каждая строка собирается по образцу
        /// заново, и своего имени у её частей на странице нет.
        /// </summary>
        private static FrameworkElement Named(DependencyObject box, string name)
        {
            var element = box as FrameworkElement;
            if (element != null && element.Name == name) return element;

            int count = VisualTreeHelper.GetChildrenCount(box);

            for (int at = 0; at < count; at++)
            {
                FrameworkElement found = Named(VisualTreeHelper.GetChild(box, at), name);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>Нажатие по играющему кружку — остановить и пустить снова.</summary>
        private void OnNoteRoundTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_playing == null) return;

            // Отставленный в угол кружок — это не столько картинка, сколько
            // указатель: нажатие возвращает к его сообщению, а не
            // останавливает его.
            if (_parked)
            {
                ScrollTo(_playing.MessageId);
                return;
            }

            PlayRow(_playing);
        }

        /// <summary>Подпись плашки в покое — длительность, если она известна.</summary>
        private string DurationOf(MessageRow row)
        {
            MaxMessage message = Find(row.MessageId);
            MaxAttachment media = message != null ? message.Playable : null;

            return media != null ? media.DurationText : "";
        }

        #endregion

        /// <summary>
        /// Нажатие кнопки под сообщением бота. Кнопка-ссылка открывается
        /// браузером, остальные уходят боту коллбэком: он отвечает либо новым
        /// сообщением — оно приедет пушем само, — либо коротким текстом,
        /// который надо показать здесь же.
        /// </summary>
        private async void OnInlineButton(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var button = element != null ? element.DataContext as InlineButton : null;
            if (button == null) return;

            if (button.IsLink)
            {
                Uri address;
                if (!Uri.TryCreate(button.Url, UriKind.Absolute, out address)) return;

                await Windows.System.Launcher.LaunchUriAsync(address);
                return;
            }

            if (string.IsNullOrEmpty(button.CallbackId)) return;

            string answer = await MessagesModule.Instance.SendCallbackAsync(
                _chatId, button.MessageId, button.CallbackId, button.Payload);

            string refusal = MessagesModule.Instance.LastError;
            if (!string.IsNullOrEmpty(refusal)) await new MessageDialog(refusal).ShowAsync();
            else if (!string.IsNullOrEmpty(answer)) await new MessageDialog(answer).ShowAsync();
        }

        /// <summary>Долгое нажатие: ответить, закрепить, удалить.</summary>
        /// <summary>
        /// Меню сообщения. Пункты набираются с ярлыками, а выбранное делается
        /// уже после того, как меню закрылось.
        ///
        /// Так приходится потому, что второе меню из обработчика первого
        /// показать нельзя: всплывающее меню в Windows Phone 8.1 на экране
        /// одно, и попытка открыть из обработчика выбор реакции роняла
        /// приложение целиком. Дождавшись закрытия, показывать можно что
        /// угодно.
        /// </summary>
        /// <summary>
        /// Правая кнопка мыши — то же меню, что и удержание.
        ///
        /// Касание сюда не пускаем: у пальца удержание своё, и оно уже
        /// открыло меню, пока палец держали. Пустить бы и его — меню
        /// открывалось бы дважды, на удержании и на отпускании.
        /// </summary>
        private async void OnMessageRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Touch) return;

            e.Handled = true;
            await ShowMessageMenuAsync(sender, e.GetPosition(null));
        }

        private async void OnMessageHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;

            await ShowMessageMenuAsync(sender, e.GetPosition(null));
        }

        /// <summary>Меню сообщения — общее для удержания и правой кнопки.</summary>
        private async Task ShowMessageMenuAsync(object sender, Point where)
        {

            var element = sender as FrameworkElement;
            var row = element != null ? element.DataContext as MessageRow : null;
            if (row == null) return;

            MaxMessage message = Find(row.MessageId);
            if (message == null) return;

            bool own = message.SenderId == ChatsModule.Instance.CurrentUserId;
            bool admin = _chat != null && _chat.IsAdmin(ChatsModule.Instance.CurrentUserId);
            bool pinned = _chat != null && _chat.PinnedMessageId.ToString() == message.MessageId;

            var menu = new PopupMenu();

            // Ответить можно только там, где нам вообще дают писать. В канале
            // без прав ответ уходил в никуда, и по виду чата было не понять,
            // кому он адресован, — предлагать его там незачем.
            if (_chat == null || _chat.CanWrite(ChatsModule.Instance.CurrentUserId))
            {
                menu.Commands.Add(new UICommand("ответить") { Id = "reply" });
            }

            menu.Commands.Add(new UICommand("переслать") { Id = "forward" });

            menu.Commands.Add(new UICommand(
                string.IsNullOrEmpty(message.YourReaction) ? "реакция" : "сменить реакцию")
                { Id = "react" });

            // Копирования в этом меню нет намеренно: буфера обмена в наборе
            // Windows Phone 8.1 не существует вовсе — текст копируют
            // выделением в самом поле ввода.

            // Закреп существует только в группах и каналах: в личной переписке
            // сервер на CHAT_UPDATE отвечает `not.found: message`, что бы мы
            // ему ни прислали. Пункт, который заведомо не сработает, лучше не
            // показывать вовсе.
            if (_chat != null && !_chat.IsDialog && admin)
            {
                menu.Commands.Add(new UICommand(pinned ? "открепить" : "закрепить") { Id = "pin" });
            }

            // Править можно только своё и только словами: у сообщения без
            // текста править нечего, а вложение сервер этим запросом не
            // меняет. Насколько давнее сообщение ещё разрешено трогать,
            // решает он же — отказ покажем.
            if (own && !message.IsService && !string.IsNullOrEmpty(message.Text))
            {
                menu.Commands.Add(new UICommand("изменить") { Id = "edit" });
            }

            if (own) menu.Commands.Add(new UICommand("удалить у всех") { Id = "delete" });

            IUICommand chosen = await menu.ShowAsync(where);
            string what = chosen != null ? chosen.Id as string : null;

            switch (what)
            {
                case "reply":
                    BeginReply(message);
                    return;

                case "forward":
                    Frame.Navigate(typeof(ChatPickerPage), new ChatPickerPage.Target
                    {
                        SourceChatId = _chatId,
                        MessageId = message.MessageId
                    });
                    return;

                case "react":
                    ShowReactions(message);
                    return;

                case "edit":
                    BeginEdit(message);
                    return;

                case "pin":
                    await PinAsync(message, pinned);
                    return;

                case "delete":
                    await DeleteAsync(message);
                    return;
            }
        }

        private void BeginReply(MaxMessage message)
        {
            _replyToId = message.MessageId;
            _editingId = null;

            ReplyKind.Text = "Ответ на";
            ReplyText.Text = Formats.OneLine(message.DisplayText);
            ReplyBar.Visibility = Visibility.Visible;

            InputBox.Focus(FocusState.Programmatic);
            UpdateSendButton();
        }

        private async Task PinAsync(MaxMessage message, bool pinned)
        {
            long id = 0;
            if (!pinned) long.TryParse(message.MessageId, out id);

            // Отказ показываем. Раньше кнопка молчала, и со стороны это
            // выглядело так, будто она не работает вовсе, — хотя сервер
            // отвечал и объяснял причину.
            if (!await ChatsModule.Instance.SetPinnedMessageAsync(id, _chatId, true))
            {
                var refused = new MessageDialog(
                    ChatsModule.Instance.LastError ?? "Не удалось закрепить сообщение.");
                await refused.ShowAsync();
                return;
            }

            UpdateHeader();
        }

        private async Task DeleteAsync(MaxMessage message)
        {
            var ids = new List<object> { message.MessageId };

            if (!await MessagesModule.Instance.DeleteAsync(_chatId, ids, true))
            {
                var refused = new MessageDialog("Не удалось удалить сообщение.");
                await refused.ShowAsync();
                return;
            }

            _messages.Remove(message);
            Rebuild();
            KeepPosition();
        }

        /// <summary>
        /// Лица для реакции — те же шесть, что предлагает Komet, и в том же
        /// виде: у сервера свой список разрешённых, и лицо не из него он
        /// отвергает. Набор нарочно короткий — в строку их влезает немного,
        /// а редкое лицо всё равно пришлось бы искать.
        /// </summary>
        private static readonly string[] Faces =
        {
            "\U0001F44D", "\u2764\uFE0F", "\U0001F525",
            "\U0001F923", "\U0001F62D", "\U0001F60D"
        };

        /// <summary>
        /// Лица, которых сервер не принял. Помним до перезапуска: кнопка,
        /// которая всякий раз отвечает отказом, хуже отсутствующей.
        /// </summary>
        private static readonly HashSet<string> Refused = new HashSet<string>();

        /// <summary>Кому ставим реакцию, пока открыт слой выбора.</summary>
        private MaxMessage _reacting;

        private void ShowReactions(MaxMessage message)
        {
            _reacting = message;

            ReactionFaces.Children.Clear();

            foreach (string face in Faces)
            {
                if (Refused.Contains(face)) continue;

                // Своё лицо каждой кнопке: без этой переменной все они
                // достались бы последнему из списка.
                string chosen = face;

                var button = new Button
                {
                    Content = chosen,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 26,
                    MinWidth = 0,
                    Padding = new Thickness(10, 2, 10, 8),
                    Margin = new Thickness(2, 0, 2, 0)
                };

                button.Click += async (source, argument) =>
                {
                    HideReactions();
                    await ReactAsync(message, chosen);
                };

                ReactionFaces.Children.Add(button);
            }

            ReactionClear.Visibility = string.IsNullOrEmpty(message.YourReaction)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Ни одного лица не осталось и снимать нечего — показывать пустую
            // панель незачем.
            if (ReactionFaces.Children.Count == 0 && ReactionClear.Visibility == Visibility.Collapsed)
            {
                _reacting = null;

                var none = new MessageDialog("Реакции здесь не принимаются.");
                var ignored = none.ShowAsync();

                return;
            }

            ReactionLayer.Visibility = Visibility.Visible;
        }

        private void HideReactions()
        {
            ReactionLayer.Visibility = Visibility.Collapsed;
            _reacting = null;
        }

        /// <summary>Мимо панели — значит, передумали.</summary>
        private void OnReactionBackdropTapped(object sender, TappedRoutedEventArgs e)
        {
            HideReactions();
        }

        /// <summary>
        /// Нажатие по самой панели дальше не идёт: иначе оно дошло бы до
        /// подложки и закрыло выбор, едва его открыв.
        /// </summary>
        private void OnReactionPanelTapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void OnReactionClear(object sender, RoutedEventArgs e)
        {
            MaxMessage message = _reacting;
            HideReactions();

            if (message != null) await ReactAsync(message, null);
        }

        /// <summary>Поставить реакцию или снять свою — пустое лицо снимает.</summary>
        private async Task ReactAsync(MaxMessage message, string face)
        {
            MessagesModule.ReactionResult result = face == null
                ? await MessagesModule.Instance.CancelReactionAsync(_chatId, message.MessageId)
                : await MessagesModule.Instance.SetReactionAsync(_chatId, message.MessageId, face);

            if (!result.Ok)
            {
                // Отказал сам сервер — значит, это лицо здесь не принимают
                // ни сейчас, ни потом: больше его не предлагаем.
                if (result.Answered && face != null) Refused.Add(face);

                string refusal = MessagesModule.Instance.LastError ?? "Не удалось поставить реакцию.";
                await new MessageDialog(refusal).ShowAsync();

                return;
            }

            // Сервер отвечает всеми реакциями сообщения разом. Если ответ
            // пришёл пустым, ставим свою на глаз: счётчики довезёт пуш.
            if (result.Info != null) message.ApplyReactions(result.Info);
            else message.YourReaction = face;

            Refresh(message);
        }

        /// <summary>
        /// Начать правку: старый текст встаёт в поле ввода, а полоска над
        /// ним объясняет, что происходит не ответ.
        /// </summary>
        private void BeginEdit(MaxMessage message)
        {
            _editingId = message.MessageId;
            _replyToId = null;
            _markup.Clear();

            ReplyKind.Text = "Правка";
            ReplyText.Text = Formats.OneLine(message.Text);
            ReplyBar.Visibility = Visibility.Visible;

            InputBox.Text = message.Text ?? "";
            InputBox.SelectionStart = InputBox.Text.Length;
            InputBox.Focus(FocusState.Programmatic);

            UpdateSendButton();
        }

        /// <summary>
        /// Своя реакция отмечается прямо в строке: панели с лицами здесь
        /// нет, и иначе не понять, ставил ли её ты сам.
        /// </summary>
        private static string ReactionLine(MaxMessage message)
        {
            string line = message.ReactionsSummary;
            if (string.IsNullOrEmpty(line)) return line;

            return string.IsNullOrEmpty(message.YourReaction) ? line : line + "  · вы";
        }

        /// <summary>
        /// Поправить один пузырь, не пересобирая список.
        ///
        /// Пересборка опустошает список, прокрутка сваливается в начало, и
        /// её приходится возвращать на прежнее место — со стороны это
        /// подскок. Здесь же меняется ровно то, что изменилось: текст после
        /// правки да строка реакций.
        ///
        /// Высота пузыря при этом всё равно меняется, а прокрутка остаётся
        /// на прежнем числе — поэтому, если стояли в самом конце, доводим
        /// туда снова.
        /// </summary>
        private void Refresh(MaxMessage message)
        {
            MessageRow row = Row(message.MessageId);
            if (row == null) return;

            bool wasAtEnd = AtEnd();

            row.Text = message.DisplayText;

            // В пузыре показан размеченный текст, а не сам `Text`: перебрать
            // его заново обязательно, иначе на экране останется прежнее.
            // Разметка накладывается только на настоящий текст сообщения —
            // на подпись вместо него («Фото», «Файл: …») отрезки указывали
            // бы мимо.
            row.Formatted = new RichString(row.Text,
                row.Text == message.Text ? TextRange.Parse(message.Elements) : null);

            row.Time = Formats.ShortTime(message.Time) + (message.IsEdited ? " · изм." : "");
            row.Reactions = ReactionLine(message);

            if (wasAtEnd) ScrollToEnd();
        }

        /// <summary>Стоит ли прокрутка в самом конце — с точностью до волоска.</summary>
        private bool AtEnd()
        {
            ScrollViewer scroll = _scroll ?? FindScrollViewer(MessagesList);
            if (scroll == null) return false;

            return scroll.ScrollableHeight - scroll.VerticalOffset < 8;
        }

        private MessageRow Row(string messageId)
        {
            foreach (MessageRow row in _rows)
            {
                if (row.MessageId == messageId) return row;
            }
            return null;
        }

        private MaxMessage Find(string messageId)
        {
            foreach (MaxMessage message in _messages)
            {
                if (message.MessageId == messageId) return message;
            }
            return null;
        }

        private void OnPinnedTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_chat == null || _chat.PinnedMessageId == 0) return;

            // Закреп открывается тем же путём, что и найденное сообщение:
            // окно истории вокруг него.
            Frame.Navigate(typeof(ChatPage), new Target
            {
                ChatId = _chatId,
                MessageId = _chat.PinnedMessageId.ToString(),
                MessageTime = _chat.PinnedMessageTime
            });
        }

        #endregion

        #region Отправка

        /// <summary>
        /// Разметка на выделенное. Ссылку отдельным пунктом не делаю: адрес
        /// в тексте находится сам при отправке, а размечать ссылкой слово,
        /// адресом не являющееся, незачем.
        /// </summary>
        private void OnMarkup(object sender, RoutedEventArgs e)
        {
            int start = InputBox.SelectionStart;
            int length = InputBox.SelectionLength;

            if (length <= 0)
            {
                var hint = new MessageDialog("Выделите в сообщении то, что нужно разметить.");
                var ignoredHint = hint.ShowAsync();
                return;
            }

            string covered = InputBox.Text.Substring(start, length);

            var menu = new PopupMenu();

            menu.Commands.Add(new UICommand("жирным", command => Mark("STRONG", start, covered)));
            menu.Commands.Add(new UICommand("наклонным", command => Mark("EMPHASIZED", start, covered)));
            menu.Commands.Add(new UICommand("подчёркнутым", command => Mark("UNDERLINE", start, covered)));
            menu.Commands.Add(new UICommand("моноширинным", command => Mark("MONOSPACED", start, covered)));

            if (_markup.Count > 0)
            {
                menu.Commands.Add(new UICommand("снять всю разметку", command => _markup.Clear()));
            }

            var ignored = menu.ShowAsync(new Point(0, 0));
        }

        private void Mark(string type, int start, string covered)
        {
            _markup.Add(new Markup.Applied { Type = type, Start = start, Covered = covered });
        }

        /// <summary>
        /// Крестик на полоске: отменяет и ответ, и правку — полоска у них
        /// одна. Набранное при этом остаётся: стереть его человек может и
        /// сам, а вот вернуть — уже нет.
        /// </summary>
        private void OnCancelReply(object sender, RoutedEventArgs e)
        {
            _replyToId = null;
            _editingId = null;

            ReplyKind.Text = "Ответ на";
            ReplyBar.Visibility = Visibility.Collapsed;

            UpdateSendButton();
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSendButton();

            if (InputBox.Text.Length == 0) return;
            MessagesModule.Instance.SendTyping(_chatId);
        }

        private async void OnSend(object sender, RoutedEventArgs e)
        {
            await SendAsync();
        }

        private async void OnInputKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;

            e.Handled = true;
            await SendAsync();
        }

        private async Task SendAsync()
        {
            string text = InputBox.Text.Trim();
            if (text.Length == 0) return;

            string replyTo = _replyToId;
            string editing = _editingId;

            // Разметка собирается до очистки поля: наложенное руками
            // сверяется с текстом, ссылки в нём находятся сами.
            List<object> elements = Markup.Build(text, _markup);

            InputBox.Text = "";
            SendButton.IsEnabled = false;
            UpdateSendButton();
            OnCancelReply(null, null);

            if (editing != null)
            {
                await ApplyEditAsync(editing, text, elements);
                return;
            }

            MaxMessage sent = await MessagesModule.Instance.SendAsync(_chatId, text, replyTo, elements);
            SendButton.IsEnabled = true;

            if (sent == null)
            {
                // Текст возвращается в поле: потерять написанное из-за обрыва
                // связи — худшее, что может сделать мессенджер.
                InputBox.Text = text;

                // Отказ бывает не про связь, а по существу: бот, закрытый для
                // писем, или чат, где писать запрещено. Молчаливая кнопка в
                // таком случае выглядит поломкой.
                string refusal = MessagesModule.Instance.LastError;
                if (!string.IsNullOrEmpty(refusal)) await new MessageDialog(refusal).ShowAsync();

                return;
            }

            _markup.Clear();

            if (!Contains(sent.MessageId)) _messages.Add(sent);
            ChatsModule.Instance.NoteMessage(sent);

            Rebuild();
            ScrollToEnd();
        }

        /// <summary>
        /// Отправить правку. Новое сообщение сервер в ответ не присылает —
        /// правленое исправляем у себя сами, а собеседнику о том же скажет
        /// пуш.
        /// </summary>
        private async Task ApplyEditAsync(string messageId, string text, List<object> elements)
        {
            bool done = await MessagesModule.Instance.EditAsync(_chatId, messageId, text, elements);
            SendButton.IsEnabled = true;

            if (!done)
            {
                // Возвращаем набранное вместе с самой правкой: иначе
                // отказ сервера означал бы, что текст набирают заново.
                MaxMessage lost = Find(messageId);
                if (lost != null) BeginEdit(lost);

                InputBox.Text = text;
                InputBox.SelectionStart = InputBox.Text.Length;
                UpdateSendButton();

                string refusal = MessagesModule.Instance.LastError ?? "Не удалось изменить сообщение.";
                await new MessageDialog(refusal).ShowAsync();

                return;
            }

            _markup.Clear();

            MaxMessage changed = Find(messageId);
            if (changed == null) return;

            changed.Text = text;
            changed.Elements = elements;
            changed.IsEdited = true;

            Refresh(changed);
        }

        /// <summary>
        /// Скрепка: фотография или файл. Что именно — спрашивается здесь же,
        /// двум кнопкам в строке ввода места нет.
        /// </summary>
        private void OnAttach(object sender, RoutedEventArgs e)
        {
            var menu = new PopupMenu();

            menu.Commands.Add(new UICommand("фотография",
                async command => await AttachAsync(true)));

            menu.Commands.Add(new UICommand("файл",
                async command => await AttachAsync(false)));

            menu.Commands.Add(new UICommand("стикер",
                async command => await SendStickerAsync()));

            // Меню открывается у самой скрепки, а не у пальца: нажатие было
            // по кнопке, и список должен вырасти из неё.
            Point origin = AttachButton.TransformToVisual(null).TransformPoint(new Point());
            var area = new Rect(origin.X, origin.Y, AttachButton.ActualWidth, AttachButton.ActualHeight);

            var ignored = menu.ShowForSelectionAsync(area);
        }

        /// <summary>
        /// Больше этого снимок в память не берём. Фотографий такого объёма
        /// не бывает — а вот ролик, выбранный как снимок, бывает.
        /// </summary>
        private const long MostPhoto = 24 * 1024 * 1024;

        /// <summary>
        /// Выбор, заливка и отправка. Подписью к вложению уходит то, что
        /// набрано в строке ввода, — отдельным сообщением её слать не надо.
        /// </summary>
        private async Task AttachAsync(bool photo)
        {
            // Фотографию берём системным выбором — он для снимков и сделан,
            // и показывает их плитками. Файл выбирается тем, что умеет эта
            // система: на «десятке» настоящим проводником, на 8.1 — своим
            // обходом папок.
            StorageFile file = photo
                ? await FilePicking.PickPhotoAsync()
                : await FilePicking.PickFileAsync();

            if (file == null) return;

            // Размер спрашивается заранее, до чтения. Фотографию мы читаем
            // целиком — под форму multipart её всё равно надо собрать в
            // памяти, — а файл уходит прямо с диска: ролик на полтораста
            // мегабайт, прочитанный в память, уносил приложение целиком.
            long size = 0;

            try
            {
                var about = await file.GetBasicPropertiesAsync();
                size = (long)about.Size;
            }
            catch (Exception error)
            {
                Log.Write("chat", "не спросить размер вложения: " + error.Message);
            }

            if (size <= 0) return;

            byte[] data = null;

            if (photo)
            {
                if (size > MostPhoto)
                {
                    await new MessageDialog("Снимок слишком велик — отправьте его файлом.")
                        .ShowAsync();
                    return;
                }

                data = await FilePicking.ReadAsync(file);
                if (data == null || data.Length == 0) return;

                size = data.Length;
            }

            string caption = InputBox.Text.Trim();
            string replyTo = _replyToId;

            // Подпись к вложению — такое же сообщение, и разметка в ней
            // работает так же.
            List<object> elements = Markup.Build(caption, _markup);

            InputBox.Text = "";
            OnCancelReply(null, null);

            AttachButton.IsEnabled = false;
            SendButton.IsEnabled = false;

            // Полоса со счётом, а не бегущая: отправка мегабайта по мобильной
            // связи идёт долго, и без чисел непонятно, движется ли она вообще.
            ShowUpload(0, size);

            MediaHttp.Progress report = (done, total) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                                                  () => ShowUpload(done, total));
            };

            MaxMessage sent = photo
                ? await MessagesModule.Instance.SendPhotoAsync(
                      _chatId, data, file.Name, caption, replyTo, elements, report)
                : await MessagesModule.Instance.SendFileAsync(
                      _chatId, file, size, file.Name, caption, replyTo, elements, report);

            HideUpload();
            AttachButton.IsEnabled = true;
            SendButton.IsEnabled = true;

            if (sent == null)
            {
                // Подпись возвращается в поле — как и при неудачной отправке
                // обычного сообщения.
                InputBox.Text = caption;

                string refusal = MessagesModule.Instance.LastError
                                 ?? Uploader.LastError
                                 ?? (photo ? "Не удалось отправить фотографию."
                                           : "Не удалось отправить файл.");

                await new MessageDialog(refusal).ShowAsync();
                return;
            }

            if (!Contains(sent.MessageId)) _messages.Add(sent);
            ChatsModule.Instance.NoteMessage(sent);

            Rebuild();
            ScrollToEnd();
        }

        /// <summary>
        /// Выбор и отправка стикера. Заливать нечего — он уже на сервере,
        /// поэтому и полосы с процентами здесь нет.
        /// </summary>
        private async Task SendStickerAsync()
        {
            MaxSticker chosen = await StickerPicker.ShowAsync();
            if (chosen == null) return;

            SendButton.IsEnabled = false;

            MaxMessage sent = await MessagesModule.Instance.SendStickerAsync(
                _chatId, chosen.StickerId);

            SendButton.IsEnabled = true;

            if (sent == null)
            {
                string refusal = MessagesModule.Instance.LastError ?? "Не удалось отправить стикер.";
                await new MessageDialog(refusal).ShowAsync();
                return;
            }

            // Отправленный поднимается к началу недавних — там его и станут
            // искать в следующий раз.
            StickersModule.Instance.NoteSent(chosen);

            if (!Contains(sent.MessageId)) _messages.Add(sent);
            ChatsModule.Instance.NoteMessage(sent);

            Rebuild();
            ScrollToEnd();
        }

        #endregion

        #region Запись голосового и кружка

        private VoiceRecorder _voice;
        private VideoNoteRecorder _note;

        /// <summary>Отсчёт для обеих записей — идёт всегда только одна.</summary>
        private DispatcherTimer _recordTicker;

        /// <summary>Отсрочка перед началом: короткое касание — это не запись.</summary>
        private DispatcherTimer _holdTimer;

        /// <summary>Палец на кнопке. Отпустили — запись кончается сама собой.</summary>
        private bool _recordHeld;

        /// <summary>Запись закреплена движением вверх: палец больше не нужен.</summary>
        private bool _recordLocked;

        /// <summary>
        /// Удержание уже переросло в запись. Этим отличается отпускание,
        /// которым отправляют записанное, от короткого касания, которым
        /// выбирают, что записывать.
        /// </summary>
        private bool _recordStarted;

        /// <summary>Запись брошена движением влево, а не отпусканием пальца.</summary>
        private bool _recordAborted;

        /// <summary>
        /// Что запишется удержанием: голосовое или кружок. Голосовое —
        /// сразу, кружок — выбором: его посылают реже, а камера включается
        /// не мгновенно.
        /// </summary>
        private bool _noteMode;

        /// <summary>
        /// Какой камерой снимать кружок. Снимать начинают удержанием
        /// кнопки, и мгновения на выбор там нет, — выбирается заранее,
        /// в дополнительном списке полосы команд.
        /// </summary>
        private bool _noteFront = true;

        private void OnNoteCamera(object sender, RoutedEventArgs e)
        {
            _noteFront = !_noteFront;

            NoteCameraButton.Label = _noteFront
                ? "кружок основной камерой"
                : "кружок передней камерой";
        }

        /// <summary>Значки на кнопке записи: микрофон и камера.</summary>
        private const string VoiceGlyph = "\uE1D6";
        private const string NoteGlyph = "\uE116";

        private Point _recordFrom;
        private bool _finishing;

        /// <summary>Насколько увести палец, чтобы отменить запись или закрепить её.</summary>
        private const double CancelSlide = 90;
        private const double LockSlide = 80;

        private bool VoiceGoing
        {
            get { return _voice != null && _voice.Recording; }
        }

        private bool NoteGoing
        {
            get { return _note != null && _note.Recording; }
        }

        /// <summary>
        /// Кнопка записи — ростом с соседнюю. Своей высоты у рамки нет, и
        /// без этого она вытягивается во всю высоту строки ввода, оказываясь
        /// заметно выше скрепки.
        /// </summary>
        private void OnAttachSized(object sender, SizeChangedEventArgs e)
        {
            // Мерить кнопку целиком нельзя: места на странице она
            // занимает больше, чем нарисована, — сверху и снизу шаблон
            // оставляет запас под палец. У рамки записи такого запаса нет,
            // и по полному размеру она выходила заметно выше соседки.
            // Значит, мерим саму коробку — первую рамку внутри кнопки.
            FrameworkElement box = Drawn(AttachButton);

            double wide = box != null && box.ActualWidth > 0 ? box.ActualWidth : e.NewSize.Width;
            double tall = box != null && box.ActualHeight > 0 ? box.ActualHeight : e.NewSize.Height;

            if (RecordButton.Width == wide && RecordButton.Height == tall) return;

            RecordButton.Width = wide;
            RecordButton.Height = tall;

            Log.Write("chat", "кнопка записи: скрепка занимает "
                              + (int)e.NewSize.Width + "×" + (int)e.NewSize.Height
                              + ", нарисована " + (int)wide + "×" + (int)tall);
        }

        /// <summary>
        /// Нарисованная коробка кнопки — первая рамка в её устройстве.
        /// Имени частей шаблона здесь не спрашиваются нарочно: они меняются
        /// от системы к системе, а рамка внутри кнопки всегда одна.
        /// </summary>
        private static FrameworkElement Drawn(DependencyObject box)
        {
            int count = VisualTreeHelper.GetChildrenCount(box);

            for (int at = 0; at < count; at++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(box, at);

                var border = child as Border;
                if (border != null) return border;

                FrameworkElement found = Drawn(child);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>
        /// Микрофон или камера ещё включаются.
        ///
        /// Пока это так, заканчивать запись нельзя — нечего ещё заканчивать,
        /// а включение идёт своим чередом и не ждёт. На «десятке» при первом
        /// включении система спрашивает разрешение: спрашивая, она отбирает
        /// у кнопки нажатие, и то выглядело как «палец отпустили». Запись
        /// заканчивалась и разбиралась, пока микрофон только открывался, —
        /// а когда открылся, разбирать было уже нечего, и приложение падало
        /// на пустом месте. Отсюда «после разрешения вылетает».
        ///
        /// Что палец за это время убрали, включение замечает само и
        /// заканчивает запись, едва она началась.
        /// </summary>
        private bool _starting;

        private void OnRecordDown(object sender, PointerRoutedEventArgs e)
        {
            if (VoiceGoing || NoteGoing || _finishing || _starting) return;

            RecordButton.CapturePointer(e.Pointer);

            _recordFrom = e.GetCurrentPoint(null).Position;
            _recordHeld = true;
            _recordLocked = false;
            _recordStarted = false;
            _recordAborted = false;

            if (_holdTimer == null)
            {
                _holdTimer = new DispatcherTimer();
                _holdTimer.Interval = TimeSpan.FromMilliseconds(250);
                _holdTimer.Tick += OnHoldElapsed;
            }

            _holdTimer.Start();
        }

        /// <summary>
        /// Кнопку держат дольше мгновения — значит, это запись, а не промах.
        /// </summary>
        private async void OnHoldElapsed(object sender, object e)
        {
            _holdTimer.Stop();

            if (!_recordHeld) return;

            _recordStarted = true;

            // Под перехватом: метод без возврата, и всё, что сорвётся при
            // включении оборудования, унесло бы приложение молча.
            try
            {
                if (_noteMode) await BeginNoteAsync();
                else await BeginVoiceAsync();
            }
            catch (Exception error)
            {
                Log.Fail("chat", "начало записи", error);
                _starting = false;

                await DropRecordingAsync();
            }
        }

        private void OnRecordMoved(object sender, PointerRoutedEventArgs e)
        {
            // Отмена и закрепление считаются с той секунды, как удержание
            // признано записью, — а не с той, как запись пошла: камера
            // включается не мгновенно, и увести палец успевают раньше.
            if (!_recordHeld || _recordLocked || !_recordStarted) return;

            Point at = e.GetCurrentPoint(null).Position;

            double up = _recordFrom.Y - at.Y;
            double left = _recordFrom.X - at.X;

            // Вверх — закрепить: дальше можно убрать палец и говорить
            // спокойно. Проверяется первым: движение вверх почти всегда идёт
            // с примесью движения вбок, и отмена перехватывала бы его.
            if (up > LockSlide)
            {
                _recordLocked = true;
                _recordHeld = false;

                RecordButtons.Visibility = Visibility.Visible;
                RecordHint.Visibility = Visibility.Collapsed;

                Log.Write("chat", "запись закреплена");
                return;
            }

            if (left > CancelSlide)
            {
                _recordHeld = false;
                _recordAborted = true;

                // Оборудование ещё включается — само и заметит, что палец
                // уже увели, и бросит запись, как только сможет.
                if (_starting) return;

                var ignored = FinishAsync(false);
            }
        }

        private async void OnRecordUp(object sender, PointerRoutedEventArgs e)
        {
            if (!_recordHeld) return;
            _recordHeld = false;

            if (_holdTimer != null) _holdTimer.Stop();

            if (_recordLocked) return;

            // Касание, не переросшее в удержание, — это не запись, а выбор,
            // что записывать: отдельной кнопки под него в строке ввода нет.
            if (!_recordStarted)
            {
                SwitchRecording();
                return;
            }

            if (_starting) return;

            try
            {
                await FinishAsync(true);
            }
            catch (Exception error)
            {
                Log.Fail("chat", "конец записи", error);
                await DropRecordingAsync();
            }
        }

        /// <summary>
        /// Нажатие отобрали не пальцем, а самой системой — звонком, блокировкой,
        /// чем угодно. Начатая запись тогда заканчивается как обычно, а вот
        /// режим записи не меняется: человек ни по чему не касался.
        /// </summary>
        private async void OnRecordLost(object sender, PointerRoutedEventArgs e)
        {
            if (!_recordHeld) return;
            _recordHeld = false;

            if (_holdTimer != null) _holdTimer.Stop();

            if (_recordLocked || !_recordStarted) return;

            // Отобрала нажатие сама система — чаще всего это её же вопрос о
            // разрешении на микрофон или камеру. Оборудование ещё
            // включается; закончит оно само, а нам сейчас трогать нечего.
            if (_starting) return;

            try
            {
                await FinishAsync(true);
            }
            catch (Exception error)
            {
                Log.Fail("chat", "конец записи", error);
                await DropRecordingAsync();
            }
        }

        /// <summary>
        /// Переключить, что записывается удержанием. Значок меняется на
        /// самой кнопке: иначе о выбранном не догадаться до первой записи.
        /// </summary>
        private void SwitchRecording()
        {
            _noteMode = !_noteMode;

            RecordGlyph.Text = _noteMode ? NoteGlyph : VoiceGlyph;

            ShowUploadHint(_noteMode
                               ? "кружок: удерживайте, чтобы снять"
                               : "голосовое: удерживайте, чтобы записать");
        }

        private async void OnRecordCancel(object sender, RoutedEventArgs e)
        {
            try { await FinishAsync(false); }
            catch (Exception error) { Log.Fail("chat", "отмена записи", error); }
        }

        private async void OnRecordSend(object sender, RoutedEventArgs e)
        {
            try { await FinishAsync(true); }
            catch (Exception error) { Log.Fail("chat", "отправка записи", error); }
        }

        /// <summary>Закончить ту запись, которая идёт, — идёт всегда одна.</summary>
        private async Task FinishAsync(bool send)
        {
            // Пока оборудование включается, заканчивать нечего: включение
            // само разберётся с тем, что палец убрали.
            if (_starting) return;

            if (_note != null) await FinishNoteAsync(send);
            else await FinishVoiceAsync(send);
        }

        private async Task BeginVoiceAsync()
        {
            if (_voice != null) return;

            // Слушать и записывать разом нельзя: звуковой поток один.
            StopPlayback();

            VoiceRecorder voice = new VoiceRecorder();
            _voice = voice;

            ShowRecordBar(true, false);

            // Включение — под признаком: пока оно идёт, никто не смеет
            // закончить или бросить запись. Иначе выходило, что микрофон
            // ещё открывается, а его уже выбросили — и по возвращении сюда
            // обращаться было не к чему.
            _starting = true;

            bool started;

            try
            {
                started = await voice.StartAsync();
            }
            finally
            {
                _starting = false;
            }

            // Пока включались, со страницы могли уйти — тогда всё уже убрано.
            if (_voice != voice) return;

            if (!started)
            {
                string why = voice.LastError;

                voice.Dispose();
                _voice = null;

                ShowRecordBar(false, false);

                await new MessageDialog(why ?? "Не удалось включить микрофон.").ShowAsync();
                return;
            }

            StartRecordTicker();

            // Палец могли отпустить, пока включался микрофон, — в том числе
            // потому, что система спрашивала разрешение. Тогда записи не
            // будет вовсе, и висеть строке записи незачем.
            if (!_recordHeld && !_recordLocked) await FinishVoiceAsync(!_recordAborted);
        }

        /// <summary>
        /// Закончить запись. Отправляем только достаточно длинную: всё, что
        /// короче, — это соскочивший палец, а не сообщение.
        /// </summary>
        private async Task FinishVoiceAsync(bool send)
        {
            if (_voice == null || _finishing) return;

            _finishing = true;
            StopRecordTicker();

            VoiceTake take = null;

            if (send) take = await _voice.StopAsync();
            else await _voice.CancelAsync();

            _voice.Dispose();
            _voice = null;

            ShowRecordBar(false, false);
            _finishing = false;

            if (!send || take == null) return;

            if (take.DurationMs < VoiceRecorder.LeastMs)
            {
                ShowUploadHint("удерживайте кнопку, чтобы записать");
                return;
            }

            if (!take.Ok)
            {
                await new MessageDialog(take.Error ?? "Голосовое не записалось.").ShowAsync();
                return;
            }

            await SendRecordedAsync(take.Data, "Голосовое не отправлено.",
                async report => await MessagesModule.Instance.SendVoiceAsync(
                    _chatId, take.Data, take.DurationMs, take.Wave, _replyToId, report));
        }

        /// <summary>
        /// Строка записи. Она одна на голосовое и кружок: место под строкой
        /// ввода всего одно, а идёт всегда только одна запись. График
        /// громкости у кружка убирается — слушать там нечего, смотрят в
        /// окошко.
        /// </summary>
        private void ShowRecordBar(bool shown, bool note)
        {
            RecordBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

            RecordButtons.Visibility = Visibility.Collapsed;
            RecordHint.Visibility = Visibility.Visible;
            RecordWave.Visibility = note ? Visibility.Collapsed : Visibility.Visible;

            RecordTime.Text = "0:00";
            RecordWave.Data = null;
            RecordDot.Opacity = 1;

            if (!shown) _recordLocked = false;
        }

        #endregion

        #region Кружок

        /// <summary>
        /// Включить камеру и снимать.
        ///
        /// Камера включается не мгновенно, и всё это время палец уже на
        /// кнопке: съёмка начинается позже нажатия. Отпустить или увести
        /// палец успевают и до того — тогда съёмки не будет вовсе.
        /// </summary>
        private async Task BeginNoteAsync()
        {
            if (_note != null) return;

            StopPlayback();

            VideoNoteRecorder note = new VideoNoteRecorder();
            note.Front = _noteFront;
            _note = note;

            ShowRecordBar(true, true);
            NoteRing.Data = null;

            // Включение камеры — под признаком, как и микрофона: пока оно
            // идёт, закончить или бросить запись нельзя. Разрешение на
            // «десятке» спрашивается именно здесь, и, спрашивая, система
            // отбирает нажатие у кнопки.
            _starting = true;

            bool ready;

            try
            {
                ready = await note.StartPreviewAsync(NotePreview);
            }
            finally
            {
                _starting = false;
            }

            if (_note != note) return;

            if (!ready)
            {
                string why = note.LastError;

                await CloseNoteAsync();
                await new MessageDialog(why ?? "Камера недоступна.").ShowAsync();

                return;
            }

            // Пока запись не пошла, полей не ждём: их не будет, если заказ
            // вытянутой точки пройдёт. Не пройдёт — поправим ниже.
            FitNotePreview(false);
            WarnAboutBars();

            if (!_recordHeld && !_recordLocked)
            {
                await CloseNoteAsync();
                return;
            }

            NoteLayer.Visibility = Visibility.Visible;

            _starting = true;

            bool started;

            try
            {
                started = await note.StartAsync();
            }
            finally
            {
                _starting = false;
            }

            if (_note != note) return;

            if (!started)
            {
                string why = note.LastError;

                await CloseNoteAsync();
                await new MessageDialog(why ?? "Не удалось начать съёмку.").ShowAsync();

                return;
            }

            // Теперь известно, вписала ли Media Foundation кадр с полями.
            if (note.Boxed) FitNotePreview(true);

            StartRecordTicker();

            // Палец могли убрать, пока включалась камера.
            if (!_recordHeld && !_recordLocked) await FinishNoteAsync(!_recordAborted);
        }

        /// <summary>
        /// Насколько повёрнуты ступени обрезки окошка вместе взятые. Круг
        /// из них складывается поворотами, и содержимое едет вместе с ними.
        /// </summary>
        private const double RoundTurn = 75;

        /// <summary>Сказано ли уже про поля — второй раз не повторяем.</summary>
        private static bool _barsSaid;

        /// <summary>
        /// Предупредить про чёрные поля — один раз за запуск.
        ///
        /// Кружок обязан быть квадратным: неквадратный сервер не принимает,
        /// проверено. А если у камеры квадратного набора нет, её кадр в
        /// квадрат впишут с полями по бокам, и таким кружок увидит
        /// собеседник. Обрезать кадр съёмка на этой системе не умеет, так
        /// что поправить это здесь нечем — но у основной камеры набор
        /// нередко другой, и попробовать её стоит.
        /// </summary>
        private void WarnAboutBars()
        {
            if (_note == null || _note.Squared || _barsSaid) return;

            _barsSaid = true;
            ShowUploadHint("у этой камеры кружок выйдет с полями по бокам");
        }

        /// <summary>
        /// Растянуть показ так, чтобы он накрыл окошко целиком.
        ///
        /// Полагаться на то, как вписывает кадр сам показ, нельзя: с
        /// поворотом он считает стороны по-своему и оставляет по краям
        /// чёрные поля — вместо круга выходит узкая полоса с чёрным по
        /// бокам, и на кружок это не похоже вовсе.
        ///
        /// Поэтому размер задаётся числом — и таким, чтобы показ повторял
        /// запись. Записывается кадр камеры, натянутый на квадрат целиком
        /// (оттого он и выходит растянутым вширь), — значит и показывать
        /// его надо натянутым на всё окошко, а не обрезанным по середине:
        /// иначе снимающий кадрирует себя по одной картинке, а собеседник
        /// увидит другую.
        /// </summary>
        private void FitNotePreview(bool boxed)
        {
            double side = NoteWindow.Width;

            double wide = _note.PreviewWidth;
            double tall = _note.PreviewHeight;

            if (wide <= 0 || tall <= 0)
            {
                Log.Write("chat", "кружок: каким кадром идёт показ — неизвестно");
                return;
            }

            // Рамке — пропорции камеры, как она их отдаёт: своей формы
            // ей задавать нельзя, кадр от этого перекашивается сам.
            double part = side / wide;

            NotePreview.Width = wide * part;
            NotePreview.Height = tall * part;

            // Поворот на четверть меняет стороны местами: то, что в рамке
            // было шириной, на экране станет высотой.
            double shownWide = tall * part;
            double shownTall = wide * part;

            double alongX = side / shownTall;
            double alongY = side / shownWide;

            // Вписыванием кадр уменьшается по большей своей стороне, и по
            // краям остаётся чёрное; натягиванием — растягивается на всё
            // окошко. Что из двух делает запись, то повторяет и показ.
            if (boxed)
            {
                double fit = Math.Min(alongX, alongY);

                alongX = fit;
                alongY = fit;
            }

            var turn = new Windows.UI.Xaml.Media.TransformGroup();

            turn.Children.Add(new Windows.UI.Xaml.Media.ScaleTransform
            {
                ScaleX = alongX,
                ScaleY = alongY
            });

            // Ступени обрезки поворачивают показ и сами — на пятнадцать
            // градусов каждая. Их общий поворот здесь и вычитается.
            turn.Children.Add(new Windows.UI.Xaml.Media.RotateTransform
            {
                Angle = _note.PreviewTurn - RoundTurn
            });

            NotePreview.RenderTransformOrigin = new Point(0.5, 0.5);
            NotePreview.RenderTransform = turn;

            Log.Write("chat", "кружок: показ " + wide + "×" + tall
                              + " рамкой " + (int)NotePreview.Width
                              + "×" + (int)NotePreview.Height
                              + ", поворот " + (int)_note.PreviewTurn
                              + (boxed ? ", вписан" : ", натянут")
                              + " в окошко " + (int)side);
        }

        /// <summary>Закончить съёмку: отправить снятое или бросить его.</summary>
        private async Task FinishNoteAsync(bool send)
        {
            if (_note == null || _finishing) return;

            _finishing = true;
            StopRecordTicker();

            NoteTake take = null;

            if (send && _note.Recording) take = await _note.StopAsync();
            else if (_note.Recording) await _note.CancelAsync();

            await CloseNoteAsync();
            _finishing = false;

            if (take == null || !take.Ok) return;

            if (take.DurationMs < VoiceRecorder.LeastMs)
            {
                try { await take.File.DeleteAsync(); }
                catch (Exception) { }

                ShowUploadHint("удерживайте кнопку, чтобы снять");
                return;
            }

            byte[] data = await ReadNoteAsync(take.File);

            // Снятое больше не нужно: на сервер уедет то, что уже в памяти.
            try { await take.File.DeleteAsync(); }
            catch (Exception) { }

            if (data == null || data.Length == 0)
            {
                await new MessageDialog("Кружок не записался.").ShowAsync();
                return;
            }

            // Недописанное на сервер не отправляем: он ответит отказом, а
            // человек увидит невнятную ругань вместо простого «не вышло».
            // Сам файл оставляем — по нему и разбираться.
            if (!Mp4Facts.Ready(data))
            {
                Log.Write("chat", "кружок: " + Mp4Facts.Describe(data));
                await KeepNoteAsync(data);

                await new MessageDialog("Кружок не дописался — попробуйте ещё раз.")
                    .ShowAsync();

                return;
            }

            // Из чего вышел ролик — в журнал. Сервер о негодном кружке
            // говорит одно слово, и разобрать снятое приходится самим.
            Log.Write("chat", "кружок: " + Mp4Facts.Describe(data));

            // Снятое телефоном к заливке не готово: вид файла объявлен
            // по-своему, описание ролика лежит в конце, а между ними —
            // части, которых стандарт не знает. Приводим в порядок.
            byte[] ready = Mp4Facts.Prepare(data);

            if (ready != null)
            {
                data = ready;
                Log.Write("chat", "кружок после правки: " + Mp4Facts.Describe(data));
            }

            int duration = take.DurationMs;

            bool sent = await SendRecordedAsync(data, "Кружок не отправлен.",
                async report => await MessagesModule.Instance.SendVideoNoteAsync(
                    _chatId, data, duration, _replyToId, report));

            // Непринятое не выбрасываем: разбираться, чем оно не угодило
            // серверу, придётся по самому файлу.
            if (!sent && await KeepNoteAsync(data) != null)
            {
                ShowUploadHint("запись оставлена на телефоне");
            }
        }

        /// <summary>
        /// Прочитать снятое, дав съёмке дописать файл.
        ///
        /// Остановка записи возвращается раньше, чем описание дорожек
        /// оказывается на диске, — и прочитанное в этот миг обрывается на
        /// полуслове. Ждём и читаем ещё раз: лишняя доля секунды здесь
        /// дешевле, чем потерянная запись.
        /// </summary>
        private static async Task<byte[]> ReadNoteAsync(StorageFile file)
        {
            byte[] data = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(300);

                try
                {
                    data = await FilePicking.ReadAsync(file);
                }
                catch (Exception error)
                {
                    Log.Write("chat", "кружок: снятое не прочиталось — " + error.Message);
                    continue;
                }

                if (Mp4Facts.Ready(data)) return data;

                Log.Write("chat", "кружок: снятое ещё не дописано, ждём");
            }

            return data;
        }

        private async Task CloseNoteAsync()
        {
            if (_note != null)
            {
                await _note.StopPreviewAsync();
                _note = null;
            }

            NotePreview.Source = null;
            NoteLayer.Visibility = Visibility.Collapsed;
            NoteRing.Data = null;

            ShowRecordBar(false, true);
        }

        /// <summary>Бросить всё начатое — при уходе со страницы.</summary>
        private async Task DropRecordingAsync()
        {
            StopRecordTicker();

            if (_voice != null)
            {
                await _voice.CancelAsync();

                _voice.Dispose();
                _voice = null;

                ShowRecordBar(false, false);
            }

            if (_note != null) await CloseNoteAsync();
        }

        #endregion

        #region Ход записи

        private int _blinks;

        private void StartRecordTicker()
        {
            if (_recordTicker == null)
            {
                _recordTicker = new DispatcherTimer();
                _recordTicker.Interval = TimeSpan.FromMilliseconds(100);
                _recordTicker.Tick += OnRecordTick;
            }

            _blinks = 0;
            _recordTicker.Start();
        }

        private void StopRecordTicker()
        {
            if (_recordTicker != null) _recordTicker.Stop();
        }

        private async void OnRecordTick(object sender, object e)
        {
            _blinks++;

            if (VoiceGoing)
            {
                // Забираем накопившееся микрофоном и тут же кодируем: свой
                // отсчёт времени этому не нужен, а второй таймер стоил бы
                // ровно столько же, сколько и первый.
                await _voice.PumpAsync();

                TimeSpan passed = _voice.Elapsed;

                RecordTime.Text = Formats.Clock(passed);
                RecordDot.Opacity = (_blinks / 5) % 2 == 0 ? 1 : 0.25;
                RecordWave.Data = Formats.Wave(LiveWave(_voice.Levels));

                if (passed.TotalMilliseconds >= VoiceRecorder.MostMs)
                {
                    await FinishVoiceAsync(true);
                }

                return;
            }

            if (NoteGoing)
            {
                TimeSpan passed = _note.Elapsed;
                double part = passed.TotalMilliseconds / VideoNoteRecorder.MostMs;

                RecordTime.Text = Formats.Clock(passed);
                RecordDot.Opacity = (_blinks / 5) % 2 == 0 ? 1 : 0.25;
                DrawRing(part);

                if (part >= 1) await FinishNoteAsync(true);

                return;
            }

            StopRecordTicker();
        }

        /// <summary>
        /// Последние замеры громкости — тот кусок, что виден на экране.
        /// Показывать весь график записи незачем: он бы сжимался тем сильнее,
        /// чем дольше говорят, и под конец превратился бы в сплошную полосу.
        /// </summary>
        private static byte[] LiveWave(IList<double> levels)
        {
            const int Shown = 40;

            var made = new byte[Shown];
            if (levels == null || levels.Count == 0) return made;

            int from = levels.Count > Shown ? levels.Count - Shown : 0;

            for (int at = from; at < levels.Count; at++)
            {
                int bar = at - from;
                if (bar >= Shown) break;

                double level = levels[at];
                if (level < 0) level = 0;
                if (level > 1) level = 1;

                made[bar] = (byte)(level * 255);
            }

            return made;
        }

        /// <summary>
        /// Кольцо по краю кружка: сколько от минуты уже снято. Дуга строится
        /// от верхней точки по часовой стрелке.
        /// </summary>
        private void DrawRing(double part)
        {
            NoteRing.Data = Arc(part, 120, 117.5);
        }

        /// <summary>
        /// Дуга от верхней точки по часовой стрелке — доля круга, которую
        /// нужно закрасить. Ею показываются и остаток минуты при съёмке, и
        /// проигранное у кружка в переписке; разнятся они только размером.
        /// </summary>
        private static Geometry Arc(double part, double middle, double radius)
        {
            if (part <= 0) return null;

            // Полный оборот не рисуем: у дуги начало совпало бы с концом, и
            // вместо кольца вышло бы ничто.
            if (part > 0.999) part = 0.999;

            double angle = 2 * Math.PI * part;

            var figure = new Windows.UI.Xaml.Media.PathFigure();
            figure.StartPoint = new Point(middle, middle - radius);
            figure.IsClosed = false;

            var curve = new Windows.UI.Xaml.Media.ArcSegment();
            curve.Point = new Point(middle + radius * Math.Sin(angle),
                                    middle - radius * Math.Cos(angle));
            curve.Size = new Size(radius, radius);
            curve.IsLargeArc = part > 0.5;
            curve.SweepDirection = Windows.UI.Xaml.Media.SweepDirection.Clockwise;

            figure.Segments.Add(curve);

            var geometry = new Windows.UI.Xaml.Media.PathGeometry();
            geometry.Figures.Add(figure);

            return geometry;
        }

        /// <summary>Короткая подсказка на месте полосы отправки.</summary>
        private void ShowUploadHint(string text)
        {
            ProgressText.Text = text;
            ProgressText.Visibility = Visibility.Visible;

            var hide = new DispatcherTimer();
            hide.Interval = TimeSpan.FromSeconds(2);

            hide.Tick += (sender, ignored) =>
            {
                hide.Stop();
                if (ProgressText.Text == text) HideUpload();
            };

            hide.Start();
        }

        /// <summary>
        /// Заливка записанного. У голосового и кружка она одна и та же:
        /// показать полосу, отправить, разобрать отказ.
        /// </summary>
        private async Task<bool> SendRecordedAsync(byte[] data, string failed,
                                                   Func<MediaHttp.Progress, Task<MaxMessage>> send)
        {
            AttachButton.IsEnabled = false;
            RecordButton.Visibility = Visibility.Collapsed;

            ShowUpload(0, data.Length);

            MediaHttp.Progress report = (done, total) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                                                  () => ShowUpload(done, total));
            };

            MaxMessage sent = await send(report);

            HideUpload();
            AttachButton.IsEnabled = true;
            UpdateSendButton();

            if (sent == null)
            {
                string refusal = MessagesModule.Instance.LastError
                                 ?? Uploader.LastError
                                 ?? failed;

                await new MessageDialog(refusal).ShowAsync();
                return false;
            }

            OnCancelReply(null, null);

            if (!Contains(sent.MessageId)) _messages.Add(sent);
            ChatsModule.Instance.NoteMessage(sent);

            Rebuild();
            ScrollToEnd();

            return true;
        }

        /// <summary>
        /// Оставить на телефоне кружок, который не ушёл.
        ///
        /// О негодной записи сервер говорит одно слово без подробностей, и
        /// понять, чем именно она ему не угодила, можно только глядя на сам
        /// файл. Из временной папки он стирается сразу после отправки,
        /// поэтому непринятый кладётся туда, откуда его достанут проводом:
        /// в библиотеку видео, а если она закрыта — в плёнку или к себе.
        /// </summary>
        private static async Task<StorageFile> KeepNoteAsync(byte[] data)
        {
            string name = "lovit-krug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4";

            StorageFile kept = await WriteToAsync(() => KnownFolders.VideosLibrary, name, data)
                               ?? await WriteToAsync(() => KnownFolders.CameraRoll, name, data)
                               ?? await WriteToAsync(() => ApplicationData.Current.LocalFolder,
                                                     name, data);

            if (kept != null) Log.Write("chat", "кружок: непринятое оставлено — " + kept.Path);
            else Log.Write("chat", "кружок: непринятое сохранить некуда");

            return kept;
        }

        /// <summary>
        /// Записать в папку, если она открыта этому приложению. Какие
        /// библиотеки телефон отдаёт, заранее не известно — обращение к
        /// закрытой само по себе и есть отказ.
        /// </summary>
        private static async Task<StorageFile> WriteToAsync(Func<StorageFolder> where, string name,
                                                            byte[] data)
        {
            try
            {
                StorageFolder folder = where();

                StorageFile file = await folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, data);
                return file;
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: не сохранить непринятое — " + error.Message);
                return null;
            }
        }

        #endregion

        #region Отправка — ход

        /// <summary>
        /// Ход отправки: полоса со счётом и подпись под ней. Общий предел
        /// берётся тот, что сообщила сама отправка, — у фотографии он чуть
        /// больше самого снимка за счёт обвязки письма.
        /// </summary>
        private void ShowUpload(long done, long total)
        {
            Progress.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;

            if (total <= 0)
            {
                Progress.IsIndeterminate = true;
                ProgressText.Text = "отправка…";
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Maximum = total;
            Progress.Value = done;

            ProgressText.Text = "отправка " + (done * 100 / total) + "% — "
                                + Formats.Size(done) + " из " + Formats.Size(total);
        }

        private void HideUpload()
        {
            Progress.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;

            // Возвращаем бегущий вид: этой же полосой показывается догрузка
            // истории, а там считать нечего.
            Progress.IsIndeterminate = true;
            Progress.Value = 0;
        }

        /// <summary>Подписка на канал и отписка от него — одной кнопкой.</summary>
        private async void OnSubscribe(object sender, RoutedEventArgs e)
        {
            SubscribeButton.IsEnabled = false;

            bool member = ChatsModule.Instance.Chat(_chatId) != null;

            bool done = member
                ? await ChatsModule.Instance.LeaveChatAsync(_chatId)
                : await ChatsModule.Instance.SubscribeToChannelAsync(_chatId);

            SubscribeButton.IsEnabled = true;

            if (!done)
            {
                var dialog = new MessageDialog(member
                    ? "Не удалось отписаться."
                    : "Не удалось подписаться: у канала нет открытой ссылки.");
                await dialog.ShowAsync();
                return;
            }

            UpdateComposer();
        }

        #endregion
    }
}
