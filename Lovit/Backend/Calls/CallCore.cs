using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Lovit.Backend.Calls
{
    /// <summary>
    /// Медиаядро звонка — родной код в LovitTls.dll (native/LovitTls/lovit_call.c):
    /// ICE, STUN/TURN, DTLS-SRTP и RTP. Здесь только перевод вызовов.
    ///
    /// Ядро однопоточное: все вызовы — с одного потока. Его заводит
    /// CallSession, и только он сюда и ходит.
    /// </summary>
    internal sealed class CallCore : IDisposable
    {
        private const string Library = "LovitTls.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_call_create();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_local(IntPtr handle, byte[] output, int capacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_add_host(IntPtr handle, byte[] ip);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_add_stun(IntPtr handle, byte[] host, int port);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_add_turn(IntPtr handle, byte[] host, int port,
                                                      byte[] user, byte[] password);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_set_remote(IntPtr handle, byte[] ufrag, byte[] password,
                                                        byte[] fingerprint, int opusPt);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_set_roles(IntPtr handle, int controlling, int dtlsClient);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_add_remote(IntPtr handle, byte[] ip, int port, uint priority);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_poll(IntPtr handle, int timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_event(IntPtr handle, byte[] output, int capacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_send_audio(IntPtr handle, byte[] opus, int length, uint timestamp);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_call_receive_audio(IntPtr handle, byte[] output, int capacity,
                                                           out uint timestamp, out uint sequence);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_call_destroy(IntPtr handle);

        /// <summary>Состояние из lovit_call_poll.</summary>
        public const int Searching = 0;
        public const int Handshake = 1;
        public const int Flowing = 2;
        public const int Broken = -1;

        private IntPtr _handle;
        private readonly byte[] _text = new byte[512];

        public string Ufrag { get; private set; }
        public string Password { get; private set; }
        public string Fingerprint { get; private set; }
        public uint Ssrc { get; private set; }

        private CallCore(IntPtr handle)
        {
            _handle = handle;

            int length = lovit_call_local(handle, _text, _text.Length);
            string[] parts = Encoding.UTF8.GetString(_text, 0, Math.Max(0, length)).Split(' ');

            Ufrag = parts[0];
            Password = parts[1];
            Fingerprint = parts[2];

            uint ssrc;
            uint.TryParse(parts[3], out ssrc);
            Ssrc = ssrc;
        }

        /// <summary>null — ядро не завелось (нет сокета или OpenSSL не смог).</summary>
        public static CallCore Create()
        {
            IntPtr handle = lovit_call_create();
            return handle == IntPtr.Zero ? null : new CallCore(handle);
        }

        public void AddHost(string ip)
        {
            lovit_call_add_host(_handle, Ascii(ip));
        }

        public void AddStun(string host, int port)
        {
            lovit_call_add_stun(_handle, Ascii(host), port);
        }

        public void AddTurn(string host, int port, string user, string password)
        {
            lovit_call_add_turn(_handle, Ascii(host), port, Ascii(user), Ascii(password));
        }

        public void SetRemote(string ufrag, string password, string fingerprint, int opusPt)
        {
            lovit_call_set_remote(_handle, Ascii(ufrag), Ascii(password), Ascii(fingerprint), opusPt);
        }

        /// <summary>
        /// Роли. Отвечая: ICE подчиняется, DTLS — клиент (так по умолчанию).
        /// Звоня сами: ICE ведёт, а DTLS — как скажет ответ собеседника.
        /// </summary>
        public void SetRoles(bool controlling, bool dtlsClient)
        {
            lovit_call_set_roles(_handle, controlling ? 1 : 0, dtlsClient ? 1 : 0);
        }

        public void AddRemote(string ip, int port, uint priority)
        {
            lovit_call_add_remote(_handle, Ascii(ip), port, priority);
        }

        public int Poll(int timeoutMs)
        {
            return lovit_call_poll(_handle, timeoutMs);
        }

        /// <summary>Очередное событие или null.</summary>
        public string NextEvent()
        {
            int length = lovit_call_event(_handle, _text, _text.Length);
            return length > 0 ? Encoding.UTF8.GetString(_text, 0, Math.Min(length, _text.Length)) : null;
        }

        public bool SendAudio(byte[] opus, int length, uint timestamp)
        {
            return lovit_call_send_audio(_handle, opus, length, timestamp) == 0;
        }

        /// <summary>Принятый кадр Opus в buffer; длина, 0 — пусто.</summary>
        public int ReceiveAudio(byte[] buffer, out uint timestamp, out uint sequence)
        {
            return lovit_call_receive_audio(_handle, buffer, buffer.Length, out timestamp, out sequence);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            lovit_call_destroy(_handle);
            _handle = IntPtr.Zero;
        }

        /// <summary>Строка для C: ASCII с нулём в конце.</summary>
        private static byte[] Ascii(string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes((text ?? "") + "\0");
            return raw;
        }
    }
}
