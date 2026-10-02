using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;

namespace QuickSell.Patches
{
    /// <summary>
    /// Starts tooltip price pre-computation when the inventory screen is fully shown.
    /// This deliberately moves large-container work away from the hover path.
    /// </summary>
    internal sealed class InventoryPrecomputePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.FirstMethod(typeof(InventoryScreen), m => m.Name == nameof(InventoryScreen.Show));

        [PatchPostfix]
        private static void Postfix()
        {
            HoverPriceCache.OnInventoryScreenShown();
        }
    }
}
