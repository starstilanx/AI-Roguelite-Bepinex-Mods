# AI Roguelite Mods Collection

This repository contains a collection of modifications and expansions for AI Roguelite. These plugins enhance various aspects of the game, from improving AI integration to adding entirely new gameplay mechanics.

---

## 🎙️ Audio & TTS

*   **Deepgram TTS** (`AIROG_DeepgramTTS`): Replaces the default TTS with high-quality Deepgram voices. Maps in-game speaker types (Player, NPC, Monster) to specific AI voices. Compatible with Sapphire.
*   **Gemini TTS** (`AIROG_GeminiTTS`): Replaces the default TTS with Google Gemini voices. Maps in-game speaker types to specific AI voices. Compatible with Sapphire.
*   **SAPI5 TTS** (`AIROG_Sapi5`): Allows the game to utilize local Windows SAPI5 text-to-speech voices for offline or customized voice generation.
*   **Music Expansion** (`AIROG_MusicExpansion`): Allows users to place `.mp3` or `.wav` files into specific folders to automatically shuffle them into the game's ambient and combat playlists. Compatible with Sapphire.

---

## 🎨 Visuals & UI

*   **Font Modifiers** (`AIROG_FontModifierMain`, `AIROG_FontSelection`): Handles the complex task of replacing fonts in Unity's TextMeshPro system. Allows real-time font switching via an in-game dropdown. Compatible with Sapphire.
*   **Nano Banana** (`AIROG_NanoBanana`): Bypasses default image generation to use Google's Gemini Imagen models. Offers specific features like automatic background removal for characters. Compatible with Sapphire.
*   **OpenAI Image** (`AIROG_OpenAIImage`): A plugin for inputting OpenAI-compatible API keys to use OpenAI's image generation models within the game.
*   **Stable Horde Detector** (`AIROG_StableHordeDetector`): Integrates with or detects Stable Horde deployments for community-driven distributed image generation.

---

## ⚔️ Gameplay Mechanics

*   **NPC Expansion** (`AIROG_NPCExpansion`): A highly complex plugin that makes NPCs "real." They gain their own agendas, inventories, equipment, and take simultaneous turns with the player. *Note: Extremely token-heavy; may not be viable on Sapphire.*
*   **Skill Web** (`AIROG_SkillWeb`): Adds a procedural, massive tree of upgrades that players navigate upon leveling up. Offers combat stats and narrative flair. Compatible with Sapphire.
*   **Settlement** (`AIROG_Settlement`): Transforms the game into a management sim. Players can found towns, build structures, and manage resources with AI-supported events. Compatible with Sapphire.
*   **Multiplayer** (`AIROG_Multiplayer`): Introduces multiplayer functionality, allowing clients to connect and share the AI Roguelite experience with synchronized story turns and entities.

---

## 🌍 World & Lore

*   **World Expansion** (`AIROG_WorldExpansion`): Introduces a background tick system. The world's economy fluctuates, and events (wars, disasters) happen and are logged in the journal even if the player stands still. Compatible with Sapphire.
*   **History Tab** (`AIROG_HistoryTab`): Solves "AI amnesia" by compiling a persistent history of world events injected into prompts, ensuring the AI remembers the journey. *Note: Heavy on token counts; needs revision.*
*   **Preset Exporter** (`AIROG_PresetExporter`): A streamlined utility for scenario creators to share their "world rules" with others. Works across all modes.

---

## ⚙️ Utilities & Fixes

*   **Loop Be Gone** (`AIROG_LoopBeGone`): A vital utility for long-form play that uses N-grams and Levenshtein distance to detect and prevent AI repetition, preserving immersion.
*   **Token Modifiers** (`AIROG_TokenModifierPlugin`): Allows advanced control over how much the AI talks by modifying token limits. Higher limits yield detailed descriptions but increase API costs. *Incompatible with Sapphire.*
*   **Token Count** (`AIROG_TokenCount`): An underlying utility associated with token management and calculation.
*   **Gen Context** (`AIROG_GenContext`): Allows users to disable context truncation, meaning they can bypass standard context limits for massive, unrestricted prompt generation.
*   **OpenAI5** (`AIROG_OpenAI5`): Provides compatibility layers or enhancements for the OpenAI API integration.
*   **Random.org** (`AIROG_RandomOrg`): Replaces standard procedural randomness with true randomness generated via the Random.org API for genuinely unpredictable results.
