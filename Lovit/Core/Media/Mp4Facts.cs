using System;
using System.Text;

namespace Lovit.Core.Media
{
    /// <summary>
    /// Что внутри снятого ролика: части, размеры кадра, длительность.
    ///
    /// Нужно это для одного разговора — с сервером. На заливку кружка он
    /// отвечает единственным словом «VIDEO_VALIDATION_FAILED» и никаких
    /// подробностей не даёт, а отладчика у нас нет. Значит, описать ролик
    /// придётся самим и положить описание в журнал.
    ///
    /// Разбирается только оболочка MP4 — она устроена как дерево частей, у
    /// каждой длина и четырёхбуквенное имя. Ни одного кадра здесь не
    /// раскодируется.
    /// </summary>
    public static class Mp4Facts
    {
        public static string Describe(byte[] mp4)
        {
            if (mp4 == null || mp4.Length < 16) return "ролика нет вовсе";

            try
            {
                var said = new StringBuilder();

                said.Append(mp4.Length).Append(" байт; части:");

                int at = 0;
                int moovAt = -1;
                int moovEnd = -1;

                while (at + 8 <= mp4.Length)
                {
                    long length = Read32(mp4, at);
                    string name = Name(mp4, at + 4);

                    int head = 8;

                    // Длина в единице означает, что настоящая лежит следом
                    // восемью байтами: так записывают части больше четырёх
                    // гигабайт. Ноль — «до конца файла».
                    if (length == 1 && at + 16 <= mp4.Length)
                    {
                        length = Read64(mp4, at + 8);
                        head = 16;
                    }
                    else if (length == 0)
                    {
                        length = mp4.Length - at;
                    }

                    if (length < head) break;

                    said.Append(' ').Append(name).Append('(').Append(length).Append(')');

                    // Заявленный вид файла — по нему принимающая сторона и
                    // выбирает, чем его разбирать.
                    if (name == "ftyp")
                    {
                        said.Append('=').Append(Brands(mp4, at + head, (int)(at + length)));
                    }

                    if (name == "moov") { moovAt = at + head; moovEnd = (int)(at + length); }

                    at += (int)length;
                }

                if (moovAt < 0) return said + "; описания дорожек нет";

                said.Append("; ").Append(Movie(mp4, moovAt, moovEnd));

                return said.ToString();
            }
            catch (Exception error)
            {
                return "ролик не разобрать: " + error.Message;
            }
        }

        /// <summary>
        /// Дописан ли ролик до конца.
        ///
        /// Съёмка складывает файл из двух частей: куски звука и картинки
        /// (`mdat`) пишутся по ходу, а описание дорожек (`moov`) —
        /// последним, когда всё уже снято. Если запись оборвалась, второй
        /// части не будет вовсе: воспроизвести такое нечем, и заливать
        /// незачем — сервер ответит отказом, а человек не поймёт, чем.
        /// </summary>
        public static bool Ready(byte[] mp4)
        {
            if (mp4 == null || mp4.Length < 16) return false;

            try
            {
                bool described = false;
                bool filled = false;

                int at = 0;

                while (at + 8 <= mp4.Length)
                {
                    long length = Read32(mp4, at);
                    int head = 8;

                    if (length == 1 && at + 16 <= mp4.Length)
                    {
                        length = Read64(mp4, at + 8);
                        head = 16;
                    }
                    else if (length == 0)
                    {
                        length = mp4.Length - at;
                    }

                    if (length < head) break;

                    string name = Name(mp4, at + 4);

                    if (name == "moov") described = true;
                    if (name == "mdat") filled = true;

                    at += (int)length;
                }

                return described && filled;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Приготовить снятое к заливке: заявить обычный вид файла,
        /// переложить описание в начало и выбросить лишнее.
        ///
        /// Описание Windows Phone пишет в конец: пока съёмка идёт, длины
        /// дорожек неизвестны, и проще дописать его последним. Так поступают
        /// почти все, и для проигрывания с диска это безразлично — а вот тот,
        /// кто принимает заливку, читает начало файла и по нему решает,
        /// годится ли ролик. Не найдя описания там, он отвечает отказом.
        ///
        /// Переложить его нельзя простым перемещением: внутри описания лежат
        /// расстояния до кусков звука и картинки, считанные от начала файла.
        /// Сдвинув описание вперёд, приходится сдвинуть и все эти числа.
        ///
        /// Возвращает null, если готовить нечего или не вышло, — тогда
        /// уезжает то, что сняли.
        /// </summary>
        public static byte[] Prepare(byte[] mp4)
        {
            if (mp4 == null || mp4.Length < 16) return null;

            try
            {
                var starts = new System.Collections.Generic.List<int>();
                var lengths = new System.Collections.Generic.List<int>();
                var names = new System.Collections.Generic.List<string>();

                int at = 0;

                while (at + 8 <= mp4.Length)
                {
                    long length = Read32(mp4, at);
                    string name = Name(mp4, at + 4);

                    if (length == 1 && at + 16 <= mp4.Length) length = Read64(mp4, at + 8);
                    else if (length == 0) length = mp4.Length - at;

                    if (length < 8 || at + length > mp4.Length) return null;

                    starts.Add(at);
                    lengths.Add((int)length);
                    names.Add(name);

                    at += (int)length;
                }

                int moov = names.IndexOf("moov");
                int mdat = names.IndexOf("mdat");

                if (moov < 0 || mdat < 0) return null;

                // Куски звука и картинки должны лежать одной частью: только
                // тогда все расстояния до них сдвигаются на одну и ту же
                // величину.
                if (names.LastIndexOf("mdat") != mdat) return null;

                // Новый порядок: сперва объявление вида файла — своё, а не
                // написанное Windows, — за ним описание, остальное следом.
                // Части, которых стандарт не знает, выбрасываем: их пишет
                // сюда сама Windows, и на той стороне они только мешают
                // разбирать.
                byte[] head = Brand();

                var order = new System.Collections.Generic.List<int>();
                order.Add(moov);

                for (int box = 0; box < names.Count; box++)
                {
                    if (names[box] == "ftyp" || box == moov) continue;
                    if (names[box] == "uuid" || names[box] == "free"
                        || names[box] == "skip") continue;

                    order.Add(box);
                }

                int total = head.Length;
                int placed = -1;

                foreach (int box in order)
                {
                    if (box == mdat) placed = total;
                    total += lengths[box];
                }

                if (placed < 0) return null;

                // На сколько уехали куски — на столько же надо подвинуть все
                // расстояния до них внутри описания.
                int delta = placed - starts[mdat];

                var described = new byte[lengths[moov]];
                Buffer.BlockCopy(mp4, starts[moov], described, 0, described.Length);

                // Диапазон яркости правится на месте, длины не меняя, —
                // поэтому до сдвига или после, безразлично.
                Limit(described);

                if (delta != 0) Shift(described, 8, described.Length - 8, delta);

                var made = new byte[total];

                Buffer.BlockCopy(head, 0, made, 0, head.Length);
                int cursor = head.Length;

                foreach (int box in order)
                {
                    if (box == moov)
                    {
                        Buffer.BlockCopy(described, 0, made, cursor, described.Length);
                        cursor += described.Length;
                        continue;
                    }

                    Buffer.BlockCopy(mp4, starts[box], made, cursor, lengths[box]);
                    cursor += lengths[box];
                }

                return made;
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: описание не переложить — " + error.Message);
                return null;
            }
        }

        /// <summary>
        /// Объявить обычный диапазон яркости вместо полного.
        ///
        /// Кодировщики Windows Phone делятся надвое. Одни пишут яркость от
        /// нуля до двухсот пятидесяти пяти и объявляют это в описании потока
        /// (`video_full_range_flag`), другие держатся обычных рамок и не
        /// объявляют ничего. Первые кружки сервер не принимает: на заливку
        /// он отвечает «VIDEO_VALIDATION_FAILED», и никаких подробностей.
        ///
        /// Проверено прямо: один и тот же ролик, четырежды отвергнутый, был
        /// принят после того, как в нём погасили этот единственный бит.
        ///
        /// Сами точки при этом не трогаются — меняется только то, как их
        /// велено читать. Картинка от этого становится чуть контрастнее, но
        /// иначе она не уезжает вовсе.
        /// </summary>
        private static void Limit(byte[] moov)
        {
            try
            {
                foreach (int table in Find(moov, 8, moov.Length - 8, "stsd"))
                {
                    int entry = table + 8;
                    if (entry + 16 > moov.Length) continue;

                    long size = Read32(moov, entry);
                    if (size < 16 || entry + size > moov.Length) continue;

                    string kind = Name(moov, entry + 4);
                    if (kind != "avc1" && kind != "avc3") continue;

                    int from;
                    int to;

                    if (!Inside(moov, entry + 86, (int)(entry + size), "avcC", out from, out to))
                    {
                        continue;
                    }

                    Widen(moov, from, to);

                    // Форма точки, объявленная отдельной частью. Запись
                    // делается нарочно вытянутой — иначе кадр вписался бы в
                    // квадрат с чёрными полями, — а объявление вытянутости
                    // снимается здесь: сервер берёт только квадрат.
                    if (Inside(moov, entry + 86, (int)(entry + size), "pasp", out from, out to)
                        && from + 8 <= to
                        && (Read32(moov, from) != 1 || Read32(moov, from + 4) != 1))
                    {
                        Write32(moov, from, 1);
                        Write32(moov, from + 4, 1);

                        Log.Write("chat", "кружок: вытянутая точка в pasp снята");
                    }
                }
            }
            catch (Exception error)
            {
                Log.Write("chat", "кружок: диапазон яркости не поправить — " + error.Message);
            }
        }

        /// <summary>
        /// Пройти по описаниям потока внутри avcC и поправить каждое.
        ///
        /// Их там может лежать несколько: сперва по счётчику идут описания
        /// последовательности, за ними описания кадра — вторые нам не нужны.
        /// </summary>
        private static void Widen(byte[] moov, int at, int end)
        {
            if (at + 6 > end) return;

            int many = moov[at + 5] & 0x1F;
            int cursor = at + 6;

            for (int which = 0; which < many; which++)
            {
                if (cursor + 2 > end) return;

                int length = (moov[cursor] << 8) | moov[cursor + 1];
                cursor += 2;

                if (length < 2 || cursor + length > end) return;

                Fix(moov, cursor, length);
                cursor += length;
            }
        }

        /// <summary>
        /// Погасить бит диапазона в одном описании последовательности.
        ///
        /// Записано оно не байтами, а битами подряд, и до нужного приходится
        /// прочесть всё, что лежит впереди. Вдобавок внутри рассыпаны лишние
        /// байты — их ставят там, где иначе получилось бы начало новой
        /// части; перед чтением они убираются, после правки ставятся заново.
        ///
        /// Если от правки длина изменилась, всё остаётся как было: за длиной
        /// описания следят ещё и размеры всех частей вокруг, и менять их
        /// ради одного бита не стоит.
        /// </summary>
        private static void Fix(byte[] moov, int at, int length)
        {
            byte[] clean = Unstuff(moov, at + 1, length - 1);

            int aspect;
            int spelt;
            int range;

            if (!Places(clean, out aspect, out spelt, out range)) return;

            bool changed = false;

            // Форма точки. Записана она либо номером из готового списка,
            // либо, когда в списке такой нет, двумя числами следом — и
            // квадратной делается в обоих случаях одинаково просто, ни
            // на байт не меняя длины.
            if (spelt >= 0)
            {
                if (Peek(clean, spelt, 16) != 1 || Peek(clean, spelt + 16, 16) != 1)
                {
                    Poke(clean, spelt, 16, 1);
                    Poke(clean, spelt + 16, 16, 1);
                    changed = true;
                }
            }
            else if (aspect >= 0 && Peek(clean, aspect, 8) != 1)
            {
                Poke(clean, aspect, 8, 1);
                changed = true;
            }

            if (range >= 0 && Peek(clean, range, 1) != 0)
            {
                Poke(clean, range, 1, 0);
                changed = true;
            }

            if (!changed) return;

            byte[] made = Stuff(clean);

            if (made.Length != length - 1)
            {
                Log.Write("chat", "кружок: описание оставлено как было — длина не сходится");
                return;
            }

            Buffer.BlockCopy(made, 0, moov, at + 1, made.Length);

            Log.Write("chat", "кружок: в описании потока объявлены квадратная точка"
                              + " и обычный диапазон");
        }

        /// <summary>Прочесть несколько битов подряд.</summary>
        private static long Peek(byte[] clean, int at, int count)
        {
            long made = 0;

            for (int step = 0; step < count; step++)
            {
                int which = (at + step) >> 3;
                made = (made << 1) | (uint)((clean[which] >> (7 - ((at + step) & 7))) & 1);
            }

            return made;
        }

        /// <summary>Записать их же обратно.</summary>
        private static void Poke(byte[] clean, int at, int count, long value)
        {
            for (int step = 0; step < count; step++)
            {
                int which = (at + step) >> 3;
                int mask = 1 << (7 - ((at + step) & 7));

                bool one = ((value >> (count - 1 - step)) & 1) != 0;

                if (one) clean[which] |= (byte)mask;
                else clean[which] &= (byte)~mask;
            }
        }

        /// <summary>Убрать лишние байты, поставленные при записи.</summary>
        private static byte[] Unstuff(byte[] data, int at, int length)
        {
            var made = new System.Collections.Generic.List<byte>(length);
            int zeros = 0;

            for (int step = 0; step < length; step++)
            {
                byte value = data[at + step];

                if (zeros == 2 && value == 3)
                {
                    zeros = 0;
                    continue;
                }

                made.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }

            return made.ToArray();
        }

        /// <summary>Поставить их обратно.</summary>
        private static byte[] Stuff(byte[] data)
        {
            var made = new System.Collections.Generic.List<byte>(data.Length + 8);
            int zeros = 0;

            foreach (byte value in data)
            {
                if (zeros >= 2 && value <= 3)
                {
                    made.Add(3);
                    zeros = 0;
                }

                made.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }

            return made.ToArray();
        }

        /// <summary>
        /// Где в описании последовательности лежат форма точки и диапазон
        /// яркости.
        ///
        /// Оба записаны битами вплотную, и до них приходится прочесть всё,
        /// что лежит впереди, — за один проход находим сразу оба. Место,
        /// которого в описании нет, отдаётся как −1: не объявленную форму и
        /// не объявленный диапазон и так считают обычными.
        ///
        /// Форма точки бывает записана двояко: номером из готового списка
        /// (`aspect`) или, когда в списке такой нет, парой чисел следом за
        /// ним (`spelt`). Второе — не редкость: отношения вроде 3:4 в
        /// списке отсутствуют.
        /// </summary>
        private static bool Places(byte[] clean, out int aspect, out int spelt, out int range)
        {
            aspect = -1;
            spelt = -1;
            range = -1;

            var bits = new Bits(clean);

            long profile = bits.Read(8);
            bits.Read(8);                       // ограничения
            bits.Read(8);                       // уровень
            bits.Number();                      // номер набора

            if (profile == 100 || profile == 110 || profile == 122 || profile == 244
                || profile == 44 || profile == 83 || profile == 86 || profile == 118
                || profile == 128 || profile == 138 || profile == 139 || profile == 134)
            {
                if (bits.Number() == 3) bits.Read(1);

                bits.Number();
                bits.Number();
                bits.Read(1);

                // Свои таблицы весов разбирать не станем: кодировщики
                // телефона их не пишут, а разбор вышел бы вдвое длиннее
                // всего остального.
                if (bits.Read(1) != 0) return false;
            }

            bits.Number();                      // разрядность счётчика кадров
            long order = bits.Number();

            if (order == 0)
            {
                bits.Number();
            }
            else if (order == 1)
            {
                bits.Read(1);
                bits.Signed();
                bits.Signed();

                long count = bits.Number();
                for (long step = 0; step < count; step++) bits.Signed();
            }

            bits.Number();                      // сколько кадров помнить
            bits.Read(1);
            bits.Number();                      // ширина в макроблоках
            bits.Number();                      // высота

            if (bits.Read(1) == 0) bits.Read(1);
            bits.Read(1);

            if (bits.Read(1) != 0)              // обрезка по краям
            {
                bits.Number();
                bits.Number();
                bits.Number();
                bits.Number();
            }

            if (bits.Read(1) == 0) return false;   // дополнительного описания нет

            if (bits.Read(1) != 0)                 // форма точки объявлена
            {
                aspect = bits.At;

                if (bits.Read(8) == 255)
                {
                    spelt = bits.At;

                    bits.Read(16);
                    bits.Read(16);
                }
            }

            if (bits.Read(1) != 0) bits.Read(1);   // выход за края
            if (bits.Read(1) == 0) return true;    // вид сигнала не объявлен

            bits.Read(3);                          // сам вид
            range = bits.At;                       // следующий бит и есть диапазон

            return true;
        }

        /// <summary>
        /// Чтение битами подряд. Числа в описании потока записаны не по
        /// байтам, а вплотную друг к другу, и иначе до них не добраться.
        /// </summary>
        private sealed class Bits
        {
            private readonly byte[] _data;
            private int _at;

            public Bits(byte[] data)
            {
                _data = data;
            }

            public int At
            {
                get { return _at; }
            }

            public long Read(int count)
            {
                long made = 0;

                for (int step = 0; step < count; step++)
                {
                    int which = _at >> 3;
                    if (which >= _data.Length) throw new FormatException("описание кончилось");

                    made = (made << 1) | (uint)((_data[which] >> (7 - (_at & 7))) & 1);
                    _at++;
                }

                return made;
            }

            /// <summary>
            /// Число переменной длины: сперва нули, затем единица, затем
            /// столько же значащих битов, сколько было нулей.
            /// </summary>
            public long Number()
            {
                int zeros = 0;

                while (Read(1) == 0)
                {
                    zeros++;
                    if (zeros > 32) throw new FormatException("число не кончается");
                }

                if (zeros == 0) return 0;

                return (1L << zeros) - 1 + Read(zeros);
            }

            /// <summary>То же, но со знаком: чётные — вправо, нечётные — влево.</summary>
            public long Signed()
            {
                long value = Number();

                return (value % 2) != 0 ? (value + 1) / 2 : -(value / 2);
            }
        }

        /// <summary>
        /// Объявление вида файла — своё, вместо написанного Windows.
        ///
        /// Windows пишет здесь «mp42». Всё же, что ходит через сервер MAX —
        /// и кружок, который он присылает обратно, — объявлено «isom» с
        /// набором, обычным для телефонной записи. По этому слову ролик
        /// начинают разбирать раньше, чем доберутся до дорожек, и спорить
        /// тут не о чем: пишем то же, что и все.
        /// </summary>
        private static byte[] Brand()
        {
            var made = new byte[32];

            Write32(made, 0, made.Length);

            Letters(made, 4, "ftyp");
            Letters(made, 8, "isom");
            Write32(made, 12, 512);

            Letters(made, 16, "isom");
            Letters(made, 20, "iso2");
            Letters(made, 24, "avc1");
            Letters(made, 28, "mp41");

            return made;
        }

        private static void Letters(byte[] data, int at, string word)
        {
            for (int letter = 0; letter < 4; letter++)
            {
                data[at + letter] = (byte)word[letter];
            }
        }

        /// <summary>
        /// Сдвинуть все расстояния до кусков. Лежат они в частях stco и co64
        /// — той же таблицей, только числа в первой по четыре байта, а во
        /// второй по восемь.
        /// </summary>
        private static void Shift(byte[] described, int at, int length, long delta)
        {
            foreach (int table in Find(described, at, length, "stco"))
            {
                long count = Read32(described, table + 4);

                for (long entry = 0; entry < count; entry++)
                {
                    int where = table + 8 + (int)entry * 4;
                    if (where + 4 > described.Length) break;

                    Write32(described, where, Read32(described, where) + delta);
                }
            }

            foreach (int table in Find(described, at, length, "co64"))
            {
                long count = Read32(described, table + 4);

                for (long entry = 0; entry < count; entry++)
                {
                    int where = table + 8 + (int)entry * 8;
                    if (where + 8 > described.Length) break;

                    long moved = Read64(described, where) + delta;

                    Write32(described, where, moved >> 32);
                    Write32(described, where + 4, moved & 0xFFFFFFFF);
                }
            }
        }

        private static void Write32(byte[] data, int at, long value)
        {
            data[at] = (byte)((value >> 24) & 0xFF);
            data[at + 1] = (byte)((value >> 16) & 0xFF);
            data[at + 2] = (byte)((value >> 8) & 0xFF);
            data[at + 3] = (byte)(value & 0xFF);
        }

        /// <summary>
        /// Виды файла из ftyp: заявленный и те, с которыми он совместим.
        /// Принимающая сторона по ним решает, чем разбирать присланное.
        /// </summary>
        private static string Brands(byte[] mp4, int at, int end)
        {
            if (at + 8 > end || at + 8 > mp4.Length) return "не записан";

            var said = new StringBuilder(Name(mp4, at));

            for (int brand = at + 8; brand + 4 <= end && brand + 4 <= mp4.Length; brand += 4)
            {
                said.Append(',').Append(Name(mp4, brand));
            }

            return said.ToString();
        }

        /// <summary>
        /// Что записано в описании: длительность всего ролика и каждая
        /// дорожка по отдельности.
        /// </summary>
        private static string Movie(byte[] mp4, int at, int end)
        {
            var said = new StringBuilder();

            int from;
            int to;

            if (Inside(mp4, at, end, "mvhd", out from, out to))
            {
                said.Append("ролик ").Append(Span(mp4, from)).Append("; ");
            }

            int number = 0;
            int box = at;

            while (box + 8 <= end)
            {
                long size = Read32(mp4, box);
                if (size < 8 || box + size > end) break;

                if (Name(mp4, box + 4) == "trak")
                {
                    said.Append(Track(mp4, box + 8, (int)(box + size), ++number)).Append("; ");
                }

                box += (int)size;
            }

            if (number == 0) said.Append("дорожек нет");

            return said.ToString();
        }

        /// <summary>
        /// Одна дорожка: чем занята, каким размером записана, чем сжата,
        /// сколько в ней кусков и с чего начинается первый.
        ///
        /// Размер в заголовке дорожки — это размер показа, и с настоящим он
        /// совпадает не всегда: сжать могли одним, а показывать велеть
        /// другим. Настоящий стоит в записи о сжатии, поэтому выводятся оба.
        /// </summary>
        private static string Track(byte[] mp4, int at, int end, int number)
        {
            var said = new StringBuilder();

            said.Append("дорожка ").Append(number).Append(' ');

            int from;
            int to;

            said.Append(Deep(mp4, at, end, "mdia/hdlr", out from, out to) && from + 12 <= to
                            ? Name(mp4, from + 8)
                            : "????")
                .Append(' ');

            if (Inside(mp4, at, end, "tkhd", out from, out to))
            {
                // У первого извода время записано четырьмя байтами, у
                // второго — восемью; всё, что за ним, съезжает на двенадцать.
                int shift = mp4[from] == 1 ? 12 : 0;

                long width = Read32(mp4, from + 76 + shift) >> 16;
                long height = Read32(mp4, from + 80 + shift) >> 16;

                if (width != 0 || height != 0)
                {
                    said.Append("показ ").Append(width).Append('×').Append(height).Append(' ');
                }
            }

            long scale = 0;

            if (Deep(mp4, at, end, "mdia/mdhd", out from, out to))
            {
                scale = Scale(mp4, from);
                said.Append(Span(mp4, from)).Append(' ');
            }

            if (Deep(mp4, at, end, "mdia/minf/stbl/stsd", out from, out to))
            {
                said.Append(Sample(mp4, from + 8, to)).Append(' ');
            }

            if (Deep(mp4, at, end, "mdia/minf/stbl/stsz", out from, out to))
            {
                said.Append("кусков ").Append(Read32(mp4, from + 8)).Append(' ');
            }

            if (Deep(mp4, at, end, "mdia/minf/stbl/stts", out from, out to))
            {
                said.Append(Pace(mp4, from, to, scale)).Append(' ');
            }

            long first = -1;

            if (Deep(mp4, at, end, "mdia/minf/stbl/stco", out from, out to) && from + 12 <= to)
            {
                first = Read32(mp4, from + 8);
            }
            else if (Deep(mp4, at, end, "mdia/minf/stbl/co64", out from, out to) && from + 16 <= to)
            {
                first = Read64(mp4, from + 8);
            }

            if (first >= 0) said.Append(Start(mp4, (int)first));

            return said.ToString();
        }

        /// <summary>
        /// Длительность из заголовка — ролика или дорожки. Записана она
        /// парой чисел: делений в секунде и самих делений.
        /// </summary>
        private static string Span(byte[] mp4, int at)
        {
            long scale = Scale(mp4, at);
            long ticks = mp4[at] == 1 ? Read64(mp4, at + 24) : Read32(mp4, at + 16);

            if (scale <= 0) return "длительности нет";

            return (ticks / (double)scale).ToString("0.00") + " с";
        }

        /// <summary>Делений в секунде — ими меряется время внутри дорожки.</summary>
        private static long Scale(byte[] mp4, int at)
        {
            return Read32(mp4, at + 12 + (mp4[at] == 1 ? 8 : 0));
        }

        /// <summary>
        /// Ровно ли шли куски.
        ///
        /// В stts у каждого записано, сколько он длится. У снятого без
        /// заминок все они одинаковы: тридцать кадров в секунду — это
        /// тридцать три миллисекунды подряд. А если телефон не поспевал,
        /// между кадрами встают провалы в полсекунды, и ролик, заявленный
        /// тридцатью кадрами, идёт вдвое реже. Заливку такого сервер не
        /// принимает.
        /// </summary>
        private static string Pace(byte[] mp4, int at, int end, long scale)
        {
            long rows = Read32(mp4, at + 4);
            long pieces = 0;
            long span = 0;
            long longest = 0;

            for (long row = 0; row < rows; row++)
            {
                int where = at + 8 + (int)row * 8;
                if (where + 8 > end) break;

                long count = Read32(mp4, where);
                long step = Read32(mp4, where + 4);

                pieces += count;
                span += count * step;

                if (step > longest) longest = step;
            }

            if (scale <= 0 || span <= 0 || pieces <= 0) return "темпа не сосчитать";

            var said = new StringBuilder();

            said.Append("темп ")
                .Append((pieces * scale / (double)span).ToString("0.0"))
                .Append("/с");

            // Провалом считаем кусок вдвое длиннее среднего: у ровной записи
            // такого не бывает вовсе, а у сорванной он бросается в глаза.
            if (longest * pieces > span * 2)
            {
                said.Append(", провал до ").Append(longest * 1000 / scale).Append(" мс");
            }

            return said.ToString();
        }

        /// <summary>
        /// Чем сжата дорожка и с какими числами. У картинки к этому идёт
        /// настоящий размер кадра и извод H.264 — официальный клиент пишет
        /// кружок высоким изводом третьего уровня.
        /// </summary>
        private static string Sample(byte[] mp4, int at, int end)
        {
            if (at + 16 > end) return "сжатие неизвестно";

            long size = Read32(mp4, at);
            if (size < 16 || at + size > end) return "сжатие неизвестно";

            string kind = Name(mp4, at + 4);
            var said = new StringBuilder(kind);

            int stop = (int)(at + size);

            if (kind == "avc1" || kind == "mp4v" || kind == "hvc1")
            {
                said.Append(' ')
                    .Append((Read32(mp4, at + 32) >> 16) & 0xFFFF)
                    .Append('×')
                    .Append(Read32(mp4, at + 32) & 0xFFFF);

                int from;
                int to;

                // Запись о картинке кончается общей частью, а за ней, с
                // восемьдесят шестого байта, лежат вложенные части — среди
                // них avcC, которым кодировщик объявляет извод и уровень.
                if (Inside(mp4, at + 86, stop, "avcC", out from, out to) && from + 4 <= to)
                {
                    said.Append(" извод ").Append(mp4[from + 1])
                        .Append(" уровень ").Append(mp4[from + 3])
                        .Append(' ').Append(Shape(mp4, from, to));
                }
                else
                {
                    said.Append(" извод не объявлен");
                }

                // Форма точки. Обычно её не пишут вовсе — точка квадратная,
                // и кадр показывают как записан. А вот сжатый в квадрат
                // прямоугольник кодировщик может пометить неквадратной
                // точкой: мол, показывать это надо обратно растянутым. Тогда
                // квадратный по числам кружок квадратным быть перестаёт.
                if (Inside(mp4, at + 86, stop, "pasp", out from, out to) && from + 8 <= to)
                {
                    said.Append(" точка ").Append(Read32(mp4, from))
                        .Append(':').Append(Read32(mp4, from + 4));
                }
            }
            else
            {
                said.Append(' ').Append(Read32(mp4, at + 32) >> 16)
                    .Append(" Гц, каналов ").Append((Read32(mp4, at + 24) >> 16) & 0xFFFF);
            }

            return said.ToString();
        }

        /// <summary>
        /// Форма точки, объявленная в описании потока.
        ///
        /// Обычно она квадратная. Неквадратной её объявляет запись, сделанная
        /// нарочно вытянутой, — и по этой строке видно, послушалась ли
        /// камера такого заказа и снялось ли объявление перед заливкой.
        /// </summary>
        private static string Shape(byte[] mp4, int at, int end)
        {
            try
            {
                if (at + 8 > end) return "точка неизвестна";

                int length = (mp4[at + 6] << 8) | mp4[at + 7];
                if ((mp4[at + 5] & 0x1F) == 0 || at + 8 + length > end) return "точка неизвестна";

                byte[] clean = Unstuff(mp4, at + 9, length - 1);

                int aspect;
                int spelt;
                int range;

                if (!Places(clean, out aspect, out spelt, out range)) return "точка не объявлена";

                if (spelt >= 0)
                {
                    return "точка " + Peek(clean, spelt, 16) + ":" + Peek(clean, spelt + 16, 16);
                }

                if (aspect < 0) return "точка не объявлена";

                long which = Peek(clean, aspect, 8);
                return which == 1 ? "точка квадратная" : "точка вида " + which;
            }
            catch (Exception)
            {
                return "точку не разобрать";
            }
        }

        /// <summary>
        /// Начало первого куска дорожки.
        ///
        /// Кадры H.264 лежат в файле каждый со своей длиной впереди. Тот же
        /// кодировщик умеет писать их и потоком — разделяя тремя нулями и
        /// единицей, — и такую запись, попавшую в файл, принимающая сторона
        /// не разбирает вовсе. По первым байтам видно, какая она.
        /// </summary>
        private static string Start(byte[] mp4, int at)
        {
            if (at < 0 || at + 8 > mp4.Length) return "первого куска нет";

            var said = new StringBuilder("начало на ");
            said.Append(at).Append(':');

            for (int byteAt = at; byteAt < at + 8; byteAt++)
            {
                said.Append(' ').Append(mp4[byteAt].ToString("X2"));
            }

            bool stream = mp4[at] == 0 && mp4[at + 1] == 0
                          && (mp4[at + 2] == 1 || (mp4[at + 2] == 0 && mp4[at + 3] == 1));

            said.Append(stream ? " — потоком" : " — длиной впереди");

            return said.ToString();
        }

        /// <summary>Вложенная часть с таким именем — где её содержимое.</summary>
        private static bool Inside(byte[] mp4, int at, int end, string wanted,
                                   out int from, out int to)
        {
            from = 0;
            to = 0;

            if (end > mp4.Length) end = mp4.Length;

            while (at + 8 <= end)
            {
                long size = Read32(mp4, at);
                if (size < 8 || at + size > end) return false;

                if (Name(mp4, at + 4) == wanted)
                {
                    from = at + 8;
                    to = (int)(at + size);
                    return true;
                }

                at += (int)size;
            }

            return false;
        }

        /// <summary>То же, но по дороге из нескольких имён через косую черту.</summary>
        private static bool Deep(byte[] mp4, int at, int end, string path,
                                 out int from, out int to)
        {
            from = at;
            to = end;

            foreach (string step in path.Split('/'))
            {
                int inner;
                int stop;

                if (!Inside(mp4, from, to, step, out inner, out stop)) return false;

                from = inner;
                to = stop;
            }

            return true;
        }

        /// <summary>
        /// Все части с таким именем на любой глубине. Части вложены друг в
        /// друга, и tkhd лежит внутри trak, а тот — внутри moov.
        /// </summary>
        private static System.Collections.Generic.List<int> Find(byte[] mp4, int at, int length,
                                                                 string wanted)
        {
            var found = new System.Collections.Generic.List<int>();

            int end = at + length;
            if (end > mp4.Length) end = mp4.Length;

            while (at + 8 <= end)
            {
                long size = Read32(mp4, at);
                string name = Name(mp4, at + 4);

                if (size < 8 || at + size > end) break;

                if (name == wanted)
                {
                    found.Add(at + 8);
                }
                else if (name == "trak" || name == "mdia" || name == "minf" || name == "stbl")
                {
                    found.AddRange(Find(mp4, at + 8, (int)size - 8, wanted));
                }

                at += (int)size;
            }

            return found;
        }

        private static string Name(byte[] data, int at)
        {
            var made = new char[4];

            for (int i = 0; i < 4; i++)
            {
                char letter = (char)data[at + i];
                made[i] = letter >= ' ' && letter < 127 ? letter : '?';
            }

            return new string(made);
        }

        private static long Read32(byte[] data, int at)
        {
            if (at + 4 > data.Length) return 0;

            return ((long)data[at] << 24) | ((long)data[at + 1] << 16)
                   | ((long)data[at + 2] << 8) | data[at + 3];
        }

        private static long Read64(byte[] data, int at)
        {
            return (Read32(data, at) << 32) | Read32(data, at + 4);
        }
    }
}
