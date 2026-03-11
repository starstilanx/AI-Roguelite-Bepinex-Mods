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

### Ability Context Guidelines
When injecting NPC context, ensure that **Abilities** are treated as first-class citizens.
*   **Format**: "Ability Name: Brief description of what it does."
*   **Usage**: The AI should be aware of these abilities to influence NPC behavior and dialogue.
*   **Example Injection**:
    ```
    Capabilities:
    - Fireball: Hurls a ball of fire at the target.
    - Healing Touch: Restores health to self or ally.
    ```

---

## NPCData ↔ NPCDataStub Field Sync Reference

When modifying `AIROG_NPCExpansion/NPCData.cs`, ensure the corresponding fields are added to `AIROG_GenContext/ContextProviders/NPCProvider.cs → NPCDataStub`.

### Currently Synced Fields:

| Category | Field | Injected to Prompt? |
|----------|-------|---------------------|
| **Core Identity** | Name, Description, Personality, Scenario | ✓ Yes |
| **Character Card** | CreatorNotes, SystemPrompt | ✓ Yes (truncated) |
| **Character Card (Metadata)** | FirstMessage, PostHistoryInstructions, AlternateGreetings, GenerationInstructions | ✗ No |
| **Tags & Traits** | Tags, InteractionTraits | ✓ Yes |
| **Goals & Memory** | LongTermMemories, CurrentGoal, GoalProgress, RecentThoughts, InteractionHistory | ✓ Yes |
| **Relationship** | Affinity, RelationshipStatus, NpcAffinities | ✓ Yes |
| **Equipment** | EquippedUuids | ✓ Yes |
| **Stats & Skills** | Attributes, Skills, DetailedAbilities, Abilities | ✓ Yes |
| **Autonomy Flags** | AllowAutoEquip, AllowSelfPreservation, AllowEconomicActivity, AllowWorldInteraction | ✗ No (runtime only) |
| **Special Flags** | IsNemesis | ✓ Yes |

### NPC Scenario Update Rate

Scenario updates (the per-NPC AI call that refreshes `CurrentGoal`, `RecentThoughts`, etc.) are **staggered and randomized** to avoid flooding the LLM every turn.

**How it works (`ScenarioUpdater.cs`):**
- Each NPC tracks its own `nextUpdateTurn` stored in `_npcNextUpdateTurn` (uuid → global turn).
- When first encountered, a random initial delay of **2–5 turns** is assigned.
- After each update fires, the NPC's next update is scheduled **2–5 turns later** (re-randomized each time).
- Only NPCs whose `nextUpdateTurn ≤ globalTurn` are included in a given turn's update batch.
- The `_isUpdating` lock still prevents concurrent batches; any NPC that would have fired while a batch is running simply catches the next available turn.

**Key constants:**
| Constant | Value | Meaning |
|---|---|---|
| `SCENARIO_MIN_INTERVAL` | 2 | Minimum turns between scenario updates per NPC |
| `SCENARIO_MAX_INTERVAL` | 5 | Maximum turns between scenario updates per NPC |
| `AUTONOMY_TURNS_PER_UPDATE` | 3 | All nearby NPCs run autonomy every N turns |
| `BARK_INTERVAL` | 5 | Ambient dialogue attempt every N turns |
| `RUMOR_INTERVAL` | 3 | Rumor propagation every N turns |
| `MEMORY_INTERVAL` | 10 | Memory synthesis + quest deadlines every N turns |

**Design rationale:** With 5+ nearby NPCs and `TURNS_PER_UPDATE = 1`, every player action triggered 5+ sequential LLM calls. Staggering reduces this to typically 0–2 calls per turn, with the load spread naturally over time.

---

### Scene Snapshot Feature

The NPCProvider now includes a **Scene Snapshot** that provides contextual awareness:
- **Location Name**: Current place name from `manager.currentPlace`
- **Danger Level**: 1-5 scale (Safe → Deadly)
- **Nearby Characters**: Lists NPCs by name (up to 3) or count if more
- **Hostile Presence**: Lists enemies by name (up to 2) or count if more
- **Player Presence**: Notes when player is nearby

Example output:
```
[Scene: The Rusty Tavern (Safe) | Present: Merchant Grok, Barmaid Ellie | Player nearby]
```

### Adding New Fields Checklist:
1. Add the field to `AIROG_NPCExpansion/NPCData.cs`
2. Add the corresponding field to `AIROG_GenContext/ContextProviders/NPCProvider.cs → NPCDataStub`
3. If the field should be injected into AI context, update `GetContext()` in `NPCProvider.cs`
4. Rebuild both projects: `dotnet build AIROG_GenContext` and `dotnet build AIROG_NPCExpansion`
