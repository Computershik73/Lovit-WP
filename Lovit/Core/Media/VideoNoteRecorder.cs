using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.UI.Xaml.Controls;

namespace Lovit.Core.Media
{
    /// <summary>Что получилось снять.</summary>
    public class NoteTake
    {
        public StorageFile File { get; set; }
        public int DurationMs { get; set; }

        public bool Ok { get { return File != null; } }
    }

    /// <summary>
    /// Запись видео-кружка.
    ///
    /// Кружок — это обычный ролик, который на той стороне показывают в круге,
    /// обрезая по середине кадра. Круг рисует тот, кто смотрит; наш
    /// собственный просмотр устроен так же — что видно в кружке
    /// предпросмотра, то и уедет.
    ///
    /// А вот сама запись вольностей не терпит: её проверяет сервер и о
    /// негодной говорит одно слово без подробностей. Поэтому снимается она
    /// теми же числами, что и у настоящего клиента, — квадратом в 480, по
    /// тридцать кадров, звук одним каналом.
    /// </summary>
    public sealed class VideoNoteRecorder : IDisposable
    {
        /// <summary>Кружок длиннее минуты не бывает — так во всех мессенджерах.</summary>
        public const int MostMs = 60 * 1000;

        private MediaCapture _capture;
        private StorageFile _file;

        /// <summary>
        /// Какой камерой снимать. Выбирается заранее, до включения: снимать
        /// начинают с удержания кнопки, и мгновения на выбор потом уже нет.
        /// </summary>
        public bool Front { get; set; }

        public bool Previewing { get; private set; }
        public bool Recording { get; private set; }

        /// <summary>
        /// Даёт ли камера квадратный кадр той дорожке, что пишется.
        ///
        /// Если нет, Media Foundation вписывает её прямоугольник в квадрат,
        /// дорисовывая чёрные поля по бокам, — и поля эти попадают прямо в
        /// точки записи. Убрать их отсюда нечем: обрезать кадр съёмка на
        /// этой системе не умеет, а неквадратный кружок сервер не берёт.
        /// Остаётся предупредить человека.
        /// </summary>
        public bool Squared { get; private set; }

        /// <summary>
        /// Вписывает ли Media Foundation кадр в квадрат с чёрными полями.
        ///
        /// Обычно нет: где камера квадрата не даёт, ей заказывается квадрат
        /// с вытянутой точкой, и кадр ложится целиком. Но если такой заказ
        /// не пройдёт и запись начнётся попроще, поля появятся — и показ
        /// должен выглядеть так же, иначе он обманет снимающего.
        /// </summary>
        public bool Boxed { get; private set; }

        /// <summary>Чем снимает дорожка записи — как её назвала камера.</summary>
        private uint _shotWidth;
        private uint _shotHeight;

        /// <summary>
        /// Каким кадром идёт показ — как его отдаёт камера, до поворота.
        /// Нужно тому, кто показывает: поворот и подгонку под круглое
        /// окошко он делает сам, и без этих чисел ему не с чего считать.
        /// </summary>
        public uint PreviewWidth { get; private set; }
        public uint PreviewHeight { get; private set; }

        /// <summary>
        /// На сколько повернуть показ. Телефон держат стоя, а снимает камера
        /// лёжа; передней камере поворот нужен в другую сторону.
        /// </summary>
        public double PreviewTurn
        {
            get { return Front ? 270 : 90; }
        }

        public string LastError { get; private set; }

        private DateTime _startedAt;

        public TimeSpan Elapsed
        {
            get { return Recording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero; }
        }

        /// <summary>
        /// Включить камеру и показать её в переданной рамке. До записи
        /// человек должен видеть, что попадает в кадр.
        /// </summary>
        public async Task<bool> StartPreviewAsync(CaptureElement target)
        {
            LastError = null;
            Edge = Plain;

            try
            {
                DeviceInformationCollection cameras =
                    await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

                if (cameras == null || cameras.Count == 0)
                {
                    LastError = "Камера не найдена.";
                    return false;
                }

                DeviceInformation chosen = Pick(cameras, Front) ?? cameras[0];

                _capture = new MediaCapture();

                var settings = new MediaCaptureInitializationSettings();
                settings.VideoDeviceId = chosen.Id;
                settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;

                await _capture.InitializeAsync(settings);

                Describe();

                // Квадрат берётся у самой камеры, а не выжимается из её
                // кадра потом, — и заказывается до показа: после него набор
                // уже не меняют.
                Squared = await ShapeAsync(MediaStreamType.VideoRecord);
                await ShapeAsync(MediaStreamType.VideoPreview);

                // Поворот заказывается только записи.
                //
                // Показу его заказывать бесполезно: рамка применяет поворот к
                // содержимому, но раскладывается по-прежнему под неповёрнутый
                // кадр — повёрнутое ложится внутрь чужой по пропорциям
                // коробки, и вокруг остаётся чёрное. Ни размером рамки, ни
                // способом вписывания это не лечится, поэтому показ
                // поворачивает и подгоняет себя сам, преобразованием.
                _capture.SetRecordRotation(Front
                    ? VideoRotation.Clockwise270Degrees
                    : VideoRotation.Clockwise90Degrees);

                target.Source = _capture;
                await _capture.StartPreviewAsync();

                Previewing = true;

                Log.Write("chat", "кружок: камера включена, "
                                  + (Front ? "передняя" : "основная"));

                return true;
            }
            catch (Exception error)
            {
                LastError = Explain(error);
                Log.Write("chat", "кружок: камера не включилась — " + error.Message);

                await StopPreviewAsync();
                return false;
            }
        }

        /// <summary>
        /// Чем камера умеет снимать. Просить у неё можно что угодно, а
        /// запишет она из своего списка — и по журналу видно, совпало ли
        /// заказанное с полученным.
        /// </summary>
        private void Describe()
        {
            try
            {
                var said = new System.Text.StringBuilder();

                foreach (Windows.Media.MediaProperties.IMediaEncodingProperties kind
                         in _capture.VideoDeviceController.GetAvailableMediaStreamProperties(
                                MediaStreamType.VideoRecord))
                {
                    var video = kind as Windows.Media.MediaProperties.VideoEncodingProperties;
                    if (video == null) continue;

                    said.Append(video.Width).Append('×').Append(video.Height).Append(' ');
                }

                Log.Write("chat", "кружок: камера умеет " + said);
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: не спросить, чем камера умеет — " + error.Message);
            }
        }

        private static DeviceInformation Pick(DeviceInformationCollection cameras, bool front)
        {
            foreach (DeviceInformation camera in cameras)
            {
                if (camera.EnclosureLocation == null) continue;

                Windows.Devices.Enumeration.Panel side = camera.EnclosureLocation.Panel;

                if (front && side == Windows.Devices.Enumeration.Panel.Front) return camera;
                if (!front && side == Windows.Devices.Enumeration.Panel.Back) return camera;
            }

            return null;
        }

        /// <summary>
        /// Выбрать, каким кадром камера будет снимать.
        ///
        /// Кружок квадратный, а камера снимает прямоугольником. Заказать
        /// квадрат одному лишь сжатию мало: кадр в него не обрежется, а
        /// сожмётся с боков — снятое выйдет вытянутым по высоте, и таким же
        /// его увидит собеседник. Зато у многих камер квадрат есть среди
        /// собственных наборов: выбрав его, сжимать и обрезать уже нечего.
        ///
        /// Если квадрата нет, берётся набор, ближе всего к нему стоящий:
        /// вписывать всё равно придётся, но чем ближе кадр к квадрату, тем
        /// уже чёрные поля по краям и тем меньше работы у телефона.
        ///
        /// Из подходящих по виду берётся самый крупный, какой не выше
        /// потолка: сторона кружка потом и назначается по нему.
        ///
        /// Возвращает, вышло ли снимать квадратом.
        /// </summary>
        private async Task<bool> ShapeAsync(MediaStreamType stream)
        {
            try
            {
                VideoEncodingProperties best = null;

                foreach (IMediaEncodingProperties kind
                         in _capture.VideoDeviceController.GetAvailableMediaStreamProperties(
                                stream))
                {
                    var video = kind as VideoEncodingProperties;
                    if (video == null || video.Width == 0 || video.Height == 0) continue;

                    if (best == null || Fitter(video, best)) best = video;
                }

                if (best == null)
                {
                    Log.Write("chat", "кружок: камера не назвала ни одного набора");
                    return false;
                }

                await _capture.VideoDeviceController.SetMediaStreamPropertiesAsync(stream, best);

                // Заказанное и полученное — разные вещи: набор бывает
                // принимают на словах и оставляют прежним. Спрашиваем, чем
                // камера снимает на самом деле: от этого зависит и перекос
                // снятого, и то, появится ли лишняя ступень сжатия.
                var now = _capture.VideoDeviceController.GetMediaStreamProperties(stream)
                          as VideoEncodingProperties;

                if (now == null)
                {
                    Log.Write("chat", "кружок: чем снимаем — камера не сказала");
                    return false;
                }

                bool square = now.Width == now.Height;

                if (stream == MediaStreamType.VideoPreview)
                {
                    PreviewWidth = now.Width;
                    PreviewHeight = now.Height;
                }
                else
                {
                    // Телефон держат стоя, а снимает камера лёжа: у записи
                    // стороны после поворота меняются местами.
                    _shotWidth = now.Height;
                    _shotHeight = now.Width;

                    // Сторона кружка — по длинной стороне снятого. У
                    // квадрата это она и есть, а у прямоугольника длинная
                    // сторона переживает натяжение целиком: короткую всё
                    // равно растянут, и терять на ней нечего.
                    uint longer = now.Width > now.Height ? now.Width : now.Height;

                    Edge = longer > Most ? Most : (longer < Plain ? Plain : longer);
                }

                Log.Write("chat", "кружок: заказали " + best.Width + "×" + best.Height
                                  + ", снимаем " + now.Width + "×" + now.Height
                                  + (square ? " — квадратом" : " — с полями до квадрата")
                                  + (stream == MediaStreamType.VideoRecord
                                         ? ", сторона " + Edge
                                         : ""));

                return square;
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: набор не заказать — " + error.Message);
                return false;
            }
        }

        /// <summary>
        /// Какой из двух наборов лучше подходит под кружок.
        ///
        /// Сперва по вытянутости: квадрат бьёт любой прямоугольник, а из
        /// прямоугольников лучше тот, что к квадрату ближе. При равной
        /// вытянутости мельче стороны кружка брать не из чего — увеличивать
        /// снятое некуда; из тех, что крупнее, берётся самый скромный:
        /// лишнее всё равно уйдёт при сжатии, а работы оно прибавляет.
        /// </summary>
        private static bool Fitter(VideoEncodingProperties offered,
                                   VideoEncodingProperties chosen)
        {
            double offeredShape = Shape(offered);
            double chosenShape = Shape(chosen);

            if (offeredShape != chosenShape) return offeredShape < chosenShape;

            bool offeredFits = Fits(offered);
            bool chosenFits = Fits(chosen);

            if (offeredFits != chosenFits) return offeredFits;

            long offeredArea = (long)offered.Width * offered.Height;
            long chosenArea = (long)chosen.Width * chosen.Height;

            // Из достаточных берём самый скромный — лишнее всё равно
            // уйдёт при сжатии, а работы оно прибавляет; из недостаточных
            // — самый крупный, чтобы выдумывать как можно меньше.
            return offeredFits ? offeredArea < chosenArea : offeredArea > chosenArea;
        }

        /// <summary>Во сколько раз кадр длиннее, чем шире. У квадрата — единица.</summary>
        private static double Shape(VideoEncodingProperties size)
        {
            double longer = size.Width > size.Height ? size.Width : size.Height;
            double shorter = size.Width > size.Height ? size.Height : size.Width;

            return longer / shorter;
        }

        /// <summary>
        /// Хватает ли набора на сторону кружка. Мельче брать незачем:
        /// увеличивать снятое — выдумывать то, чего камера не видела.
        /// </summary>
        private static bool Fits(VideoEncodingProperties size)
        {
            return size.Width >= Most && size.Height >= Most;
        }

        public async Task<bool> StartAsync()
        {
            if (!Previewing || Recording) return false;

            LastError = null;

            try
            {
                StorageFolder folder = ApplicationData.Current.TemporaryFolder;

                _file = await folder.CreateFileAsync(
                    "note_" + DateTime.UtcNow.Ticks + ".mp4",
                    CreationCollisionOption.ReplaceExisting);

                if (!await BeginAnyAsync()) return false;

                _startedAt = DateTime.UtcNow;
                Recording = true;

                Log.Write("chat", "кружок: запись пошла");
                return true;
            }
            catch (Exception error)
            {
                LastError = "Не удалось начать запись.";
                Log.Write("chat", "кружок: запись не началась — " + error.Message);

                Recording = false;
                return false;
            }
        }

        /// <summary>
        /// Сторона кружка у настоящего клиента. Ниже неё не опускаемся и к
        /// ней же отступаем, если камера не потянет крупнее.
        ///
        /// Квадратным кружок делается не для красоты: заливку проверяет
        /// сервер, и о негодной записи он отвечает одним словом
        /// «VIDEO_VALIDATION_FAILED», не называя, что именно ему не
        /// понравилось.
        /// </summary>
        private const uint Plain = 480;

        /// <summary>
        /// Выше этого не поднимаемся, даже если камера предлагает.
        ///
        /// Пока что ровно сторона настоящего клиента: кружок 640×640 сервер
        /// отверг — со всеми прочими его требованиями в порядке (квадрат,
        /// обычный диапазон, ровные тридцать кадров). Поднимать сюда
        /// число стоит только вместе с очередной проверкой на живом сервере.
        /// </summary>
        private const uint Most = 480;

        /// <summary>
        /// Сторона, которой пишется этот кружок. Берётся от камеры: у неё
        /// среди наборов нередко есть квадрат покрупнее обычного.
        /// </summary>
        public uint Edge { get; private set; }

        /// <summary>
        /// Три попытки начать запись, от самой похожей на кружок настоящего
        /// клиента к самой невзыскательной.
        ///
        /// Заказанное камере и полученное от неё — разные вещи: чего она не
        /// умеет, на том запись просто не начнётся. Поэтому требования
        /// снимаются по одному, а не все разом, и в журнале видно, на какой
        /// попытке дело пошло.
        /// </summary>
        private const int Exactly = 0;
        private const int Square = 1;
        private const int AsIs = 2;

        /// <summary>
        /// Начать запись, отступая от лучшего к терпимому.
        ///
        /// Крупный кадр телефон может и не потянуть — тогда пробуем ту же
        /// сторону, что у настоящего клиента, и только потом сдаёмся на
        /// «как выйдет».
        /// </summary>
        private async Task<bool> BeginAnyAsync()
        {
            if (await TryStartAsync(Exactly, Edge)) return true;
            if (await TryStartAsync(Square, Edge)) return true;

            if (Edge != Plain)
            {
                if (await TryStartAsync(Exactly, Plain)) return true;
                if (await TryStartAsync(Square, Plain)) return true;
            }

            return await TryStartAsync(AsIs, Edge);
        }

        private static string Named(int how)
        {
            if (how == Exactly) return "квадратом с вытянутой точкой";
            return how == Square ? "простым квадратом" : "как выйдет";
        }

        private async Task<bool> TryStartAsync(int how, uint side)
        {
            try
            {
                // Основа берётся по размеру: у неё своя частота, свой поток и
                // свой звук, и хотя мы переписываем всё это следом, начинать
                // с заведомо мелкой под крупный кадр незачем.
                MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(
                    side > Plain ? VideoEncodingQuality.HD720p : VideoEncodingQuality.Vga);

                // Кружок всегда квадратный.
                //
                // Неквадратный сервер не берёт ни от кого: один и тот же
                // файл 640×480, посланный и с телефона, где кружки уходят, и
                // с того, где не уходят, был отвергнут одинаково.
                //
                // Где камера квадрата не даёт, кадр в квадрат вписывает Media
                // Foundation — с чёрными полями по бокам, и поля эти уезжают
                // собеседнику: сервер кружок не перекодирует, что залили, то
                // он и покажет.
                //
                // Обойти это можно так: заказать квадрат, но объявить точку
                // неквадратной — ровно настолько, насколько вытянут кадр
                // камеры. Пропорции показа тогда совпадают, вписывать нечего,
                // и кадр ложится на весь квадрат. Объявление неквадратной
                // точки потом снимается при подготовке к заливке, отчего
                // картинка выходит растянутой вширь — зато без полей.
                if (how != AsIs)
                {
                    profile.Video.Width = side;
                    profile.Video.Height = side;
                }

                if (how == Exactly && !Squared && _shotWidth > 0 && _shotHeight > 0)
                {
                    uint slice = Common(_shotWidth, _shotHeight);

                    profile.Video.PixelAspectRatio.Numerator = _shotWidth / slice;
                    profile.Video.PixelAspectRatio.Denominator = _shotHeight / slice;

                    Log.Write("chat", "кружок: точка объявлена как "
                                      + (_shotWidth / slice) + ":" + (_shotHeight / slice));
                }

                profile.Video.Bitrate = Stream(side, Rate);

                // Остальное — как пишет кружок настоящий клиент: тридцать
                // кадров в секунду и одноканальный звук на сорока восьми
                // тысячах. Отступление здесь не безобидно: заливку проверяет
                // не наш телефон, а сервер, и о негодной записи он говорит
                // одно слово без подробностей.
                if (how != AsIs)
                {
                    profile.Video.FrameRate.Numerator = Rate;
                    profile.Video.FrameRate.Denominator = 1;

                    profile.Audio.ChannelCount = 1;
                    profile.Audio.SampleRate = 48000;
                    profile.Audio.Bitrate = 96000;
                }

                await _capture.StartRecordToStorageFileAsync(profile, _file);

                Boxed = !Squared && how != Exactly;

                Log.Write("chat", "кружок: пишем " + Named(how) + ", сторона " + side);
                return true;
            }
            catch (Exception error)
            {
                LastError = "Не удалось начать запись.";

                Log.Write("chat", "кружок: запись " + Named(how) + " стороной " + side
                                  + " не пошла, 0x" + ((uint)error.HResult).ToString("X8")
                                  + " — " + error.Message);
                return false;
            }
        }

        /// <summary>Наибольший общий делитель — чтобы отношение вышло коротким.</summary>
        private static uint Common(uint one, uint other)
        {
            while (other != 0)
            {
                uint left = one % other;
                one = other;
                other = left;
            }

            return one == 0 ? 1 : one;
        }

        /// <summary>Сколько кадров в секунду — столько же, сколько у настоящего.</summary>
        private const uint Rate = 30;

        /// <summary>
        /// Какой поток просить под кружок такой стороны и частоты.
        ///
        /// Настоящий клиент пишет 480×480 на тридцати кадрах примерно
        /// мегабитом. На спокойном лице этого хватает, а на движении
        /// картинка начинает сыпаться квадратами — на это жаловались и на
        /// 900 тысячах, и на мегабите. Берём в полтора раза больше:
        /// шестисекундный кружок прибавляет от этого четверть мегабайта,
        /// а рассыпаться перестаёт. Столько сервер точно берёт: среди
        /// принятых им записей была и на полтора мегабита.
        ///
        /// Считается от площади кадра и частоты, чтобы при смене
        /// стороны поток подобрался сам.
        /// </summary>
        private static uint Stream(uint side, uint rate)
        {
            long dots = (long)side * side;
            long stream = 1600000L * dots / (480 * 480) * rate / 30;

            // Выше двух с половиной мегабит не поднимаемся: кружок
            // уходит по мобильной связи, и лишний вес там дороже резкости.
            if (stream > 2500000) stream = 2500000;

            return (uint)stream;
        }

        /// <summary>Остановить запись и отдать снятое.</summary>
        public async Task<NoteTake> StopAsync()
        {
            if (!Recording) return new NoteTake();

            var take = new NoteTake
            {
                DurationMs = (int)(DateTime.UtcNow - _startedAt).TotalMilliseconds
            };

            Recording = false;

            try
            {
                await _capture.StopRecordAsync();
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: остановка сорвалась — " + error.Message);
                return take;
            }

            take.File = _file;
            _file = null;

            return take;
        }

        /// <summary>Бросить запись и убрать за собой снятый кусок.</summary>
        public async Task CancelAsync()
        {
            if (!Recording) return;

            Recording = false;

            try { await _capture.StopRecordAsync(); }
            catch (Exception) { }

            if (_file != null)
            {
                try { await _file.DeleteAsync(); }
                catch (Exception) { }

                _file = null;
            }

            Log.Write("chat", "кружок: запись отменена");
        }

        public async Task StopPreviewAsync()
        {
            if (_capture == null) { Previewing = false; return; }

            if (Recording) await CancelAsync();

            try
            {
                if (Previewing) await _capture.StopPreviewAsync();
            }
            catch (Exception) { }

            try { _capture.Dispose(); }
            catch (Exception) { }

            _capture = null;
            Previewing = false;
        }

        private static string Explain(Exception error)
        {
            uint code = (uint)error.HResult;

            if (code == 0x80070005 || code == 0xC00DABE0)
            {
                return "Нет доступа к камере или микрофону. Разрешите их в настройках телефона.";
            }

            return "Камера недоступна.";
        }

        public void Dispose()
        {
            var ignored = StopPreviewAsync();
        }
    }
}
