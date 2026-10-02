using EFT.UI;
using SPT.Reflection.Patching;
using System.Reflection;

namespace QuickSell.Patches
{
    /// <summary>
    /// Starts the one-time price warm-up as soon as the main menu exists. If the session is not
    /// ready yet this is a no-op; InventoryPrecomputePatch is the guaranteed fallback.
    /// </summary>
    internal sealed class MenuPrecomputePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => typeof(MenuScreen).GetMethod(nameof(MenuScreen.Awake));

        [PatchPostfix]
        private static void Postfix()
        {
            HoverPriceCache.OnMenuReady();
        }
    }
}
