using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AIROG_OpenAI5
{
    [BepInPlugin("com.airog.openai5", "AIROG OpenAI5 Compatibility", "1.0.0")]
    public class OpenAI5Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony("com.airog.openai5");
            _harmony.PatchAll();
            Log.LogInfo("AIROG_OpenAI5 loaded - max_completion_tokens support active for GPT-4.1+ and o-series models.");
        }
    }

    /// <summary>
    /// Newer OpenAI models (gpt-4.1, o1, o3, o4, ...) reject max_tokens and require
    /// max_completion_tokens instead. This patch rewrites the request body before it
    /// is sent so that users can select these models without getting a BadRequest error.
    /// </summary>
    [HarmonyPatch(typeof(MyHttpClient), nameof(MyHttpClient.DoHttpRequest))]
    public static class MaxTokensRewritePatch
    {
        // Models that require max_completion_tokens instead of max_tokens:
        //   - o1, o3, o4, o5... (OpenAI reasoning/o-series)
        //   - gpt-4.1, gpt-4.1-mini, gpt-4.1-nano, gpt-4.1-preview, ...
        //   - gpt-5 and beyond
        private static bool NeedsMaxCompletionTokens(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string lower = model.ToLowerInvariant();
            // o-series: o1, o1-mini, o3, o3-mini, o4, o4-mini, o5...
            if (lower.Length >= 2 && lower[0] == 'o' && char.IsDigit(lower[1])) return true;
            // gpt-4.1 family
            if (lower.StartsWith("gpt-4.1")) return true;
            // gpt-5 and future
            if (lower.StartsWith("gpt-5")) return true;
            return false;
        }

        [HarmonyPrefix]
        public static void Prefix(ref string contentStr, string url)
        {
            if (string.IsNullOrEmpty(contentStr)) return;
            if (url == null || !url.Contains("/v1/chat/completions")) return;

            string model = SS.I?.openaiApiModel;
            if (!NeedsMaxCompletionTokens(model)) return;

            try
            {
                JObject jo = JObject.Parse(contentStr);
                bool changed = false;

                if (jo["max_tokens"] != null)
                {
                    // Reasoning models (gpt-5, o-series) spend internal tokens on thinking before
                    // outputting, all counted against max_completion_tokens. The game's default
                    // values (e.g. 1090) leave too little room for complex responses after reasoning.
                    // Boost to at least 8192 to give the model enough headroom.
                    int originalVal = jo["max_tokens"].Value<int>();
                    int boostedVal = Math.Max(originalVal, 8192);
                    jo["max_completion_tokens"] = boostedVal;
                    jo.Remove("max_tokens");
                    changed = true;
                    OpenAI5Plugin.Log.LogInfo($"[OpenAI5] Rewrote max_tokens ({originalVal}) -> max_completion_tokens ({boostedVal}) for model: {model}");
                }

                // These models only support temperature=1. Set it explicitly rather than
                // removing it, to avoid the model using an unexpected default.
                if (jo["temperature"] != null && jo["temperature"].Value<double>() != 1.0)
                {
                    jo["temperature"] = 1.0;
                    changed = true;
                    OpenAI5Plugin.Log.LogInfo($"[OpenAI5] Forced temperature=1 for model: {model}");
                }

                if (changed)
                    contentStr = jo.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                OpenAI5Plugin.Log.LogWarning($"[OpenAI5] Failed to rewrite request body: {ex.Message}");
            }
        }
    }
}
