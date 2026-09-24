using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace QuickSell.TestFixes
{
    public static class RuntimeFixes
    {
        private static object _playPerItemEntry;
        private static Assembly _quickSellAssembly;
        private static Assembly _gameAssembly;
        private static MethodInfo _cloneItemMethod;
        private static MethodInfo _fleaTryGetMethod;

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
            }
            catch
            {
                _playPerItemEntry = null;
            }
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
