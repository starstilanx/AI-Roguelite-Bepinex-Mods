# AIROG_ALife — Patch Notes

## v2.0.0 (07/15/2026) — The Squads Live

Phase 1 of the A-Life grand project ("The Zone Lives"): squads stop being disposable
spawn records and become persistent characters with names, histories, grudges, and
opinions about you. Still **zero extra AI calls** — templates simulate, GenContext
narrates.

### New: Embodiment — two-way materialization (`ALifeEmbodiment.cs`)
- Meeting a squad no longer deletes it. Materialized members are tracked by uuid;
  when the player leaves, the visit is **reconciled** (deaths, defections, peace)
  and the squad resumes its offline life — with the SAME real `GameCharacter`s.
- Embodied squads move their real entities across the world offline (native
  place-list core of `SetAsChildOfPl` without its four per-call UI refreshes —
  embodied moves only ever happen off-screen).
- Offline battle casualties are applied to real members via `SetAsCorpse` — real
  corpses are left on the field for the player to find, and the game's native
  corpse decay culls them.
- Kill three of five raiders and flee: that squad limps on with two members.
  Recruit a member as a follower: the squad records the defection (and respects
  you more for it).
- `v2.PersistentSquads=false` restores the v1.0 one-way handoff.

### New: Leaders & dossiers
- Every squad has a **named leader** (syllable-built names, beast-names for wild
  packs) with kills, victories/defeats, and **earned epithets** ("the Red" →
  "Bloodhand" → "the Reaper"; "Who Walked Away" for sole survivors; "the Avenger"
  for heirs of the slain). Leaders materialize first, by name, with their record
  woven into their character description.
- **Succession**: when a leader dies — in battle, offline, or at the player's
  hand — a new leader rises; the band mourns, and the killer earns a feud.
- Squads keep a **chronicle** (capped history of deeds) surfaced in `ALIFE_DOSSIER`.

### New: Blood feuds (`squad ↔ squad`, deliberately NOT a player-nemesis system)
- Squads that survive a battle swear feuds against their victors; slain leaders
  add fuel. Heat ≥50 sends a squad **HUNTing** its enemy across the travel graph
  (live retargeting each hop). Feuding squads fight with fury (+15% power),
  override faction allegiances, and settle only in blood — or cool off over time.
- Wiping out a squad settles every feud sworn against it, and the world hears
  about it.

### New: The Wake — the world reacts to the player like an ecosystem
- No squad ever hunts the player (NPCExpansion's Nemesis owns personal grudges).
  Instead, squads accumulate **FEAR** (witnessed kills, spread one hop as rumor)
  and **AWE** (bloodless visits, defections to your side).
- Fear ≥60: the squad **flees before you arrive** — you find a hastily abandoned
  camp. Fear 30–59: hostile squads materialize **wary** — as NPCs who parley and
  hold back instead of attacking (the AI plays the standoff).
- **Dread zones**: places where you kill accrue dread; caravans and pilgrims
  reroute around killing grounds, and frightened fighters balk at crossing them —
  your violence physically reshapes travel patterns.
- **Player legend**: a global notoriety tally with tiers ("known and talked
  about" → "feared across the region" → "a walking legend"), injected via
  GenContext. Everything decays — reputations fade if you stop earning them.

### New: Squad lifecycle (`ALifeLifecycle.cs`)
- **Veterancy**: battles and raids grant XP; squads level up (`AvgLevel` rises,
  materialized members scale with it via native place-level spawning).
- **Recruiting** on home ground / owned territory; **merging** when two mauled
  same-faction squads meet; **splitting** when a band swells past 8 — the
  splinter marches under a newly named leader.
- **Defection**: faction squads with broken morale desert and turn renegade
  (pushed to WorldExpansion as world news).

### GenContext injection upgrades
- Squad lines now name leaders; per-squad regard notes ("they know of the player
  and are AFRAID — they will parley or withdraw"); blood-feud lines for visible
  squads; dread-zone flavor; `[REPUTATION]` legend tier; guidance directive tells
  the AI to play leaders as persistent characters and honor fear/awe honestly.

### New console commands
- `ALIFE_DOSSIER` — full per-squad dossier: leader record, regard, feuds, chronicle.
- `ALIFE_LEGEND` — player legend tier, dread zones, and who fears/respects you.

### Migration & housekeeping
- v1.0 saves migrate automatically (leaderless squads are assigned leaders on load).
- Named-NPC migration now excludes embodied squad members (they move with their
  squad, not on their own).
- Plugin version 1.0.0 → 2.0.0.

## v1.0.0 (07/12/2026) — Initial release

STALKER-style A-Life: a persistent offline population of squads that travel, fight,
and die across the world map while the player is elsewhere.

### Systems
- **Virtual squads** — six archetypes: PATROL / WARBAND / CARAVAN (faction-bound),
  RAIDERS, HUNTERS (wild packs), PILGRIMS. Population target scales with world size
  and active wars (`MaxSquads` config overrides). Zero AI calls — all names/descs
  are template-generated; the narrative AI embellishes via GenContext.
- **Travel graph** — k-nearest-neighbor adjacency over top-level `Place.worldCoords`
  (cap = 3× median NN spacing), rebuilt lazily on place-count change. Squads hop one
  edge per `TurnsPerHop` (2 caravans / 3 default / 4 hunters).
- **Goals** — patrols shuttle between owned places; warbands raid war enemies'
  territory (via WorldExpansion `ActiveWars` + `ClaimedPlaceUuids`, native
  `Place.faction` first); caravans run two-stop trade routes; hunters drift toward
  high-danger places; losers FLEE.
- **Offline battles** — hostile squads sharing a place fight: strength =
  size×(lvl+2)×morale×rand. Loser takes 40–80% casualties, flees or is wiped;
  events logged; faction-vs-faction wipes pushed into WorldExpansion's event log.
- **Hostility matrix** — hunters attack everyone (not each other); raiders prey on
  caravans + pilgrims; faction squads fight along WorldExpansion war lines.
- **Online bubble** — squads at the player's current top-level place are frozen;
  the real game owns everything there.
- **Materialization** — on `ApplyLocationChange`, one squad at the destination
  becomes real `GameCharacter`s (hostile → `NORMAL_MOB` enemies, neutral → NPCs;
  max 3/2 members; sync ctor with canned desc = no AI call; native
  `SetAsChildOfPl` + `PopulateGrdInfo` + grid sync). Squad record is then handed
  off to the game permanently.
- **Named NPC migration** — 8%/turn (config) one off-screen named NPC relocates to
  an adjacent top-level place via native `SetAsChildOfPl`. Excludes followers,
  merchants, the dead, and anyone at the player's location. Serializes natively
  (`parentPlaceUuid`), so migrations persist in vanilla saves.
- **GenContext provider** (`A-Life`, priority 78) — injects aftermath events at the
  player's location (25-turn window), squads present, and one-hop rumors, plus a
  guidance directive. Reads live in-process state.

### Persistence
`{saveDir}/alife_data.json`, written every turn tick + on WriteSaveFile;
loaded on LoadGame; reset on NewGame.

### Console commands
`ALIFE_STATUS`, `ALIFE_EVENTS`, `ALIFE_TICK` (force 3 turns), `ALIFE_BATTLE_TEST`,
`ALIFE_SPAWN_HERE` (spawn + materialize a hostile pack at the player).

### Config (BepInEx)
`EnableSimulation`, `MaxSquads` (0=auto), `MaterializeSquads`, `NamedNpcMigration`,
`NpcMigrationChancePerTurn`.

### Dependencies
Soft: AIROG_GenContext (prompt injection), AIROG_WorldExpansion (wars/territories/
world news). Both bridged with fail-safe wrappers — the mod runs standalone
without them.
