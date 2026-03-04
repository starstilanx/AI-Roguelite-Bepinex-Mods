# AIROG_GenContext Architecture Guidelines

## ⚠️ CRITICAL: READ THIS BEFORE MODIFYING PROMPT INJECTION ⚠️

**`AIROG_GenContext` is the ONLY allowed entry point for modifying the AI prompt.**

### The Core Rule
All prompt injection logic has been centralized into `AIROG_GenContext` to manage token budgets and prevent crashes.
*   **DO NOT** create patches for `GameplayManager.BuildPromptString` in any other mod.
*   **DO NOT** add `Postfix` methods that append strings to the prompt in `NPCExpansion`, `HistoryTab`, `WorldExpansion`, or `Settlement`.

### Architecture Overview
1.  **Data Providers (Other Mods)**:
    *   Mods like `AIROG_NPCExpansion` and `AIROG_HistoryTab` are responsible **ONLY** for:
        *   Game logic (spawning NPCs, tracking events).
        *   UI (History tab, NPC details panel).
        *   **Saving/Loading Data**: They must serialize their state to JSON files (e.g., `npcexpansion_lore.json`, `universe_history.json`).
2.  **Context Manager (AIROG_GenContext)**:
    *   This mod reads the JSON files produced by the other mods.
    *   It intelligently selects, summarizes, and truncates the data.
    *   It injects the final text into the prompt, ensuring the total context stays within the **2048 token limit**.

### How to Add New Context
If you are developing a new mod and want it to affect the AI story:
1.  **In your new mod**: Save your narrative data to a structured JSON file in the save directory.
2.  **In AIROG_GenContext**:
    *   Create a new provider class in `ContextProviders/` (e.g., `MyNewModProvider.cs`).
    *   Implement `BaseContextProvider`.
    *   Read your JSON file.
    *   Register the provider in `ContextManager.cs`.

### Why This Exists
*   **Token Bloat**: Decentralized mods previously caused the prompt to exceed 8000+ tokens, crashing the AI and costing money.
*   **Duplicate Injection**: Having injection logic in both the original mod and GenContext causes information to appear twice, confusing the AI and wasting space.
