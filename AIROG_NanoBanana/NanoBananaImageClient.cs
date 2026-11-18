using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace AIROG_NanoBanana
{
    /// <summary>
    /// Gemini image-generation HTTP call, extracted out of NanoBananaPlugin so the
    /// plugin class only holds bootstrap/config concerns.
    /// </summary>
    public static class NanoBananaImageClient
    {
        // Models that 400'd with the optional generationConfig extras but worked without them.
        // Which extras a Gemini image model accepts is not predictable from its name, so this is
        // learned once per session instead of failing every image.
        private static readonly ConcurrentDictionary<string, bool> _plainRequestOnly =
            new ConcurrentDictionary<string, bool>();

        public static async Task<GameEntity.ImgGenState> GenerateGeminiImage(
            string apiKey, string model, GameEntity geArg, GameEntity.ImgGenInfo imgGenInfo, string prompt)
        {
            try
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    NanoBananaPlugin.Log.LogError("NanoBanana: Gemini API Key is missing! Please set it in the options menu or BepInEx config.");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }

                // Resolved before the request, like the game's own clients: the save folder can
                // change while the request is in flight (quit to menu, load another save).
                string filePathNoExt = geArg.GetImgPathNoExt(imgGenInfo.imgType);
                string resolution = NanoBananaPlugin.Instance.GeminiResolution;

                // Construct the URL (API key is passed as a header, not query param)
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

                NanoBananaPlugin.Log.LogInfo($"NanoBanana: Sending request to Gemini ({model}) for {geArg.name}");

                bool withExtras = !_plainRequestOnly.ContainsKey(model);
                string responseText = null;
                while (true)
                {
                    JObject body = BuildBody(prompt, resolution, withExtras);

                    using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                    {
                        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                        request.downloadHandler = new DownloadHandlerBuffer();
                        request.timeout = 120;
                        request.SetRequestHeader("Content-Type", "application/json");
                        request.SetRequestHeader("x-goog-api-key", apiKey);

                        // Send the request and wait for completion
                        var operation = request.SendWebRequest();
                        while (!operation.isDone)
                        {
                            await Task.Yield();
                        }

                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            string errBody = request.downloadHandler.text ?? "";
                            if (errBody.Length > 800) errBody = errBody.Substring(0, 800) + "...[truncated]";

                            if (withExtras && request.responseCode == 400)
                            {
                                NanoBananaPlugin.Log.LogWarning($"NanoBanana: {model} rejected the request ({request.error}); retrying without thinking/image-size options.\n{errBody}");
                                withExtras = false;
                                continue;
                            }

                            NanoBananaPlugin.Log.LogError($"NanoBanana: Gemini API Error ({request.responseCode}): {request.error}\n{errBody}");
                            return GameEntity.ImgGenState.REGULAR_FAILED;
                        }

                        responseText = request.downloadHandler.text;
                    }
                    break;
                }

                if (!withExtras && _plainRequestOnly.TryAdd(model, true))
                    NanoBananaPlugin.Log.LogWarning($"NanoBanana: {model} only accepts plain requests; the preset's resolution is not applied to it this session.");

                // Parse the response
                JObject response = JObject.Parse(responseText);
                var candidates = response["candidates"];
                if (candidates != null && candidates.HasValues)
                {
                    var candidateParts = candidates[0]?["content"]?["parts"];
                    if (candidateParts != null)
                    {
                        foreach (var part in candidateParts)
                        {
                            if (part["thought"]?.Value<bool>() == true) continue;

                            if (part["inlineData"] != null)
                            {
                                string base64Data = part["inlineData"]["data"]?.ToString();
                                if (!string.IsNullOrEmpty(base64Data))
                                {
                                    // The entity's save was unloaded while we waited; its folder is no longer ours to write to.
                                    if (Utils.GenTargetAbandoned(geArg)) return GameEntity.ImgGenState.REGULAR_FAILED;

                                    byte[] imageBytes = Convert.FromBase64String(base64Data);
                                    string fullPath = filePathNoExt + ".png";

                                    // Ensure directory exists
                                    string dir = Path.GetDirectoryName(fullPath);
                                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                                    File.WriteAllBytes(fullPath, imageBytes);
                                    NanoBananaPlugin.Log.LogInfo($"NanoBanana: Successfully generated and saved image for {geArg.name} at {fullPath}");
                                    return GameEntity.ImgGenState.FINISHED;
                                }
                            }
                        }
                    }
                }

                // Log only the first 800 chars of the response for diagnosis
                string respPreview = responseText;
                if (respPreview.Length > 800) respPreview = respPreview.Substring(0, 800) + "...[truncated]";
                NanoBananaPlugin.Log.LogError($"NanoBanana: No image data found in Gemini response: {respPreview}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
            catch (Exception ex)
            {
                NanoBananaPlugin.Log.LogError($"NanoBanana: Exception during image generation: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
        }

        /// <summary>
        /// generateContent body. <paramref name="withExtras"/> adds the optional knobs (minimal
        /// thinking, the preset's output resolution); the plain form is the fallback every
        /// image model accepts.
        /// </summary>
        internal static JObject BuildBody(string prompt, string resolution, bool withExtras)
        {
            JObject generationConfig = new JObject
            {
                ["responseModalities"] = new JArray { "IMAGE" },
            };
            if (withExtras)
            {
                generationConfig["thinkingConfig"] = new JObject { ["thinkingLevel"] = "minimal" };
                // imageSize defaults to 1K when omitted, so only send it when the preset names one.
                if (!string.IsNullOrEmpty(resolution))
                    generationConfig["imageConfig"] = new JObject { ["imageSize"] = resolution };
            }

            return new JObject
            {
                ["generationConfig"] = generationConfig,
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JArray { new JObject { ["text"] = prompt } },
                    },
                },
            };
        }

        public static string GetAspectRatioForEntity(GameEntity entity, GameEntity.ImgGenInfo imgGenInfo)
        {
            if (imgGenInfo == entity.spGenInfo) return "2:3";   // sprites: upright character art
            if (entity is Place)                return "16:9";  // locations: landscape scene
            if (entity is GameCharacter)        return "3:4";   // characters/NPCs: portrait
            if (entity is GameItem)             return "1:1";   // inventory items: square
            return "4:3";                                        // static objects / fallback
        }
    }
}
