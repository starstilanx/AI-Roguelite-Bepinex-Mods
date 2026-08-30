using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Image generation through the Gemini image models on Vertex. Imagen is not used:
    /// every imagen-* model is deprecated and shutting down from 2026-08-17, and Google's
    /// own guidance is to generate images with the Gemini image models via generateContent.
    /// </summary>
    public static class VertexImageClient
    {
        /// <summary>Aspect ratios generateContent accepts; anything else is silently rewritten to 1:1.</summary>
        private static readonly (string label, double ratio)[] SUPPORTED_RATIOS =
        {
            ("1:8", 0.125), ("1:4", 0.25), ("9:16", 0.5625), ("2:3", 0.6667), ("3:4", 0.75),
            ("4:5", 0.8), ("1:1", 1.0), ("5:4", 1.25), ("4:3", 1.3333), ("3:2", 1.5),
            ("16:9", 1.7778), ("21:9", 2.3333), ("4:1", 4.0), ("8:1", 8.0),
        };

        /// <summary>
        /// Generates one image and writes it to the entity's image path.
        /// <paramref name="widthOverHeight"/> is the game's own preferred aspect ratio for
        /// this entity — the same value it hands the official backends — snapped to the
        /// nearest ratio Gemini accepts.
        /// <paramref name="imgPathNoExtOverride"/> mirrors the game's own clients: when the
        /// caller wants a new variant rather than a replacement, it supplies the path.
        /// Returns the state to store on the entity's ImgGenInfo.
        /// </summary>
        public static async Task<GameEntity.ImgGenState> GenerateImage(
            GameEntity entity, GameEntity.ImgGenInfo imgGenInfo, float widthOverHeight,
            string prompt, CancellationToken ct, string imgPathNoExtOverride = null)
        {
            try
            {
                string model = VertexAIPlugin.CachedImageModel;
                string size = VertexAIPlugin.CachedImageSize;
                string aspectRatio = PickAspectRatio(widthOverHeight);

                var imageConfig = new JObject { ["aspectRatio"] = aspectRatio };
                // imageSize defaults to 1K when omitted, so only send it when chosen.
                if (!string.IsNullOrEmpty(size)) imageConfig["imageSize"] = size;

                var body = new JObject
                {
                    ["contents"] = new JArray
                    {
                        new JObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JArray { new JObject { ["text"] = prompt } },
                        },
                    },
                    ["generationConfig"] = new JObject
                    {
                        ["responseModalities"] = new JArray { "IMAGE" },
                        ["imageConfig"] = imageConfig,
                    },
                };

                JArray safety = VertexApiClient.BuildSafetySettings();
                if (safety != null) body["safetySettings"] = safety;

                VertexAIPlugin.Log.LogInfo($"[VertexAI] image gen: model={model} size={(string.IsNullOrEmpty(size) ? "default" : size)} " +
                                           $"ratio={aspectRatio} for {entity.name}");

                JObject response = await VertexApiClient.PostGenerateContent(model, body, ct);
                byte[] bytes = VertexApiClient.ExtractImageBytes(response, out string failureReason);
                if (bytes == null)
                {
                    VertexAIPlugin.Log.LogError($"[VertexAI] image gen failed for {entity.name}: {failureReason}");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }

                string path = (imgPathNoExtOverride ?? entity.GetImgPathNoExt(imgGenInfo.imgType)) + ".png";
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);

                VertexAIPlugin.Log.LogInfo($"[VertexAI] wrote {bytes.Length} bytes to {path}");
                return GameEntity.ImgGenState.FINISHED;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] image gen threw for {entity?.name}: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
        }

        /// <summary>
        /// Snaps a width/height ratio to the nearest value generateContent accepts.
        /// Ratios outside its whitelist are silently rewritten to 1:1 by the API, so
        /// picking the closest legal one preserves the game's framing intent.
        /// </summary>
        public static string PickAspectRatio(float widthOverHeight)
        {
            double target = widthOverHeight;
            if (double.IsNaN(target) || double.IsInfinity(target) || target <= 0) target = 1.0;

            string best = "1:1";
            double bestDistance = double.MaxValue;
            foreach ((string label, double ratio) in SUPPORTED_RATIOS)
            {
                // Compare in log space so 2:1 and 1:2 are treated as equally far from 1:1.
                double distance = Math.Abs(Math.Log(target) - Math.Log(ratio));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = label;
                }
            }
            return best;
        }

        /// <summary>
        /// Keys out a flat background. Gemini has no transparency support, so the prompt
        /// asks for a plain backdrop and ffmpeg removes whatever colour actually came
        /// back — the model routinely ignores "white background" and picks its own.
        /// No-ops when the corners are already transparent or ffmpeg is missing.
        /// </summary>
        public static async Task RemoveFlatBackground(GameEntity entity, GameEntity.ImgGenInfo info, string pathNoExtOverride = null)
        {
            string pathNoExt = pathNoExtOverride ?? entity.GetImgPathNoExt(info.imgType);
            string imagePath = pathNoExt + ".png";
            string tempPath = pathNoExt + "_keyed.png";
            string ffmpegPath = Path.Combine(SS.I.toolsDir, "ffmpeg.exe");

            try
            {
                if (!File.Exists(imagePath) || !File.Exists(ffmpegPath)) return;

                string bgColor = await DetectCornerColor(ffmpegPath, imagePath);
                if (bgColor == null)
                {
                    VertexAIPlugin.Log.LogInfo($"[VertexAI] {entity.name} sprite is already transparent — no keying needed.");
                    return;
                }

                await Utils.ExecuteCommandAsync(ffmpegPath,
                    $"-y -i \"{imagePath}\" -vf \"colorkey={bgColor}:0.15:0.1\" \"{tempPath}\"");

                if (File.Exists(tempPath))
                {
                    File.Delete(imagePath);
                    File.Move(tempPath, imagePath);
                    VertexAIPlugin.Log.LogInfo($"[VertexAI] keyed out {bgColor} background for {entity.name}.");
                    Utils.MarkEntityAsNeedingImgUpdate(entity.uuid, info);
                }
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] background removal failed for {entity?.name}: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Area-averages the four corners and returns them as an ffmpeg hex colour, or
        /// null when they are already transparent. Uses ffmpeg rather than Unity textures
        /// so it is safe to call from a background image-generation task.
        /// </summary>
        private static async Task<string> DetectCornerColor(string ffmpegPath, string imagePath)
        {
            string rawPath = imagePath + ".corners.raw";
            try
            {
                string filter =
                    "[0:v]crop=iw/8:ih/8:0:0,scale=1:1:flags=area[a];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:0,scale=1:1:flags=area[b];" +
                    "[0:v]crop=iw/8:ih/8:0:ih*7/8,scale=1:1:flags=area[c];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:ih*7/8,scale=1:1:flags=area[d];" +
                    "[a][b][c][d]hstack=inputs=4,format=rgba";

                await Utils.ExecuteCommandAsync(ffmpegPath,
                    $"-y -i \"{imagePath}\" -filter_complex \"{filter}\" -f rawvideo -pix_fmt rgba \"{rawPath}\"");

                if (!File.Exists(rawPath)) return "white";
                byte[] raw = File.ReadAllBytes(rawPath);
                if (raw.Length < 16) return "white";

                int r = 0, g = 0, b = 0, a = 0;
                for (int p = 0; p < 4; p++)
                {
                    r += raw[p * 4];
                    g += raw[p * 4 + 1];
                    b += raw[p * 4 + 2];
                    a += raw[p * 4 + 3];
                }
                if (a / 4 < 32) return null;

                return $"0x{r / 4:X2}{g / 4:X2}{b / 4:X2}";
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogWarning($"[VertexAI] corner colour detection failed, assuming white: {ex.Message}");
                return "white";
            }
            finally
            {
                try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
            }
        }
    }
}
