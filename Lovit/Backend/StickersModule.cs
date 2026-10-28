using System.Collections.Generic;
using System.Threading.Tasks;
using Lovit.Core;
using Lovit.Core.Protocol;
using Lovit.Models;

namespace Lovit.Backend
{
    /// <summary>
    /// Наборы стикеров. Порт StickersModule.
    ///
    /// Устроено в два шага, и иначе нельзя: сначала сервер называет только
    /// номера наборов, потом по этим номерам приходят сами наборы, и лишь в
    /// них — номера стикеров, за которыми нужен третий запрос. Поэтому здесь
    /// всё складывается в память: перелистывая наборы, человек возвращается к
    /// одним и тем же, и спрашивать их заново было бы расточительно.
    /// </summary>
    public class StickersModule
    {
        private static readonly StickersModule _instance = new StickersModule();
        public static StickersModule Instance { get { return _instance; } }

        private readonly Dictionary<long, MaxStickerSet> _sets =
            new Dictionary<long, MaxStickerSet>();

        private readonly Dictionary<long, MaxSticker> _stickers =
            new Dictionary<long, MaxSticker>();

        /// <summary>Свои наборы, в том порядке, в каком их держит сервер.</summary>
        private readonly List<long> _mine = new List<long>();

        /// <summary>Недавно отправленные — они идут первой вкладкой.</summary>
        private readonly List<long> _recent = new List<long>();

        private bool _loaded;

        private StickersModule() { }

        public IList<MaxSticker> Recent
        {
            get { return Pick(_recent); }
        }

        public IList<MaxStickerSet> Sets
        {
            get
            {
                var found = new List<MaxStickerSet>();

                foreach (long id in _mine)
                {
                    MaxStickerSet set;
                    if (_sets.TryGetValue(id, out set)) found.Add(set);
                }

                return found;
            }
        }

        /// <summary>
        /// Загрузить свои наборы и недавние. Второй раз ничего не спрашивает:
        /// панель выбора открывают часто.
        /// </summary>
        public async Task LoadAsync()
        {
            if (_loaded) return;
            if (!await MaxApi.Instance.EnsureOnlineAsync()) return;

            // Недавние и разделы приходят одним ответом.
            IDictionary<string, object> common = await UpdateAsync("STICKER");
            if (common != null) ReadRecents(common);

            // Свои наборы лежат отдельным видом с собственным разделом.
            IDictionary<string, object> mine = await UpdateAsync("FAVORITE_STICKER");
            if (mine != null) ReadMine(mine);

            if (_mine.Count > 0) await FetchSetsAsync(_mine);

            _loaded = true;

            Log.Write("chat", "стикеры: наборов " + _sets.Count
                              + ", недавних " + _recent.Count);
        }

        private async Task<IDictionary<string, object>> UpdateAsync(string type)
        {
            Packet packet = await MaxApi.Instance.RequestAsync(
                Opcodes.AssetsUpdate,
                new Dictionary<string, object> { { "type", type }, { "sync", 0L } });

            return packet == null || packet.IsError ? null : packet.Map;
        }

        private void ReadRecents(IDictionary<string, object> payload)
        {
            IList<object> sections = MessagePack.List(payload, "sections");
            if (sections == null) return;

            foreach (object raw in sections)
            {
                var section = raw as IDictionary<string, object>;
                if (section == null) continue;

                if (MessagePack.String(section, "type") != "RECENTS") continue;

                // Недавние приезжают целыми стикерами, а не номерами: их и
                // складываем сразу, лишний запрос за ними не нужен.
                IList<object> list = MessagePack.List(section, "recentsList");
                if (list == null) continue;

                foreach (object item in list)
                {
                    MaxSticker sticker = MaxSticker.FromMap(item as IDictionary<string, object>);
                    if (sticker == null) continue;

                    _stickers[sticker.StickerId] = sticker;
                    if (!_recent.Contains(sticker.StickerId)) _recent.Add(sticker.StickerId);
                }
            }
        }

        private void ReadMine(IDictionary<string, object> payload)
        {
            IList<object> sections = MessagePack.List(payload, "sections");
            if (sections == null) return;

            foreach (object raw in sections)
            {
                var section = raw as IDictionary<string, object>;
                if (section == null) continue;

                if (MessagePack.String(section, "id") != "FAVORITE_STICKER_SETS") continue;

                IList<object> ids = MessagePack.List(section, "stickerSets");
                if (ids == null) continue;

                foreach (object item in ids)
                {
                    long id = MessagePack.ToLong(item);
                    if (id != 0 && !_mine.Contains(id)) _mine.Add(id);
                }
            }
        }

        private async Task FetchSetsAsync(IList<long> ids)
        {
            IList<object> found = await ByIdsAsync("STICKER_SET", ids, "stickerSets");
            if (found == null) return;

            foreach (object raw in found)
            {
                MaxStickerSet set = MaxStickerSet.FromMap(raw as IDictionary<string, object>);
                if (set != null) _sets[set.SetId] = set;
            }
        }

        /// <summary>
        /// Стикеры набора. Те, что уже лежат в памяти, повторно не
        /// спрашиваются — а лежит там обычно почти всё, потому что наборы
        /// перелистывают туда-обратно.
        /// </summary>
        public async Task<IList<MaxSticker>> StickersOfAsync(MaxStickerSet set)
        {
            if (set == null) return new List<MaxSticker>();

            var missing = new List<long>();
            foreach (long id in set.StickerIds)
            {
                if (!_stickers.ContainsKey(id)) missing.Add(id);
            }

            if (missing.Count > 0)
            {
                IList<object> found = await ByIdsAsync("STICKER", missing, "stickers");
                if (found != null)
                {
                    foreach (object raw in found)
                    {
                        MaxSticker sticker =
                            MaxSticker.FromMap(raw as IDictionary<string, object>);

                        if (sticker != null) _stickers[sticker.StickerId] = sticker;
                    }
                }
            }

            return Pick(set.StickerIds);
        }

        private async Task<IList<object>> ByIdsAsync(string type, IList<long> ids, string key)
        {
            var numbers = new List<object>();
            foreach (long id in ids) numbers.Add(id);

            // Просить всё разом нельзя: у иных наборов сотни стикеров, а
            // сервер на слишком длинный список отвечает отказом.
            const int Page = 100;

            var all = new List<object>();

            for (int at = 0; at < numbers.Count; at += Page)
            {
                var part = new List<object>();
                for (int i = at; i < numbers.Count && i < at + Page; i++) part.Add(numbers[i]);

                Packet packet = await MaxApi.Instance.RequestAsync(
                    Opcodes.AssetsGetByIds,
                    new Dictionary<string, object> { { "type", type }, { "ids", part } });

                if (packet == null || packet.IsError) continue;

                IList<object> found = MessagePack.List(packet.Map, key);
                if (found != null) all.AddRange(found);
            }

            return all;
        }

        /// <summary>
        /// Поиск по значкам — тем самым, которыми стикер помечен. Ищем среди
        /// уже загруженного: сервер отдельного поиска по стикерам не
        /// предлагает, и Komet поступает так же.
        /// </summary>
        public IList<MaxSticker> Search(string query)
        {
            var found = new List<MaxSticker>();

            string term = (query ?? "").Trim().ToLower();
            if (term.Length == 0) return found;

            foreach (MaxSticker sticker in _stickers.Values)
            {
                foreach (string tag in sticker.Tags)
                {
                    if (tag == null) continue;

                    if (tag.ToLower().IndexOf(term, System.StringComparison.Ordinal) >= 0)
                    {
                        found.Add(sticker);
                        break;
                    }
                }

                if (found.Count >= 60) break;
            }

            return found;
        }

        /// <summary>Отметить отправленный — он встанет первым среди недавних.</summary>
        public void NoteSent(MaxSticker sticker)
        {
            if (sticker == null) return;

            _stickers[sticker.StickerId] = sticker;

            _recent.Remove(sticker.StickerId);
            _recent.Insert(0, sticker.StickerId);

            while (_recent.Count > 40) _recent.RemoveAt(_recent.Count - 1);
        }

        private IList<MaxSticker> Pick(IList<long> ids)
        {
            var found = new List<MaxSticker>();

            foreach (long id in ids)
            {
                MaxSticker sticker;
                if (_stickers.TryGetValue(id, out sticker)) found.Add(sticker);
            }

            return found;
        }

        public void Reset()
        {
            _sets.Clear();
            _stickers.Clear();
            _mine.Clear();
            _recent.Clear();

            _loaded = false;
        }
    }
}
