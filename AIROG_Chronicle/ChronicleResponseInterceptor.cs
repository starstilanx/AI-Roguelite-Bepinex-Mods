using System;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Chronicle
{
    public static class ChronicleResponseInterceptor
    {
        // ---- Main AI response interceptor: extracts <CHRONICLE_BEAT> ----

        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
        public static class Patch_GenerateTxtNoTryStrStyle
        {
            [HarmonyPostfix]
            public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
            {
                // Skip internal calls (chapter title/recap generation)
                if (ChronicleManager.IsInternalCall) return;

                if (chatGptPromptType == AIAsker.ChatGptPromptType.STORY_COMPLETER
                 || chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED)
                {
                    __result = ExtractAndStrip(__result);
                }
            }

            private static async Task<string> ExtractAndStrip(Task<string> original)
            {
                string text = await original;
                try
                {
                    const string OPEN  = "<CHRONICLE_BEAT>";
                    const string CLOSE = "</CHRONICLE_BEAT>";
                    int start = text.IndexOf(OPEN,  StringComparison.OrdinalIgnoreCase);
                    int end   = text.IndexOf(CLOSE, StringComparison.OrdinalIgnoreCase);
                    if (start >= 0 && end > start)
                    {
                        // Full well-formed block: extract, process, strip.
                        string block = text.Substring(start + OPEN.Length, end - start - OPEN.Length).Trim();
                        // In UNIFIED mode newlines inside JSON strings are encoded as \n literal
                        block = block.Replace("\\n", "\n").Replace("\\r", "");
                        ChronicleManager.ProcessBeatBlock(block);
                        string after = text.Substring(end + CLOSE.Length).TrimStart('\n', '\r', ' ');
                        text = text.Substring(0, start) + after;
                    }
                    else if (start >= 0)
                    {
                        // Partial/unclosed block — response got cut off before </CHRONICLE_BEAT>
                        // (token limit, truncation, etc). Strip from the tag to end-of-line rather
                        // than let the raw tag leak onto the player's screen.
                        Debug.LogWarning("[Chronicle] Found <CHRONICLE_BEAT> without closing tag — stripping.");
                        int lineEnd = text.IndexOf('\n', start);
                        text = text.Substring(0, start) +
                               (lineEnd > start ? text.Substring(lineEnd + 1) : "");
                        text = text.TrimStart('\n', '\r', ' ');
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Chronicle] Beat extraction error: {ex.Message}");
                }
                return text;
            }
        }

        // ---- Save / load lifecycle ----

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        public static class Patch_ReadSaveFile
        {
            public static void Postfix(string saveSubDir) => ChronicleManager.Load(saveSubDir);
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        public static class Patch_WriteSaveFile
        {
            public static void Postfix() => ChronicleManager.Save();
        }

        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.doNewGame))]
        public static class Patch_DoNewGame
        {
            public static void Prefix() => ChronicleManager.Reset();
        }

        // ---- Level-up milestone detector ----

        [HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
        public static class Patch_GainXp
        {
            // PlayerCharacter.playerLevel is a legacy field only ever set from save data on
            // hydrate — GainXp() levels up the player via GameCharacter.level (through
            // GetPcGameCharacter()), same as the game's own GetPlayerLevel()/UpdatePlayerLevelTxt.
            // Comparing __instance.playerLevel here would always read the same stale number.
            private static int _prevLevel;

            [HarmonyPrefix]
            public static void Prefix(PlayerCharacter __instance)
            {
                _prevLevel = __instance.GetPcGameCharacter()?.level ?? 0;
            }

            [HarmonyPostfix]
            public static void Postfix(PlayerCharacter __instance)
            {
                int newLevel = __instance.GetPcGameCharacter()?.level ?? 0;
                if (newLevel > _prevLevel)
                {
                    ChronicleManager.RecordBeat(new ChronicleBeat
                    {
                        Turn      = ChronicleManager.State?.GlobalTurn ?? 0,
                        Type      = BeatType.LevelUp,
                        Summary   = $"Reached level {newLevel}.",
                        IsMilestone = true
                    });
                }
            }
        }
    }
}
