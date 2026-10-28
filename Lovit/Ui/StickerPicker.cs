using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Backend;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Models;
using Windows.Phone.UI.Input;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Lovit.Ui
{
    /// <summary>
    /// Выбор стикера.
    ///
    /// Слоем поверх переписки, а не отдельной страницей: выбор стикера — это
    /// часть набора сообщения, и уходить ради него со страницы чата
    /// неправильно. Заодно так сохраняется всё набранное и место в переписке.
    ///
    /// Картинки стикеров у MAX в WebP, который система не знает, — их
    /// разбирает наш декодер через общий запас картинок.
    /// </summary>
    public static class StickerPicker
    {
        /// <summary>Сколько стикеров в ряду.</summary>
        private const int Columns = 4;

        /// <summary>Сторона картинки в точках — под ширину телефонного экрана.</summary>
        private const int Side = 96;

        private static Popup _popup;
        private static Panel _grid;
        private static TextBlock _title;
        private static TaskCompletionSource<MaxSticker> _pending;

        /// <summary>
        /// Показать и дождаться выбора. Возвращает null, если человек ушёл
        /// ни с чем.
        /// </summary>
        public static Task<MaxSticker> ShowAsync()
        {
            if (_pending != null) return _pending.Task;

            _pending = new TaskCompletionSource<MaxSticker>();

            Build();
            HardwareButtons.BackPressed += OnBackPressed;

            var ignored = FillAsync();
            return _pending.Task;
        }

        #region Разметка

        private static void Build()
        {
            _title = new TextBlock
            {
                Text = "загружаем…",
                FontSize = 20,
                Margin = new Thickness(0, 0, 0, 8),
                TextTrimming = TextTrimming.WordEllipsis
            };

            var search = new TextBox
            {
                PlaceholderText = "поиск по значкам",
                Margin = new Thickness(0, 0, 0, 8)
            };

            search.TextChanged += OnSearchChanged;

            _grid = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = Side,
                ItemHeight = Side,
                MaximumRowsOrColumns = Columns
            };

            var scroll = new ScrollViewer
            {
                Content = _grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var header = new StackPanel { Margin = new Thickness(19, 24, 19, 0) };
            header.Children.Add(_title);
            header.Children.Add(search);

            var layout = new Grid
            {
                Background = Application.Current.Resources["PhoneBackgroundBrush"] as Brush,
                Width = Window.Current.Bounds.Width,
                Height = Window.Current.Bounds.Height
            };

            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(header, 0);
            Grid.SetRow(scroll, 1);

            layout.Children.Add(header);
            layout.Children.Add(scroll);

            _popup = new Popup { Child = layout, IsOpen = true };
        }

        #endregion

        #region Наполнение

        /// <summary>
        /// Показываем всё разом: недавние, потом свои наборы подряд. Разбивать
        /// на вкладки на этом экране некуда — он и так узкий, а полос
        /// прокрутки хватает одной.
        /// </summary>
        private static async Task FillAsync()
        {
            await StickersModule.Instance.LoadAsync();
            if (_grid == null) return;

            _grid.Children.Clear();

            var shown = new List<MaxSticker>();

            foreach (MaxSticker sticker in StickersModule.Instance.Recent) shown.Add(sticker);

            foreach (MaxStickerSet set in StickersModule.Instance.Sets)
            {
                IList<MaxSticker> stickers =
                    await StickersModule.Instance.StickersOfAsync(set);

                if (_grid == null) return;

                foreach (MaxSticker sticker in stickers) shown.Add(sticker);
            }

            Show(shown, shown.Count == 0
                ? "стикеров нет"
                : "стикеры — " + shown.Count);
        }

        private static void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            var box = sender as TextBox;
            if (box == null) return;

            string term = box.Text.Trim();

            if (term.Length == 0)
            {
                var ignored = FillAsync();
                return;
            }

            IList<MaxSticker> found = StickersModule.Instance.Search(term);
            Show(found, found.Count == 0 ? "ничего не нашлось" : "найдено — " + found.Count);
        }

        private static void Show(IList<MaxSticker> stickers, string title)
        {
            if (_grid == null) return;

            _title.Text = title;
            _grid.Children.Clear();

            foreach (MaxSticker sticker in stickers)
            {
                if (string.IsNullOrEmpty(sticker.Url)) continue;

                var image = new Image
                {
                    Width = Side - 12,
                    Height = Side - 12,
                    Margin = new Thickness(6),
                    Stretch = Stretch.Uniform
                };

                // Заливка обязательна: без неё нажатие мимо самой картинки —
                // а у стикера края прозрачные — проваливается насквозь.
                var plate = new Border
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    Child = image,
                    Tag = sticker
                };

                plate.Tapped += OnStickerTapped;

                _grid.Children.Add(plate);

                LoadAsync(image, sticker.Url);
            }
        }

        private static async void LoadAsync(Image target, string url)
        {
            ImageSource picture = await ImageCache.LoadAsync(url, Side * 2);
            if (picture != null) target.Source = picture;
        }

        private static void OnStickerTapped(object sender, TappedRoutedEventArgs e)
        {
            var plate = sender as FrameworkElement;
            var sticker = plate != null ? plate.Tag as MaxSticker : null;

            if (sticker != null) Close(sticker);
        }

        #endregion

        private static void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            e.Handled = true;
            Close(null);
        }

        private static void Close(MaxSticker chosen)
        {
            HardwareButtons.BackPressed -= OnBackPressed;

            if (_popup != null)
            {
                _popup.IsOpen = false;
                _popup.Child = null;
                _popup = null;
            }

            // Картинки отпускаем сразу: их тут под сотню, и держать их за
            // закрытым слоем незачем.
            if (_grid != null)
            {
                _grid.Children.Clear();
                _grid = null;
            }

            _title = null;

            TaskCompletionSource<MaxSticker> waiting = _pending;
            _pending = null;

            if (waiting != null) waiting.TrySetResult(chosen);
        }
    }
}
