using System;
using System.Collections.Generic;
using System.ComponentModel;
using Lovit.Core;
using Lovit.Core.Media;
using Lovit.Models;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Lovit.Ui
{
    /// <summary>
    /// Строка списка. Шаблоны в XAML привязываются к свойствам, а модели
    /// протокола для этого не годятся: заголовок диалога считается по кэшу
    /// контактов, а время — по правилам показа. Этот класс и есть то, что
    /// видно на экране.
    /// </summary>
    public class ListRow : INotifyPropertyChanged
    {
        private ImageSource _avatar;
        private string _title;
        private string _preview;
        private string _time;
        private int _unreadCount;
        private bool _dimmed;

        public long Id { get; set; }

        /// <summary>
        /// Адрес поставленной аватарки. По нему видно, что она сменилась:
        /// поставленная однажды картинка иначе держалась бы до перезапуска,
        /// и попавшая не туда осталась бы навсегда.
        /// </summary>
        public string AvatarUrl { get; set; }

        /// <summary>
        /// Изменяемые поля, а не простые свойства: строка списка живёт дольше
        /// одного обновления. Пересоздавать её на каждый чих нельзя — вместе
        /// с ней пересоздавалась бы и аватарка, и список мигал бы всякий раз,
        /// когда приходит хоть одно сообщение.
        /// </summary>
        public string Title
        {
            get { return _title; }
            set { if (_title != value) { _title = value; Changed("Title"); } }
        }

        public string Preview
        {
            get { return _preview; }
            set { if (_preview != value) { _preview = value; Changed("Preview"); } }
        }

        public string Time
        {
            get { return _time; }
            set { if (_time != value) { _time = value; Changed("Time"); } }
        }

        public int UnreadCount
        {
            get { return _unreadCount; }
            set
            {
                if (_unreadCount == value) return;

                _unreadCount = value;
                Changed("Unread");
                Changed("UnreadVisibility");
            }
        }

        /// <summary>Приглушённая строка — чат с выключенными уведомлениями.</summary>
        public bool Dimmed
        {
            get { return _dimmed; }
            set { if (_dimmed != value) { _dimmed = value; Changed("TitleOpacity"); } }
        }

        public double TitleOpacity { get { return Dimmed ? 0.5 : 1.0; } }

        /// <summary>Что открывать по нажатию, когда строк несколько видов.</summary>
        public object Tag { get; set; }

        public string Unread
        {
            get { return UnreadCount > 99 ? "99+" : UnreadCount.ToString(); }
        }

        public Visibility UnreadVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        #region Аватарка

        /// <summary>
        /// Картинка приезжает позже самой строки: сеть медленнее списка.
        /// Поэтому строка умеет сообщать разметке, что аватарка приехала.
        /// </summary>
        public ImageSource Avatar
        {
            get { return _avatar; }
            set
            {
                if (_avatar == value) return;

                _avatar = value;

                Changed("Avatar");
                Changed("InitialsVisibility");
            }
        }

        private void Changed(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// Пока картинки нет — буквы на цветном квадрате. Так делали все
        /// приложения той поры: пустой серый прямоугольник в списке выглядит
        /// поломкой, а буква узнаётся с одного взгляда.
        /// </summary>
        public string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Title)) return "?";

                string[] words = Title.Split(' ');
                string first = words[0].Length > 0 ? words[0].Substring(0, 1) : "";

                if (words.Length > 1 && words[1].Length > 0) first += words[1].Substring(0, 1);

                return first.ToUpper();
            }
        }

        public Visibility InitialsVisibility
        {
            get { return _avatar == null ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Цвет подложки под буквами. Он не случайный, а выведен из
        /// идентификатора: у одного и того же чата плитка всегда одного
        /// цвета, и список не пестрит заново при каждом обновлении.
        /// </summary>
        public Brush AvatarBrush
        {
            get
            {
                Color[] palette =
                {
                    Color.FromArgb(255, 0x8C, 0x3B, 0xA6),   // сиреневый
                    Color.FromArgb(255, 0x00, 0x72, 0xC6),   // синий
                    Color.FromArgb(255, 0x00, 0x8A, 0x00),   // зелёный
                    Color.FromArgb(255, 0xC1, 0x4A, 0x00),   // оранжевый
                    Color.FromArgb(255, 0xA4, 0x00, 0x3D),   // малиновый
                    Color.FromArgb(255, 0x00, 0x80, 0x80)    // бирюзовый
                };

                long index = Math.Abs(Id) % palette.Length;
                return new SolidColorBrush(palette[index]);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion
    }

    /// <summary>
    /// Пузырь переписки. Разделитель дня — не отдельная строка списка, а часть
    /// первого сообщения этого дня: так он всегда едет вместе с ним и не
    /// повисает один на границе загруженного куска истории.
    /// </summary>
    public class MessageRow : INotifyPropertyChanged
    {
        private ImageSource _photo;
        private RichString _formatted;
        private string _text;
        private string _time;
        private string _reactions;

        public string MessageId { get; set; }

        /// <summary>
        /// Текст пузыря. Об изменении оповещает: правленое сообщение
        /// исправляется прямо на месте, а не пересборкой списка — пересборка
        /// опустошает его, прокрутка сваливается, и чат подпрыгивает.
        /// </summary>
        public string Text
        {
            get { return _text; }
            set
            {
                _text = value;

                Notify("Text");
                Notify("TextVisibility");
            }
        }

        /// <summary>Время, а после правки ещё и отметка «изм.».</summary>
        public string Time
        {
            get { return _time; }
            set
            {
                _time = value;
                Notify("Time");
            }
        }

        public string Author { get; set; }
        public string DayLabel { get; set; }
        public bool IsOutgoing { get; set; }

        /// <summary>Подпись над текстом: «Переслано от …» либо «Ответ на …».</summary>
        public string QuoteTitle { get; set; }
        public string QuoteText { get; set; }

        /// <summary>
        /// Реакции строкой. Меняются чаще всего прочего — и своей рукой, и
        /// чужой, — поэтому тоже правятся на месте.
        /// </summary>
        public string Reactions
        {
            get { return _reactions; }
            set
            {
                _reactions = value;

                Notify("Reactions");
                Notify("ReactionsVisibility");
            }
        }

        /// <summary>Адрес вложенной картинки — фотографии или стикера.</summary>
        public string PhotoUrl { get; set; }

        /// <summary>
        /// Картинка будет, но адреса у неё нет: кадр ролика приходит байтами
        /// внутри самого сообщения. Место под него отводится сразу — иначе
        /// пузырь подрастает в тот миг, когда кадр разобрался, и прокрутка,
        /// уже доведённая до конца, оказывается не у конца.
        /// </summary>
        public bool PhotoExpected { get; set; }

        /// <summary>
        /// Несколько фотографий одного сообщения — рядами сетки. Пусто,
        /// когда снимок один: тот показывается во всю ширину пузыря.
        /// </summary>
        public IList<AlbumLine> Album { get; set; }

        public Visibility AlbumVisibility
        {
            get
            {
                return Album == null || Album.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        /// <summary>
        /// Стикер — не фотография: он рисуется без пузыря и без серой
        /// подложки, и размером поменьше. В оригинальном клиенте он висит
        /// прямо на фоне переписки.
        /// </summary>
        public bool IsSticker { get; set; }

        /// <summary>
        /// Кнопки под сообщением бота — рядами, как их прислал сервер.
        /// </summary>
        public IList<IList<InlineButton>> Buttons { get; set; }

        #region Проигрыватель

        /// <summary>
        /// Голосовое, музыка или видео — то, что можно послушать или
        /// посмотреть прямо в переписке.
        /// </summary>
        public bool IsPlayable { get; set; }

        /// <summary>
        /// Кружок — видео-сообщение. Показывается не как ролик: кругом и
        /// без облачка, потому что это и есть его вид у всех, кто его
        /// умеет. Прямоугольный кадр в облачке был бы просто видео.
        /// </summary>
        public bool IsNote { get; set; }

        /// <summary>Сторона кружка в переписке.</summary>
        public const double NoteSide = 220;

        /// <summary>Что написано на плашке: «Голосовое сообщение», имя песни.</summary>
        public string MediaTitle { get; set; }

        /// <summary>
        /// Чьё это вложение. У сообщения их бывает несколько, и каждое идёт
        /// своей строкой; без этой ссылки нажатие на вторую открывало бы
        /// первое.
        /// </summary>
        public MaxAttachment Source { get; set; }

        private string _mediaGlyph = "";   // треугольник «пуск»
        private string _mediaStatus;
        private double _mediaPercent;
        private bool _mediaBusy;

        /// <summary>Значок кнопки: пуск или пауза.</summary>
        public string MediaGlyph
        {
            get { return _mediaGlyph; }
            set { _mediaGlyph = value; Notify("MediaGlyph"); }
        }

        /// <summary>Подпись под плашкой: время или ход скачивания.</summary>
        public string MediaStatus
        {
            get { return _mediaStatus; }
            set { _mediaStatus = value; Notify("MediaStatus"); }
        }

        /// <summary>Сколько прошло, от нуля до ста.</summary>
        public double MediaPercent
        {
            get { return _mediaPercent; }
            set { _mediaPercent = value; Notify("MediaPercent"); }
        }

        /// <summary>Идёт ли скачивание или проигрывание — показывать ли полосу.</summary>
        public bool MediaBusy
        {
            get { return _mediaBusy; }
            set
            {
                _mediaBusy = value;
                Notify("MediaBusy");
                Notify("MediaBarVisibility");
                Notify("WaveVisibility");
            }
        }

        /// <summary>Есть ли кадр — по нему решается, где рисовать управление.</summary>
        private bool HasFrame
        {
            get { return !string.IsNullOrEmpty(PhotoUrl) || _photo != null; }
        }

        /// <summary>
        /// Плашка под картинкой. У ролика с кадром её нет: значок и время
        /// лежат поверх самого кадра, как это делают нынешние мессенджеры, а
        /// подпись «Видео» под ним не нужна вовсе — и так видно, что это.
        /// </summary>
        public Visibility MediaVisibility
        {
            get
            {
                if (!IsPlayable || HasFrame) return Visibility.Collapsed;
                return Visibility.Visible;
            }
        }

        /// <summary>
        /// График громкости голосового — одной фигурой, а не полусотней
        /// отдельных прямоугольников: строк в переписке много, и полсотни
        /// лишних частей разметки в каждой обошлись бы дорого.
        /// </summary>
        public Geometry WaveShape { get; set; }

        /// <summary>Отсчёты, из которых он построен, — по ним считается закраска.</summary>
        public byte[] WaveBytes { get; set; }

        private Geometry _wavePlayed;

        /// <summary>Пройденная часть графика — она поверх и ярче.</summary>
        public Geometry WavePlayed
        {
            get { return _wavePlayed; }
            set { _wavePlayed = value; Notify("WavePlayed"); }
        }

        /// <summary>
        /// График виден всегда, в том числе пока идёт загрузка: пропадая, он
        /// уносил с собой ширину, и облачко на это время сжималось до подписи.
        /// Полоса с процентами показывается поверх него.
        /// </summary>
        public Visibility WaveVisibility
        {
            get { return WaveShape != null ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>Значок и время поверх кадра.</summary>
        public Visibility OverlayVisibility
        {
            get { return IsPlayable && HasFrame ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MediaBarVisibility
        {
            get { return MediaBusy ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility NoteVisibility
        {
            get { return IsNote ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Чем кадр обрезается в круг.
        ///
        /// Прямого способа обрезать по кругу в этой разметке нет: обрезка
        /// умеет только прямоугольник. Вырезаем наоборот — закрашиваем всё
        /// вокруг круга цветом страницы, поверх кадра.
        /// </summary>
        public Geometry NoteMask
        {
            get
            {
                if (!IsNote) return null;

                const double Half = NoteSide / 2;

                var group = new GeometryGroup();
                group.FillRule = FillRule.EvenOdd;

                group.Children.Add(new RectangleGeometry
                {
                    Rect = new Rect(0, 0, NoteSide, NoteSide)
                });

                group.Children.Add(new EllipseGeometry
                {
                    Center = new Point(Half, Half),
                    RadiusX = Half,
                    RadiusY = Half
                });

                return group;
            }
        }

        /// <summary>
        /// Где стоит длительность ролика. У прямоугольного кадра — в углу,
        /// а у кружка углов нет: там она ушла бы под обрезку.
        /// </summary>
        public HorizontalAlignment MediaStatusSide
        {
            get { return IsNote ? HorizontalAlignment.Center : HorizontalAlignment.Left; }
        }

        /// <summary>Вернуть плашку в исходный вид — когда играть перестали.</summary>
        public void ResetMedia(string status)
        {
            MediaGlyph = "";
            MediaPercent = 0;
            MediaBusy = false;
            MediaStatus = status;
        }

        #endregion

        /// <summary>
        /// Высота, под которую место отводится заранее, — из размеров,
        /// присланных вместе с картинкой. Без этого список дёргался: пузырь
        /// вырастал в тот миг, когда фотография догружалась.
        /// </summary>
        public double PhotoHeight { get; set; }

        /// <summary>
        /// Служебное сообщение — «вошёл», «вышел», «закрепил», звонок. Такое
        /// рисуется строкой посередине, без пузыря: это не чья-то реплика.
        /// </summary>
        public bool IsService { get; set; }

        /// <summary>Присланный файл: имя и размер, по нажатию открывается.</summary>
        public string FileName { get; set; }
        public string FileSize { get; set; }

        /// <summary>
        /// Текст вместе с разметкой — ссылками, жирным, наклонным. Пока
        /// разметки нет, здесь просто текст: собирается всё равно кодом,
        /// потому что привязать набор Inline к строке нельзя.
        ///
        /// В пузыре показано именно это, а не `Text`, — и оповещать об
        /// изменении надо здесь же. Без этого правленое сообщение остаётся
        /// на экране прежним до первой пересборки списка, то есть до чужого
        /// сообщения или до перезахода в чат.
        /// </summary>
        public RichString Formatted
        {
            get { return _formatted; }
            set
            {
                _formatted = value;
                Notify("Formatted");
            }
        }

        public HorizontalAlignment Side
        {
            get { return IsOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        /// <summary>
        /// Свои сообщения — цветом системы, чужие — её же фоном плиток.
        /// Собственной палитры у приложения нет: пользователь выбрал цвет
        /// в настройках телефона, и переписка должна его слушаться.
        /// </summary>
        public Brush Bubble
        {
            get
            {
                // У стикера пузыря нет вовсе — он и сам по себе картинка с
                // прозрачным краем, и в рамке выглядел бы наклейкой на листе.
                // У кружка — по той же причине: он круглый, а облачко
                // квадратное, и углы торчали бы из-под него.
                if (IsSticker || IsNote) return new SolidColorBrush(Colors.Transparent);

                object key = Application.Current.Resources[IsOutgoing ? "PhoneAccentBrush" : "PhoneChromeBrush"];
                return key as Brush;
            }
        }

        /// <summary>
        /// Серая подложка под картинку — она же занятое место, пока картинка
        /// едет. Стикеру не нужна: он прозрачный, и подложка стала бы видимым
        /// прямоугольником вокруг него.
        /// </summary>
        public Brush PhotoBackdrop
        {
            get
            {
                if (IsSticker) return new SolidColorBrush(Colors.Transparent);
                return new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0));
            }
        }

        /// <summary>
        /// Ширина картинки. У фотографии — по пузырю (NaN значит «сколько
        /// дадут»), у стикера своя: во всю ширину пузыря он был бы вдвое
        /// больше, чем в оригинальном клиенте.
        /// </summary>
        public double PhotoWidth
        {
            get
            {
                if (IsSticker) return 160;
                return IsNote ? NoteSide : double.NaN;
            }
        }

        public Visibility ButtonsVisibility
        {
            get { return Buttons == null || Buttons.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// Текст на своём пузыре всегда белый: цвет выделения пользователь
        /// выбирает сам, и обычный цвет текста на нём в светлой теме
        /// оказался бы тёмным по тёмному. Система на плитках поступает так же.
        ///
        /// А вот там, где пузыря нет вовсе — у стикера и кружка, — белый
        /// лёг бы прямо на фон страницы и в светлой теме пропал: время под
        /// кружком берётся обычным цветом.
        /// </summary>
        public Brush TextBrush
        {
            get
            {
                if (!IsOutgoing || IsSticker || IsNote)
                {
                    return Application.Current.Resources["PhoneForegroundBrush"] as Brush;
                }

                return new SolidColorBrush(Colors.White);
            }
        }

        public ImageSource Photo
        {
            get { return _photo; }
            set
            {
                _photo = value;

                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler == null) return;

                handler(this, new PropertyChangedEventArgs("Photo"));
                handler(this, new PropertyChangedEventArgs("PhotoVisibility"));
                handler(this, new PropertyChangedEventArgs("TextVisibility"));

                // Кадр, вложенный в сообщение, приезжает уже после сборки
                // строки — и вместе с ним переезжает управление роликом.
                handler(this, new PropertyChangedEventArgs("MediaVisibility"));
                handler(this, new PropertyChangedEventArgs("OverlayVisibility"));
            }
        }

        /// <summary>
        /// Место под картинку занимается сразу, ещё до её появления: адрес
        /// известен с самого начала, а сама она приезжает по сети.
        /// </summary>
        public Visibility PhotoVisibility
        {
            get
            {
                // Кадр из сообщения приходит байтами, без адреса: о нём
                // говорит `PhotoExpected`.
                if (!string.IsNullOrEmpty(PhotoUrl) || _photo != null || PhotoExpected)
                {
                    return Visibility.Visible;
                }

                return Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Подпись «Фото» вместо картинки не нужна, раз картинка будет, —
        /// а настоящая подпись под фотографией нужна.
        /// </summary>
        public Visibility TextVisibility
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return Visibility.Collapsed;

                bool placeholder = Text == "Фото" || Text == "Стикер";
                if (!string.IsNullOrEmpty(PhotoUrl) && placeholder) return Visibility.Collapsed;

                return Visibility.Visible;
            }
        }

        /// <summary>Служебное вместо пузыря и пузырь вместо служебного.</summary>
        public Visibility ServiceVisibility
        {
            get { return IsService ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility BubbleVisibility
        {
            get { return IsService ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility FileVisibility
        {
            get { return string.IsNullOrEmpty(FileName) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility DayVisibility
        {
            get { return string.IsNullOrEmpty(DayLabel) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility AuthorVisibility
        {
            get { return string.IsNullOrEmpty(Author) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility QuoteVisibility
        {
            get { return string.IsNullOrEmpty(QuoteTitle) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ReactionsVisibility
        {
            get { return string.IsNullOrEmpty(Reactions) ? Visibility.Collapsed : Visibility.Visible; }
        }

        private void Notify(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Ряд сетки фотографий.</summary>
    public class AlbumLine
    {
        public IList<AlbumCell> Cells { get; set; }
    }

    /// <summary>
    /// Одна фотография в сетке. Размер у неё свой, а не общий: в ряду из
    /// трёх ячейки уже, чем в ряду из двух, и посчитаны они заранее — иначе
    /// сетка меняла бы высоту в тот миг, когда снимки догружаются.
    /// </summary>
    public class AlbumCell : INotifyPropertyChanged
    {
        private ImageSource _photo;

        public MaxAttachment Source { get; set; }
        public string Url { get; set; }

        public double Wide { get; set; }
        public double Tall { get; set; }

        /// <summary>
        /// Все снимки этого сообщения и место этого среди них: из
        /// просмотрщика по ним листают, не возвращаясь в переписку.
        /// Список тот же самый у всех ячеек — не копия на каждую.
        /// </summary>
        public IList<string> All { get; set; }
        public int At { get; set; }

        /// <summary>Зазор до соседей — им и разделяются ячейки.</summary>
        public Thickness Gap { get; set; }

        /// <summary>«+3» на последней, когда показаны не все.</summary>
        public string More { get; set; }

        public Visibility MoreVisibility
        {
            get { return string.IsNullOrEmpty(More) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public ImageSource Photo
        {
            get { return _photo; }
            set
            {
                _photo = value;

                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null) handler(this, new PropertyChangedEventArgs("Photo"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Время и даты в том же виде, что в версии для iOS.</summary>
    public static class Formats
    {
        /// <summary>Ширина сетки фотографий — она же ширина пузыря без полей.</summary>
        public const double AlbumWidth = 356;

        /// <summary>Зазор между снимками.</summary>
        private const double AlbumGap = 3;

        /// <summary>Больше этого в сетку не ставим — на последней пишем остаток.</summary>
        private const int AlbumMost = 6;

        /// <summary>
        /// Разложить несколько снимков одного сообщения сеткой.
        ///
        /// Ряды подобраны так, чтобы сетка всегда была одной ширины с
        /// пузырём, а ячейки в ряду — одного размера: два снимка встают
        /// рядом, три — один сверху и два под ним, четыре — квадратом,
        /// пять и шесть — по два и по три. Больше шести не показываем:
        /// седьмой и дальше считаются на последней ячейке.
        ///
        /// Столбиком в полную ширину, как было раньше, десяток снимков
        /// занимал десяток экранов, и пролистать переписку сквозь них
        /// становилось нельзя.
        /// </summary>
        public static List<AlbumLine> Album(IList<MaxAttachment> photos)
        {
            var made = new List<AlbumLine>();
            if (photos == null || photos.Count < 2) return made;

            int shown = Math.Min(photos.Count, AlbumMost);
            int[] rows = Rows(shown);

            // В просмотрщике листаются все снимки сообщения, включая те,
            // что в сетку не попали и сосчитаны на последней ячейке.
            var all = new List<string>(photos.Count);
            foreach (MaxAttachment photo in photos) all.Add(photo.Url);

            int at = 0;

            foreach (int count in rows)
            {
                double wide = (AlbumWidth - AlbumGap * (count - 1)) / count;

                // Один снимок в ряду — это полоса во всю ширину: квадратом
                // такой ряд занял бы пол-экрана.
                double tall = count == 1 ? AlbumWidth * 0.55 : wide;

                var cells = new List<AlbumCell>();

                for (int i = 0; i < count; i++, at++)
                {
                    MaxAttachment photo = photos[at];
                    bool last = at == shown - 1;

                    cells.Add(new AlbumCell
                    {
                        Source = photo,
                        Url = photo.Url,
                        All = all,
                        At = at,
                        Wide = wide,
                        Tall = tall,
                        Gap = new Thickness(0, 0, i == count - 1 ? 0 : AlbumGap, AlbumGap),
                        More = last && photos.Count > shown
                            ? "+" + (photos.Count - shown)
                            : null
                    });
                }

                made.Add(new AlbumLine { Cells = cells });
            }

            return made;
        }

        /// <summary>По скольку снимков в каждом ряду.</summary>
        private static int[] Rows(int count)
        {
            switch (count)
            {
                case 2: return new[] { 2 };
                case 3: return new[] { 1, 2 };
                case 4: return new[] { 2, 2 };
                case 5: return new[] { 2, 3 };
                default: return new[] { 3, 3 };
            }
        }

        /// <summary>
        /// График громкости голосового: столбики от середины вверх и вниз.
        ///
        /// Считается в условных единицах — ширина 100, высота 1, — а на месте
        /// растягивается под свою плашку. Так одна и та же фигура годится
        /// любой ширине экрана, и пересчитывать её при повороте не надо.
        ///
        /// Столбиков берём не больше полусотни: их присылают и вдвое больше,
        /// но на телефонном экране такие уже не различить.
        /// </summary>
        public static Geometry Wave(byte[] values)
        {
            return Wave(values, 1.0);
        }

        /// <summary>
        /// То же, но только пройденная часть — ею закрашивается уже
        /// прозвучавшее.
        ///
        /// Обрезаем по столбикам, а не по точкам: закраска идёт вровень с
        /// краями столбиков, без половинок. Чтобы обе фигуры совпали при
        /// растягивании, у обрезанной остаётся та же ширина — её держит
        /// незаметная чёрточка на правом краю.
        /// </summary>
        public static Geometry Wave(byte[] values, double part)
        {
            if (values == null || values.Length == 0) return null;

            const int Most = 50;

            int count = Math.Min(values.Length, Most);
            int step = values.Length / count;
            if (step < 1) step = 1;

            double width = 100.0 / count;
            double bar = width * 0.6;

            // Ничего не пройдено — и рисовать нечего. Пустая фигура здесь
            // недопустима: у неё нет ширины, а растягивание «на всю плашку»
            // превратило бы её в сплошной прямоугольник. Он и мелькал белым
            // в первый миг проигрывания.
            if (part <= 0) return null;

            double edge = count * Math.Min(part, 1.0);
            int whole = (int)edge;

            var shape = new GeometryGroup();

            // Ширину держим прежней, даже когда закрашена только часть: иначе
            // короткая фигура растянулась бы на всю плашку и столбики двух
            // слоёв разошлись бы.
            if (edge < count)
            {
                shape.Children.Add(new RectangleGeometry
                {
                    Rect = new Rect(100, 0.5, 0.01, 0.01)
                });
            }

            int drawn = Math.Min(whole + 1, count);

            for (int i = 0; i < drawn; i++)
            {
                // Между столбиками берём наибольшее, а не первое попавшееся:
                // так на глаз остаётся тот же рисунок, что и в полном графике.
                int peak = 0;
                for (int s = 0; s < step; s++)
                {
                    int at = i * step + s;
                    if (at < values.Length && values[at] > peak) peak = values[at];
                }

                // Совсем пустые всё равно рисуем чёрточкой: сплошной график
                // с провалами до нуля выглядит рваным.
                double height = Math.Max(peak / 255.0, 0.08);

                // Последний столбик закрашивается по частям, а не целиком:
                // иначе закраска прыгала бы через столбик за раз, и это было
                // видно как рывки.
                double piece = bar;
                if (i == whole && whole < count) piece = bar * (edge - whole);

                if (piece <= 0) continue;

                shape.Children.Add(new RectangleGeometry
                {
                    Rect = new Rect(i * width, (1 - height) / 2, piece, height)
                });
            }

            return shape;
        }

        /// <summary>
        /// Время на секундомере — для записи голосового и кружка. Часов тут
        /// не бывает, поэтому их и нет: минуты и секунды.
        /// </summary>
        public static string Clock(TimeSpan passed)
        {
            long seconds = (long)passed.TotalSeconds;
            if (seconds < 0) seconds = 0;

            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        /// <summary>Объём словами — для полосы отправки и плашки файла.</summary>
        public static string Size(long bytes)
        {
            if (bytes <= 0) return "0 Б";
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " КБ";

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
        }

        /// <summary>
        /// Текст в одну строку — для превью, закрепа и цитат.
        ///
        /// Переносы внутри сообщения превращаются в пробелы, а не остаются
        /// переносами: иначе в строке списка две отведённые строки съедало бы
        /// одно короткое слово с пустой строкой под ним. Обрезать после этого
        /// есть что и есть где — по-настоящему длинный текст ляжет в две
        /// строки и оборвётся на второй.
        /// </summary>
        public static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var line = new System.Text.StringBuilder(text.Length);
            bool space = false;

            foreach (char symbol in text)
            {
                bool blank = symbol == '\n' || symbol == '\r' || symbol == '\t' || symbol == ' ';

                if (blank)
                {
                    // Несколько пробелов подряд — это по-прежнему один пробел.
                    if (line.Length > 0) space = true;
                    continue;
                }

                if (space)
                {
                    line.Append(' ');
                    space = false;
                }

                line.Append(symbol);
            }

            return line.ToString();
        }

        /// <summary>Сегодняшнее — часами, остальное — датой.</summary>
        public static string RelativeTime(long milliseconds)
        {
            if (milliseconds <= 0) return "";

            DateTime moment = FromUnix(milliseconds);
            return moment.Date == DateTime.Now.Date
                ? moment.ToString("HH:mm")
                : moment.ToString("dd.MM.yy");
        }

        public static string ShortTime(long milliseconds)
        {
            if (milliseconds <= 0) return "";
            return FromUnix(milliseconds).ToString("HH:mm");
        }

        /// <summary>
        /// Надпись над первым сообщением дня: «Сегодня», «Вчера» или дата.
        /// </summary>
        public static string DaySeparator(long milliseconds)
        {
            if (milliseconds <= 0) return "";

            DateTime day = FromUnix(milliseconds).Date;
            if (day == DateTime.Now.Date) return "Сегодня";
            if (day == DateTime.Now.Date.AddDays(-1)) return "Вчера";

            return day.Year == DateTime.Now.Year
                ? day.ToString("d MMMM")
                : day.ToString("d MMMM yyyy");
        }

        public static bool SameDay(long first, long second)
        {
            if (first <= 0 || second <= 0) return false;
            return FromUnix(first).Date == FromUnix(second).Date;
        }

        /// <summary>
        /// Время протокола — миллисекунды с 1970 года в UTC; показывать его
        /// надо в поясе устройства, иначе «сегодня» наступит не тогда.
        /// </summary>
        private static DateTime FromUnix(long milliseconds)
        {
            return UnixTime.ToLocal(milliseconds);
        }
    }
}
