using System;

namespace Lovit.Core.Protocol
{
    /// <summary>
    /// Распаковка блока LZ4. Порт src/Core/Protocol/MAXLZ4.m.
    ///
    /// Только распаковка: исходящие пакеты мы не сжимаем — протокол это
    /// допускает, а входящие сервер сжимает по своему усмотрению, и не
    /// разобрать их нельзя.
    ///
    /// Формат блока: последовательность лексем. Старшие четыре бита лексемы —
    /// сколько байт скопировать как есть, младшие — сколько повторить из уже
    /// распакованного. Длины больше 14 продолжаются байтами 0xFF.
    /// </summary>
    public static class Lz4
    {
        /// <summary>
        /// Распаковывает блок целиком.
        ///
        /// `maxOutput` — только предел на случай кривой длины, а не ожидаемый
        /// размер: буфер растёт по мере надобности. Раньше он был жёстким,
        /// и это выходило боком — в заголовке пакета лежит не точный размер,
        /// а множитель-подсказка, и когда данные сжимались лучше подсказки,
        /// хвост ответа молча обрезался. Разобрать такой ответ уже нельзя,
        /// пакет отбрасывался, и, например, история чата просто не приходила —
        /// через раз, потому что зависело это от того, насколько хорошо
        /// сжались именно эти сообщения.
        /// </summary>
        /// <summary>
        /// Чем кончилась последняя распаковка. Пусто — всё разобралось до
        /// конца; иначе здесь то место, где пришлось остановиться.
        ///
        /// Молчаливый обрыв — худшее, что может сделать распаковщик:
        /// наружу выходит наполовину разобранная каша, и спотыкается на ней
        /// уже разбор содержимого, за тридевять земель от настоящей причины.
        /// </summary>
        public static string LastTrouble { get; private set; }

        public static byte[] Decompress(byte[] input, int maxOutput)
        {
            LastTrouble = null;

            var output = new byte[Math.Min(Math.Max(input.Length * 4, 4096), maxOutput)];

            int source = 0;
            int destination = 0;

            while (source < input.Length)
            {
                int token = input[source++];

                int literals = token >> 4;
                if (literals == 15)
                {
                    int add;
                    do
                    {
                        if (source >= input.Length)
                        {
                            return Stop(output, destination, "длина литералов оборвана на "
                                        + source + " из " + input.Length);
                        }

                        add = input[source++];
                        literals += add;
                    } while (add == 255);
                }

                if (literals > 0)
                {
                    if (source + literals > input.Length)
                    {
                        return Stop(output, destination, "литералов " + literals
                                    + " от " + source + ", а всего " + input.Length);
                    }

                    output = Ensure(output, destination + literals, maxOutput);

                    if (destination + literals > output.Length)
                    {
                        return Stop(output, destination, "некуда класть " + literals
                                    + " литералов: предел " + maxOutput);
                    }

                    Buffer.BlockCopy(input, source, output, destination, literals);
                    source += literals;
                    destination += literals;
                }

                // Хвост блока — только литералы, смещения за ними нет.
                if (source + 1 >= input.Length) break;

                int offset = input[source] | (input[source + 1] << 8);
                source += 2;
                if (offset == 0 || offset > destination)
                {
                    return Stop(output, destination, "смещение " + offset
                                + " при разобранных " + destination + " байтах, вход на "
                                + source + " из " + input.Length);
                }

                int length = token & 0x0F;
                if (length == 15)
                {
                    int add;
                    do
                    {
                        if (source >= input.Length)
                        {
                            return Stop(output, destination, "длина повтора оборвана на "
                                        + source + " из " + input.Length);
                        }

                        add = input[source++];
                        length += add;
                    } while (add == 255);
                }
                length += 4;   // минимальное совпадение в LZ4 — четыре байта

                output = Ensure(output, destination + length, maxOutput);
                if (destination + length > output.Length) length = output.Length - destination;

                // Побайтно, а не блоком: куски накладываются друг на друга —
                // так LZ4 и повторяет короткие последовательности.
                int from = destination - offset;
                for (int i = 0; i < length; i++) output[destination + i] = output[from + i];
                destination += length;
            }

            return Trim(output, destination);
        }

        /// <summary>Оборвались на полпути — запомнить, где именно, и отдать что есть.</summary>
        private static byte[] Stop(byte[] buffer, int length, string why)
        {
            LastTrouble = why;
            return Trim(buffer, length);
        }

        /// <summary>Растит буфер вдвое, пока в него не влезет нужное.</summary>
        private static byte[] Ensure(byte[] buffer, int needed, int limit)
        {
            if (needed <= buffer.Length || buffer.Length >= limit) return buffer;

            int size = buffer.Length;
            while (size < needed && size < limit) size *= 2;
            if (size > limit) size = limit;

            var grown = new byte[size];
            Buffer.BlockCopy(buffer, 0, grown, 0, buffer.Length);
            return grown;
        }

        private static byte[] Trim(byte[] buffer, int length)
        {
            if (length == buffer.Length) return buffer;

            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
    }
}
