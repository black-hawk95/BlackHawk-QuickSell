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
        private static readonly HashSet<string> AssortmentLoaded = new();

        /// <summary>
        /// Clears everything. Called when the session changes so a new profile does not inherit the
        /// previous one's traders.
        /// </summary>
        public static void Reset()
        {
            _traders = null;
            AssortmentLoaded.Clear();
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
            if (_traders != null && _traders.Length > 0) return _traders;
            if (session == null) return null;

            _traders = session.Traders
                .Where(trader => !trader.Settings.AvailableInRaid)
                .ToArray();

            return _traders;
        }

        /// <summary>
        /// Makes sure a trader's assortment is loaded before its prices are trusted.
        ///
        /// Tracked per trader ID so repeated sells don't re-fetch, but NOT treated as
        /// "once per session and never again" - see EnsureAssortmentsFor for the bulk case.
        /// </summary>
        public static void EnsureAssortment(Trader trader)
        {
            if (trader == null) return;
            if (trader.Id == LightkeeperTraderId) return;
            if (!AssortmentLoaded.Add(trader.Id)) return;

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
                if (Plugin.TradersBlacklist.Contains(trader.LocalizedName)) continue;

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
    }
}
