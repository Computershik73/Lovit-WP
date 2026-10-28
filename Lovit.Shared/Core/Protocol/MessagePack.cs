using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lovit.Core.Protocol
{
    /// <summary>
    /// MessagePack — тот же формат, которым разговаривает MAX.
    /// Порт src/Core/Protocol/MAXMessagePack.m из версии для iOS.
    ///
    /// Своя реализация, а не библиотека, по двум причинам: нужен ровно этот
    /// набор типов (карта, массив, строка, двоичные данные, число, булево,
    /// nil), и всё это должно собираться под Windows Phone 8.1, где выбор
    /// пакетов давно заморожен.
    ///
    /// Соответствие типов: IDictionary&lt;string, object&gt; ↔ map,
    /// IList ↔ array, string ↔ str, byte[] ↔ bin, long/double/bool ↔ число
    /// и булево, null ↔ nil.
    /// </summary>
    public static class MessagePack
    {
        public static byte[] Pack(object value)
        {
            using (var stream = new MemoryStream())
            {
                Write(stream, value);
                return stream.ToArray();
            }
        }

        public static object Unpack(byte[] data)
        {
            int offset = 0;
            return Read(data, ref offset);
        }

        #region Запись

        private static void Write(Stream stream, object value)
        {
            if (value == null) { stream.WriteByte(0xC0); return; }

            var boolean = value as bool?;
            if (boolean.HasValue) { stream.WriteByte((byte)(boolean.Value ? 0xC3 : 0xC2)); return; }

            var text = value as string;
            if (text != null) { WriteString(stream, text); return; }

            var bytes = value as byte[];
            if (bytes != null) { WriteBinary(stream, bytes); return; }

            var map = value as IDictionary<string, object>;
            if (map != null) { WriteMap(stream, map); return; }

            var list = value as IList<object>;
            if (list != null) { WriteArray(stream, list); return; }

            if (value is double || value is float)
            {
                stream.WriteByte(0xCB);
                WriteBigEndian(stream, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value)), 8);
                return;
            }

            WriteInteger(stream, Convert.ToInt64(value));
        }

        private static void WriteInteger(Stream stream, long value)
        {
            if (value >= 0)
            {
                if (value < 0x80) { stream.WriteByte((byte)value); return; }
                if (value <= 0xFF) { stream.WriteByte(0xCC); stream.WriteByte((byte)value); return; }
                if (value <= 0xFFFF) { stream.WriteByte(0xCD); WriteBigEndian(stream, value, 2); return; }
                if (value <= 0xFFFFFFFF) { stream.WriteByte(0xCE); WriteBigEndian(stream, value, 4); return; }
                stream.WriteByte(0xCF); WriteBigEndian(stream, value, 8);
                return;
            }

            if (value >= -32) { stream.WriteByte((byte)(0xE0 | (value + 32))); return; }
            if (value >= sbyte.MinValue) { stream.WriteByte(0xD0); stream.WriteByte((byte)(sbyte)value); return; }
            if (value >= short.MinValue) { stream.WriteByte(0xD1); WriteBigEndian(stream, value, 2); return; }
            if (value >= int.MinValue) { stream.WriteByte(0xD2); WriteBigEndian(stream, value, 4); return; }

            stream.WriteByte(0xD3);
            WriteBigEndian(stream, value, 8);
        }

        private static void WriteString(Stream stream, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);

            if (utf8.Length < 32) stream.WriteByte((byte)(0xA0 | utf8.Length));
            else if (utf8.Length <= 0xFF) { stream.WriteByte(0xD9); stream.WriteByte((byte)utf8.Length); }
            else if (utf8.Length <= 0xFFFF) { stream.WriteByte(0xDA); WriteBigEndian(stream, utf8.Length, 2); }
            else { stream.WriteByte(0xDB); WriteBigEndian(stream, utf8.Length, 4); }

            stream.Write(utf8, 0, utf8.Length);
        }

        private static void WriteBinary(Stream stream, byte[] bytes)
        {
            if (bytes.Length <= 0xFF) { stream.WriteByte(0xC4); stream.WriteByte((byte)bytes.Length); }
            else if (bytes.Length <= 0xFFFF) { stream.WriteByte(0xC5); WriteBigEndian(stream, bytes.Length, 2); }
            else { stream.WriteByte(0xC6); WriteBigEndian(stream, bytes.Length, 4); }

            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteMap(Stream stream, IDictionary<string, object> map)
        {
            if (map.Count < 16) stream.WriteByte((byte)(0x80 | map.Count));
            else if (map.Count <= 0xFFFF) { stream.WriteByte(0xDE); WriteBigEndian(stream, map.Count, 2); }
            else { stream.WriteByte(0xDF); WriteBigEndian(stream, map.Count, 4); }

            foreach (var pair in map)
            {
                WriteString(stream, pair.Key);
                Write(stream, pair.Value);
            }
        }

        private static void WriteArray(Stream stream, IList<object> list)
        {
            if (list.Count < 16) stream.WriteByte((byte)(0x90 | list.Count));
            else if (list.Count <= 0xFFFF) { stream.WriteByte(0xDC); WriteBigEndian(stream, list.Count, 2); }
            else { stream.WriteByte(0xDD); WriteBigEndian(stream, list.Count, 4); }

            foreach (object item in list) Write(stream, item);
        }

        private static void WriteBigEndian(Stream stream, long value, int bytes)
        {
            for (int i = bytes - 1; i >= 0; i--) stream.WriteByte((byte)((value >> (i * 8)) & 0xFF));
        }

        #endregion

        #region Чтение

        private static object Read(byte[] data, ref int offset)
        {
            byte marker = data[offset++];

            if (marker <= 0x7F) return (long)marker;                       // положительный fixint
            if (marker >= 0xE0) return (long)(sbyte)marker;                // отрицательный fixint
            if ((marker & 0xF0) == 0x80) return ReadMap(data, ref offset, marker & 0x0F);
            if ((marker & 0xF0) == 0x90) return ReadArray(data, ref offset, marker & 0x0F);
            if ((marker & 0xE0) == 0xA0) return ReadString(data, ref offset, marker & 0x1F);

            switch (marker)
            {
                case 0xC0: return null;
                case 0xC2: return false;
                case 0xC3: return true;

                case 0xC4: return ReadBinary(data, ref offset, (int)ReadUnsigned(data, ref offset, 1));
                case 0xC5: return ReadBinary(data, ref offset, (int)ReadUnsigned(data, ref offset, 2));
                case 0xC6: return ReadBinary(data, ref offset, (int)ReadUnsigned(data, ref offset, 4));

                case 0xCA: return BitConverter.ToSingle(Reverse(data, offset, 4, ref offset), 0);
                case 0xCB: return BitConverter.Int64BitsToDouble(ReadSigned(data, ref offset, 8));

                case 0xCC: return ReadUnsigned(data, ref offset, 1);
                case 0xCD: return ReadUnsigned(data, ref offset, 2);
                case 0xCE: return ReadUnsigned(data, ref offset, 4);
                case 0xCF: return ReadUnsigned(data, ref offset, 8);

                case 0xD0: return (long)(sbyte)data[offset++];
                case 0xD1: return ReadSigned(data, ref offset, 2);
                case 0xD2: return ReadSigned(data, ref offset, 4);
                case 0xD3: return ReadSigned(data, ref offset, 8);

                case 0xD9: return ReadString(data, ref offset, (int)ReadUnsigned(data, ref offset, 1));
                case 0xDA: return ReadString(data, ref offset, (int)ReadUnsigned(data, ref offset, 2));
                case 0xDB: return ReadString(data, ref offset, (int)ReadUnsigned(data, ref offset, 4));

                case 0xDC: return ReadArray(data, ref offset, (int)ReadUnsigned(data, ref offset, 2));
                case 0xDD: return ReadArray(data, ref offset, (int)ReadUnsigned(data, ref offset, 4));
                case 0xDE: return ReadMap(data, ref offset, (int)ReadUnsigned(data, ref offset, 2));
                case 0xDF: return ReadMap(data, ref offset, (int)ReadUnsigned(data, ref offset, 4));
            }

            // Расширенные типы (0xC7…0xD8) сервер не присылает; пропускать их
            // вслепую нельзя — дальше пойдёт мусор, лучше сказать об этом сразу.
            throw new FormatException("MessagePack: неизвестный маркер 0x" + marker.ToString("X2"));
        }

        private static Dictionary<string, object> ReadMap(byte[] data, ref int offset, int count)
        {
            var map = new Dictionary<string, object>(count);
            for (int i = 0; i < count; i++)
            {
                object key = Read(data, ref offset);
                object value = Read(data, ref offset);
                map[key == null ? "" : key.ToString()] = value;
            }
            return map;
        }

        private static List<object> ReadArray(byte[] data, ref int offset, int count)
        {
            var list = new List<object>(count);
            for (int i = 0; i < count; i++) list.Add(Read(data, ref offset));
            return list;
        }

        private static string ReadString(byte[] data, ref int offset, int length)
        {
            string text = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return text;
        }

        private static byte[] ReadBinary(byte[] data, ref int offset, int length)
        {
            var bytes = new byte[length];
            Buffer.BlockCopy(data, offset, bytes, 0, length);
            offset += length;
            return bytes;
        }

        private static long ReadUnsigned(byte[] data, ref int offset, int bytes)
        {
            long value = 0;
            for (int i = 0; i < bytes; i++) value = (value << 8) | data[offset++];
            return value;
        }

        private static long ReadSigned(byte[] data, ref int offset, int bytes)
        {
            long value = ReadUnsigned(data, ref offset, bytes);
            int bits = bytes * 8;
            if (bits < 64 && (value & (1L << (bits - 1))) != 0) value -= 1L << bits;
            return value;
        }

        private static byte[] Reverse(byte[] data, int start, int length, ref int offset)
        {
            var slice = new byte[length];
            for (int i = 0; i < length; i++) slice[i] = data[start + length - 1 - i];
            offset = start + length;
            return slice;
        }

        #endregion

        #region Удобства для разбора ответов

        public static IDictionary<string, object> AsMap(object value)
        {
            return value as IDictionary<string, object>;
        }

        public static IList<object> AsList(object value)
        {
            return value as IList<object>;
        }

        public static string String(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? value.ToString();
        }

        public static long Long(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return 0;

            return ToLong(value);
        }

        /// <summary>
        /// Число из чего угодно. Числа в разборе приходят разной шириной, а в
        /// списках лежат без имён — там без такого не обойтись.
        /// </summary>
        public static long ToLong(object value)
        {
            if (value == null) return 0;

            if (value is long) return (long)value;
            if (value is int) return (int)value;
            if (value is double) return (long)(double)value;

            long parsed;
            return long.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }

        public static bool Bool(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            return Long(map, key) != 0;
        }

        public static IDictionary<string, object> Map(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return null;
            return value as IDictionary<string, object>;
        }

        public static IList<object> List(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return null;
            return value as IList<object>;
        }

        #endregion
    }
}
