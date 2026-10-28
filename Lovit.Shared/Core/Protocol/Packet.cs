using System;
using System.Collections.Generic;

namespace Lovit.Core.Protocol
{
    /// <summary>
    /// Пакет протокола MAX. Порт src/Core/Protocol/MAXPacket.m.
    ///
    /// Заголовок — ровно десять байт:
    ///   [0]     версия протокола, всегда 10
    ///   [1]     тип команды: у клиента 0, у ответа 1, у ошибки 3
    ///   [2..3]  порядковый номер запроса, uint16 BE
    ///   [4..5]  код операции, uint16 BE
    ///   [6]     множитель сжатия
    ///   [7..9]  длина полезной нагрузки, 24 бита BE
    ///
    /// Байт [6] — не идентификатор формата, а подсказка получателю, во сколько
    /// раз данные распухнут при распаковке: 0 означает «без сжатия».
    /// </summary>
    public class Packet
    {
        public const int HeaderSize = 10;

        /// <summary>Больше этого не распаковываем: защита от кривой длины.</summary>
        private const int MaxDecompressedSize = 32 * 1024 * 1024;

        public int Cmd { get; set; }
        public int Seq { get; set; }
        public int Opcode { get; set; }
        public object Payload { get; set; }

        public bool IsOk { get { return Cmd == 1; } }
        public bool IsError { get { return Cmd == 3; } }
        public bool IsPush { get { return Cmd == 0; } }

        public IDictionary<string, object> Map
        {
            get { return Payload as IDictionary<string, object>; }
        }

        public static byte[] Pack(int opcode, IDictionary<string, object> payload, int seq)
        {
            byte[] body = MessagePack.Pack(payload ?? new Dictionary<string, object>());

            var frame = new byte[HeaderSize + body.Length];
            frame[0] = 10;
            frame[1] = 0;
            frame[2] = (byte)((seq >> 8) & 0xFF);
            frame[3] = (byte)(seq & 0xFF);
            frame[4] = (byte)((opcode >> 8) & 0xFF);
            frame[5] = (byte)(opcode & 0xFF);
            frame[6] = 0;                       // исходящее не сжимаем
            frame[7] = (byte)((body.Length >> 16) & 0xFF);
            frame[8] = (byte)((body.Length >> 8) & 0xFF);
            frame[9] = (byte)(body.Length & 0xFF);

            Buffer.BlockCopy(body, 0, frame, HeaderSize, body.Length);
            return frame;
        }

        /// <summary>
        /// Длина полезной нагрузки по заголовку. Нужна, чтобы понять, сколько
        /// ещё байт ждать из сокета.
        /// </summary>
        public static int BodyLength(byte[] header)
        {
            return (header[7] << 16) | (header[8] << 8) | header[9];
        }

        public static Packet Unpack(byte[] header, byte[] body)
        {
            var packet = new Packet
            {
                Cmd = header[1],
                Seq = (header[2] << 8) | header[3],
                Opcode = (header[4] << 8) | header[5]
            };

            if (body.Length == 0) return packet;

            packet.Payload = ReadBody(body, header[6]);
            return packet;
        }

        /// <summary>
        /// Разобрать содержимое, пробуя способы по очереди.
        ///
        /// Байт сжатия в заголовке — подсказка, а не описание способа: в нём
        /// лежит множитель, во сколько раз данные распухнут, и о самом виде
        /// упаковки он говорит только «ноль — как есть, иначе сжато».
        /// Что именно значит «сжато», зависит от того, чем упаковывал
        /// сервер, а он не один и не всегда одинаков.
        ///
        /// Отсюда и порядок: сперва самое вероятное, дальше — соседние
        /// толкования тех же байтов. Годным считается только то, что
        /// разобралось целиком; наполовину разобранное — не ответ.
        ///
        /// Так у людей и не входило в аккаунт: ответ на вход приходил
        /// сжатым — а сжимает сервер только крупные, оттого и «у некоторых»,
        /// — первое же толкование не подходило, и пакет отбрасывался целиком.
        /// </summary>
        private static object ReadBody(byte[] body, byte ratio)
        {
            string first = null;

            bool ok;

            if (ratio != 0)
            {
                object made = TryUnpack(Inflate(body, 0), ref first, "сжатое", out ok);
                if (ok) return made;

                // Тот же блок, но с длиной впереди: некоторые упаковщики
                // пишут перед сжатым его будущий размер четырьмя байтами.
                if (body.Length > 4)
                {
                    made = TryUnpack(Inflate(body, 4), ref first, "сжатое с длиной впереди", out ok);
                    if (ok) return made;
                }

                // А бывает, что множитель проставлен, но сжатия нет.
                made = TryUnpack(body, ref first, "несжатое, хоть и помечено", out ok);
                if (ok) return made;
            }
            else
            {
                object made = TryUnpack(body, ref first, "как есть", out ok);
                if (ok) return made;

                made = TryUnpack(Inflate(body, 0), ref first, "сжатое без пометки", out ok);
                if (ok) return made;
            }

            throw new FormatException("содержимое не разобралось ни одним способом; первая беда — "
                                      + (first ?? "нет"));
        }

        /// <summary>
        /// Распаковать, пропустив сколько-то байт впереди. Пусто в ответе
        /// означает «не вышло» — разбирать его дальше незачем.
        /// </summary>
        private static byte[] Inflate(byte[] body, int skip)
        {
            if (skip >= body.Length) return new byte[0];

            var slice = body;

            if (skip > 0)
            {
                slice = new byte[body.Length - skip];
                Buffer.BlockCopy(body, skip, slice, 0, slice.Length);
            }

            byte[] raw = Lz4.Decompress(slice, MaxDecompressedSize);

            // Распаковка, оборвавшаяся на полпути, отдаёт половину. Такой
            // ответ негоден: пусть его отвергнет разбор, а не примет.
            return Lz4.LastTrouble == null ? raw : new byte[0];
        }

        /// <summary>
        /// Попытка разобрать. `ok` говорит, удалась ли она, — а не сам
        /// ответ: пустое содержимое сервер тоже присылает, и путать его с
        /// неудачей нельзя. Первая по счёту беда запоминается для журнала.
        /// </summary>
        private static object TryUnpack(byte[] raw, ref string first, string how, out bool ok)
        {
            ok = false;
            if (raw.Length == 0) return null;

            try
            {
                object made = MessagePack.Unpack(raw);
                ok = true;

                // О необычном способе говорим вслух — и один раз: строка в
                // журнале ценна тем, что называет способ, а не тем, что
                // повторяется на каждый пакет.
                if (how != "сжатое" && how != "как есть" && !_told.Contains(how))
                {
                    _told.Add(how);
                    Log.Write("api", "содержимое разобрано как " + how);
                }

                return made;
            }
            catch (Exception error)
            {
                if (first == null) first = how + ": " + error.Message;
                return null;
            }
        }

        /// <summary>О каких способах уже сказано в журнале.</summary>
        private static readonly List<string> _told = new List<string>();

    }
}
