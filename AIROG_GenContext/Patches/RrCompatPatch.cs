using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.Patches
{
    /// <summary>
    /// Two recovery paths for AiOutputException from Utils.GetAiJsonSubstr:
    ///
    /// 1. Truncated JSON (starts with '{' but braces are unbalanced) — happens when the AI
    ///    response is cut off before the closing braces, e.g. mid api.give_items array.
    ///    We close all open containers so the game gets a valid, partial JSON object.
    ///    Active always — no toggle needed.
    ///
    /// 2. Plain-text response (no JSON at all) — happens in RR/Reactive Realms mode where
    ///    the workshop preset strips the UNIFIED preamble, so the AI writes plain narrative.
    ///    We wrap the text as {"story":"..."} so the game can continue.
    ///    Gated behind "RR Compat Mode" toggle in the GenContext Mod Manager.
    /// </summary>
    [HarmonyPatch(typeof(Utils), nameof(Utils.GetAiJsonSubstr))]
    public static class Patch_GetAiJsonSubstr
    {
        /// <summary>The text of the latest UNIFIED story response, exactly as the game is about to parse it.</summary>
        internal static string UnifiedStoryResponse;

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ref string __result, string s)
        {
            if (!(__exception is AiOutputException)) return __exception;

            // Utils.GetAiJsonSubstr is shared by every JSON the game asks an AI for (perk trees,
            // quests, calendars, encounters, ...), and most of those callers now regenerate when it
            // throws. Both recoveries below are meant for the story response alone; applied to the
            // others they turned a retry into a half-built perk tree, or into an object with every
            // field empty.
            if (s == null || s != UnifiedStoryResponse) return __exception;

            string trimmed = s.TrimStart();

            // Path 1: looks like JSON but is truncated — repair by closing open braces.
            if (trimmed.StartsWith("{"))
            {
                string repaired = TryRepairTruncatedJson(s);
                // Closing the open containers does not always yield JSON: a response cut off right
                // after a key or a colon ({"story":"...","api":{"give_items":) closes into something
                // unparseable. Handing that to the game would trade the AiOutputException it knows
                // how to recover from for a raw JsonReaderException it does not, so only swallow
                // the exception when the repair actually parses.
                if (repaired != null && !IsParseableJson(repaired)) repaired = null;
                if (repaired != null)
                {
                    __result = repaired;
                    Debug.LogWarning("[GenContext] Repaired truncated UNIFIED JSON (unclosed braces). Story and resolution preserved; api may be partial.");
                    return null;
                }
            }

            // Path 2: plain-text response (no JSON) — RRCompat wrap. Not for a response that set out
            // as JSON and could not be repaired: wrapping that would print raw JSON as the story.
            if (!trimmed.StartsWith("{") && ContextManager.GetGlobalSetting("RRCompat"))
            {
                string storyJson = JsonConvert.SerializeObject(s);
                __result = $"{{\"story\":{storyJson}}}";
                Debug.Log("[GenContext] RR Compat: wrapped plain-text AI response as UNIFIED JSON story.");
                return null;
            }

            return __exception;
        }

        private static bool IsParseableJson(string candidate)
        {
            try
            {
                int start = candidate.IndexOf('{');
                if (start < 0) return false;
                Newtonsoft.Json.Linq.JToken.Parse(candidate.Substring(start));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Walks the string tracking open braces/brackets (skipping string contents),
        /// then closes any that were never closed. Returns null if already balanced.
        /// </summary>
        private static string TryRepairTruncatedJson(string s)
        {
            var stack = new Stack<char>();
            bool inString = false;
            bool escape = false;

            foreach (char c in s)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{' || c == '[') stack.Push(c);
                else if (c == '}' || c == ']')
                {
                    if (stack.Count > 0) stack.Pop();
                }
            }

            if (stack.Count == 0) return null; // already balanced, not a truncation

            var sb = new StringBuilder(s);

            // If truncated mid-string, close the open string literal first
            if (inString) sb.Append('"');

            // Close open containers innermost-first
            while (stack.Count > 0)
                sb.Append(stack.Pop() == '{' ? '}' : ']');

            return sb.ToString();
        }
    }

    /// <summary>
    /// Remembers the UNIFIED story response, so <see cref="Patch_GetAiJsonSubstr"/> can tell the
    /// story's JSON from every other JSON that passes through Utils.GetAiJsonSubstr. The game hands
    /// the string this method returns straight to that parser.
    /// </summary>
    [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.GenerateUnifiedResponse))]
    public static class Patch_GenerateUnifiedResponse
    {
        [HarmonyPostfix]
        public static void Postfix(ref Task<string> __result)
        {
            if (__result != null) __result = Remember(__result);
        }

        private static async Task<string> Remember(Task<string> original)
        {
            string text = await original;
            Patch_GetAiJsonSubstr.UnifiedStoryResponse = text;
            return text;
        }
    }
}
