using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Linq;
using System.Reflection;

namespace QuickSell.Patches
{
    /// <summary>
    /// Overrides how long the game waits before showing an item tooltip.
    ///
    /// Kept as its own patch, separate from the one that appends prices, for a specific reason:
    /// Harmony binds prefix parameters by NAME. If the delay parameter is not called "delay" on
    /// this game build, a patch declaring it fails when it is enabled. Folding that into the price
    /// patch would mean a wrong guess takes the prices down with it.
    ///
    /// Split apart, the worst case is that the delay stays at the game default while prices carry
    /// on working. The parameter name is also verified by reflection before the patch is enabled,
    /// so a mismatch is reported rather than thrown.
    /// </summary>
    internal class TooltipDelayPatch : ModulePatch
    {
        /// <summary>
        /// Enables the patch only if SimpleTooltip.Show really does have a float parameter named
        /// "delay". Returns false (with a log line) instead of throwing when it does not.
        /// </summary>
        public static bool TryEnable()
        {
            var method = FindShowMethod();

            if (method == null)
            {
                Plugin.LogSource?.LogWarning(
                    "QuickSell: could not find SimpleTooltip.Show, so the tooltip delay is unchanged.");
                return false;
            }

            var delayParam = method.GetParameters()
                .FirstOrDefault(p => p.Name == "delay" && p.ParameterType == typeof(float));

            if (delayParam == null)
            {
                var names = string.Join(", ", method.GetParameters()
                    .Select(p => $"{p.ParameterType.Name} {p.Name}"));

                Plugin.LogSource?.LogWarning(
                    "QuickSell: SimpleTooltip.Show has no float parameter named 'delay', so the " +
                    $"tooltip delay is left at the game default. Its parameters are: {names}");
                return false;
            }

            new TooltipDelayPatch().Enable();
            return true;
        }

        internal static MethodInfo FindShowMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(SimpleTooltip))
                .FirstOrDefault(m =>
                    m.Name == "Show"
                    && m.GetParameters().Length > 0
                    && m.GetParameters()[0].ParameterType == typeof(string));
        }

        protected override MethodBase GetTargetMethod() => FindShowMethod();

        [PatchPrefix]
        private static void Prefix(ref float delay)
        {
            if (!Plugin.OverrideTooltipDelay) return;

            delay = Plugin.TooltipDelay;
        }
    }
}
