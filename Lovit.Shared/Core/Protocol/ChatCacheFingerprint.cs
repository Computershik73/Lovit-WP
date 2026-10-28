using System;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace Lovit.Core.Protocol
{
    /// <summary>
    /// Отпечаток сборки клиента. Порт MAXChatCacheFingerprint.
    ///
    /// Настоящий Android-клиент считает три хеша от собственных файлов —
    /// подписи пакета, dex-кода и нативной библиотеки — подмешивая туда
    /// callsSeed из ответа на приветствие и признак устройства. Девяносто
    /// шесть байт, которые получаются, уезжают в `mode` при запросе кода и
    /// в `chatCacheFingerprint` при входе.
    ///
    /// Без них сервер видит незнакомую сборку и ведёт себя так, будто всё в
    /// порядке: отвечает удачей на запрос кода, но SMS не отправляет вовсе.
    /// Хеши самих файлов подставлены готовыми — считать их нам не от чего.
    /// </summary>
    public static class ChatCacheFingerprint
    {
        private const string SignatureDigest = "1684414033eb263e2c615f8b7df5ed8793850a07656304997fbf07e9e21e1e93";
        private const string DexDigest = "38cff46f392dc1734c308be011c2f0d8da152a390b41063dbb2c913e3032f4b3";
        private const string SoDigest = "634ecc42b246784d975f180b4fecf903df235cdf0476da47163a85630eb1a6a8";

        /// <summary>Порядок частей тот же, что у оригинала: подпись, dex, библиотека.</summary>
        public static byte[] Compute(long callsSeed, string deviceId)
        {
            byte[] seed = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                seed[i] = (byte)((ulong)callsSeed >> (56 - 8 * i));
            }

            byte[] device = Encoding.UTF8.GetBytes(deviceId ?? "");

            // Полное имя: здесь есть ещё и Windows.Storage.Streams.Buffer.
            var result = new byte[96];
            System.Buffer.BlockCopy(Sha256(FromHex(SignatureDigest), seed, device), 0, result, 0, 32);
            System.Buffer.BlockCopy(Sha256(FromHex(DexDigest), seed, device), 0, result, 32, 32);
            System.Buffer.BlockCopy(Sha256(FromHex(SoDigest), seed, device), 0, result, 64, 32);

            return result;
        }

        private static byte[] Sha256(byte[] first, byte[] second, byte[] third)
        {
            HashAlgorithmProvider provider =
                HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);

            CryptographicHash hash = provider.CreateHash();
            hash.Append(CryptographicBuffer.CreateFromByteArray(first));
            hash.Append(CryptographicBuffer.CreateFromByteArray(second));
            hash.Append(CryptographicBuffer.CreateFromByteArray(third));

            byte[] digest;
            CryptographicBuffer.CopyToByteArray(hash.GetValueAndReset(), out digest);
            return digest;
        }

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
