# Claude_PluginIdeas.md — Future Plugin Concepts

Brainstormed 2026-07-03. Ideas for new gameplay-expansion plugins, split into two tiers:
conventional expansions (proven genre mechanics adapted to AI Roguelite) and AI-native
concepts (mechanics only possible because the game runs on an LLM).

The unifying design thesis for the AI-native tier: **use the LLM as a judge of fuzzy
human concepts** — belief, promise-breaking, dramatic quality, resemblance — rather than
merely as a text generator. This is design space untouched by the existing 26 mods.

---

## Tier 1 — AI-Native Concepts (most novel)

### 1. AIROG_Hearsay — Rumor-into-Reality Engine ⭐ top pick
You can *lie to the world, and the world might make it true.*

- Player-spoken rumors ("there's a dragon in the northern mines") are captured via an
  extraction block (Insight pattern) and propagate through NPCExpansion's gossip network
  with a **belief score**.
- When belief crosses a threshold, the mod instantiates the rumored thing — the dragon
  now exists because enough people believed it.
- Speech becomes a crafting system: talk kingdoms into wars, fabricate heroes, invent
  treasures (which someone else might loot first).
- Inversion: the world spreads rumors about *you*; your reputation can reshape what you are.
- **Feasibility**: gossip substrate already exists (NPCExpansion), rumor capture =
  Insight's extraction-block pattern, materialization = WorldExpansion's
  PendingWorldEvent queue. Most buildable of the novel tier.

### 2. AIROG_Geas — Oath & Promise Enforcement
Promises become physics.

- Oaths sworn by the player (detected via extraction block) are recorded and
  metaphysically enforced: abandon a village you swore to protect → escalating curses;
  swear off swords → mechanical punishment for drawing one.
- The LLM adjudicates "did this action violate the *spirit* of that promise?" — fuzzy
  judgment rules engines can't do.
- Reverse gameplay: maneuver NPCs / faction leaders into oaths, then engineer situations
  where they must break them. A manipulation layer on top of GrandStrategy factions.

### 3. AIROG_Reverie — Dream Layer ✅ IMPLEMENTED (v1.0.0, 2026-07-03)
Your own play history becomes a procedurally haunted second world.
See `AIROG_Reverie/DESIGN.md` for the built architecture.

- Resting sometimes drops the player into a playable dream: surreal recombination of
  Chronicle beats — places stitched together wrong, dead NPCs speaking, inferred fears
  given form.
- Low stakes on the body, high stakes on the mind: solve a dream → wake with an insight
  or prophecy that is *actually true* (planted forward via the world event queue);
  fail → something follows you out.

### 4. AIROG_Palimpsest — Echoes of Dead Runs
Old save files become content.

- Past characters (dead or abandoned saves) exist in the current world as echoes: a
  doppelganger doing its own background-sim run, a legend NPCs tell wrong, a hostile
  revenant convinced *it* is the real you.
- Meet, rob, ally with, or absorb them. Every failed run permanently thickens the world
  instead of vanishing.
- **Feasibility**: save-file archaeology using the SaveIO machinery already built for
  AIROG_Multiplayer (`SaveIO.ReadSaveFile`, `GameSaveData`).

### 5. AIROG_Voice — The Narrator as an Entity
The antagonist is literally the prompt layer.

- The narrator develops personality and agenda from how you play (bored by caution,
  delighted by drama) and starts *cheating*: embellishing, foreshadowing things it then
  feels obligated to deliver, holding grudges.
- Appease it, bargain with it, or exorcise it.
- **Implementation**: a GenContext provider maintaining a narrator-mood model that
  injects prose-biasing directives.

### 6. AIROG_Fate — Co-Author Currency
Formalizes the thing players already want to do in AI games: nudge the fiction.

- Chronicle already detects story beats — grade them. Dramatic play (sacrifices,
  reversals, callbacks to your own history) earns **Fate**.
- Spend Fate on bounded authorial edits: declare a fact ("the guard captain owes me from
  the war"), demand a plot twist, retcon one sentence.
- The AI prices each edit by how much it warps the story. Skill loop: play
  *interestingly* to earn narrative power.

---

## Tier 2 — Conventional Expansions (proven mechanics, new to this game)

### AIROG_Legacy — Bloodline Meta-Progression
On death the world persists; your character becomes a legend/ghost/corpse NPC and you
roll a successor (heir, apprentice, rival) inheriting items, faction standing, and the
Chronicle. Death becomes content. Uses Multiplayer's save machinery
(`SaveIO.ReadSaveFile`/`WriteSaveFile`, `GameplayManager.LoadGame`).

### AIROG_Party — Companion System
Recruit NPCs as persistent traveling companions: follow between places, act in combat
with AI-decided moves, loyalty via NPCExpansion memories/secrets, betrayal, permadeath,
banter injected via GenContext. Highest-impact moment-to-moment change — the game is
currently a lonely experience.

### AIROG_Arcana — Freeform Ritual Magic
Player describes a ritual in the convo input; a GenContext directive makes the AI
adjudicate it into a structured `<RITUAL_RESULT>` block (Insight extraction pattern)
with mechanical costs (HP, items consumed, corruption meter) and a persistent registered
effect. Discovered rituals saved to a grimoire.

### AIROG_Outlaw — Crime, Bounty & Disguise
Witnessed crimes generate per-faction bounties; bounty hunters spawn via the world event
queue; disguises and bribes clear heat. Gives GrandStrategy factions another lever
against the player.

### AIROG_Delve — Structured Mega-Dungeons
Multi-floor dungeons as a graph of linked Places with keys, locked shortcuts, a boss,
themed loot tables. AI writes the flavor; the mod owns the structure so there is real
spatial gameplay. Persistent between visits, respawn timers.

### AIROG_Commerce — Living Economy & Caravans
Regional prices responding to WorldExpansion disasters/faction events; trade routes to
invest in or rob; a player caravan mini-loop connecting the Settlement to other towns.
Bridges Settlement + WorldExpansion + GrandStrategy into one economic layer.

### (Retired) Nemesis System
A persistent evolving antagonist was considered but already attempted within
AIROG_NPCExpansion — not pursuing as a standalone plugin.

---

## Recommended starting point
**AIROG_Hearsay** — most novel *and* most buildable: every required substrate (gossip
propagation, extraction blocks, world event queue, GenContext injection) already exists
across NPCExpansion, Insight, WorldExpansion, and GenContext.
