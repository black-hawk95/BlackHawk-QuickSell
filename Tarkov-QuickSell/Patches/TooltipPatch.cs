using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using EFT.UI.Ragfair;
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
        /// Finished tooltip text, keyed by item id and the observed item-tree state.
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

                // The root id does not change when a weapon is rebuilt. Include every attached
                // item, its stack count and the current footprint so swapping a scope, magazine,
                // ammunition or folding a stock immediately produces a fresh tooltip.
                var id = GetItemStateKey(item);

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
        /// Builds a single best-route recommendation shared by hover text and the item card.
        /// Returns empty when neither price is available, leaving the original UI text unchanged.
        ///
        /// <paramref name="complete"/> is false when a line that should be present is still
        /// pending - trader assortments loading, or a flea price not yet known. The caller uses it
        /// to decide whether the result is worth caching.
        /// </summary>
        internal static string BuildPriceLines(Item item, out bool complete)
        {
            complete = true;

            int traderPrice = 0;
            string traderName = null;
            double fleaNetPrice = 0;
            var session = ContextMenuPatch.GetSession();

            if (Plugin.ShowTraderPriceInTooltip)
            {
                // Trader prices are menu-only: in raid there is no session and no trader data.
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

            OfflineInventoryController inventoryController = null;
            var fleaEligibility = Plugin.ShowFleaPriceInTooltip
                ? GetFleaEligibility(item, session, out inventoryController)
                : FleaEligibility.ItemIneligible;

            if (fleaEligibility == FleaEligibility.TemporarilyUnavailable)
            {
                // Money and offer-slot capacity can change without the item changing. Do not
                // freeze a trader-only recommendation in the cache in that situation.
                complete = false;
            }

            if (Plugin.ShowFleaPriceInTooltip && fleaEligibility == FleaEligibility.Eligible)
            {
                // The cached unit value is the sum of the minimum market prices of the root item
                // and every installed part. CalculateTaxPrice is the same game method used by the
                // offer window, so the comparison is trader payout versus actual flea NET payout.
                var missing = new HashSet<string>();
                if (FleaPriceCache.TryGetTreeUnitPrice(item, out var cached, missing))
                {
                    // Tooltip recommendation is intentionally based on the market minimum itself,
                    // independent of the configurable percentage used by the quick-list command.
                    var listingUnitPrice = (int)Math.Ceiling(cached);
                    var count = Math.Max(1, item.StackObjectsCount);
                    var gross = listingUnitPrice * (double)count;
                    var fee = (int)Math.Ceiling(
                        PriceCalculator.CalculateTaxPrice(item, count, listingUnitPrice, false));

                    if (ModEnvironment.IsInRaid || HasEnoughRoublesForFee(inventoryController, fee))
                    {
                        fleaNetPrice = Math.Max(0, gross - fee);
                    }
                    else
                    {
                        // The item itself is eligible, but it cannot be listed right now because
                        // the commission cannot be paid (or could not be calculated reliably).
                        complete = false;
                    }
                }
                else if (!ModEnvironment.IsInRaid)
                {
                    // Outside raid every unknown component can be resolved in the background so
                    // the next hover shows the value of the complete item tree. Deliberately not
                    // done in raid.
                    var ragFair = session?.RagFair;
                    if (ragFair != null)
                    {
                        foreach (var templateId in missing)
                            FleaPriceCache.RequestIfMissing(ragFair, templateId);
                    }

                    // The price is on its way, so this result would be missing a line.
                    complete = false;
                }
            }

            if (traderName == null && fleaNetPrice <= 0) return null;

            // On a tie the trader wins: the payout is immediate and has no offer-slot cost.
            bool fleaWins = fleaNetPrice > traderPrice;

            var sb = new StringBuilder();

            if (fleaWins)
            {
                AppendLine(sb, "Барахолка (мин., после комиссии)", fleaNetPrice, item);
            }
            else if (traderName != null)
            {
                AppendLine(sb, traderName, traderPrice, item);
            }

            return sb.ToString();
        }

        /// <summary>Formats the winning quote, tier colour and optional value per occupied cell.</summary>
        private static void AppendLine(StringBuilder sb, string label, double price, Item item)
        {
            sb.Append("<br>");
            sb.Append($"<color=#{Plugin.BestTradeColor}>{label}</color>: ");

            var formatted = PriceFormat.Format(price);
            var perSlotFormatted = PriceFormat.Format(price / TierColors.GetSlotCount(item));

            if (Plugin.EnableColorCoding)
            {
                // Tier is decided by price PER SLOT, not total: a full ammo box and a single bullet
                // should not read as the same value density. Ammo is scored on penetration instead,
                // because a cheap high-pen round is worth more to a player than an expensive weak one.
                var hex = TierColors.GetHex(item, price);
                formatted = $"<color=#{hex}>{formatted}</color>";
            }

            formatted = $"<b>{formatted}</b>";

            sb.Append(formatted);

            // For a one-cell item this would merely repeat the same number and make both the
            // hover tooltip and the item card noisier.
            if (TierColors.GetSlotCount(item) > 1)
                sb.Append($" <color=#b0b0b0>({perSlotFormatted} за слот)</color>");
        }

        // Temporary failures should not be cached as a permanent item restriction.
        private enum FleaEligibility
        {
            Eligible,
            ItemIneligible,
            TemporarilyUnavailable
        }

        /// <summary>
        /// Uses EFT's own final selection check instead of trying to duplicate flea rules. It
        /// covers examination, template bans, forbidden installed parts, non-empty containers,
        /// FIR restrictions and failure to remove the item from its current address.
        /// </summary>
        private static FleaEligibility GetFleaEligibility(
            Item item,
            IEftSession session,
            out OfflineInventoryController inventoryController)
        {
            inventoryController = null;

            if (session?.RagFair == null || !session.RagFair.Available)
                return FleaEligibility.ItemIneligible;

            if (ModEnvironment.IsInRaid)
            {
                // A raid item cannot be listed until extraction, so menu-only checks such as its
                // current address, free offer slots and cash in the stash are inapplicable. The
                // useful answer here is whether this exact item will be eligible after extraction.
                // CompoundItem.CanSellOnRagfair also checks forbidden installed components.
                if (!item.CanSellOnRagfair || item.IsNotEmpty())
                    return FleaEligibility.ItemIneligible;

                if (RagFair.Settings.isOnlyFoundInRaidAllowed &&
                    !item.CanSellOnRagfairRaidRelated)
                    return FleaEligibility.ItemIneligible;

                return FleaEligibility.Eligible;
            }

            inventoryController = ContextMenuPatch.GetInventoryController();
            if (inventoryController == null || inventoryController is not ItemController itemController)
                return FleaEligibility.TemporarilyUnavailable;

            if (!SellLocation.IsSellable(item))
                return FleaEligibility.ItemIneligible;

            var helper = new RagfairNewOfferContext(
                inventoryController.Inventory.Stash.Grids[0], itemController);

            if (!helper.HighlightedAtRagfair(item) ||
                !RagFair.CanBeSelectedAtRagfair(item, itemController, out _))
                return FleaEligibility.ItemIneligible;

            if (!Plugin.IgnoreFleaCapacity &&
                session.RagFair.MyOffersCount >=
                session.RagFair.GetMaxOffersCount(session.RagFair.MyRating))
                return FleaEligibility.TemporarilyUnavailable;

            return FleaEligibility.Eligible;
        }

        /// <summary>Checks stash RUB funds using the same inventory money aggregation as EFT.</summary>
        private static bool HasEnoughRoublesForFee(
            OfflineInventoryController inventoryController,
            int fee)
        {
            if (inventoryController == null || fee < 0) return false;
            if (fee == 0) return true;

            var money = InventoryExtension.GetMoneySums(
                inventoryController.Inventory.Stash.Grid.ContainedItems.Keys);

            return money.TryGetValue(ECurrencyType.RUB, out var roubles) && roubles >= fee;
        }

        /// <summary>
        /// Fingerprints the item tree, footprint and observed eligibility state for hover caching.
        /// Price-table/session changes are handled separately by Invalidate.
        /// </summary>
        private static string GetItemStateKey(Item item)
        {
            unchecked
            {
                var hash = 17;
                foreach (var part in item.GetAllItems())
                {
                    hash = hash * 31 + part.Id.ToString().GetHashCode();
                    hash = hash * 31 + part.TemplateId.ToString().GetHashCode();
                    hash = hash * 31 + part.StackObjectsCount;
                    hash = hash * 31 + part.SpawnedInSession.GetHashCode();
                    hash = hash * 31 + part.CanSellOnRagfair.GetHashCode();
                    hash = hash * 31 + part.CanSellOnRagfairRaidRelated.GetHashCode();
                    hash = hash * 31 + part.PinLockState.GetHashCode();
                }

                var size = item.CalculateCellSize();
                hash = hash * 31 + size.X;
                hash = hash * 31 + size.Y;
                hash = hash * 31 + ModEnvironment.IsInRaid.GetHashCode();

                var session = ContextMenuPatch.GetSession();
                hash = hash * 31 + (session?.RagFair?.Available ?? false).GetHashCode();

                var inventoryController = ContextMenuPatch.GetInventoryController();
                if (inventoryController != null)
                    hash = hash * 31 + inventoryController.Examined(item).GetHashCode();

                return $"{item.Id}:{hash:X8}";
            }
        }
    }

    /// <summary>
    /// Replaces the old server-generated template valuation in the opened item card with a value
    /// calculated from the concrete item instance. This makes FIR, condition and weapon parts
    /// accurate and lets the same recommendation be used in the card and hover tooltip.
    /// </summary>
    internal class ItemSpecificationPricePatch : ModulePatch
    {
        // Cache reflection handles once; these fields belong to the SPT 4.1.3 EFT item panel.
        private static readonly FieldInfo ItemField =
            AccessTools.Field(typeof(ItemSpecificationPanel), "_item");
        private static readonly FieldInfo LabelsField =
            AccessTools.Field(typeof(ItemSpecificationPanel), "_itemLabels");

        // Hook description refresh so the concrete item recommendation appears in the item card.
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(ItemSpecificationPanel), "method_1");

        [PatchPostfix]
        private static void Postfix(ItemSpecificationPanel __instance)
        {
            try
            {
                // Match hover visibility settings and do not reveal prices for unexamined items.
                if (!Plugin.ShowPriceTooltips ||
                    (ModEnvironment.IsInRaid && !Plugin.ShowPricesInRaid) ||
                    !__instance.Examined)
                    return;

                var item = ItemField?.GetValue(__instance) as Item;
                var labels = LabelsField?.GetValue(__instance) as ItemInfoWindowLabels;
                if (item == null || labels?._description == null) return;

                var lines = TooltipPatch.BuildPriceLines(item, out _);
                if (string.IsNullOrEmpty(lines)) return;

                // BuildPriceLines starts with <br> because it normally appends to a tooltip.
                // In the card the recommendation is the first line instead.
                if (lines.StartsWith("<br>", StringComparison.Ordinal))
                    lines = lines.Substring(4);

                labels.SetDescriptionText($"{lines}<br><br>{labels._description.text}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuickSell: item-card price failed: {ex.Message}");
            }
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
        internal static int GetSlotCount(Item item)
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
