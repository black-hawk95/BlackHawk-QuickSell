using Comfort.Common;
using EFT;
using EFT.Hideout;
using System;
using System.Linq;

namespace QuickSell.Patches
{
    /// <summary>
    /// Cheap, allocation-free checks for "where are we running".
    ///
    /// Every hot path in this mod starts with one of these. The rule is that the cheapest and most
    /// selective check runs first: a raid check that returns in a couple of nanoseconds is worth far
    /// more than one placed after a reflection call, because the reflection call still runs.
    /// </summary>
    internal static class ModEnvironment
    {
        /// <summary>
        /// True when the player is in an actual raid (not the hideout).
        ///
        /// GameWorld exists in the hideout too, so the type has to be checked rather than just its
        /// existence. Both operations are field reads with no allocation, which is what makes this
        /// safe to call every frame.
        /// </summary>
        public static bool IsInRaid =>
            Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is not HideoutGameWorld;

        /// <summary>
        /// True on a Fika headless client - a server-side client with no player and no UI.
        ///
        /// Nothing this mod does makes sense there: there is no one to show a tooltip to and no one
        /// to press a keybind. Letting it run would also mean one more client fetching the price
        /// table at startup, which is exactly the load we are trying to avoid on shared servers.
        ///
        /// Detected by looking for Fika's headless plugin rather than by referencing Fika directly,
        /// so this builds and runs fine without Fika installed.
        /// </summary>
        public static bool IsHeadlessClient
        {
            get
            {
                if (_isHeadless.HasValue) return _isHeadless.Value;

                _isHeadless = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys
                    .Any(guid => guid.IndexOf("headless", StringComparison.OrdinalIgnoreCase) >= 0);

                if (_isHeadless.Value)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuickSell: headless client detected; all QuickSell features are disabled here.");
                }

                return _isHeadless.Value;
            }
        }

        private static bool? _isHeadless;

        /// <summary>
        /// True when the mod should do nothing at all. Checked once at startup, not per frame.
        /// </summary>
        public static bool IsDisabledEntirely => IsHeadlessClient;
    }
}
