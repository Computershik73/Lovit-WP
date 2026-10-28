using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Голосовые из Opus в обычный звук.
    ///
    /// MAX шлёт голосовые в Opus внутри контейнера Ogg, а этого кодека в
    /// Windows Phone 8.1 нет вовсе: система отказывает с «формат не
    /// поддерживается», и обойти это её же средствами нельзя. Поэтому звук
    /// раскодируется своей библиотекой (libopus, собран рядом с OpenSSL и
    /// libwebp) и заворачивается в WAV — простейший вид, который система
    /// играет всегда.
    ///
    /// Контейнер разбирается здесь, а не в нативной части: Ogg устроен
    /// просто, и тащить ради него ещё одну библиотеку незачем.
    /// </summary>
    public static class OpusAudio
    {
        private const string Library = "LovitOpus.dll";

        /// <summary>
        /// Частота на выходе. Голос шире двенадцати килогерц не бывает, а
        /// вдвое меньшая частота — это вдвое меньший файл на диске: пятиминутное
        /// сообщение занимает около десяти мегабайт вместо тридцати.
        /// </summary>
        private const int Rate = 24000;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_opus_create(int sampleRate, int channels);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_opus_decode(IntPtr handle, byte[] packet, int length,
                                                    short[] pcm, int maxSamples);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_opus_destroy(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_opus_max_frame();

        /// <summary>
        /// Раскодировать и положить рядом готовый WAV. Второй раз ничего не
        /// считается: файл остаётся на диске.
        /// </summary>
        public static async Task<StorageFile> ConvertAsync(StorageFile ogg)
        {
            if (ogg == null) return null;

            string name = ogg.Name;
            int dot = name.LastIndexOf('.');
            string wavName = (dot > 0 ? name.Substring(0, dot) : name) + ".wav";

            try
            {
                // Родительскую папку у файла не спросить — берём ту же, куда
                // складывается всё скачанное.
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("media", CreationCollisionOption.OpenIfExists);

                // Уже раскодировано — берём готовое.
                try
                {
                    var ready = await folder.GetFileAsync(wavName) as StorageFile;
                    if (ready != null)
                    {
                        Windows.Storage.FileProperties.BasicProperties about =
                            await ready.GetBasicPropertiesAsync();

                        if (about.Size > 0) return ready;
                    }
                }
                catch (Exception) { }

                byte[] source = (await FileIO.ReadBufferAsync(ogg)).ToArray();

                byte[] wav = Decode(source);
                if (wav == null) return null;

                StorageFile made = await folder.CreateFileAsync(
                    wavName, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(made, wav);

                Log.Write("mem", "голосовое раскодировано: " + wav.Length + " байт звука");
                return made;
            }
            catch (Exception error)
            {
                Log.Write("mem", "голосовое не раскодировать: " + error.Message);
                return null;
            }
        }

        /// <summary>Ogg с Opus внутри — в WAV. Возвращает null, если не вышло.</summary>
        private static byte[] Decode(byte[] ogg)
        {
            List<byte[]> packets = ReadPackets(ogg);
            if (packets == null || packets.Count < 2) return null;

            int channels;
            int preSkip;

            if (!ReadHead(packets[0], out channels, out preSkip)) return null;

            IntPtr decoder = lovit_opus_create(Rate, channels);
            if (decoder == IntPtr.Zero)
            {
                Log.Write("mem", "декодер Opus не завёлся");
                return null;
            }

            try
            {
                int frame = lovit_opus_max_frame();
                var pcm = new short[frame * channels];

                var sound = new List<byte>(ogg.Length * 12);

                // Первые отсчёты — разгон кодировщика, их выбрасывают.
                // Счёт в заголовке идёт по 48 кГц, у нас частота своя.
                int skip = preSkip * Rate / 48000;

                // Первые два пакета — заголовок и подписи, звука в них нет.
                for (int i = 2; i < packets.Count; i++)
                {
                    int got = lovit_opus_decode(decoder, packets[i], packets[i].Length, pcm, frame);
                    if (got <= 0) continue;

                    for (int s = 0; s < got; s++)
                    {
                        if (skip > 0) { skip--; continue; }

                        for (int c = 0; c < channels; c++)
                        {
                            short value = pcm[s * channels + c];

                            sound.Add((byte)(value & 0xFF));
                            sound.Add((byte)((value >> 8) & 0xFF));
                        }
                    }
                }

                if (sound.Count == 0) return null;

                return Wrap(sound.ToArray(), channels);
            }
            finally
            {
                lovit_opus_destroy(decoder);
            }
        }

        #region Контейнер Ogg

        /// <summary>
        /// Разбор Ogg на пакеты.
        ///
        /// Страница несёт таблицу отрезков; отрезок в 255 байт означает, что
        /// пакет продолжается следующим, всё прочее его завершает. Пакет
        /// может тянуться и через границу страниц — потому склейка ведётся
        /// поверх всего потока, а не внутри страницы.
        /// </summary>
        private static List<byte[]> ReadPackets(byte[] data)
        {
            var packets = new List<byte[]>();
            var current = new List<byte>();

            int at = 0;

            while (at + 27 <= data.Length)
            {
                if (data[at] != 'O' || data[at + 1] != 'g'
                    || data[at + 2] != 'g' || data[at + 3] != 'S')
                {
                    return packets.Count > 0 ? packets : null;
                }

                int segments = data[at + 26];
                int table = at + 27;
                if (table + segments > data.Length) break;

                int body = table + segments;

                foreach (int length in Lengths(data, table, segments))
                {
                    if (body + length > data.Length) return packets;

                    for (int i = 0; i < length; i++) current.Add(data[body + i]);
                    body += length;

                    // Отрезок короче 255 закрывает пакет.
                    if (length != 255)
                    {
                        packets.Add(current.ToArray());
                        current.Clear();
                    }
                }

                at = body;
            }

            return packets;
        }

        private static IEnumerable<int> Lengths(byte[] data, int table, int segments)
        {
            for (int i = 0; i < segments; i++) yield return data[table + i];
        }

        /// <summary>
        /// Первый пакет — OpusHead: сколько каналов и сколько отсчётов в
        /// начале приходится на разгон.
        /// </summary>
        private static bool ReadHead(byte[] packet, out int channels, out int preSkip)
        {
            channels = 1;
            preSkip = 0;

            if (packet == null || packet.Length < 19) return false;

            string magic = "OpusHead";
            for (int i = 0; i < magic.Length; i++)
            {
                if (packet[i] != (byte)magic[i]) return false;
            }

            channels = packet[9];
            if (channels < 1 || channels > 2) return false;

            preSkip = packet[10] | (packet[11] << 8);
            return true;
        }

        #endregion

        /// <summary>
        /// Заголовок WAV поверх готовых отсчётов. Вид простейший — целые
        /// шестнадцать разрядов без сжатия: его система играет всегда.
        /// </summary>
        private static byte[] Wrap(byte[] sound, int channels)
        {
            int byteRate = Rate * channels * 2;

            var wav = new byte[44 + sound.Length];

            Ascii(wav, 0, "RIFF");
            Number(wav, 4, 36 + sound.Length, 4);
            Ascii(wav, 8, "WAVE");

            Ascii(wav, 12, "fmt ");
            Number(wav, 16, 16, 4);           // длина этого куска
            Number(wav, 20, 1, 2);            // без сжатия
            Number(wav, 22, channels, 2);
            Number(wav, 24, Rate, 4);
            Number(wav, 28, byteRate, 4);
            Number(wav, 32, channels * 2, 2); // байт на кадр
            Number(wav, 34, 16, 2);           // разрядов на отсчёт

            Ascii(wav, 36, "data");
            Number(wav, 40, sound.Length, 4);

            Buffer.BlockCopy(sound, 0, wav, 44, sound.Length);
            return wav;
        }

        private static void Ascii(byte[] target, int at, string text)
        {
            for (int i = 0; i < text.Length; i++) target[at + i] = (byte)text[i];
        }

        private static void Number(byte[] target, int at, int value, int size)
        {
            for (int i = 0; i < size; i++) target[at + i] = (byte)((value >> (i * 8)) & 0xFF);
        }
    }
}
