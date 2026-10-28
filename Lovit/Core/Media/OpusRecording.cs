using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Голосовое из обычных отсчётов в Opus внутри Ogg.
    ///
    /// Обратный ход к <see cref="OpusAudio"/>: там присланное разворачивалось
    /// в понятный телефону WAV, здесь снятое микрофоном сворачивается в то,
    /// что примет сервер. Другого пути нет — своего Opus у Windows Phone нет
    /// ни на раскодирование, ни на кодирование, а голосовые MAX принимает
    /// только в нём.
    ///
    /// Кодирование идёт по ходу записи, кадр за кадром, а не в конце: держать
    /// в памяти пять минут несжатого звука — это тридцать мегабайт, тогда как
    /// готовых пакетов набирается меньше мегабайта.
    /// </summary>
    public sealed class OpusRecorder : IDisposable
    {
        private const string Library = "LovitOpus.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_opus_encoder_create(int sampleRate, int channels,
                                                               int bitrate);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_opus_encode(IntPtr handle, short[] pcm, int samples,
                                                    byte[] packet, int maxLength);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_opus_encoder_destroy(IntPtr handle);

        /// <summary>
        /// Частота, с которой работает поток. Ровно та же, что у настоящего
        /// клиента: сорок восемь килогерц и один канал.
        /// </summary>
        public const int Rate = 48000;

        /// <summary>Двадцать миллисекунд при этой частоте — обычный кадр Opus.</summary>
        private const int FrameSamples = 960;

        /// <summary>
        /// Сколько отсчётов кодек проглатывает на разгон. Число не наше: его
        /// пишет в заголовок настоящий клиент, и заголовок должен совпадать.
        /// </summary>
        private const int PreSkip = 312;

        private const int Bitrate = 24000;

        /// <summary>Больше этого один кадр не занимает никогда.</summary>
        private const int MostPacket = 4000;

        private IntPtr _handle;

        private readonly List<byte[]> _packets = new List<byte[]>();
        private readonly byte[] _packet = new byte[MostPacket];

        /// <summary>Недобранный до целого кадра остаток предыдущей порции.</summary>
        private readonly short[] _tail = new short[FrameSamples];
        private int _tailLength;

        private long _samples;

        /// <summary>Сколько отсчётов уже закодировано — по ним считается длительность.</summary>
        public long Samples { get { return _samples; } }

        public int Packets { get { return _packets.Count; } }

        public OpusRecorder()
        {
            _handle = lovit_opus_encoder_create(Rate, 1, Bitrate);

            if (_handle == IntPtr.Zero)
            {
                Log.Write("chat", "голосовое: кодер не завёлся");
            }
        }

        public bool Ready { get { return _handle != IntPtr.Zero; } }

        /// <summary>
        /// Скормить очередную порцию отсчётов. Всё, что не сложилось в целый
        /// кадр, дожидается следующей порции.
        /// </summary>
        public void Feed(short[] pcm, int length)
        {
            if (_handle == IntPtr.Zero || pcm == null || length <= 0) return;

            int at = 0;

            while (at < length)
            {
                int need = FrameSamples - _tailLength;
                int take = Math.Min(need, length - at);

                Array.Copy(pcm, at, _tail, _tailLength, take);

                _tailLength += take;
                at += take;

                if (_tailLength < FrameSamples) break;

                Encode(_tail, FrameSamples);
                _tailLength = 0;
            }
        }

        private void Encode(short[] frame, int samples)
        {
            int written;

            try
            {
                written = lovit_opus_encode(_handle, frame, samples, _packet, MostPacket);
            }
            catch (Exception error)
            {
                Log.Write("chat", "голосовое: кадр не закодировался — " + error.Message);
                return;
            }

            if (written <= 0) return;

            var made = new byte[written];
            Buffer.BlockCopy(_packet, 0, made, 0, written);

            _packets.Add(made);
            _samples += samples;
        }

        /// <summary>
        /// Закончить запись и собрать готовый файл. Недобранный остаток
        /// дописывается тишиной: обрубать кадр посередине Opus не умеет.
        /// </summary>
        public byte[] Finish()
        {
            if (_handle == IntPtr.Zero) return null;

            if (_tailLength > 0)
            {
                for (int at = _tailLength; at < FrameSamples; at++) _tail[at] = 0;

                Encode(_tail, FrameSamples);
                _tailLength = 0;
            }

            if (_packets.Count == 0) return null;

            return Ogg.Build(_packets, _samples, PreSkip, Rate);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;

            lovit_opus_encoder_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Контейнер Ogg: страницы, разметка пакетов по ним и контрольная сумма.
    ///
    /// Устроен он просто, и своей библиотеки под него не нужно — как и при
    /// разборе присланного. Порт OggPageWriter и сборки заголовков из Komet:
    /// заголовки должны совпасть с тем, что шлёт настоящий клиент, иначе CDN
    /// оставляет вложение необработанным.
    /// </summary>
    internal static class Ogg
    {
        /// <summary>Столько кусков помещается в одну страницу, и не больше.</summary>
        private const int MostSegments = 255;

        private const int HeaderSize = 27;

        /// <summary>Чей это поток. Число произвольное, важна лишь его одинаковость.</summary>
        private const int Serial = 0x4C6F7669;

        private const string Vendor = "libopus unknown";

        public static byte[] Build(IList<byte[]> packets, long samples, int preSkip, int rate)
        {
            var target = new MemoryStream();
            int sequence = 0;

            // Первые две страницы служебные: чем это записано и чем играть.
            // Каждая обязана лежать в своей странице — так велит формат.
            Write(target, 0x02, 0, sequence++, new List<byte[]> { Head(preSkip, rate) });
            Write(target, 0x00, 0, sequence++, new List<byte[]> { Tags() });

            var page = new List<byte[]>();
            int segments = 0;
            long counted = 0;

            for (int at = 0; at < packets.Count; at++)
            {
                byte[] packet = packets[at];
                int need = packet.Length / 255 + 1;

                if (page.Count > 0 && segments + need > MostSegments)
                {
                    Write(target, 0x00, counted + preSkip, sequence++, page);

                    page = new List<byte[]>();
                    segments = 0;
                }

                page.Add(packet);
                segments += need;
                counted += 960;
            }

            // Последняя страница помечается концом потока, и время в ней —
            // настоящая длина записи, а не сумма кадров: хвост дописан
            // тишиной, и играть её незачем.
            Write(target, 0x04, samples + preSkip, sequence++, page);

            return target.ToArray();
        }

        private static void Write(Stream target, int kind, long granule, int sequence,
                                  IList<byte[]> packets)
        {
            int segments = 0;
            int body = 0;

            for (int at = 0; at < packets.Count; at++)
            {
                segments += packets[at].Length / 255 + 1;
                body += packets[at].Length;
            }

            var page = new byte[HeaderSize + segments + body];

            page[0] = (byte)'O';
            page[1] = (byte)'g';
            page[2] = (byte)'g';
            page[3] = (byte)'S';
            page[4] = 0;
            page[5] = (byte)kind;

            Little(page, 6, (ulong)granule, 8);
            Little(page, 14, (uint)Serial, 4);
            Little(page, 18, (uint)sequence, 4);
            Little(page, 22, 0, 4);

            page[26] = (byte)segments;

            int table = HeaderSize;

            for (int at = 0; at < packets.Count; at++)
            {
                int left = packets[at].Length;

                while (left >= 255)
                {
                    page[table++] = 255;
                    left -= 255;
                }

                page[table++] = (byte)left;
            }

            int cursor = table;

            for (int at = 0; at < packets.Count; at++)
            {
                Buffer.BlockCopy(packets[at], 0, page, cursor, packets[at].Length);
                cursor += packets[at].Length;
            }

            Little(page, 22, Crc32(page, page.Length), 4);

            target.Write(page, 0, page.Length);
        }

        /// <summary>Чем записано: частота, каналы и разгон кодека.</summary>
        private static byte[] Head(int preSkip, int rate)
        {
            var head = new byte[19];

            Ascii(head, 0, "OpusHead");

            head[8] = 1;   // версия
            head[9] = 1;   // каналов

            Little(head, 10, (uint)preSkip, 2);
            Little(head, 12, (uint)rate, 4);
            Little(head, 16, 0, 2);   // поправка громкости

            head[18] = 0;  // раскладка каналов — простейшая

            return head;
        }

        /// <summary>Подписи. Своих нет, но и пустыми страницу пропустить нельзя.</summary>
        private static byte[] Tags()
        {
            var made = new byte[8 + 4 + Vendor.Length + 4];

            Ascii(made, 0, "OpusTags");
            Little(made, 8, (uint)Vendor.Length, 4);
            Ascii(made, 12, Vendor);
            Little(made, 12 + Vendor.Length, 0, 4);

            return made;
        }

        private static void Ascii(byte[] target, int at, string text)
        {
            for (int i = 0; i < text.Length; i++) target[at + i] = (byte)text[i];
        }

        private static void Little(byte[] target, int at, ulong value, int bytes)
        {
            for (int i = 0; i < bytes; i++) target[at + i] = (byte)((value >> (i * 8)) & 0xFF);
        }

        private static uint[] _table;

        /// <summary>
        /// Контрольная сумма Ogg. Тот же многочлен, что у обычной CRC-32, но
        /// без обращения битов и без начального заполнения — своя, и обычной
        /// её не заменить.
        /// </summary>
        private static uint Crc32(byte[] data, int length)
        {
            if (_table == null)
            {
                var made = new uint[256];

                for (uint at = 0; at < 256; at++)
                {
                    uint value = at << 24;

                    for (int bit = 0; bit < 8; bit++)
                    {
                        value = (value & 0x80000000) != 0
                            ? (value << 1) ^ 0x04C11DB7
                            : value << 1;
                    }

                    made[at] = value;
                }

                _table = made;
            }

            uint crc = 0;

            for (int at = 0; at < length; at++)
            {
                crc = (crc << 8) ^ _table[((crc >> 24) ^ data[at]) & 0xFF];
            }

            return crc;
        }
    }
}
