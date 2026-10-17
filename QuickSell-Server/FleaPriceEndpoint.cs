using Microsoft.AspNetCore.Http;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Ragfair;
using SPTarkov.Server.Core.Models.Enums;
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
    /// Prices prefer the average of the AI flea offers that are actually present on the market,
    /// rather than SPT's underlying dynamic/base price table. This makes the tooltip line up with
    /// the listings a player sees when they search the flea market.
    ///
    /// SPT's dynamic price table remains the fallback for templates that currently have no usable
    /// money-only offers. Weapons intentionally use the static base-weapon price, then the client
    /// adds the prices of the installed mods. This avoids counting weapon attachments twice.
    ///
    /// The result is briefly cached so several Fika clients connecting at once share one rebuild.
    /// A manual QuickSell refresh bypasses that cache.
    /// </summary>
    [Injectable(InjectionType = InjectionType.Singleton, TypePriority = OnLoadOrder.Routers + 1)]
    public class FleaPriceEndpoint(
        ISptLogger<FleaPriceEndpoint> logger,
        RagfairPriceService ragfairPriceService,
        RagfairOfferService ragfairOfferService,
        ItemHelper itemHelper,
        PaymentHelper paymentHelper) : IHttpListener
    {
        private const string RoutePrefix = "/quicksell";
        private const string RouteGetFleaPrices = "/quicksell/getFleaPrices";

        // Startup requests from multiple Fika clients can arrive together. Keep only a short cache;
        // the F12 manual refresh explicitly bypasses it.
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, double> _cached = new();
        private DateTime _nextUpdate = DateTime.MinValue;

        private struct PriceAccumulator
        {
            public double Sum;
            public int Count;
        }

        public bool CanHandle(HttpContext context)
        {
            return context.Request.Path.StartsWithSegments(RoutePrefix, StringComparison.OrdinalIgnoreCase);
        }

        public async Task HandleAsync(MongoId sessionId, HttpContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (!context.Request.Path.Equals(RouteGetFleaPrices, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                var forceRefresh = context.Request.Query.TryGetValue("force", out var forceValue)
                    && (string.Equals(forceValue.ToString(), "1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(forceValue.ToString(), "true", StringComparison.OrdinalIgnoreCase));

                var prices = await GetPricesAsync(forceRefresh, cancellationToken);
                await context.Response.WriteAsJsonAsync(prices, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Error($"[QuickSell] Failed to serve flea prices: {ex}");
                context.Response.StatusCode = 500;
            }
        }

        private async Task<Dictionary<string, double>> GetPricesAsync(
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            if (!forceRefresh && _nextUpdate > DateTime.UtcNow && _cached.Count > 0)
                return _cached;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!forceRefresh && _nextUpdate > DateTime.UtcNow && _cached.Count > 0)
                    return _cached;

                var stopwatch = Stopwatch.StartNew();

                // Fallback table. Mods such as Live Flea Prices update this SPT price source, so
                // templates without current offers still remain compatible with those mods.
                var fallbackPrices = ragfairPriceService.GetAllFleaPrices();

                // Build averages in ONE pass over the currently generated flea offers. This avoids
                // asking the offer service once for every template and keeps refresh work low.
                var offerAverages = new Dictionary<MongoId, PriceAccumulator>();
                var offers = ragfairOfferService.GetOffers();
                var countedOffers = 0;

                foreach (var offer in offers)
                {
                    if (offer == null || offer.IsTraderOffer() || offer.IsPlayerOffer())
                        continue;

                    if (offer.Items == null || offer.Items.Count == 0)
                        continue;

                    var moneyOnly = true;
                    if (offer.Requirements != null)
                    {
                        foreach (OfferRequirement requirement in offer.Requirements)
                        {
                            if (!paymentHelper.IsMoneyTpl(requirement.TemplateId))
                            {
                                moneyOnly = false;
                                break;
                            }
                        }
                    }

                    if (!moneyOnly)
                        continue;

                    var firstItem = offer.Items[0];
                    if (firstItem == null)
                        continue;

                    var itemCount = offer.SellInOnePiece.GetValueOrDefault(false)
                        ? firstItem.Upd?.StackObjectsCount ?? 1
                        : 1;

                    if (itemCount <= 0)
                        continue;

                    double? perItemPrice = offer.RequirementsCost / itemCount;
                    if (!perItemPrice.HasValue || !IsValidPrice(perItemPrice.Value))
                        continue;

                    var templateId = firstItem.Template;
                    offerAverages.TryGetValue(templateId, out var accumulator);
                    accumulator.Sum += perItemPrice.Value;
                    accumulator.Count++;
                    offerAverages[templateId] = accumulator;
                    countedOffers++;
                }

                var result = new Dictionary<string, double>(fallbackPrices.Count);
                var liveAverageCount = 0;
                var fallbackCount = 0;
                var weaponStaticCount = 0;

                foreach (var entry in fallbackPrices)
                {
                    var templateId = entry.Key;
                    double price = 0d;

                    // Show Me The Money follows the same rule: use the static weapon base price,
                    // then let the client add each installed mod separately. Using a generated
                    // assembled-weapon offer here would double-count its attachments.
                    if (itemHelper.IsOfBaseclass(templateId, BaseClasses.WEAPON))
                    {
                        price = ragfairPriceService.GetStaticPriceForItem(templateId) ?? 0d;
                        if (IsValidPrice(price))
                            weaponStaticCount++;
                    }
                    else if (offerAverages.TryGetValue(templateId, out var average)
                             && average.Count > 0)
                    {
                        price = Math.Round(average.Sum / average.Count);
                        if (IsValidPrice(price))
                            liveAverageCount++;
                    }

                    if (!IsValidPrice(price) && IsValidPrice(entry.Value))
                    {
                        price = entry.Value;
                        fallbackCount++;
                    }

                    if (IsValidPrice(price))
                        result[templateId.ToString()] = price;
                }

                // A custom item can theoretically have active offers before it appears in the
                // fallback table. Keep those usable too.
                foreach (var entry in offerAverages)
                {
                    var key = entry.Key.ToString();
                    if (result.ContainsKey(key) || entry.Value.Count <= 0)
                        continue;

                    var price = Math.Round(entry.Value.Sum / entry.Value.Count);
                    if (IsValidPrice(price))
                    {
                        result[key] = price;
                        liveAverageCount++;
                    }
                }

                _cached = result;
                _nextUpdate = DateTime.UtcNow.Add(CacheDuration);

                stopwatch.Stop();
                logger.Info(
                    $"[QuickSell] Built flea price table: {result.Count} items from {countedOffers} live offers " +
                    $"({liveAverageCount} live averages, {weaponStaticCount} weapon bases, {fallbackCount} fallbacks) " +
                    $"in {stopwatch.ElapsedMilliseconds}ms.");

                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static bool IsValidPrice(double price)
        {
            return price > 0d && !double.IsNaN(price) && !double.IsInfinity(price);
        }
    }
}
