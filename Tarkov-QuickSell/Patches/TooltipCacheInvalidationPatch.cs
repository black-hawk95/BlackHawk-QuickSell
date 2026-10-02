using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;

namespace QuickSell.Patches
{
    /// <summary>
    /// Low-level EFT inventory events can fire while a move is still in a temporary remove/add state.
    /// Do not inspect or recalculate parent trees inside those events. Queue one safe reconcile back
    /// onto Unity's main-thread context after the current inventory operation has finished.
    /// </summary>
    internal static class TooltipCacheInvalidationPatch
    {
        public static void Enable()
        {
            new ItemAddressAddTooltipCachePatch().Enable();
            new ItemAddressRemoveTooltipCachePatch().Enable();
            new ItemRefreshTooltipCachePatch().Enable();
        }

        private static void Queue(Item item, bool rebuildSelf, string reason)
        {
            if (item != null)
                TooltipPatch.InvalidateItem(item.Id.ToString());

            HoverPriceCache.QueueInventoryReconcile(item, rebuildSelf, reason);
        }

        private sealed class ItemAddressAddTooltipCachePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(ItemAddress), nameof(ItemAddress.RaiseAddEvent));

            [PatchPostfix]
            private static void Postfix(Item item, CommandStatus status)
            {
                if (status != CommandStatus.Succeed) return;
                Queue(item, rebuildSelf: false, reason: "add");
            }
        }

        private sealed class ItemAddressRemoveTooltipCachePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(ItemAddress), nameof(ItemAddress.RaiseRemoveEvent));

            [PatchPostfix]
            private static void Postfix(Item item, CommandStatus status)
            {
                if (status != CommandStatus.Succeed) return;
                Queue(item, rebuildSelf: false, reason: "remove");
            }
        }

        private sealed class ItemRefreshTooltipCachePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
                => AccessTools.Method(typeof(Item), nameof(Item.RaiseRefreshEvent));

            [PatchPostfix]
            private static void Postfix(Item __instance)
            {
                if (__instance == null) return;
                Queue(__instance, rebuildSelf: true, reason: "refresh");
            }
        }
    }
}
