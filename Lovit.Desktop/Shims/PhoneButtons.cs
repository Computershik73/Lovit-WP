using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Windows.Phone.UI.Input
{
    /// <summary>
    /// Железная кнопка «назад» — та самая, которой на настольной Windows нет.
    ///
    /// Это подставка, а не подделка ради подделки. Общий код приложения
    /// подписан на телефонное событие в трёх местах, и переписывать их
    /// значило бы менять исходники самого приложения — а они здесь только
    /// читаются. Проще объявить своё пространство имён с тем же названием:
    /// код подключается ссылкой и собирается слово в слово, а событие ему
    /// подаёт уже здешняя стрелка в заголовке окна.
    ///
    /// Стрелка эта, в отличие от телефонной кнопки, не показывается сама:
    /// её видимостью распоряжается приложение. Поэтому здесь же за ней и
    /// следим — зажигаем ровно тогда, когда есть куда возвращаться.
    /// </summary>
    public static class HardwareButtons
    {
        private static EventHandler<BackPressedEventArgs> _handlers;
        private static bool _armed;

        public static event EventHandler<BackPressedEventArgs> BackPressed
        {
            add
            {
                _handlers += value;
                Arm();
            }
            remove { _handlers -= value; }
        }

        /// <summary>
        /// Дождаться окна и только потом подключаться к системной стрелке.
        ///
        /// Ждать приходится всерьёз. Первая подписка случается в самом
        /// начале, ещё в устройстве приложения, — окна в этот миг нет, а
        /// `SystemNavigationManager.GetForCurrentView()` без окна не просто
        /// отказывает, а падает: внутри он берёт окно и сразу читает по
        /// пустой ссылке. Это падение в чужом, неуправляемом коде, и никакой
        /// перехват его не ловит — приложение исчезало сразу после того, как
        /// на экране появлялась рамка окна.
        ///
        /// Поэтому здесь не спрашивают ничего у системы, а подписываются на
        /// отрисовку: она начинается тогда, когда рисовать уже есть где.
        /// Первый же кадр, на котором окно с рамкой на месте, и есть нужный
        /// миг; после него отрисовку отпускаем.
        /// </summary>
        private static void Arm()
        {
            if (_armed) return;

            _armed = true;
            CompositionTarget.Rendering += OnFrameDrawn;
        }

        private static void OnFrameDrawn(object sender, object e)
        {
            if (Window.Current == null) return;

            var frame = Window.Current.Content as Frame;
            if (frame == null) return;

            CompositionTarget.Rendering -= OnFrameDrawn;

            try
            {
                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }
            catch (Exception)
            {
                return;
            }

            frame.Navigated += (source, argument) => ShowArrow(frame.CanGoBack);
            ShowArrow(frame.CanGoBack);
        }

        private static void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            EventHandler<BackPressedEventArgs> handlers = _handlers;
            if (handlers == null) return;

            var mine = new BackPressedEventArgs();
            handlers(sender, mine);

            e.Handled = mine.Handled;
        }

        private static void ShowArrow(bool shown)
        {
            try
            {
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    shown ? AppViewBackButtonVisibility.Visible
                          : AppViewBackButtonVisibility.Collapsed;
            }
            catch (Exception) { }
        }
    }

    /// <summary>Нажали «назад». `Handled` означает «мы разобрались сами».</summary>
    public sealed class BackPressedEventArgs
    {
        public bool Handled { get; set; }
    }
}
