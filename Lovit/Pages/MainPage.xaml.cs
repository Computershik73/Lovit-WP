using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Storage;
using Lovit.Models;
using Lovit.Ui;
using Windows.Foundation;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Списки чатов и контактов. Порт MAXChatsViewController и
    /// MAXContactsViewController.
    ///
    /// Вкладок столько, сколько у аккаунта папок: «все», «новые», сами папки
    /// и «контакты». Папка — это правило отбора, а не отдельный список, и
    /// состав считается на месте по тем же чатам, что уже в памяти.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>Что за список висит на вкладке.</summary>
        private class Tab
        {
            public ListView List { get; set; }
            public MaxFolder Folder { get; set; }
            public bool UnreadOnly { get; set; }
            public bool Contacts { get; set; }

            /// <summary>
            /// Живой список, а не заново собранный. Переприсваивание
            /// ItemsSource заставляет ListView создать все строки заново —
            /// с анимацией появления и с пустыми плитками вместо аватарок,
            /// и это отлично видно при каждом возвращении к списку.
            /// </summary>
            public ObservableCollection<ListRow> Rows { get; set; }

            public Tab()
            {
                Rows = new ObservableCollection<ListRow>();
            }
        }

        private readonly List<Tab> _tabs = new List<Tab>();
        private bool _loading;
        private bool _built;

        /// <summary>Каким был набор вкладок: по нему видно, надо ли пересобирать.</summary>
        private string _tabsSignature;

        public MainPage()
        {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Required;
        }

        /// <summary>
        /// Подписки заводятся здесь, а не в конструкторе, и снимаются при
        /// уходе с экрана.
        ///
        /// В конструкторе они были ошибкой. Слушают они одиночек, живущих
        /// всё время работы приложения, а сама страница столько не живёт:
        /// стоит рамке её пересоздать — и прежняя остаётся жива через эти
        /// подписки навсегда. Дальше каждое изменение списка приходило во
        /// все прошлые страницы разом, и каждая лезла в свою давно
        /// выброшенную разметку.
        ///
        /// Снятие перед подписыванием — не перестраховка: страница помечена
        /// как сохраняемая, и сюда возвращаются повторно, с тем же самым
        /// объектом.
        /// </summary>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Unsubscribe();

            ChatsModule.Instance.Changed += OnDataChanged;
            FoldersModule.Instance.Changed += OnFoldersChanged;
            MaxApi.Instance.StateChanged += OnStateChanged;

            // Повторного входа после обрыва здесь больше нет: он живёт в
            // AccountModule. Обрывы случаются и когда этот экран закрыт, а
            // подписка, привязанная к экрану, в такие минуты бездействовала.

            BuildTabs();
            Reload();
            UpdateTitle();

            if (!AccountModule.Instance.IsLoggedIn) await LoginAsync();

            await OpenPendingLinkAsync();
        }

        /// <summary>
        /// Открыть ссылку, по которой нас запустили. Спрашиваем у сервера,
        /// что за ней стоит: чат открываем сразу, человека — диалогом с ним.
        /// </summary>
        private async Task OpenPendingLinkAsync()
        {
            string link = App.PendingLink;
            if (string.IsNullOrEmpty(link)) return;

            App.PendingLink = null;

            LinkTarget target = await SearchModule.Instance.ResolveLinkAsync(link);
            if (target == null)
            {
                var missing = new Windows.UI.Popups.MessageDialog(
                    "Не удалось открыть ссылку: " + Lovit.Core.Links.Tagged(link));

                await missing.ShowAsync();
                return;
            }

            if (target.ChatId != 0)
            {
                Frame.Navigate(typeof(ChatPage), new ChatPage.Target
                {
                    ChatId = target.ChatId,
                    Stub = target.Stub
                });
                return;
            }

            if (target.ContactId == 0) return;

            // Ссылка вела на человека: переписка с ним открывается и до того,
            // как заведена на сервере, — её номер выводится из двух номеров.
            MaxChat dialog = ChatsModule.Instance.DialogWithContact(target.ContactId);
            if (dialog == null) return;

            Frame.Navigate(typeof(ChatPage), new ChatPage.Target
            {
                ChatId = dialog.ChatId,
                Stub = dialog
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            ChatsModule.Instance.Changed -= OnDataChanged;
            FoldersModule.Instance.Changed -= OnFoldersChanged;
            MaxApi.Instance.StateChanged -= OnStateChanged;
        }

        #region Вход

        /// <summary>
        /// Вход по сохранённому токену. Приложение открывает этот экран сразу,
        /// минуя вход, если токен есть, — значит списки надо получить здесь.
        /// </summary>
        private async Task<bool> LoginAsync()
        {
            string token = AppSettings.Token;
            if (string.IsNullOrEmpty(token))
            {
                Frame.Navigate(typeof(LoginPage));
                Frame.BackStack.Clear();
                return false;
            }

            if (_loading) return false;
            _loading = true;

            try
            {
                if (!await MaxApi.Instance.EnsureOnlineAsync()) return false;

                AuthResult result = await AccountModule.Instance.LoginAsync(token);
                if (result.Step != AuthStep.LoggedIn)
                {
                    // Токен не стираем.
                    //
                    // Раньше любая неудача входа считалась отзывом токена, и
                    // аккаунт очищался вместе с ним. А неудача — это чаще
                    // всего обрыв связи или временный отказ; вдобавок сервер
                    // отвергает повторный вход по живой сессии, и кнопка
                    // «обновить» выкидывала из аккаунта каждым нажатием.
                    // Komet при неудачном входе не стирает ничего: выход
                    // делается только руками.
                    Log.Write("app", "вход по токену не удался: " + result.Error);

                    EmptyText.Text = "Не удалось обновить: " + result.Error;
                    EmptyText.Visibility = Visibility.Visible;
                    return false;
                }

                ChatsModule.Instance.ApplyLoginPayload(result.LoginPayload);
                return true;
            }
            finally
            {
                _loading = false;
                UpdateTitle();
            }
        }

        #endregion

        #region Вкладки

        /// <summary>
        /// Заводит вкладки. Постоянных три — «все», «новые» и «контакты», —
        /// а между вторым и третьим встают папки аккаунта, когда приедут.
        ///
        /// Пересобирать всё заново нельзя. Pivot после очистки продолжает
        /// показывать содержимое выброшенной вкладки, пока с неё не уйдут и
        /// не вернутся, — и это приходилось изображать перещёлкиванием
        /// вкладок. Перещёлкивание же и швыряло: при запуске список
        /// открывался на «новых», нажатие на «все» уводило к «контактам»
        /// (Pivot закольцован, и шаг влево от первой вкладки ведёт к
        /// последней), а сами чаты появлялись с задержкой.
        ///
        /// Поэтому постоянные вкладки заводятся один раз и не трогаются, а
        /// папки досаживаются на своё место. Досадка выбора не сбивает: то,
        /// что на экране, остаётся на экране.
        /// </summary>
        private void BuildTabs()
        {
            if (!_built)
            {
                InsertTab(0, "все", new Tab());
                InsertTab(1, "новые", new Tab { UnreadOnly = true });
                InsertTab(2, "контакты", new Tab { Contacts = true });

                _built = true;
            }

            SyncFolders();
        }

        /// <summary>
        /// Привести вкладки папок к тому, что прислал сервер.
        ///
        /// Обычно это разовая досадка: папок нет, пока не пришёл их список,
        /// а потом они появляются все разом. Убирать при этом нечего, и
        /// показанное на экране не шелохнётся.
        /// </summary>
        private void SyncFolders()
        {
            string wanted = Signature();
            if (wanted == _tabsSignature) return;

            _tabsSignature = wanted;

            // Папки лежат между «новыми» и «контактами»: последняя вкладка
            // всегда контакты, первые две — постоянные.
            for (int at = _tabs.Count - 2; at >= 2; at--) RemoveTab(at);

            int where = 2;

            foreach (MaxFolder folder in FoldersModule.Instance.Folders)
            {
                // «Все чаты» и «только непрочитанные» сервер присылает
                // наравне с остальными, а у нас это первые две вкладки.
                if (folder.IsAllChats || folder.IsUnreadOnly) continue;

                string title = string.IsNullOrEmpty(folder.Title) ? "папка" : folder.Title;
                InsertTab(where++, title.ToLower(), new Tab { Folder = folder });
            }
        }

        /// <summary>
        /// Убрать вкладку вместе с её списком. Обработчики снимаются здесь
        /// же: выброшенная ячейка не должна дожить до следующего нажатия —
        /// у долгоживущей строки иначе накапливаются подписчики от давно
        /// выброшенных списков, и первое же изменение уходит в них.
        /// </summary>
        private void RemoveTab(int at)
        {
            Tab gone = _tabs[at];

            if (gone.List != null)
            {
                gone.List.ItemClick -= OnRowClick;
                gone.List.Holding -= OnChatHolding;
                gone.List.RightTapped -= OnChatRightTapped;
                gone.List.ItemsSource = null;
            }

            _tabs.RemoveAt(at);
            Sections.Items.RemoveAt(at);
        }

        /// <summary>
        /// Чем один набор вкладок отличается от другого: их у нас две
        /// постоянные, «контакты» и папки аккаунта посередине.
        /// </summary>
        private static string Signature()
        {
            var text = new System.Text.StringBuilder();

            foreach (MaxFolder folder in FoldersModule.Instance.Folders)
            {
                if (folder.IsAllChats || folder.IsUnreadOnly) continue;
                text.Append(folder.Title ?? "папка").Append('|');
            }

            return text.ToString();
        }

        private void InsertTab(int at, string header, Tab tab)
        {
            var list = new ListView
            {
                ItemTemplate = tab.Contacts
                    ? (DataTemplate)Resources["ContactRowTemplate"]
                    : (DataTemplate)Resources["ChatRowTemplate"],

                // Без него строка ужимается по своей ширине, и правый край —
                // время со счётчиком — стоит у каждого чата на своём месте.
                ItemContainerStyle = (Style)Resources["ChatRowContainerStyle"],

                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true
            };

            list.ItemClick += OnRowClick;
            list.ItemsSource = tab.Rows;

            // Долгое нажатие — то же меню, что в оригинале открывается
            // долгим нажатием на строку списка.
            if (!tab.Contacts)
            {
                // Удержание — для пальца, правая кнопка — для мыши: событие
                // удержания мышь не подаёт вовсе.
                list.Holding += OnChatHolding;
                list.RightTapped += OnChatRightTapped;
            }

            tab.List = list;

            _tabs.Insert(at, tab);
            Sections.Items.Insert(at, new PivotItem { Header = header, Content = list });
        }

        private void OnFoldersChanged()
        {
            BuildTabs();
            Reload();
        }

        #endregion

        #region Наполнение

        private void OnDataChanged()
        {
            Reload();
        }

        private void Reload()
        {
            if (!_built) return;

            ChatsModule chats = ChatsModule.Instance;
            List<MaxChat> all = chats.Chats;
            HashSet<long> contactIds = chats.ContactIds;

            foreach (Tab tab in _tabs)
            {
                if (tab.Contacts)
                {
                    List<ListRow> people = ListRows.FromContacts(chats.Contacts);

                    // По этой строке видно, где потерялись контакты: их не
                    // прислал сервер или они не дошли до экрана.
                    if (people.Count != tab.Rows.Count)
                    {
                        Log.Write("app", "вкладка контактов: строк " + people.Count);
                    }

                    Sync(tab.Rows, people);
                    continue;
                }

                var picked = new List<MaxChat>();

                foreach (MaxChat chat in all)
                {
                    if (tab.UnreadOnly)
                    {
                        if (chat.UnreadCount > 0) picked.Add(chat);
                        continue;
                    }

                    if (tab.Folder == null)
                    {
                        picked.Add(chat);
                        continue;
                    }

                    if (tab.Folder.Matches(chat, chats.CurrentUserId, contactIds)) picked.Add(chat);
                }

                Sync(tab.Rows, ListRows.FromChats(picked));
            }

            EmptyText.Text = _loading ? "Загружаем…" : "Пока пусто";
            EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Приводит показанный список к нужному, трогая только то, что
        /// изменилось: строки, которых больше нет, убираются, новые
        /// вставляются на своё место, а переехавшие переставляются. Уцелевшие
        /// остаются теми же объектами — вместе со своими аватарками.
        /// </summary>
        private static void Sync(ObservableCollection<ListRow> shown, List<ListRow> wanted)
        {
            for (int i = shown.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(shown[i])) shown.RemoveAt(i);
            }

            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < shown.Count && shown[i] == wanted[i]) continue;

                int found = shown.IndexOf(wanted[i]);
                if (found >= 0) shown.Move(found, i);
                else shown.Insert(i, wanted[i]);
            }
        }

        private void UpdateTitle()
        {
            switch (MaxApi.Instance.State)
            {
                case SessionState.Online:
                    Sections.Title = "ЛОВИТ";
                    break;
                case SessionState.Connecting:
                    Sections.Title = "СОЕДИНЕНИЕ…";
                    break;
                default:
                    Sections.Title = "НЕТ СВЯЗИ";
                    break;
            }
        }

        private void OnStateChanged()
        {
            UpdateTitle();
        }

        #endregion

        #region Нажатия

        private void OnRowClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as ListRow;
            if (row == null) return;

            Tab tab = TabFor(sender as ListView);

            if (tab != null && tab.Contacts)
            {
                Frame.Navigate(typeof(ContactPage), row.Id);
                return;
            }

            Frame.Navigate(typeof(ChatPage), new ChatPage.Target { ChatId = row.Id });
        }

        /// <summary>
        /// Долгое нажатие на чат: тишина, очистка истории и выход. Порт меню
        /// из списка чатов iOS-версии.
        /// </summary>
        /// <summary>
        /// Правая кнопка мыши — то же меню, что и удержание. Касание сюда не
        /// пускаем: у пальца своё удержание, и меню от него уже открылось.
        /// </summary>
        private void OnChatRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == Windows.Devices.Input.PointerDeviceType.Touch) return;

            e.Handled = true;
            ShowChatMenu(e.OriginalSource as FrameworkElement, e.GetPosition(null));
        }

        private void OnChatHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;

            ShowChatMenu(e.OriginalSource as FrameworkElement, e.GetPosition(null));
        }

        /// <summary>Меню чата — общее для удержания и правой кнопки.</summary>
        private void ShowChatMenu(FrameworkElement element, Point where)
        {
            var row = element != null ? element.DataContext as ListRow : null;
            if (row == null) return;

            MaxChat chat = ChatsModule.Instance.Chat(row.Id);
            if (chat == null) return;

            var menu = new PopupMenu();

            menu.Commands.Add(new UICommand(chat.IsMuted ? "включить уведомления" : "выключить уведомления",
                async command => await ChatsModule.Instance.SetMuteUntilAsync(chat.IsMuted ? 0 : -1, chat.ChatId)));

            menu.Commands.Add(new UICommand("очистить историю",
                async command => await ChatsModule.Instance.ClearHistoryAsync(chat.ChatId, false)));

            if (!chat.IsDialog)
            {
                menu.Commands.Add(new UICommand(chat.IsChannel ? "отписаться" : "выйти из группы",
                    async command => await ChatsModule.Instance.LeaveChatAsync(chat.ChatId)));
            }

            var ignored = menu.ShowAsync(where);
        }

        private Tab TabFor(ListView list)
        {
            foreach (Tab tab in _tabs)
            {
                if (tab.List == list) return tab;
            }
            return null;
        }

        #endregion

        private void OnSearch(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage));
        }

        /// <summary>
        /// Обновление. Вход повторяется только если сессии действительно
        /// нет: по живой сессии сервер второй LOGIN отвергает, а список
        /// чатов и без того поддерживается пушами.
        /// </summary>
        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            RefreshButton.IsEnabled = false;

            try
            {
                if (!AccountModule.Instance.IsLoggedIn)
                {
                    // Не вышло — на экране осталось объяснение, и затирать
                    // его пересборкой списка незачем.
                    if (!await LoginAsync()) return;
                }
                else
                {
                    await MaxApi.Instance.EnsureOnlineAsync();
                }

                Reload();
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }
    }
}
