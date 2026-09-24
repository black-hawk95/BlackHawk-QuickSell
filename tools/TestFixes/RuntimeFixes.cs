using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace QuickSell.TestFixes
{
    public static class RuntimeFixes
    {
        private static object _playPerItemEntry;
        private static object _allowFleaSalesEntry;
        private static Func<bool> _refreshHotkeyIsDown;
        private static Assembly _quickSellAssembly;
        private static Assembly _gameAssembly;
        private static MethodInfo _cloneItemMethod;
        private static MethodInfo _getAllItemsMethod;
        private static bool _getAllItemsResolved;
        private static MethodInfo _fleaTryGetMethod;
        private static readonly Dictionary<string, string> TooltipItemTrees = new Dictionary<string, string>();
        private static bool _globalFleaPatchInstalled;
        private static bool _globalFleaPatchWarningLogged;
        private static DateTime _nextFleaPatchAttempt;

        public static void Initialize(object plugin)
        {
            try
            {
                if (plugin == null) return;

                var configProp = FindProperty(plugin.GetType(), "Config", true);
                var config = configProp?.GetValue(plugin);
                if (config == null) return;

                var bind = config.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.Name == "Bind" && m.IsGenericMethodDefinition)
                    .FirstOrDefault(m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 4
                            && p[0].ParameterType == typeof(string)
                            && p[1].ParameterType == typeof(string)
                            && p[3].ParameterType == typeof(string);
                    });

                if (bind == null) return;

                _playPerItemEntry = bind.MakeGenericMethod(typeof(bool)).Invoke(
                    config,
                    new object[]
                    {
                        "1. Selling",
                        "Play sell sound for each item",
                        false,
                        "Off = play the trade-complete sound once for the whole batch. On = play it once for every item that sells successfully."
                    });

                try
                {
                    _allowFleaSalesEntry = bind.MakeGenericMethod(typeof(bool)).Invoke(
                        config,
                        new object[]
                        {
                            "1. Selling",
                            "Allow flea market selling",
                            true,
                            "Off blocks all new flea listings, including the normal flea market page, QuickSell right-click menu and flea keybind. Buying remains available."
                        });
                }
                catch { _allowFleaSalesEntry = null; }

                try
                {
                    var shortcutType = FindType("BepInEx.Configuration.KeyboardShortcut");
                    var keyType = FindType("UnityEngine.KeyCode");
                    if (shortcutType != null && keyType != null)
                    {
                        var none = Enum.Parse(keyType, "None");
                        var ctor = shortcutType.GetConstructor(new[] { keyType, keyType.MakeArrayType() });
                        var unassigned = ctor?.Invoke(new[] { none, Array.CreateInstance(keyType, 0) });
                        if (unassigned != null)
                        {
                            var entry = bind.MakeGenericMethod(shortcutType).Invoke(
                                config,
                                new object[]
                                {
                                    "2. Tooltips",
                                    "Refresh flea prices (key)",
                                    unassigned,
                                    "Optional hotkey for Refresh flea prices now. Works in the menu, not during a raid or while typing. Unassigned by default."
                                });

                            // Compile the value getter and IsDown call once. The inventory UI
                            // checks this every frame, so reflection must stay off the hot path.
                            var value = Expression.Property(Expression.Constant(entry), "Value");
                            var isDown = shortcutType.GetMethod("IsDown", Type.EmptyTypes);
                            if (isDown != null)
                                _refreshHotkeyIsDown = Expression.Lambda<Func<bool>>(
                                    Expression.Call(value, isDown)).Compile();
                        }
                    }
                }
                catch { _refreshHotkeyIsDown = null; }
            }
            catch
            {
                _playPerItemEntry = null;
            }
        }

        public static bool AllowFleaSales()
        {
            try
            {
                if (_allowFleaSalesEntry == null) return true;
                var value = FindProperty(_allowFleaSalesEntry.GetType(), "Value", true)?.GetValue(_allowFleaSalesEntry);
                return value is bool b ? b : true;
            }
            catch
            {
                return true;
            }
        }

        public static bool ShouldShowFleaEntry(bool originalSetting)
            => originalSetting && AllowFleaSales();

        public static void EnsureGlobalFleaBlockInstalled()
        {
            if (_globalFleaPatchInstalled || DateTime.UtcNow < _nextFleaPatchAttempt) return;
            _nextFleaPatchAttempt = DateTime.UtcNow.AddSeconds(2);

            try
            {
                var context = QuickSellAssembly?.GetType("QuickSell.Patches.ContextMenuPatch", false);
                var getSession = context?.GetMethod("GetSession", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var session = getSession?.Invoke(null, null);
                if (session == null) return;

                var harmonyType = FindType("HarmonyLib.Harmony");
                var harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                if (harmonyType == null || harmonyMethodType == null) return;

                var prefixMethod = typeof(RuntimeFixes).GetMethod(nameof(BlockGlobalFleaOfferPrefix));
                var prefix = Activator.CreateInstance(harmonyMethodType, new object[] { prefixMethod });
                var harmony = Activator.CreateInstance(harmonyType, new object[] { "com.blackhawk.quicksell.flea-offer-block" });
                var patch = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Patch" && m.GetParameters().Length == 5 &&
                        m.GetParameters()[0].ParameterType == typeof(MethodBase));
                if (patch == null) return;

                var targets = session.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name.EndsWith("RagfairAddOffer", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Any(p => typeof(Delegate).IsAssignableFrom(p.ParameterType)))
                    .GroupBy(m => m.Module.ModuleVersionId + ":" + m.MetadataToken)
                    .Select(group => group.First())
                    .ToList();

                foreach (var target in targets)
                    patch.Invoke(harmony, new[] { target, prefix, null, null, null });

                if (targets.Count > 0)
                {
                    _globalFleaPatchInstalled = true;
                    LogQuickSellInfo("Global flea listing guard installed on " + targets.Count + " session method(s).");
                }
                else if (!_globalFleaPatchWarningLogged)
                {
                    _globalFleaPatchWarningLogged = true;
                    LogQuickSellWarning("Could not find the game's RagfairAddOffer method; global flea blocking is unavailable.");
                }
            }
            catch (Exception ex)
            {
                LogQuickSellWarning("Could not install global flea offer guard: " + ex.Message);
            }
        }

        // The session method is shared by QuickSell and the normal flea listing page.
        // Return a failed callback so the native screen can leave its waiting state.
        public static bool BlockGlobalFleaOfferPrefix(object[] __args)
        {
            if (AllowFleaSales()) return true;

            try
            {
                var callback = __args?.OfType<Delegate>().LastOrDefault();
                var resultType = callback?.GetType().GetMethod("Invoke")?.GetParameters().FirstOrDefault()?.ParameterType;
                if (resultType != null)
                {
                    var failureType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(GetLoadableTypes)
                        .FirstOrDefault(t => t.Name == "FailedResult" && resultType.IsAssignableFrom(t));
                    var failure = failureType?.GetConstructor(new[] { typeof(string), typeof(int) })
                        ?.Invoke(new object[] { "Flea selling is disabled in F12", 0 });
                    if (failure != null) callback.DynamicInvoke(failure);
                    else LogQuickSellWarning("Flea offer blocked but a failed callback result was unavailable.");
                }

                var utils = QuickSellAssembly?.GetType("QuickSell.Patches.Utils", false);
                utils?.GetMethod("SendError", BindingFlags.Static | BindingFlags.Public)
                    ?.Invoke(null, new object[] { "Flea selling is disabled in F12" });
            }
            catch (Exception ex)
            {
                LogQuickSellWarning("Flea offer was blocked: " + ex.Message);
            }

            return false;
        }

        private static void LogQuickSellWarning(string message)
        {
            try
            {
                var plugin = QuickSellAssembly?.GetType("QuickSell.Plugin", false);
                var logger = plugin?.GetField("LogSource", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                logger?.GetType().GetMethod("LogWarning", new[] { typeof(object) })?.Invoke(logger, new object[] { message });
            }
            catch { }
        }

        private static void LogQuickSellInfo(string message)
        {
            try
            {
                var plugin = QuickSellAssembly?.GetType("QuickSell.Plugin", false);
                var logger = plugin?.GetField("LogSource", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                logger?.GetType().GetMethod("LogInfo", new[] { typeof(object) })?.Invoke(logger, new object[] { message });
            }
            catch { }
        }

        public static bool TryRefreshFleaPricesHotkey()
        {
            try
            {
                if (_refreshHotkeyIsDown == null || !_refreshHotkeyIsDown()) return false;

                var environment = QuickSellAssembly?.GetType("QuickSell.Patches.ModEnvironment", false);
                if (FindProperty(environment, "IsInRaid", true)?.GetValue(null) is bool inRaid && inRaid)
                    return false;

                var keybinds = QuickSellAssembly?.GetType("QuickSell.Patches.KeybindPatches", false);
                var textbox = keybinds?.GetMethod("TextboxActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (textbox?.Invoke(null, null) is bool typing && typing) return false;

                var tooltip = QuickSellAssembly?.GetType("QuickSell.Patches.TooltipPatch", false);
                tooltip?.GetMethod("Invalidate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(null, null);

                var client = QuickSellAssembly?.GetType("QuickSell.Patches.FleaPriceClient", false);
                client?.GetMethod("ForceRefresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(null, null);
                return true;
            }
            catch { return false; }
        }

        public static bool PlaySellSoundPerItem()
        {
            try
            {
                if (_playPerItemEntry == null) return false;
                var value = FindProperty(_playPerItemEntry.GetType(), "Value", true)?.GetValue(_playPerItemEntry);
                return value is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldSkipTooltip()
        {
            try
            {
                var type = FindType("EFT.UI.ItemUiContext");
                if (type == null) return false;

                object instance = FindProperty(type, "Instance", true)?.GetValue(null);
                if (instance == null)
                {
                    var field = FindField(type, "Instance", true);
                    instance = field?.GetValue(null);
                }

                if (instance == null) return false;

                var context = FindProperty(instance.GetType(), "CurrentItemContext", true)?.GetValue(instance);
                if (context == null) return false;

                var viewType = FindProperty(context.GetType(), "ViewType", true)?.GetValue(context);
                return string.Equals(viewType?.ToString(), "TradingTrader", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static void InvalidateTooltipIfContentsChanged()
        {
            try
            {
                var uiType = FindType("EFT.UI.ItemUiContext");
                var ui = uiType == null ? null : FindProperty(uiType, "Instance", true)?.GetValue(null);
                if (ui == null && uiType != null)
                    ui = FindField(uiType, "Instance", true)?.GetValue(null);
                var item = GetMemberValue(GetMemberValue(ui, "CurrentItemContext"), "Item");
                var id = GetMemberValue(item, "Id")?.ToString();
                if (string.IsNullOrEmpty(id)) return;

                // A tooltip is cached by root ID. Its attachments can change while that
                // root ID stays the same, so include every child ID and stack count.
                var tree = string.Join("|", EnumerateItemTree(item).Select(node =>
                    GetMemberValue(node, "Id") + ":" +
                    GetMemberValue(node, "TemplateId") + ":" +
                    GetMemberValue(node, "StackObjectsCount")));

                if (TooltipItemTrees.TryGetValue(id, out var previous))
                {
                    if (previous == tree) return;

                    var tooltipType = QuickSellAssembly?.GetType("QuickSell.Patches.TooltipPatch", false);
                    var resultCache = tooltipType == null ? null :
                        FindField(tooltipType, "ResultCache", true)?.GetValue(null) as IDictionary;
                    resultCache?.Remove(id);
                }

                if (TooltipItemTrees.Count >= 4000)
                {
                    TooltipItemTrees.Clear();
                    var tooltipType = QuickSellAssembly?.GetType("QuickSell.Patches.TooltipPatch", false);
                    tooltipType?.GetMethod("Invalidate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.Invoke(null, null);
                }

                TooltipItemTrees[id] = tree;
            }
            catch { /* A tooltip must still open even if cache invalidation fails. */ }
        }

        public static object[] GetBestTraderOffer(object item, object session)
        {
            object bestTrader = null;
            int bestPrice = 0;

            try
            {
                if (item == null || session == null)
                    return new object[] { null, 0, false };

                var qs = QuickSellAssembly;
                var traderService = qs?.GetType("QuickSell.Patches.TraderService", false);
                var plugin = qs?.GetType("QuickSell.Plugin", false);
                if (traderService == null)
                    return new object[] { null, 0, false };

                var getTraders = traderService.GetMethod(
                    "GetTraders",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                var isBlacklisted = plugin?.GetMethod(
                    "IsBlacklisted",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                var traders = getTraders?.Invoke(null, new[] { session }) as IEnumerable;
                if (traders == null)
                    return new object[] { null, 0, false };

                foreach (var trader in traders)
                {
                    if (trader == null) continue;

                    if (isBlacklisted != null)
                    {
                        var name = GetMemberValue(trader, "LocalizedName")?.ToString();
                        var id = GetMemberValue(trader, "Id")?.ToString();
                        try
                        {
                            if ((bool)isBlacklisted.Invoke(null, new object[] { name, id }))
                                continue;
                        }
                        catch { }
                    }

                    var price = GetCompositeTraderPrice(trader, item);
                    if (price <= 0) continue;

                    if (bestTrader == null || price > bestPrice)
                    {
                        bestTrader = trader;
                        bestPrice = price;
                    }
                }
            }
            catch
            {
                // Fall through with whatever valid offer was found before the failure.
            }

            return new object[] { bestTrader, bestPrice, bestTrader != null };
        }

        public static double GetCompositeFleaPrice(object rootItem, double rootAverage)
        {
            try
            {
                if (rootItem == null || rootAverage <= 0)
                    return rootAverage;

                var nodes = EnumerateItemTree(rootItem).ToList();
                if (nodes.Count <= 1)
                    return rootAverage;

                double total = rootAverage;

                for (int i = 1; i < nodes.Count; i++)
                {
                    var child = nodes[i];
                    var templateId = GetMemberValue(child, "TemplateId")?.ToString();
                    if (string.IsNullOrEmpty(templateId)) continue;

                    if (!TryGetCachedFleaPrice(templateId, out var price) || price <= 0)
                        continue;

                    var countObj = GetMemberValue(child, "StackObjectsCount");
                    var count = 1;
                    if (countObj != null)
                    {
                        try { count = Math.Max(1, Convert.ToInt32(countObj)); }
                        catch { count = 1; }
                    }

                    total += price * count;
                }

                return total;
            }
            catch
            {
                return rootAverage;
            }
        }

        private static int GetCompositeTraderPrice(object trader, object rootItem)
        {
            try
            {
                var original = GetTraderPrice(trader, rootItem);
                var nodes = EnumerateItemTree(rootItem).ToList();
                if (nodes.Count <= 1)
                    return original;

                long sum = 0;
                var pricedAny = false;

                foreach (var node in nodes)
                {
                    var clone = CloneItem(node);
                    if (clone == null)
                        return original;

                    ClearChildren(clone);

                    var price = GetTraderPrice(trader, clone);
                    if (price <= 0)
                        continue;

                    pricedAny = true;
                    sum += price;
                    if (sum >= int.MaxValue)
                        return int.MaxValue;
                }

                if (!pricedAny || sum <= 0)
                    return original;

                return (int)sum;
            }
            catch
            {
                return GetTraderPrice(trader, rootItem);
            }
        }

        private static int GetTraderPrice(object trader, object item)
        {
            try
            {
                if (trader == null || item == null) return 0;

                var method = trader.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetUserItemPrice" && m.GetParameters().Length == 1);

                if (method == null) return 0;

                var result = method.Invoke(trader, new[] { item });
                if (result == null) return 0;

                var amount = GetMemberValue(result, "Amount");
                if (amount == null) return 0;

                var value = Convert.ToInt64(amount);
                if (value <= 0) return 0;
                return value >= int.MaxValue ? int.MaxValue : (int)value;
            }
            catch
            {
                return 0;
            }
        }

        private static object CloneItem(object item)
        {
            try
            {
                if (item == null) return null;
                EnsureCloneMethod();
                if (_cloneItemMethod == null) return null;

                var closed = _cloneItemMethod.MakeGenericMethod(item.GetType());
                return closed.Invoke(null, new object[] { item, null });
            }
            catch
            {
                return null;
            }
        }

        private static void ClearChildren(object item)
        {
            try
            {
                var containers = GetContainers(item).ToList();
                foreach (var container in containers)
                {
                    if (container == null) continue;

                    var containedProp = FindProperty(container.GetType(), "ContainedItem", true);
                    var setter = containedProp?.GetSetMethod(true);
                    if (setter != null)
                    {
                        try { setter.Invoke(container, new object[] { null }); }
                        catch { }
                    }

                    var itemsProp = FindProperty(container.GetType(), "Items", true);
                    var items = itemsProp?.GetValue(container);
                    if (items != null)
                    {
                        try
                        {
                            var clear = items.GetType().GetMethod(
                                "Clear",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                Type.EmptyTypes,
                                null);
                            clear?.Invoke(items, null);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static IEnumerable<object> EnumerateItemTree(object root)
        {
            var result = new List<object>();
            if (root == null) return result;

            // The game exposes the complete item tree directly. Walking a guessed
            // "Containers" property missed weapon slots in the previous test build,
            // leaving flea prices unchanged when the sight, magazine or suppressor
            // was removed. Use the game's tree enumeration first.
            try
            {
                if (!_getAllItemsResolved)
                {
                    _getAllItemsResolved = true;
                    _getAllItemsMethod = root.GetType().GetMethod("GetAllItems",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);

                    // Some EFT builds expose GetAllItems as an extension method.
                    if (_getAllItemsMethod == null)
                    {
                        _getAllItemsMethod = GetLoadableTypes(GameAssembly)
                            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                            .FirstOrDefault(m => m.Name == "GetAllItems" &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType.IsAssignableFrom(root.GetType()) &&
                                typeof(IEnumerable).IsAssignableFrom(m.ReturnType));
                    }
                }

                var allItems = _getAllItemsMethod == null ? null :
                    (_getAllItemsMethod.IsStatic
                        ? _getAllItemsMethod.Invoke(null, new[] { root })
                        : _getAllItemsMethod.Invoke(root, null)) as IEnumerable;

                if (allItems != null)
                {
                    var seenItems = new HashSet<object>(ReferenceComparer.Instance);
                    result.Add(root);
                    seenItems.Add(root);
                    foreach (var item in allItems)
                        if (item != null && seenItems.Add(item)) result.Add(item);
                    if (result.Count > 1) return result;
                    result.Clear();
                }
            }
            catch { result.Clear(); }

            var seen = new HashSet<object>(ReferenceComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null || !seen.Add(current)) continue;

                result.Add(current);

                var children = new List<object>();
                foreach (var container in GetContainers(current))
                {
                    foreach (var child in GetContainerItems(container))
                    {
                        if (child != null) children.Add(child);
                    }
                }

                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }

            return result;
        }

        private static IEnumerable<object> GetContainers(object item)
        {
            if (item == null) yield break;

            object value = null;
            try
            {
                value = FindProperty(item.GetType(), "Containers", true)?.GetValue(item);
            }
            catch { }

            if (value is IEnumerable enumerable)
            {
                foreach (var container in enumerable)
                    if (container != null)
                        yield return container;
            }
        }

        private static IEnumerable<object> GetContainerItems(object container)
        {
            if (container == null) yield break;

            object items = null;
            try
            {
                items = FindProperty(container.GetType(), "Items", true)?.GetValue(container);
            }
            catch { }

            if (items is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    if (item != null)
                        yield return item;
            }
            else
            {
                object single = null;
                try
                {
                    single = FindProperty(container.GetType(), "ContainedItem", true)?.GetValue(container);
                }
                catch { }

                if (single != null)
                    yield return single;
            }
        }

        private static bool TryGetCachedFleaPrice(string templateId, out double price)
        {
            price = 0;

            try
            {
                if (_fleaTryGetMethod == null)
                {
                    var type = QuickSellAssembly?.GetType("QuickSell.Patches.FleaPriceCache", false);
                    _fleaTryGetMethod = type?.GetMethod(
                        "TryGet",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_fleaTryGetMethod == null) return false;

                var args = new object[] { templateId, 0d };
                var ok = (bool)_fleaTryGetMethod.Invoke(null, args);
                if (!ok) return false;

                price = Convert.ToDouble(args[1]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureCloneMethod()
        {
            if (_cloneItemMethod != null) return;

            try
            {
                foreach (var type in GetLoadableTypes(GameAssembly))
                {
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (method.Name != "CloneItem" || !method.IsGenericMethodDefinition)
                            continue;

                        var ga = method.GetGenericArguments();
                        var p = method.GetParameters();
                        if (ga.Length == 1 && p.Length == 2)
                        {
                            _cloneItemMethod = method;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();

            try
            {
                var prop = FindProperty(type, name, true);
                if (prop != null) return prop.GetValue(target);
            }
            catch { }

            try
            {
                var field = FindField(type, name, true);
                if (field != null) return field.GetValue(target);
            }
            catch { }

            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name, bool includeStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (includeStatic) flags |= BindingFlags.Static | BindingFlags.FlattenHierarchy;

            for (var t = type; t != null; t = t.BaseType)
            {
                var prop = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (prop != null) return prop;
            }

            return null;
        }

        private static FieldInfo FindField(Type type, string name, bool includeStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (includeStatic) flags |= BindingFlags.Static | BindingFlags.FlattenHierarchy;

            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }

            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        private static Assembly QuickSellAssembly
        {
            get
            {
                if (_quickSellAssembly != null) return _quickSellAssembly;
                _quickSellAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "QuickSell", StringComparison.OrdinalIgnoreCase));
                return _quickSellAssembly;
            }
        }

        private static Assembly GameAssembly
        {
            get
            {
                if (_gameAssembly != null) return _gameAssembly;
                _gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.OrdinalIgnoreCase));
                return _gameAssembly;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
