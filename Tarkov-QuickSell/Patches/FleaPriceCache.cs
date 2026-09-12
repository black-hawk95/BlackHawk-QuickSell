using Comfort.Common;
using EFT;
using EFT.HandBook;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI.Ragfair;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// Callbacks that arrived while a request for the same template was already in flight.
        /// They all run once that request resolves.
        /// </summary>
        private static readonly Dictionary<string, List<Action<double>>> Waiting = new();

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
        /// Returns the minimum flea value of one unit of an item together with everything that
        /// will be included when that item is listed. A weapon offer, for example, contains its
        /// mods, magazine and ammunition even though RagfairAddOffer receives only the weapon id.
        ///
        /// The old code looked up only the root template. That listed the complete item tree but
        /// priced it as the bare receiver, effectively giving every child item away for free.
        /// </summary>
        public static bool TryGetTreeUnitPrice(Item root, out double price, ISet<string> missing = null)
        {
            price = 0d;
            if (root == null) return false;

            var complete = true;

            foreach (var part in root.GetAllItems())
            {
                var templateId = part.TemplateId.ToString();
                if (!Prices.TryGetValue(templateId, out var unitPrice))
                {
                    complete = false;
                    missing?.Add(templateId);
                    continue;
                }

                // The requirement price supplied to RagfairAddOffer is per root unit. A stack of
                // loose ammunition therefore stays a per-round price, while ammunition contained
                // inside a magazine contributes the value of every round to that magazine's one
                // offer unit.
                var count = ReferenceEquals(part, root) ? 1 : Math.Max(1, part.StackObjectsCount);
                price += GetConditionAdjustedUnitPrice(part, unitPrice) * count;
            }

            return complete;
        }

        /// <summary>
        /// Applies EFT's own condition/resource valuation to a market price. This covers partially
        /// consumed food, drinks, medkits, fuel and repair kits, used keys and damaged equipment.
        /// The factor is capped at one so unrelated premiums (for example dogtag level or an item
        /// enhancement) do not inflate a template-wide flea price.
        /// </summary>
        public static double GetConditionAdjustedUnitPrice(Item item, double marketUnitPrice)
        {
            if (item == null || marketUnitPrice <= 0d) return 0d;

            try
            {
                var handbook = Singleton<Handbook>.Instance;
                var count = Math.Max(1, item.StackObjectsCount);
                // Matching stack counts in numerator and denominator leave a per-unit factor.
                var fullBasePrice = handbook.GetBasePrice(item.TemplateId) * (double)count;
                if (fullBasePrice <= 0d) return marketUnitPrice;

                var currentBasePrice = PriceCalculator.CalculateBasePriceForSingleItem(
                    item, count, handbook);
                var conditionFactor = Math.Max(0d, Math.Min(1d, currentBasePrice / fullBasePrice));
                return marketUnitPrice * conditionFactor;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuickSell: condition adjustment failed for {item.TemplateId}: {ex.Message}");
                return marketUnitPrice;
            }
        }

        /// <summary>Every template whose price is needed to value the complete item tree.</summary>
        public static IEnumerable<string> GetTreeTemplateIds(Item root)
        {
            if (root == null) yield break;

            foreach (var part in root.GetAllItems())
            {
                yield return part.TemplateId.ToString();
            }
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

            // A request for this template is already in flight, most likely started by a tooltip.
            // Returning here without calling back would leave a caller that is counting responses
            // waiting forever - the flea confirmation window would simply never appear. Queue the
            // callback instead so it runs when the in-flight request resolves.
            if (!Pending.Add(templateId))
            {
                if (onResolved != null)
                {
                    if (!Waiting.TryGetValue(templateId, out var queue))
                    {
                        queue = new List<Action<double>>();
                        Waiting[templateId] = queue;
                    }

                    queue.Add(onResolved);
                }

                return;
            }

            try
            {
                ragFair.GetMarketPrices(templateId, (ItemMarketPrices result) =>
                {
                    Pending.Remove(templateId);

                    // Keep the fallback aligned with the server table's minimum-price semantics.
                    if (result != null) Prices[templateId] = result.min;

                    // Everyone waiting on this template is notified, whether the lookup succeeded
                    // or not. A caller counting responses must hear back either way, or it stalls.
                    var value = result?.min ?? 0d;

                    onResolved?.Invoke(value);
                    ReleaseWaiting(templateId, value);
                });
            }
            catch (Exception ex)
            {
                Pending.Remove(templateId);
                // Release both the original caller and queued callers on synchronous failure.
                onResolved?.Invoke(0d);
                ReleaseWaiting(templateId, 0d);
                Plugin.LogSource?.LogWarning($"QuickSell: flea price lookup failed for {templateId}: {ex.Message}");
            }
        }

        /// <summary>Runs and clears any callbacks queued behind an in-flight request.</summary>
        private static void ReleaseWaiting(string templateId, double value)
        {
            if (!Waiting.TryGetValue(templateId, out var queue)) return;

            Waiting.Remove(templateId);

            foreach (var callback in queue)
            {
                try { callback(value); }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"QuickSell: queued price callback threw: {ex.Message}");
                }
            }
        }

        /// <summary>Drops everything. Used by the manual refresh in the F12 menu.</summary>
        public static void Clear()
        {
            Prices.Clear();
            Pending.Clear();

            // Anything still waiting is released so no caller is left hanging on a request that
            // will now never resolve.
            foreach (var templateId in Waiting.Keys.ToList()) ReleaseWaiting(templateId, 0d);
            Waiting.Clear();
        }
    }
}
