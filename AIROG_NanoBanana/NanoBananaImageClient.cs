using System;
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

                // Construct the URL (API key is passed as a header, not query param)
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

                JObject body = new JObject();

                JObject generationConfig = new JObject
                {
                    ["responseModalities"] = new JArray { "IMAGE" },
                    ["thinkingConfig"]     = new JObject { ["thinkingLevel"] = "minimal" },
                };
                body["generationConfig"] = generationConfig;

                JArray contents = new JArray();
                JObject content = new JObject { ["role"] = "user" };
                JArray parts = new JArray();
                parts.Add(new JObject { ["text"] = prompt });
                content["parts"] = parts;
                contents.Add(content);
                body["contents"] = contents;

                NanoBananaPlugin.Log.LogInfo($"NanoBanana: Sending request to Gemini ({model}) for {geArg.name}");

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
                        string errBody = request.downloadHandler.text;
                        if (errBody.Length > 800) errBody = errBody.Substring(0, 800) + "...[truncated]";
                        NanoBananaPlugin.Log.LogError($"NanoBanana: Gemini API Error ({request.responseCode}): {request.error}\n{errBody}");
                        return GameEntity.ImgGenState.REGULAR_FAILED;
                    }

                    // Parse the response
                    JObject response = JObject.Parse(request.downloadHandler.text);
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
                                        byte[] imageBytes = Convert.FromBase64String(base64Data);
                                        string filePathNoExt = geArg.GetImgPathNoExt(imgGenInfo.imgType);
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
                    string respPreview = request.downloadHandler.text;
                    if (respPreview.Length > 800) respPreview = respPreview.Substring(0, 800) + "...[truncated]";
                    NanoBananaPlugin.Log.LogError($"NanoBanana: No image data found in Gemini response: {respPreview}");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }
            }
            catch (Exception ex)
            {
                NanoBananaPlugin.Log.LogError($"NanoBanana: Exception during image generation: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
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
