# Claude_ModsCatalogue.md
> **AI Assistant Reference Guide:** This document catalogues the complete `AIROG_*` mod suite for AI Roguelite. Use this file to understand each mod's functionality, architecture, and what it hooks into, facilitating rapid patch development or debugging.

---

## 🏗️ Ecosystem Overview

AI Roguelite is expanded through a network of **BepInEx** plugins using **Harmony** to patch `Assembly-CSharp.dll`. 
Mods are separated into discrete `.csproj` solutions, generally prefixed with `AIROG_`. All UI is generated procedurally via code.

### 🌐 Shared Dependencies & Bridges

*   **`AIROG_GenContext`**: A central manager that controls what historical/world state is injected into the prompt. Other mods (like `AIROG_Chronicle` or `AIROG_NPCExpansion`) register `IContextProvider` classes with GenContext to inject their bespoke narrative states safely.
*   **`AIROG_UnifiedBridge`**: A framework unification point handling cross-compatibility and standardising API calls logic or event triggers across multiple specific mod features.

---

## 🏆 Flagship Gameplay Expansions

### ⚔️ AIROG_Multiplayer
**Purpose:** Turns the game into a shared, synchronised tabletop experience.
*   **Architecture:** TCP Client/Server model.
*   **Key Hooks:** Intercepts `DoConvoTextFieldSubmission` to send client text to the host; patches `WriteSaveFile` to compress and broadcast the host save to clients.
*   **Features:** MPInventoryManager, GZIP state sync.

### 🎭 AIROG_NPCExpansion
**Purpose:** Overhauls NPCs into autonomous entities with memories, gossip, and nemesis generation.
*   **Architecture:** Modular systems inside `/Systems/` (SocialRippleSystem, RumorNetwork, NPCBarkSystem, QuestManager). 
*   **Key Hooks:** Modifies `GameEventResult` processing to detect deaths, affinity shifts, and extracts 'memories' every 10 turns.

### 🕸️ AIROG_SkillWeb
**Purpose:** Generates a vast, procedural skill tree per playthrough.
*   **Architecture:** Node-based rendering via procedural `GameObject` graph inside `SkillWebUI.cs`.
*   **Key Hooks:** Injects "Action Affixes" into combat prompts. Scales upgrade costs.

### 🌎 AIROG_WorldExpansion
**Purpose:** Background "tick" system simulating a living world (disasters, economic shifts).
*   **Architecture:** Subscribes to global turn events. Uses `ScenarioUpdater.GlobalTurn`.
*   **Key Hooks:** Background AI calls that append ongoing world events back into the current region's description text.

### 👑 AIROG_GrandStrategy
**Purpose:** 4X grand-strategy layer — the player founds a Dominion and explores/expands/exploits/exterminates as a sovereign power inside WorldExpansion's sim.
*   **Architecture:** Hard dependency on `AIROG_WorldExpansion`; registers the native "Player" faction into the world sim; strategic tick rides `WorldSimulation.RunMinorTick` via Harmony postfix.
*   **Key Hooks:** Order engine (annex/develop/levy/war/campaign/espionage) mutating real `Place.faction` ownership; retaliation raids, rebellions, victory conditions; `GrandStrategyProvider` in GenContext injects `[DOMINION]` state + ruler directives from `grand_strategy_data.json`.
*   **Theme system (v0.5.0):** all flavor text is voiced through a `ThemeLexicon` (Themes.cs) — ruler title, currency, advisor titles/names, wonder names — auto-detected from the world's description (MEDIEVAL/POSTAPOC/SCIFI/MODERN/GENERIC), overridable via `GS_THEME` or the panel's THEME cycler; the world's native AI-generated currency name overrides the preset.

### 🐺 AIROG_ALife
**Purpose:** STALKER-style A-Life — a persistent offline population of squads (patrols, warbands, caravans, raiders, wild packs) that travel the world map, fight, and die while the player is elsewhere; the player finds the aftermath. v2.0 "The Squads Live": squads are persistent characters — named leaders with earned epithets and succession, veterancy/recruiting/merging/splitting/defection, squad-vs-squad **blood feuds** (HUNT goal tracks enemies across the map), and **The Wake** — an anti-nemesis ecology where squads accumulate fear/awe of the player, flee or parley instead of fighting, and reroute around "dread zone" killing grounds.
*   **Architecture:** Virtual-squad sim ticks on `InvokeTurnHappened`; travel graph = k-NN adjacency over top-level `Place.worldCoords`; player's current top-level place is a frozen "online bubble". **Embodiment (v2.0):** materialization is two-way — met squads keep their real `GameCharacter`s forever; the sim moves them offline via the light `RemoveInGameEnt/SetParentEnt/AddInGameEnt` core and kills them via `SetAsCorpse` (real corpses on real battlefields). Zero AI calls — templates + GenContext embellishment.
*   **Key Hooks:** `ApplyLocationChange` postfix reconciles the departed place (deaths/defections/peace) then materializes one squad (leader first, by name, with dossier desc); `GameCharacter.SetAsCorpse` postfix = live kill tracking (fear, dread, legend, succession); named NPCs migrate off-screen via `SetAsChildOfPl`; soft bridges to WorldExpansion (wars→warband raid targets, `ClaimedPlaceUuids`, world-news push) and GenContext (`ALifeProvider`, priority 78: aftermath, leaders, feuds, regard, `[REPUTATION]` tier). Save: `alife_data.json`. Console: `ALIFE_DOSSIER`, `ALIFE_LEGEND`.

### 🏰 AIROG_Settlement
**Purpose:** Transforms the game into a structural town builder and faction simulator.
*   **Key Hooks:** Adds a custom Settlement menu tab, manages structural resources, patches grid rendering for town instances.

---

## 🛠️ Narrative & Context Utilities

### 📜 AIROG_Chronicle & AIROG_HistoryTab
**Purpose:** Solves the game's default AI context window amnesia.
*   **`AIROG_HistoryTab`**: A UI-first approach allowing players to scroll upward through previously committed turns.
*   **`AIROG_Chronicle`**: Hooked into `GenerateTxtNoTryStrStyle`. It uses `ChronicleProvider` to automatically summarise the story saga and injects it back through `GenContext`. 

### 🌙 AIROG_Reverie
**Purpose:** The dream layer — sleeping can drop the player into a playable dream woven from their own Chronicle beats; triumph grants a self-fulfilling prophetic Omen, losing all Lucidity means something follows you out (Haunting).
*   **Architecture:** Rest-intent regex on `DoConvoTextFieldSubmission` triggers the dream roll (35%, 12-turn cooldown); `DreamWeaver` composes a dreamscape locally from `chronicle.json` stubs + a 12-theme table; `<DREAM_STATE>` hidden blocks (lucidity/progress/event/omen) drive the state machine; HP snapshot at sleep is restored on wake so dreams never kill the body.
*   **Key Hooks:** `ReverieProvider` (GenContext, priority 96) injects dream directives / `[WAKING]` one-shot / `[PROPHETIC OMEN]` + `[HAUNTED]` awake context; `GenerateTxtNoTryStrStyle` postfix extracts blocks; persists to `reverie.json`. Console: `REVERIE_TEST`, `REVERIE_STATUS`, `REVERIE_WAKE`.

### 🎴 AIROG_Mythic
**Purpose:** A Mythic GME–style **chaos director**. A Chaos Factor (1–9) rises when the player loses control of scenes and falls when they win it back; it gates how often oracle-rolled random events disrupt play and switches the prose register the narrative AI is told to write in (LOW/MODERATE/HIGH volatility). Includes a real player-facing solo-RPG oracle (`MYTHIC ASK <odds> <question>` — full 1e Fate Chart with exceptional results, rulings injected as established fact). Zero extra AI calls — dice and tables simulate, GenContext narrates.
*   **Architecture:** A "scene" = one stay at a top-level place; kills (`SetAsCorpse` postfix, capped), near-death HP checks, NPC deaths, and NPCExpansion quest outcomes (reflection poll with watermarks) accumulate a control score that moves the CF exactly ±1 at scene boundaries. Per-turn d100 doubles whose digit ≤ CF fire director events (published 1e rule — chaos gates disruption) with focus mapped to real NPCs/quests and original Action+Descriptor inspiration words; events queue as GenContext directives with 3-turn expiry windows. Optional scene testing (d10 ≤ CF → Altered/Interrupted arrivals, off by default).
*   **Key Hooks:** `InvokeTurnHappened` postfix (trigger rolls + signals), `ApplyLocationChange` postfix (scene boundaries + tests), `GameCharacter.SetAsCorpse` postfix (witnessed kills), `MythicProvider` (GenContext, priority 82: volatility register + `[DIRECTOR EVENT]` + `[ORACLE RULING]`). Save: `mythic_data.json`. Console: `MYTHIC`, `MYTHIC LOG`, `MYTHIC CF <n>`, `MYTHIC EVENT`, `MYTHIC ASK`. Design doc: `Claude_MythicOracle_Design.md`.

### 🧠 AIROG_Insight
**Purpose:** NPC conversation tracking and narrative memory extraction.
*   **Mechanics:** Counts consecutive turns directed at an NPC. Once the conversation threshold (default: 3) is met, it intercepts response generation to extract and strip hidden `<NPC_INSIGHT>` blocks.
*   **Persistence:** Saves insights to `insight_data.json` inside the game's active save directory.

### 🔄 AIROG_LoopBeGone
**Purpose:** Prevents the AI from repeating the same dialogue formats.
*   **Architecture:** Hooked into AI Response Generation parsing.
*   **Mechanic:** Employs N-gram algorithms and Levenshtein sequence distance mathematical checks to flag and intercept repeating paragraphs natively.

### 🔌 AIROG_OpenAI5
**Purpose:** Compatibility layer for newer OpenAI GPT-4.1+, o-series, and GPT-5+ models.
*   **Key Hooks:** Prefix patches `MyHttpClient.DoHttpRequest` to intercept outbound requests to `/v1/chat/completions`.
*   **Mechanics:** Replaces `max_tokens` with `max_completion_tokens` (boosting it to at least 8192 for reasoning headroom) and forces `temperature` to `1.0`.

### 🎟️ AIROG_TokenCount & AIROG_TokenModifierPlugin
**Purpose:** Fine-tuning AI Token expenses and generation maximums per API call. 
*   **Key Hooks:** Directly edits the configurations fed to `AIAsker`.

### 🧹 AIROG_GCHelper
**Purpose:** Automated garbage collection and memory optimization.
*   **Mechanics:** Forces a full GC on scene loads and every 5 gameplay turns (throttled to a minimum of 30 seconds apart). Also trims `AIROG_GenContext` DMNotes history to 30 items via reflection to limit memory bloat.

### 🗃️ AIROG_PresetExporter
**Purpose:** World-builders tool for exporting active games as shareable setup preset templates (prompts, rules, custom data).

### 🎲 AIROG_RandomOrg
**Purpose:** API Wrapper. Replaces C#'s standard PRNG with atmospheric noise TRNG from the Random.org API for true die-rolls.
*   **v1.1.0**: `DiceRollsOnly` mode (default ON) — TRNG is only spent inside `Utils.GetRollOutcome*` (real dice/skill-check rolls, tracked via a `[ThreadStatic]` Harmony roll scope); all other randomness stays on the vanilla PRNG, drastically cutting daily API quota usage.

---

## 🎨 Sensory, Audio, & Visual Overhauls

### 🗣️ TTS Engines (AIROG_DeepgramTTS, GeminiTTS, Sapi5)
**Purpose:** Character vocalisation via various TTS backends.
*   **Deepgram/Gemini**: Cloud-based high fidelity voices requiring API networks.
*   **Sapi5**: Leverages the local Windows OS voice bank for zero-latency instant offline speech.

### 🖼️ AIROG_NanoBanana & AIROG_OpenAIImage
**Purpose:** Image Generation enhancements.
*   **NanoBanana**: Direct integration to Google Gemini Imagen models, built explicitly for automatically removing background chromas from generated NPC portraits.
*   **OpenAIImage**: Hooks for DALL-E generation.

### 🔍 AIROG_StableHordeDetector
**Purpose:** Real-time tracking and logging of Stable Horde image generation workers.
*   **Key Hooks:** Postfix patches `StableHordeClient.HttpWithRetry` to intercept `/generate/status/` responses.
*   **Features:** Extracts the generating model name, worker name, worker ID, and state, logging them to Unity's console and appending to `stable_horde_log.txt`.

### 🎼 AIROG_MusicExpansion
**Purpose:** Smart dynamic looping audio. Hooked to the location `Place.cs` and current combat state tags to shuffle ambient environment and battle tracks dynamically.

### 🔤 AIROG_FontModifierMain / AIROG_FontSelection
**Purpose:** Procedurall UI Typography replacers. Patches Unity's text components globally to load and insert modern TTF/OTF custom fonts over default arrays.

### 🖌️ AIROG_WomboStyles
**Purpose:** Integrates Wombo AI aesthetic "Styles" for image prompts, modifying the visual flavour appended to scenery requests.

---

## 🧩 Structural Guidelines for Modding

When modifying these modules, follow the established conventions:
1.  **UI is Procedural:** We do not use XML or Prefabs. If you need a new panel, instantiate new `GameObject` types and bind `RectTransform` layouts within a dedicated `ModNameUI.cs`.
2.  **Harmony is King:** Every plugin extends `BaseUnityPlugin` and relies on `HarmonyLib` to hook core methods (Prefix/Postfix).
3.  **Singleton Access:** For game state variables, use `SS.I.hackyManager` (GameplayManager) and `SS.I.p` (PlayerCharacter).
4.  **Serialization:** Save specific mod states as secondary JSON files adjacent to `SS.I.saveTopLvlDir`/`SS.I.saveSubDirAsArg`. Do not break `my_save.txt`.
