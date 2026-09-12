using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace QuickSell.Patches
{
    /// <summary>
    /// Implements the default N-key workflow. Every physical item in the selected trees is routed
    /// independently: eligible parts whose estimated flea proceeds after commission beat the
    /// trader quote are listed first, then everything left is sold to a trader.
    /// </summary>
    internal static class SmartSell
    {
        /// <summary>Batch-local routing state; Parent records the original selected item tree.</summary>
        private sealed class PlannedItem
        {
            // Live inventory reference plus original ancestry for ordering and failure recovery.
            public Item Item;
            public PlannedItem Parent;
            public int Depth;
            // Routing can change on failure; completion and protection flags prevent item loss.
            public bool UseFlea;
            public bool FleaCompleted;
            public bool TraderBlocked;
            // Market prices are per unit; fee, net proceeds and trader quote cover the full stack.
            public double FleaUnitPrice;
            public int FleaListingUnitPrice;
            public int FleaFee;
            public int FleaNetProfit;
            public int TraderPrice;
        }

        /// <summary>
        /// UI/keybind entry point: resolve the inventory, build a plan and request one confirmation.
        /// Exceptions are caught here because an async-void UI callback cannot be awaited by EFT.
        /// </summary>
        public static async void Sell(Item item)
        {
            try
            {
                var session = ContextMenuPatch.GetSession();
                if (session == null)
                {
                    Utils.SendError("Session is not available");
                    return;
                }

                var app = ClientAppUtils.GetMainApp();
                var mainMenu = app == null
                    ? null
                    : Compat.Get<MainMenuShowOperation>(app, "mainMenuControllerClass");
                var inventoryController = mainMenu?.InventoryController;
                if (inventoryController == null || inventoryController is not ItemController itemController)
                {
                    Utils.SendError("Could not load inventory");
                    return;
                }

                var nodes = BuildPlanItems(ContextMenuPatch.GetItemsToSell(item));
                if (nodes.Count == 0) return;

                await AssignFleaItems(nodes, session, inventoryController, itemController);

                var fleaCount = nodes.Count(node => node.UseFlea);
                var traderCount = nodes.Count - fleaCount;
                var source = fleaCount == 0
                    ? "to the best-paying traders"
                    : traderCount == 0
                        ? "on the flea market"
                        : "by the most profitable route (flea after fee or best trader)";

                ContextMenuPatch.ConfirmWindow(
                    () => _ = ExecutePlan(nodes, session, inventoryController, itemController),
                    source,
                    nodes.Count);
            }
            catch (Exception ex)
            {
                Utils.SendError(ex.ToString());
                Plugin.LogSource?.LogWarning(ex.ToString());
            }
        }

        /// <summary>Expands selected roots into unique physical items without moving the inventory.</summary>
        private static List<PlannedItem> BuildPlanItems(IEnumerable<Item> selectedItems)
        {
            var selected = selectedItems?
                .Where(candidate => candidate != null)
                .GroupBy(candidate => candidate.Id)
                .Select(group => group.First())
                .ToList() ?? new List<Item>();

            // UIFixes can expose both a parent and one of its children. Keep only the outermost
            // selected roots or the same physical item would be scheduled twice.
            var roots = selected
                .Where(candidate => !selected.Any(other =>
                    !ReferenceEquals(candidate, other)
                    && other.GetAllItems().Skip(1).Any(child => child.Id == candidate.Id)))
                .ToList();

            var items = roots
                .SelectMany(root => root.GetAllItems())
                .GroupBy(candidate => candidate.Id)
                .Select(group => group.First())
                .ToList();

            var byId = items.ToDictionary(candidate => candidate.Id, candidate => new PlannedItem
            {
                Item = candidate
            });

            // Only retain parents inside this batch; the stash itself is never a planned sale.
            foreach (var node in byId.Values)
            {
                var parentItem = node.Item.Parent?.Container?.ParentItem;
                if (parentItem != null && byId.TryGetValue(parentItem.Id, out var parent))
                    node.Parent = parent;
            }

            foreach (var node in byId.Values)
            {
                var current = node.Parent;
                var guard = 0;
                // Bound traversal defensively if a mod supplies a malformed parent chain.
                while (current != null && guard++ < 64)
                {
                    node.Depth++;
                    current = current.Parent;
                }
            }

            return byId.Values.ToList();
        }

        /// <summary>
        /// Filters with EFT's selection rules, obtains prices, then reserves available slots in
        /// descending order of estimated additional proceeds over the standalone trader quote.
        /// </summary>
        private static async Task AssignFleaItems(
            List<PlannedItem> nodes,
            IEftSession session,
            OfflineInventoryController inventoryController,
            ItemController itemController)
        {
            var ragFair = session.RagFair;
            if (ragFair == null || !ragFair.Available) return;

            List<PlannedItem> eligible;
            using (var helper = new RagfairNewOfferContext(
                inventoryController.Inventory.Stash.Grids[0], itemController))
            {
                eligible = nodes.Where(node =>
                {
                    if (!helper.HighlightedAtRagfair(node.Item)) return false;
                    return RagFair.CanBeSelectedAtRagfair(node.Item, itemController, out _);
                }).ToList();
            }

            if (eligible.Count == 0) return;

            TraderService.EnsureAllAssortments(session);
            await WaitForTraderAssortments(session);

            var templates = eligible
                .Select(node => node.Item.TemplateId.ToString())
                .Distinct()
                .ToList();

            await Task.WhenAll(templates.Select(templateId => EnsureFleaPrice(ragFair, templateId)));

            eligible = eligible
                .Where(node =>
                {
                    if (!FleaPriceCache.TryGet(node.Item.TemplateId, out var price) || price <= 0d)
                        return false;

                    node.FleaUnitPrice = FleaPriceCache.GetConditionAdjustedUnitPrice(
                        node.Item, price);
                    node.FleaListingUnitPrice = (int)Math.Ceiling(
                        node.FleaUnitPrice / 100d * Plugin.AvgPricePercent);

                    // Use the same per-unit requirement and stack count as RagfairAddOffer below.
                    // EFT computes the fee from the item's current tree and profile bonuses.
                    var count = Math.Max(1, node.Item.StackObjectsCount);
                    node.FleaFee = (int)Math.Ceiling(PriceCalculator.CalculateTaxPrice(
                        node.Item, count, node.FleaListingUnitPrice, false));
                    node.FleaNetProfit = node.FleaListingUnitPrice * count - node.FleaFee;

                    if (TraderService.TryGetBestStandaloneOffer(
                            node.Item, session, out _, out var traderPrice))
                        node.TraderPrice = traderPrice;

                    // A flea slot is used only when its net proceeds beat the best trader.
                    return node.FleaNetProfit > node.TraderPrice && node.FleaNetProfit > 0;
                })
                // When slots are limited, maximize the batch's total gain over trader sales.
                .OrderByDescending(node => node.FleaNetProfit - node.TraderPrice)
                .ThenByDescending(node => node.Depth)
                .ToList();

            // Ignoring the local limit does not bypass server validation: rejected listings still
            // enter the trader phase through ExecutePlan's normal failure handling.
            var availableSlots = Plugin.IgnoreFleaCapacity
                ? int.MaxValue
                : Math.Max(0, ragFair.GetMaxOffersCount(ragFair.MyRating) - ragFair.MyOffersCount);

            foreach (var node in eligible.Take(availableSlots))
                node.UseFlea = true;
        }

        /// <summary>
        /// Gives lazy trader requests up to ten seconds to finish without blocking Unity's thread.
        /// This is a bounded wait, not a guarantee that every trader returned a usable quote.
        /// </summary>
        private static async Task WaitForTraderAssortments(IEftSession session)
        {
            const int maximumWaits = 200;
            for (var attempt = 0; attempt < maximumWaits
                                  && TraderService.AssortmentsLoading(session); attempt++)
                await Task.Delay(50);
        }

        /// <summary>Adapts the shared callback cache to a task; cached hits complete immediately.</summary>
        private static Task<double> EnsureFleaPrice(RagFair ragFair, string templateId)
        {
            var completion = new TaskCompletionSource<double>();
            FleaPriceCache.RequestIfMissing(ragFair, templateId, value => completion.TrySetResult(value));
            return completion.Task;
        }

        /// <summary>
        /// Separates cross-route children, awaits all flea operations, then awaits trader sales.
        /// Serial callbacks let EFT apply each inventory change before its parent is processed.
        /// </summary>
        private static async Task ExecutePlan(
            List<PlannedItem> nodes,
            IEftSession session,
            OfflineInventoryController inventoryController,
            ItemController itemController)
        {
            try
            {
                // A trader-bound child must be detached before its flea-bound parent is listed,
                // otherwise the server includes that child in the parent's offer automatically.
                var boundaries = nodes
                    .Where(node => !node.UseFlea && node.Parent?.UseFlea == true)
                    .OrderByDescending(node => node.Depth)
                    .ToList();

                foreach (var boundary in boundaries)
                {
                    if (await MoveToStash(boundary, inventoryController, itemController)) continue;

                    // No sale has happened yet. Falling the whole batch back to traders is safer
                    // than letting a flea parent silently carry an inseparable child with it.
                    foreach (var node in nodes) node.UseFlea = false;
                    Utils.SendNotification(
                        "QuickSell could not safely separate every item; using traders for this batch.");
                    break;
                }

                var soundPlayed = false;

                // Deepest first: once a child has been listed, its parent no longer contains it.
                foreach (var node in nodes.Where(candidate => candidate.UseFlea)
                             .OrderByDescending(candidate => candidate.Depth).ToList())
                {
                    if (!node.UseFlea) continue;

                    var price = node.FleaListingUnitPrice;
                    var result = await AddFleaOffer(session, node.Item, price);

                    if (result?.Succeed == true)
                    {
                        node.FleaCompleted = true;
                        PlaySoundOnce(ref soundPlayed);
                    }
                    else
                    {
                        node.UseFlea = false;

                        // Protect a still-pending flea parent from absorbing this failed child.
                        if (node.Parent?.UseFlea == true
                            && !await MoveToStash(node, inventoryController, itemController))
                        {
                            for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
                                ancestor.UseFlea = false;
                        }
                    }

                    await Task.Yield();
                }

                TraderService.EnsureAllAssortments(session);
                var unsold = 0;

                // Every remaining component is priced only after its children have gone, so each
                // part can choose its own best trader without double-counting a child.
                foreach (var node in nodes.Where(candidate => !candidate.FleaCompleted)
                             .OrderByDescending(candidate => candidate.Depth))
                {
                    if (node.TraderBlocked)
                    {
                        unsold++;
                        continue;
                    }

                    if (!TraderService.TryGetBestOffer(
                            node.Item, session, out var trader, out var price))
                    {
                        unsold++;
                        await ProtectTraderParent(node, inventoryController, itemController);
                        continue;
                    }

                    var result = await SellToTrader(trader, node.Item, price);
                    if (result?.Succeed == true)
                        PlaySoundOnce(ref soundPlayed);
                    else
                    {
                        unsold++;
                        await ProtectTraderParent(node, inventoryController, itemController);
                    }

                    await Task.Yield();
                }

                TooltipPatch.Invalidate();
                if (unsold > 0)
                    Utils.SendError($"QuickSell could not sell {unsold} item(s).");
            }
            catch (Exception ex)
            {
                Utils.SendError(ex.ToString());
                Plugin.LogSource?.LogWarning(ex.ToString());
            }
        }

        /// <summary>
        /// A child that no trader bought must not disappear later as an unpaid part of its
        /// parent's sale. Move it aside; if that is impossible, leave every ancestor unsold too.
        /// </summary>
        private static async Task ProtectTraderParent(
            PlannedItem node,
            OfflineInventoryController inventoryController,
            ItemController itemController)
        {
            if (node.Parent == null || node.Parent.FleaCompleted) return;
            if (await MoveToStash(node, inventoryController, itemController)) return;

            for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.TraderBlocked = true;
        }

        /// <summary>
        /// Moves a still-attached child into a free stash location using an EFT network operation.
        /// An already-separated child is a success; unavailable space or an invalid move is not.
        /// </summary>
        private static async Task<bool> MoveToStash(
            PlannedItem node,
            OfflineInventoryController inventoryController,
            ItemController itemController)
        {
            var liveParent = node.Item.Parent?.Container?.ParentItem;
            if (liveParent == null || node.Parent == null || liveParent.Id != node.Parent.Item.Id)
                return true;

            var address = inventoryController.Inventory.Stash.Grid.FindLocationForItem(node.Item);
            if (address == null) return false;

            // Validate with a simulated operation before submitting it to the inventory controller.
            var operation = ItemManipulator.Move(node.Item, address, itemController, true);
            if (operation.Failed) return false;

            var result = await itemController.TryRunNetworkTransaction(operation, null);
            return result?.Succeed == true;
        }

        /// <summary>Lists one physical item/stack with a per-unit RUB requirement and awaits EFT.</summary>
        private static Task<IResult> AddFleaOffer(IEftSession session, Item item, int price)
        {
            var completion = new TaskCompletionSource<IResult>();
            var requirements = new[]
            {
                new BarterTemplate
                {
                    _tpl = (string)CurrencyUtil.GetCurrencyId(ECurrencyType.RUB),
                    count = price,
                    onlyFunctional = true
                }
            };

            try
            {
                session.RagfairAddOffer(
                    false, new[] { item.Id }, requirements,
                    new Callback(result => completion.TrySetResult(result)));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuickSell: flea offer failed: {ex.Message}");
                completion.TrySetResult(null);
            }

            return completion.Task;
        }

        /// <summary>Submits the selected trader's quoted price for the item's entire current stack.</summary>
        private static Task<IResult> SellToTrader(Trader trader, Item item, int price)
        {
            var completion = new TaskCompletionSource<IResult>();
            var interactions = Compat.Get<ITradingSession>(trader, "_trading");
            if (interactions == null)
            {
                completion.TrySetResult(null);
                return completion.Task;
            }

            try
            {
                interactions.ConfirmSell(
                    trader.Id,
                    new[] { new TradingItemReference { Item = item, Count = item.StackObjectsCount } },
                    price,
                    new Callback(result => completion.TrySetResult(result)));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuickSell: trader sale failed: {ex.Message}");
                completion.TrySetResult(null);
            }

            return completion.Task;
        }

        /// <summary>Plays feedback only for the first successful transaction in this batch.</summary>
        private static void PlaySoundOnce(ref bool played)
        {
            if (played) return;
            played = true;
            Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.TradeOperationComplete);
        }
    }
}
