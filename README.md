# QuickSell — SPT 4.1.3

**SPT 4.1.3 port of QuickSell. All credit goes to the original author.**

- **Original mod:** [TadMaj](https://github.com/TadMaj/Tarkov-QuickSell)
- **Multiselect interop:** Tyfon (UI Fixes)
- **Price tooltip approach inspired by:** SwiftXP (Show Me The Money)
- **License:** MIT (unchanged)

This port was uploaded to keep the mod available on SPT 4.1.3. If TadMaj asks me to take it down, I will remove it immediately, no questions asked. Same goes if they would rather publish their own 4.1 version — this repo exists only until then.

---

Sell stash items fast — to whichever trader pays most, or straight onto the flea.

- **Context menu:** right-click any stash item for **QuickSell (Flea)** and **QuickSell (Trader)**
- **Keybinds:** `N` flea, `M` traders
- **Price tooltips:** best trader price and flea price on hover, colour-coded by value
- **Multi-select:** with UI Fixes installed, sell many items in one confirmation

Traders are picked automatically by best offer. Flea listings use the average market price.

## Installation

1. Extract into your **SPT root folder** (the one containing `BepInEx\` and `EscapeFromTarkov_Data\`)
2. Client files land in `BepInEx\plugins\QuickSell\`
3. Server files land in `SPT_Runtime\user\mods\QuickSell\`
4. Start SPT

**Requires SPT 4.1.3** (EFT `0.16.9.5.40743`). Will not work on 4.0.x.

**Optional:** [UI Fixes](https://sp-mod.com/mods) 6.0.0+ for multi-select selling.

**The server component is optional but recommended.** Without it, flea prices are looked up one item at a time and are unavailable in raid. With it, the full price table loads once at startup and works everywhere.

**Fika:** works on client and server. Detects headless clients and disables itself there.

## Configuration

Settings are in the **F12 menu** under QuickSell, in three sections:

- **1. Selling** — which context menu entries to show, confirmation dialog, flea listing price, trader blacklist, keybinds
- **2. Tooltips** — which prices to show, whether to show them in raid, tooltip delay, colour coding
- **3. Colors** — points to the config file

Tier colours and their thresholds are in `BepInEx\plugins\QuickSell\config.json`, because each one is a list of values that the F12 menu cannot edit. That file is optional; delete it to restore defaults.

### Blacklisting a trader

Use **Never sell to** in the F12 menu, comma-separated (`Fence, Ragman`). The **Traders on this profile** box below it lists every trader you can name, modded ones included, so you can copy from it. Names match case-insensitively, and a trader's ID works too if a translated name won't match.

## Uninstalling

Delete `BepInEx\plugins\QuickSell\` and `SPT_Runtime\user\mods\QuickSell\`. Nothing is written to your profile.

## Network activity

The client fetches the flea price table **once at startup** from your own local SPT server (`/quicksell/getFleaPrices`). No external servers, no telemetry, no data collection. Nothing is fetched during a raid.

---

## Notes for the curious

### Performance

Built so that a raid costs effectively nothing.

- **Keybinds check the key first.** `ItemUiContext.Update` runs every frame; the keybind test used to be the last thing in it, so every frame paid for reflection, LINQ and a `GetComponent` before discovering no key was pressed. Now it is two field reads and a return.
- **No per-frame allocation.** The reflection cache was keyed on an interpolated string (an allocation every frame) and UI Fixes' `Count` was a reflection invoke (an array plus a boxed int per call). Both are now allocation-free.
- **Trader assortments load lazily.** The original hooked the `Trader` constructor and force-refreshed every trader at startup, holding all of it for the session. Now nothing loads until a sale or price lookup needs it.
- **Tooltips are cached per item.** Building the price lines costs a few milliseconds, mostly in asking every trader what it would pay. An item's price does not change while it sits in the stash, so it is worked out once.
- **Bulk selling prices each item once.** It used to find the best trader once to build the confirmation total and again to perform the sale: 30 items meant 60 full trader sweeps.
- **Flea prices are fetched per template, not per item.** Selling 20 identical items fired 20 identical requests.

### Server load

The price table is fetched **once per client at startup**, never on a timer. On a Fika server with ten players that is ten requests total, at menu time where latency is invisible — rather than a poll multiplying by player count forever. The server caches the computed table so simultaneous connections cost one computation.

### Modded traders

Nothing is hardcoded. The trader list comes from the server, and any trader that does not buy an item is skipped. Traders released in future work with no changes.


## License

MIT, unchanged from upstream. See `LICENSE.txt`.

---

If you'd like to support my work, you can [buy me a coffee](https://ko-fi.com/its_blackhawk) ☕
