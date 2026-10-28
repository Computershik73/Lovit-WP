namespace Lovit.Core.Protocol
{
    /// <summary>
    /// Коды операций MAX. Взяты из версии для iOS
    /// (src/Core/Protocol/MAXOpcodes.h), там же разобраны по Komet.
    ///
    /// Здесь только те, что нужны этому клиенту: остальные полторы сотни
    /// перечислять незачем — список должен читаться, а не быть полным.
    /// </summary>
    public static class Opcodes
    {
        public const int Ping = 1;
        public const int SessionInit = 6;
        public const int ContactsGet = 8;

        public const int AuthRequest = 17;   // запрос кода в SMS
        public const int Auth = 18;          // проверка кода
        public const int Login = 19;         // вход по токену
        public const int Logout = 20;
        public const int Config = 22;        // настройки, в том числе тишина чата
        public const int AuthConfirm = 23;   // создание профиля новым номером

        // Пароль для входа (двухфакторная аутентификация)
        public const int Auth2faDetails = 104;
        public const int AuthValidatePassword = 107;
        public const int AuthValidateHint = 108;
        public const int AuthVerifyEmail = 109;
        public const int AuthCheckEmail = 110;
        public const int AuthSet2fa = 111;
        public const int AuthCreateTrack = 112;
        public const int AuthCheckPassword = 113;
        public const int AuthLoginCheckPassword = 115;

        // Контакты
        public const int ContactInfo = 32;
        public const int ContactUpdate = 34;
        public const int ContactPresence = 35;
        public const int ContactList = 36;

        // Чаты
        public const int ChatInfo = 48;
        public const int ChatHistory = 49;
        public const int ChatMark = 50;
        public const int ChatMedia = 51;
        public const int ChatsList = 53;
        public const int ChatClear = 54;
        public const int ChatUpdate = 55;
        public const int ChatJoin = 57;
        public const int ChatLeave = 58;
        public const int ChatMembers = 59;
        public const int PublicSearch = 60;

        // Сообщения
        public const int MsgSend = 64;
        public const int MsgTyping = 65;
        public const int MsgDelete = 66;
        public const int MsgEdit = 67;
        public const int ChatSearch = 68;
        public const int ChatSubscribe = 75;
        public const int ChatMembersUpdate = 77;

        // Вложения. Заливка идёт в два шага: этими запросами берётся адрес
        // на CDN, а тело уходит уже обычным POST мимо протокола.
        // Стикеры. Наборы и сами стикеры лежат в общем хранилище «предметов»,
        // поэтому опкоды у них общие, а вид задаётся полем `type`.
        public const int AssetsGet = 26;
        public const int AssetsUpdate = 27;
        public const int AssetsGetByIds = 28;

        public const int PhotoUpload = 80;

        /// <summary>
        /// Адрес для видео, кружка и голосового — все трое идут этим же
        /// запросом, а что именно грузится, задаётся полями `uploaderType` и
        /// `type`.
        /// </summary>
        public const int VideoUpload = 82;

        /// <summary>Адреса видео — набором качеств, а не одной ссылкой.</summary>
        public const int VideoPlay = 83;

        public const int FileUpload = 87;
        public const int FileDownload = 88;

        /// <summary>Что стоит за ссылкой: чат, канал или человек.</summary>
        public const int LinkInfo = 89;

        // Реакции. Ставятся и снимаются разными запросами, а изменения —
        // и свои, и чужие — сервер потом присылает пушем.
        public const int MsgReaction = 178;
        public const int MsgCancelReaction = 179;

        // Боты
        public const int MsgSendCallback = 118;

        // Папки
        public const int FoldersGet = 272;

        // Пуши от сервера
        public const int NotifMessage = 128;
        public const int NotifTyping = 129;
        public const int NotifMark = 130;
        public const int NotifContact = 131;
        public const int NotifPresence = 132;
        public const int NotifConfig = 134;
        public const int NotifChat = 135;

        /// <summary>
        /// Входящий звонок. В пуше — кто звонит, номер разговора и `vcp`:
        /// сжатые параметры, по которым открывается сигнализация звонка
        /// (см. Backend/Calls/CallParams).
        /// </summary>
        public const int NotifCallStart = 137;
        public const int NotifMsgDelete = 142;
        public const int NotifMsgReactions = 155;
        public const int NotifFolders = 277;
    }
}
