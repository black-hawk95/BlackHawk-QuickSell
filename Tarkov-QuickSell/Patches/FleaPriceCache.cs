using EFT;
using EFT.UI.Ragfair;
using System;
using System.Collections.Generic;

namespace QuickSell.Patches
{
    /// <summary>
    /// Flea prices, keyed by item TEMPLATE id.
    ///
    /// Two sources feed this, in priority order:
    ///
    ///  1. A bulk table fetched once from the server mod at startup (see FleaPriceClient). This is
    ///     what makes tooltips work in raid without touching the network mid-raid.
    ///  2. The game's own per-template lookup, used as a fallback when the server mod is absent or
    ///     a template is missing from the table.
    ///
    /// The per-template results are cached too, which is what stops a 20-item multi-select of
    /// identical loot from firing 20 identical price requests. Flea price is a property of the
    /// template, not of the individual item, so one lookup answers for every copy.
    ///
    /// Note this holds PRICES ONLY - a dictionary of string to double. It does not hold item
    /// objects or assortment data, so its memory cost stays flat regardless of session length.
    /// </summary>
    internal static class FleaPriceCache
    {
        private static readonly Dictionary<string, double> Prices = new();
        private static readonly HashSet<string> Pending = new();

        /// <summary>Number of prices currently held. Used for logging.</summary>
        public static int Count => Prices.Count;

        /// <summary>
        /// Loads the bulk table fetched from the server mod. Existing entries are overwritten.
        /// </summary>
        public static void LoadBulk(Dictionary<string, double> prices)
        {
            if (prices == null) return;

            foreach (var kvp in prices)
            {
                Prices[kvp.Key] = kvp.Value;
            }

            Plugin.LogSource?.LogInfo($"QuickSell: loaded {prices.Count} flea prices; cache now holds {Prices.Count}.");
        }

        /// <summary>
        /// Returns a cached price if one is known. Never hits the network.
        ///
        /// This is the only method the tooltip uses, which is what keeps hovering free: in raid it
        /// is a dictionary lookup and nothing else.
        /// </summary>
        public static bool TryGet(string templateId, out double price)
        {
            return Prices.TryGetValue(templateId, out price);
        }

        /// <summary>
        /// Returns a cached price, or asks the game for it if unknown.
        ///
        /// Only safe outside raid - the request is asynchronous and its callback arrives later.
        /// Requests already in flight are tracked so hovering the same unknown item repeatedly does
        /// not queue duplicate requests.
        /// </summary>
        public static void RequestIfMissing(RagFair ragFair, string templateId, Action<double> onResolved = null)
        {
            if (ragFair == null || string.IsNullOrEmpty(templateId)) return;

            if (Prices.TryGetValue(templateId, out var known))
            {
                onResolved?.Invoke(known);
                return;
            }

            if (!Pending.Add(templateId)) return;

            try
            {
                ragFair.GetMarketPrices(templateId, (ItemMarketPrices result) =>
                {
                    Pending.Remove(templateId);

                    if (result == null) return;

                    Prices[templateId] = result.avg;
                    onResolved?.Invoke(result.avg);
                });
            }
            catch (Exception ex)
            {
                Pending.Remove(templateId);
                Plugin.LogSource?.LogWarning($"QuickSell: flea price lookup failed for {templateId}: {ex.Message}");
            }
        }

        /// <summary>Drops everything. Used by the manual refresh in the F12 menu.</summary>
        public static void Clear()
        {
            Prices.Clear();
            Pending.Clear();
        }
    }
}
