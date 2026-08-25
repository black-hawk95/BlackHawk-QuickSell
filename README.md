# QuickSell — SPT 4.1.3

**SPT 4.1.3 port of QuickSell.**
**All credit goes to the original author.**

- **Original mod:** TadMaj — https://github.com/TadMaj/Tarkov-QuickSell
- **Multiselect interop:** Tyfon (UI Fixes)
- **Price tooltip approach inspired by:** SwiftXP (Show Me The Money)
- **License:** MIT (unchanged)

---

Sell stash items fast — to whichever trader pays most, or straight onto the flea.

- **Context menu:** right-click any stash item for **QuickSell (Flea)** and **QuickSell (Trader)**
- **Keybinds:** `N` for flea, `M` for traders (configurable in F12)
- **Price tooltips:** hover any item to see the best trader price and the flea price
- **Multi-select:** with UI Fixes installed, select many items and sell them in one confirmation

Traders are picked automatically by best offer. Flea listings use the average market price.

## Installation

1. Extract into your **SPT root folder** (the one containing `BepInEx\` and `EscapeFromTarkov_Data\`)
2. Client files land in `BepInEx\plugins\QuickSell\`
3. Server files land in `SPT_Runtime\user\mods\QuickSell\`
4. Start SPT

**Requires SPT 4.1.3** (EFT `0.16.9.5.40743`). Will not work on 4.0.x.

**Optional:** [UI Fixes](https://sp-mod.com/mods) 6.0.0+ for multi-select selling.

**The server component is optional but recommended.** Without it, flea prices are looked up one item at a time and are unavailable in raid. With it, the full price table loads once at startup and works everywhere.

**Fika:** works on client and server. **Do not install on a headless client** — it detects headless and disables itself, but there is no reason to put it there.

## Configuration

`BepInEx\plugins\QuickSell\config.json`:

| Key | Default | Meaning |
| --- | --- | --- |
| `EnableQuickSellFlea` | `true` | Show the flea context menu entry |
| `EnableQuickSellTraders` | `true` | Show the trader context menu entry |
| `ShowConfirmationDialog` | `true` | Ask before selling |
| `TradersBlacklist` | `[]` | Trader names never to sell to, e.g. `["Fence"]` |
| `AvgPricePercent` | `100` | List at this % of the average flea price |
| `IgnoreFleaCapacity` | `false` | Skip the free-offer-slot check |
| `DisableKeybinds` | `false` | Turn off `M` / `N` |
| `ShowPriceTooltips` | `true` | Price lines on item tooltips |
| `ShowTraderPriceInTooltip` | `true` | Show best trader price |
| `ShowFleaPriceInTooltip` | `true` | Show flea price |
| `ShowPricesInRaid` | `true` | Show prices while in a raid |
| `EnableColorCoding` | `true` | Colour prices by value tier |
| `UseAmmoPenetrationTiers` | `true` | Colour ammo by penetration, not price |
| `BestTradeColor` | `FFFFFF` | Colour for the better of the two options |
| `PriceTierThresholds` | `[900, 12000, 21000, 38000, 92000]` | Tier bounds, price per slot |
| `AmmoTierThresholds` | `[15, 25, 34, 43, 55]` | Tier bounds, penetration |
| `TierColors` | WoW scheme | 6 hex colours, poor → legendary |

Threshold and colour arrays must have exactly 5 and 6 entries respectively; wrong-length arrays are rejected with a log warning and the defaults kept.

**F12 menu** has the keybinds and a **Refresh flea prices** button.

## Uninstalling

Delete `BepInEx\plugins\QuickSell\` and `SPT_Runtime\user\mods\QuickSell\`. Nothing is written to your profile.

---

## Notes for the curious

### Performance

The mod is built to cost effectively nothing during a raid.

- **Keybinds check the key first.** `ItemUiContext.Update` runs every frame; the keybind test used to be the *last* thing in it, so every frame paid for reflection, LINQ and a `GetComponent` before discovering no key was pressed. Now it is two field reads and a return.
- **No per-frame allocation.** The reflection cache was keyed on an interpolated string (a new allocation every frame) and UI Fixes' `Count` was a reflection invoke (an array plus a boxed int, every call). Both are now allocation-free.
- **Trader assortments load lazily.** The original hooked the `Trader` constructor and force-refreshed every trader at startup, holding all of it for the session — carried through every raid whether or not you sold anything. Now nothing loads until a sale or price lookup needs it.
- **Bulk selling prices each item once.** It used to find the best trader once to build the confirmation total and again to perform the sale: 30 items meant 60 full trader sweeps.
- **Flea prices are fetched per template, not per item.** Selling 20 identical items fired 20 identical requests. Price is a property of the template, so one lookup answers for all of them.

### Server load

The price table is fetched **once per client at startup**, never on a timer. On a Fika server with ten players that is ten requests total, at menu time where latency is invisible — rather than a poll multiplying by player count forever, hitting players on distant continents hardest. The server caches the computed table so simultaneous connections cost one computation.

Prices are never fetched during a raid.

### Modded traders

Nothing is hardcoded. The trader list comes from the server, and any trader that does not buy an item is skipped. Traders released in future work with no changes. The list is re-queried when empty, which also fixes traders unlocked mid-session (quest-gated modded traders, Lightkeeper) not appearing until restart.

## Building

**Client:** put in `[SPT root]\Development\Tarkov-QuickSell\`, then `dotnet build -c Release`
**Server:** `dotnet build -c Release` in `QuickSell-Server`

Both accept `-p:SptPath="C:\Path\To\SPT"` if they live elsewhere. `SptPath` is the folder containing `BepInEx\` and `EscapeFromTarkov_Data\` — not `SPT_Runtime\`.

Client and server versions must stay identical (`3.1.0`).

## AI assistance disclosure

This port and its added features were produced with substantial AI assistance. Type renames were taken from SPT's official [class name mappings](https://wiki.sp-tushonka.com/) and member renames were read directly from the installed `Assembly-CSharp.dll` rather than guessed, but the code was largely AI-generated.

The Forge requires the **"Contains AI Content"** flag to be enabled for any mod produced with LLM assistance.
