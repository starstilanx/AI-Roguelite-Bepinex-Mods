# NPC Expansion — Patch Notes

---

## v4.4.0

- Secrets an NPC confided in you were never passed to the storyteller. You could earn a character's deepest trust, listen to them admit something terrible, and have the story carry on as though the conversation had never happened. Secrets you have earned now shape how the AI writes that character. Secrets you have *not* earned stay sealed, so nothing can slip out that you were never told.
- Relationship milestones were recorded but never used. The AI knew a character's affinity number and nothing about how it got there. It now sees the turning points, when you became allies, what you did to earn it, so a hard-won friendship reads differently from a number that merely happens to be high.
- Quests were entirely invisible to the storyteller. Every quest you accepted was missing from the AI's view, so the very NPC who sent you after something would speak to you as though they had never mentioned it. Active quests are now part of the conversation.
- Named characters standing nearby were ignored while exploring. Personalities were only handed to the AI during a direct conversation, so if the story mentioned someone else in the room it wrote them as a stranger. Nearby characters the story names (hostile ones included) are now described in character.
- The living world was invisible until the game happened to save. Barks, spreading rumors, gossip, NPC memories and refreshed goals only reached the AI when a save file was written. On any autosave setting other than *every turn*, hours of simulated NPC life could pass without the storyteller ever being told. Everything the simulation produces now reaches the AI at the end of the turn it happens in.
- Techniques taught to you by NPCs were skipping the context manager. They were forced into the prompt through a side channel, which meant they ignored the shared context budget and could not be switched off alongside the rest of the mod's context. They now travel the same route as everything else.
- One talkative NPC could crowd out every other mod. NPC details are the first thing added to the prompt, and AI-written personalities and backstories routinely run to several hundred words, so a single verbose character could swallow the entire context allowance and leave nothing for world state, history, settlements or anything else. Long entries are now trimmed, and the block as a whole keeps to its share.
- The dead were written as though they were still going about their day. Examining a fallen character handed the AI their current goals, plans and outstanding quests. They now get their death and their epitaph instead.
- Quest data was being re-read from disk on every single AI request. It is now read on the same schedule as everything else.
- Loading a different save mid-session left the previous run's characters lingering in memory until a refresh timer happened to expire.
- A character who had only just appeared could cause the entire NPC description to be dropped from the prompt.
- World news picked up as rumor was being cut off mid-word. Headlines are now trimmed at a word boundary so they still read as sentences.

---

## v4.3.0 — *Word on the Street*

> *"Did you hear? The Iron Pact marches again."*

### Added

- **World news enters the rumor mill.** If AIROG_WorldExpansion is installed, recent world-simulation events (wars, successions, conquests, dominion news) are periodically seeded as `News:` facts on nearby NPCs via the rumor tick. They then spread NPC-to-NPC through the existing `RumorNetwork` and surface in conversations via GenContext's per-NPC known-facts injection — the background world sim now reaches the player through NPC mouths, not just the World News tab. Soft dependency: reads `world_expansion_data.json` from the save directory (the same file-based contract GenContext uses); a no-op when WorldExpansion isn't present. (`Systems/WorldNewsGossip.cs`)

---

## v4.2.0 — *Refinement Pass*

> *"A hundred small gears, each now turning true."*

A codebase-wide refinement: several long-standing logic bugs fixed, AI-call volume reduced, and dead code removed.

### Fixed

- **Quest Log and Hall of Fallen opened as empty panels.** Both scroll views masked their content with a `Mask` component driven by a fully transparent `Image`. Modern Unity culls fully transparent UI meshes (`cullTransparentMesh`), so the mask wrote no stencil and hid *every* entry — the window rendered but its contents never did. Both viewports now use `RectMask2D` (the same approach as the Examine panel, which is why that one always worked).
- **"Give to NPC" could target the wrong NPC.** The bottom-bar convo dropdown reserves slot 0 for `[OPEN-ENDED]`, so NPC entries are offset by one. The give-item menu read the dropdown value as a direct list index, handing items to the NPC *above* the one selected (or to the first NPC while in open-ended mode). Now resolved through the game's own `Utils.GetTargetedChar`, which handles the offset correctly.
- **Social ripples ignored rivalries.** When the player harmed an NPC, bystanders who *hated* that NPC still disapproved. Bystander reactions now invert when their bond with the target is negative — harming their rival pleases them, helping their rival angers them.
- **Global turn counter drifted across save loads.** `GlobalTurn` lived only in memory, so loading a save (or switching saves mid-session) desynced quest deadlines, bark cooldowns, and memory-synthesis timers. It is now persisted to `npcexpansion_state.json` in the save folder and restored on load; per-NPC scenario scheduling is reset on load so stale UUIDs don't linger.
- **Hostile-NPC idle suppression never triggered.** The "hostile enemies shouldn't casually browse bookshelves while the player watches" check compared the wrong things and always passed. It now correctly checks whether the NPC is in the player's current place.
- **Version string finally matches** — the plugin reported itself as 3.0.0 since the v4.0 rewrite.

### Improved

- **Quest completion checks are now one AI call instead of N.** Every story turn previously fired a separate YES/NO AI call per active quest. All active quest conditions are now batched into a single numbered-list prompt, with per-line YES/NO parsing. Story excerpt window widened from 300 to 500 chars for better detection.
- **Fewer redundant scene scans.** All `FindObjectOfType<GameplayManager>()` lookups replaced with the game's `SS.I.hackyManager` singleton reference.
- **Barks are normalised before validation** — multi-line AI output is collapsed to its first line and re-trimmed before length checks, so a valid bark wrapped in whitespace is no longer discarded.
- **"Ask Secret" is now exception-safe.** The reveal flow runs inside a menu-action lambda; a failed AI call could previously throw into the void mid-flow. It is now fully guarded.
- **Quest deadline sweep only writes to disk when something actually changed.**

### Removed

- **`Patches/NemesisPatch.cs` (dead code).** This Harmony class was never registered — `PatchAll` only targets the main plugin type — and if it ever *had* been registered it would have double-promoted nemeses alongside the `DeadLogic` patch (a promotion immediately followed by a phantom "repeat-kill" boost). Nemesis promotion is handled solely by the `DeadLogic` prefix.
- Dead `IsPlausibleToExamine` shim and other unused locals; async methods with no awaits are now synchronous (no more CS1998 hazards).

---

## v4.1.1 — *Hotfix*

- **Fix: NPCs no longer loot storage containers.** NPCs were incorrectly able to extract items from chests, storage racks, and other `StorageThingGameEntity` objects — in some cases removing an entire container from the world. The autonomy engine now skips all storage containers when looking for loose items to pick up. Additionally, the loose-item name filter has been expanded to exclude a broader set of container keywords (`crate`, `barrel`, `box`, `bin`, `locker`, `vault`, `coffer`, `reliquary`, `storage`, `container`, `sarcophagus`, `urn`) so AI-generated container props can't slip through.

---

## v4.1.0 — *Native Harmony Update*

> *"The world remembers them even when we don't."*

This update aligns NPC Expansion with the May 20 game patch, which introduced native character details (personality, background, visual description), playable party members, and an item-transfer API. Rather than being made redundant, the mod now bridges both worlds: native game data feeds our systems, and our extended data enriches the native UI. An NPC profiled via the game's own "Generate details" button is immediately recognised by every mod system — bark, secrets, reputation, quests, arcs — as if we generated them ourselves.

---

### Native Profile Bridge

The game now stores `personality`, `background`, and `visualDescription` directly on each `GameCharacter` via a new `ImportantCharacterData` object. NPC Expansion now treats this as a first-class data source.

Three new helpers in `NPCData` form the bridge:

- **`HasProfile(npc, data)`** — returns `true` if *either* `npc.importantData.personality` or our own `NPCData.Personality` has content. Every system that previously gated on `!string.IsNullOrEmpty(data.Personality)` now calls this instead, so natively-profiled NPCs activate bark, secret generation, reputation, death tracking, memory synthesis, arc actions, and quest generation without needing a separate "Generate Profile" step.
- **`GetPersonality(npc, data)`** — reads `importantData.personality` first; falls back to `NPCData.Personality` for saves predating the update.
- **`GetBackground(npc, data)`** — reads `importantData.background` first; falls back to `NPCData.Scenario`.
- **`SyncToNativeImportantData(npc, personality, background, visual)`** — writes our data into the game's native object so CharacterSheet always reflects what we generated.

---

### Generation Sync

When our generator finishes producing personality, scenario, attributes, skills, and abilities, it now writes the personality and scenario back into `ch.importantData` immediately. The native CharacterSheet panel will display our generated content without any additional steps. Scenario updates (the background AI call that refreshes an NPC's current situation every 2–5 turns) also sync on each update.

---

### "Generate Details" Bootstrap

A new Harmony postfix on `GameCharacter.GenerateImportantData` intercepts the game's own "Generate details" button. When the native generation completes:

1. Our `NPCData` is seeded from the native result so `HasProfile` returns `true` immediately.
2. If extended stats (attributes, skills, abilities) haven't been generated yet, our full extended generation is kicked off in the background automatically.

The result: one click on the native button is enough to unlock the full NPC Expansion feature set for that character.

---

### GenContext Injection — Native Fallback

`NPCProvider` in `AIROG_GenContext` previously injected NPC context exclusively from `npcexpansion_lore.json`. It now has a two-stage fallback:

1. **No-stub path** — if an NPC has no entry in the JSON cache but has native `importantData.personality`, a minimal stub is built on the fly from the native fields and injected at full priority into the AI prompt.
2. **Merge path** — if a cached stub exists but is missing personality or background (e.g. old save, generation still in progress), the native fields are layered in before injection.

Both the direct-conversation injection and the ambient ("NPC mentioned in prompt") injection have been updated. Natively-profiled NPCs are now visible to the AI even before extended generation completes.

---

### Updated Menu Labels

The NPC action menu entry now has three distinct states:

| State | Label | Action |
|---|---|---|
| No profile of any kind | **Generate Profile** | Runs full NPCExpansion generation |
| Native profile exists, no extended stats | **Generate Extended Stats** | Runs extended generation (attributes, skills, abilities) seeded from native data |
| Full NPCData exists | **Edit Extended Profile** | Opens the lore editor |

This makes it clear that the game's native "Generate details" and our "Generate Profile" are complementary, not competing.

---

### UI — Read from Native First

**NPCExamineUI** — the Examine panel's "Personality" and "Current Situation" sections now call `GetPersonality` / `GetBackground` so they display native profile data even when our extended JSON hasn't loaded yet.

**NPCUI Lore Editor** — the personality and scenario fields are pre-populated from native `importantData` when our own fields are empty. Saving any field in the editor also writes back to `ch.importantData` so CharacterSheet stays in sync.

---

### Under the Hood

- All 9 prompt-building systems (`NPCBarkSystem`, `NPCSecretSystem`, `NPCTeachingSystem`, `NPCDeathTracker`, `NPCReputationSystem`, `NPCMemorySynthesis`, `RelationshipArcSystem`, `QuestManager`, `QuestChainManager`) updated from `data.Personality` reads to `NPCData.GetPersonality(npc, data)` and `HasProfile(npc, data)` guards
- `NPCProvider` native fallback operates entirely in-memory; no disk reads — no performance cost
- Backward compatible: old saves with only NPCData JSON continue to work exactly as before

---

## v4.0.0 — *The Living World Update*

> *"They were never just set dressing."*

This is the largest update to NPC Expansion since its creation. Eight interconnected systems have been added on top of the existing lore generation, autonomy engine, and nemesis framework. NPCs now remember, react, gossip, grieve, speak unprompted, earn reputations, and give quests that the AI itself resolves.

---

### ⚔️ Faction-Sentiment Bridge
Your relationship with an NPC is no longer just a number in a log. Every affinity change — gifts, attacks, conversations — is now synchronized into the game's native `sentimentV2` system, directly affecting combat behavior, merchant pricing, and faction standing. NPCs you've befriended fight differently. NPCs you've wronged remember.

---

### 🌊 Social Ripple Effects
Actions have witnesses. When you help or harm an NPC, nearby characters who care about that person will react. A bystander who adores your target will warm to you. One who hates them might quietly approve. The ripple scales with bond strength — close allies react strongly, loose acquaintances barely at all. Watch the game log.

---

### 🧠 NPC Memory Synthesis
Every 10 turns, the AI distills recent story events into concrete memories for nearby NPCs. These aren't generic observations — they're drawn directly from `storyTurnHistoryV2`, the actual turns of your run. Over time, an NPC's long-term memory becomes a compressed journal of what they've actually witnessed. Examine them to see it grow.

---

### 👁️ Reputation System
NPCs earn reputation tags through behavior — not assignment. An NPC who auto-equips for combat might become *"battle-hardened."* One who sold off surplus goods earns *"shrewd trader."* A Nemesis who killed you is permanently marked. Up to five tags accumulate per NPC and are injected into every AI prompt, shaping how they speak and act going forward.

---

### 💀 Death Tracking & Hall of the Fallen
Named, lore-generated NPCs no longer disappear when killed — they're remembered. Their death is recorded with cause, last known goal, and an AI-generated epitaph. Nearby NPCs with bonds to the fallen will grieve or celebrate accordingly, with affinity changes and new memories. A **Hall of the Fallen** panel (accessible from any NPC action menu) memorializes every lost character across the run.

---

### 📜 Rumor Network
Information spreads. Every 3 turns, NPCs in the same location share facts with one another — scenario updates, things the player told them, events they witnessed. A piece of news seeded with one NPC can reach others organically without any player involvement. Ask an NPC what they know; the answer may surprise you.

---

### 🗣️ NPC Barks
Every 5 turns, nearby NPCs have a 40% chance to mutter something aloud — fully AI-generated, character-specific ambient dialogue drawn from their personality, current situation, current goal, and relationship status. Nemeses are especially vocal. Barks appear in the game log and respect an 8-turn cooldown so no one becomes a chatterbox.

---

### 📋 AI Quest System
NPCs with generated lore can now give quests. Select **Accept Quest** from any NPC's action menu and the AI will generate a complete quest on the spot — an objective, a specific completion condition, a narrative reward, and a gold amount — all derived from that NPC's personality and current situation.

After every story turn, the AI checks whether recent events fulfilled any active quest's condition. Completion is detected automatically; no button to press. Rewards include gold, an affinity boost with the giver, and a new memory entry for them.

Quests fail automatically if their giver is killed or if a deadline (when set) passes. Open the **Quest Log** from any NPC action menu to track everything.

---

### 🪨 Lore Button Asset
The bottom-bar lore button is now a hand-crafted stone tablet pulled from `StreamingAssets/NPCExpansion/LoreButton.png`. It sits quietly at the edge of the NPC dropdown — neutral when no lore exists, cyan-tinted when lore is present and ready to edit, gold-pulsing while generating.

---

### Under the Hood
- All new systems are driven by `ScenarioUpdater`'s global turn counter — no new hooks required
- Rumor propagation, bark ticks, and memory synthesis each run on independent intervals (3 / 5 / 10 turns)
- Quest data, memorial records, and rumor facts are all persisted to the save directory alongside existing lore files
- `NPCProvider` in GenContext now injects reputation tags, known facts, and active quest context into every relevant AI prompt at a cost of ~35–70 tokens overhead
- Full fallback path for all asset loading; missing assets degrade gracefully

---

## v1.x — Legacy

Earlier versions established the core systems this update builds on:
- AI lore generation (personality, scenario, skills, abilities, attributes)
- NPC autonomy (auto-equip, self-preservation, economic activity)
- Nemesis system (promotion on player death, persistent threat escalation)
- Affinity & relationship tracking
- NPC Examine UI, Equipment UI, and lore editor
- Faction bridge (sentimentV2 sync)
- Gear system (armor damage reduction, weapon damage scaling)
