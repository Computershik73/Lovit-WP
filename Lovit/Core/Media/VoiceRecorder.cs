using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Lovit.Core.Media
{
    /// <summary>Что получилось записать.</summary>
    public class VoiceTake
    {
        /// <summary>Готовый Ogg с Opus — то, что уходит на сервер.</summary>
        public byte[] Data { get; set; }

        public int DurationMs { get; set; }

        /// <summary>График громкости, восемьдесят столбиков.</summary>
        public byte[] Wave { get; set; }

        public string Error { get; set; }

        public bool Ok
        {
            get { return Data != null && Data.Length > 0 && string.IsNullOrEmpty(Error); }
        }
    }

    /// <summary>
    /// Запись голосового.
    ///
    /// Микрофон снимается обычным WAV в память, а не в файл: WAV — это
    /// заголовок и подряд идущие отсчёты, и по мере их появления мы тут же
    /// кодируем их в Opus. Иначе пришлось бы держать в памяти всю запись
    /// несжатой — тридцать мегабайт на пять минут.
    ///
    /// Из тех же отсчётов считается громкость: и та, что видна во время
    /// записи, и та, что уезжает графиком вместе с сообщением.
    /// </summary>
    public sealed class VoiceRecorder : IDisposable
    {
        /// <summary>Дольше этого не пишем — голосовое, а не подкаст.</summary>
        public const int MostMs = 5 * 60 * 1000;

        /// <summary>Короче этого не отправляем: это случайное касание.</summary>
        public const int LeastMs = 700;

        /// <summary>Столбиков в графике — столько же шлёт настоящий клиент.</summary>
        private const int WaveBars = 80;

        /// <summary>Наибольшая высота столбика в том же графике.</summary>
        private const int WaveTop = 120;

        private MediaCapture _capture;
        private InMemoryRandomAccessStream _stream;
        private OpusRecorder _encoder;

        private ulong _read;
        private bool _pumping;
        private bool _stopping;

        private DateTime _startedAt;

        /// <summary>Разбор заголовка WAV: пока он не дочитан, отсчётов ещё нет.</summary>
        private byte[] _head = new byte[0];
        private bool _headDone;
        private int _rate = OpusRecorder.Rate;
        private int _channels = 1;

        /// <summary>Половинка отсчёта, разрезанного границей порции.</summary>
        private byte _halfByte;
        private bool _halfHeld;

        private readonly Resampler _resampler = new Resampler();
        private readonly List<double> _levels = new List<double>();

        public bool Recording { get; private set; }

        /// <summary>Громкость сейчас, от нуля до единицы.</summary>
        public double Level { get; private set; }

        /// <summary>Все замеры громкости — из них строится итоговый график.</summary>
        public IList<double> Levels { get { return _levels; } }

        public TimeSpan Elapsed
        {
            get
            {
                return Recording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;
            }
        }

        /// <summary>Почему не началось или не получилось.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Начать запись. Ложь — значит не вышло, и причина лежит в
        /// <see cref="LastError"/>: чаще всего это запрет на микрофон.
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (Recording) return true;

            LastError = null;

            try
            {
                _encoder = new OpusRecorder();

                if (!_encoder.Ready)
                {
                    LastError = "Не удалось подготовить кодировщик голоса.";
                    return false;
                }

                _capture = new MediaCapture();

                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

                try
                {
                    await _capture.InitializeAsync(settings);
                }
                catch (Exception error)
                {
                    LastError = Explain(error);

                    Log.Write("chat", "голосовое: микрофон не открылся, 0x"
                                      + ((uint)error.HResult).ToString("X8")
                                      + " — " + error.Message);

                    Release();
                    return false;
                }

                // Сначала просим ровно то, что нужно кодеку. Если телефон
                // такой записи не умеет — пробуем ещё раз, ничего не
                // навязывая: что вышло на самом деле, всё равно читается
                // потом из заголовка записи, и под него всё пересчитывается.
                if (!await TryStartAsync(true) && !await TryStartAsync(false))
                {
                    LastError = "Телефон не даёт записывать звук в нужном виде.";

                    Release();
                    return false;
                }

                _startedAt = DateTime.UtcNow;
                Recording = true;

                Log.Write("chat", "голосовое: запись пошла");
                return true;
            }
            catch (Exception error)
            {
                LastError = Explain(error);
                Log.Write("chat", "голосовое: запись не началась — " + error.Message);

                Release();
                return false;
            }
        }

        private async Task<bool> TryStartAsync(bool ours)
        {
            try
            {
                MediaEncodingProfile profile =
                    MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);

                if (ours)
                {
                    profile.Audio = AudioEncodingProperties.CreatePcm(
                        (uint)OpusRecorder.Rate, 1, 16);
                }

                if (_stream != null) { _stream.Dispose(); _stream = null; }
                _stream = new InMemoryRandomAccessStream();

                await _capture.StartRecordToStreamAsync(profile, _stream);
                return true;
            }
            catch (Exception error)
            {
                Log.Write("chat", "голосовое: запись " + (ours ? "по-своему" : "как выйдет")
                                  + " не пошла, 0x" + ((uint)error.HResult).ToString("X8")
                                  + " — " + error.Message);
                return false;
            }
        }

        /// <summary>
        /// Забрать накопившееся. Зовётся из отсчёта времени страницы: свой
        /// таймер здесь завёл бы второй, а нужен один.
        /// </summary>
        public async Task PumpAsync()
        {
            if (!Recording || _pumping || _stream == null) return;

            _pumping = true;

            try
            {
                ulong size = _stream.Size;
                if (size <= _read) return;

                uint want = (uint)Math.Min(size - _read, 512UL * 1024);

                using (IInputStream input = _stream.GetInputStreamAt(_read))
                using (var reader = new DataReader(input))
                {
                    await reader.LoadAsync(want);

                    uint got = reader.UnconsumedBufferLength;
                    if (got == 0) return;

                    var chunk = new byte[got];
                    reader.ReadBytes(chunk);

                    _read += got;

                    Consume(chunk);
                }
            }
            catch (Exception error)
            {
                Log.Write("chat", "голосовое: чтение записи сорвалось — " + error.Message);
            }
            finally
            {
                _pumping = false;
            }
        }

        /// <summary>
        /// Разобрать очередной кусок записи: снять заголовок, свести к одному
        /// каналу, привести к нужной частоте и отдать кодировщику.
        /// </summary>
        private void Consume(byte[] chunk)
        {
            int at = 0;

            if (!_headDone)
            {
                // Заголовок приходит по частям, как и всё остальное:
                // накапливаем, пока в нём не найдётся начало отсчётов.
                var grown = new byte[_head.Length + chunk.Length];

                System.Buffer.BlockCopy(_head, 0, grown, 0, _head.Length);
                System.Buffer.BlockCopy(chunk, 0, grown, _head.Length, chunk.Length);

                _head = grown;

                int body = FindBody(_head);
                if (body < 0) return;

                _headDone = true;

                // Отметка нужна не для красоты: если запись пойдёт, а этой
                // строки в журнале не будет, значит телефон отдаёт снятое
                // только в конце — и живого графика громкости не выйдет.
                Log.Write("chat", "голосовое: пишем " + _rate + " Гц, каналов " + _channels);

                chunk = _head;
                at = body;

                _head = new byte[0];
            }

            int available = chunk.Length - at;
            if (available <= 0) return;

            // Отсчёт занимает два байта, и граница порции может пройти по
            // его середине — тогда первая половина ждёт следующей порции.
            var pcm = new short[(available + 1) / 2];
            int count = 0;

            if (_halfHeld)
            {
                pcm[count++] = (short)(_halfByte | (chunk[at] << 8));
                at++;
                _halfHeld = false;
            }

            while (at + 1 < chunk.Length)
            {
                pcm[count++] = (short)(chunk[at] | (chunk[at + 1] << 8));
                at += 2;
            }

            if (at < chunk.Length)
            {
                _halfByte = chunk[at];
                _halfHeld = true;
            }

            if (count == 0) return;

            if (_channels > 1) count = ToMono(pcm, count, _channels);

            int written;
            short[] ready = _resampler.To48k(pcm, count, _rate, out written);

            Measure(ready, written);

            _encoder.Feed(ready, written);
        }

        /// <summary>Свести каналы в один — прямо на месте, без второго массива.</summary>
        private static int ToMono(short[] pcm, int count, int channels)
        {
            int frames = count / channels;

            for (int at = 0; at < frames; at++)
            {
                int sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += pcm[at * channels + channel];
                }

                pcm[at] = (short)(sum / channels);
            }

            return frames;
        }

        /// <summary>Громкость порции — по ней рисуется и живой график, и итоговый.</summary>
        private void Measure(short[] pcm, int count)
        {
            if (count <= 0) return;

            double sum = 0;

            for (int at = 0; at < count; at++)
            {
                double value = pcm[at] / 32768.0;
                sum += value * value;
            }

            double loud = Math.Sqrt(sum / count);

            // Ухо слышит громкость не так, как её считает арифметика: без
            // этого обычная речь дала бы почти незаметные столбики, а разница
            // между шёпотом и криком терялась бы вовсе.
            double shown = Math.Sqrt(loud) * 1.6;
            if (shown > 1) shown = 1;

            Level = shown;
            _levels.Add(shown);
        }

        /// <summary>
        /// Где в заголовке кончается описание и начинаются отсчёты. Заодно
        /// оттуда же берутся частота и число каналов: попросить можно что
        /// угодно, а телефон запишет как умеет.
        /// </summary>
        private int FindBody(byte[] data)
        {
            if (data.Length < 12) return -1;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return -1;

            int at = 12;

            while (at + 8 <= data.Length)
            {
                string name = "" + (char)data[at] + (char)data[at + 1]
                                 + (char)data[at + 2] + (char)data[at + 3];

                long size = (uint)(data[at + 4] | (data[at + 5] << 8)
                                   | (data[at + 6] << 16) | (data[at + 7] << 24));

                if (name == "data") return at + 8;

                if (name == "fmt " && at + 16 <= data.Length)
                {
                    _channels = data[at + 10] | (data[at + 11] << 8);

                    _rate = data[at + 12] | (data[at + 13] << 8)
                            | (data[at + 14] << 16) | (data[at + 15] << 24);

                    if (_channels < 1) _channels = 1;
                    if (_rate < 8000) _rate = OpusRecorder.Rate;
                }

                // Куски выравниваются по чётному числу байтов.
                at += 8 + (int)size + (int)(size & 1);
            }

            return -1;
        }

        /// <summary>Остановить и собрать готовое голосовое.</summary>
        public async Task<VoiceTake> StopAsync()
        {
            if (!Recording) return new VoiceTake { Error = "Запись не шла." };

            _stopping = true;

            int elapsed = (int)(DateTime.UtcNow - _startedAt).TotalMilliseconds;

            try
            {
                await _capture.StopRecordAsync();
            }
            catch (Exception error)
            {
                Log.Write("chat", "голосовое: остановка сорвалась — " + error.Message);
            }

            Recording = false;

            // Хвост записи доходит до потока уже после остановки — забираем
            // его, иначе в сообщении не хватит последних долей секунды.
            await PumpAsync();

            var take = new VoiceTake { DurationMs = elapsed };

            try
            {
                take.Data = _encoder.Finish();
                take.Wave = BuildWave();

                if (take.Data == null || take.Data.Length == 0)
                {
                    take.Error = "Записать не получилось: звука не поступило.";
                }
            }
            catch (Exception error)
            {
                take.Error = "Не удалось собрать голосовое.";
                Log.Write("chat", "голосовое: сборка сорвалась — " + error.Message);
            }

            Log.Write("chat", "голосовое: " + elapsed + " мс, "
                              + (take.Data == null ? 0 : take.Data.Length) + " байт");

            Release();
            _stopping = false;

            return take;
        }

        /// <summary>Бросить запись и ничего не отправлять.</summary>
        public async Task CancelAsync()
        {
            if (!Recording) { Release(); return; }

            Recording = false;

            try
            {
                await _capture.StopRecordAsync();
            }
            catch (Exception) { }

            Log.Write("chat", "голосовое: запись отменена");
            Release();
        }

        /// <summary>
        /// Собрать график: сколько бы замеров ни набралось, столбиков всегда
        /// восемьдесят. В каждом — самый громкий замер своего отрезка, а не
        /// средний: усреднение съедает рисунок речи.
        /// </summary>
        private byte[] BuildWave()
        {
            var wave = new byte[WaveBars];
            if (_levels.Count == 0) return wave;

            for (int bar = 0; bar < WaveBars; bar++)
            {
                int from = _levels.Count * bar / WaveBars;
                int to = _levels.Count * (bar + 1) / WaveBars;
                if (to <= from) to = from + 1;

                double peak = 0;

                for (int at = from; at < to && at < _levels.Count; at++)
                {
                    if (_levels[at] > peak) peak = _levels[at];
                }

                int height = (int)Math.Round(peak * WaveTop);

                if (height < 0) height = 0;
                if (height > WaveTop) height = WaveTop;

                wave[bar] = (byte)height;
            }

            return wave;
        }

        private static string Explain(Exception error)
        {
            // Отказ в доступе к микрофону выглядит как обычная ошибка COM, и
            // человеку она ничего не говорит.
            uint code = (uint)error.HResult;

            if (code == 0x80070005 || code == 0xC00DABE0)
            {
                return "Нет доступа к микрофону. Разрешите его в настройках телефона.";
            }

            return "Не удалось включить микрофон.";
        }

        private void Release()
        {
            if (_encoder != null) { _encoder.Dispose(); _encoder = null; }

            if (_capture != null)
            {
                try { _capture.Dispose(); } catch (Exception) { }
                _capture = null;
            }

            if (_stream != null)
            {
                try { _stream.Dispose(); } catch (Exception) { }
                _stream = null;
            }

            _read = 0;
            _head = new byte[0];
            _headDone = false;
            _halfHeld = false;
            Level = 0;
        }

        public void Dispose()
        {
            if (Recording && !_stopping)
            {
                var ignored = CancelAsync();
                return;
            }

            Release();
        }

        /// <summary>
        /// Приведение к сорока восьми килогерцам. Кодек принимает лишь
        /// несколько частот, а телефон записывает своей — и совпадают они не
        /// всегда.
        ///
        /// Считаем по прямой между соседними отсчётами. Для голоса этого
        /// довольно: слышимой разницы с честным пересчётом нет, а стоит он
        /// вдесятеро меньше.
        /// </summary>
        internal sealed class Resampler
        {
            private double _at;
            private short _last;
            private bool _started;

            public short[] To48k(short[] pcm, int count, int rate, out int written)
            {
                if (count <= 0) { written = 0; return pcm; }

                if (rate == OpusRecorder.Rate)
                {
                    written = count;
                    return pcm;
                }

                double step = (double)rate / OpusRecorder.Rate;

                var made = new short[(int)(count / step) + 2];
                int made_at = 0;

                double position = _started ? _at : 0;

                while (position < count - 1 && made_at < made.Length)
                {
                    int whole = (int)Math.Floor(position);
                    double part = position - whole;

                    short before = whole < 0 ? _last : pcm[whole];
                    short after = pcm[whole + 1];

                    made[made_at++] = (short)(before + (after - before) * part);

                    position += step;
                }

                _at = position - count;
                _last = pcm[count - 1];
                _started = true;

                written = made_at;
                return made;
            }
        }
    }
}
