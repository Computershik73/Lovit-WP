using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Lovit.Ui
{
    /// <summary>
    /// Свой выбор файла.
    ///
    /// Системный на Windows Phone 8.1 показывает не файлы, а список
    /// приложений, умеющих их отдавать. Такое приложение на телефоне обычно
    /// одно — «Фотографии», — и система уходит прямо в него. Отсюда и
    /// галерея вместо файлов: выбрать что-то кроме снимка через него нельзя
    /// в принципе.
    ///
    /// Поэтому здесь свой обход папок. Доступны не все: телефон открывает
    /// приложению только библиотеки — снимки, музыку, видео — и карту
    /// памяти. Ни «Загрузок», ни общего диска, как на компьютере, в этой
    /// системе просто нет. Что именно откроется, спрашивается у самой
    /// системы при открытии: разрешения зависят от прошивки, и гадать о них
    /// нельзя — папка, которая не открылась, в списке не показывается.
    /// </summary>
    public static class FileBrowser
    {
        private class Entry
        {
            public string Title { get; set; }
            public string Detail { get; set; }
            public StorageFolder Folder { get; set; }
            public StorageFile File { get; set; }

            /// <summary>Недоступную папку показываем, но не открываем.</summary>
            public bool Barred { get; set; }
        }

        /// <summary>
        /// Больше этого в одной папке не показываем: в библиотеке снимков их
        /// бывают тысячи, и разметка на все сразу съела бы память.
        /// </summary>
        private const int MaxEntries = 400;

        private static Popup _popup;
        private static ListView _list;
        private static TextBlock _path;
        private static TaskCompletionSource<StorageFile> _pending;

        /// <summary>Где мы сейчас; пусто — на самом верху, среди папок-корней.</summary>
        private static readonly List<StorageFolder> _trail = new List<StorageFolder>();

        public static Task<StorageFile> ShowAsync()
        {
            if (_pending != null) return _pending.Task;

            _pending = new TaskCompletionSource<StorageFile>();
            _trail.Clear();

            Build();

            HardwareButtons.BackPressed += OnBackPressed;

            var ignored = ShowRootsAsync();
            return _pending.Task;
        }

        #region Разметка

        private static void Build()
        {
            _path = new TextBlock
            {
                FontSize = 20,
                TextTrimming = TextTrimming.WordEllipsis,
                Margin = new Thickness(0, 0, 0, 12)
            };

            _list = new ListView { SelectionMode = ListViewSelectionMode.None };

            var header = new StackPanel { Margin = new Thickness(19, 24, 19, 0) };
            header.Children.Add(new TextBlock
            {
                Text = "выбор файла",
                FontSize = 14,
                Opacity = 0.6
            });
            header.Children.Add(_path);

            var layout = new Grid
            {
                Background = Application.Current.Resources["PhoneBackgroundBrush"] as Brush,
                Width = Window.Current.Bounds.Width,
                Height = Window.Current.Bounds.Height
            };

            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(header, 0);
            Grid.SetRow(_list, 1);

            layout.Children.Add(header);
            layout.Children.Add(_list);

            _popup = new Popup { Child = layout, IsOpen = true };
        }

        private static void Fill(string title, IList<Entry> entries)
        {
            _path.Text = title;
            _list.Items.Clear();

            if (entries.Count == 0)
            {
                _list.Items.Add(new ListViewItem
                {
                    Content = new TextBlock
                    {
                        Text = "здесь пусто",
                        Opacity = 0.6,
                        Margin = new Thickness(19, 12, 19, 12)
                    },
                    IsHitTestVisible = false
                });
                return;
            }

            foreach (Entry entry in entries)
            {
                var text = new StackPanel();

                text.Children.Add(new TextBlock
                {
                    Text = entry.Title,
                    FontSize = 20,
                    TextTrimming = TextTrimming.WordEllipsis
                });

                text.Children.Add(new TextBlock
                {
                    Text = entry.Detail,
                    FontSize = 13,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                });

                // Нажатие ловится самой строкой, а не списком: у готовых
                // ListViewItem, положенных в список руками, список нажатия не
                // разбирает — оттого ни одна папка и не открывалась.
                //
                // Прозрачная подложка обязательна: без заливки нажатие мимо
                // букв проваливается насквозь.
                var plate = new Border
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    Padding = new Thickness(19, 8, 19, 8),
                    Opacity = entry.Barred ? 0.4 : 1,
                    Tag = entry
                };

                plate.Child = text;

                if (!entry.Barred) plate.Tapped += OnEntryTapped;

                _list.Items.Add(plate);
            }
        }

        #endregion

        #region Обход

        /// <summary>
        /// Корни. Какие из них есть на самом деле, знает только система:
        /// каждый проверяется попыткой заглянуть внутрь, и не открывшийся
        /// не показывается вовсе.
        /// </summary>
        private static async Task ShowRootsAsync()
        {
            _trail.Clear();

            var roots = new List<Entry>();

            await AddRootAsync(roots, "Фотографии", KnownFolders.PicturesLibrary);
            await AddRootAsync(roots, "Плёнка", KnownFolders.CameraRoll);
            await AddRootAsync(roots, "Сохранённые картинки", KnownFolders.SavedPictures);
            await AddRootAsync(roots, "Музыка", KnownFolders.MusicLibrary);
            await AddRootAsync(roots, "Видео", KnownFolders.VideosLibrary);
            await AddRootAsync(roots, "Документы", Documents());

            // Карта памяти приходит списком устройств: их может не быть вовсе,
            // а может быть больше одного.
            try
            {
                foreach (StorageFolder card in await KnownFolders.RemovableDevices.GetFoldersAsync())
                {
                    await AddRootAsync(roots, "Карта памяти", card);
                }
            }
            catch (Exception error)
            {
                Log.Write("app", "карта памяти недоступна: " + error.Message);
            }

            // Своя папка: в ней лежит журнал, и отправить его получается прямо
            // отсюда, не выковыривая с телефона отдельно.
            await AddRootAsync(roots, "Папка приложения", ApplicationData.Current.LocalFolder);

            Fill("откуда", roots);
        }

        /// <summary>
        /// Документов на телефоне может не быть: обращение к ним без
        /// разрешения бросает исключение прямо на этой строке, поэтому оно
        /// отдельно от общей проверки.
        /// </summary>
        private static StorageFolder Documents()
        {
            try { return KnownFolders.DocumentsLibrary; }
            catch (Exception) { return null; }
        }

        private static async Task AddRootAsync(IList<Entry> roots, string title, StorageFolder folder)
        {
            if (folder == null)
            {
                roots.Add(new Entry
                {
                    Title = title,
                    Detail = "этого телефона нет такой папки",
                    Barred = true
                });
                return;
            }

            try
            {
                // Заглядываем на один предмет: полный обход библиотеки со
                // снимками занял бы заметное время, а нам нужно лишь понять,
                // пускают ли нас внутрь.
                await folder.GetItemsAsync(0, 1);
            }
            catch (Exception error)
            {
                // Недоступную показываем серой и с причиной, а не прячем:
                // пустой список ничего не объясняет, а так сразу видно, что
                // именно не пустило.
                Log.Write("app", "папка «" + title + "» недоступна: " + error.Message);

                roots.Add(new Entry
                {
                    Title = title,
                    Detail = "недоступна: " + error.Message,
                    Barred = true
                });
                return;
            }

            roots.Add(new Entry { Title = title, Detail = folder.Name, Folder = folder });
        }

        private static async Task OpenAsync(StorageFolder folder)
        {
            var entries = new List<Entry>();

            try
            {
                foreach (StorageFolder nested in await folder.GetFoldersAsync())
                {
                    entries.Add(new Entry { Title = nested.Name, Detail = "папка", Folder = nested });
                    if (entries.Count >= MaxEntries) break;
                }

                foreach (StorageFile file in await folder.GetFilesAsync())
                {
                    if (entries.Count >= MaxEntries) break;

                    string kind = string.IsNullOrEmpty(file.FileType)
                        ? "файл"
                        : file.FileType.TrimStart('.').ToLower();

                    entries.Add(new Entry { Title = file.Name, Detail = kind, File = file });
                }
            }
            catch (Exception error)
            {
                Log.Write("app", "папку не открыть: " + error.Message);

                entries.Add(new Entry
                {
                    Title = "не открылась",
                    Detail = error.Message,
                    Barred = true
                });
            }

            _trail.Add(folder);
            Fill(folder.Name, entries);
        }

        private static async void OnEntryTapped(object sender, TappedRoutedEventArgs e)
        {
            var plate = sender as FrameworkElement;
            var entry = plate != null ? plate.Tag as Entry : null;
            if (entry == null) return;

            if (entry.Folder != null)
            {
                await OpenAsync(entry.Folder);
                return;
            }

            Close(entry.File);
        }

        #endregion

        #region Закрытие

        /// <summary>
        /// Кнопка «назад» поднимает на уровень выше, а с самого верха
        /// закрывает выбор. Системе о нажатии сообщается как об обработанном:
        /// иначе она заодно увела бы со страницы чата.
        /// </summary>
        private static async void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            e.Handled = true;

            if (_trail.Count == 0)
            {
                Close(null);
                return;
            }

            _trail.RemoveAt(_trail.Count - 1);

            if (_trail.Count == 0)
            {
                await ShowRootsAsync();
                return;
            }

            StorageFolder back = _trail[_trail.Count - 1];

            // Открытие само допишет путь, поэтому прежний шаг снимаем.
            _trail.RemoveAt(_trail.Count - 1);
            await OpenAsync(back);
        }

        private static void Close(StorageFile file)
        {
            HardwareButtons.BackPressed -= OnBackPressed;

            if (_popup != null)
            {
                _popup.IsOpen = false;
                _popup.Child = null;
                _popup = null;
            }

            if (_list != null)
            {
                _list.Items.Clear();
                _list = null;
            }

            _path = null;
            _trail.Clear();

            TaskCompletionSource<StorageFile> waiting = _pending;
            _pending = null;

            if (waiting != null) waiting.TrySetResult(file);
        }

        #endregion
    }
}
