# QuickSell — SPT 4.1.x

**Fast trader and flea selling for SPT, with price tooltips, multi-select support, safe flea pricing, and low-overhead caching.**

This is an SPT 4.1.x port and continuation of the original QuickSell mod.

- **Original mod:** [TadMaj](https://github.com/TadMaj/Tarkov-QuickSell)
- **Multiselect interop:** Tyfon (UI Fixes)
- **Price tooltip approach inspired by:** SwiftXP (Show Me The Money)
- **License:** MIT (unchanged)

If the original author asks me to take this port down, I will remove it.

---

## Features

### Quick selling

- **Sell to the best trader automatically**
  - QuickSell checks the available traders and picks the one paying the most.
  - Attached parts are included in the value.
  - Modded traders are supported automatically.
- **Sell directly to the flea market**
  - Right-click an item and choose **QuickSell (Flea)**.
  - Flea listing price can be adjusted from **25% to 200%** of the calculated market price.
- **Context-menu selling**
  - **QuickSell (Trader)**
  - **QuickSell (Flea)**
- **Keyboard shortcuts**
  - Default flea key: **N**
  - Default trader key: **M**
- **Confirmation window**
  - Shows the sale/listing total before anything is sold.
  - Flea confirmation also shows the listing fee and net profit.
- **Sell from containers**
  - Can sell items directly from backpacks, rigs and pockets.
  - Secure-container selling has its own safety option.
- **Trader blacklist**
  - Exclude traders you never want QuickSell to use.
  - Works with normal and modded traders.

### Multi-select selling

With **UI Fixes** installed, QuickSell can sell multiple selected items in one operation.

- One confirmation for the selection.
- Each item still goes to its best trader when trader selling.
- Flea listings are created for the selected items.
- Compatible UI Fixes integration is detected automatically.

### Price tooltips

QuickSell adds price information directly to item tooltips.

- **Best trader price**
- **Flea market price**
- **Colour-coded value tiers**
- **Ammo penetration-based colouring option**
- Works with:
  - Weapons and attached parts
  - Nested containers
  - Large backpacks/cases
  - Ammo stacks
  - Meds and other consumables
  - Modded items where normal EFT pricing data is available

The flea tooltip shows the price QuickSell is configured to list at, so changing the F12 flea percentage also changes the displayed flea value.

### Flea pricing

QuickSell uses the currently generated flea offers when available instead of relying only on the underlying base-price table.

- Current generated flea offer averages are preferred.
- SPT dynamic/base prices are used as a fallback.
- Weapons use the base weapon price while attachments are added separately to avoid double-counting.
- Manual **Refresh flea prices** is available from F12.
- Refresh keeps the previous completed table active until the new table is ready, so tooltips do not temporarily lose their flea value.

### Stack and consumable pricing

QuickSell accounts for the actual item being sold.

- **Ammo stacks:** tooltip shows the value of the entire stack, not one round.
- **Meds:** price scales with remaining HP/resource.
- **Food and drinks:** price scales with remaining resource.
- **Repair kits:** price scales with remaining repair resource.
- **Generic resource items:** remaining resource is taken into account when supported by EFT's normal resource component.

Example: an item with roughly 50% of its usable resource remaining is valued at roughly 50% of its full flea value before applying your configured flea percentage.

### Sound controls

F12 includes a dedicated **4. Sounds** section.

- **Trader sell sound** — On / Off
- **Flea listing sound** — On / Off
- **Sound mode**
  - **Once per batch**
  - **Per item**

### Flea selling control

QuickSell includes an option to disable flea selling while keeping flea buying available.

When disabled, QuickSell blocks both its own flea-sale action and normal flea listings.

### Performance

The current QuickSell pricing system is designed so hovering items stays very cheap.

- Player inventory prices are **precomputed** rather than rebuilding full container trees on every hover.
- Large backpacks and cases use finished cached totals.
- Inventory add/remove/refresh events reconcile the cache away from the hover path.
- No per-frame flea-price polling.
- No frame-spread price calculator.
- No worker thread touches live EFT inventory objects.
- Trader assortments are loaded lazily.
- Repeated flea prices are cached by template.
- Multi-item trader selling avoids unnecessarily pricing the same item twice.

Optional **Debug logging** in F12 writes lightweight `[QSPERF]` diagnostics to `LogOutput.log`.

---

## Installation

1. Extract the release ZIP into your **SPT root folder**.
2. Client files go to:
   `BepInEx\plugins\QuickSell\`
3. Server files go to:
   `SPT_Runtime\user\mods\QuickSell\`
4. Start SPT.

When upgrading from v4.0.0, delete:

`BepInEx\plugins\QuickSell\QuickSell.TestFixes.dll`

All client code is now packaged into `QuickSell.dll`.

**Supported SPT:** 4.1.3–4.1.x.

**Optional:** UI Fixes 6.0.0+ for multi-select selling.

### Server component

The server component is optional but recommended.

With it:

- The full flea price table is loaded at startup.
- Flea tooltips work in raid.
- Manual refresh can fetch the current table in one request.

Without it, QuickSell falls back to per-template lookups outside raid.

### Fika

- Install `QuickSell.dll` on **playing clients**.
- Install the QuickSell server component on the SPT server/headless host.
- Do **not** install the client DLL as an active gameplay plugin on the Fika headless client.
- QuickSell also detects a headless client and disables its client-side plugin if installed there accidentally.

---

## Configuration

All normal settings are available in the **F12 menu**.

### 1. Selling

Includes:

- Trader context-menu entry
- Flea context-menu entry
- Confirmation dialog
- Flea listing percentage (**25%–200%**)
- Flea selling enable/disable
- Offer-slot handling
- Sell-from-container options
- Secure-container option
- Trader blacklist
- Keybinds

### 2. Tooltips

Includes:

- Enable/disable price tooltips
- Best trader price
- Flea price
- In-raid price display
- Tooltip delay
- Value colouring
- Ammo penetration tiers
- Manual flea-price refresh

### 3. Colors

Tier colours and thresholds are stored in:

`BepInEx\plugins\QuickSell\config.json`

The file contains the colour arrays and price/ammo tier thresholds that are not practical to edit as individual F12 entries.

Delete the file to restore its defaults.

### 4. Sounds

Includes:

- Trader sell sound
- Flea listing sound
- Once-per-batch / per-item mode

### 9. Debug

Optional `[QSPERF]` performance/cache diagnostics.

---

## Network activity

QuickSell only talks to your own SPT server.

The client fetches the flea price table:

- Once at startup
- When you manually request a flea-price refresh

There is:

- No external QuickSell server
- No telemetry
- No analytics/data collection
- No periodic network polling during raids

---

## Uninstalling

Delete:

- `BepInEx\plugins\QuickSell\`
- `SPT_Runtime\user\mods\QuickSell\`

QuickSell does not permanently modify your profile.

---

## Changelog

Version-specific changes are kept in `docs/changelog/`.

The README describes the **complete current feature set**, not only the latest release.

---

## License

MIT, unchanged from upstream. See `LICENSE.txt`.

---

### Support / Donations

If you'd like to support my work, you can [buy me a coffee](https://ko-fi.com/its_blackhawk) ☕


**Solana (SOL)**  
`RtkZyhpCD7rAmcsocf3RWPizCCzEdGNuUAX7sBcD1pF`

**Bitcoin (BTC)**  
`bc1qj983ved6kp4uz6r6t285j0r5tyf5h6jph9eajl`

**Ethereum (ETH)**  
`0xEaA910a5A779C85Fb9F07CA22bd2d2847a5969A5`

Please double-check the address and network before sending.
