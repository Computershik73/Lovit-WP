using System.Collections.Generic;

namespace Lovit.Core.Config
{
    /// <summary>
    /// Устройство, которым приложение представляется серверу. Порт
    /// MAXDevicePresets из версии для iOS (а туда — из Komet,
    /// lib/core/config/device_presets.dart).
    /// </summary>
    public class DevicePreset
    {
        public string DeviceName { get; set; }
        public string OsVersion { get; set; }
        public string Screen { get; set; }
        public string Timezone { get; set; }

        public DevicePreset(string deviceName, string osVersion, string screen, string timezone)
        {
            DeviceName = deviceName;
            OsVersion = osVersion;
            Screen = screen;
            Timezone = timezone;
        }
    }

    /// <summary>
    /// Набор устройств для подмены отпечатка сессии.
    ///
    /// Клиента MAX под Windows Phone нет и никогда не было, поэтому
    /// приложение называется Android-телефоном. Какой именно это телефон —
    /// выбирается здесь; из iOS-версии перенесены только Android-устройства,
    /// прочие её списки (iOS, настольные, браузеры) здесь бессмысленны.
    /// </summary>
    public static class DevicePresets
    {
        /// <summary>
        /// Что берётся, пока пользователь не выбрал другое. Русский телефон
        /// с московским поясом — самое неприметное, чем может оказаться
        /// клиент, говорящий с сервером по-русски.
        /// </summary>
        public const string DefaultName = "Xiaomi 14 Ultra";

        private static readonly List<DevicePreset> _all = new List<DevicePreset>
        {
            new DevicePreset("Xiaomi 14 Ultra", "Android 14", "xxhdpi 450dpi 1440x3200", "Europe/Moscow"),
            new DevicePreset("Samsung Galaxy S24 Ultra", "Android 14", "xxhdpi 450dpi 1440x3120", "Europe/Berlin"),
            new DevicePreset("Samsung Galaxy S24", "Android 14", "xxhdpi 450dpi 1440x3120", "America/Vancouver"),
            new DevicePreset("Samsung Galaxy S21 Ultra", "Android 13", "xxhdpi 460dpi 1440x3200", "Europe/London"),
            new DevicePreset("Samsung Galaxy A54", "Android 14", "xxhdpi 400dpi 1080x2340", "Australia/Sydney"),
            new DevicePreset("Samsung Galaxy A55", "Android 14", "xxhdpi 460dpi 1440x3200", "Europe/Madrid"),
            new DevicePreset("Samsung Galaxy S20 FE", "Android 13", "xxhdpi 400dpi 1080x2400", "Europe/Berlin"),
            new DevicePreset("Samsung Galaxy S24 Edge", "Android 14", "xxhdpi 460dpi 1440x3200", "Asia/Tokyo"),
            new DevicePreset("Samsung Galaxy Z Fold 4", "Android 13", "xhdpi 350dpi 1812x2176", "America/Denver"),
            new DevicePreset("Samsung Galaxy Z Flip 3", "Android 12", "xhdpi 370dpi 1080x2640", "America/Mexico_City"),
            new DevicePreset("Google Pixel 8 Pro", "Android 14", "xxhdpi 430dpi 1344x2992", "America/New_York"),
            new DevicePreset("Google Pixel 9 Pro", "Android 14", "xxhdpi 430dpi 1344x2992", "Europe/London"),
            new DevicePreset("Google Pixel 7a", "Android 14", "xxhdpi 400dpi 1080x2400", "Europe/Amsterdam"),
            new DevicePreset("Google Pixel 6", "Android 12", "xxhdpi 420dpi 1080x2400", "America/Chicago"),
            new DevicePreset("Xiaomi 13 Pro", "Android 13", "xxhdpi 460dpi 1440x3200", "Asia/Shanghai"),
            new DevicePreset("Xiaomi 12T", "Android 13", "xxhdpi 460dpi 1440x3200", "Europe/Rome"),
            new DevicePreset("Xiaomi K30 Ultra", "Android 13", "xxhdpi 450dpi 1440x3200", "Asia/Shanghai"),
            new DevicePreset("Xiaomi Mi 10T Pro", "Android 12", "xxhdpi 460dpi 1440x3200", "America/Sao_Paulo"),
            new DevicePreset("Redmi Note 11 Pro", "Android 12", "xxhdpi 420dpi 1080x2400", "Europe/Rome"),
            new DevicePreset("Poco F3", "Android 11", "xxhdpi 420dpi 1080x2400", "Europe/Madrid"),
            new DevicePreset("Poco F3 GT", "Android 13", "xxhdpi 420dpi 1080x2400", "Asia/Kolkata"),
            new DevicePreset("OnePlus 12", "Android 14", "xxhdpi 450dpi 1440x3168", "Asia/Kolkata"),
            new DevicePreset("OnePlus 11", "Android 13", "xxhdpi 450dpi 1440x3216", "Australia/Sydney"),
            new DevicePreset("OnePlus 9 Pro", "Android 13", "xxhdpi 460dpi 1440x3216", "America/Toronto"),
            new DevicePreset("OnePlus 9", "Android 12", "xxhdpi 420dpi 1080x2400", "Europe/Stockholm"),
            new DevicePreset("OnePlus 8 Pro", "Android 12", "xxhdpi 450dpi 1440x3168", "America/New_York"),
            new DevicePreset("Oppo Find N3", "Android 14", "xxhdpi 430dpi 1440x3168", "Europe/Paris"),
            new DevicePreset("Realme GT Master Edition", "Android 13", "xxhdpi 400dpi 1080x2400", "Asia/Dubai"),
            new DevicePreset("Realme GT Neo 3", "Android 13", "xxhdpi 460dpi 1440x3200", "Asia/Hong_Kong"),
            new DevicePreset("Vivo V29", "Android 14", "xxhdpi 460dpi 1440x3200", "Asia/Bangkok"),
            new DevicePreset("Sony Xperia 1 V", "Android 14", "xxxhdpi 560dpi 1644x3840", "Asia/Tokyo"),
            new DevicePreset("Motorola Edge 30 Pro", "Android 13", "xxhdpi 400dpi 1080x2400", "America/Sao_Paulo"),
            new DevicePreset("Motorola Edge 40", "Android 13", "xxhdpi 400dpi 1080x2400", "Asia/Dubai"),
            new DevicePreset("Nothing Phone (2)", "Android 14", "xxhdpi 400dpi 1080x2412", "America/Toronto"),
            new DevicePreset("Asus ROG Phone 6", "Android 13", "xxhdpi 420dpi 1080x2448", "Asia/Taipei"),
            new DevicePreset("Huawei P60 Pro", "Android 13 (EMUI)", "xxhdpi 430dpi 1220x2700", "Europe/Paris"),
            new DevicePreset("Nokia 8", "Android 10", "xxhdpi 380dpi 1440x2560", "Europe/Helsinki"),
            new DevicePreset("LG G7 ThinQ", "Android 9", "xxhdpi 450dpi 1440x3120", "Asia/Seoul")
        };

        public static IList<DevicePreset> All { get { return _all; } }

        /// <summary>
        /// Имя наугад — им приложение представится в первый раз.
        ///
        /// Считаем настоящей случайностью, а не Random: тот берёт зерно из
        /// часов, а первый запуск у многих приходится на одну и ту же минуту
        /// после рассылки сборки — и половина выбрала бы один и тот же
        /// телефон.
        /// </summary>
        public static string Random()
        {
            uint number = Windows.Security.Cryptography.CryptographicBuffer
                .GenerateRandomNumber();

            return _all[(int)(number % (uint)_all.Count)].DeviceName;
        }

        /// <summary>Устройство по имени; неизвестное имя даёт то, что по умолчанию.</summary>
        public static DevicePreset Named(string deviceName)
        {
            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (DevicePreset preset in _all)
                {
                    if (preset.DeviceName == deviceName) return preset;
                }
            }

            foreach (DevicePreset preset in _all)
            {
                if (preset.DeviceName == DefaultName) return preset;
            }

            return _all[0];
        }
    }
}
