using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using QuickSell.Patches;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace QuickSell
{
    [BepInPlugin("com.blackhawk.quicksell", "BlackHawk-QuickSell", "3.2.3")]
    // UI Fixes 6.0 (SPT 4.1) changed its GUID from "Tyfon.UIFixes" to "com.tyfon.uifixes".
    // Both are declared so load order is correct against either version.
    [BepInDependency("com.tyfon.uifixes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Tyfon.UIFixes", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        // Sections are numbered because the configuration manager sorts them alphabetically;
        // without the prefix they would appear in an arbitrary order.
        private const string SectionSelling = "1. Selling";
        private const string SectionTooltips = "2. Tooltips";
        private const string SectionColors = "3. Colors";

        private static readonly Version UIFixesMinimumVersion = new(2, 5);
        private static readonly string[] UIFixesPluginIds = { "com.tyfon.uifixes", "Tyfon.UIFixes" };

        public static ManualLogSource LogSource;

        // ------------------------------------------------------------------ selling

        private static ConfigEntry<bool> _enableQuickSellFlea;
        private static ConfigEntry<bool> _enableQuickSellTraders;
        private static ConfigEntry<bool> _showConfirmationDialog;
        private static ConfigEntry<bool> _ignoreFleaCapacity;
        private static ConfigEntry<int> _avgPricePercent;
        private static ConfigEntry<string> _tradersBlacklist;
        private static ConfigEntry<string> _availableTraders;
        private static ConfigEntry<bool> _disableKeybinds;

        // Every setting is exposed as a property reading the live entry, so the rest of the mod
        // keeps using Plugin.Something with no idea the value now comes from the F12 menu, and
        // changes take effect the moment they are made.
        public static bool EnableQuickSellFlea => _enableQuickSellFlea?.Value ?? true;
        public static bool EnableQuickSellTraders => _enableQuickSellTraders?.Value ?? true;
        public static bool ShowConfirmationDialog => _showConfirmationDialog?.Value ?? true;
        public static bool IgnoreFleaCapacity => _ignoreFleaCapacity?.Value ?? false;
        public static double AvgPricePercent => _avgPricePercent?.Value ?? 100;
        public static bool DisableKeybinds => _disableKeybinds?.Value ?? false;

        internal static ConfigEntry<KeyboardShortcut> KeybindTraders;
        internal static ConfigEntry<KeyboardShortcut> KeybindFlea;

        // ------------------------------------------------------------------ tooltips

        private static ConfigEntry<bool> _showPriceTooltips;
        private static ConfigEntry<bool> _showTraderPriceInTooltip;
        private static ConfigEntry<bool> _showFleaPriceInTooltip;
        private static ConfigEntry<bool> _showPricesInRaid;
        private static ConfigEntry<bool> _enableColorCoding;
        private static ConfigEntry<bool> _useAmmoPenetrationTiers;
        internal static ConfigEntry<float> TooltipDelayEntry;

        public static bool ShowPriceTooltips => _showPriceTooltips?.Value ?? true;
        public static bool ShowTraderPriceInTooltip => _showTraderPriceInTooltip?.Value ?? true;
        public static bool ShowFleaPriceInTooltip => _showFleaPriceInTooltip?.Value ?? true;
        public static bool ShowPricesInRaid => _showPricesInRaid?.Value ?? true;
        public static bool EnableColorCoding => _enableColorCoding?.Value ?? true;
        public static bool UseAmmoPenetrationTiers => _useAmmoPenetrationTiers?.Value ?? true;

        public static bool OverrideTooltipDelay = true;
        public static float TooltipDelay => TooltipDelayEntry?.Value ?? 0.2f;

        // ------------------------------------------------------------------ colors (config.json)

        // These stay in config.json because they are arrays - six colours and two five-value
        // threshold lists. They would need twenty separate entries to live in the F12 menu, and
        // they are set once and forgotten rather than adjusted during play.
        public static string BestTradeColor = "FFFFFF";
        public static double[] PriceTierThresholds = { 900, 12000, 21000, 38000, 92000 };
        public static double[] AmmoTierThresholds = { 15, 25, 34, 43, 55 };
        public static string[] TierColorHexes = { "9D9D9D", "FFFFFF", "1EFF00", "0070DD", "A335EE", "FF8000" };

        // ------------------------------------------------------------------ UI Fixes

        public static bool EnableUIFixesIntegration = false;

        public static bool UIFixesDetected =>
            UIFixesPluginIds.Any(id =>
                Chainloader.PluginInfos.TryGetValue(id, out var pluginInfo) &&
                pluginInfo?.Metadata?.Version >= UIFixesMinimumVersion);

        // ------------------------------------------------------------------ startup

        private void Awake()
        {
            LogSource = Logger;

            // A Fika headless client has no player and no UI. Running here would achieve nothing
            // and would add another client fetching the price table at startup.
            if (ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuickSell: headless client detected, plugin disabled.");
                return;
            }

            LoadColorConfig();
            BindSellingSettings();
            BindTooltipSettings();
            BindColorInfo();

            EnableUIFixesIntegration = UIFixesDetected;

            new ContextMenuShowPatch().Enable();
            new ContextMenuShowMenuPatch().Enable();

            if (ShowPriceTooltips) new TooltipPatch().Enable();
            if (OverrideTooltipDelay) TooltipDelayPatch.TryEnable();

            if (!DisableKeybinds) KeybindPatches.Enable();

            if (ShowPriceTooltips && ShowFleaPriceInTooltip) FleaPriceClient.FetchAsync();
        }

        private void BindSellingSettings()
        {
            _enableQuickSellFlea = Config.Bind(SectionSelling, "Flea market entry", true,
                new ConfigDescription(
                    "Show \"QuickSell (Flea)\" when you right-click an item in your stash.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _enableQuickSellTraders = Config.Bind(SectionSelling, "Trader entry", true,
                new ConfigDescription(
                    "Show \"QuickSell (Trader)\" when you right-click an item in your stash.\n\n" +
                    "Sells to whichever trader offers the most.",
                    null, new ConfigurationManagerAttributes { Order = 99 }));

            _showConfirmationDialog = Config.Bind(SectionSelling, "Ask before selling", true,
                new ConfigDescription(
                    "Show a confirmation window with the total price before anything is sold.\n\n" +
                    "Turning this off sells immediately with no undo. Selling cannot be reversed, " +
                    "so leaving it on is recommended if you use the keybinds.",
                    null, new ConfigurationManagerAttributes { Order = 98 }));

            _avgPricePercent = Config.Bind(SectionSelling, "Flea listing price (%)", 100,
                new ConfigDescription(
                    "What percentage of the average flea price to list items at.\n\n" +
                    "100 = the average price shown on the add-offer window.\n" +
                    "Below 100 undercuts the market so items sell faster.\n" +
                    "Above 100 asks more than average.",
                    new AcceptableValueRange<int>(50, 150),
                    new ConfigurationManagerAttributes { Order = 97 }));

            _ignoreFleaCapacity = Config.Bind(SectionSelling, "Ignore offer slot limit", false,
                new ConfigDescription(
                    "Skip the check for free flea offer slots.\n\n" +
                    "Normally QuickSell refuses to list more items than you have slots for. With " +
                    "this on it will try anyway, and the server rejects whatever does not fit.",
                    null, new ConfigurationManagerAttributes { Order = 96 }));

            _tradersBlacklist = Config.Bind(SectionSelling, "Never sell to", "",
                new ConfigDescription(
                    "Traders to exclude when looking for the best price. Separate names with commas.\n\n" +
                    "Example:  Fence, Ragman\n\n" +
                    "Use the trader's name exactly as it appears in game. Capitalisation and spaces " +
                    "around the commas do not matter. Modded traders work the same as vanilla ones - " +
                    "just type their name.\n\n" +
                    "The \"Traders on this profile\" box below lists everything you can put here, so " +
                    "you can copy names straight out of it. If a name will not match (non-English " +
                    "clients show translated names), a trader's ID works here too.\n\n" +
                    "Leave empty to allow every trader.",
                    null, new ConfigurationManagerAttributes { Order = 95 }));

            _tradersBlacklist.SettingChanged += (_, _) =>
            {
                _blacklistCache = null;
                TooltipPatch.Invalidate();
            };

            _availableTraders = Config.Bind(SectionSelling, "Traders on this profile", "",
                new ConfigDescription(
                    "Every trader QuickSell can sell to, including any added by mods.\n\n" +
                    "A reference list for the \"Never sell to\" box above. It fills in once your " +
                    "profile has loaded and the traders are known.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        // Drawn rather than edited: a plain string setting renders as a one-line
                        // text box, which both invites typing into a read-only list and cuts the
                        // list off at the edge of the field. The drawer wraps instead.
                        CustomDrawer = AvailableTradersDrawer,
                        HideDefaultButton = true,
                        Order = 94
                    }));

            _disableKeybinds = Config.Bind(SectionSelling, "Disable keybinds (restart required)", false,
                new ConfigDescription(
                    "Turn off the quick-sell keybinds entirely.\n\n" +
                    "Restart required: the keybinds are wired into the game's UI when the mod loads, " +
                    "so this cannot be switched on or off mid-session.",
                    null, new ConfigurationManagerAttributes { Order = 93 }));

            KeybindFlea = Config.Bind(SectionSelling, "Sell on flea (key)", new KeyboardShortcut(KeyCode.N),
                new ConfigDescription(
                    "Sells the item under the cursor on the flea market.\n\n" +
                    "Works on a multi-selection when UI Fixes is installed.",
                    null, new ConfigurationManagerAttributes { Order = 92 }));

            KeybindTraders = Config.Bind(SectionSelling, "Sell to trader (key)", new KeyboardShortcut(KeyCode.M),
                new ConfigDescription(
                    "Sells the item under the cursor to the best-paying trader.\n\n" +
                    "Works on a multi-selection when UI Fixes is installed.",
                    null, new ConfigurationManagerAttributes { Order = 91 }));
        }

        private void BindTooltipSettings()
        {
            _showPriceTooltips = Config.Bind(SectionTooltips, "Show prices on tooltips", true,
                new ConfigDescription(
                    "Add price lines to the tooltip when you hover an item.\n\n" +
                    "Restart required to turn back on once disabled.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _showTraderPriceInTooltip = Config.Bind(SectionTooltips, "Show best trader price", true,
                new ConfigDescription(
                    "Show which trader pays the most for the item, and how much.\n\n" +
                    "Only available outside raid, since trader data is not loaded during one.",
                    null, new ConfigurationManagerAttributes { Order = 99 }));

            _showFleaPriceInTooltip = Config.Bind(SectionTooltips, "Show flea price", true,
                new ConfigDescription(
                    "Show the average flea market price for the item.\n\n" +
                    "Needs the QuickSell server mod for prices to be available in raid. Without it, " +
                    "prices are looked up one item at a time and only outside raid.",
                    null, new ConfigurationManagerAttributes { Order = 98 }));

            _showPricesInRaid = Config.Bind(SectionTooltips, "Show prices in raid", true,
                new ConfigDescription(
                    "Show flea prices on items while you are in a raid, so you can judge what is " +
                    "worth the space.\n\n" +
                    "Prices come from a table loaded before the raid started - nothing is fetched " +
                    "from the server mid-raid, so this costs no performance.\n\n" +
                    "Turn it off if you would rather not know item values while looting.",
                    null, new ConfigurationManagerAttributes { Order = 97 }));

            TooltipDelayEntry = Config.Bind(SectionTooltips, "Tooltip delay (seconds)", 0.2f,
                new ConfigDescription(
                    "How long the cursor must rest on an item before its price tooltip appears.\n\n" +
                    "0.00 = instant. Prices show the moment the cursor touches an item, but sweeping " +
                    "across a full stash makes the mod calculate a tooltip for every item it passes " +
                    "over, which can show up as stutter.\n\n" +
                    "0.20 (default) = the cursor has to settle briefly. Items you skim past are never " +
                    "calculated at all, so fast looting stays smooth while a deliberate hover still " +
                    "feels immediate.\n\n" +
                    "1.00 = maximum pause. Cheapest possible, but you wait a full second on every item.\n\n" +
                    "Each item is only ever calculated once, so hovering something a second time is " +
                    "free no matter what this is set to.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = 96 }));

            _enableColorCoding = Config.Bind(SectionTooltips, "Color prices by value", true,
                new ConfigDescription(
                    "Tint prices by how valuable the item is, so you can judge it at a glance " +
                    "instead of reading the number.\n\n" +
                    "Grey is near worthless, then white, green, blue, purple, and orange for the " +
                    "most valuable.\n\n" +
                    "Based on price per inventory slot, not total price, so a large item has to be " +
                    "worth more to reach the same colour.",
                    null, new ConfigurationManagerAttributes { Order = 95 }));

            _useAmmoPenetrationTiers = Config.Bind(SectionTooltips, "Color ammo by penetration", true,
                new ConfigDescription(
                    "Color ammo by armour penetration rather than by price.\n\n" +
                    "A cheap round that punches through armour is worth more to you than an " +
                    "expensive one that does not, so price is a poor guide for ammo.\n\n" +
                    "Turn off to color ammo by price like everything else.",
                    null, new ConfigurationManagerAttributes { Order = 94 }));

            Config.Bind(SectionTooltips, "Refresh flea prices", string.Empty,
                new ConfigDescription(
                    "Fetch flea prices from the server again. Menu only, not during a raid.\n\n" +
                    "Prices are loaded once when the game starts. Use this if another mod has " +
                    "changed flea prices since then and you want the tooltips to catch up.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        CustomDrawer = RefreshFleaPricesDrawer,
                        HideDefaultButton = true,
                        Order = 93
                    }));
        }

        private void BindColorInfo()
        {
            // A read-only note rather than a setting: the colours themselves are arrays, which do
            // not fit the F12 menu, so this points at where they actually live.
            Config.Bind(SectionColors, "Where to edit colors", string.Empty,
                new ConfigDescription(
                    "Tier colors and their thresholds are edited in:\n\n" +
                    "BepInEx\\plugins\\QuickSell\\config.json\n\n" +
                    "TierColors      - six hex colors, cheapest to most valuable\n" +
                    "PriceTierThresholds - the price-per-slot bands those colors map to\n" +
                    "AmmoTierThresholds  - the penetration bands used for ammo\n" +
                    "BestTradeColor  - highlight on whichever option pays more\n\n" +
                    "They are in a file rather than here because each one is a list of values, " +
                    "which this menu cannot edit.\n\n" +
                    "Restart the game after editing. Delete the file to go back to defaults.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        CustomDrawer = ColorInfoDrawer,
                        HideDefaultButton = true,
                        Order = 100
                    }));
        }

        private void RefreshFleaPricesDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button("Refresh flea prices now", GUILayout.ExpandWidth(true)))
            {
                // Cached tooltips hold the old prices, so they go with them.
                TooltipPatch.Invalidate();
                FleaPriceClient.ForceRefresh();
            }
        }

        private static GUIStyle _listStyle;

        private void AvailableTradersDrawer(ConfigEntryBase entry)
        {
            var value = _availableTraders?.Value;

            if (string.IsNullOrWhiteSpace(value))
            {
                GUILayout.Label("Load your profile to see the list.", GUILayout.ExpandWidth(true));
                return;
            }

            // wordWrap is what stops the list being cut off after the first line. Built once and
            // reused, since a drawer runs every frame the menu is open.
            _listStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };

            GUILayout.Label(value, _listStyle, GUILayout.ExpandWidth(true));
        }

        private void ColorInfoDrawer(ConfigEntryBase entry)
        {
            _listStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };

            GUILayout.Label(
                "Colors and thresholds are edited in BepInEx\\plugins\\QuickSell\\config.json - " +
                "they are lists of values, which this menu cannot edit. Restart the game after " +
                "changing them.",
                _listStyle, GUILayout.ExpandWidth(true));
        }

        // ------------------------------------------------------------------ trader blacklist

        private static HashSet<string> _blacklistCache;

        /// <summary>
        /// True when a trader should be skipped. Matches on display name OR id.
        ///
        /// Matching the id as well matters for non-English clients, where LocalizedName is
        /// translated and an English name typed into the box would silently never match.
        /// </summary>
        public static bool IsBlacklisted(string traderName, string traderId)
        {
            var blacklist = _blacklistCache ??= ParseBlacklist(_tradersBlacklist?.Value);
            if (blacklist.Count == 0) return false;

            return (traderName != null && blacklist.Contains(traderName.Trim()))
                || (traderId != null && blacklist.Contains(traderId.Trim()));
        }

        private static HashSet<string> ParseBlacklist(string raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return set;

            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }

            return set;
        }

        /// <summary>
        /// Fills the reference list in the F12 menu once traders are known. Called by TraderService
        /// when it first loads them, because they do not exist at startup.
        /// </summary>
        public static void PublishTraderList(IEnumerable<string> traderNames)
        {
            if (_availableTraders == null) return;

            var names = traderNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            if (names == null || names.Length == 0) return;

            _availableTraders.Value = string.Join(", ", names);
        }

        // ------------------------------------------------------------------ colour config file

        private void LoadColorConfig()
        {
            try
            {
                var modPath = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (string.IsNullOrEmpty(modPath)) return;

                var configPath = Path.Combine(modPath, "config.json");
                if (!File.Exists(configPath)) return;   // defaults are fine; the file is optional

                var config = JObject.Parse(File.ReadAllText(configPath));

                if (config.ContainsKey("BestTradeColor"))
                    BestTradeColor = ((string)config["BestTradeColor"]).TrimStart('#');

                // Wrong-length arrays are rejected rather than partially applied: a four-entry
                // threshold list would silently mis-tier everything above it.
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
            }
            catch (Exception e)
            {
                Logger.LogError($"QuickSell: could not read config.json, using default colors: {e.Message}");
            }
        }
    }
}
