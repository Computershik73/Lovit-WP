using System.Threading.Tasks;

namespace Lovit.Core.Transport
{
    /// <summary>
    /// Защищённый поток до сервера. Реализаций две: своя криптография на
    /// OpenSSL и системная — на случай, если своей на этой сборке нет.
    /// </summary>
    public interface ITlsStream
    {
        /// <summary>Больше нуля — прочитано; ноль — закрыто; -2 — данных пока нет.</summary>
        Task<int> ReadAsync(byte[] buffer, int length);

        Task<bool> WriteAsync(byte[] data);

        Task<bool> ConnectAsync(string host, int port);

        void Close();

        /// <summary>Чем и о чём договорились — для настроек и разбора отказов.</summary>
        string Description { get; }

        /// <summary>Причина последнего отказа.</summary>
        string Error { get; }
    }
}
