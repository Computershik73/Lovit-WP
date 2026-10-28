using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage.Streams;

namespace Lovit.Core.Transport
{
    /// <summary>
    /// Запасной путь — системный TLS. Используется там, где своей
    /// криптографии нет: библиотека собрана только под ARM, а под эмулятор
    /// её нет вовсе.
    ///
    /// Работает не всегда: у системы свой набор шифров и своё хранилище
    /// корней, пополнить которое нечем, — ровно поэтому и появился
    /// [NativeTlsStream]. Здесь цепочка проверяется вручную по отпечатку
    /// вшитого корня; это не «принимать любой сертификат», но и не защита
    /// от того, что сервер однажды сменит удостоверяющий центр.
    /// </summary>
    public class SystemTlsStream : ITlsStream
    {
        /// <summary>
        /// Отпечатки SHA-1 корня и промежуточного НУЦ Минцифры — их отдаёт
        /// `Certificate.GetHashValue()` на Windows Phone 8.1.
        /// </summary>
        private static readonly string[] TrustedThumbprints =
        {
            "8FF915CCAB7BC16F8C5C8099D53E0E115B3AEC2F",   // Russian Trusted Root CA
            "335D43F53451B781535FF3882DF713D3C14F8A01"    // Russian Trusted Sub CA
        };

        private StreamSocket _socket;
        private DataWriter _writer;
        private DataReader _reader;
        private string _error = "";

        public string Description { get { return "системный TLS 1.2"; } }

        public string Error { get { return _error; } }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            try
            {
                _socket = new StreamSocket();

                // Список заранее: после рукопожатия его уже не спросят, а
                // добавить ошибку в исключения можно только до него.
                _socket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
                _socket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.IncompleteChain);
                _socket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.RevocationInformationMissing);
                _socket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.RevocationFailure);

                var hostName = new HostName(host);
                await _socket.ConnectAsync(hostName, port.ToString());
                await _socket.UpgradeToSslAsync(SocketProtectionLevel.Tls12, hostName);

                if (!IsChainTrusted(_socket.Information))
                {
                    _error = "цепочка сертификатов не та";
                    Close();
                    return false;
                }

                _writer = new DataWriter(_socket.OutputStream);
                _reader = new DataReader(_socket.InputStream);
                _reader.InputStreamOptions = InputStreamOptions.Partial;

                return true;
            }
            catch (Exception failure)
            {
                _error = failure.Message;
                Close();
                return false;
            }
        }

        public async Task<int> ReadAsync(byte[] buffer, int length)
        {
            if (_reader == null) return 0;

            try
            {
                uint loaded = await _reader.LoadAsync((uint)length);
                if (loaded == 0) return 0;

                var chunk = new byte[loaded];
                _reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, buffer, 0, (int)loaded);

                return (int)loaded;
            }
            catch (Exception failure)
            {
                _error = failure.Message;
                return -1;
            }
        }

        public async Task<bool> WriteAsync(byte[] data)
        {
            if (_writer == null) return false;

            try
            {
                _writer.WriteBytes(data);
                await _writer.StoreAsync();
                return true;
            }
            catch (Exception failure)
            {
                _error = failure.Message;
                return false;
            }
        }

        public void Close()
        {
            try { if (_writer != null) _writer.DetachStream(); } catch (Exception) { }
            try { if (_reader != null) _reader.DetachStream(); } catch (Exception) { }
            try { if (_socket != null) _socket.Dispose(); } catch (Exception) { }

            _writer = null;
            _reader = null;
            _socket = null;
        }

        /// <summary>
        /// Проверка цепочки вручную: одно из её звеньев должно быть тем самым
        /// корнем. Системе эта цепочка неизвестна, но неизвестна — не значит
        /// «любая».
        /// </summary>
        private static bool IsChainTrusted(StreamSocketInformation information)
        {
            IReadOnlyList<Certificate> chain = information.ServerIntermediateCertificates;
            if (chain == null || chain.Count == 0) return false;

            foreach (Certificate certificate in chain)
            {
                string thumbprint = Thumbprint(certificate);
                foreach (string trusted in TrustedThumbprints)
                {
                    if (thumbprint == trusted) return true;
                }
            }

            return false;
        }

        private static string Thumbprint(Certificate certificate)
        {
            byte[] hash = certificate.GetHashValue();
            var text = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte value in hash) text.Append(value.ToString("X2"));
            return text.ToString();
        }
    }
}
