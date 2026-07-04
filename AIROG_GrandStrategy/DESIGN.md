# AIROG_GrandStrategy — 4X Layer Design (v0.4.0)

A 4X experience layered on top of **AIROG_WorldExpansion** (hard dependency) and
**AIROG_GenContext** (soft, file-based). The player stops being just an adventurer
in the world sim and becomes a *power* in it: founding a Dominion, expanding
territory, exploiting holdings, waging wars, and pursuing victory — all while the
narrative AI acknowledges them as a sovereign.

The four X's, mapped onto what WorldExpansion already simulates:

| X | Feature | Backing system (already exists) |
|---|---------|--------------------------------|
| eXplore | Scout/spy missions reveal rival strength; political map lens | `StrategicMapUI` Voronoi lens, `FactionExtData` |
| eXpand | Annex orders claim real top-level Places; capital founding | `Place.faction`, `ClaimedPlaceUuids`, `TryExpandTerritories` pattern |
| eXploit | Holdings develop improvements (Farm/Mine/Market/Garrison/Watchtower) that yield treasury/pop/defense per strategic tick | minor tick (5-turn) cadence |
| eXterminate | Casus belli → war declaration → campaigns that flip native place ownership; faction elimination | `WorldData.DeclareWar`, `TransferNativePlaceOwnership`, `ActiveWars` |

## Core loop
1. **Found a Dominion** (`GS_FOUND <name>`): the native "Player" faction is
   registered into WorldExpansion's sim (`WorldData.Factions[playerFactionUuid]`),
   the current top-level Place becomes the capital (`Place.faction = player`).
   From then on the dominion participates in wars, diplomacy tiers, the political
   map, and prompt injection like any other faction.
2. **Strategic tick** rides WorldExpansion's minor tick (~every 5 turns) via a
   Harmony postfix on `WorldSimulation.RunMinorTick`:
   - Command Points regenerate (+2, cap 3)
   - Holdings yield treasury/population; unrest evolves
   - Enemy factions at war with the dominion may retaliate (raids/sieges)
   - Rebellion check: unrest ≥ 100 → holding secedes
   - Victory conditions checked
3. **Issue orders** (spend CP + treasury): resolved immediately, logged as
   *deeds*, surfaced to the narrative AI via GenContext.

## Orders (v0.1 engine, console-driven; UI in Phase 1)
| Order | Cost | Effect |
|-------|------|--------|
| ANNEX | 1 CP + 25 | Claim nearest unowned top-level place (native flip) |
| DEVELOP <IMP> [holding] | 1 CP + 30 | Add improvement to any holding (optional name; defaults to capital; max 3, unique) |
| LEVY | 1 CP | −100 population → +10 army strength, +5 unrest |
| TRADE | 1 CP | Treasury windfall scaled by market condition |
| ENVOY <faction> | 1 CP + 20 | ShiftTier +1 with target |
| FABRICATE <faction> | 2 CP + 40 | Gain casus belli against target |
| WAR <faction> | 2 CP | Declare war (needs casus belli or tier ≤ Hostile) |
| CAMPAIGN <faction> | 2 CP | Army battle; win → seize a place, lose → attrition |
| INCITE <faction> | 1 CP + 30 | Add grievance between target and a rival |
| SABOTAGE <faction> | 2 CP + 30 | −20 target resources; 35% discovery → native rep hit (feeds PlayerWorldActor bounty pipeline!) |
| PILLAGE <faction> | 2 CP | Raid a war enemy for 20–40 gold instead of land; adds grievance |
| PEACE <faction> | 1 CP + 25 | Sue for peace — if you're the dominant force you extract reparations; otherwise you pay |
| VASSAL <faction> | 2 CP | Weakened war enemy (res ≤ 40 or pop ≤ 300, army 15+) bends the knee: war ends, Alliance tier, tribute every tick |
| FESTIVAL | 1 CP + 25 | −15 unrest in every holding |
| PROJECT <wonder> | 1 CP + gold | Begin a great work in the capital (one at a time, 3 ticks) |
| SCOUT <faction> | 1 CP + 15 | Spy mission reveals a rival's resources, population, and estimated combat strength (55% precise, 45% noisy) |
| DISBAND | 1 CP | Demobilise 10 army → +50 population, −1 unrest per holding (trade military mass for civilian stability) |
| PACT <faction> | 1 CP + 15 | Swear non-aggression (`DiplomaticTier.NonAggression`) — relations must already be Cold War or better |
| TRADE_DEAL <faction> | 1 CP + 20 | Sign a trade pact (`DiplomaticTier.TradePact`) — standing income every strategic tick while it holds; needs PACT first |
| COUNCIL <role> | 1 CP + 40 | Recruit an advisor (MARSHAL/STEWARD/SPYMASTER/CHANCELLOR, one per role) who triples the odds their domain's petitions reach the throne |

Improvements (max 4/holding): **FARM** (−unrest, +pop), **MINE** (+6 treasury),
**MARKET** (+4 × market multiplier), **GARRISON** (+15 defense vs raids/campaign
defense), **WATCHTOWER** (halves enemy raid success), **SHRINE** (−2 unrest/tick),
**BARRACKS** (+1 army strength/tick, passive).

ANNEX and CAMPAIGN both accept an optional map-clicked target (DominionUI's 🎯
pick mode) in place of the automatic nearest-unclaimed / random-adjacent pick.

## v0.2.0 systems
- **Great works (wonders)** — capital-only, one under construction at a time:
  **CITADEL** (Grand Citadel, 80g: +30 capital defense, −2 capital unrest/tick),
  **MINT** (Royal Mint, 100g: +50% income), **TEMPLE** (High Temple, 90g: −3
  unrest everywhere/tick). Completion fires a DOMINION_WONDER player event.
- **Tax edicts** — `GS_TAX <LOW|NORMAL|HIGH>` persistent policy: HIGH = +50%
  income, +3 unrest/holding/tick; LOW = half income, −2 unrest/tick.
- **Vassalage** — vassals pay tribute each tick (clamp(res/10, 2..10), drained
  from their sim resources). Eliminated vassals lapse; a vassal whose tier the
  sim sours to ≤ Cold War **renounces vassalage** (DOMINION_VASSAL_REVOLT).
  Vassals count toward HEGEMONY.
- **Court petitions** (`CourtSystem.cs`) — 35%/tick when none pending: a random
  dilemma (toll relief, veteran pensions, blight aid, selling titles, holy
  procession, mercenary company) with flat gold/unrest/army deltas resolved at
  generation. Answer via `GS_PETITION ACCEPT|REJECT` (bare `GS_PETITION` re-reads
  it); lapses after ~3 ticks → +5 unrest everywhere. Queued as DOMINION_PETITION
  and injected into prompts so courtiers can press the matter in-fiction.

## v0.4.0 systems
- **Two-sided war** — a decisive enemy win in `CheckRetaliation` (attack > defense × 1.5,
  25% chance) seizes a non-capital border holding outright instead of just raiding it.
  The capital can never fall this way, but ordinary holdings now carry real war risk —
  the eXterminate axis finally cuts both directions.
- **Overextension** — every non-capital holding accrues up to +3 unrest/tick scaled by
  raw distance from the capital (~1 unrest per 300 world-units), a soft cap on sprawl
  that rewards contiguous territory over scattered conquests.
- **Espionage counter-play** — rivals holding grievance ≥ 3 against the dominion get a
  15%/tick chance to SABOTAGE (gold) or incite unrest in a holding — the same tools the
  player's own SABOTAGE/INCITE orders use, now aimed back.
- **Diplomatic pacts** — PACT and TRADE_DEAL ride WorldExpansion's own
  `DiplomaticTier.NonAggression`/`TradePact` tiers (already read by `WorldSimulation`'s
  own grievance logic), so a trade pact is a real, sim-visible relationship, not
  dominion-only bookkeeping. Trade income (+5g/tick/partner) is derived live from tier
  each strategic tick — no separate state to desync.
- **Stale claims** — fabricated casus belli now expire ~40 turns (~8 ticks) after
  fabrication (`CasusBelliExpiry`) if never spent on a WAR declaration.
- **Council of Advisors (a first slice of Phase 2)** — COUNCIL recruits one advisor per
  role (Marshal/Steward/Spymaster/Chancellor, procedurally named, no AI call). A
  recruited advisor triples the selection weight of `CourtSystem` petitions tagged with
  their role; 8 templates now span all four roles (2 new Spymaster petitions added).
  Advisors are injected into GenContext with name + role + personality so the narrative
  AI can voice them.
- **WONDER AGE** — 4th victory condition: all three wonders standing in the capital.
- **Dominion map presence** — a gold "♛ Name  ⚔Army" label now floats above the
  capital's icon on the world map regardless of whether WorldExpansion's POL lens is on.
- **Map-click targeting** — a 🎯 toggle next to ANNEX/CAMPAIGN arms pick mode; the next
  left-click on a `MapLocation` (intercepted via a Harmony prefix on `OnPointerUp`, which
  also blocks the native travel-click while armed) supplies the specific place instead of
  the automatic nearest/adjacent choice.
- **GS_RENAME <name>** — rename an already-founded dominion.
- Chronicle crossover: founding, wonder completions, vassalization, and victories are
  recorded as milestone beats via the same soft reflection dependency AIROG_Insight uses
  (`AIROG_Chronicle.ChronicleManager.RecordBeat`) — a no-op if Chronicle isn't installed.

## Victory conditions (non-ending — roguelite continues, legacy title awarded)
- **DOMINATION** — own ≥ 50% of top-level places (min 5)
- **HEGEMONY** — every surviving faction at Alliance tier or vassal
- **GOLDEN AGE** — treasury ≥ 500, zero unrest, ≥ 5 holdings
- **WONDER AGE** — all three great works (Citadel, Mint, Temple) built

## Prompt injection
`AIROG_GenContext/ContextProviders/GrandStrategyProvider.cs` (Priority 75, right
after WorldContextProvider's 80) reads `{save}/grand_strategy_data.json` via stub
classes (same pattern as WorldStateProvider — **keep stubs in sync with
GrandStrategyData.cs**). Injects:

```
[DOMINION — <name>]
Capital: X | Holdings: N | Army: S | Treasury: T | CP: n/3
Holdings: Name (improvements) [unrest!], …
Casus belli against: …; Vassals: …
Recent deeds: last 3
[DOMINION GUIDANCE] • The player is the sovereign of <name>; NPCs inside its
holdings know them as their liege • high-unrest holdings show discontent • war
directives, victory-title directive
```

GrandStrategy itself never injects prompts (same rule as WorldExpansion).

## Phases
- **Phase 0 (this scaffold)** — data model, founding, strategic tick, order
  engine, retaliation, rebellion, victories, GenContext provider. Console-driven:
  `GS_FOUND`, `GS_STATUS`, `GS_ORDERS`, `GS_ORDER <TYPE> [target]`, `GS_TICK`, `GS_CP`.
- **Phase 1 — Dominion UI + map integration** (DONE as of v0.4.0): `DominionUI.cs`
  adds a "DOM" button on the world map beside the political lens button (same
  cloned-frame pattern), toggling a left-anchored control panel on `mapViewTrans`:
  status readout, all 18 orders as buttons, improvement/wonder/advisor/target cycle
  selectors, tax cycler, petition ACCEPT/REJECT, founding button (default name
  "Dominion of <player>"; console GS_FOUND for custom names), last-result line.
  Map-click targeting (🎯 pick mode on ANNEX/CAMPAIGN) and the capital label/army
  marker on the world map both shipped in v0.4.0.
- **Phase 2 — Council of Advisors**: v0.4.0 shipped the recruitment half (COUNCIL
  order, 4 procedurally-named roles, role-weighted petitions, GenContext voicing).
  Still open: AI-narrated order outcomes via AIAsker instead of templated text, and
  advisors initiating their own petitions proactively rather than only biasing
  CourtSystem's random draw. Also still open: sim factions targeting the dominion
  in their own random actions beyond the tick-based retaliation/espionage v0.4.0
  added (i.e. AI-driven wars the dominion didn't provoke).
- **Phase 3 — Free-text rule**: natural-language orders parsed via AIAsker into
  structured orders; advisor chat; succession/legacy events via Chronicle.

## Build & deploy
- Outputs to its own `bin/` (no GenContext OutputPath quirk); deploy
  `AIROG_GrandStrategy.dll` **and** rebuilt `AIROG_GenContext.dll` (provider) to
  the game's BepInEx/plugins alongside `AIROG_WorldExpansion.dll`.
- `[BepInDependency(com.airog.worldexpansion)]` enforces load order.
- Save file: `{save}/grand_strategy_data.json`, written every strategic tick and
  on order resolution (GenContext reads from disk).

## Theme system (v0.5.0)
The dominion layer is setting-agnostic. `Themes.cs` defines a `ThemeLexicon`
(ruler title, domain noun, banners/colors, currency word + short suffix, weapons,
soldiers, court noun, petition noun, vassal noun, panel icon, advisor role titles,
advisor name pools, wonder display names) with five presets: MEDIEVAL, POSTAPOC,
SCIFI, MODERN, GENERIC. Every player-facing string in OrderSystem, CourtSystem,
DominionManager, GrandStrategyPlugin, DominionUI, and the GenContext provider is
voiced through the lexicon; mechanical keys (MINT, MARSHAL, ...) stay stable for
save compatibility.

- **Auto-detection:** at founding (and lazily for legacy saves via
  `Themes.EnsureTheme` on the first tick / panel open / GS_STATUS), the universe
  name+description and world backstory are keyword-scored; zero hits fall back to
  GENERIC. The world's own AI-generated currency name
  (`manager.GetCurrentOrDefaultCurrency().nme`) overrides the preset currency.
- **Overrides:** `GS_THEME <MEDIEVAL|POSTAPOC|SCIFI|MODERN|GENERIC|AUTO>` console
  command, plus a `THEME: <key> >` cycler button in the Dominion panel.
- **Persistence:** the resolved lexicon is serialized inside
  `grand_strategy_data.json`, so `GrandStrategyProvider` (separate assembly) reads
  the exact same terms via its `LexStub` and never hardcodes flavor. The provider
  also emits a standing directive telling the AI to express dominion matters in
  the world's own idiom and never assume medieval royalty.
