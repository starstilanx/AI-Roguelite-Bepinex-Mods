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

### 🏰 AIROG_Settlement
**Purpose:** Transforms the game into a structural town builder and faction simulator.
*   **Key Hooks:** Adds a custom Settlement menu tab, manages structural resources, patches grid rendering for town instances.

---

## 🛠️ Narrative & Context Utilities

### 📜 AIROG_Chronicle & AIROG_HistoryTab
**Purpose:** Solves the game's default AI context window amnesia.
*   **`AIROG_HistoryTab`**: A UI-first approach allowing players to scroll upward through previously committed turns.
*   **`AIROG_Chronicle`**: Hooked into `GenerateTxtNoTryStrStyle`. It uses `ChronicleProvider` to automatically summarise the story saga and injects it back through `GenContext`. 

### 🔄 AIROG_LoopBeGone
**Purpose:** Prevents the AI from repeating the same dialogue formats.
*   **Architecture:** Hooked into AI Response Generation parsing.
*   **Mechanic:** Employs N-gram algorithms and Levenshtein sequence distance mathematical checks to flag and intercept repeating paragraphs natively.

### 🎟️ AIROG_TokenCount & AIROG_TokenModifierPlugin
**Purpose:** Fine-tuning AI Token expenses and generation maximums per API call. 
*   **Key Hooks:** Directly edits the configurations fed to `AIAsker`.

### 🗃️ AIROG_PresetExporter
**Purpose:** World-builders tool for exporting active games as shareable setup preset templates (prompts, rules, custom data).

### 🎲 AIROG_RandomOrg
**Purpose:** API Wrapper. Replaces C#'s standard PRNG with atmospheric noise TRNG from the Random.org API for true die-rolls.

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
