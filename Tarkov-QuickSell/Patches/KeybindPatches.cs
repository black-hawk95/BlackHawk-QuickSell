using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Linq;
using System.Reflection;
using TMPro;
using UIFixesInterop;
using UnityEngine.EventSystems;

namespace QuickSell.Patches
{
    /// <summary>
    /// Patches ItemUiContext.Update to run keybind checks in the same execution context as the game UI.
    /// </summary>
    public static class KeybindPatches
    {
        public static void Enable()
        {
            new ItemUiContextKeybindPatch().Enable();
        }

        internal static bool TextboxActive()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null || !selected.activeInHierarchy) return false;

            // GetComponent is not free, and this used to run on every frame. The selected object
            // only changes when focus changes, so the answer is cached against it and the lookup
            // happens once per focus change instead of once per frame.
            if (ReferenceEquals(selected, _lastSelectedObject)) return _lastSelectedWasTextbox;

            _lastSelectedObject = selected;
            _lastSelectedWasTextbox = selected.GetComponent<TMP_InputField>() != null;
            return _lastSelectedWasTextbox;
        }

        private static UnityEngine.GameObject _lastSelectedObject;
        private static bool _lastSelectedWasTextbox;

        internal class ItemUiContextKeybindPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
            {
                return AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.Update));
            }

            [PatchPostfix]
            private static void Postfix(ItemUiContext __instance)
            {
                // ---------------------------------------------------------------------------
                // THE IMPORTANT PART: this method runs every frame the inventory UI is alive.
                //
                // Previously the keybind test was the LAST thing here, so every frame paid for
                // a reflection call, several singleton lookups, a GetComponent and a LINQ query
                // before discovering that no key was pressed - which is the case on virtually
                // every frame.
                //
                // Reading two key states first turns the common case into two field reads and a
                // return: no allocation, no reflection, nothing for the GC to collect later.
                // Everything below only runs on the frame a key actually goes down.
                // ---------------------------------------------------------------------------
                bool fleaDown = Plugin.KeybindFlea != null && Plugin.KeybindFlea.Value.IsDown();
                bool tradersDown = Plugin.KeybindTraders != null && Plugin.KeybindTraders.Value.IsDown();

                if (!fleaDown && !tradersDown)
                    return;

                // Selling is a menu-only action, so bail out of raid before doing anything else.
                if (ModEnvironment.IsInRaid)
                    return;

                if (Singleton<MenuUI>.Instantiated &&
                    Singleton<MenuUI>.Instance.HideoutAreaTransferItemsScreen != null &&
                    Singleton<MenuUI>.Instance.HideoutAreaTransferItemsScreen.isActiveAndEnabled)
                    return;

                if (TextboxActive())
                    return;

                // Skip while an item is being dragged. Was the private "itemContextClass" field in
                // 4.0 (injected by Harmony as ___itemContextClass); now _dragItemContext, resolved
                // by type so a rename degrades to a logged warning rather than a broken patch.
                if (Compat.Get<DragItemContext>(__instance, "_dragItemContext") != null)
                    return;

                // Was .ItemContextAbstractClass in 4.0.
                var itemContext = __instance.CurrentItemContext;
                var hasMultiSelection = Plugin.EnableUIFixesIntegration && MultiSelect.Count > 0;
                if (itemContext == null && !hasMultiSelection)
                    return;

                if (itemContext != null && itemContext.ViewType != EItemViewType.Inventory)
                    return;

                // If no hovered item exists, allow keybinds to operate on active UIFixes multi-selection.
                var item = itemContext?.Item;
                if (item == null && hasMultiSelection)
                    item = MultiSelect.Items.FirstOrDefault();
                if (item == null)
                    return;

                if (item.GetAllParentItems().Any(x => x is InventoryEquipment))
                    return;
                if (item.Parent?.Container?.ParentItem?.TemplateId == "55d7217a4bdc2d86028b456d")
                    return;

                if (fleaDown)
                {
                    ContextMenuPatch.SellToFlea(item);
                    return;
                }

                ContextMenuPatch.SellToTraders(item);
            }
        }
    }
}
