using System;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Media;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>
    /// Журнал последних событий.
    ///
    /// Показывается прямо в приложении, потому что достать файл с телефона
    /// иначе нечем: без средств разработчика в `LocalFolder` не заглянуть, а
    /// беда случается ровно тогда, когда телефон не подключён к отладчику.
    /// </summary>
    public sealed partial class LogPage : Page
    {
        public LogPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ShowAsync();
        }

        /// <summary>
        /// Показывается файл, а не память: после падения память пуста, а
        /// причина падения записана как раз в файл — она здесь и нужна.
        /// Строки уходят на диск сразу, так что и свежие в нём уже есть.
        ///
        /// Внизу самое новое, поэтому туда же и прокручиваем.
        /// </summary>
        private async Task ShowAsync()
        {
            string text = await Log.ReadAsync();

            // В отправляемой сборке пишутся только падения — иначе пустой
            // журнал выглядел бы поломкой.
            if (!Log.WritesEverything)
            {
                string note = "В этой сборке записываются только падения: "
                              + "переписка в журнал не попадает.";
                text = string.IsNullOrEmpty(text) ? note : note + Environment.NewLine + Environment.NewLine + text;
            }

            Body.Text = text;

            // В конце самое новое, туда и смотрим: файл переживает
            // перезапуски, и сверху может лежать позавчерашний запуск.
            Body.Select(Body.Text.Length, 0);
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await ShowAsync();
        }

        /// <summary>
        /// Отдать журнал наружу. Своей папки «Документы» у приложений
        /// Windows Phone 8.1 нет, поэтому место выбирает система — а с ней
        /// и то хранилище, которое в ней настроено.
        /// </summary>
        private async void OnSave(object sender, RoutedEventArgs e)
        {
            // Накопленное дописывается перед выгрузкой: иначе последние
            // строки — как раз самые нужные — остались бы только в памяти.
            await Log.FlushAsync();

            string name = "lovit-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm");

            bool saved = await FilePicking.SaveAsync(name, await Log.ReadAsync());

            var dialog = new MessageDialog(saved
                ? "Журнал сохранён."
                : "Не сохранилось — место не выбрано или недоступно.");

            await dialog.ShowAsync();
        }

        private async void OnClear(object sender, RoutedEventArgs e)
        {
            await Log.ClearAsync();
            await ShowAsync();
        }
    }
}
