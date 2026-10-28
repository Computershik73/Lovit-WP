using System.Collections.Generic;
using Lovit.Core.Protocol;

namespace Lovit.Models
{
    /// <summary>Карточка человека. Порт MAXContact.</summary>
    public class MaxContact
    {
        public long ContactId { get; set; }
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
        public long Phone { get; set; }
        public string Description { get; set; }

        /// <summary>Признаки: BOT, OFFICIAL, REMOVED и прочие.</summary>
        public HashSet<string> Options { get; set; }

        /// <summary>
        /// Человек записан у нас в книжке. Видно по записи `CUSTOM` в
        /// `names` — это имя, которое дали ему мы, а не он себе.
        /// </summary>
        public bool IsSavedContact { get; set; }

        public MaxContact()
        {
            Options = new HashSet<string>();
        }

        public bool IsBot { get { return Options.Contains("BOT"); } }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (Phone > 0) return "+" + Phone;
                return "ID " + ContactId;
            }
        }

        public string PhoneText
        {
            get { return Phone > 0 ? "+" + Phone : ""; }
        }

        public static MaxContact FromMap(IDictionary<string, object> map)
        {
            if (map == null) return null;

            var contact = new MaxContact
            {
                ContactId = MessagePack.Long(map, "id"),
                AvatarUrl = MessagePack.String(map, "baseUrl"),
                Phone = MessagePack.Long(map, "phone"),
                Description = MessagePack.String(map, "description")
            };

            // Имя приходит по-разному: у своих контактов это `names` с
            // разобранными частями, у остальных — готовая строка.
            //
            // Записей в `names` бывает несколько, и брать первую попавшуюся
            // нельзя: рядом с собственным именем человека лежит то, под
            // которым он записан у нас в книжке (`CUSTOM`), и порядок их
            // сервер не обещает. Нужна запись с `ONEME` — так выбирают и
            // Komet (chat_parsing.dart), и версия для iOS; первая идёт лишь
            // запасным вариантом, если своего имени в списке нет.
            IList<object> names = MessagePack.List(map, "names");

            foreach (object item in names ?? new List<object>())
            {
                var entry = item as IDictionary<string, object>;
                if (entry != null && MessagePack.String(entry, "type") == "CUSTOM")
                {
                    contact.IsSavedContact = true;
                    break;
                }
            }

            IDictionary<string, object> chosen = Preferred(names);
            if (chosen != null)
            {
                contact.Name = MessagePack.String(chosen, "name");

                if (string.IsNullOrEmpty(contact.Name))
                {
                    string firstName = MessagePack.String(chosen, "firstName");
                    string lastName = MessagePack.String(chosen, "lastName");
                    contact.Name = (firstName + " " + lastName).Trim();
                }
            }

            if (string.IsNullOrEmpty(contact.Name))
            {
                contact.Name = MessagePack.String(map, "name");
            }

            // Признаки у контакта приходят списком, а не словарём, — в отличие
            // от чата, где тот же `options` устроен иначе.
            IList<object> options = MessagePack.List(map, "options");
            if (options != null)
            {
                foreach (object option in options)
                {
                    if (option != null) contact.Options.Add(option.ToString());
                }
            }

            return contact;
        }

        /// <summary>Своё имя человека, а не то, под которым он у нас записан.</summary>
        private static IDictionary<string, object> Preferred(IList<object> names)
        {
            if (names == null) return null;

            IDictionary<string, object> fallback = null;

            foreach (object item in names)
            {
                var entry = item as IDictionary<string, object>;
                if (entry == null) continue;

                if (fallback == null) fallback = entry;
                if (MessagePack.String(entry, "type") == "ONEME") return entry;
            }

            return fallback;
        }
    }
}
