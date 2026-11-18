using System;
using System.Collections.Generic;
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
                => Intercept(ref __result, chatGptPromptType);
        }

        // GameplayManager.GenerateStory / GenerateUnifiedResponse send the story request through
        // this method instead whenever there is an image to attach (a place with a panorama), so
        // without this patch those turns are never seen and their hidden block reaches the screen.
        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtFromTxtAndImg))]
        public static class Patch_GenerateTxtFromTxtAndImg
        {
            [HarmonyPostfix]
            public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
                => Intercept(ref __result, chatGptPromptType);
        }

        private static void Intercept(ref Task<string> result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            // Skip internal calls (chapter title/recap generation)
            if (ChronicleManager.IsInternalCall || result == null) return;

            if (chatGptPromptType == AIAsker.ChatGptPromptType.STORY_COMPLETER
             || chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED)
            {
                // The state is captured now, when the request starts: the reply can arrive after
                // the player has moved to another save.
                result = ExtractAndStrip(result, chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED,
                    ChronicleManager.State);
            }
        }

        private static async Task<string> ExtractAndStrip(Task<string> original, bool unified, ChronicleState stateAtRequest)
        {
            string text = await original;
            if (text == null) return null;
            try
            {
                var blocks = new List<string>();
                string stripped = ChronicleText.StripBeatBlocks(text, unified, blocks, out string partial);

                // Switched off for this save, or a reply for a save that is no longer loaded: the
                // block is still removed so it can never show on screen, but nothing is recorded.
                if (ModSwitch.Enabled && ReferenceEquals(ChronicleManager.State, stateAtRequest))
                {
                    bool recorded = false;
                    foreach (string block in blocks)
                        recorded |= ChronicleManager.ProcessBeatBlock(block);

                    if (partial != null)
                    {
                        // Response got cut off before </CHRONICLE_BEAT> (token limit). The beat is
                        // still good if the cut came after the summary line.
                        bool salvaged = ChronicleManager.ProcessBeatBlock(partial, requireCompleteSummary: true);
                        recorded |= salvaged;
                        Debug.LogWarning("[Chronicle] Found <CHRONICLE_BEAT> without closing tag — stripped" +
                                         (salvaged ? ", beat recovered from the partial block." : "."));
                    }

                    ChronicleManager.OnStoryTurn(ChronicleText.WithoutModBlocks(StoryTextOf(stripped, unified)), recorded);
                }
                text = stripped;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Beat extraction error: {ex.Message}");
            }
            return text;
        }

        /// <summary>The narration inside a response: the text itself, or the "story" field of a unified JSON reply.</summary>
        private static string StoryTextOf(string text, bool unified)
        {
            if (!unified) return text;
            try
            {
                int open = text.IndexOf('{'), close = text.LastIndexOf('}');
                if (open >= 0 && close > open)
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(text.Substring(open, close - open + 1));
                    return json["story"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)json["story"] : null;
                }
            }
            catch
            {
                // The game repairs some malformed JSON this plain parse rejects; read the field directly.
            }
            try
            {
                var m = StoryFieldRx.Match(text);
                if (m.Success) return Newtonsoft.Json.JsonConvert.DeserializeObject<string>("\"" + m.Groups[1].Value + "\"");
            }
            catch { }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex StoryFieldRx =
            new System.Text.RegularExpressions.Regex("\"story\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                System.Text.RegularExpressions.RegexOptions.Compiled);

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
                try
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
                catch (Exception ex)
                {
                    // Never let a chronicle entry break the XP grant it is observing.
                    Debug.LogError($"[Chronicle] Level-up beat error: {ex.Message}");
                }
            }
        }
    }
}
