using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickSell.Patches
{
    /// <summary>
    /// Owns the trader list and their assortments.
    ///
    /// This replaces the old TraderInventoryLoadingPatch, which hooked the Trader CONSTRUCTOR and
    /// called RefreshAssortment on every trader the moment it was built. That meant a full
    /// assortment fetch for every trader at startup whether or not the player ever sold anything,
    /// and the resulting data stayed referenced for the whole session - carried through every raid.
    ///
    /// Here assortments load on first use instead. Nothing is fetched until a sell or a price
    /// lookup actually needs it.
    /// </summary>
    internal static class TraderService
    {
        // Lightkeeper is a special trader and must not be force-refreshed.
        private const string LightkeeperTraderId = "638f541a29ffd1183d187f57";

        private static Trader[] _traders;

        /// <summary>
        /// Which traders have had their assortment loaded.
        ///
        /// Keyed on the trader OBJECT, not its id. The game rebuilds its Trader objects after a
        /// raid - that is why the original mod hooked the Trader constructor - and the new objects
        /// reuse the same ids. Tracking by id meant a rebuilt trader looked "already loaded" and
        /// never had its assortment fetched, so every price came back null.
        ///
        /// A reference-keyed set follows the objects instead: a rebuilt trader is a different
        /// object, so it correctly counts as not loaded.
        /// </summary>
        private static readonly HashSet<Trader> AssortmentLoaded =
            new(ReferenceEqualityComparer<Trader>.Instance);

        /// <summary>Clears the cached traders and their assortment state.</summary>
        public static void Reset()
        {
            _traders = null;
            AssortmentLoaded.Clear();
        }

        /// <summary>
        /// True when the cached array no longer matches what the session reports.
        ///
        /// After a raid the session hands back a fresh set of Trader objects while the cache still
        /// holds the old, now-inert ones. Those return null from GetUserItemPrice for everything,
        /// which is what produced "No items can be sold to traders" until the game was restarted.
        ///
        /// Comparing the first entry by reference is enough: the game replaces the whole set at
        /// once, so one mismatch means all of them are stale. A count change catches a trader being
        /// unlocked mid-session.
        /// </summary>
        private static bool CacheIsStale(IEftSession session)
        {
            if (_traders == null || _traders.Length == 0) return true;

            var current = session.Traders;
            if (current == null) return true;

            var live = current.FirstOrDefault(t => !t.Settings.AvailableInRaid);
            if (live == null) return true;

            return !ReferenceEquals(live, _traders[0])
                || current.Count(t => !t.Settings.AvailableInRaid) != _traders.Length;
        }

        /// <summary>
        /// The sellable traders for the current session.
        ///
        /// Deliberately re-queried whenever the cache is empty rather than cached permanently: a
        /// trader unlocked mid-session (a quest-gated modded trader, or Lightkeeper) would
        /// otherwise not appear as a sell target until the game was restarted. Modded traders need
        /// no special handling - whatever the server reports is used.
        /// </summary>
        public static Trader[] GetTraders(IEftSession session)
        {
            if (session == null) return _traders;
            if (!CacheIsStale(session)) return _traders;

            // Rebuilding is a Where plus a ToArray over roughly ten traders. It runs once per sell
            // operation or uncached tooltip, never per frame, so the cost is not worth the risk of
            // holding a stale set.
            AssortmentLoaded.Clear();

            // Tooltips cache their finished text, and any built from the previous trader objects
            // hold prices that are now wrong.
            TooltipPatch.Invalidate();

            _traders = session.Traders
                .Where(trader => !trader.Settings.AvailableInRaid)
                .ToArray();

            // Publish the names into the F12 menu so the blacklist box has a reference list to
            // copy from. Traders do not exist when the plugin binds its settings, so this is the
            // first point at which the list can be filled in - and modded traders appear here
            // automatically, since nothing about the list is hardcoded.
            Plugin.PublishTraderList(_traders.Select(t => t.LocalizedName));

            return _traders;
        }

        /// <summary>
        /// Makes sure a trader's assortment is loaded before its prices are trusted.
        ///
        /// Tracked per trader object so repeated sells don't re-fetch, while a trader rebuilt
        /// after a raid is correctly treated as needing a fresh assortment.
        /// </summary>
        public static void EnsureAssortment(Trader trader)
        {
            if (trader == null) return;
            if (trader.Id == LightkeeperTraderId) return;
            if (!AssortmentLoaded.Add(trader)) return;

            try
            {
                trader.RefreshAssortment(false, true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuickSell: failed to refresh assortment for trader {trader.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads assortments for every trader once, ahead of a pricing pass.
        ///
        /// Called once per sell operation rather than once per item: with a 30-item multi-select
        /// the old code re-ran its trader health check for every single item.
        /// </summary>
        public static void EnsureAllAssortments(IEftSession session)
        {
            var traders = GetTraders(session);
            if (traders == null) return;

            foreach (var trader in traders)
            {
                EnsureAssortment(trader);
            }
        }

        /// <summary>
        /// True while any trader is still fetching its assortment.
        ///
        /// RefreshAssortment goes to the server, so there is a window where prices are asked for
        /// before the data exists and every trader answers null. During that window a "nobody buys
        /// this" result is not trustworthy, and the tooltip uses this to avoid caching it.
        /// </summary>
        public static bool AssortmentsLoading(IEftSession session)
        {
            var traders = GetTraders(session);
            if (traders == null) return false;

            foreach (var trader in traders)
            {
                if (trader.AssortmentLoading) return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the trader paying the most for an item, or null if nobody buys it.
        ///
        /// Returns the price alongside the trader so callers don't have to ask again. The old code
        /// called this once to build the confirmation total and then AGAIN inside the sell itself,
        /// doubling a full trader sweep for every item in a bulk sale.
        /// </summary>
        public static bool TryGetBestOffer(Item item, IEftSession session, out Trader bestTrader, out int bestPrice)
        {
            bestTrader = null;
            bestPrice = 0;

            var traders = GetTraders(session);
            if (traders == null)
            {
                Utils.SendError("Traders could not be loaded. Cannot sell.");
                return false;
            }

            foreach (var trader in traders)
            {
                // Matches on display name or id, so non-English clients (where LocalizedName is
                // translated) still have a way to blacklist reliably.
                if (Plugin.IsBlacklisted(trader.LocalizedName, trader.Id)) continue;

                var price = trader.GetUserItemPrice(item);
                if (price == null) continue;

                if (bestTrader == null || price.Value.Amount > bestPrice)
                {
                    bestTrader = trader;
                    bestPrice = price.Value.Amount;
                }
            }

            return bestTrader != null;
        }

        /// <summary>
        /// Finds the best price for this physical item alone. GetUserItemPrice recursively includes
        /// attached children, which is correct for a normal trader sale but would double-count
        /// those children while SmartSell is deciding a route for every component independently.
        /// </summary>
        public static bool TryGetBestStandaloneOffer(
            Item item, IEftSession session, out Trader bestTrader, out int bestPrice)
        {
            bestTrader = null;
            bestPrice = 0;

            var traders = GetTraders(session);
            if (traders == null) return false;

            foreach (var trader in traders)
            {
                if (Plugin.IsBlacklisted(trader.LocalizedName, trader.Id)) continue;
                // The Item overload recursively checks every attached child. SmartSell is asking
                // about this component alone, so only its own template belongs in the decision.
                if (!trader.Info.CanBuyItem(item.Template)) continue;

                // Reuse the trader's loaded supply prices rather than global handbook prices.
                var supplyData = Compat.Get<SupplyData>(trader, "_supplyData");
                if (supplyData == null) continue;

                var currencyId = CurrencyUtil.GetCurrencyId(trader.Settings.Currency);
                if (!trader.CurrencyCourses.TryGetValue(currencyId, out var currencyCourse)
                    || currencyCourse <= 0d)
                    continue;

                // Mirror GetUserItemPrice's calculation and rounding, replacing only its recursive
                // base-price call. The result remains denominated in this trader's payout currency.
                var value = PriceCalculator.CalculateBuyoutBasePriceForSingleItem(
                    item, 0, supplyData, trader.Settings.BuyerUp);
                value /= currencyCourse;
                value = trader.Info.ApplyPriceModifier(value);
                value = PriceCalculator.ApplyCustomPriceIfNeeded(item, value);

                var price = Convert.ToInt32(Math.Floor(value));
                if (price <= 0) continue;

                if (bestTrader == null || price > bestPrice)
                {
                    bestTrader = trader;
                    bestPrice = price;
                }
            }

            return bestTrader != null;
        }
    }

    /// <summary>
    /// Compares by object identity rather than by Equals.
    ///
    /// .NET provides a non-generic ReferenceEqualityComparer, but only from .NET 5 onwards - this
    /// plugin targets netstandard2.1, so it is supplied here.
    /// </summary>
    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();

        public bool Equals(T x, T y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
