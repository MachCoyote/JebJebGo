# JebJebGo

**JebJebGo is a complete rewrite of KSP's part drawer rendering and part search systems.**

It greatly speeds up switching tabs and searching for parts, and shines in heavily modded installs with a significant amount of parts, where it can cut multi-second search and tab-switch freezes to mere milliseconds.

## Speed

JebJebGo significantly speeds up both the time part searching takes, as well as the time it takes KSP to draw parts in the parts drawer. This manifests as an up to or over a 8x speedup in times to search parts and switch part tabs in the VAB.

## Non-Blocking

JebJebGo makes both part searching, and tab-switching, non-blocking, meaning the game no longer freezes when you switch tabs, or as you type into the part search bar.

## Fuzzy-Search

JebJebGo implements an improved fuzzy-search algorithm that includes more parts you'd expect when typing terms in. You can also configure it to use the stock algorithm's order aswell.

## Same UI

JebJebGo does not alter the search UI in any way. It's the same parts search you know and love, just way faster and infinitely less frustrating.

## Support

JebJebGo supports KSP 1.12.5, aswell as KSP ReTooled 1.13.0, with even greater time savings on ReTooled.

Effort has been made to keep JebJebGo compatible with other popular mods, though compatibility cannot be guaranteed.

JebJebGo is part of the vAlphabet suite.

## Installation

1. Install [Harmony](https://github.com/KSPModdingLibs/HarmonyKSP) (`GameData/000_Harmony`), if it is not installed already.
2. Copy the `GameData/JebJebGo` folder from the release zip into your KSP `GameData` folder. Delete any older `GameData/JebJebGo` folder first.

## Technical details

**Search.** The stock search box and its keystroke delay are kept. Matching runs on a background thread over an index of all loaded parts, built once at the main menu, so typing never waits for it; only the latest keystroke's results are shown. A part matches when any word of the query matches one of its words: exact, prefix, substring, or with one or two typos (not for numbers, so "1.25" does not match "1.5"). Designations can be typed without dashes ("lvt30"). Results are ranked, with titles weighted above manufacturers, part names, and tags.

**Parts drawer.** Stock creates and lays out an icon, a live 3D model, for every part in a search result or category, all in one frame. JebJebGo only creates icons for the rows on screen, a few per frame starting with the visible ones, reuses them, and sorts results from precomputed ranks instead of re-sorting the whole list on every change. The stock exclude, availability, and category filters still decide which parts are listed.

**Performance** (worst frame while searching or switching tabs):

| | Stock | JebJebGo |
| --- | --- | --- |
| Typing a search, 10,000 parts, ReTooled | 846 ms | 105 ms |
| Pasting a search, 10,000 parts, ReTooled | 871 ms | 37 ms |
| Opening a category, 10,000 parts, ReTooled | 1,250 ms | 40 ms |
| Broad search, 4,828 parts, KSP 1.12.5 | 2,472 ms | 43 ms |
| Opening a category, 4,828 parts, KSP 1.12.5 | 776 ms | 30 ms |

**Settings.** In the game's difficulty options, JebJebGo tab (saved per save):

* *Search results: best matches first* (on). Off keeps the list's sort order (Name, Mass, Cost, Size).
* *Janitor's Closet Filter Fix* (on). Janitor's Closet's mod filters re-check every part on every tab switch and search, which can take seconds on large installs. JebJebGo remembers their answers until a Janitor's Closet filter setting changes. The parts shown are the same.

**Launch options.** `-jebjebgo-disable` (stock behavior), `-jebjebgo-stock-order` (stock result order), `-jebjebgo-parity` (stock matching: every word must match), `-jebjebgo-no-hitch-log` (no freeze lines in the log).

**Diagnostics.** Editor frames over 100 ms log one `[JebJebGo] editor hitch` line in `KSP.log` showing how much of the frame was JebJebGo's own work. Part filters from other mods that take longer than 250 ms per refresh are named in a `[JebJebGo] slow parts list filters` warning.

**Failures.** If any part of JebJebGo fails, it logs one error and the stock search and parts drawer take over for the rest of the session.

**Tested with** KSP Community Fixes, EditorExtensionsRedux, Community Category Kit, Janitor's Closet, Hyperdrive, and VAB Organizer.

## Building

Requires the .NET SDK. Set `KSP_ROOT` to a KSP install (its `KSP_x64_Data/Managed` assemblies and `GameData/000_Harmony` are referenced), then:

```
dotnet build JebJebGo.slnx -p:Deploy=false
dotnet test tests/JebJebGo.Core.Tests
```

Without `-p:Deploy=false` the build copies the plugin into `KSP_ROOT/GameData/JebJebGo/Plugins`.

## License

MIT. See [LICENSE](LICENSE).
