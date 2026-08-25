using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuickSell.Patches
{
    /// <summary>
    /// Fetches the bulk flea price table from the QuickSell server mod.
    ///
    /// Deliberately a ONE-SHOT fetch at startup rather than a polling loop.
    ///
    /// On a Fika server every connected player runs this client. A 30-second poll would mean each
    /// player generating two requests a minute forever, multiplied by the player count, and some of
    /// those players are on other continents where that round trip is expensive. Fetching once per
    /// session means ten players cost ten requests total, all at menu time where latency is
    /// invisible. A manual refresh is available in the F12 menu for anyone who wants fresher
    /// numbers, which keeps the choice with the user instead of on a timer.
    ///
    /// Never called in raid, and never called on a headless client.
    /// </summary>
    internal static class FleaPriceClient
    {
        private const string Route = "/quicksell/getFleaPrices";

        private static bool _fetchInFlight;

        /// <summary>
        /// Requests the price table. Safe to call when the server mod is absent - it logs and the
        /// mod carries on with per-hover lookups instead.
        /// </summary>
        public static void FetchAsync(Action onComplete = null)
        {
            if (_fetchInFlight) return;
            if (ModEnvironment.IsHeadlessClient) return;

            _fetchInFlight = true;

            Task.Run(() =>
            {
                try
                {
                    var json = RequestHandler.GetJson(Route);

                    if (string.IsNullOrEmpty(json))
                    {
                        Plugin.LogSource?.LogInfo(
                            "QuickSell: no flea price table returned. The server mod is probably not " +
                            "installed - flea prices will be looked up per item instead, and will not " +
                            "be available in raid.");
                        return;
                    }

                    var prices = JsonConvert.DeserializeObject<Dictionary<string, double>>(json);

                    if (prices == null || prices.Count == 0)
                    {
                        Plugin.LogSource?.LogWarning("QuickSell: flea price table was empty.");
                        return;
                    }

                    FleaPriceCache.LoadBulk(prices);

                    // Any tooltip built before the table arrived has no flea line in it, so those
                    // cached results are now wrong and must be discarded.
                    TooltipPatch.Invalidate();

                    onComplete?.Invoke();
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuickSell: could not fetch the flea price table ({ex.Message}). " +
                        "Falling back to per-item lookups outside raid.");
                }
                finally
                {
                    _fetchInFlight = false;
                }
            });
        }

        /// <summary>Clears the cache and re-fetches. Wired to the F12 refresh button.</summary>
        public static void ForceRefresh()
        {
            if (ModEnvironment.IsInRaid)
            {
                Utils.SendError("Flea prices cannot be refreshed during a raid");
                return;
            }

            FleaPriceCache.Clear();
            FetchAsync();
        }
    }
}
