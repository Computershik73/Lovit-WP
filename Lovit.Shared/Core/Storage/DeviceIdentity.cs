using System;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace Lovit.Core.Storage
{
    /// <summary>
    /// Постоянные приметы устройства для SESSION_INIT. Порт MAXDeviceIdentity.
    ///
    /// Их нельзя вычислять заново при каждом запуске: сервер считает сессию
    /// новой при смене `deviceId`, а это письмо «вход с нового устройства»
    /// и, если у аккаунта есть пароль, требование его ввести. Поэтому всё
    /// рождается один раз и хранится.
    /// </summary>
    public static class DeviceIdentity
    {
        private static ApplicationDataContainer Local
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        /// <summary>
        /// Признак устройства: восемь случайных байт шестнадцатеричной
        /// записью, шестнадцать знаков. Ровно столько же и в том же виде
        /// шлёт Android-клиент — randomHex(8) в Komet и
        /// randomHexWithByteCount:8 в версии для iOS. Длина здесь не
        /// мелочь: она уезжает в SESSION_INIT и подмешивается в отпечаток
        /// сборки, а тридцать два знака вместо шестнадцати не бывают ни у
        /// одного настоящего клиента.
        /// </summary>
        public static string DeviceId
        {
            get { return Persistent("deviceId", () => RandomHex(8)); }
        }

        /// <summary>
        /// Признак установки — UUID четвёртой версии со строчными буквами и
        /// чёрточками, как его пишут оба исходника (uuidV4). Запись без
        /// чёрточек выдавала бы клиента так же верно, как и неверная длина.
        /// </summary>
        public static string InstanceId
        {
            get { return Persistent("instanceId", () => Guid.NewGuid().ToString("D")); }
        }

        private static string RandomHex(int byteCount)
        {
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(
                CryptographicBuffer.GenerateRandom((uint)byteCount), out bytes);

            var hex = new StringBuilder(byteCount * 2);
            foreach (byte value in bytes) hex.Append(value.ToString("x2"));

            return hex.ToString();
        }

        /// <summary>
        /// Номер сессии клиента: случайное число, новое на каждый запуск.
        ///
        /// Единственный из трёх, который не хранится, — и это не упущение.
        /// Он означает не устройство, а именно запуск приложения на нём,
        /// поэтому оба исходника заводят его заново в памяти
        /// (device_identity.dart:14-15, MAXDeviceIdentity.m:28-30) и берут
        /// случайное из тех же 1..0x7FFFFFFF. Метка времени на этом месте
        /// узнаётся с одного взгляда: у настоящих клиентов числа раскиданы
        /// по всему промежутку, а не жмутся к нынешнему мгновению.
        /// </summary>
        public static long ClientSessionId
        {
            get
            {
                lock (_sessionLock)
                {
                    if (_clientSessionId == 0)
                    {
                        byte[] bytes;
                        CryptographicBuffer.CopyToByteArray(
                            CryptographicBuffer.GenerateRandom(4), out bytes);

                        long value = ((long)bytes[0] << 24)
                                     | ((long)bytes[1] << 16)
                                     | ((long)bytes[2] << 8)
                                     | bytes[3];

                        // Ровно тот же промежуток, что даёт
                        // nextInt(0x7FFFFFFF) + 1, — от единицы до
                        // 0x7FFFFFFF включительно.
                        _clientSessionId = value % 0x7FFFFFFF + 1;
                    }

                    return _clientSessionId;
                }
            }
        }

        private static readonly object _sessionLock = new object();
        private static long _clientSessionId;

        private static string Persistent(string key, Func<string> create)
        {
            object value;
            if (Local.Values.TryGetValue(key, out value))
            {
                var text = value as string;
                if (!string.IsNullOrEmpty(text)) return text;
            }

            string fresh = create();
            Local.Values[key] = fresh;
            return fresh;
        }
    }
}
