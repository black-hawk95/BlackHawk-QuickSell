using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using QuickSell.Patches;
using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace QuickSell
{
    [BepInPlugin("com.blackhawk.quicksell", "BlackHawk-QuickSell", "3.1.0")]
    // UI Fixes 6.0 (SPT 4.1) changed its GUID from "Tyfon.UIFixes" to "com.tyfon.uifixes".
    // Both are declared so load order is correct against either version.
    [BepInDependency("com.tyfon.uifixes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Tyfon.UIFixes", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        private static readonly Version UIFixesMinimumVersion = new(2, 5);
        private static readonly string[] UIFixesPluginIds = { "com.tyfon.uifixes", "Tyfon.UIFixes" };

        // ---- selling
        public static bool EnableQuickSellFlea = true;
        public static bool EnableQuickSellTraders = true;
        public static bool ShowConfirmationDialog = true;
        public static string[] TradersBlacklist = [];
        public static double AvgPricePercent = 100;
        public static bool IgnoreFleaCapacity = false;
        public static bool DisableKeybinds = false;

        // ---- tooltips
        public static bool ShowPriceTooltips = true;
        public static bool ShowTraderPriceInTooltip = true;
        public static bool ShowFleaPriceInTooltip = true;
        public static bool ShowPricesInRaid = true;

        // How long the cursor must rest on an item before its tooltip appears.
        public static bool OverrideTooltipDelay = true;

        // Backed by a live F12 entry, so dragging the slider takes effect straight away rather
        // than needing a restart. Falls back to the config.json value before the entry is bound.
        internal static ConfigEntry<float> TooltipDelayEntry;
        private static float _tooltipDelayFallback = 0.2f;
        public static float TooltipDelay => TooltipDelayEntry?.Value ?? _tooltipDelayFallback;

        // ---- colour coding
        public static bool EnableColorCoding = true;
        public static bool UseAmmoPenetrationTiers = true;
        public static string BestTradeColor = "FFFFFF";

        // Upper bound of each of the first five tiers; anything above the last is the sixth.
        public static double[] PriceTierThresholds = { 900, 12000, 21000, 38000, 92000 };
        public static double[] AmmoTierThresholds = { 15, 25, 34, 43, 55 };

        // poor, common, uncommon, rare, epic, legendary
        public static string[] TierColorHexes = { "9D9D9D", "FFFFFF", "1EFF00", "0070DD", "A335EE", "FF8000" };

        // ---- UI Fixes
        public static bool EnableUIFixesIntegration = false;
        public static bool UIFixesDetected =>
            UIFixesPluginIds.Any(id =>
                Chainloader.PluginInfos.TryGetValue(id, out var pluginInfo) &&
                pluginInfo?.Metadata?.Version >= UIFixesMinimumVersion);

        internal static ConfigEntry<KeyboardShortcut> KeybindTraders;
        internal static ConfigEntry<KeyboardShortcut> KeybindFlea;

        public static ManualLogSource LogSource;

        private void Awake()
        {
            LogSource = Logger;

            // A Fika headless client has no player and no UI. Running here would achieve nothing and
            // would add one more client fetching the price table at startup, which is exactly the
            // server load this mod is built to avoid.
            if (Patches.ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuickSell: headless client detected, plugin disabled.");
                return;
            }

            try
            {
                var modPath = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(modPath))
                    LoadConfig(modPath.Replace('\\', '/'));
                else
                    EnableUIFixesIntegration = UIFixesDetected;
            }
            catch (Exception e)
            {
                Logger.LogError($"QuickSell: config load failed, using defaults: {e.Message}");
                EnableUIFixesIntegration = UIFixesDetected;
            }

            Logger.LogInfo(UIFixesDetected
                ? $"QuickSell: UI Fixes detected, multi-select integration {(EnableUIFixesIntegration ? "enabled" : "disabled in config")}"
                : "QuickSell: UI Fixes not found, multi-select integration disabled");

            // Both SimpleContextMenu entry points are patched; see ContextMenuPatch for why.
            new ContextMenuShowPatch().Enable();
            new ContextMenuShowMenuPatch().Enable();

            if (ShowPriceTooltips) new TooltipPatch().Enable();

            // An AcceptableValueRange on a float makes the Configuration Manager draw a slider
            // rather than a text box.
            TooltipDelayEntry = Config.Bind(
                "QuickSell",
                "Tooltip delay (seconds)",
                _tooltipDelayFallback,
                new ConfigDescription(
                    "How long the cursor must rest on an item before its price tooltip appears.\n\n" +
                    "0.00 = instant. Prices show the moment the cursor touches an item, but sweeping " +
                    "across a full stash makes the mod build a tooltip for every item it passes over, " +
                    "which can cost a few ms each and show up as stutter.\n\n" +
                    "0.20 (default) = the cursor has to settle briefly. Items you skim past are never " +
                    "calculated at all, so fast looting stays smooth while a deliberate hover still " +
                    "feels immediate.\n\n" +
                    "1.00 = maximum pause. Cheapest possible, but you wait a full second on every item.\n\n" +
                    "Note that each item is only ever calculated once - hovering something a second " +
                    "time is free regardless of this setting.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = 3 }));

            // Self-detecting: skipped with a log line if the parameter it needs is not present,
            // rather than taking the price tooltips down with it.
            if (OverrideTooltipDelay) TooltipDelayPatch.TryEnable();

            if (!DisableKeybinds)
            {
                KeybindFlea = Config.Bind("QuickSell", "SellFlea", new KeyboardShortcut(KeyCode.N), "Quicksell on the Flea");
                KeybindTraders = Config.Bind("QuickSell", "SellTraders", new KeyboardShortcut(KeyCode.M), "QuickSell to Traders");
                KeybindPatches.Enable();
            }

            // Fetched once, here, and never on a timer. Needed before raid because tooltips in raid
            // read from this table and must not touch the network.
            if (ShowPriceTooltips && ShowFleaPriceInTooltip)
                FleaPriceClient.FetchAsync();

            // Drawn as a button in the F12 menu. ConfigurationManagerAttributes is a vendored
            // template matched by reflection, so this degrades to an ordinary setting rather than
            // failing if the user has no Configuration Manager installed.
            Config.Bind("QuickSell", "RefreshFleaPrices", string.Empty,
                new ConfigDescription("Re-fetch flea prices from the server. Menu only, not during a raid.", null,
                    new ConfigurationManagerAttributes
                    {
                        CustomDrawer = RefreshFleaPricesDrawer,
                        HideDefaultButton = true,
                        Order = 1
                    }));
        }

        private void RefreshFleaPricesDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button("Refresh flea prices now", GUILayout.ExpandWidth(true)))
            {
                // Cached tooltips hold the OLD prices, so they have to go with them.
                TooltipPatch.Invalidate();
                FleaPriceClient.ForceRefresh();
            }
        }

        private void LoadConfig(string path)
        {
            var configPath = Path.Combine(path, "config.json");
            if (!File.Exists(configPath))
            {
                Logger.LogWarning("QuickSell: config.json not found, using defaults");
                EnableUIFixesIntegration = UIFixesDetected;
                return;
            }

            var config = JObject.Parse(File.ReadAllText(configPath));

            Bool(config, "EnableQuickSellFlea", ref EnableQuickSellFlea);
            Bool(config, "EnableQuickSellTraders", ref EnableQuickSellTraders);
            Bool(config, "ShowConfirmationDialog", ref ShowConfirmationDialog);
            Bool(config, "IgnoreFleaCapacity", ref IgnoreFleaCapacity);
            Bool(config, "DisableKeybinds", ref DisableKeybinds);

            Bool(config, "ShowPriceTooltips", ref ShowPriceTooltips);
            Bool(config, "ShowTraderPriceInTooltip", ref ShowTraderPriceInTooltip);
            Bool(config, "ShowFleaPriceInTooltip", ref ShowFleaPriceInTooltip);
            Bool(config, "ShowPricesInRaid", ref ShowPricesInRaid);
            Bool(config, "OverrideTooltipDelay", ref OverrideTooltipDelay);

            if (config.ContainsKey("TooltipDelay"))
                _tooltipDelayFallback = (float)config["TooltipDelay"];

            Bool(config, "EnableColorCoding", ref EnableColorCoding);
            Bool(config, "UseAmmoPenetrationTiers", ref UseAmmoPenetrationTiers);

            if (config.ContainsKey("TradersBlacklist"))
                TradersBlacklist = config["TradersBlacklist"].ToObject<string[]>();

            if (config.ContainsKey("AvgPricePercent"))
                AvgPricePercent = (double)config["AvgPricePercent"];

            if (config.ContainsKey("BestTradeColor"))
                BestTradeColor = ((string)config["BestTradeColor"]).TrimStart('#');

            // Wrong-length arrays are rejected rather than partially applied: a 4-entry threshold
            // list would silently mis-tier everything above it.
            if (config.ContainsKey("PriceTierThresholds"))
            {
                var v = config["PriceTierThresholds"].ToObject<double[]>();
                if (v?.Length == 5) PriceTierThresholds = v;
                else Logger.LogWarning("QuickSell: PriceTierThresholds needs exactly 5 values; using defaults.");
            }

            if (config.ContainsKey("AmmoTierThresholds"))
            {
                var v = config["AmmoTierThresholds"].ToObject<double[]>();
                if (v?.Length == 5) AmmoTierThresholds = v;
                else Logger.LogWarning("QuickSell: AmmoTierThresholds needs exactly 5 values; using defaults.");
            }

            if (config.ContainsKey("TierColors"))
            {
                var v = config["TierColors"].ToObject<string[]>();
                if (v?.Length == 6) TierColorHexes = v.Select(c => c.TrimStart('#')).ToArray();
                else Logger.LogWarning("QuickSell: TierColors needs exactly 6 values; using defaults.");
            }

            if (config.ContainsKey("EnableUIFixesIntegration"))
                EnableUIFixesIntegration = (bool)config["EnableUIFixesIntegration"];
            else
                EnableUIFixesIntegration = UIFixesDetected;
        }

        private static void Bool(JObject config, string key, ref bool target)
        {
            if (config.ContainsKey(key)) target = (bool)config[key];
        }
    }
}
