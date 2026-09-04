using SemanticVersioning;
using SPTarkov.Server.Core.Models.Spt.Mod;
using System.Collections.Generic;

namespace BlackHawk.QuickSell.Server
{
    /// <summary>
    /// Mod identity.
    ///
    /// IModMetadata is an INTERFACE in 4.1 (it was the abstract record AbstractModMetadata in 4.0),
    /// so every member has to be implemented here and none of them are overrides. IsBundleMod is
    /// gone - the server looks for a bundles.json in the mod folder instead of trusting a flag.
    ///
    /// ModGuid and Version must match the client plugin exactly: the Forge requires every version
    /// declared within one mod to agree, and the shared GUID is how the two halves are recognised
    /// as a single mod.
    /// </summary>
    public record ModMetadata : IModMetadata
    {
        public string ModGuid { get; init; } = "com.blackhawk.quicksell";
        public string Name { get; init; } = "QuickSell";
        public string Author { get; init; } = "BlackHawk";
        public Version Version { get; init; } = new("3.3.0");

        // Pinned to 4.1.3 and above within 4.1. The server refuses to load a mod built against a
        // different core version, so this should track what it was actually built and tested on.
        public Range SptVersion { get; init; } = new(">=4.1.3 <4.2.0");

        public string License { get; init; } = "MIT";
        public string Url { get; init; } = "https://github.com/TadMaj/Tarkov-QuickSell";

        // Original author credited here as well as in the README, since this is what the server
        // and the Forge read.
        public List<string> Contributors { get; init; } = new() { "TadMaj (original mod)", "Tyfon (UI Fixes interop)", "925316 (flea duplicate-offer fix)" };

        public List<string> Incompatibilities { get; init; } = new();
        public Dictionary<string, Range> ModDependencies { get; init; } = new();

        // No prepatcher: prepatching exists to extend Core's enums before it loads, and this mod
        // only reads prices.
        public bool HasPrepatcher { get; init; } = false;
    }
}
