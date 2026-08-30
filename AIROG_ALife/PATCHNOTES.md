# AIROG_ALife — Patch Notes

## v2.3.1 (08/26/2026): Polish Pass

A code-review pass over the still-unshipped v2.3.0 changes before they ever hit a save.
No new features; several correctness bugs found and fixed.

### Fixed: loading a save could clobber its own A-Life data
`GameplayManager.LoadGame`'s own arrival call (`VisitPlace` calling `ApplyLocationChange`)
fires **fully synchronously** whenever the voronoi world was already visited, which is
the normal case on every load. That meant our `ApplyLocationChange` postfix ran, and
wrote `alife_data.json` to disk, **before** `LoadGame`'s own postfix ever got to read the
save's real A-Life state back in: overwriting the save being loaded with whatever was
already in memory (a previous save, or nothing at all), then reading that clobbered file
back.
> Fixed: arrival handling is suppressed for the duration of `LoadGame` and replayed once
> the correct save data is actually loaded, in `ALifePlugin.cs` / `ALifeMaterializer.cs`.

### Fixed: War Made Real could go permanently dark
The WorldExpansion bridge's failure latch was never reset. One transient exception (a
load-order race where WorldExpansion's state wasn't populated yet) disabled wars,
territory seizure, and court figures for the rest of the process, including after
loading into a healthy game or starting an entirely new one.
> Fixed: the latch clears on New Game and Load Game (`ALifeWorldBridge.cs`).

### Fixed: a landless faction's war could never end "in the field"
The decisive-war-end check (enough territory seized, or the loser has nothing left) only
ran when a battle actually seized a place that turn, so a faction that was *already*
landless going into a battle could never trigger it. Unopposed raids also updated the war
score but skipped the front-push/decisive-end check entirely, letting the ledger blow
past the threshold with no territory changing hands until an unrelated future battle
happened to notice.
> Fixed: both paths now share one front-push check that runs the decisive-end condition
> unconditionally (`ALifeWar.cs`).

### Fixed: a failed spawn could get a squad wrongly wiped
If every member-spawn attempt for a squad threw (a transient entity/faction lookup
failure), the squad was still marked `IsEmbodied` with an empty roster, so the very next
encounter treated it as a band that died with no survivors and erased it, even though it
never actually got a chance to appear.
> Fixed: a squad that spawns nobody stays virtual for a later retry (`ALifeMaterializer.cs`).

### Fixed: a few smaller drifts
- A band with nowhere to flee to (an isolated location with no reachable neighbor) was
  still dropped from the encounter list every visit, silently skipping it from
  materialization forever. It now only drops out once it actually relocates.
- The Rumors tab could show a band as merely "rumored" in the same turn the AI was told
  its name and leader outright as physically present. The fog-of-war and narration
  layers now agree on which co-located bands actually count as met.
- The RAIDERS hostility roll used `string.GetHashCode()`, which .NET randomizes per
  process, so the same squad could flip from friendly to hostile across a restart. Now
  uses a stable hash.
- `SpreadRumorOfPlayer` gave zero secondhand fear to a witnessing band that had already
  met the player, while a band a full hop away that had never met the player got a fear
  bump for hearsay: backwards from direct witnessing vs. rumor.
- The auto squad population cap double-counted active wars when `MaxSquads` was set to a
  fixed value, admitting more squads than intended.
- The travel-graph cache only invalidated on a place-*count* change, missing a same-tick
  swap (one place removed, a different one added); it now signs the actual place set.
- The cloned Rumors tab button used `RemoveAllListeners()`, which doesn't clear Unity's
  inspector-persistent listeners on a cloned prefab instance, so it could also fire the
  native tab button's own click handler. Matches the fix already used for the Tracks lens
  button.
- A couple of defensive guards: an empty place name no longer crashes RAIDERS squad
  naming, and a null console command string no longer throws.

All fixes verified building clean. Not yet play-tested in a live save.

## v2.3.0 (08/17/2026) — The Room Is Not About You

Field-report fixes. One bug produced both halves of the report: a band's presence
notice was re-announced on **every** location change, and those notices were fed
straight into the narrative prompt — so the Whispers feed filled with copies of
"…is here — and hostile" while the AI wrote every scene as if a predator pack
were standing in the room.

### Fixed: presence-notice spam (`ALifeMaterializer.cs`)
- `ApplyLocationChange` fires on every arrival — **including sub-location moves
  inside one settlement** (tavern → street → shop). The materializer logged its
  ENCOUNTER notice unconditionally on each one, even when zero characters spawned
  because the band was already standing there.
- A band is now announced **once per meeting**: only when real members are present
  (spawned or already embodied), and only on new ground or after a 25-turn
  cooldown (matched to the provider's event window, so a repeat can never share a
  prompt with the first).
- An **embodied band with no living members left** — deleted by the player, culled
  with a pruned place, lost to a stale save — is now retired via `WipeSquad`
  instead of silently re-spawning bodies the player got rid of.
- `DeathsThisVisit` moved to the top-level arrival boundary. It used to reset on
  every re-entry, so killing two raiders and stepping indoors wiped the tally and
  earned the band's respect for a "bloodless" visit.

### Fixed: bands pinned in the online bubble (`ALifeSimulation.cs`)
- `MoveSquads` skipped anything standing on the player's ground, so a band the
  player neither fought nor fled from was frozen there for as long as the player
  stayed — the reason one pack haunted a single location for 40+ turns.
- Bands now break camp and move on after `BUBBLE_LINGER_TURNS` (12) of stalemate,
  using the new `ALifeEmbodiment.RelocateVisibly` (full native move with UI
  refreshes, since the player is looking at this ground). `FleeFromPlayer` was
  refactored onto the same helper.

### Fixed: the whole tavern reacting to the player (`ALifeProvider.cs`, `ALifeLegend.cs`)
- ENCOUNTER notices are **excluded from the LOCAL ACTIVITY block**. They describe
  who is standing here *now*, which the "in this area" lines already report;
  filed as aftermath they read as a fresh threat every turn.
- `EventsAt` **collapses repeated lines** to their most recent telling and takes an
  `excludeType` filter.
- `[REPUTATION]` is now **gated on there being an audience** (a band here, one
  nearby, or a dread zone) and explicitly scoped: it applies to fighting bands and
  road-travellers, *not* to townsfolk, traders, innkeepers or staff. Injected
  unconditionally it reached every scene, and the AI duly had shopkeepers cowering
  at a stranger buying bread.
- Player legend is **capped at 60** and now fades on the same 4-turn cadence as
  fear and dread. The old rule only fired when the turn counter landed on a
  multiple of 12, so clearing a couple of bands early left the player "feared
  across the region" for the rest of the run.

### New: `[AMBIENT LIFE]` directive (`ALifeProvider.cs`)
- Every mod directive describes how the world regards the **player**, which leaves
  the AI nothing to write ordinary life from. The provider now always injects a
  counterweight: NPCs have their own business and standing with each other, most
  have no reason to react to the player at all, and *a stranger eating a meal is
  not the center of the room*.
- Config: `v2 / AmbientLifeDirective` (default on).

### Save compatibility
- Loading an older save **prunes duplicate ENCOUNTER notices** from the feed and
  clamps legend to the new ceiling, so an already-spammed save cleans itself up on
  first load rather than needing a restart.

## v2.2.0 (07/16/2026) — Whispers & Tracks

Phase 3 of "The Zone Lives": the simulation finally becomes **visible** — through
a fog of war. Deliberately genre-neutral (no "PDA"): rumors and tracks read as
naturally in a fantasy realm as in a wasteland or a starport.

### New: Fog-of-war knowledge (`ALifeKnowledge.cs`)
- The sim knows everything; the **player only knows what they've seen or heard**.
  Intel channels: meeting a band face to face (full dossier — leader, regard,
  feuds), rumor of bands within one hop (name + rough position), and walking the
  ground where something happened (arriving anywhere reveals its recent events).
- Intel **freezes when a band leaves earshot**: entries show last-known position
  with age ("rumored at Kraghold, 12 turns ago"), go visually stale after 15
  turns, show "(fate unknown)" when the band no longer exists, and are forgotten
  entirely after 150.

### New: "Rumors" journal tab (`ALifeRumorsUI.cs`)
- Header: your legend among the wandering bands.
- **⚔ Fronts** — every active war and how the field is going.
- **Known Bands** — fog-of-warred dossiers: leader (if met), last seen/rumored
  where and when, strength, activity; met bands show regard ("they fear you",
  "they respect you") and live blood feuds.
- **☠ Killing Grounds** — your dread zones and how long until travelers forget.
- **Whispers** — the rumor feed: only events you could actually know about,
  newest first, colored by type.
- Injection uses every 07/11-build lesson from WorldEventsUI: tab button re-wired
  on every `JournalModal.Init`, own TMP label ("RUMOR") on the icon-only button,
  no tab-count assumptions, `SoundManager.I` guarded, `RectMask2D` viewport.

### New: "Tracks" world-map lens (`ALifeTracksLens.cs`)
- A **TRK** toggle on the world map (auto-stacks left of WorldExpansion's POL
  button when both are installed) overlays:
  - **Band markers** at last-known positions — glyph by archetype (⚔ war parties,
    💰 caravans, 🗡 raiders, ☠ wild packs, · pilgrims), gold names for met bands,
    faded when stale ("The Gnashers? (18t)"), dagger † for bands whose fate is
    unknown; up to 3 stacked per place.
  - **⚔ battle sites** (recent known battles) and **☠ killing grounds**.
- Overlay pinned to `mapLocationsParent`'s pivot (the MapLocation icon space),
  rebuilt on every `ShowWorldView`, hidden in detached/universe modes; place
  icons re-raised so the map stays clickable. `ButtonPressEffect` removed from
  the cloned button (it caches child Graphics we replace).

### New console command
- `ALIFE_MAP` — arms the Tracks lens so it's on the next time the map opens.

### Config
- `v2.RumorsJournalTab`, `v2.TracksMapLens` (both default true).

### Housekeeping
- csproj now references UnityEngine.UI / UIModule / TextMeshPro / Localization.

## v2.1.0 (07/16/2026) — War Made Real

Phase 2 of "The Zone Lives": WorldExpansion declares the wars — **A-Life now fights
them**. War outcomes stop being dice rolls in an abstract sim and become the sum of
warband battles you could have witnessed (or joined).

### New: The war ledger (`ALifeWar.cs`)
- Every battle between squads of warring factions feeds a per-war **ledger**
  (+1 per victory, +1 for a wipe, +1 for a slain enemy leader). At a net lead of
  3, the winning side **pushes the front**: a real territory flips — mod claim
  AND native `Place.faction` — chosen as the loser's claimed place nearest the
  battle. World News reports the front moving after the specific battle that
  moved it.
- **Decisive victory**: three net territories seized through the ledger, or a
  landless loser, ends the war outright ("decided in the field — X's warbands
  broke Y") via WorldExpansion's own `EndWar` (tier reset, events, the works).
- **Attrition is real**: every lost battle drains the losing faction's treasury
  (−8), feeding WorldExpansion's exhaustion-peace check — even a stalemate
  ground out by squads eventually bleeds a war to its end.
- Ledger entries for wars that end by other means (peace, exhaustion) are pruned.

### New: Garrisons
- New squad goal **GARRISON**: patrols on home ground sometimes dig in
  (10–20 turns); warbands of a war's *defender* prefer holding their own soil —
  or march home to do so — rather than counter-raiding.
- Dug-in squads fight at **×1.25** on prepared ground and don't move until
  their watch ends.
- **Contested assaults**: a raid arriving at a defended place no longer lands
  automatically — "battle is joined", the encounter step fights it out, and the
  sack only happens once the field is won.
- Raids that DO land now have consequences: the victim's treasury bleeds (−5),
  the war ledger ticks, and WorldExpansion's court can lose figures to the raid
  (`RollRaidCasualty` — same odds as its own abstract raids).

### New: Court figures take the field
- When a warband musters during a war, a **faction-court lieutenant** may lead
  it (25%): the squad's leader IS that named figure — title and all — steadying
  morale (+10).
- If the warband is destroyed or its leader slain offline, the court records the
  death (`COURT` event) and mourns; if the figure had risen to faction Leader in
  the meantime, native court **succession** fires.
- If the squad materializes, the leader spawns under the figure's real name — so
  WorldExpansion's own name-matching binds them, and killing them in person
  drives the court's native succession with zero extra glue. Lieutenants never
  turn renegade with deserters and never lead two bands at once.

### GenContext injection
- Wartime territory line for the player's current region: "This is wartime
  territory (A vs B): the front favors A (2 territories taken)."

### New console command
- `ALIFE_WARS` — every war's front status, active garrisons, and court figures
  currently in the field.

### Config
- `v2.WarMadeReal` (default true) — master switch for the entire pillar.

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
