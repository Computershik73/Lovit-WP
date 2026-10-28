using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Models;
using Lovit.Ui;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Lovit.Pages
{
    /// <summary>
    /// Поиск по контактам, своим чатам, сообщениям и по всему MAX.
    /// Порт MAXSearchViewController.
    /// </summary>
    public sealed partial class SearchPage : Page
    {
        /// <summary>
        /// Строка выдачи: либо заголовок раздела, либо находка.
        ///
        /// Наследует строку списка ради аватарки: та умеет и буквы на
        /// цветном квадрате, пока картинка едет, и сообщить разметке, что
        /// картинка приехала. Заводить всё это заново значило бы держать
        /// два одинаковых куска в двух местах.
        /// </summary>
        public class ResultRow : ListRow
        {
            public string Header { get; set; }

            /// <summary>Что открывать: чат, контакт или найденное сообщение.</summary>
            public object Target { get; set; }

            public Visibility HeaderVisibility
            {
                get { return string.IsNullOrEmpty(Header) ? Visibility.Collapsed : Visibility.Visible; }
            }

            public Visibility RowVisibility
            {
                get { return string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible; }
            }

            public Visibility PreviewVisibility
            {
                get { return string.IsNullOrEmpty(Preview) ? Visibility.Collapsed : Visibility.Visible; }
            }
        }

        /// <summary>
        /// Задержка перед запросом на сервер. Без неё каждая буква уходила бы
        /// отдельным запросом, и ответы возвращались бы вперемешку.
        /// </summary>
        private const int TypingPauseMs = 400;

        private readonly List<ResultRow> _rows = new List<ResultRow>();
        private string _query = "";
        private int _generation;

        public SearchPage()
        {
            InitializeComponent();
            Results.ItemsSource = _rows;
        }

        private async void OnQueryChanged(object sender, TextChangedEventArgs e)
        {
            _query = QueryBox.Text.Trim();

            // Своё показываем сразу: оно берётся из памяти и ждать нечего.
            ShowLocal();

            int generation = ++_generation;
            await Task.Delay(TypingPauseMs);

            // За время паузы набрали что-то ещё — этот заход больше не нужен.
            if (generation != _generation || _query.Length == 0) return;

            await SearchRemoteAsync(generation);
        }

        private void ShowLocal()
        {
            _rows.Clear();

            if (_query.Length > 0)
            {
                List<MaxContact> contacts = SearchModule.Instance.LocalContacts(_query);
                if (contacts.Count > 0)
                {
                    _rows.Add(new ResultRow { Header = "контакты" });

                    foreach (MaxContact contact in contacts)
                    {
                        var row = new ResultRow
                        {
                            Id = contact.ContactId,
                            Title = contact.DisplayName,
                            Preview = contact.PhoneText,
                            Target = contact
                        };

                        ListRows.Load(row, contact.AvatarUrl);
                        _rows.Add(row);
                    }
                }

                List<MaxChat> chats = SearchModule.Instance.LocalChats(_query);
                if (chats.Count > 0)
                {
                    _rows.Add(new ResultRow { Header = "чаты" });

                    foreach (MaxChat chat in chats)
                    {
                        var row = new ResultRow
                        {
                            Id = chat.ChatId,
                            Title = ChatsModule.Instance.Title(chat),
                            Preview = ChatsModule.Instance.Subtitle(chat),
                            Target = chat
                        };

                        ListRows.Load(row, ChatsModule.Instance.AvatarUrl(chat));
                        _rows.Add(row);
                    }
                }
            }

            Refresh();
        }

        private async Task SearchRemoteAsync(int generation)
        {
            Progress.Visibility = Visibility.Visible;

            List<MessageHit> messages = await SearchModule.Instance.SearchMessagesAsync(_query);
            if (generation != _generation) return;

            if (messages.Count > 0)
            {
                _rows.Add(new ResultRow { Header = "сообщения" });

                foreach (MessageHit hit in messages)
                {
                    MaxChat chat = ChatsModule.Instance.Chat(hit.ChatId);

                    var row = new ResultRow
                    {
                        Id = hit.ChatId,
                        Title = Formats.OneLine(hit.Text),
                        Preview = (chat != null ? ChatsModule.Instance.Title(chat) : "Чат")
                                  + " · " + Formats.RelativeTime(hit.Time),
                        Target = hit
                    };

                    // У находки в переписке лицо чата, а не сообщения:
                    // своего у сообщения нет.
                    if (chat != null) ListRows.Load(row, ChatsModule.Instance.AvatarUrl(chat));

                    _rows.Add(row);
                }

                Refresh();
            }

            List<PublicHit> found = await SearchModule.Instance.SearchPublicAsync(_query);
            if (generation != _generation) return;

            if (found.Count > 0)
            {
                _rows.Add(new ResultRow { Header = "каналы и группы" });

                foreach (PublicHit hit in found)
                {
                    var row = new ResultRow
                    {
                        Id = hit.ChatId,
                        Title = hit.Title,
                        Preview = Formats.OneLine(hit.Subtitle),
                        Target = hit
                    };

                    ListRows.Load(row, hit.AvatarUrl);
                    _rows.Add(row);
                }
            }

            Progress.Visibility = Visibility.Collapsed;
            Refresh();
        }

        /// <summary>
        /// Список пересобирается целиком: разделы приходят вразнобой, и
        /// показывать половину выдачи, пока подтягивается остальное, — самое
        /// понятное поведение.
        /// </summary>
        private void Refresh()
        {
            Results.ItemsSource = null;
            Results.ItemsSource = _rows;
        }

        private void OnResultClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as ResultRow;
            if (row == null || row.Target == null) return;

            var contact = row.Target as MaxContact;
            if (contact != null)
            {
                Frame.Navigate(typeof(ContactPage), contact.ContactId);
                return;
            }

            var chat = row.Target as MaxChat;
            if (chat != null)
            {
                Frame.Navigate(typeof(ChatPage), new ChatPage.Target { ChatId = chat.ChatId });
                return;
            }

            var hit = row.Target as MessageHit;
            if (hit != null)
            {
                // Открываем чат на найденном сообщении: история грузится
                // с обеих сторон от него, иначе вниз прокрутить нечего.
                Frame.Navigate(typeof(ChatPage), new ChatPage.Target
                {
                    ChatId = hit.ChatId,
                    MessageId = hit.MessageId,
                    MessageTime = hit.Time
                });
                return;
            }

            var found = row.Target as PublicHit;
            if (found != null)
            {
                // Чужой канал: чата у нас нет, открываем по заготовке — она
                // и подпишет на него, если пользователь захочет.
                Frame.Navigate(typeof(ChatPage), new ChatPage.Target
                {
                    ChatId = found.ChatId,
                    Stub = found.ChatStub()
                });
            }
        }
    }
}
