# AIROG_WorldExpansion Patch Notes

## v1.3.0 — Grand Strategy: Political Map Lens (07/02/2026)

Phase 1 of the Grand Strategy layer (modeled on Pax Historia-style AI grand
strategy): a **political map mode** overlaid on the game's own world map.

### New: Political Map Lens (`StrategicMapUI.cs`)
- **"Political: ON/OFF" button** on the world map (cloned next to the
  jump-to-current-location button). Toggles a territory overlay on the map's
  WORLD view; state persists while browsing.
- **Territory cells** — every top-level place gets a Voronoi cell (computed by
  half-plane clipping over place `worldCoords`, tagged with the neighbor across
  each edge) tinted with its owner's **native faction color**. Ownership =
  native `Place.faction` first, then mod `ClaimedPlaceUuids`; eliminated
  factions excluded. Unclaimed land shows as faint gray.
- **War fronts** — borders between territories of factions currently at war
  render as thick red edges (adjacency comes free from the bisector tags).
- **Player cell** — the territory you're standing in gets a gold border.
- **Faction labels** — each faction's name is drawn on its largest territory,
  in a brightened version of its color.
- **Legend panel** (right side) — turn/season/economy header, then each
  land-holding faction: color swatch, standing toward the player, territory
  count, population + state, active wars, and a ☠ BOUNTY marker when they've
  put a price on your head. Unclaimed-place count at the bottom.
- Overlay renders via a custom `PoliticalCellGraphic` (triangle-fan fill +
  per-edge border quads); no dependency on the game's gnarly `GetNextBigPoly`
  region tracing. Cells are non-raycast so map dragging/clicking still works;
  place icons are re-raised above the overlay.
- Lens hides itself in DETACHED and UNIVERSE map modes.

### New console command
- `WORLD_MAP` — opens the world map with the political lens forced on.

### New: Organic Territorial Expansion (`WorldSimulation.cs`)
- **Factions grow** — each minor tick, a healthy faction (≥50 resources, not
  Struggling/Razed, 35% chance) annexes the nearest unclaimed top-level place
  to its territory for 25 resources, setting **native `Place.faction`** so the
  claim is visible in-game. Borders creep toward each other until war fronts
  actually meet on the political map.
- **Fair-share cap** — a faction stops expanding at `places / factionCount`
  (min 3), so nobody eats the whole map.
- **Contiguous homelands** — initial seeding now claims 2–4 places (was 1–3)
  clustered around a random anchor instead of scattered picks.
- New event type **TERRITORY** (tan) in World News with its own filter button;
  a TERRITORY_CLAIMED world alert fires only when the annexed place is the one
  the player is standing in.

### Lens fixes (post-screenshot)
- **Overlay alignment** — cells/labels are now pinned to the map container's
  pivot (the space MapLocation icons use) instead of its rect center; the whole
  overlay was offset on maps with asymmetric offsets.
- **Border de-cluttering** — bounding-box edges are no longer drawn, wilderness
  ↔ wilderness borders are hairline-faint, and only edges touching owned
  territory get a real outline.
- **Faction labels auto-scale** to their cell (√area vs name length, 9–30pt;
  was fixed 54) so names no longer sprawl across the map.
- **POL button** — the toggle was a pixel-identical clone hidden behind the
  map's button column; it now sits to the LEFT of the column with its own
  "POL" face, gold when the lens is active.
- **Disc-bounded cells** — on clustered maps, claimed hull cells exploded into
  huge wedges reaching the bounding box, with labels stranded in the void.
  Every cell is now also clipped to a 12-gon "disc" around its own place
  (radius ≈ 1.7× median nearest-neighbor spacing), so territory hugs the
  places it belongs to; owned blobs get an outline on the rim.
- **Compact legend** — one line per faction (swatch · name · standing ·
  territories · pop · wars/bounty), capped at 10 entries with "+N more", so it
  no longer covers the Travel Enhancement panel and map button column.

### Housekeeping
- Plugin version 1.2.0 → 1.3.0.

## v1.2.0 — Player as World Actor, Real Territories (07/02/2026)

### New: Player as World Actor (`PlayerWorldActor.cs`)
The world simulation now reacts to the game's **native** player↔faction reputation
(`Faction.playerFactionRep`, changed by the AI via `DeltaRep`):
- **Grievances & bounties** — repeated rep losses (≤ −10) with a faction accumulate
  grievances; at 3+ grievances while Scorned or worse, the faction places a **bounty**
  on the player (world event + AI alert + World News section). Goodwill (≥ +10) erodes
  grievances and lifts the bounty once standing recovers to Neutral.
- **Standing tick** — every minor tick, Scorned-or-worse factions may harass the player
  (bounty hunters, denouncements, hostile watch); Admired+ factions may honor them
  (envoys, gifts, songs). At most one player-targeted alert live at a time.
- **War ripples** — a faction declaring war on a player-Trusted faction loses rep with
  the player (−10) and a WAR_SUSPICION alert fires; if the player is close to both
  sides, a TORN_ALLEGIANCE alert fires instead.
- **Faction falls are personal** — if a player-Trusted faction falls, the victor loses
  rep (−15) and an ALLY_FALLEN alert fires; if a hostile faction falls, its bounty is
  cleared and an ENEMY_FALLEN alert fires.
- New event type `PLAYER` (World News filter + color), new pending-event types:
  FACTION_BOUNTY, BOUNTY_LIFTED, FACTION_HOSTILITY, FACTION_HONOR, WAR_SUSPICION,
  TORN_ALLEGIANCE, ALLY_FALLEN, ENEMY_FALLEN.

### New: Real Territories
- Faction territories are now **real Place UUIDs** (top-level places), not
  `territory_*` placeholder strings. Seeding first adopts places the game already
  assigns to the faction (`Place.faction`), then landless factions claim 1–3 unowned
  places. Old saves are migrated automatically (fake IDs stripped, re-seeded).
- **Conquest flips native ownership** — captured/absorbed territories set
  `Place.faction` to the victor, so the change is visible in-game (place icon,
  native prompts), and conquest events name the actual place.
- World News header shows **"You are in X — territory of Y (standing)"** with an
  at-war marker; GenContext injects the same location context + a wartime directive.

### GenContext injection upgrades (WorldStateProvider)
- **Diplomacy line** — non-neutral pacts/rivalries (Alliance, Trade Pact, Cold War…)
  now reach the AI (previously only wars did).
- **Population states** — faction entries show thriving/struggling/razed.
- **Current Location** — territory owner, disposition toward player, active war.
- **Bounty directive** — active bounties tell the AI that bounty hunters, informants,
  and ambushes are fair game.
- **Two alerts** — up to 2 live pending world events injected (was 1).

### Fixes
- **World state now reaches the AI every turn** — the JSON is saved at end of each
  turn tick (and on bounty changes), not only when the game writes its save file.
  Previously, short-TTL alerts could expire before ever being injected.
- **Mid-game factions are now seeded** — per-faction lazy seeding replaces the
  one-shot `TerritoriesInitialized` flag, so factions generated later get territory
  and population.
- **Multi-turn skips no longer miss ticks** — accumulator counters replace
  `turn % N == 0` checks for minor/diplomacy/economy ticks.
- **War peace fixes** — wars can no longer end the turn they start (5-turn minimum);
  exhaustion peace names the actually-exhausted side instead of always claiming
  "both sides are exhausted".
- **Economy keyword matching is word-bounded** — "war" no longer matches
  "warm"/"reward"; stem keywords replaced with real words.
- **Housekeeping** — plugin version 1.0.0 → 1.2.0; removed the dead
  `WorldPromptInjection` no-op patch that ran on every AIAsker call.

### New console commands
- `WORLD_STATUS` — modal dump of turn/season, economy, wars, bounties, next major event.
- `WORLD_BOUNTY_TEST` — force a bounty from the first non-player faction.

## v1.1 — Diplomacy & Pending Events
- DiplomaticTier system (War→Alliance), PendingWorldEvent queue, GenContext handoff.

## v1.0 — Initial release
- Faction sim (raid/trade/rumor), seasons, economy, major events, World News tab, lore expansion.
