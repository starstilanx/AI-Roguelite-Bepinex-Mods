using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace AIROG_OpenAIImage
{
    /// <summary>Which dialect of the /images/generations body a model expects.</summary>
    internal enum ModelFamily
    {
        GptImage,   // gpt-image-*  — always returns b64, 400s if response_format is sent
        DallE3,     // dall-e-3     — response_format + quality, rejects moderation/output_format
        DallE2,     // dall-e-2     — response_format only, square sizes
        Compatible  // Venice, local servers, anything else
    }

    /// <summary>
    /// The OpenAI-compatible image call, kept separate from the plugin bootstrap and the
    /// Harmony patches. Mirrors AIROG_NanoBanana's client split.
    /// </summary>
    public static class OpenAIImageClient
    {
        private static readonly string[] SIZES_GPT_IMAGE = { "1024x1024", "1536x1024", "1024x1536" };
        private static readonly string[] SIZES_DALLE3    = { "1024x1024", "1792x1024", "1024x1792" };
        private static readonly string[] SIZES_DALLE2    = { "1024x1024" };

        internal static ModelFamily FamilyOf(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.StartsWith("gpt-image")) return ModelFamily.GptImage;
            if (m.StartsWith("dall-e-3"))  return ModelFamily.DallE3;
            if (m.StartsWith("dall-e-2"))  return ModelFamily.DallE2;
            return ModelFamily.Compatible;
        }

        /// <summary>True when the model can return a genuinely transparent background itself.</summary>
        internal static bool SupportsTransparency(string model) => FamilyOf(model) == ModelFamily.GptImage;

        /// <summary>Width/height the entity's art wants, before snapping to a supported size.</summary>
        internal static float DesiredAspectRatio(GameEntity geArg, GameEntity.ImgGenInfo imgGenInfo)
        {
            try
            {
                if (imgGenInfo != null && imgGenInfo.imgType == GameEntity.ImgType.SPRITE) return 2f / 3f;  // upright character art
                if (geArg is Place)         return 16f / 9f;  // locations: landscape scene
                if (geArg is GameCharacter) return 3f / 4f;   // portraits
                if (geArg is GameItem)      return 1f;        // inventory items: square
            }
            catch { /* type checks are best-effort; fall through to the default */ }
            return 4f / 3f;
        }

        /// <summary>Snaps the desired ratio to the nearest size the model actually accepts.</summary>
        internal static string PickSize(ModelFamily fam, float desiredRatio, string overrideSize)
        {
            if (!string.IsNullOrEmpty(overrideSize)) return overrideSize;
            if (desiredRatio <= 0f || float.IsNaN(desiredRatio)) desiredRatio = 1f;

            string[] allowed;
            switch (fam)
            {
                case ModelFamily.GptImage: allowed = SIZES_GPT_IMAGE; break;
                case ModelFamily.DallE3:   allowed = SIZES_DALLE3;    break;
                case ModelFamily.DallE2:   allowed = SIZES_DALLE2;    break;
                default:                   return "1024x1024"; // unknown server: play it safe
            }

            string best = allowed[0];
            double bestDelta = double.MaxValue;
            foreach (string s in allowed)
            {
                string[] parts = s.Split('x');
                if (parts.Length != 2) continue;
                if (!int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) || h == 0) continue;

                // Compare in log space so 16:9 vs 9:16 are equidistant from square.
                double delta = Math.Abs(Math.Log((double)w / h) - Math.Log(desiredRatio));
                if (delta < bestDelta) { bestDelta = delta; best = s; }
            }
            return best;
        }

        /// <summary>
        /// Generates one image and writes it to disk. <paramref name="wantTransparent"/> asks the
        /// model for a cut-out background where it can do that natively (gpt-image-*).
        /// <paramref name="imgPathNoExtOverride"/> supports AIAsker's useNewImgFileList variants.
        /// </summary>
        public static async Task<GameEntity.ImgGenState> GenerateImage(
            GameEntity geArg, GameEntity.ImgGenInfo imgGenInfo, string prompt,
            bool wantTransparent = false, string imgPathNoExtOverride = null)
        {
            try
            {
                var p = OpenAIImagePlugin.Instance;
                string apiKey = p.ApiKey;
                string model  = p.ActiveModel;

                string baseUrl = p.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://api.openai.com/v1";
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                string url = baseUrl + "images/generations";

                ModelFamily fam = FamilyOf(model);

                JObject body = new JObject
                {
                    ["model"]  = model,
                    ["prompt"] = prompt,
                    ["n"]      = 1,
                    ["size"]   = PickSize(fam, DesiredAspectRatio(geArg, imgGenInfo), p.SizeOverride),
                };

                string quality      = p.Quality;
                string moderation   = p.Moderation;
                string outputFormat = p.OutputFormat;

                // The game loads images through Unity's PNG/JPEG decoder, which has no webp path.
                if (outputFormat == "webp")
                {
                    OpenAIImagePlugin.Log.LogWarning(
                        "OpenAIImage: OutputFormat=webp cannot be decoded by the game — using png instead.");
                    outputFormat = "png";
                }

                bool transparent = wantTransparent && fam == ModelFamily.GptImage;

                switch (fam)
                {
                    case ModelFamily.GptImage:
                        // gpt-image-* always returns b64 and rejects response_format outright.
                        if (transparent)
                        {
                            body["background"] = "transparent";
                            // Transparency needs a lossless container; jpeg would flatten it.
                            if (outputFormat != "png") outputFormat = "png";
                        }
                        if (!string.IsNullOrEmpty(moderation))   body["moderation"]    = moderation;
                        if (!string.IsNullOrEmpty(outputFormat)) body["output_format"] = outputFormat;
                        if (!string.IsNullOrEmpty(quality))      body["quality"]       = quality;
                        break;

                    case ModelFamily.DallE3:
                    case ModelFamily.DallE2:
                        // DALL·E 400s on moderation/output_format, and needs response_format for b64.
                        body["response_format"] = "b64_json";
                        if (fam == ModelFamily.DallE3 && !string.IsNullOrEmpty(quality)) body["quality"] = quality;
                        break;

                    default:
                        // Third-party servers: request b64 (widely supported) plus whatever
                        // extras the user explicitly opted into via config.
                        body["response_format"] = "b64_json";
                        if (!string.IsNullOrEmpty(moderation))   body["moderation"]    = moderation;
                        if (!string.IsNullOrEmpty(outputFormat)) body["output_format"] = outputFormat;
                        if (!string.IsNullOrEmpty(quality))      body["quality"]       = quality;
                        break;
                }

                OpenAIImagePlugin.Log.LogInfo(
                    $"OpenAIImage: requesting {model} ({body["size"]}{(transparent ? ", transparent" : "")}) for {geArg.name}");

                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                    request.uploadHandler   = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 180; // image gen is slow; without this a stall hangs forever
                    request.SetRequestHeader("Content-Type", "application/json");
                    if (!string.IsNullOrEmpty(apiKey))
                        request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        OpenAIImagePlugin.Log.LogError(
                            $"OpenAIImage: API error ({request.responseCode}): {request.error}\n{Preview(request.downloadHandler.text)}");
                        return GameEntity.ImgGenState.REGULAR_FAILED;
                    }

                    JObject response = JObject.Parse(request.downloadHandler.text);
                    byte[] imageBytes = null;

                    var data = response["data"];
                    if (data != null && data.HasValues)
                    {
                        var first = data[0];
                        string b64 = first?["b64_json"]?.ToString();
                        if (!string.IsNullOrEmpty(b64))
                        {
                            imageBytes = Convert.FromBase64String(b64);
                        }
                        else
                        {
                            string imageUrl = first?["url"]?.ToString();
                            if (!string.IsNullOrEmpty(imageUrl))
                                imageBytes = await DownloadBytes(imageUrl);
                        }
                    }

                    if (imageBytes == null || imageBytes.Length == 0)
                    {
                        OpenAIImagePlugin.Log.LogError(
                            $"OpenAIImage: no image data in response: {Preview(request.downloadHandler.text)}");
                        return GameEntity.ImgGenState.REGULAR_FAILED;
                    }

                    string filePathNoExt = imgPathNoExtOverride ?? geArg.GetImgPathNoExt(imgGenInfo.imgType);
                    string fullPath = filePathNoExt + ".png";

                    string dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    File.WriteAllBytes(fullPath, imageBytes);
                    OpenAIImagePlugin.Log.LogInfo($"OpenAIImage: saved image for {geArg.name} at {fullPath}");
                    return GameEntity.ImgGenState.FINISHED;
                }
            }
            catch (Exception ex)
            {
                OpenAIImagePlugin.Log.LogError($"OpenAIImage: exception during image generation: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
        }

        /// <summary>
        /// Fetches raw bytes for a URL-style response. Deliberately uses DownloadHandlerBuffer
        /// rather than DownloadHandlerTexture — decoding a Texture2D and calling EncodeToPNG are
        /// main-thread-only Unity calls, and this runs on a background generation task.
        /// </summary>
        private static async Task<byte[]> DownloadBytes(string imageUrl)
        {
            using (UnityWebRequest imageRequest = UnityWebRequest.Get(imageUrl))
            {
                imageRequest.downloadHandler = new DownloadHandlerBuffer();
                imageRequest.timeout = 120;

                var imgOp = imageRequest.SendWebRequest();
                while (!imgOp.isDone) await Task.Yield();

                if (imageRequest.result != UnityWebRequest.Result.Success)
                {
                    OpenAIImagePlugin.Log.LogError($"OpenAIImage: failed to download image from URL: {imageRequest.error}");
                    return null;
                }
                return imageRequest.downloadHandler.data;
            }
        }

        private static string Preview(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s.Length > 800 ? s.Substring(0, 800) + "...[truncated]" : s;
        }
    }
}
