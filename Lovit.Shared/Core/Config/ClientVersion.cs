namespace Lovit.Core.Config
{
    /// <summary>
    /// Версия клиента, которой приложение представляется серверу.
    ///
    /// Число здесь не украшение: сервер сверяет его со своим и отвечает
    /// «приложение устарело, обновитесь», если сборка слишком старая. Это
    /// первое, что нужно поднять, когда вход перестал работать у всех сразу.
    ///
    /// Значения те же, что в версии для iOS (MAXSpoofingService.m). Меняются
    /// вместе с ней.
    /// </summary>
    public static class ClientVersion
    {
        public const string AppVersion = "26.23.2";
        public const long BuildNumber = 6779;

        /// <summary>
        /// Экран по умолчанию — в том виде, в каком его пишет Android-клиент:
        /// плотность, точки на дюйм и разрешение. Формат «1080x1920 3.0x» —
        /// это запись iOS, и Android-клиенту она не к лицу.
        /// </summary>
        public const string DefaultScreen = "420dpi 420dpi 1080x2340";

        /// <summary>Разрядность, которую называет Android-клиент.</summary>
        public const string Architecture = "arm64-v8a";

        /// <summary>Служба доставки уведомлений у Android — GCM.</summary>
        public const string PushDeviceType = "GCM";
    }
}
