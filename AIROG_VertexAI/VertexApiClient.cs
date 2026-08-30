using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Vertex AI express-mode REST client. Express mode uses the global
    /// aiplatform.googleapis.com endpoint with no project or location in the path, and
    /// authenticates with a single API key:
    ///
    ///   POST https://aiplatform.googleapis.com/v1/publishers/google/models/{model}:generateContent
    ///
    /// Requests go through the game's MyHttpClient so timeouts, cancellation and the
    /// game's error plumbing behave exactly like every other backend. The key travels in
    /// an x-goog-api-key header rather than the documented ?key= query parameter, so it
    /// never lands in the game's URL logging.
    /// </summary>
    public static class VertexApiClient
    {
        private const string HOST = "https://aiplatform.googleapis.com";

        private static readonly object _gateLock = new object();
        private static SemaphoreSlim _gate;
        private static int _gateSize;

        /// <summary>
        /// Throttles concurrent Vertex calls. Background AI tasks can reach this from
        /// several threads at once, so the lazy init is locked — two threads racing here
        /// would otherwise each build a semaphore and neither would throttle the other.
        /// </summary>
        private static SemaphoreSlim Gate()
        {
            int want = Math.Max(1, VertexAIPlugin.Catalogue?.maxConcurrentRequests ?? 1);
            lock (_gateLock)
            {
                if (_gate == null || _gateSize != want)
                {
                    _gate = new SemaphoreSlim(want, want);
                    _gateSize = want;
                }
                return _gate;
            }
        }

        private static readonly string[] HARM_CATEGORIES =
        {
            "HARM_CATEGORY_HARASSMENT",
            "HARM_CATEGORY_HATE_SPEECH",
            "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_DANGEROUS_CONTENT",
        };

        public static string BuildUrl(string model)
        {
            string version = VertexAIPlugin.Catalogue?.apiVersion;
            if (string.IsNullOrEmpty(version)) version = "v1";
            return $"{HOST}/{version}/publishers/google/models/{model}:generateContent";
        }

        /// <summary>safetySettings with every category at the configured threshold, or null to omit.</summary>
        public static JArray BuildSafetySettings()
        {
            string threshold = VertexAIPlugin.Catalogue?.safetyThreshold;
            if (string.IsNullOrEmpty(threshold)) return null;

            var arr = new JArray();
            foreach (string category in HARM_CATEGORIES)
                arr.Add(new JObject { ["category"] = category, ["threshold"] = threshold });
            return arr;
        }

        /// <summary>
        /// Thinking settings proven to work, keyed by model id. Populated the first time a
        /// request succeeds so later calls skip attempts already known to 400.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _resolvedThinking =
            new ConcurrentDictionary<string, string>();

        /// <summary>
        /// The model's configured thinking setting, or an auto-detected default.
        /// Which levels a model accepts is NOT predictable from its family — gemini-3.7-flash
        /// rejects "minimal" even though Flash models are documented to support it — so the
        /// default is the conservative "low" and <see cref="ThinkingLadder"/> handles the rest.
        /// </summary>
        public static string ResolveThinkingSetting(VertexTextModel model)
        {
            string setting = model?.thinking;
            string id = model?.id ?? "";

            if (setting == null)
            {
                if (id.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase)) setting = "0";
                else if (id.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase)) setting = "low";
                else setting = "";
            }
            return setting.Trim();
        }

        /// <summary>
        /// Settings to try in order, most preferred first, ending in "" (send no
        /// thinkingConfig at all) which every model accepts. Once a model's working setting
        /// is known the ladder collapses to just that, so the retry costs one round trip
        /// per model per session rather than one per request.
        /// </summary>
        public static List<string> ThinkingLadder(string modelId, string configured)
        {
            if (modelId != null && _resolvedThinking.TryGetValue(modelId, out string known))
                return new List<string> { known };

            var ladder = new List<string>();
            void Add(string s) { if (!ladder.Contains(s)) ladder.Add(s); }

            Add(configured);
            if (!int.TryParse(configured, out _))
            {
                // Downgrade through the levels before giving up on thinking control entirely.
                if (configured.Equals("minimal", StringComparison.OrdinalIgnoreCase) ||
                    configured.Equals("medium", StringComparison.OrdinalIgnoreCase))
                    Add("low");
            }
            Add("");
            return ladder;
        }

        public static void RememberThinking(string modelId, string setting)
        {
            if (modelId != null) _resolvedThinking[modelId] = setting;
        }

        /// <summary>True for the 400 a model returns when it rejects the thinking parameter.</summary>
        public static bool IsThinkingRejection(HttpException ex)
        {
            return ex != null && ex.code == 400 && ex.Message != null &&
                   ex.Message.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Turns a thinking setting into a thinkingConfig object. Gemini 3.x takes
        /// thinkingLevel; Gemini 2.5 takes a numeric thinkingBudget; "" omits it.
        /// <paramref name="spendsTokens"/> reports whether reasoning will eat into
        /// maxOutputTokens, so callers can add headroom.
        /// </summary>
        public static JObject BuildThinkingConfig(string setting, out bool spendsTokens)
        {
            setting = (setting ?? "").Trim();

            if (setting.Length == 0 || setting.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown budget — assume the model thinks and reserve room for it.
                spendsTokens = true;
                return null;
            }

            if (int.TryParse(setting, out int budget))
            {
                spendsTokens = budget != 0;
                return new JObject { ["thinkingBudget"] = budget };
            }

            spendsTokens = true;
            return new JObject { ["thinkingLevel"] = setting.ToLowerInvariant() };
        }

        /// <summary>
        /// Converts the game's OpenAI-shaped preamble (a JArray of {role, content}) into
        /// Gemini's split systemInstruction + contents form. System turns are folded into
        /// systemInstruction; assistant turns become role "model"; consecutive same-role
        /// turns are merged because Gemini expects alternating conversation roles.
        /// </summary>
        public static void ConvertPreamble(JArray preamble, out JObject systemInstruction, out JArray contents)
        {
            var systemParts = new List<string>();
            contents = new JArray();
            systemInstruction = null;

            if (preamble != null)
            {
                foreach (JToken turn in preamble)
                {
                    string role = turn["role"]?.ToString() ?? "user";
                    JToken contentTok = turn["content"];
                    string content = contentTok == null
                        ? ""
                        : (contentTok.Type == JTokenType.String ? contentTok.ToString() : contentTok.ToString(Newtonsoft.Json.Formatting.None));
                    if (string.IsNullOrEmpty(content)) continue;

                    if (role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                        role.Equals("developer", StringComparison.OrdinalIgnoreCase))
                    {
                        systemParts.Add(content);
                        continue;
                    }

                    string geminiRole = (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                                         role.Equals("model", StringComparison.OrdinalIgnoreCase))
                        ? "model" : "user";
                    AppendTurn(contents, geminiRole, content);
                }
            }

            if (systemParts.Count > 0)
            {
                var parts = new JArray { new JObject { ["text"] = string.Join("\n\n", systemParts) } };
                systemInstruction = new JObject { ["parts"] = parts };
            }
        }

        /// <summary>Adds a turn, merging into the previous one when the role repeats.</summary>
        public static void AppendTurn(JArray contents, string role, string text)
        {
            if (contents.Count > 0)
            {
                JToken last = contents[contents.Count - 1];
                if (last["role"]?.ToString() == role)
                {
                    ((JArray)last["parts"]).Add(new JObject { ["text"] = text });
                    return;
                }
            }
            contents.Add(new JObject
            {
                ["role"] = role,
                ["parts"] = new JArray { new JObject { ["text"] = text } },
            });
        }

        /// <summary>
        /// POSTs a generateContent body and returns the parsed response. Throws
        /// HttpException (the game's own type, so existing error handling applies) on
        /// transport errors, non-2xx responses, and malformed JSON.
        /// </summary>
        public static async Task<JObject> PostGenerateContent(string model, JObject body, CancellationToken ct)
        {
            string apiKey = VertexAIPlugin.CachedApiKey;
            if (string.IsNullOrEmpty(apiKey))
                throw new HttpException("Vertex AI: no API key set. Open Options and paste your Vertex AI express-mode API key.");

            string url = BuildUrl(model);
            var headers = new Dictionary<string, string> { { "x-goog-api-key", apiKey } };

            VertexAIPlugin.Log.LogInfo($"[VertexAI] POST {url} ({body.ToString(Newtonsoft.Json.Formatting.None).Length} bytes)");

            SemaphoreSlim gate = Gate();
            await gate.WaitAsync(ct);
            string respStr;
            try
            {
                int delayMs = SS.I?.apiDelayMs ?? 0;
                if (delayMs > 0) await Task.Delay(delayMs, ct);

                int timeoutMs = SS.I?.defaultTimeoutMs ?? 0;
                respStr = await MyHttpClient.I.DoHttpRequest(
                    HttpMethod.Post, url, body.ToString(Newtonsoft.Json.Formatting.None),
                    contentIsJson: true, customContentType: null, timeoutMs: timeoutMs,
                    token: null, apiKey: null, cookie: null, includeUserAgent: true,
                    authHeaders: headers, verbose: false, ct: ct);
            }
            finally
            {
                gate.Release();
            }

            return MyHttpClient.ParseRespJson(respStr, "Vertex AI");
        }

        /// <summary>
        /// Pulls the generated text out of a generateContent response, skipping the
        /// model's internal "thought" parts, and turns Google's several distinct ways of
        /// returning nothing into an actionable message.
        /// </summary>
        public static string ExtractText(JObject response)
        {
            string blockReason = response["promptFeedback"]?["blockReason"]?.ToString();
            if (!string.IsNullOrEmpty(blockReason))
                throw new HttpException($"Vertex AI blocked the prompt (blockReason={blockReason}). " +
                                        "Try lowering safetyThreshold in airog_vertexai_models.json, or a different model.");

            JToken candidate = response["candidates"]?[0];
            if (candidate == null)
                throw new HttpException($"Vertex AI returned no candidates. Response: {Truncate(response.ToString(), 600)}");

            var sb = new System.Text.StringBuilder();
            JToken parts = candidate["content"]?["parts"];
            if (parts != null)
            {
                foreach (JToken part in parts)
                {
                    if (part["thought"]?.Type == JTokenType.Boolean && part["thought"].Value<bool>()) continue;
                    string text = part["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text)) sb.Append(text);
                }
            }

            if (sb.Length > 0) return sb.ToString();

            string finishReason = candidate["finishReason"]?.ToString() ?? "UNKNOWN";
            if (finishReason == "MAX_TOKENS")
                throw new HttpException("Vertex AI hit MAX_TOKENS before emitting any text — the model spent its whole " +
                                        "budget on reasoning. Lower the model's \"thinking\" setting in " +
                                        "airog_vertexai_models.json, or pick a Flash model.");
            throw new HttpException($"Vertex AI returned an empty response (finishReason={finishReason}). " +
                                    $"Response: {Truncate(response.ToString(), 600)}");
        }

        /// <summary>Returns the first inline image in the response as raw bytes, or null if there is none.</summary>
        public static byte[] ExtractImageBytes(JObject response, out string failureReason)
        {
            failureReason = null;

            string blockReason = response["promptFeedback"]?["blockReason"]?.ToString();
            if (!string.IsNullOrEmpty(blockReason))
            {
                failureReason = $"prompt blocked (blockReason={blockReason})";
                return null;
            }

            JToken parts = response["candidates"]?[0]?["content"]?["parts"];
            if (parts != null)
            {
                foreach (JToken part in parts)
                {
                    if (part["thought"]?.Type == JTokenType.Boolean && part["thought"].Value<bool>()) continue;

                    // camelCase from the REST API; snake_case shows up via some proxies.
                    string base64 = part["inlineData"]?["data"]?.ToString()
                                 ?? part["inline_data"]?["data"]?.ToString();
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try { return Convert.FromBase64String(base64); }
                        catch (FormatException ex)
                        {
                            failureReason = "inline image data was not valid base64: " + ex.Message;
                            return null;
                        }
                    }
                }
            }

            string finishReason = response["candidates"]?[0]?["finishReason"]?.ToString() ?? "UNKNOWN";
            failureReason = $"no image in response (finishReason={finishReason}): {Truncate(response.ToString(), 600)}";
            return null;
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...[truncated]";
        }
    }
}
