using System;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Reverie
{
    public static class ReverieInterceptor
    {
        // ---- Rest-intent detection on the player's typed action ----

        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.DoConvoTextFieldSubmission))]
        public static class Patch_DoConvoTextFieldSubmission
        {
            [HarmonyPrefix]
            public static void Prefix(GameplayManager __instance)
            {
                try
                {
                    if (Utils.PlayerInteractionsDisabled()) return; // original bails out too
                    string text = __instance.npcConvoTextInput?.text;
                    ReverieManager.OnPlayerAction(text, __instance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Reverie] Rest detection error: {ex.Message}");
                }
            }
        }

        // ---- Main AI response interceptor: extracts <DREAM_STATE> (Chronicle pattern) ----
        // Internal AI calls from other mods never see the dream directive (it is injected via
        // GameplayManager's prompt builder, which direct AIAsker calls bypass), so no
        // internal-call flag is needed — a response without the block is simply left alone.

        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
        public static class Patch_GenerateTxtNoTryStrStyle
        {
            [HarmonyPostfix]
            public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
            {
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
                    const string OPEN = "<DREAM_STATE>";
                    const string CLOSE = "</DREAM_STATE>";
                    int start = text.IndexOf(OPEN, StringComparison.OrdinalIgnoreCase);
                    int end = text.IndexOf(CLOSE, StringComparison.OrdinalIgnoreCase);
                    if (start >= 0 && end > start)
                    {
                        string block = text.Substring(start + OPEN.Length, end - start - OPEN.Length).Trim();
                        // In UNIFIED mode newlines inside JSON strings are encoded as \n literal
                        block = block.Replace("\\n", "\n").Replace("\\r", "");
                        ReverieManager.ProcessDreamStateBlock(block);
                        string after = text.Substring(end + CLOSE.Length).TrimStart('\n', '\r', ' ');
                        text = text.Substring(0, start) + after;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Reverie] Dream-state extraction error: {ex.Message}");
                }
                return text;
            }
        }

        // ---- Save / load lifecycle ----

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        public static class Patch_ReadSaveFile
        {
            public static void Postfix(string saveSubDir) => ReverieManager.Load(saveSubDir);
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        public static class Patch_WriteSaveFile
        {
            public static void Postfix() => ReverieManager.Save();
        }

        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.doNewGame))]
        public static class Patch_DoNewGame
        {
            public static void Prefix() => ReverieManager.Reset();
        }
    }
}
