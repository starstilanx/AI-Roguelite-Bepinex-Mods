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
            {
                if (chatGptPromptType == AIAsker.ChatGptPromptType.STORY_COMPLETER
                    && ContextManager.GetGlobalSetting("DMNotes"))
                {
                    __result = ExtractAndStrip(__result);
                }
            }

            private static async Task<string> ExtractAndStrip(Task<string> original)
            {
                string text = await original;
                try
                {
                    int start = text.IndexOf("<DM_NOTES>", StringComparison.OrdinalIgnoreCase);
                    int end   = text.IndexOf("</DM_NOTES>", StringComparison.OrdinalIgnoreCase);
                    if (start >= 0 && end > start)
                    {
                        string block = text.Substring(start + 10, end - start - 10).Trim();
                        DmNotesManager.ProcessNotes(block);
                        string after = text.Substring(end + 11).TrimStart('\n', '\r', ' ');
                        text = text.Substring(0, start) + after;
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[GenContext] DmNotes extraction error: {ex.Message}");
                }
                return text;
            }
        }

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        public static class Patch_ReadSaveFile
        {
            public static void Postfix(string saveSubDir) => DmNotesManager.LoadState(saveSubDir);
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        public static class Patch_WriteSaveFile
        {
            public static void Postfix() => DmNotesManager.SaveState();
        }
    }
}
