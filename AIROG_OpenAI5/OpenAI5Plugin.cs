using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AIROG_OpenAI5
{
    [BepInPlugin("com.airog.openai5", "AIROG OpenAI5 Compatibility", "1.0.1")]
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
    /// OpenAI's reasoning models (o1, o3, o4-mini, gpt-5, ...) reject max_tokens and require
    /// max_completion_tokens instead, and accept only temperature=1. This patch rewrites the
    /// request body before it is sent so that users can select these models without getting a
    /// BadRequest error. The game itself still sends max_tokens + its own temperature for every
    /// OpenAI-compatible request (OpenaiApiClient.GetGeneratedTextChatgpt, 11/18 build).
    /// </summary>
    [HarmonyPatch(typeof(MyHttpClient), nameof(MyHttpClient.DoHttpRequest))]
    public static class MaxTokensRewritePatch
    {
        // Reasoning models. These reject max_tokens outright, accept only temperature=1, and pay
        // for their hidden reasoning out of the completion budget:
        //   - o1, o3, o4-mini, ... (o-series)
        //   - gpt-5 and beyond (gpt-5, gpt-5-mini, gpt-5.1, ...)
        private static bool IsReasoningModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string lower = model.Trim().ToLowerInvariant();
            // o-series: o1, o1-mini, o3, o3-mini, o4, o4-mini, o5...
            if (lower.Length >= 2 && lower[0] == 'o' && char.IsDigit(lower[1])) return true;
            // gpt-5 and future
            if (lower.StartsWith("gpt-5")) return true;
            return false;
        }

        // Models whose max_tokens is renamed to max_completion_tokens: the reasoning models above,
        // plus the gpt-4.1 family (gpt-4.1, gpt-4.1-mini, gpt-4.1-nano). gpt-4.1 is an ordinary
        // chat model - it has no reasoning budget and honours temperature - so it gets the rename
        // only, with the game's own limit and temperature left as they are.
        private static bool NeedsMaxCompletionTokens(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            if (IsReasoningModel(model)) return true;
            return model.Trim().ToLowerInvariant().StartsWith("gpt-4.1");
        }

        // Last, so that it sees max_tokens after any other mod's prefix has adjusted it. This plugin
        // loads (and so patches) before AIROG_TokenModifierPlugin; at equal priority it ran first,
        // renamed the key, and the player's token-limit setting was never applied to these models.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static void Prefix(ref string contentStr, string url)
        {
            if (string.IsNullOrEmpty(contentStr)) return;
            if (url == null || !url.Contains("/v1/chat/completions")) return;

            string model = SS.I?.openaiApiModel;
            if (!NeedsMaxCompletionTokens(model)) return;
            bool reasoning = IsReasoningModel(model);

            try
            {
                JObject jo = JObject.Parse(contentStr);
                bool changed = false;

                if (jo["max_tokens"] != null)
                {
                    // Reasoning models (gpt-5, o-series) spend internal tokens on thinking before
                    // outputting, all counted against max_completion_tokens. The game's default
                    // values (e.g. 260 for a story turn) leave too little room for a response after
                    // reasoning. Boost to at least 8192 to give the model enough headroom.
                    // gpt-4.1 does no hidden reasoning, so its limit is passed through unchanged:
                    // boosting it would only lift the game's cap on story length and short answers.
                    int originalVal = jo["max_tokens"].Value<int>();
                    int boostedVal = reasoning ? Math.Max(originalVal, 8192) : originalVal;
                    jo["max_completion_tokens"] = boostedVal;
                    jo.Remove("max_tokens");
                    changed = true;
                    OpenAI5Plugin.Log.LogInfo($"[OpenAI5] Rewrote max_tokens ({originalVal}) -> max_completion_tokens ({boostedVal}) for model: {model}");
                }

                // Reasoning models only support temperature=1. Set it explicitly rather than
                // removing it, to avoid the model using an unexpected default. gpt-4.1 keeps the
                // game's value - event checks ask for 0.01 so their answers are repeatable.
                if (reasoning && jo["temperature"] != null && jo["temperature"].Value<double>() != 1.0)
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
