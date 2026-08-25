using Microsoft.AspNetCore.Http;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services.Ragfair;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BlackHawk.QuickSell.Server
{
    /// <summary>
    /// Serves the whole flea price table in one response.
    ///
    /// The client needs prices for arbitrary items while in a raid, where it must not make network
    /// calls. Asking for them one template at a time would be thousands of round trips, so the
    /// entire table is handed over in a single request at startup instead.
    ///
    /// The result is cached here, so N Fika players connecting costs one computation rather than N.
    ///
    /// Notes on the 4.1 API, since several things moved:
    ///  - RagfairPriceService is now in Services.Ragfair, not Services.
    ///  - ISptLogger moved out of Core into SPTarkov.Common.Models.Logging.
    ///  - Load order is expressed relative to OnLoadOrder.Routers. A route on a URL SPT does not
    ///    already handle goes ABOVE it: there is nothing of SPT's to order against, and no reason
    ///    to sit below. Overriding an existing SPT route is the case that needs Routers - 1.
    /// </summary>
    [Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.Routers + 1)]
    public class FleaPriceEndpoint(
        ISptLogger<FleaPriceEndpoint> logger,
        RagfairPriceService ragfairPriceService) : IHttpListener
    {
        private const string RoutePrefix = "/quicksell";
        private const string RouteGetFleaPrices = "/quicksell/getFleaPrices";

        // Long by design. Prices barely move on a single-player server and the client only asks
        // once per session anyway. The F12 refresh button covers anyone running something like
        // LiveFleaPrices who wants to pick up changes immediately.
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, double> _cached = new();
        private DateTime _nextUpdate = DateTime.MinValue;

        // 4.1 signature: no sessionId here (it was CanHandle(MongoId, HttpContext) in 4.0).
        public bool CanHandle(HttpContext context)
        {
            return context.Request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase);
        }

        // 4.1 signature: renamed from Handle to HandleAsync and given a CancellationToken, which is
        // sourced from HttpContext.RequestAborted and cancels if the client disconnects.
        public async Task HandleAsync(MongoId sessionId, HttpContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (!context.Request.Path.Equals(RouteGetFleaPrices, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var prices = await GetPricesAsync(cancellationToken);
                await context.Response.WriteAsJsonAsync(prices, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The client went away. Cancellation is not an error, so let it propagate rather
                // than logging it as a failure.
                throw;
            }
            catch (Exception ex)
            {
                logger.Error($"[QuickSell] Failed to serve flea prices: {ex.Message}");
                context.Response.StatusCode = 500;
            }
        }

        private async Task<Dictionary<string, double>> GetPricesAsync(CancellationToken cancellationToken)
        {
            if (_nextUpdate > DateTime.UtcNow && _cached.Count > 0) return _cached;

            // SemaphoreSlim rather than lock: this path is async, and holding a monitor across an
            // await is not safe. Several Fika clients can connect at the same moment, and without
            // this each would rebuild the table.
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_nextUpdate > DateTime.UtcNow && _cached.Count > 0) return _cached;

                var stopwatch = Stopwatch.StartNew();

                var source = ragfairPriceService.GetAllFleaPrices();
                var result = new Dictionary<string, double>(source.Count);

                foreach (var entry in source)
                {
                    var price = entry.Value;
                    if (price > 0d && !double.IsNaN(price) && !double.IsInfinity(price))
                        result[entry.Key.ToString()] = price;
                }

                _cached = result;
                _nextUpdate = DateTime.UtcNow.Add(CacheDuration);

                stopwatch.Stop();
                logger.Info($"[QuickSell] Built flea price table: {result.Count} items in {stopwatch.ElapsedMilliseconds}ms.");

                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
