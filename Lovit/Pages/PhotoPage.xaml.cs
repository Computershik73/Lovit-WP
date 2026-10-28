using System.Collections.Generic;
using System.ComponentModel;
using Lovit.Core.Media;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Фотография на весь экран. Порт MAXPhotoViewController.
    ///
    /// Снимки одного сообщения листаются вбок. Раньше их приходилось
    /// смотреть по одному, возвращаясь в переписку за каждым: вбок на
    /// телефоне листаются разделы приложения, и жест казался занятым. Здесь
    /// разделов нет — страница своя, во весь экран, — и мешать ему нечему.
    /// </summary>
    public sealed partial class PhotoPage : Page
    {
        /// <summary>
        /// Что открыть: все снимки сообщения и тот, с которого начали.
        /// Одиночный снимок приходит сюда же простой строкой.
        /// </summary>
        public class Album
        {
            public IList<string> Urls { get; set; }
            public int At { get; set; }
        }

        /// <summary>Одна страница просмотра.</summary>
        public class Shot : INotifyPropertyChanged
        {
            private ImageSource _photo;

            public string Url { get; set; }

            public ImageSource Photo
            {
                get { return _photo; }
                set
                {
                    _photo = value;

                    PropertyChangedEventHandler handler = PropertyChanged;
                    if (handler != null) handler(this, new PropertyChangedEventArgs("Photo"));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<Shot> _shots = new List<Shot>();

        public PhotoPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Крупные копии живут не дольше самого просмотра: каждая весит
        /// столько же, сколько весь остальной кэш, и, задержавшись, они
        /// вытеснили бы из него всю переписку — та потом грузилась бы заново.
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Pages.ItemsSource = null;

            foreach (Shot shot in _shots)
            {
                shot.Photo = null;
                ImageCache.Forget(shot.Url, ImageCache.ScreenSide);
            }

            _shots.Clear();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var album = e.Parameter as Album;

            IList<string> urls = album != null ? album.Urls : null;
            int at = album != null ? album.At : 0;

            if (urls == null)
            {
                var single = e.Parameter as string;
                urls = string.IsNullOrEmpty(single) ? null : new List<string> { single };
                at = 0;
            }

            if (urls == null || urls.Count == 0)
            {
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            foreach (string url in urls)
            {
                // Пока крупная не разобралась, показываем ту, что уже была в
                // пузыре: лучше мягкая картинка сразу, чем пустота на секунду.
                _shots.Add(new Shot
                {
                    Url = url,
                    Photo = ImageCache.Cached(url, ImageCache.ScreenSide)
                            ?? ImageCache.Cached(url, ImageCache.PhotoSide)
                });
            }

            Pages.ItemsSource = _shots;

            if (at < 0 || at >= _shots.Count) at = 0;
            Pages.SelectedIndex = at;

            // Выбор мог и не смениться — тогда о нём никто не скажет, а
            // крупную копию всё равно надо разобрать.
            var ignored = ShowAsync(_shots[at]);
        }

        /// <summary>
        /// Пролистнули к соседнему. Крупная копия разбирается только для
        /// того, что на экране: разбирать соседей заранее — верный способ
        /// остаться без памяти на третьем снимке.
        /// </summary>
        private async void OnPageChanged(object sender, SelectionChangedEventArgs e)
        {
            var shot = Pages.SelectedItem as Shot;
            if (shot == null) return;

            await ShowAsync(shot);
        }

        private async System.Threading.Tasks.Task ShowAsync(Shot shot)
        {
            ErrorText.Visibility = Visibility.Collapsed;

            ImageSource ready = ImageCache.Cached(shot.Url, ImageCache.ScreenSide);
            if (ready != null)
            {
                shot.Photo = ready;
                return;
            }

            Progress.Visibility = Visibility.Visible;
            ImageSource loaded = await ImageCache.LoadAsync(shot.Url, ImageCache.ScreenSide);
            Progress.Visibility = Visibility.Collapsed;

            // Пока качали, могли пролистнуть дальше — тогда это уже не та
            // страница, и говорить о ней нечего.
            if (Pages.SelectedItem != shot) return;

            if (loaded == null)
            {
                if (shot.Photo == null) ErrorText.Visibility = Visibility.Visible;
                return;
            }

            shot.Photo = loaded;
        }

        /// <summary>
        /// Подложка держится размером с окно. Это и есть то, что делает
        /// щипок осмысленным: содержимое при единичном увеличении ровно
        /// заполняет экран, и увеличивать его есть откуда.
        /// </summary>
        private void OnViewportChanged(object sender, SizeChangedEventArgs e)
        {
            FrameworkElement stage = StageOf(sender);
            if (stage == null) return;

            stage.Width = e.NewSize.Width;
            stage.Height = e.NewSize.Height;
        }

        /// <summary>
        /// Двойное нажатие: приблизить и вернуть обратно. Щипком тоже
        /// можно, но одной рукой он неудобен.
        ///
        /// Приближается то место, по которому нажали, а не левый верхний
        /// угол: иначе двойное нажатие по лицу показывает небо над ним.
        /// </summary>
        private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var zoom = sender as ScrollViewer;
            FrameworkElement stage = StageOf(sender);

            if (zoom == null || stage == null) return;

            if (zoom.ZoomFactor > 1.01f)
            {
                zoom.ChangeView(0, 0, 1.0f);
                return;
            }

            const float wanted = 2.5f;

            Point tap = e.GetPosition(stage);

            // Точка нажатия — в несжатых мерках подложки; умножение
            // переводит её в мерки увеличенного содержимого.
            zoom.ChangeView(tap.X * wanted - zoom.ViewportWidth / 2,
                            tap.Y * wanted - zoom.ViewportHeight / 2,
                            wanted);
        }

        /// <summary>
        /// Подложка своей страницы. Имени у неё нет: страниц несколько, и
        /// каждая собрана по одному образцу, — поэтому берём её у той
        /// прокрутки, из которой пришло событие.
        /// </summary>
        private static FrameworkElement StageOf(object sender)
        {
            var zoom = sender as ScrollViewer;
            return zoom != null ? zoom.Content as FrameworkElement : null;
        }
    }
}
