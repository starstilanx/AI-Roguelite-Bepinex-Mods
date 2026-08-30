using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Builds and sends the text-generation request. Temperature and token budgets are
    /// copied from OpenaiApiClient.GetGeneratedTextChatgpt so switching to Vertex doesn't
    /// silently change how verbose or how deterministic each prompt type is.
    /// </summary>
    public static class VertexTextClient
    {
        /// <summary>Extra output tokens reserved when the model spends budget on reasoning.</summary>
        private const int THINKING_HEADROOM_TOKENS = 1024;

        public static async Task<string> GenerateText(
            AIAsker.ChatGptPromptType promptType,
            string userMsg,
            string langOverride,
            FirebaseClient.HighCostMode hcMode,
            InteractionInfo interactionInfo,
            CancellationToken ct)
        {
            VertexTextModel model = VertexAIPlugin.Catalogue.TextModelById(VertexAIPlugin.CachedTextModel);

            double temperature = 0.9;
            int maxOutputTokens = (hcMode == FirebaseClient.HighCostMode.GRD) ? 15000 : 3000;
            switch (promptType)
            {
                case AIAsker.ChatGptPromptType.UNIFIED:
                    maxOutputTokens = 5000;
                    break;
                case AIAsker.ChatGptPromptType.STORY_COMPLETER:
                    maxOutputTokens = 260;
                    break;
                case AIAsker.ChatGptPromptType.CHAR_DESCRIPTION_GENERATOR:
                    maxOutputTokens = 160;
                    break;
                case AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER:
                case AIAsker.ChatGptPromptType.ENTITY_GENERATOR:
                    temperature = 0.7;
                    break;
                case AIAsker.ChatGptPromptType.OBVIOUS_QUESTION_ANSWERER:
                    temperature = 0.01;
                    break;
                case AIAsker.ChatGptPromptType.OBVIOUS_QUESTION_ANSWERER_SHORT:
                    temperature = 0.01;
                    maxOutputTokens = 16;
                    break;
                case AIAsker.ChatGptPromptType.EVENT_CHECKS_ANSWERER:
                    temperature = 0.01;
                    maxOutputTokens = 1000;
                    break;
                default:
                    VertexAIPlugin.Log.LogWarning($"[VertexAI] Unknown prompt type {promptType}, using defaults.");
                    break;
            }

            JArray preamble = Utils.GetPreamble(SS.I.Manager(), promptType, langOverride, PanoNode.ImgArgMode.NONE, interactionInfo);
            VertexApiClient.ConvertPreamble(preamble, out JObject systemInstruction, out JArray contents);
            VertexApiClient.AppendTurn(contents, "user", userMsg);

            int baseMaxOutputTokens = maxOutputTokens;
            VertexAIPlugin.Log.LogInfo($"[VertexAI] text gen: model={model.id} type={promptType} temp={temperature}");

            JObject response = await SendWithThinkingFallback(model, (thinkingConfig, spendsTokens) =>
            {
                // Reasoning tokens are billed against maxOutputTokens, so a tight budget like
                // the 16 used for OBVIOUS_QUESTION_ANSWERER_SHORT would return nothing at all.
                int maxOut = baseMaxOutputTokens + (spendsTokens ? THINKING_HEADROOM_TOKENS : 0);

                var generationConfig = new JObject
                {
                    ["temperature"] = temperature,
                    ["maxOutputTokens"] = maxOut,
                    ["candidateCount"] = 1,
                };
                if (thinkingConfig != null) generationConfig["thinkingConfig"] = thinkingConfig;

                JObject extra = VertexAIPlugin.Catalogue?.extraGenerationConfig;
                if (extra != null)
                    generationConfig.Merge(extra, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });

                // Deep-cloned per attempt: Json.NET refuses to re-parent a token that
                // already belongs to a previous attempt's body.
                var body = new JObject
                {
                    ["contents"] = contents.DeepClone(),
                    ["generationConfig"] = generationConfig,
                };
                if (systemInstruction != null) body["systemInstruction"] = systemInstruction.DeepClone();

                JArray safety = VertexApiClient.BuildSafetySettings();
                if (safety != null) body["safetySettings"] = safety;

                return body;
            }, ct);

            string generated = VertexApiClient.ExtractText(response);
            VertexAIPlugin.Log.LogInfo($"[VertexAI] text gen ok: {VertexApiClient.Truncate(generated, 200)}");
            return generated;
        }

        /// <summary>
        /// Plain completion-style call, used only by the game's legacy non-chat path.
        /// Vertex has no completions endpoint, so the prompt is sent as a single user turn.
        /// </summary>
        public static async Task<string> GenerateCompletion(string prompt, int maxTokens, double temperature, CancellationToken ct)
        {
            VertexTextModel model = VertexAIPlugin.Catalogue.TextModelById(VertexAIPlugin.CachedTextModel);

            JObject response = await SendWithThinkingFallback(model, (thinkingConfig, spendsTokens) =>
            {
                var generationConfig = new JObject
                {
                    ["temperature"] = temperature,
                    ["maxOutputTokens"] = maxTokens + (spendsTokens ? THINKING_HEADROOM_TOKENS : 0),
                    ["candidateCount"] = 1,
                };
                if (thinkingConfig != null) generationConfig["thinkingConfig"] = thinkingConfig;

                var contents = new JArray();
                VertexApiClient.AppendTurn(contents, "user", prompt);

                var body = new JObject
                {
                    ["contents"] = contents,
                    ["generationConfig"] = generationConfig,
                };
                JArray safety = VertexApiClient.BuildSafetySettings();
                if (safety != null) body["safetySettings"] = safety;

                return body;
            }, ct);

            return VertexApiClient.ExtractText(response);
        }

        /// <summary>
        /// Sends the request, stepping down the thinking ladder when the model rejects the
        /// setting. Which levels a model accepts can't be predicted from its name — Vertex
        /// returns 400 "Thinking level is unsupported" for gemini-3.7-flash + minimal, even
        /// though Flash models are documented to take it — so the working setting is
        /// discovered once and then cached for the session.
        /// </summary>
        private static async Task<JObject> SendWithThinkingFallback(
            VertexTextModel model, Func<JObject, bool, JObject> buildBody, CancellationToken ct)
        {
            string configured = VertexApiClient.ResolveThinkingSetting(model);
            List<string> ladder = VertexApiClient.ThinkingLadder(model.id, configured);

            for (int i = 0; i < ladder.Count; i++)
            {
                string setting = ladder[i];
                JObject thinkingConfig = VertexApiClient.BuildThinkingConfig(setting, out bool spendsTokens);
                JObject body = buildBody(thinkingConfig, spendsTokens);

                try
                {
                    JObject response = await VertexApiClient.PostGenerateContent(model.id, body, ct);
                    VertexApiClient.RememberThinking(model.id, setting);
                    return response;
                }
                catch (HttpException ex) when (i < ladder.Count - 1 && VertexApiClient.IsThinkingRejection(ex))
                {
                    VertexAIPlugin.Log.LogWarning(
                        $"[VertexAI] {model.id} rejected thinking setting " +
                        $"'{Describe(setting)}' — retrying with '{Describe(ladder[i + 1])}'. " +
                        "Set \"thinking\" for this model in " + VertexCatalogue.FILE_NAME + " to skip this probe.");
                }
            }

            // Unreachable: the ladder always ends in "", which every model accepts.
            throw new HttpException($"Vertex AI: exhausted thinking fallbacks for {model.id}.");
        }

        private static string Describe(string setting)
        {
            return string.IsNullOrEmpty(setting) ? "none" : setting;
        }
    }
}
