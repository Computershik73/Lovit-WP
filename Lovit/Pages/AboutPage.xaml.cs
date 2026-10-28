using Lovit.Backend;
using Lovit.Core.Config;
using Lovit.Core.Storage;
using Windows.ApplicationModel;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Lovit.Pages
{
    /// <summary>О приложении. Порт MAXAboutViewController.</summary>
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            PackageVersion version = Package.Current.Id.Version;
            VersionText.Text = string.Format("Версия {0}.{1}.{2}",
                version.Major, version.Minor, version.Build);

            string transport = MaxApi.Instance.TransportDescription;
            TransportText.Text = string.IsNullOrEmpty(transport)
                ? "Соединение: нет"
                : "Соединение: " + transport;

            // Чем приложение представляется серверу — здесь же: это первое,
            // что нужно знать, если сервер вдруг начал отказывать.
            DevicePreset device = DevicePresets.Named(AppSettings.DeviceName);

            DeviceText.Text = string.Format("Серверу мы {0}, {1}, MAX {2} ({3})",
                device.DeviceName, device.OsVersion,
                ClientVersion.AppVersion, ClientVersion.BuildNumber);
        }
    }
}
