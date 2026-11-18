using System;
using System.Threading.Tasks;
using HarmonyLib;

namespace AIROG_GenContext.DMNotes
{
    public static class DmNotesResponseInterceptor
    {
        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
        public static class Patch_GenerateTxtNoTryStrStyle
        {
            [HarmonyPostfix]
            public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
                => Intercept(ref __result, chatGptPromptType);
        }

        // A story turn taken in a place that has a panorama sends the panorama image along, and the
        // game then generates through this method instead of GenerateTxtNoTryStrStyle. Without this
        // patch those turns kept their <DM_NOTES> block (shown to the player) and were never read.
        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtFromTxtAndImg))]
        public static class Patch_GenerateTxtFromTxtAndImg
        {
            [HarmonyPostfix]
            public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
                => Intercept(ref __result, chatGptPromptType);
        }

        private static void Intercept(ref Task<string> result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (result == null) return;
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER
                && chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;
            if (!DmNotesManager.IsActive) return;

            result = ExtractAndStrip(result, chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED,
                DmNotesManager.CurrentState);
        }

        private static async Task<string> ExtractAndStrip(Task<string> original, bool jsonResponse, DmNotesState askedFor)
        {
            string text = await original;
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string notes;
                string stripped = DmNotesText.Strip(text, jsonResponse, out notes);
                if (notes != null)
                {
                    text = stripped;
                    // A reply that arrives after the player has moved to another save (the state
                    // object is replaced on load and on new game) is still cleaned up, but its
                    // notes belong to the save that asked for it.
                    if (notes.Trim().Length > 0 && ReferenceEquals(askedFor, DmNotesManager.CurrentState))
                        DmNotesManager.ProcessNotes(notes);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[GenContext] DmNotes extraction error: {ex.Message}");
            }
            return text;
        }

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        public static class Patch_ReadSaveFile
        {
            public static void Postfix(string saveSubDir)
            {
                try
                {
                    // The main menu also reads save files it is only listing (to rebuild a missing
                    // summary); only the save being loaded is ours to follow.
                    if (SS.I != null && saveSubDir == SS.I.saveSubDirAsArg)
                        DmNotesManager.LoadState(saveSubDir);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GenContext] DmNotes load hook error: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.doNewGame))]
        public static class Patch_DoNewGame
        {
            public static void Prefix() => DmNotesManager.ResetState();
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        public static class Patch_WriteSaveFile
        {
            public static void Postfix()
            {
                DmNotesManager.SaveState();
                // A new game has no save folder until its first save; write any mod switches
                // the player flipped before then.
                ModToggles.Flush();
                // Normally raised when the load finishes (Patch_AfterLoadOrNewGame). This is the
                // fallback should that hook ever stop binding: a save only happens after a load.
                ModToggles.RaiseReloadedIfPending();
            }
        }
    }
}
