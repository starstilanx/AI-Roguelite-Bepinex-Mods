# Claude_RootReference.md
> This file acts as the root `CLAUDE.md` for this project. It provides AI agents with everything needed to navigate, build, and modify the AI Roguelite mod suite.

---

## Important Files

| File | Purpose |
|---|---|
| `dirs` | Deployment paths, game folder, save directory, source directory |
| `MODDING_LLM.md` | Extended modding guide with patterns and tips |
| `AIRL/claude.md` | Architecture reference for the decompiled game source |
| `README.md` | Project overview and installation guide |

---

## Directory Structure

```
AI Roguelite Decompilation/
├── AIRL/                   ← Decompiled game source (READ-ONLY, reference only)
├── AIROG_Multiplayer/      ← Co-op TCP networking mod (main focus)
├── AIROG_NPCExpansion/     ← NPC generation, autonomy, quests (v2.0.0)
├── AIROG_SkillWeb/         ← Procedural skill tree mod
├── AIROG_HistoryTab/       ← Conversation history UI
├── AIROG_Settlement/       ← Settlement system
├── AIROG_WorldExpansion/   ← World prompt injection & background ticks
├── AIROG_LoopBeGone/       ← AI dialogue loop prevention
├── AIROG_DeepgramTTS/      ← Deepgram TTS integration
├── AIROG_NanoBanana/       ← Gemini Imagen image generation
├── AIROG_TokenModifierPlugin/ ← Token usage modifier
├── ai-roguelite-config/    ← Narrative templates, world presets, AI prompts
├── dirs                    ← Path config file
├── MODDING_LLM.md          ← Modding guide for AI agents
└── Claude_RootReference.md ← This file
```

### Key External Paths (from `dirs`)

| Purpose | Path |
|---|---|
| Deploy plugins | `C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike\BepInEx\plugins` |
| Game folder | `C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike` |
| Save & player.log | `C:\Users\15307\AppData\LocalLow\MaxLoh\AI Roguelite` |
| Game libs | `C:\Program Files (x86)\Steam\steamapps\common\AI Roguelike\AI Roguelite_Data\Managed` |

---

## Build Commands

All mods target **net472** and reference `Assembly-CSharp.dll` + Unity dlls from `AIRL/libs/`.

```bash
# Build Multiplayer mod
dotnet build AIROG_Multiplayer/AIROG_Multiplayer.csproj -c Debug

# Build SkillWeb mod
dotnet build AIROG_SkillWeb/AIROG_SkillWeb.csproj -c Debug
```

After building, copy the `.dll` to the plugins folder listed in `dirs`.

> ⚠️ **Do NOT edit files in `AIRL/`** — they are decompiled reference source. All game modifications must be Harmony patches in the `AIROG_*` projects.

---

## AIRL — Key Architecture

### The Two God Objects

| Class | File | Role |
|---|---|---|
| `SS` | `SS.cs` | Static singleton hub. `SS.I` is the global access point. |
| `GameplayManager` | `GameplayManager.cs` | Game runtime brain — turns, saves, core logic. |

**Common access patterns:**
```csharp
SS.I.hackyManager          // The active GameplayManager
SS.I.p                     // The active PlayerCharacter
SS.I.saveTopLvlDir         // Top-level save directory path
SS.I.saveSubDirAsArg       // Current save slot subdirectory
SS.I.gameMode              // Current game mode (e.g. SS.GameMode.LOAD)
SS.I.uuidToGameEntityMap   // All game entities by UUID
```

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

### AI Pipeline (How the LLM Drives the Game)

1. **`InteractionLogic.cs`** — Detects player input (clicks, Shift+Click for custom commands).
2. **`UnifiedPromptBuilder.cs`** — Assembles the full prompt string from world state + history.
3. **`AIAsker.cs`** — Sends the prompt to the configured AI backend and receives raw text/JSON.
4. **`UnifiedResponseParser.cs`** — **CRITICAL.** Parses AI JSON commands (e.g., `spawn_entities`, `hp_deltas`, `ADD_STATUS_EFFECT`) into `GameEventResult` objects.
5. **`GameEventResult.cs`** — The structured result that `GameplayManager` applies to the world.

**To add a new AI command:** Patch `UnifiedResponseParser` (or `AIAsker.ParseStoryGenResp`) to recognise a new key/pattern and produce a `GameEventResult`.

### Save System

```csharp
// Key APIs
SaveIO.ReadSaveFile(string subDir)         // → GameSaveData (reads from saveTopLvlDir/subDir/my_save.txt)
SaveIO.WriteSaveFile(GameplayManager, bool clean)
GameplayManager.LoadGame(GameSaveData)     // async Task, loads game in-place

// Safe path access (never hardcode)
string topDir = SS.I.saveTopLvlDir;
string subDir = SS.I.saveSubDirAsArg;

// JSON helpers
SaveIO.SaveJson(obj, path);
SaveIO.LoadJson<T>(path);
```

### Story & Narrative

| Class | File | Role |
|---|---|---|
| `StoryChain` | `StoryChain.cs` | Ordered list of all `StoryTurn` objects for the current game. |
| `StoryTurn` | `StoryTurn.cs` | A single narrative moment (player action + AI response text). |
| `NarrativeFlavor` | `NarrativeFlavor.cs` | A tagged narrative trait (e.g., "Armor Piercing"). |
| `NarrativeFlavors` | `NarrativeFlavors.cs` | Collection of flavors on an entity or skill. |

### Skills & Abilities

| Class | File | Role |
|---|---|---|
| `PlayerSkill` | `PlayerSkill.cs` | A skill the player has learned. |
| `GameAbility` | `GameAbility.cs` | An active ability slot (mapped to a skill). |
| `AbilitiesV2Manager` | `AbilitiesV2Manager.cs` | Manages the player's ability loadout. |

### Status Effects

Status effects are **strings** the AI sees in the prompt, not typed classes.
- **`StatusEffect.cs`** — Simple data holder (name string + value).
- **`PcGameEntity.GetPlayerStatusStrToAppendNoSpace()`** — Appends active status strings to the AI prompt.
- Patch `GetPlayerStatusStrToAppendNoSpace` to add custom effects the AI can see.

### UI Conventions

AIRL's UI is **procedural** — no UXML, no prefab files. All panels are built at runtime via `GameObject`, `RectTransform`, and `UnityEngine.UI`.

Key UI entry points for injection:
- `ItemPanel.cs` — Inventory panel.
- `JournalModal.cs` — Main journal/story view.
- `MainMenu.cs` — Main/pause menu (~290 KB).
- `MainLayouts.cs` — Top-level layout management.
- `SurvivalBarInfo.cs` / `SurvBarInfoV2.cs` — HUD survival bars.

### High-Value Files for Modding Reference

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

## Modding Patterns

### Harmony Patching

```csharp
// Prefix — run BEFORE the original method (return false to skip original)
[HarmonyPatch(typeof(GameplayManager), "DoConvoTextFieldSubmission")]
public static class MyPatch {
    [HarmonyPrefix]
    public static bool Prefix(GameplayManager __instance) {
        // return false to skip original
        return true;
    }
}

// Postfix — run AFTER the original method
[HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
public static class MyPatch {
    [HarmonyPostfix]
    public static void Postfix(PlayerCharacter __instance, int amount) {
        // modify __result here if needed
    }
}
```

### Injecting into AI Prompts

```csharp
// Add data to the AI's prompt view
// Option A: patch GetPlayerStatusStrToAppendNoSpace to add status effects
// Option B: patch UnifiedPromptBuilder methods
// Option C: patch AIAsker.GetGameEventResultForStoryInteraction
```

### Common Tasks

| Goal | Where to patch |
|---|---|
| Add a stat | `GameplayManager.GetAttributeValAfterItemBonuses` |
| Add a button to a menu | `Start()` / `OnEnable()` of the relevant panel class |
| Intercept AI output | `AIAsker.ParseStoryGenResp` |
| Add an AI command | `UnifiedResponseParser` |
| Block/intercept player action | `Prefix` on `GameplayManager.DoConvoTextFieldSubmission` |
| React after save written | `Postfix` on `SaveIO.WriteSaveFile` |

---

## net472 Gotchas

This project targets **.NET Framework 4.7.2**. Modern C# APIs are unavailable:

```csharp
// ❌ No TakeLast
list.TakeLast(5)
// ✅ Use Skip instead
list.Skip(Math.Max(0, list.Count - 5))

// ❌ No Math.Clamp
Math.Clamp(val, min, max)
// ✅ Use Mathf.Clamp (Unity)
Mathf.Clamp(val, min, max)

// ❌ No IAsyncEnumerable, no records, no init-only props, etc.
```

---

## AIROG_Multiplayer Architecture (v2.0)

### Core Concepts

- **Save sync**: host's `my_save.txt` is GZip-compressed → sent as `SaveFileGzipB64` in `SaveData` packet
- **`IsClientMode`** static flag on `MultiplayerPlugin` — set `true` when joining as client
- **Client first join**: write save → `SS.I.saveSubDirAsArg = "mp_client"` → `SceneManager.LoadScene("Main Scene")`
- **Subsequent reloads**: `SaveIO.ReadSaveFile("mp_client")` → `SS.I.hackyManager.LoadGame(saveData)`
- **Client action interception**: `Prefix_DoConvoTextFieldSubmission` reads text, calls `Client.SendAction()`, returns `false`
- **Save write block/broadcast**: `Prefix_WriteSaveFile` blocks on client; `Postfix_WriteSaveFile` broadcasts to clients on host
- **Save timing**: broadcast is in `Postfix_WriteSaveFile` (fires AFTER async AI completes)
- **Image path fix**: `HostSaveSubDir` field in `SaveDataPayload` — client replaces `"{hostDir}/"` with `"mp_client/"` in JSON before writing
- **`CoopStatusOverlay`** (in `ClientHUD.cs`) — small top-right corner overlay for clients and hosts
- **Host overlay**: `CoopStatusOverlay.ShowForHost(port)` called from `MultiplayerPlugin.StartHost()` with `_isHostMode = true`
- **`PacketUtils.GzipCompress/Decompress`** in `Packet.cs` for save compression

### Inventory System

- **`Inventory/MPInventoryData.cs`**: `MPItem`, `PlayerInventory`, `MPInventoryDatabase` data models
- **`Inventory/MPInventoryManager.cs`**: Static manager — `Initialize`, `SyncHostFromGame`, `Load/Save`, `SerializeToJson/LoadFromJson`, `GetOrCreate`, `TransferItem`; `OnInventoryChanged` event
- **`Inventory/MPInventoryUI.cs`**: MonoBehaviour panel — `GetOrCreate(rootGO)`, `Toggle()`, `Refresh()`
- **`Inventory/MPPaperDollUI.cs`**: Paper-doll viewer — equipped slots + bag; player tabs at top
- **`Inventory/MPGiftUI.cs`**: Two-step gift flow — select item → select recipient → sends `ItemTransfer` packet
- **Host player ID**: `MPInventoryManager.HOST_PLAYER_ID = "host"`; client IDs = `ConnectedClient.PlayerId` (short GUID)
- **DB file**: `{saveSubDir}/mp_inventory.json`

### Character Update / HP Sync

- **`CharacterUpdate` packet** (client → host): `AIROGClient.SendCharacterUpdate(info)` → server updates `client.CharacterInfo` → fires `OnCharacterUpdateReceived` → `SendPartyUpdate` rebroadcasts
- **`MultiplayerPlugin.LocalCharacterInfo`** — stores client's own `RemoteCharacterInfo`; set in `StartClient()`, mutated on HP save

---

## AIROG_SkillWeb Architecture (v2.1)

- **`NodeType` enum**: `Basic`, `Notable`, `Keystone`
- **Cost scaling** in `UnlockCost()`: Basic=1×, Notable=2×, Keystone=4× NodeCost
- **Node sizes**: Basic=90×90, Notable=110×110, Keystone=140×140 (icon=60% of nodeSize)
- **Sprites**: Check `StreamingAssets/SkillWeb/` first, then `Assets/` folder fallback
- **StreamingAssets**: `AI Roguelite_Data/StreamingAssets/SkillWeb/` — `SkillRingBasic.png`, `PassiveSkillRingNotable.png`, `SkillRingKeystone.png`, `SkillWeb_bkg.png`
- **Generation**: cluster roots=Notable, frontier=Basic, manual "Extend Web"=Notable (2pt), "⬡ Keystone"=Keystone (4pt)
- **`ScenarioUpdater.GlobalTurn`** (static int) — shared turn counter for all systems

---

## AIROG_NPCExpansion Architecture (v2.0.0)

### 8 Core Systems

| System | File | Description |
|---|---|---|
| `SocialRippleSystem` | `Systems/SocialRippleSystem.cs` | Affinity cascades to nearby NPCs |
| `NPCReputationSystem` | `Systems/NPCReputationSystem.cs` | AI-generates behavior-driven reputation tags (max 5) |
| `NPCMemorySynthesis` | `Systems/NPCMemorySynthesis.cs` | Condenses story turns into LongTermMemories every 10 turns |
| `NPCDeathTracker` | `Systems/NPCDeathTracker.cs` | Death recording, bystander reactions, AI epitaphs |
| `RumorNetwork` | `Systems/RumorNetwork.cs` | Fact propagation between co-located NPCs every 3 turns |
| `NPCBarkSystem` | `Systems/NPCBarkSystem.cs` | AI ambient dialogue, 40% chance every 5 turns (8-turn cooldown) |
| `QuestManager` | `Quests/QuestManager.cs` | Full AI quest lifecycle: generate, detect completion, rewards |
| `QuestUI` | `Quests/QuestUI.cs` | Scrollable quest log panel |

### Key Patterns

```csharp
// Gold reward — use pcGameEntity, NOT manager.playerCharacter directly
manager.playerCharacter.pcGameEntity.numGold += amount;

// Affinity sync
NPCExpansionPlugin.SyncAffinityToGame(uuid, data);  // call after any affinity change

// Faction bridge: maps [-100,+100] affinity → [-15,+15] game sentiment
gc.sentimentV2 = (data.Affinity / 100f) * 15f;

// Death detection (in existing interaction postfix)
if (npc.corpseState != CorpseState.NONE && !data.IsDeceased) { ... }

// Quest story observer patches
// AIAsker.GenerateTxtNoTryStrStyle postfix, filters to STORY_COMPLETER/UNIFIED
```

---

## Search Tips

- **Find a mechanic:** `rg "MethodName" AIRL/` from the repo root
- **Find AI commands:** Search `UnifiedResponseParser.cs` for command keyword strings (e.g., `"spawn_entities"`, `"hp_deltas"`)
- **Find UI hooks:** Search for `.AddComponent<` or `new GameObject(` in the relevant panel file
- **Trace a data field:** Search for the field name across all `.cs` files; ownership is obvious from context
- **Follow the singletons:** Start from `SS.I.hackyManager` (GameplayManager) to find almost any system
