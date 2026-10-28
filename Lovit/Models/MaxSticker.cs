using System.Collections.Generic;
using Lovit.Core.Protocol;

namespace Lovit.Models
{
    /// <summary>Один стикер. Порт StickerItem.</summary>
    public class MaxSticker
    {
        public long StickerId { get; set; }
        public string Url { get; set; }
        public long SetId { get; set; }

        public long Width { get; set; }
        public long Height { get; set; }

        /// <summary>Значки, по которым стикер ищут: «улыбка», «кот» и прочее.</summary>
        public List<string> Tags { get; set; }

        /// <summary>
        /// Движущийся стикер. Рисовать мы такое не умеем — Lottie это набор
        /// указаний для отрисовки, а не картинка, — но неподвижная у него
        /// есть, ею и обходимся.
        /// </summary>
        public bool IsAnimated { get; set; }

        public MaxSticker()
        {
            Tags = new List<string>();
        }

        public static MaxSticker FromMap(IDictionary<string, object> map)
        {
            if (map == null) return null;

            var sticker = new MaxSticker
            {
                StickerId = MessagePack.Long(map, "id"),
                Url = MessagePack.String(map, "url"),
                SetId = MessagePack.Long(map, "setId"),
                Width = MessagePack.Long(map, "width"),
                Height = MessagePack.Long(map, "height"),
                IsAnimated = !string.IsNullOrEmpty(MessagePack.String(map, "lottieUrl"))
            };

            if (sticker.StickerId == 0) return null;

            IList<object> tags = MessagePack.List(map, "tags");
            if (tags != null)
            {
                foreach (object tag in tags)
                {
                    if (tag != null) sticker.Tags.Add(tag.ToString());
                }
            }

            return sticker;
        }
    }

    /// <summary>Набор стикеров. Порт StickerSet.</summary>
    public class MaxStickerSet
    {
        public long SetId { get; set; }
        public string Name { get; set; }
        public string IconUrl { get; set; }

        /// <summary>
        /// Номера стикеров. Сами стикеры приходят отдельным запросом: набор
        /// хранит только состав.
        /// </summary>
        public List<long> StickerIds { get; set; }

        public MaxStickerSet()
        {
            StickerIds = new List<long>();
        }

        public static MaxStickerSet FromMap(IDictionary<string, object> map)
        {
            if (map == null) return null;

            var set = new MaxStickerSet
            {
                SetId = MessagePack.Long(map, "id"),
                Name = MessagePack.String(map, "name") ?? "",
                IconUrl = MessagePack.String(map, "iconUrl")
            };

            if (set.SetId == 0) return null;

            IList<object> ids = MessagePack.List(map, "stickers");
            if (ids != null)
            {
                foreach (object id in ids)
                {
                    long number = MessagePack.ToLong(id);
                    if (number != 0) set.StickerIds.Add(number);
                }
            }

            return set;
        }
    }
}
