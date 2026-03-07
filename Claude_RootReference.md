# This is a copy of the claude.md file from the AIRL sub-directory within this project. It is provided here for reference only and contains additional information about the project structure.

# Important Files

- dirs file = Contains directory locations
- MODDING_LLM.md = Contains modding information
- Claude_RootReference.md = Same as the file in the AIRL sub-directory, but has additional information about the project structure
- README.md = Contains general information about the project


# Note about directories
- The dirs file contains important paths. Within those paths are the plugins, player.log file for game log reference, and the AI Roguelite alpha branch directory.

# AIRL — Decompiled Game Source

This directory contains the **decompiled C# source** of `Assembly-CSharp.dll`, the core game logic of **AI Roguelite (AIRL)**. It is **read-only reference material** — you cannot rebuild or run the game from this source directly. Its purpose is to let modders understand how the game works so they can write **BepInEx/Harmony patches** in the `AIROG_*` mod projects.

> ⚠️ Do **not** edit files in this directory expecting them to affect the game. All gameplay modifications must be implemented as Harmony patches in the appropriate `AIROG_*` project.

---

## Key Architecture at a Glance

### The Two God Objects

Almost everything routes through one of these two:

| Class | File | Role |
|---|---|---|
| `SS` | `SS.cs` | Static singleton hub. `SS.I` is the global access point. |
| `GameplayManager` | `GameplayManager.cs` | The game's runtime brain. Handles turns, saves, and all core logic. |

**Common access patterns:**
```csharp
SS.I.hackyManager          // The active GameplayManager
SS.I.p                     // The active PlayerCharacter
SS.I.saveTopLvlDir         // Top-level save directory path
SS.I.saveSubDirAsArg       // Current save slot subdirectory
```

---

### Core Entity Hierarchy

```
GameEntity          (GameEntity.cs)       — Base for everything
├── InGameEntity    (InGameEntity.cs)     — Things that exist in-world
│   ├── GameCharacter (GameCharacter.cs) — Characters (player + NPCs)
│   │   └── PcGameEntity (PcGameEntity.cs) — Player specifically
│   ├── Place       (Place.cs)           — World locations
│   └── ThingGameEntity (ThingGameEntity.cs) — Physical objects (chests, trees)
└── GameItem        (GameItem.cs)        — Inventory items (not in-world)
```

---

### AI Pipeline (How the LLM Drives the Game)

The flow for a player action:

1. **`InteractionLogic.cs`** — Detects player input (clicks, Shift+Click for custom commands).
2. **`UnifiedPromptBuilder.cs`** — Assembles the full prompt string from world state + history.
3. **`AIAsker.cs`** — Sends the prompt to the configured AI backend and receives raw text/JSON.
4. **`UnifiedResponseParser.cs`** — **CRITICAL.** Parses AI JSON commands (e.g., `spawn_entities`, `hp_deltas`, `ADD_STATUS_EFFECT`) into `GameEventResult` objects.
5. **`GameEventResult.cs`** — The structured result that `GameplayManager` applies to the world.

**To add a new AI command:** Patch `UnifiedResponseParser` (or `AIAsker.ParseStoryGenResp`) to recognise a new key/pattern and produce a `GameEventResult`.

---

### Story & Narrative

| Class | File | Role |
|---|---|---|
| `StoryChain` | `StoryChain.cs` | The ordered list of all `StoryTurn` objects for the current game. |
| `StoryTurn` | `StoryTurn.cs` | A single narrative moment (player action + AI response text). |
| `NarrativeFlavor` | `NarrativeFlavor.cs` | A tagged narrative trait (e.g., "Armor Piercing", "Stun Chance"). |
| `NarrativeFlavors` | `NarrativeFlavors.cs` | The collection of flavors on an entity or skill. |

---

### Skills & Abilities

| Class | File | Role |
|---|---|---|
| `PlayerSkill` | `PlayerSkill.cs` | A skill the player has learned. |
| `GameAbility` | `GameAbility.cs` | An active ability slot (mapped to a skill). |
| `AbilitiesV2Manager` | `AbilitiesV2Manager.cs` | Manages the player's ability loadout. |
| `NgSkill` / `NgSkills` | `NgSkill.cs`, `NgSkills.cs` | New-game narrative skill definitions. |

---

### Status Effects

Status effects are **strings** the AI sees in the prompt, not a typed class. They work via:
- **`StatusEffect.cs`** — A simple data holder (name string + value).
- **`PcGameEntity.GetPlayerStatusStrToAppendNoSpace()`** — Appends active status effect strings to the player's description in the AI prompt.
- **`StatusFxV2.cs`** — Extended status effect logic.

To add a status effect a mod can "see," patch `GetPlayerStatusStrToAppendNoSpace` to append your effect's name.

---

### World & Map

| Class | File | Role |
|---|---|---|
| `Place` | `Place.cs` | A named location (dungeon, town, etc.) with sub-locations. |
| `MapLocation` | `MapLocation.cs` | The overworld map representation of a place. |
| `GridManager` | `GridManager.cs` | The tactical 2D grid used in encounters. |
| `Region` / `NgRegion` | `Region.cs`, `NgRegion.cs` | Named geographic regions of the world. |

---

### Quests

| Class | File | Role |
|---|---|---|
| `QuestManager` | `QuestManager.cs` | Tracks all active/completed quests. |
| `QuestChain` | `QuestChain.cs` | A multi-step quest. |
| `QuestTaskV4` | `QuestTaskV4.cs` | A single task/objective within a quest. |

---

### UI Conventions

AIRL's UI is **procedural** — no UXML, no prefab files in this source. All panels are built at runtime via `GameObject`, `RectTransform`, and `UnityEngine.UI` calls.

Key UI entry points to patch for injection:
- `ItemPanel.cs` — Inventory panel.
- `JournalModal.cs` — The main journal/story view.
- `MainMenu.cs` — The main/pause menu (~290 KB; enormous).
- `MainLayouts.cs` — Top-level layout management.
- `SurvivalBarInfo.cs` / `SurvBarInfoV2.cs` — The HUD survival bars.

---

### Save System

```csharp
// Finding save paths (use these in mod code, not hardcoded strings)
string topDir = SS.I.saveTopLvlDir;
string subDir = SS.I.saveSubDirAsArg;

// Loading/saving JSON
SaveIO.SaveJson(obj, path);
SaveIO.LoadJson<T>(path);
```

---

## High-Value Files for Modding Reference

| File | Why It Matters |
|---|---|
| `GameplayManager.cs` (~571 KB) | Core turn/event loop. Grep here for any game mechanic. |
| `SS.cs` (~156 KB) | All singletons and global state. |
| `AIAsker.cs` (~428 KB) | Full AI integration: prompt submission, parsing, backends. |
| `Utils.cs` (~191 KB) | Shared utility methods used everywhere. |
| `Place.cs` (~93 KB) | Location logic, sub-location traversal. |
| `LS.cs` (~101 KB) | Localisation strings — useful for finding string keys. |
| `UnifiedResponseParser.cs` | The AI command dispatch table. |
| `GameCharacter.cs` | Stats, HP, inventory for all characters. |

---

## Search Tips

- **Find a mechanic:** `rg "MethodName" AIRL/` from the repo root.
- **Trace a data field:** Search for the field name across all `.cs` files; ownership is usually obvious from context.
- **Find AI commands:** Search `UnifiedResponseParser.cs` for the command keyword strings (e.g., `"spawn_entities"`, `"hp_deltas"`).
- **Find UI hooks:** Search for `.AddComponent<` or `new GameObject(` in the relevant panel file.
