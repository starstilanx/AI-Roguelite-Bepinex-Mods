using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIROG_UnifiedBridge
{
    [BepInPlugin("com.airog.unifiedbridge", "AIROG UnifiedBridge", "1.0.0")]
    public class UnifiedBridgePlugin : BaseUnityPlugin
    {
        private static readonly Harmony _harmony = new Harmony("com.airog.unifiedbridge");

        void Awake()
        {
            _harmony.PatchAll(typeof(UnifiedBridgePlugin).Assembly);
            Logger.LogInfo("UnifiedBridge loaded — auto-converting Specialized preambles for Unified mode.");
        }
    }

    /// <summary>
    /// Patches NarrativeFlavor loading to auto-generate UNIFIED_ versions of any
    /// Workshop mod preamble that only ships Specialized-mode files.
    /// Runs before the game's hasUnifiedSupport check, so generated entries are
    /// picked up as if the mod author had provided them.
    /// </summary>
    [HarmonyPatch(typeof(NarrativeFlavor), "GetProcessedNarrativeFlavor")]
    public static class Patch_NarrativeFlavor_GetProcessedNarrativeFlavor
    {
        [HarmonyPrefix]
        static void Prefix(ModdableNarrativeFlavor mod, Dictionary<string, string> storyPreamblesV2Dict)
        {
            PreambleBridge.EnsureUnifiedSupport(mod, storyPreamblesV2Dict);
        }
    }

    public static class PreambleBridge
    {
        // Sentences that instruct the AI to produce plain text only.
        // These are incompatible with Unified mode, which requires JSON output.
        private static readonly string[] _incompatiblePhrases = new[]
        {
            "Output only story text, never bracketed nor metadata text.",
            "Continue the story according to the bracketed instructions.",
            "Important: When writing, follow the bracketed instructions at the end of the user prompt as completely and accurately as possible.",
            // Catch partial fragment in case surrounding punctuation differs
            "never bracketed nor metadata text",
        };

        // Matches ${AUTO_SUBSTR_some_file_name} — plain-text auto-substrings only.
        // AUTO_JSON_SUBSTR_ refs are intentionally NOT matched; they stay as template
        // references for the game to expand later.
        private static readonly Regex _autoSubstrRegex =
            new Regex(@"\$\{AUTO_SUBSTR_([a-zA-Z0-9_.\-]+)\}");

        // -----------------------------------------------------------------------
        // Entry point called from the Harmony prefix
        // -----------------------------------------------------------------------

        public static void EnsureUnifiedSupport(ModdableNarrativeFlavor mod, Dictionary<string, string> dict)
        {
            try
            {
                if (!string.IsNullOrEmpty(mod.preamble_file_name))
                {
                    // Direct preamble flavor — ensure its UNIFIED_ counterpart exists
                    EnsureUnifiedForKey(Path.GetFileName(mod.preamble_file_name), dict);
                }
                else if (!string.IsNullOrEmpty(mod.algo_file_name))
                {
                    // Algorithmic flavor — need UNIFIED_ for the default preamble
                    // AND every rule's preamble that the algo may switch to
                    string algoKey = Path.GetFileName(mod.algo_file_name);
                    if (!dict.TryGetValue(algoKey, out string algoJson)) return;

                    JObject algo = ParseAlgoJson(algoJson);
                    if (algo == null) return;

                    string defaultFile = (string)algo["default_file_name"];
                    if (!string.IsNullOrEmpty(defaultFile))
                        EnsureUnifiedForKey(Path.GetFileName(defaultFile), dict);

                    JArray rules = algo["rules"] as JArray;
                    if (rules != null)
                    {
                        foreach (JObject rule in rules)
                        {
                            string ruleFile = (string)rule["file_name"];
                            if (!string.IsNullOrEmpty(ruleFile))
                                EnsureUnifiedForKey(Path.GetFileName(ruleFile), dict);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnifiedBridge] EnsureUnifiedSupport failed for '{mod}': {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Per-key generation
        // -----------------------------------------------------------------------

        private static void EnsureUnifiedForKey(string key, Dictionary<string, string> dict)
        {
            string unifiedKey = "UNIFIED_" + key;
            if (dict.ContainsKey(unifiedKey)) return;           // Mod already provides a Unified version
            if (!dict.TryGetValue(key, out string specialized)) return; // Source missing

            try
            {
                string unified = GenerateUnifiedPreamble(specialized, dict);
                if (unified != null)
                {
                    dict[unifiedKey] = unified;
                    Debug.Log($"[UnifiedBridge] Auto-generated {unifiedKey}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnifiedBridge] Failed to generate {unifiedKey}: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Preamble conversion
        // -----------------------------------------------------------------------

        private static string GenerateUnifiedPreamble(string specialized, Dictionary<string, string> dict)
        {
            // Case 1 — preamble uses the game's standard Specialized common-part tokens.
            // These map 1-to-1 with their Unified equivalents, so a simple rename is safe.
            if (specialized.Contains("AUTO_JSON_SUBSTR_story_preamble_common_part"))
            {
                return specialized
                    .Replace("AUTO_JSON_SUBSTR_story_preamble_common_part1",
                             "AUTO_JSON_SUBSTR_unified_common_part1")
                    .Replace("AUTO_JSON_SUBSTR_story_preamble_common_part2",
                             "AUTO_JSON_SUBSTR_unified_common_part2");
            }

            // Case 2 — preamble uses custom AUTO_SUBSTR_ references (e.g. Reactive Realms).
            // Resolve those references, strip plain-text-only instructions, then rewrap
            // the remaining narrative guidance inside the Unified common-part tokens.

            JArray arr = JArray.Parse(specialized);
            string content = (string)arr[0]?["content"];
            if (string.IsNullOrEmpty(content)) return null;

            // Expand AUTO_SUBSTR_ references one level at a time (recursive, depth-limited).
            // AUTO_JSON_SUBSTR_ references are left intact as template vars for the game.
            content = ResolveAutoSubstr(content, dict);

            // Remove sentences that tell the AI to produce plain story text only —
            // those directly conflict with Unified mode's required JSON output.
            content = StripIncompatiblePhrases(content);
            content = content.Trim();

            if (string.IsNullOrWhiteSpace(content)) return null;

            // Wrap the surviving narrative guidance with the Unified structural tokens.
            // The game will expand AUTO_JSON_SUBSTR_unified_common_part1/2 at prompt
            // build time, injecting the JSON format instructions around our content.
            string contentValue =
                "${AUTO_JSON_SUBSTR_unified_common_part1}\n\n"
                + content
                + "\n\n${AUTO_JSON_SUBSTR_unified_common_part2}";

            var result = new JArray(new JObject(
                new JProperty("role", "system"),
                new JProperty("content", contentValue)
            ));

            return result.ToString(Formatting.None);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static string ResolveAutoSubstr(string content, Dictionary<string, string> dict, int depth = 0)
        {
            if (depth > 8) return content; // Guard against circular references

            return _autoSubstrRegex.Replace(content, match =>
            {
                // Key format expected by storyPreamblesV2Dict: "AUTO_SUBSTR_name.txt"
                string fileKey = "AUTO_SUBSTR_" + match.Groups[1].Value + ".txt";
                if (dict.TryGetValue(fileKey, out string value))
                    return ResolveAutoSubstr(value, dict, depth + 1);

                return match.Value; // Leave the token if the file isn't loaded
            });
        }

        private static string StripIncompatiblePhrases(string content)
        {
            foreach (string phrase in _incompatiblePhrases)
                content = content.Replace(phrase, string.Empty);

            // Collapse any runs of 3+ blank lines left behind by the removals
            content = Regex.Replace(content, @"\n{3,}", "\n\n");
            return content;
        }

        private static JObject ParseAlgoJson(string json)
        {
            try
            {
                // Newtonsoft supports C-style comments when configured
                return JObject.Parse(json,
                    new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            }
            catch
            {
                // Fallback: strip // line comments manually then re-parse
                try
                {
                    string stripped = Regex.Replace(json, @"//[^\n]*", string.Empty);
                    return JObject.Parse(stripped);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnifiedBridge] Could not parse algo JSON: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
