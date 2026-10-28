using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lovit.Models;
using Windows.Storage;

namespace Lovit.Core.Storage
{
    /// <summary>
    /// Карточки собеседников, пережившие закрытие приложения.
    ///
    /// У диалога своего названия нет: и имя, и значок берутся из карточки
    /// того, с кем говорят. Список чатов приезжает с сервера за доли
    /// секунды, а карточки — десятками отдельных запросов и несколько
    /// секунд, и всё это время диалоги стоят в списке номерами вместо имён.
    ///
    /// Поэтому имена и адреса значков кладутся на диск и читаются при
    /// следующем запуске раньше, чем придёт список чатов. Приехавшее с
    /// сервера потом всё равно перепишет их — здесь лежит не истина, а
    /// то, что было в прошлый раз.
    ///
    /// Хранится строками: номер, имя, адрес значка и признаки через
    /// табуляцию. Формат нарочно простой — читать это приходится на самом
    /// раннем шаге запуска, когда каждая доля секунды на виду.
    /// </summary>
    public static class ContactsCache
    {
        private const string FileName = "contacts.txt";

        /// <summary>
        /// Больше этого не храним. Тысяча карточек — это около сотни
        /// килобайт; больше телефону читать при запуске уже дорого, а
        /// пользы от хвоста нет: в списке видны первые несколько десятков.
        /// </summary>
        private const int Most = 1000;

        /// <summary>Когда сохраняли в прошлый раз — чаще, чем раз в пять секунд, незачем.</summary>
        private static DateTime _saved;

        private static readonly TimeSpan Rest = TimeSpan.FromSeconds(5);

        public static async Task<List<MaxContact>> LoadAsync()
        {
            var made = new List<MaxContact>();

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(FileName);

                foreach (string line in await FileIO.ReadLinesAsync(file))
                {
                    MaxContact contact = Parse(line);
                    if (contact != null) made.Add(contact);
                }

                Log.Write("mem", "карточки из памяти: " + made.Count);
            }
            catch (Exception)
            {
                // Файла ещё нет или он испорчен — не беда: карточки
                // приедут с сервера, просто чуть позже.
            }

            return made;
        }

        /// <summary>
        /// Сохранить, если с прошлого раза прошло достаточно. Карточки
        /// приезжают пачками по два-три десятка подряд, и записывать файл
        /// на каждую значило бы писать его полсотни раз кряду.
        /// </summary>
        public static Task SaveAsync(ICollection<MaxContact> contacts)
        {
            if (DateTime.UtcNow - _saved < Rest) return Task.FromResult(false);

            return ForceSaveAsync(contacts);
        }

        public static async Task ForceSaveAsync(ICollection<MaxContact> contacts)
        {
            if (contacts == null || contacts.Count == 0) return;

            _saved = DateTime.UtcNow;

            try
            {
                var lines = new List<string>(contacts.Count);

                foreach (MaxContact contact in contacts)
                {
                    if (contact == null || contact.ContactId == 0) continue;
                    if (string.IsNullOrEmpty(contact.Name)) continue;

                    lines.Add(Write(contact));
                    if (lines.Count >= Most) break;
                }

                if (lines.Count == 0) return;

                StorageFile file = await ApplicationData.Current.LocalFolder
                    .CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteLinesAsync(file, lines);
            }
            catch (Exception error)
            {
                Log.Write("mem", "карточки не сохранить — " + error.Message);
            }
        }

        private static string Write(MaxContact contact)
        {
            var made = new StringBuilder();

            made.Append(contact.ContactId).Append('\t');
            made.Append(Plain(contact.Name)).Append('\t');
            made.Append(Plain(contact.AvatarUrl)).Append('\t');

            bool first = true;

            foreach (string option in contact.Options)
            {
                if (!first) made.Append(',');
                made.Append(Plain(option));

                first = false;
            }

            return made.ToString();
        }

        private static MaxContact Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;

            string[] parts = line.Split('\t');
            if (parts.Length < 3) return null;

            long id;
            if (!long.TryParse(parts[0], out id) || id == 0) return null;

            var contact = new MaxContact
            {
                ContactId = id,
                Name = parts[1],
                AvatarUrl = parts[2]
            };

            if (parts.Length > 3 && parts[3].Length > 0)
            {
                foreach (string option in parts[3].Split(',')) contact.Options.Add(option);
            }

            return contact;
        }

        /// <summary>
        /// Строка без разделителей: имя человека может содержать что угодно,
        /// а табуляция и перевод строки здесь разбивают запись.
        /// </summary>
        private static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
