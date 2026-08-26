using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace QuickSell.Patches
{
    /// <summary>
    /// Appends price information to item tooltips.
    ///
    /// The tooltip is a TextMeshPro label, so this is entirely string work - colours and bold are
    /// TMP rich-text tags injected into the text. There is no rendering code and no new UI object,
    /// which is what makes it cheap enough to run on every hover.
    ///
    /// Approach inspired by SwiftXP's "Show Me The Money"; written independently against this mod's
    /// own price cache.
    /// </summary>
    internal class TooltipPatch : ModulePatch
    {
        // Set once the first tooltip asks for a trader price, so the assortment load happens a
        // single time per session rather than on every hover.
        private static bool _assortmentsRequested;

        /// <summary>
        /// Finished tooltip text, keyed by item id.
        ///
        /// Building these lines costs a few milliseconds, most of it in TryGetBestOffer, which asks
        /// every trader what it would pay. Measured while sweeping the cursor across a full stash
        /// that reached 2-3ms - a sixth of a 60fps frame, repeatedly, which is exactly what the
        /// stutter was.
        ///
        /// An item's price does not change while it sits in the stash, so the answer only has to be
        /// worked out once. Repeat hovers, sweeping back over items already seen, and re-opening
        /// the stash all become a dictionary lookup.
        ///
        /// Cleared when flea prices are refreshed or the session changes; see Invalidate.
        /// </summary>
        private static readonly Dictionary<string, string> ResultCache = new();

        // A stash plus containers can hold a lot of items. This only bounds pathological cases -
        // each entry is a short string, so normal play stays well under it.
        private const int MaxCachedResults = 4000;

        /// <summary>Drops every cached tooltip. Called when prices change underneath us.</summary>
        public static void Invalidate()
        {
            ResultCache.Clear();
            _assortmentsRequested = false;
        }

        protected override MethodBase GetTargetMethod()
        {
            // SimpleTooltip.Show has several overloads; the one that takes the tooltip text first
            // is the one to patch.
            var method = AccessTools.GetDeclaredMethods(typeof(SimpleTooltip))
                .FirstOrDefault(m =>
                    m.Name == "Show"
                    && m.GetParameters().Length > 0
                    && m.GetParameters()[0].ParameterType == typeof(string));

            if (method == null)
            {
                Plugin.LogSource?.LogError(
                    "QuickSell: SimpleTooltip.Show(string, ...) not found; price tooltips are disabled.");
            }

            return method;
        }

        [PatchPrefix]
        private static void Prefix(ref string text)
        {
            try
            {
                if (!Plugin.ShowPriceTooltips) return;

                // Raid gate. With ShowPricesInRaid off this is the only thing the patch does in
                // raid: one boolean pair, no allocation.
                if (ModEnvironment.IsInRaid && !Plugin.ShowPricesInRaid) return;

                var item = ItemUiContext.Instance?.CurrentItemContext?.Item;
                if (item == null) return;

                var id = item.Id.ToString();

                // Cache hit is the common case once the stash has been browsed once.
                if (!ResultCache.TryGetValue(id, out var suffix))
                {
                    suffix = BuildPriceLines(item, out var complete) ?? string.Empty;

                    // Only a COMPLETE result is cached.
                    //
                    // RefreshAssortment is a server fetch, so the first tooltip after traders load
                    // asks for prices before the data has arrived and every trader returns null.
                    // Caching that empty answer froze the item without a trader line until
                    // something cleared the cache - which is why pressing "Refresh flea prices"
                    // appeared to fix it. Leaving an incomplete result uncached lets the next hover
                    // recompute, so it heals itself once the assortments land.
                    if (complete)
                    {
                        if (ResultCache.Count >= MaxCachedResults) ResultCache.Clear();
                        ResultCache[id] = suffix;
                    }
                }

                if (!string.IsNullOrEmpty(suffix)) text += suffix;
            }
            catch (Exception ex)
            {
                // A tooltip must never take the UI down with it.
                Plugin.LogSource?.LogWarning($"QuickSell: tooltip failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the appended lines. Returns empty when neither price is available, so the tooltip
        /// is left exactly as the game made it.
        /// </summary>
        /// <summary>
        /// Builds the appended lines.
        ///
        /// <paramref name="complete"/> is false when a line that should be present is still
        /// pending - trader assortments loading, or a flea price not yet known. The caller uses it
        /// to decide whether the result is worth caching.
        /// </summary>
        private static string BuildPriceLines(Item item, out bool complete)
        {
            complete = true;

            int traderPrice = 0;
            string traderName = null;
            double fleaPrice = 0;

            if (Plugin.ShowTraderPriceInTooltip)
            {
                // Trader prices are menu-only: in raid there is no session and no trader data.
                var session = ContextMenuPatch.GetSession();
                if (session != null)
                {
                    // Assortments must be loaded before GetUserItemPrice returns anything. Since
                    // assortments now load lazily rather than at startup, the tooltip has to ask
                    // for them itself - without this every trader returns null and no trader line
                    // ever appears.
                    //
                    // Guarded by a flag rather than relying on the per-trader set inside
                    // TraderService, so the common case is one bool test instead of a loop over
                    // every trader on every hover.
                    if (!_assortmentsRequested)
                    {
                        _assortmentsRequested = true;
                        TraderService.EnsureAllAssortments(session);
                    }

                    if (TraderService.TryGetBestOffer(item, session, out var trader, out var price))
                    {
                        traderName = trader.LocalizedName;
                        traderPrice = price;
                    }
                    else if (TraderService.AssortmentsLoading(session))
                    {
                        // No trader bought it, but at least one is still fetching its assortment,
                        // so "nobody buys this" is not yet a trustworthy answer.
                        complete = false;
                    }
                }
                else
                {
                    // No session yet; the answer may differ once there is one.
                    complete = false;
                }
            }

            if (Plugin.ShowFleaPriceInTooltip)
            {
                // Cache only - never a network call. This is what allows tooltips in raid without
                // touching the server: the table was fetched once at startup.
                if (FleaPriceCache.TryGet(item.TemplateId, out var cached))
                {
                    fleaPrice = cached * item.StackObjectsCount;
                }
                else if (!ModEnvironment.IsInRaid)
                {
                    // Outside raid an unknown template can be resolved in the background so the
                    // next hover has it. Deliberately not done in raid.
                    var ragFair = ContextMenuPatch.GetSession()?.RagFair;
                    if (ragFair != null) FleaPriceCache.RequestIfMissing(ragFair, item.TemplateId);

                    // The price is on its way, so this result would be missing a line.
                    complete = false;
                }
            }

            if (traderName == null && fleaPrice <= 0) return null;

            // Whichever pays more gets highlighted, so the "sell it here" answer is visible without
            // comparing the numbers.
            bool fleaWins = fleaPrice > traderPrice;

            var sb = new StringBuilder();

            if (traderName != null)
            {
                AppendLine(sb, traderName, traderPrice, item, isBest: !fleaWins);
            }

            if (fleaPrice > 0)
            {
                AppendLine(sb, "Flea market", fleaPrice, item, isBest: fleaWins);
            }

            return sb.ToString();
        }

        private static void AppendLine(StringBuilder sb, string label, double price, Item item, bool isBest)
        {
            sb.Append("<br>");

            if (isBest)
                sb.Append($"<color=#{Plugin.BestTradeColor}>{label}</color>: ");
            else
                sb.Append($"{label}: ");

            var formatted = PriceFormat.Format(price);

            if (Plugin.EnableColorCoding)
            {
                // Tier is decided by price PER SLOT, not total: a full ammo box and a single bullet
                // should not read as the same value density. Ammo is scored on penetration instead,
                // because a cheap high-pen round is worth more to a player than an expensive weak one.
                var hex = TierColors.GetHex(item, price);
                formatted = $"<color=#{hex}>{formatted}</color>";
            }

            if (isBest) formatted = $"<b>{formatted}</b>";

            sb.Append(formatted);
        }
    }

    /// <summary>Maps an item and its price onto a rarity colour.</summary>
    internal static class TierColors
    {
        public static string GetHex(Item item, double totalPrice)
        {
            if (Plugin.UseAmmoPenetrationTiers && TryGetPenetration(item, out var penetration))
            {
                return PickByThresholds(
                    penetration,
                    Plugin.AmmoTierThresholds,
                    Plugin.TierColorHexes);
            }

            var perSlot = totalPrice / GetSlotCount(item);

            return PickByThresholds(perSlot, Plugin.PriceTierThresholds, Plugin.TierColorHexes);
        }

        /// <summary>
        /// Grid footprint of the item, minimum 1. Guarded because the cell-size API is one of the
        /// less stable corners of the game and a colour is not worth an exception on every hover.
        /// </summary>
        private static int GetSlotCount(Item item)
        {
            try
            {
                var size = item.CalculateCellSize();
                var cells = size.X * size.Y;
                return cells > 0 ? cells : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static bool TryGetPenetration(Item item, out double penetration)
        {
            penetration = 0;

            // AmmoItemClass was renamed to EFT.InventoryLogic.Ammo in 4.1. AmmoBox kept its name.
            if (item is Ammo ammo)
            {
                penetration = ammo.PenetrationPower;
                return true;
            }

            if (item is AmmoBox box && box.Cartridges?.Items?.FirstOrDefault() is Ammo boxed)
            {
                penetration = boxed.PenetrationPower;
                return true;
            }

            return false;
        }

        /// <summary>
        /// thresholds has 5 entries (the upper bound of each of the first 5 tiers); colors has 6.
        /// Anything above the last threshold falls into the final tier.
        /// </summary>
        private static string PickByThresholds(double value, double[] thresholds, string[] colors)
        {
            for (int i = 0; i < thresholds.Length && i < colors.Length - 1; i++)
            {
                if (value < thresholds[i]) return colors[i];
            }

            return colors[colors.Length - 1];
        }
    }
}
