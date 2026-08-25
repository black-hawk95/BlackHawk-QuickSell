using BepInEx;
using BepInEx.Bootstrap;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

/*
UI Fixes Multi-Select InterOp

First, add the following attribute to your plugin class:

[BepInDependency("com.tyfon.uifixes", BepInDependency.DependencyFlags.SoftDependency)]

This will ensure UI Fixes is loaded already when your code is run. It will fail gracefully if UI Fixes is missing.

Second, add this file to your project. Use the below UIFixesInterop.MultiSelect static methods, no explicit initialization required.

Some things to keep in mind:
- While you can use MultiSelect.Items to get the items, this should only be used for reading purproses. If you need to
execute an operation on the items, I strongly suggest using the provided MultiSelect.Apply() method. 

- Apply() will execute the provided operation on each item, sequentially (sorted by grid order), maximum of one operation per frame.
It does this because strange bugs manifest if you try to do more than one thing in a single frame.

- If the operation you are passing to Apply() does anything async, use the overload that takes a Func<Item, Task>. It will wait
until each operation is over before doing the next. This is especially important if an operation could be affected by the preceding one, 
for example in a quick-move where the avaiable space changes. It's also required if you are doing anything in-raid that will trigger
an animation, as starting the next one before it is complete will likely cancel the first.
*/
namespace UIFixesInterop
{
    /// <summary>
    /// Provides access to UI Fixes' multiselect functionality. 
    /// </summary>
    internal static class MultiSelect
    {
        private static readonly Version RequiredVersion = new Version(2, 5);

        // UIFixes 6.0 (SPT 4.1) changed its plugin GUID from "Tyfon.UIFixes" to
        // "com.tyfon.uifixes". The assembly name (Tyfon.UIFixes.dll) did not change.
        // Both GUIDs are checked so this works against 4.1 and older builds alike.
        private static readonly string[] PluginIds = { "com.tyfon.uifixes", "Tyfon.UIFixes" };

        private static bool? UIFixesLoaded;

        private static Type MultiSelectType;

        // Count is read on the keybind path and once per context menu build. Invoking a MethodInfo
        // allocates an argument array and boxes the returned int every single call; a delegate
        // bound once at load costs the same as a normal method call and allocates nothing.
        private static Func<int> GetCountDelegate;

        private static MethodInfo GetCountMethod;
        private static MethodInfo GetItemsMethod;
        private static MethodInfo ApplyMethod;

        /// <value><c>Count</c> represents the number of items in the current selection, 0 if UI Fixes is not present.</value>
        public static int Count
        {
            get
            {
                if (!Loaded())
                {
                    return 0;
                }

                return GetCountDelegate();
            }
        }

        /// <value><c>Items</c> is an enumerable list of items in the current selection, empty if UI Fixes is not present.</value>
        public static IEnumerable<Item> Items
        {
            get
            {
                if (!Loaded())
                {
                    return new Item[] { };
                }

                return (IEnumerable<Item>)GetItemsMethod.Invoke(null, new object[] { });
            }
        }

        /// <summary>
        /// This method takes an <c>Action</c> and calls it *sequentially* on each item in the current selection.
        /// Will no-op if UI Fixes is not present.
        /// </summary>
        /// <param name="action">The action to call on each item.</param>
        /// <param name="itemUiContext">Optional <c>ItemUiContext</c>; will use <c>ItemUiContext.Instance</c> if not provided.</param>
        public static void Apply(Action<Item> action, ItemUiContext itemUiContext = null)
        {
            if (!Loaded())
            {
                return;
            }

            Func<Item, Task> func = item =>
            {
                action(item);
                return Task.CompletedTask;
            };

            ApplyMethod.Invoke(null, new object[] { func, itemUiContext });
        }

        /// <summary>
        /// This method takes an <c>Func</c> that returns a <c>Task</c> and calls it *sequentially* on each item in the current selection.
        /// Will return a completed task immediately if UI Fixes is not present.
        /// </summary>
        /// <param name="func">The function to call on each item</param>
        /// <param name="itemUiContext">Optional <c>ItemUiContext</c>; will use <c>ItemUiContext.Instance</c> if not provided.</param>
        /// <returns>A <c>Task</c> that will complete when all the function calls are complete.</returns>
        public static Task Apply(Func<Item, Task> func, ItemUiContext itemUiContext = null)
        {
            if (!Loaded())
            {
                return Task.CompletedTask;
            }

            return (Task)ApplyMethod.Invoke(null, new object[] { func, itemUiContext });
        }

        private static bool Loaded()
        {
            if (UIFixesLoaded.HasValue) return UIFixesLoaded.Value;

            PluginInfo pluginInfo = null;
            string matchedId = null;
            foreach (var id in PluginIds)
            {
                if (Chainloader.PluginInfos.TryGetValue(id, out pluginInfo) && pluginInfo != null)
                {
                    matchedId = id;
                    break;
                }
            }

            if (matchedId == null)
            {
                UIFixesLoaded = false;
                QuickSell.Plugin.LogSource?.LogInfo(
                    "QuickSell: UI Fixes not installed (looked for " + string.Join(" and ", PluginIds) +
                    "). Multi-select integration is off.");
                return false;
            }

            var version = pluginInfo.Metadata?.Version;
            if (version < RequiredVersion)
            {
                UIFixesLoaded = false;
                QuickSell.Plugin.LogSource?.LogWarning(
                    $"QuickSell: UI Fixes {version} is older than the required {RequiredVersion}. " +
                    "Multi-select integration is off.");
                return false;
            }

            MultiSelectType = Type.GetType("UIFixes.MultiSelectController, Tyfon.UIFixes");
            if (MultiSelectType == null)
            {
                UIFixesLoaded = false;
                QuickSell.Plugin.LogSource?.LogError(
                    "QuickSell: found UI Fixes " + version + " (" + matchedId + ") but could not load " +
                    "UIFixes.MultiSelectController from Tyfon.UIFixes. The interop contract changed; " +
                    "multi-select is off.");
                return false;
            }

            GetCountMethod = AccessTools.Method(MultiSelectType, "GetCount");
            GetItemsMethod = AccessTools.Method(MultiSelectType, "GetItems");
            ApplyMethod = AccessTools.Method(MultiSelectType, "Apply");

            // Previously these could be left null while UIFixesLoaded stayed true, turning a
            // renamed method into a NullReferenceException deep inside a sell. Fail closed instead.
            if (GetCountMethod == null || GetItemsMethod == null || ApplyMethod == null)
            {
                UIFixesLoaded = false;
                QuickSell.Plugin.LogSource?.LogError(
                    "QuickSell: MultiSelectController is missing one of GetCount/GetItems/Apply " +
                    $"(GetCount={GetCountMethod != null}, GetItems={GetItemsMethod != null}, Apply={ApplyMethod != null}). " +
                    "Multi-select is off.");
                return false;
            }

            // Bind the zero-argument Count getter to a delegate. If UI Fixes ever changes the
            // signature this throws here, at load, where it is caught and reported - rather than
            // silently misbehaving later.
            try
            {
                GetCountDelegate = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), GetCountMethod);
            }
            catch (Exception ex)
            {
                UIFixesLoaded = false;
                QuickSell.Plugin.LogSource?.LogError(
                    $"QuickSell: could not bind UI Fixes GetCount ({ex.Message}). Multi-select is off.");
                return false;
            }

            UIFixesLoaded = true;
            QuickSell.Plugin.LogSource?.LogInfo(
                $"QuickSell: UI Fixes {version} detected via '{matchedId}'; multi-select interop ready.");
            return true;
        }
    }
}