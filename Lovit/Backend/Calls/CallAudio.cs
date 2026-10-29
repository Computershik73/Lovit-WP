using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Media;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Lovit.Backend.Calls
{
    /// <summary>
    /// Микрофон в разговоре: кадры по 20 мс, сразу в Opus.
    ///
    /// Пишет так же, как голосовые (VoiceRecorder): системная запись в WAV в
    /// память, откуда новое вычитывается по таймеру. Отличие — категория
    /// «связь»: телефон включает своё эхоподавление, и собеседник не
    /// слышит себя из нашего динамика.
    /// </summary>
    internal sealed class CallMicrophone : IDisposable
    {
        private const string Library = "LovitOpus.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_opus_encoder_create(int sampleRate, int channels, int bitrate);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_opus_encode(IntPtr handle, short[] pcm, int samples,
                                                    byte[] packet, int maxLength);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_opus_encoder_destroy(IntPtr handle);

        public const int Rate = 48000;
        public const int FrameSamples = 960;
        private const int Bitrate = 32000;

        private MediaCapture _capture;
        private InMemoryRandomAccessStream _stream;
        private IntPtr _encoder;
        /// <summary>
        /// Таймер чтения — на пуле потоков, а не на потоке окна: тот бывает
        /// занят разметкой, и тогда кадры уходили пачкой после паузы —
        /// собеседник слышал «подвисание».
        /// </summary>
        private Windows.System.Threading.ThreadPoolTimer _timer;

        /// <summary>Самая длинная пауза между кадрами с прошлого вопроса, мс.</summary>
        private double _longestGap;
        private DateTime _lastFrameAt;

        public double TakeLongestGap()
        {
            double gap = _longestGap;
            _longestGap = 0;
            return gap;
        }
        private ulong _read;

        private byte[] _head = new byte[0];
        private bool _headDone;
        private int _rate = Rate;
        private int _channels = 1;
        private bool _halfHeld;
        private byte _halfByte;
        private readonly VoiceRecorder.Resampler _resampler = new VoiceRecorder.Resampler();

        private int _frames;
        private readonly short[] _frame = new short[FrameSamples];
        private int _frameLength;
        private readonly byte[] _packet = new byte[1500];

        /// <summary>Готовый кадр Opus — на потоке разметки.</summary>
        public event Action<byte[]> Frame;

        /// <summary>Микрофон выключен — кодируется тишина: звук идёт, но пустой.</summary>
        public bool Muted { get; set; }

        public string Error { get; private set; }

        /// <summary>
        /// Запись не росла столько — значит, система её остановила (так
        /// бывает, когда воспроизведение уходит из категории «связь» в
        /// обычную): перезапускаем.
        /// </summary>
        private static readonly TimeSpan Stalled = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// Запись копится в памяти целиком, около 5,5 МБ в минуту; получасовой
        /// разговор съел бы всё, что телефон отводит приложению. Поэтому
        /// время от времени переезжаем в новый буфер — пауза в долю секунды.
        /// </summary>
        private const ulong MostBuffered = 8UL * 1024 * 1024;

        private MediaEncodingProfile _profile;
        private DateTime _grewAt;
        private bool _restarting;
        private bool _disposed;

        public async Task<bool> StartAsync()
        {
            _encoder = lovit_opus_encoder_create(Rate, 1, Bitrate);
            if (_encoder == IntPtr.Zero)
            {
                Error = "кодер не завёлся";
                return false;
            }

            if (!await OpenAsync()) return false;

            _timer = Windows.System.Threading.ThreadPoolTimer.CreatePeriodicTimer(
                async timer => await PumpAsync(), TimeSpan.FromMilliseconds(10));
            return true;
        }

        /// <summary>Завести захват и начать запись в новый буфер.</summary>
        private async Task<bool> OpenAsync()
        {
            try
            {
                _capture = new MediaCapture();
                _capture.Failed += (sender, argument) =>
                    Log.WriteNow("call", "микрофон: система сообщила о сбое 0x"
                                         + argument.Code.ToString("X8") + " — " + argument.Message);
                _capture.RecordLimitationExceeded += sender =>
                    Log.WriteNow("call", "микрофон: система остановила запись по пределу");

                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Communications,
                    AudioProcessing = AudioProcessing.Default
                };
                await _capture.InitializeAsync(settings);

                _profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                _profile.Audio = AudioEncodingProperties.CreatePcm(Rate, 1, 16);

                if (!await RecordAsync())
                {
                    // Своего вида телефон не дал — берём, какой даёт: что вышло,
                    // всё равно читается из заголовка.
                    _profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                    if (!await RecordAsync()) throw new InvalidOperationException("запись не пошла");
                }
                return true;
            }
            catch (Exception error)
            {
                Error = "микрофон не открылся: 0x" + ((uint)error.HResult).ToString("X8") + " " + error.Message;
                return false;
            }
        }

        /// <summary>Запись в новый буфер — с чистого листа, заголовок WAV заново.</summary>
        private async Task<bool> RecordAsync()
        {
            InMemoryRandomAccessStream old = _stream;
            _stream = new InMemoryRandomAccessStream();
            _read = 0;
            _head = new byte[0];
            _headDone = false;
            _halfHeld = false;
            _grewAt = DateTime.UtcNow;

            if (old != null) old.Dispose();

            try
            {
                await _capture.StartRecordToStreamAsync(_profile, _stream);
                return true;
            }
            catch (Exception error)
            {
                Log.Write("call", "микрофон: запись не пошла — " + error.Message);
                return false;
            }
        }

        /// <summary>
        /// Начать запись заново. Сперва — тем же захватом; не вышло — захват
        /// целиком новый.
        /// </summary>
        private async Task RestartAsync(string why)
        {
            if (_restarting || _disposed) return;
            _restarting = true;

            Log.WriteNow("call", "микрофон: перезапуск — " + why);

            try
            {
                try
                {
                    Task stop = _capture.StopRecordAsync().AsTask();
                    await Task.WhenAny(stop, Task.Delay(1500));
                }
                catch (Exception) { }

                if (_disposed) return;

                if (!await RecordAsync())
                {
                    MediaCapture broken = _capture;
                    _capture = null;
                    try { broken.Dispose(); }
                    catch (Exception) { }

                    if (_disposed) return;
                    if (!await OpenAsync()) Log.WriteNow("call", "микрофон: " + Error);
                }
            }
            finally
            {
                _grewAt = DateTime.UtcNow;
                _inPump = 0;     // зависшее чтение, когда очнётся, увидит новый буфер и уйдёт
                _restarting = false;
            }
        }

        private int _inPump;
        private int _inRestart;

        /// <summary>
        /// Тик таймера — на пуле потоков, и тики могут налезать друг на друга:
        /// входы в чтение и в перезапуск поэтому атомарные.
        /// </summary>
        private async Task PumpAsync()
        {
            if (_restarting || _disposed || _stream == null) return;

            string restart = null;
            if (DateTime.UtcNow - _grewAt > Stalled)
                restart = _inPump != 0 ? "чтение зависло" : "запись перестала расти";
            else if (_read > MostBuffered)
                restart = "свежий буфер";

            if (restart != null)
            {
                if (Interlocked.CompareExchange(ref _inRestart, 1, 0) != 0) return;
                try { await RestartAsync(restart); }
                finally { _inRestart = 0; }
                return;
            }

            if (Interlocked.CompareExchange(ref _inPump, 1, 0) != 0) return;

            try
            {
                InMemoryRandomAccessStream stream = _stream;
                ulong size = stream.Size;
                if (size <= _read) return;

                uint want = (uint)Math.Min(size - _read, 256UL * 1024);
                using (IInputStream input = stream.GetInputStreamAt(_read))
                using (var reader = new DataReader(input))
                {
                    await reader.LoadAsync(want);
                    if (stream != _stream) return;   // пока читали, буфер сменился

                    uint got = reader.UnconsumedBufferLength;
                    if (got == 0) return;

                    var chunk = new byte[got];
                    reader.ReadBytes(chunk);
                    _read += got;
                    _grewAt = DateTime.UtcNow;
                    Consume(chunk);
                }
            }
            catch (Exception error)
            {
                Log.Write("call", "микрофон: чтение сорвалось — " + error.Message);
            }
            finally
            {
                _inPump = 0;
            }
        }

        private void Consume(byte[] chunk)
        {
            int at = 0;

            if (!_headDone)
            {
                var grown = new byte[_head.Length + chunk.Length];
                System.Buffer.BlockCopy(_head, 0, grown, 0, _head.Length);
                System.Buffer.BlockCopy(chunk, 0, grown, _head.Length, chunk.Length);
                _head = grown;

                int body = FindBody(_head);
                if (body < 0) return;

                _headDone = true;
                Log.Write("call", "микрофон: " + _rate + " Гц, каналов " + _channels);
                chunk = _head;
                at = body;
                _head = new byte[0];
            }

            int available = chunk.Length - at;
            if (available <= 0) return;

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

            if (_channels > 1)
            {
                int frames = count / _channels;
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < _channels; c++) sum += pcm[i * _channels + c];
                    pcm[i] = (short)(sum / _channels);
                }
                count = frames;
            }

            int written;
            short[] ready = _resampler.To48k(pcm, count, _rate, out written);

            for (int i = 0; i < written; i++)
            {
                _frame[_frameLength++] = Muted ? (short)0 : ready[i];
                if (_frameLength < FrameSamples) continue;

                _frameLength = 0;
                int length = lovit_opus_encode(_encoder, _frame, FrameSamples, _packet, _packet.Length);
                if (length <= 0) continue;

                var packet = new byte[length];
                System.Buffer.BlockCopy(_packet, 0, packet, 0, length);

                if (_frames++ == 0) Log.WriteNow("call", "микрофон: первый кадр закодирован");

                DateTime now = DateTime.UtcNow;
                if (_frames > 1)
                {
                    double gap = (now - _lastFrameAt).TotalMilliseconds;
                    if (gap > _longestGap) _longestGap = gap;
                }
                _lastFrameAt = now;

                Action<byte[]> handler = Frame;
                if (handler != null) handler(packet);
            }
        }

        private int FindBody(byte[] data)
        {
            if (data.Length < 12) return -1;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return -1;

            int at = 12;
            while (at + 8 <= data.Length)
            {
                string name = "" + (char)data[at] + (char)data[at + 1] + (char)data[at + 2] + (char)data[at + 3];
                long size = (uint)(data[at + 4] | (data[at + 5] << 8) | (data[at + 6] << 16) | (data[at + 7] << 24));

                if (name == "data") return at + 8;
                if (name == "fmt " && at + 16 <= data.Length)
                {
                    _channels = Math.Max(1, data[at + 10] | (data[at + 11] << 8));
                    _rate = data[at + 12] | (data[at + 13] << 8) | (data[at + 14] << 16) | (data[at + 15] << 24);
                    if (_rate < 8000) _rate = Rate;
                }
                at += 8 + (int)size + (int)(size & 1);
            }
            return -1;
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null) _timer.Cancel();
            _timer = null;

            MediaCapture capture = _capture;
            _capture = null;
            if (capture != null)
            {
                var ignored = StopAsync(capture);
            }

            if (_encoder != IntPtr.Zero)
            {
                lovit_opus_encoder_destroy(_encoder);
                _encoder = IntPtr.Zero;
            }
        }

        private async Task StopAsync(MediaCapture capture)
        {
            try { await capture.StopRecordAsync(); }
            catch (Exception) { }
            try { capture.Dispose(); }
            catch (Exception) { }
            if (_stream != null) { _stream.Dispose(); _stream = null; }
        }
    }

    /// <summary>
    /// Буфер против дрожания: пакеты по сети приходят неровно и не по
    /// порядку, а играть их надо ровно. Держим 60 мс запаса; пропавший пакет
    /// Opus достраивает сам (PLC), по тому, что звучало перед ним.
    ///
    /// Пишет поток сети, читает поток звука — отсюда замок.
    /// </summary>
    internal sealed class JitterBuffer : IDisposable
    {
        private const string Library = "LovitOpus.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr lovit_opus_create(int sampleRate, int channels);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int lovit_opus_decode(IntPtr handle, byte[] packet, int length,
                                                    short[] pcm, int maxSamples);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void lovit_opus_destroy(IntPtr handle);

        /// <summary>Сколько кадров копить перед началом.</summary>
        private const int Prefill = 3;
        /// <summary>Больше этого — задержка растёт, лишнее старое выбрасываем.</summary>
        private const int MostQueued = 12;
        /// <summary>Сколько кадров подряд достраивать, прежде чем замолчать и копить заново.</summary>
        private const int MostConcealed = 5;

        private readonly object _lock = new object();
        private readonly SortedDictionary<long, byte[]> _packets = new SortedDictionary<long, byte[]>();
        private readonly short[] _pcm = new short[5760];
        private IntPtr _decoder;

        private bool _sizeLogged;
        private long _expected = -1;
        private long _lastExtended = -1;
        private bool _playing;
        private int _concealed;

        public int Received { get; private set; }
        public int Lost { get; private set; }

        public JitterBuffer()
        {
            _decoder = lovit_opus_create(CallMicrophone.Rate, 1);
        }

        /// <summary>Кадр из сети. seq — 16 бит из RTP: разворачиваем в сквозной номер.</summary>
        public void Add(uint sequence, byte[] payload)
        {
            lock (_lock)
            {
                long extended = Extend(sequence);
                Received++;

                if (_expected >= 0 && extended < _expected) return;   // опоздал — уже сыграли без него
                _packets[extended] = payload;

                while (_packets.Count > MostQueued)
                {
                    long oldest = First();
                    _packets.Remove(oldest);
                    _expected = First();
                }
            }
        }

        /// <summary>Следующие 20 мс звука: из пакета, достроенные или тишина.</summary>
        public short[] Next(out int samples)
        {
            lock (_lock)
            {
                if (!_playing)
                {
                    if (_packets.Count < Prefill || _decoder == IntPtr.Zero)
                    {
                        samples = CallMicrophone.FrameSamples;
                        return Silence();
                    }
                    _playing = true;
                    _expected = First();
                }

                byte[] packet;
                if (_packets.TryGetValue(_expected, out packet))
                {
                    _packets.Remove(_expected);
                    _expected++;
                    _concealed = 0;
                    samples = Decode(packet);
                    return _pcm;
                }

                if (_packets.Count > 0 || _concealed < MostConcealed)
                {
                    // Пакета нет: достраиваем.
                    Lost++;
                    _concealed++;
                    _expected++;
                    if (_packets.Count > 0 && First() < _expected) _expected = First();
                    samples = Decode(null);
                    return _pcm;
                }

                // Совсем пусто — молчим и копим заново.
                _playing = false;
                samples = CallMicrophone.FrameSamples;
                return Silence();
            }
        }

        private int Decode(byte[] packet)
        {
            int got = lovit_opus_decode(_decoder, packet, packet == null ? 0 : packet.Length,
                                        _pcm, _pcm.Length);
            if (got <= 0)
            {
                Array.Clear(_pcm, 0, CallMicrophone.FrameSamples);
                return CallMicrophone.FrameSamples;
            }
            if (packet != null && !_sizeLogged)
            {
                // Длина кадра собеседника: 20 мс — 960 отсчётов, 40 — 1920.
                _sizeLogged = true;
                Log.Write("call", "приём: кадр собеседника " + got + " отсчётов");
            }
            return got;
        }

        private short[] Silence()
        {
            Array.Clear(_pcm, 0, CallMicrophone.FrameSamples);
            return _pcm;
        }

        private long First()
        {
            foreach (long key in _packets.Keys) return key;
            return _expected;
        }

        private long Extend(uint sequence)
        {
            if (_lastExtended < 0)
            {
                _lastExtended = sequence;
                return sequence;
            }

            long cycle = _lastExtended & ~0xFFFFL;
            long candidate = cycle | sequence;
            if (candidate - _lastExtended > 32768) candidate -= 65536;
            else if (_lastExtended - candidate > 32768) candidate += 65536;

            if (candidate > _lastExtended) _lastExtended = candidate;
            return candidate;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_decoder != IntPtr.Zero) lovit_opus_destroy(_decoder);
                _decoder = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Звук собеседника: живой поток в MediaElement через MediaStreamSource.
    ///
    /// Категория «связь»: телефон играет в разговорный динамик, а не в
    /// громкий, и считает это звонком — с эхоподавлением в паре с микрофоном.
    /// </summary>
    internal sealed class CallSpeaker
    {
        private readonly JitterBuffer _jitter;
        private MediaElement _element;
        private MediaStreamSource _source;
        private TimeSpan _clock;

        public CallSpeaker(JitterBuffer jitter)
        {
            _jitter = jitter;
        }

        /// <summary>Играть в громкий динамик, а не в разговорный.</summary>
        public bool Loud { get; set; }

        public void SetLoud(bool loud)
        {
            if (Loud == loud) return;
            Loud = loud;
            if (_element == null) return;

            // Категорию на ходу не сменить — перезапускаем воспроизведение.
            // Буфер звука при этом свой, он не теряется.
            MediaElement element = _element;
            Stop();
            Start(element);
        }

        private bool _wired;

        public void Start(MediaElement element)
        {
            _element = element;
            _clock = TimeSpan.Zero;   // новый источник начинает с нуля

            var descriptor = new AudioStreamDescriptor(
                AudioEncodingProperties.CreatePcm(CallMicrophone.Rate, 1, 16));

            _source = new MediaStreamSource(descriptor)
            {
                CanSeek = false,
                BufferTime = TimeSpan.Zero
            };
            _source.Starting += (sender, argument) =>
                argument.Request.SetActualStartPosition(TimeSpan.Zero);
            _source.SampleRequested += OnSampleRequested;

            if (!_wired)
            {
                _wired = true;
                element.MediaFailed += (sender, argument) =>
                    Log.WriteNow("call", "динамик: MediaElement отказал — " + argument.ErrorMessage);
                element.CurrentStateChanged += (sender, argument) =>
                    Log.Write("call", "динамик: " + element.CurrentState);
            }

            // Категория всегда «связь». Обычный медиазвук система глушит, пока
            // идёт звонок (наш же микрофон — «звонковый»): в 1.1.0.172 громкая
            // связь так и молчала. Громкость — выбором устройства: у «связи»
            // это разговорный динамик, у «мультимедиа» — громкий.
            element.AudioCategory = AudioCategory.Communications;
            element.AudioDeviceType = Loud ? AudioDeviceType.Multimedia : AudioDeviceType.Communications;
            Log.Write("call", "динамик: " + (Loud ? "громкий" : "разговорный"));
            element.RealTimePlayback = true;
            element.SetMediaStreamSource(_source);
            element.Play();
        }

        private bool _firstSample = true;
        private bool _complained;

        /// <summary>
        /// Зовётся на потоке воспроизведения. Исключение отсюда уронило бы
        /// приложение мимо обработчика падений — поэтому всё под защитой, а
        /// вместо сбойного кусочка играет тишина.
        /// </summary>
        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs argument)
        {
            if (_firstSample)
            {
                _firstSample = false;
                Log.WriteNow("call", "динамик: первый кусочек запрошен");
            }

            try
            {
                Supply(argument);
            }
            catch (Exception error)
            {
                if (!_complained)
                {
                    _complained = true;
                    Log.WriteNow("call", "динамик: сбой — " + error.GetType().Name + ": " + error.Message);
                }
            }
        }

        private void Supply(MediaStreamSourceSampleRequestedEventArgs argument)
        {
            int samples;
            short[] pcm = _jitter.Next(out samples);

            var bytes = new byte[samples * 2];
            System.Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

            TimeSpan duration = TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / CallMicrophone.Rate);
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), _clock);
            sample.Duration = duration;
            _clock += duration;

            argument.Request.Sample = sample;
        }

        public void Stop()
        {
            if (_element != null)
            {
                try { _element.Stop(); }
                catch (Exception) { }
                _element.Source = null;
            }
            _element = null;
            _source = null;
        }
    }
}
