# 🛠️ AI Roguelite Modding Guide for LLMs

This document provides a comprehensive overview of the **AI Roguelite (AIRL)** codebase, designed to help AI agents (like yourself) navigate, understand, and modify the game.

---

## 🏗️ Project Overview

*   **Game Engine**: Unity (C#)
*   **Modding Framework**: [BepInEx](https://github.com/BepInEx/BepInEx)
*   **Patching Library**: [Harmony](https://github.com/pardeike/Harmony)
*   **Code Location**: 
    *   `AIRL/`: Decompiled source of `Assembly-CSharp.dll` (the game's core).
    *   `AIROG_*/`: Individual mod projects (e.g., `AIROG_SkillWeb`, `AIROG_NPCExpansion`).
    *   `config/` (Path to `ai-roguelite-config`): Narrative templates, world presets, and AI instructions.

---

## 🎭 Narrative Modding (Non-Code)

Before diving into C#, you can often achieve significant gameplay changes by modifying the files in the `ai-roguelite-config` directory.

*   **`world_presets/`**: Define the rules, factions, and initial state of the world.
*   **`chatgpt-preambles/`**: The system prompts sent to the LLM to define its "personality" or the game's genre.
*   **`prompts-chatgpt/`**: Templates for specific game actions (e.g., "Attack," "Enter Location"). Modifying these changes how the AI interprets the world.
*   **`model-config/`**: Configuration for specific AI backends.

---

## 🏗️ Code Modding (C#)

### 1. 🧠 AI Interaction & Parsing
The bridge between game state and natural language.
*   **`AIAsker.cs`**: Submits prompts and captures raw text/JSON.
*   **`UnifiedPromptBuilder.cs`**: Orchestrates state into the final string.
*   **`UnifiedResponseParser.cs`**: **CRITICAL.** Translates AI JSON commands (like `spawn_entities`, `hp_deltas`) into `GameEventResult` objects that the game can execute.
*   **`InteractionLogic.cs`**: Handles the player's intent (e.g., Shift+Clicking to give a custom command).

### 2. 🌍 World & State (`GameplayManager.cs`, `SS.cs`)
*   **`GameplayManager`**: The global "brain" of the game's logic. It handles turns, loading/saving, and coordinates between systems.
*   **`SS` (Static System)**: A ubiquitous singleton (`SS.I`) that provides easy access to the current `GameplayManager`, player, and save paths.
    *   `SS.I.hackyManager`: The active `GameplayManager`.
    *   `SS.I.p`: The active `PlayerCharacter`.
*   **`Place.cs`**: Represents a location in the world.
*   **`GridManager.cs`**: Manages the tactical 2D grid/map.

### 3. 👤 Entities (`GameEntity.cs`, `PcGameEntity.cs`, `GameCharacter.cs`)
*   **`GameEntity`**: Base class for everything in the game (Items, Characters, Places).
*   **`PcGameEntity`**: Specifically for the Player.
*   **`GameCharacter`**: Used for both the Player and NPCs. Contains stats, inventory, and health.
*   **`ThingGameEntity`**: Represents physical objects on the grid (chests, trees, etc.).

### 4. 🎒 Inventory & Items (`GameItem.cs`, `ItemPanel.cs`)
*   **`GameItem`**: Data structure for an item in an inventory.
*   **`ItemPanel`**: The UI class for the inventory.

---

## 🛠️ Modding Patterns

### Harmony Patching
Most mods use Harmony to "hook" into game methods.
*   **Prefix**: Run code *before* a game method. Can skip the original method by returning `false`.
*   **Postfix**: Run code *after* a game method. Can modify the return value (`ref __result`).

```csharp
[HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
public static class MyPatch {
    [HarmonyPostfix]
    public static void Postfix(PlayerCharacter __instance, int amount) {
        // Code to run after player gains XP
    }
}
```

### Accessing Save Data
Use `SS.I.saveTopLvlDir` and `SS.I.saveSubDirAsArg` to find where the current game is saved. Many mods save their own JSON data alongside the main save file.

### Injecting into Prompts
To add information to the AI's "vision," patch methods like:
*   `AIAsker.GetGameEventResultForStoryInteraction`
*   `PcGameEntity.GetPlayerStatusStrToAppendNoSpace` (to add "Status Effects" that the AI sees)

---

## 📂 Directory Structure for Mods

A typical mod folder (`AIROG_ModName`) contains:
*   `ModNamePlugin.cs`: Inherits from `BaseUnityPlugin`. The entry point where patches are applied in `Awake()`.
*   `ModNameConfig.cs`: Handles BepInEx configuration (settings shown in the mod menu).
*   `ModNameUI.cs`: If the mod has a custom menu, it's usually built here using Unity's `GameObject` and `RectTransform` APIs.

---

## 💡 Tips for AI Agents

1.  **Grep is your friend**: If you need to know how "Questing" works, grep for `QuestManager` or `QuestChain`.
2.  **Follow the Singletons**: Start from `SS.I.hackyManager` (GameplayManager) to find almost any other system.
3.  **Check existing mods**: `AIROG_SkillWeb` is a great example of UI injection. `AIROG_WorldExpansion` shows how to handle background ticks.
4.  **UI is manual**: AIRL doesn't use XML/UXML for mod UI. It's mostly procedural `GameObject` creation (see `SkillWebUI.cs`).
5.  **LLM "Commands"**: If you want the AI to do something new, look at how `AIAsker` parses strings like `ADD_STATUS_EFFECT`. You can add your own via regex patches.

---

## 🚀 Common Tasks

*   **Adding a stat**: Patch `GameplayManager.GetAttributeValAfterItemBonuses`.
*   **Adding a button to a menu**: Look for the `Start()` or `OnEnable()` method of the menu class (e.g., `ItemPanel`) and postfix it to instantiate a new button.
*   **Intercepting AI output**: Patch `AIAsker.ParseStoryGenResp`.

---

## ⚠️ Scope Disclaimer

The `AIRL` directory contains ~500 files and is the full source of the game. While this guide documents the **Core Architecture** and **Primary Singletons**, it is not an exhaustive line-by-line manual for every file. 

When working on a specific feature:
1.  **Reference this guide** for the architectural "hooks."
2.  **Grep the codebase** for the class names mentioned here.
3.  **Explore the Singletons** (`SS.I`) to trace data flow.

Happy modding! 👾
