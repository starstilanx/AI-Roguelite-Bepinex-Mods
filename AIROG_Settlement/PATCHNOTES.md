# Settlement Mod — Patch Notes

---

## v1.1.1 — *No More Phantom Settlements*

### Fixed

- **The UI was fully interactive before any settlement existed.** Opening the panel from the HUD button let you spend the default state's 100 gold on buildings for a settlement with no location — and since production, population, and image generation all require a location, that gold was simply lost with no income ever arriving. The Buildings, Population, and Trade tabs now show a clear "travel to a location and press Establish Settlement" notice until a settlement actually exists, and build/upgrade actions are hard-gated as well. The sidebar shows "—" for resources in that state instead of the misleading defaults.
- **Starting gold raised 100 → 150.** Previously the two resource producers (Woodcutter 40 + Quarry 60) consumed exactly your entire starting purse, leaving the Farm — the prerequisite for population growth — reachable only through a slow wood-export grind. With 150 you can open with both producers and still afford a Farm once the first wood comes in.

---

## v1.1.0 — *Turns, Townsfolk & Upgrades*

### Fixed

- **Resource production is now tied to actual game turns.** Production previously ran on every save-file write, which can fire several times (or not at all) per turn — making income erratic and exploitable. It now hooks the game's `TurnHappenedEvent` and scales correctly when multiple turns elapse at once. The save hook now only persists state.
- **Image generation no longer runs on a background thread.** `Task.Run` was pushing game/Unity API calls onto a thread-pool thread, which Unity forbids — a crash/silent-failure risk. Generation is now a main-thread fire-and-forget async call.
- **Settlement image texture leak fixed.** The overview image re-read the PNG from disk and allocated a fresh `Texture2D` on every UI refresh without freeing the old one. Textures are now cached by image UUID and destroyed on replacement.
- **Establishing a new settlement is now a true fresh start.** Previously it reset your resources but silently carried over all buildings and residents from the old settlement.
- Removed dead prompt-injection helper — settlement context is injected by `AIROG_GenContext`'s `SettlementProvider` (which reads `settlement_data.json`), per the architecture guidelines.

### Added

- **Building upgrades.** Built structures can now be upgraded to Level 3. Each level multiplies production (this multiplier always existed in the data model — there was just no way to raise it). Upgrade cost = base cost × next level. The Buildings tab shows per-level costs and an Upgrade button.
- **Population system (Population tab is now live).** Residents settle in over time: requires a Farm (food), one capacity slot per completed building (max 6), ~20% arrival chance per turn. Each resident takes a job from one of your buildings (Farmer, Merchant, Barkeep, Stonemason, Lumberjack, Militia Guard) and pays 1 gold/turn in taxes — 2 if content. Arrivals are announced in the game log.
- **Happiness.** Resident morale derives from amenities (Tavern +20, Farm +15, Market +10, Barracks +5 on a base of 40) and is shown per-resident in the Population tab.
- **Settlement level now grows.** Level = 1 + (total building levels ÷ 2); shown to the AI via GenContext when above 1.
- **Population readout** in the right sidebar under the resource counters.
