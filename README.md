# 💠 AI Roguelite: Ultra Expansion Collection

Welcome to the definitive collection of modifications for **AI Roguelite**. This repository houses a suite of advanced plugins designed to push the boundaries of AI-driven gaming—from seamless multiplayer co-op to deep, persistent world simulations.

> [!IMPORTANT]
> **Developer Guide:** If you are an AI assistant or developer looking to contribute or modify these plugins, please refer to [MODDING_LLM.md](file:///d:/Projects/AI%20Roguelite/AI%20Roguelite%20Decompilation/MODDING_LLM.md) for architectural overviews and coding patterns.

---

## 🏆 Flagship Expansions

The following mods represent the most significant transformations to the AI Roguelite experience.

### ⚔️ Co-op Multiplayer (`AIROG_Multiplayer`)
**The ultimate way to play.** Turns AI Roguelite into a shared tabletop-style RPG. One player hosts, and others join from the main menu.
- **Unified Narrative:** The AI responds to *all* players simultaneously, weaving their actions into a single story response.
- **Real-time Sync:** Shared story logs, party health tracking, and instant world updates.
- **Zero Configuration:** Clients only need the plugin—no save data required to join.

### 🎭 NPC Expansion: Living World (`AIROG_NPCExpansion`)
**They were never just set dressing.** This mod overhauls every NPC in the game with deep autonomy and memory.
- **Social Ripple Effects:** NPCs remember your actions, form gossip networks, and react to how you treat their friends or enemies.
- **Nemesis System:** Characters who defeat you are promoted, gaining unique titles and scaling stats for persistent threats.
- **Memory Synthesis:** NPCs distill story turns into concrete memories, affecting their future dialogue and behavior.
- **Quest Engine:** Fully AI-generated quests derived from an NPC's unique personality and local world context.

### 🕸️ Skill Web (`AIROG_SkillWeb`)
**Infinite growth, procedurally generated.** Replaces static stats with a massive, AI-generated upgrade tree.
- **Narrative Traits:** Unlock "Action Affixes" like *Armor Piercing* or *Stun Chance* that the AI incorporates into combat descriptions.
- **Node Generation:** Every node has AI-generated lore and icons that fit your specific character's journey.

---

## 🌎 World & Simulation

| Mod | Description | Sapphire Ready? |
| :--- | :--- | :---: |
| **World Expansion** | Adds a background tick system. Wars, disasters, and economic shifts happen even while you're idle. | ✅ |
| **Settlement** | Transform the game into a town-builder. Found settlements, manage structures, and navigate faction politics. | ✅ |
| **History Tab** | Solve "AI amnesia" with a persistent repository of past events injected into current prompts. | ⚠️ (High Tokens) |
| **Preset Exporter** | A utility for world-builders to export their custom rules and scenarios as shareable presets. | ✅ |

---

## 🎙️ Sensory & UI Enhancements

### 🔊 Text-To-Speech (TTS)
Enhance immersion by giving different characters unique voices.
- **Deepgram / Gemini TTS:** High-fidelity cloud-based voices using the latest AI models.
- **SAPI5 TTS:** Utilizes your local Windows voices for a latency-free, offline experience.

### 🎨 Visual & Interface
- **Nano Banana:** Bypasses default image loops to use **Google Gemini Imagen**. Features automatic background removal for characters.
- **Font Selection:** A complete overhaul of the UI font system, allowing for custom typography across all game menus.
- **Music Expansion:** Dynamic ambient and combat playlists that shuffle based on world metadata.

---

## 🛠️ Utilities & Core Logic

- **Loop Be Gone:** Uses N-grams and Levenshtein distance to detect and break AI "dialogue loops," preserving immersion during long sessions.
- **Gen Context:** A powerful tool for power users to toggle context truncation and manage how much history the AI "sees."
- **Token Manager:** Real-time tracking and fine-tuning of API token usage to balance narrative depth with cost.
- **Random.org:** Replaces pseudorandom hardware generators with **true randomness** sourced from atmospheric noise.

---

## 🚀 Installation

1. Ensure [BepInEx 5.x](https://github.com/BepInEx/BepInEx) is installed in your AI Roguelite directory.
2. Download any mod folder (e.g., `AIROG_SkillWeb`).
3. Copy the compiled `.dll` (and any associated `StreamingAssets`) into `BepInEx/plugins/`.
4. Launch the game and enjoy the expanded universe!

---

*Built with ❤️ for the AI Roguelite community.*
