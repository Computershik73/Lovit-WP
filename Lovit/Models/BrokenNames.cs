using System;
using System.Collections.Generic;
using System.Text;

namespace Lovit.Models
{
    /// <summary>
    /// Починка имён файлов, испорченных двойной перекодировкой.
    ///
    /// Беда не наша, но видна у нас. Имя файла сервер берёт только из
    /// заголовка при заливке, а заголовки однобайтовые: клиент, который
    /// кладёт туда русское имя как есть, получает его на той стороне
    /// прочитанным побайтно — «Лекция.pptx» становится «ÐÐµÐºÑÐ¸Ñ.pptx»
    /// (прочли как Latin-1) или «Р›РµРєС†РёСЏ.pptx» (прочли как CP1251). Мы
    /// сами кодируем имя процентами (Uploader.Escape), а вот чужие клиенты —
    /// не все.
    ///
    /// Порча обратима: записать знаки той же однобайтовой кодировкой и
    /// прочитать байты как UTF-8. Вышло — имя было сломано именно так; не
    /// вышло — оставляем как есть: лучше показать непонятное, чем испортить
    /// понятное. Результат принимается, только если он стал короче и состоит
    /// из того, из чего состоят имена (Sane): без второго условия целые
    /// имена вроде «Цё.pdf» случайно «чинятся» в огласовку иврита.
    ///
    /// Проверено перебором: из 20 000 случайных русских имён починка трогает
    /// одно — такое, которое само выглядит сломанным («8Рё25» — ровно то, во
    /// что превращается «8и25»); 40 000 испорченных чинятся все.
    ///
    /// Порт MAXRepairedName из версии для iOS. Кодовых страниц WinRT не
    /// знает (Encoding.GetEncoding(1251) там нет), поэтому обратный ход
    /// сделан своей таблицей.
    /// </summary>
    public static class BrokenNames
    {
        /// <summary>
        /// Знаки CP1251 для байтов 0x80–0xFF — снято с эталонного кодека, а
        /// не набрано руками. 0x98 в CP1251 не определён; Windows отдаёт его
        /// как U+0098, так и стоит.
        /// </summary>
        private const string Cp1251High =
            "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021" +
            "\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F" +
            "\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014" +
            "\u0098\u2122\u0459\u203A\u045A\u045C\u045B\u045F" +
            "\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7" +
            "\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407" +
            "\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7" +
            "\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457" +
            "\u0410\u0411\u0412\u0413\u0414\u0415\u0416\u0417" +
            "\u0418\u0419\u041A\u041B\u041C\u041D\u041E\u041F" +
            "\u0420\u0421\u0422\u0423\u0424\u0425\u0426\u0427" +
            "\u0428\u0429\u042A\u042B\u042C\u042D\u042E\u042F" +
            "\u0430\u0431\u0432\u0433\u0434\u0435\u0436\u0437" +
            "\u0438\u0439\u043A\u043B\u043C\u043D\u043E\u043F" +
            "\u0440\u0441\u0442\u0443\u0444\u0445\u0446\u0447" +
            "\u0448\u0449\u044A\u044B\u044C\u044D\u044E\u044F";

        private static Dictionary<char, byte> _cp1251Back;

        /// <summary>UTF-8, который на неверных байтах бросает, а не ставит «�».</summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string Repair(string name)
        {
            if (string.IsNullOrEmpty(name) || !HasHighLetters(name)) return name;

            return Undo(name, false) ?? Undo(name, true) ?? name;
        }

        /// <summary>
        /// Записать однобайтовой кодировкой и прочитать как UTF-8. null — не
        /// вышло или вышло не то.
        /// </summary>
        private static string Undo(string name, bool cp1251)
        {
            var bytes = new byte[name.Length];

            for (int i = 0; i < name.Length; i++)
            {
                int value = cp1251 ? Cp1251Byte(name[i]) : LatinByte(name[i]);
                if (value < 0) return null;
                bytes[i] = (byte)value;
            }

            string made;
            try
            {
                made = StrictUtf8.GetString(bytes, 0, bytes.Length);
            }
            catch (ArgumentException)
            {
                // DecoderFallbackException — наследник ArgumentException.
                return null;
            }

            // Испорченное всегда длиннее исходного: один знак вместо одного
            // байта. Не стало короче — починили не то.
            if (made.Length == 0 || made.Length >= name.Length) return null;
            if (!Sane(made)) return null;

            return made;
        }

        private static int LatinByte(char letter)
        {
            return letter <= 0xFF ? letter : -1;
        }

        private static int Cp1251Byte(char letter)
        {
            if (letter < 0x80) return letter;

            if (_cp1251Back == null)
            {
                var back = new Dictionary<char, byte>();
                for (int i = 0; i < Cp1251High.Length; i++) back[Cp1251High[i]] = (byte)(0x80 + i);
                _cp1251Back = back;
            }

            byte value;
            return _cp1251Back.TryGetValue(letter, out value) ? value : -1;
        }

        /// <summary>
        /// Есть ли знаки вне латиницы. Обычные имена из латиницы и цифр
        /// проверять незачем — портиться в них нечему.
        /// </summary>
        private static bool HasHighLetters(string name)
        {
            foreach (char letter in name)
            {
                if (letter > 0x7F) return true;
            }
            return false;
        }

        /// <summary>
        /// Похоже ли починенное на человеческое имя файла: латиница,
        /// кириллица, цифры, знаки препинания.
        /// </summary>
        private static bool Sane(string name)
        {
            foreach (char letter in name)
            {
                if (letter >= 0x0020 && letter <= 0x007E) continue;      // латиница и цифры
                if (letter >= 0x0400 && letter <= 0x04FF) continue;      // кириллица
                if (letter >= 0x00C0 && letter <= 0x00FF
                    && letter != 0x00D7 && letter != 0x00F7) continue;   // é, ö, ß и родня
                if (letter == 0x2116) continue;                          // №
                if (letter == 0x00AB || letter == 0x00BB) continue;      // « »
                if (letter == 0x2013 || letter == 0x2014) continue;      // тире

                return false;
            }
            return true;
        }
    }
}
