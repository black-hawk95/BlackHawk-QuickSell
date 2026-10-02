using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace QuickSell.Patches
{
    /// <summary>
    /// Pre-computed tooltip prices.
    ///
    /// Important rule: a tooltip NEVER builds a player container tree. Large backpacks/cases are
    /// warmed from the main menu (inventory-open is the fallback) and then maintained from EFT
    /// inventory mutation events.
    /// </summary>
    internal static class HoverPriceCache
    {
        private sealed class Entry
        {
            public Item Item;
            public readonly Dictionary<string, int> SelfTrader = new(StringComparer.Ordinal);
            public readonly Dictionary<string, int> TotalTrader = new(StringComparer.Ordinal);
            // v4.0.1 flea semantics: a hovered root uses one template average, while a stack
            // nested inside another item contributes average * StackObjectsCount.
            public double SelfFlea;
            public double SelfFleaAsChild;
            public double TotalFlea;
            public double TotalFleaAsChild;
            public bool SelfFleaComplete;
            public bool SelfTraderComplete;
            public int TotalFleaIncompleteCount;
            public int TotalTraderIncompleteCount;
        }

        private sealed class TraderPricingContext
        {
            public Trader Trader;
            public string TraderId;
            public SupplyData Supply;
            public double CurrencyCourse;
            public bool Ready;
            public readonly Dictionary<string, bool> CanBuyTemplate = new(StringComparer.Ordinal);
        }

        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo SupplyDataField = typeof(Trader)
            .GetFields(AnyInstance)
            .FirstOrDefault(f => typeof(SupplyData).IsAssignableFrom(f.FieldType));

        private static IEftSession _session;
        private static Trader[] _subscribedTraders = Array.Empty<Trader>();
        private static readonly List<Action> TraderEventUnsubscribers = new();
        private static bool _warming;
        private static bool _warmRequested;
        private static bool _warmComplete;
        private static bool _traderPriceDataReady;
        private static bool _fleaRebuildRequested;
        private static int _warmGeneration;
        private static SynchronizationContext _mainThreadContext;
        private static readonly object ReconcileLock = new();
        private static readonly HashSet<string> RebuildSelfIds = new(StringComparer.Ordinal);
        private static bool _reconcileQueued;
        private static string _reconcileReason;

        public static bool IsWarm => _warmComplete;

        public static void Reset()
        {
            UnsubscribeTraderEvents();
            Entries.Clear();
            _session = null;
            _warmRequested = false;
            _warmComplete = false;
            _traderPriceDataReady = false;
            _fleaRebuildRequested = false;
            _warming = false;
            _warmGeneration++;
            lock (ReconcileLock)
            {
                _reconcileQueued = false;
                _reconcileReason = null;
                RebuildSelfIds.Clear();
            }

            if (Plugin.DebugLogging)
                Plugin.DebugLog("PRECOMPUTE reset");
        }

        public static void OnMenuReady()
            => RequestWarm("menu-ready");

        public static void OnInventoryScreenShown()
            => RequestWarm("inventory-open");

        private static void RequestWarm(string trigger)
        {
            try
            {
                var session = ContextMenuPatch.GetSession();
                if (session == null) return;

                if (!ReferenceEquals(_session, session))
                {
                    Reset();
                    _session = session;
                }

                _mainThreadContext ??= SynchronizationContext.Current;

                var traderLoadTask = TraderService.EnsureAllAssortmentsAsync(session);
                SubscribeTraderEvents(session);

                if (_warmComplete)
                {
                    if (_fleaRebuildRequested && string.Equals(trigger, "inventory-open", StringComparison.Ordinal))
                        RebuildFleaTotals(trigger);
                    return;
                }

                _warmRequested = true;

                if (traderLoadTask == null || traderLoadTask.IsCompleted)
                {
                    _traderPriceDataReady = true;
                    TryWarmNow(trigger);
                    return;
                }

                // RefreshAssortment is asynchronous. Do not poll it from Update and do not build
                // prices on hover. Wait once, then post the precompute back to Unity's main thread.
                var generation = ++_warmGeneration;
                var expectedSession = session;
                traderLoadTask.ContinueWith(
                    _ => PostTraderReady(expectedSession, generation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE trigger={trigger} failed error={ex.Message}");
            }
        }

        private static void PostTraderReady(IEftSession expectedSession, int generation)
        {
            void CompleteOnMainThread()
            {
                if (generation != _warmGeneration || !ReferenceEquals(_session, expectedSession)) return;
                _traderPriceDataReady = true;
                TryWarmNow("assortments-ready");
            }

            var context = _mainThreadContext;
            if (context != null)
            {
                context.Post(_ => CompleteOnMainThread(), null);
                return;
            }

            // This should not happen when called from InventoryScreen.Show, but never touch live
            // EFT Item objects from a worker thread. A later inventory open will finish the warm.
            if (Plugin.DebugLogging)
                Plugin.DebugLog("PRECOMPUTE wait-complete but no main-thread context; deferred");
        }

        private static void SubscribeTraderEvents(IEftSession session)
        {
            var traders = TraderService.GetTraders(session) ?? Array.Empty<Trader>();

            var sameTraders = _subscribedTraders.Length == traders.Length &&
                _subscribedTraders.Zip(traders, (a, b) => ReferenceEquals(a, b)).All(x => x);
            if (sameTraders) return;

            if (_subscribedTraders.Length > 0)
            {
                Entries.Clear();
                _warmComplete = false;
                _traderPriceDataReady = false;
                _warmRequested = true;
                _warmGeneration++;
                TooltipPatch.Invalidate();

                if (Plugin.DebugLogging)
                    Plugin.DebugLog("PRECOMPUTE trader-set-changed");
            }

            UnsubscribeTraderEvents();
            _subscribedTraders = traders;

            foreach (var trader in _subscribedTraders)
            {
                if (trader == null) continue;
                TraderEventUnsubscribers.Add(trader.AssortmentLoadingChanged.Subscribe(OnTraderLoadingChanged));
                TraderEventUnsubscribers.Add(trader.AssortmentChanged.Subscribe(OnTraderAssortmentChanged));

                if (trader.Info != null)
                    trader.Info.OnLoyaltyChanged += OnTraderPricingChanged;
            }
        }

        private static void UnsubscribeTraderEvents()
        {
            foreach (var trader in _subscribedTraders)
            {
                if (trader?.Info == null) continue;
                try { trader.Info.OnLoyaltyChanged -= OnTraderPricingChanged; } catch { }
            }

            foreach (var unsubscribe in TraderEventUnsubscribers)
            {
                try { unsubscribe?.Invoke(); } catch { }
            }

            TraderEventUnsubscribers.Clear();
            _subscribedTraders = Array.Empty<Trader>();
        }

        private static void OnTraderLoadingChanged()
        {
            // This event is raised when EFT STARTS an assortment/supply refresh. Completion is
            // handled by the actual Task returned from RefreshAssortment, not by frame polling.
            if (_session == null) return;
            _traderPriceDataReady = false;
        }

        private static void OnTraderAssortmentChanged()
        {
            if (_session == null) return;
            _traderPriceDataReady = true;
            _warmRequested = true;
            TryWarmNow("assortment-changed");
        }

        private static void OnTraderPricingChanged()
        {
            if (_session == null) return;
            Entries.Clear();
            _warmComplete = false;
            _traderPriceDataReady = true;
            _warmRequested = true;
            TooltipPatch.Invalidate();
            TryWarmNow("trader-pricing-changed");
        }

        private static void TryWarmNow(string reason)
        {
            if (_warming || !_warmRequested || _session == null || !_traderPriceDataReady) return;
            WarmInventory(_session, reason);
        }

        private static void WarmInventory(IEftSession session, string reason)
        {
            if (_warming || session?.Profile?.Inventory == null) return;

            _warming = true;
            var sw = Stopwatch.StartNew();
            try
            {
                var traders = TraderService.GetTraders(session) ?? Array.Empty<Trader>();
                var traderContexts = CreateTraderContexts(traders);
                Entries.Clear();

                var seenRoots = new HashSet<string>(StringComparer.Ordinal);
                int nodeCount = 0;

                void BuildRoot(Item root)
                {
                    if (root == null) return;
                    var id = root.Id.ToString();
                    if (string.IsNullOrEmpty(id) || !seenRoots.Add(id)) return;
                    nodeCount += BuildTree(root, traderContexts);
                }

                if (!ModEnvironment.IsInRaid)
                    BuildRoot(session.Profile.Inventory.Stash);

                BuildRoot(session.Profile.Inventory.Equipment);

                _warmComplete = true;
                _warmRequested = false;
                _fleaRebuildRequested = false;

                sw.Stop();
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE done reason={reason} nodes={nodeCount} entries={Entries.Count} traders={traders.Length} ms={sw.Elapsed.TotalMilliseconds:0.00}");
            }
            catch (Exception ex)
            {
                _warmComplete = false;
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE failed reason={reason} error={ex}");
            }
            finally
            {
                _warming = false;
            }
        }

        /// <summary>
        /// Builds aggregate totals for a root in one enumeration. No item cloning and no recursive
        /// trader GetUserItemPrice calls are used.
        /// </summary>
        private static int BuildTree(Item root, TraderPricingContext[] traders)
        {
            if (root == null) return 0;

            var timing = Plugin.DebugLogging ? Stopwatch.StartNew() : null;
            var nodes = root.GetAllItems().ToList();
            if (nodes.Count == 0) nodes.Add(root);
            var scanMs = timing?.Elapsed.TotalMilliseconds ?? 0;

            // First build a self-price for every node.
            foreach (var node in nodes)
            {
                if (node == null) continue;
                var entry = BuildSelf(node, traders);
                Entries[node.Id.ToString()] = entry;
            }
            var selfMs = (timing?.Elapsed.TotalMilliseconds ?? 0) - scanMs;

            // Then add each self-price into its own total and every parent total. Depth is small,
            // so this stays cheap even for very large cases and avoids repeated tree enumeration.
            var rootId = root.Id.ToString();
            foreach (var node in nodes)
            {
                if (node == null || !Entries.TryGetValue(node.Id.ToString(), out var self)) continue;

                Item current = node;
                var seenParents = new HashSet<string>(StringComparer.Ordinal);
                while (current != null)
                {
                    var currentId = current.Id.ToString();
                    if (string.IsNullOrEmpty(currentId) || !seenParents.Add(currentId)) break;
                    if (!Entries.TryGetValue(currentId, out var total)) break;
                    AddSelfToTotal(total, self, +1, ReferenceEquals(current, node));
                    if (currentId == rootId) break;
                    current = current.Parent?.Container?.ParentItem;
                }
            }

            if (timing != null)
            {
                timing.Stop();
                var totalMs = timing.Elapsed.TotalMilliseconds;
                var aggregateMs = Math.Max(0, totalMs - scanMs - selfMs);
                Plugin.DebugLog(
                    $"PRECOMPUTE tree root={root.Id} type={root.GetType().Name} nodes={nodes.Count} " +
                    $"scanMs={scanMs:0.00} selfMs={selfMs:0.00} aggregateMs={aggregateMs:0.00} totalMs={totalMs:0.00}");
            }

            return nodes.Count;
        }

        private static Entry BuildSelf(Item item, TraderPricingContext[] traders)
        {
            double flea = 0;
            var fleaComplete = !Plugin.ShowFleaPriceInTooltip ||
                FleaPriceCache.TryGet(item.TemplateId.ToString(), out flea);

            var entry = new Entry
            {
                Item = item,
                SelfTraderComplete = true,
                SelfFleaComplete = fleaComplete
            };

            if (Plugin.ShowFleaPriceInTooltip && entry.SelfFleaComplete)
            {
                entry.SelfFlea = flea;
                entry.SelfFleaAsChild = flea * Math.Max(1, item.StackObjectsCount);
            }

            if (!Plugin.ShowTraderPriceInTooltip)
                return entry;

            foreach (var context in traders)
            {
                if (context?.Trader == null) continue;
                var price = GetSingleItemTraderPrice(context, item, out var complete);
                if (!complete) entry.SelfTraderComplete = false;
                entry.SelfTrader[context.TraderId] = price;
            }

            return entry;
        }

        private static void AddSelfToTotal(Entry total, Entry self, int sign, bool isSelfRoot)
        {
            var fleaContribution = isSelfRoot ? self.SelfFlea : self.SelfFleaAsChild;
            total.TotalFlea += sign * fleaContribution;
            total.TotalFleaAsChild += sign * self.SelfFleaAsChild;
            if (!self.SelfFleaComplete) total.TotalFleaIncompleteCount++;
            if (!self.SelfTraderComplete) total.TotalTraderIncompleteCount++;

            foreach (var kvp in self.SelfTrader)
            {
                total.TotalTrader.TryGetValue(kvp.Key, out var previous);
                long next = (long)previous + sign * (long)kvp.Value;
                total.TotalTrader[kvp.Key] = next <= 0 ? 0 : next >= int.MaxValue ? int.MaxValue : (int)next;
            }
        }

        private static TraderPricingContext[] CreateTraderContexts(Trader[] traders)
        {
            if (traders == null || traders.Length == 0)
                return Array.Empty<TraderPricingContext>();

            var contexts = new TraderPricingContext[traders.Length];
            for (int i = 0; i < traders.Length; i++)
            {
                var trader = traders[i];
                var context = new TraderPricingContext
                {
                    Trader = trader,
                    TraderId = trader?.Id.ToString() ?? string.Empty
                };

                try
                {
                    if (trader != null)
                    {
                        context.Supply = SupplyDataField?.GetValue(trader) as SupplyData;
                        if (context.Supply != null)
                        {
                            var currencyId = (string)CurrencyUtil.GetCurrencyId(trader.Settings.Currency);
                            if (!string.IsNullOrEmpty(currencyId) &&
                                context.Supply.CurrencyCourses != null &&
                                context.Supply.CurrencyCourses.TryGetValue(currencyId, out var course) &&
                                course > 0)
                            {
                                context.CurrencyCourse = course;
                                context.Ready = true;
                            }
                        }
                    }
                }
                catch
                {
                    context.Ready = false;
                }

                contexts[i] = context;
            }

            return contexts;
        }

        private static int GetSingleItemTraderPrice(TraderPricingContext context, Item item, out bool complete)
        {
            complete = true;
            try
            {
                var trader = context?.Trader;
                if (trader == null || item == null) return 0;

                // If this trader still has no price source after the awaited refresh, it simply
                // contributes no offer, matching v4.0.1's null GetUserItemPrice result.
                if (!context.Ready || context.Supply == null || context.CurrencyCourse <= 0)
                    return 0;

                var templateId = item.TemplateId.ToString();
                if (!context.CanBuyTemplate.TryGetValue(templateId, out var canBuy))
                {
                    canBuy = trader.Info.CanBuyItem(item.Template);
                    context.CanBuyTemplate[templateId] = canBuy;
                }

                if (!canBuy) return 0;

                double amount = PriceCalculator.CalculateBuyoutBasePriceForSingleItem(
                    item,
                    0,
                    context.Supply,
                    trader.Settings.BuyerUp);

                if (amount <= 0) return 0;

                amount /= context.CurrencyCourse;
                amount = trader.Info.ApplyPriceModifier(amount);
                amount = PriceCalculator.ApplyCustomPriceIfNeeded(item, amount);
                if (amount <= 0) return 0;

                var rounded = Convert.ToInt64(Math.Floor(amount));
                return rounded <= 0 ? 0 : rounded >= int.MaxValue ? int.MaxValue : (int)rounded;
            }
            catch
            {
                complete = false;
                return 0;
            }
        }

        public static void EnsureTransientForNonPlayer(Item item, IEftSession session)
        {
            if (item == null || session?.Profile == null) return;
            var id = item.Id.ToString();
            if (Entries.TryGetValue(id, out var existing))
            {
                if (existing.TotalTraderIncompleteCount == 0 || TraderService.AssortmentsLoading(session))
                    return;
            }

            var ownerId = item.Owner?.ID;
            if (ownerId != null && ownerId == session.Profile.ProfileId)
                return;

            var sw = Plugin.DebugLogging ? Stopwatch.StartNew() : null;
            try
            {
                var traders = TraderService.GetTraders(session) ?? Array.Empty<Trader>();
                var nodes = BuildTree(item, CreateTraderContexts(traders));
                sw?.Stop();

                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE transient id={item.Id} type={item.GetType().Name} nodes={nodes} ms={(sw?.Elapsed.TotalMilliseconds ?? 0):0.00}");
            }
            catch (Exception ex)
            {
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE transient failed id={item.Id} error={ex.Message}");
            }
        }

        public static bool TryGetBestTrader(Item item, IEftSession session, out Trader bestTrader, out int bestPrice, out bool complete)
        {
            bestTrader = null;
            bestPrice = 0;
            complete = false;

            if (item == null || session == null) return false;
            if (!Entries.TryGetValue(item.Id.ToString(), out var entry))
            {
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE miss kind=trader id={item.Id} type={item.GetType().Name}");
                return false;
            }

            complete = entry.TotalTraderIncompleteCount == 0;
            var traders = TraderService.GetTraders(session);
            if (traders == null) return false;

            foreach (var trader in traders)
            {
                if (trader == null || Plugin.IsBlacklisted(trader.LocalizedName, trader.Id)) continue;
                if (!entry.TotalTrader.TryGetValue(trader.Id.ToString(), out var price) || price <= 0) continue;
                if (bestTrader == null || price > bestPrice)
                {
                    bestTrader = trader;
                    bestPrice = price;
                }
            }

            return bestTrader != null;
        }

        public static bool TryGetFlea(Item item, out double price, out bool complete)
        {
            price = 0;
            complete = false;
            if (item == null) return false;

            if (!Entries.TryGetValue(item.Id.ToString(), out var entry))
            {
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE miss kind=flea id={item.Id} type={item.GetType().Name}");
                return false;
            }

            if (_fleaRebuildRequested)
            {
                complete = false;
                return false;
            }

            complete = entry.TotalFleaIncompleteCount == 0;
            price = entry.TotalFlea;
            return price > 0;
        }

        /// <summary>
        /// Inventory add/remove/refresh events happen inside EFT's transaction stack. Touching parent
        /// aggregates there can observe a half-moved tree. Queue one reconciliation to Unity's
        /// synchronization context instead; it runs after the current operation returns.
        /// </summary>
        public static void QueueInventoryReconcile(Item changedItem, bool rebuildSelf, string reason)
        {
            try
            {
                if (_session == null || !_warmComplete) return;

                var id = changedItem?.Id.ToString();
                lock (ReconcileLock)
                {
                    if (rebuildSelf && !string.IsNullOrEmpty(id))
                        RebuildSelfIds.Add(id);

                    if (!string.IsNullOrEmpty(reason))
                        _reconcileReason = string.IsNullOrEmpty(_reconcileReason)
                            ? reason
                            : _reconcileReason + "+" + reason;

                    if (_reconcileQueued) return;
                    _reconcileQueued = true;
                }

                _mainThreadContext ??= SynchronizationContext.Current;
                var context = _mainThreadContext;
                if (context == null)
                {
                    lock (ReconcileLock) _reconcileQueued = false;
                    if (Plugin.DebugLogging)
                        Plugin.DebugLog("PRECOMPUTE reconcile deferred: no main-thread context");
                    return;
                }

                context.Post(_ => ReconcileInventory(), null);
            }
            catch (Exception ex)
            {
                lock (ReconcileLock) _reconcileQueued = false;
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE reconcile-queue failed error={ex.Message}");
            }
        }

        private static void ReconcileInventory()
        {
            HashSet<string> rebuildSelf;
            string reason;
            lock (ReconcileLock)
            {
                _reconcileQueued = false;
                rebuildSelf = new HashSet<string>(RebuildSelfIds, StringComparer.Ordinal);
                RebuildSelfIds.Clear();
                reason = _reconcileReason ?? "mutation";
                _reconcileReason = null;
            }

            if (_session?.Profile?.Inventory == null || !_warmComplete) return;

            var sw = Plugin.DebugLogging ? Stopwatch.StartNew() : null;
            try
            {
                var traders = TraderService.GetTraders(_session) ?? Array.Empty<Trader>();
                var traderContexts = CreateTraderContexts(traders);
                var nodes = new List<Item>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                void Collect(Item root)
                {
                    if (root == null) return;
                    foreach (var node in root.GetAllItems())
                    {
                        if (node == null) continue;
                        var nodeId = node.Id.ToString();
                        if (string.IsNullOrEmpty(nodeId) || !seen.Add(nodeId)) continue;
                        nodes.Add(node);
                    }
                }

                if (!ModEnvironment.IsInRaid)
                    Collect(_session.Profile.Inventory.Stash);
                Collect(_session.Profile.Inventory.Equipment);

                int rebuilt = 0;
                foreach (var node in nodes)
                {
                    var id = node.Id.ToString();
                    if (!Entries.TryGetValue(id, out var entry) || rebuildSelf.Contains(id))
                    {
                        entry = BuildSelf(node, traderContexts);
                        Entries[id] = entry;
                        rebuilt++;
                    }
                    else
                    {
                        entry.Item = node;
                    }

                    ResetTotals(entry);
                }

                foreach (var node in nodes)
                {
                    var nodeId = node.Id.ToString();
                    if (!Entries.TryGetValue(nodeId, out var self)) continue;

                    Item current = node;
                    var seenParents = new HashSet<string>(StringComparer.Ordinal);
                    while (current != null)
                    {
                        var currentId = current.Id.ToString();
                        if (string.IsNullOrEmpty(currentId) || !seenParents.Add(currentId)) break;
                        if (!Entries.TryGetValue(currentId, out var total)) break;
                        AddSelfToTotal(total, self, +1, ReferenceEquals(current, node));
                        current = current.Parent?.Container?.ParentItem;
                    }
                }

                TooltipPatch.Invalidate();
                sw?.Stop();
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE reconcile reason={reason} nodes={nodes.Count} rebuiltSelf={rebuilt} ms={(sw?.Elapsed.TotalMilliseconds ?? 0):0.00}");
            }
            catch (Exception ex)
            {
                MarkDirty("reconcile", ex);
            }
        }

        private static void ResetTotals(Entry entry)
        {
            if (entry == null) return;
            entry.TotalTrader.Clear();
            entry.TotalFlea = 0;
            entry.TotalFleaAsChild = 0;
            entry.TotalFleaIncompleteCount = 0;
            entry.TotalTraderIncompleteCount = 0;
        }

        private static void ApplySelfDelta(
            Item item,
            double fleaRootDelta,
            double fleaChildDelta,
            Dictionary<string, int> traderDelta,
            int fleaIncompleteDelta,
            int traderIncompleteDelta)
        {
            Item current = item;
            bool isRoot = true;
            var seenParents = new HashSet<string>(StringComparer.Ordinal);
            while (current != null)
            {
                var currentId = current.Id.ToString();
                if (string.IsNullOrEmpty(currentId) || !seenParents.Add(currentId)) break;
                if (!Entries.TryGetValue(currentId, out var entry)) break;
                entry.TotalFlea = Math.Max(0, entry.TotalFlea + (isRoot ? fleaRootDelta : fleaChildDelta));
                entry.TotalFleaAsChild = Math.Max(0, entry.TotalFleaAsChild + fleaChildDelta);
                entry.TotalFleaIncompleteCount = Math.Max(0, entry.TotalFleaIncompleteCount + fleaIncompleteDelta);
                entry.TotalTraderIncompleteCount = Math.Max(0, entry.TotalTraderIncompleteCount + traderIncompleteDelta);

                foreach (var kvp in traderDelta)
                {
                    entry.TotalTrader.TryGetValue(kvp.Key, out var previous);
                    long next = (long)previous + kvp.Value;
                    entry.TotalTrader[kvp.Key] = next <= 0 ? 0 : next >= int.MaxValue ? int.MaxValue : (int)next;
                }

                isRoot = false;
                current = current.Parent?.Container?.ParentItem;
            }
        }

        private static void ApplyAggregateToParents(Item parent, Entry moved, int sign)
        {
            Item current = parent;
            var seenParents = new HashSet<string>(StringComparer.Ordinal);
            while (current != null)
            {
                var currentId = current.Id.ToString();
                if (string.IsNullOrEmpty(currentId) || !seenParents.Add(currentId)) break;
                if (!Entries.TryGetValue(currentId, out var entry)) break;

                entry.TotalFlea = Math.Max(0, entry.TotalFlea + sign * moved.TotalFleaAsChild);
                entry.TotalFleaAsChild = Math.Max(0, entry.TotalFleaAsChild + sign * moved.TotalFleaAsChild);
                entry.TotalFleaIncompleteCount = Math.Max(0, entry.TotalFleaIncompleteCount + sign * moved.TotalFleaIncompleteCount);
                entry.TotalTraderIncompleteCount = Math.Max(0, entry.TotalTraderIncompleteCount + sign * moved.TotalTraderIncompleteCount);
                foreach (var kvp in moved.TotalTrader)
                {
                    entry.TotalTrader.TryGetValue(kvp.Key, out var previous);
                    long next = (long)previous + sign * (long)kvp.Value;
                    entry.TotalTrader[kvp.Key] = next <= 0 ? 0 : next >= int.MaxValue ? int.MaxValue : (int)next;
                }

                current = current.Parent?.Container?.ParentItem;
            }
        }


        public static void InvalidateFlea()
        {
            foreach (var entry in Entries.Values)
            {
                entry.SelfFlea = 0;
                entry.SelfFleaAsChild = 0;
                entry.TotalFlea = 0;
                entry.TotalFleaAsChild = 0;
                entry.SelfFleaComplete = false;
                entry.TotalFleaIncompleteCount = 1;
            }

            _fleaRebuildRequested = true;
            TooltipPatch.Invalidate();
            if (Plugin.DebugLogging)
                Plugin.DebugLog($"PRECOMPUTE flea-invalidated entries={Entries.Count}");
        }
        public static void OnFleaPricesChanged()
        {
            // Flea callbacks may arrive from networking code. Do not touch live EFT Item objects
            // from that callback. Mark the aggregate dirty; it is rebuilt on the next inventory
            // screen open or native inventory mutation (both are main-thread game paths).
            _fleaRebuildRequested = true;
            TooltipPatch.Invalidate();

            if (Plugin.DebugLogging)
                Plugin.DebugLog("PRECOMPUTE flea-rebuild requested");
        }

        private static void RebuildFleaTotals(string reason)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                foreach (var entry in Entries.Values)
                {
                    entry.SelfFlea = 0;
                    entry.SelfFleaAsChild = 0;
                    entry.TotalFlea = 0;
                    entry.TotalFleaAsChild = 0;
                    entry.TotalFleaIncompleteCount = 0;
                    double flea = 0;
                    entry.SelfFleaComplete = entry.Item != null &&
                        FleaPriceCache.TryGet(entry.Item.TemplateId.ToString(), out flea);

                    if (entry.SelfFleaComplete)
                    {
                        entry.SelfFlea = flea;
                        entry.SelfFleaAsChild = flea * Math.Max(1, entry.Item.StackObjectsCount);
                    }
                }

                foreach (var self in Entries.Values.ToList())
                {
                    var current = self.Item;
                    bool isRoot = true;
                    var seenParents = new HashSet<string>(StringComparer.Ordinal);
                    while (current != null)
                    {
                        var currentId = current.Id.ToString();
                        if (string.IsNullOrEmpty(currentId) || !seenParents.Add(currentId)) break;
                        if (!Entries.TryGetValue(currentId, out var total)) break;
                        total.TotalFlea += isRoot ? self.SelfFlea : self.SelfFleaAsChild;
                        total.TotalFleaAsChild += self.SelfFleaAsChild;
                        if (!self.SelfFleaComplete) total.TotalFleaIncompleteCount++;
                        isRoot = false;
                        current = current.Parent?.Container?.ParentItem;
                    }
                }

                _fleaRebuildRequested = false;
                TooltipPatch.Invalidate();
                sw.Stop();
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE flea-rebuild reason={reason} entries={Entries.Count} ms={sw.Elapsed.TotalMilliseconds:0.00}");
            }
            catch (Exception ex)
            {
                if (Plugin.DebugLogging)
                    Plugin.DebugLog($"PRECOMPUTE flea-rebuild failed error={ex.Message}");
            }
        }

        private static void MarkDirty(string reason, Exception ex)
        {
            _warmComplete = false;
            _warmRequested = true;
            if (Plugin.DebugLogging)
                Plugin.DebugLog($"PRECOMPUTE dirty reason={reason} error={ex.Message}");
        }
    }
}