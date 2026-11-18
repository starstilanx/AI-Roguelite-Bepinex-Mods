using System;
using System.Text.RegularExpressions;
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
                    if (!ReverieManager.Active) return;
                    string text = __instance.npcConvoTextInput?.text;
                    // The original sends "/say ..." off as co-op chat before anything else: not an action
                    if (MpSession.IsActive && text != null && GameplayManager.GetMpChatMsg(text) != null) return;
                    if (Utils.PlayerInteractionsDisabled()) return; // original bails out too
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
                => Intercept(ref __result, chatGptPromptType);
        }

        // Story turns at a place with a panorama are sent through this method instead (the game
        // attaches the image), so it needs the same treatment or the block reaches the screen.
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
            if (chatGptPromptType == AIAsker.ChatGptPromptType.STORY_COMPLETER
             || chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED)
            {
                // The state is captured now, when the request starts: the reply can arrive after
                // the player has moved to another save.
                result = ExtractAndStrip(result, chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED,
                    ReverieManager.State);
            }
        }

        private const string OPEN = "<DREAM_STATE>";

        // "<\/DREAM_STATE>" is the same tag with JSON's optional escaped slash
        private static readonly Regex CloseRx = new Regex(@"<\\?/DREAM_STATE>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DreamKeyRx = new Regex(
            @"^[\s\-\*""\[]*(lucidity[_ ]?delta|progress|event|omen)[ \t]*[:=]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] TruncatedTails =
            { "lucidity_delta:", "progress:", "event:", "omen:", "</dream_state>" };

        private static async Task<string> ExtractAndStrip(Task<string> original, bool unified, ReverieState stateAtRequest)
        {
            string text = await original;
            if (text == null) return null;
            try
            {
                int start = text.IndexOf(OPEN, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return text;
                int bodyStart = start + OPEN.Length;
                Match close = CloseRx.Match(text, bodyStart);

                // Switched off, or a reply for a save that is no longer loaded: the block is still
                // removed so it can never show on screen, but it no longer drives anything.
                bool active = ReverieManager.Active && ReferenceEquals(ReverieManager.State, stateAtRequest);

                if (close.Success)
                {
                    string block = text.Substring(bodyStart, close.Index - bodyStart).Trim();
                    // In UNIFIED mode newlines inside JSON strings are encoded as \n literal
                    block = block.Replace("\\n", "\n").Replace("\\r", "");
                    if (active) ReverieManager.ProcessDreamStateBlock(block);
                    string after = text.Substring(close.Index + close.Length).TrimStart('\n', '\r', ' ');
                    text = text.Substring(0, start) + after;
                }
                else
                {
                    // The reply was cut off inside the block (token limit). Remove what there is of
                    // it, and still count the dream-turn using the lines that arrived whole.
                    int cut = EndOfUnclosedBlock(text, bodyStart, unified, out int lastWholeLineEnd);
                    string whole = text.Substring(bodyStart, lastWholeLineEnd - bodyStart)
                        .Replace("\\n", "\n").Replace("\\r", "");
                    text = text.Substring(0, start) + text.Substring(cut);
                    Debug.LogWarning("[Reverie] Found <DREAM_STATE> without closing tag — stripped.");
                    if (active) ReverieManager.ProcessDreamStateBlock(whole);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Reverie] Dream-state extraction error: {ex.Message}");
            }
            return text;
        }

        /// <summary>
        /// Index just past the last line after <paramref name="pos"/> that still reads as block
        /// content (a key line, or the cut-off start of one). <paramref name="lastWholeLineEnd"/>:
        /// end of the last such line that was terminated by a line break, i.e. certainly complete.
        /// In a unified (JSON) reply line breaks are the two characters \n and the block may sit
        /// inside a string, whose closing quote ends it.
        /// </summary>
        private static int EndOfUnclosedBlock(string text, int pos, bool json, out int lastWholeLineEnd)
        {
            int consumedEnd = pos;
            lastWholeLineEnd = pos;
            int i = pos;
            while (i < text.Length)
            {
                int lineEnd = text.Length, next = text.Length;
                bool last = true, hitQuote = false;
                for (int j = i; j < text.Length; j++)
                {
                    char c = text[j];
                    if (c == '\n') { lineEnd = j; next = j + 1; last = false; break; }
                    if (!json) continue;
                    if (c == '\\' && j + 1 < text.Length)
                    {
                        if (text[j + 1] == 'n') { lineEnd = j; next = j + 2; last = false; break; }
                        j++;
                        continue;
                    }
                    if (c == '"') { lineEnd = j; next = j; last = false; hitQuote = true; break; }
                }

                string line = text.Substring(i, lineEnd - i).Replace("\\r", "").Trim();
                bool isKeyLine = DreamKeyRx.IsMatch(line);
                bool blockish = line.Length == 0 || isKeyLine || ((last || hitQuote) && IsTruncatedTail(line));
                if (!blockish) break;

                if (line.Length > 0) consumedEnd = hitQuote ? lineEnd : next;
                if (isKeyLine && !last && !hitQuote) lastWholeLineEnd = lineEnd;
                if (last || hitQuote) break;
                i = next;
            }
            return consumedEnd;
        }

        private static bool IsTruncatedTail(string line)
        {
            string l = line.ToLowerInvariant().Replace("\\/", "/");
            foreach (string tail in TruncatedTails)
                if (tail.StartsWith(l, StringComparison.Ordinal)) return true;
            return false;
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
