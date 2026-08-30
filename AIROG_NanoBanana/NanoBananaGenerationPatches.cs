using HarmonyLib;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_NanoBanana
{
    // Patches that intercept AIAsker's image/sprite generation entry points and redirect
    // them to NanoBananaImageClient when Gemini (mode 99) is the active image-gen backend.

    [HarmonyPatch(typeof(AIAsker), "getGeneratedImage")]
    public static class Patch_getGeneratedImage
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, ref Task __result)
        {
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)99)
            {
                __result = GenerateGeminiImageTask(entImgSettings, geArg);
                return false;
            }
            return true;
        }

        private static async Task GenerateGeminiImageTask(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg)
        {
            string prompt = entImgSettings.GetFormatted(await geArg.GetGenerateImagePrompt());
            GameEntity.ImgGenState state = await NanoBananaImageClient.GenerateGeminiImage(
                NanoBananaPlugin.Instance.GeminiApiKey, NanoBananaPlugin.Instance.GeminiModel, geArg, geArg.imgGenInfo, prompt);

            lock (geArg.imgGenInfo.imgGenLock)
            {
                geArg.imgGenInfo.imgGenState = state;
                if (state == GameEntity.ImgGenState.FINISHED)
                {
                    geArg.imgGenInfo.imgGenProgressAmount = 1f;
                    geArg.imgGenInfo.imageDirtyBit = true;
                }
            }
            Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.imgGenInfo);

            if (state == GameEntity.ImgGenState.REGULAR_FAILED)
            {
                NanoBananaPlugin.Log.LogWarning("NanoBanana: Image generation failed, but skipping exception to keep background task alive.");
                // throw new Exception("Gemini image generation failed.");
            }
        }
    }

    [HarmonyPatch(typeof(AIAsker), "getGeneratedSprite")]
    public static class Patch_getGeneratedSprite
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg, ref Task __result)
        {
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)99)
            {
                __result = GenerateGeminiSpriteTask(entImgSettings, geArg, removeBg);
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(ref Task __result, SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            // If the mode is NanoBanana (99), our Prefix handled it.
            // If it's Sapphire or AIRL_Free, they handle bg removal natively.
            // Mode 97 is AIROG_OpenAIImage, which does its own removal (or gets real alpha from
            // gpt-image-*) — running ours too just spawns a wasted ffmpeg pass per sprite.
            // For everyone else (Local, Wombo, etc), we need to do it manually if removeBg is requested.
            if (SS.I.imageGenerationMode != (SS.ImageGenerationMode)99 &&
                SS.I.imageGenerationMode != (SS.ImageGenerationMode)97 &&
                SS.I.imageGenerationMode != SS.ImageGenerationMode.SAPPHIRE &&
                SS.I.imageGenerationMode != SS.ImageGenerationMode.AIRL_FREE &&
                removeBg)
            {
                var originalTask = __result;
                __result = Task.Run(async () =>
                {
                    await originalTask;
                    await PerformManualBackgroundRemoval(geArg);
                });
            }
        }

        private static async Task PerformManualBackgroundRemoval(GameEntity geArg)
        {
            try
            {
                string filePathNoExt = geArg.GetImgPathNoExt(GameEntity.ImgType.SPRITE);
                string originalPath = filePathNoExt + ".png";
                string tempPath = filePathNoExt + "_transparent_pp.png";
                string toolsDir = SS.I.toolsDir;
                string ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");

                if (File.Exists(originalPath) && File.Exists(ffmpegPath))
                {
                    // Gemini/Imagen routinely ignore the "white background" instruction and return a
                    // solid colour of their choosing, so keying a hardcoded white removes nothing.
                    // Detect the background from the image corners and key *that* colour instead.
                    string bgColor = await DetectBackgroundColorHex(ffmpegPath, originalPath);
                    if (bgColor == null)
                    {
                        NanoBananaPlugin.Log.LogInfo($"[UniversalFix] {geArg.name} already transparent — skipping bg removal");
                        return;
                    }

                    string arguments = $"-y -i \"{originalPath}\" -vf \"colorkey={bgColor}:0.15:0.1\" \"{tempPath}\"";

                    await Utils.ExecuteCommandAsync(ffmpegPath, arguments);

                    if (File.Exists(tempPath))
                    {
                        File.Delete(originalPath);
                        File.Move(tempPath, originalPath);
                        NanoBananaPlugin.Log.LogInfo($"[UniversalFix] Removed {bgColor} background for {geArg.name}");

                        // Force refresh UI
                        Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.spGenInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                NanoBananaPlugin.Log.LogError($"[UniversalFix] Error removing background: {ex.Message}");
            }
        }

        /// <summary>
        /// Samples the four corners of an image (area-averaged) and returns the background colour as
        /// an ffmpeg-ready hex string like "0x3a3a3a". Returns null if the corners are already
        /// transparent (nothing to remove), or "white" if detection fails (safe legacy fallback).
        /// Uses ffmpeg only — no Unity texture calls — so it is safe on this background thread.
        /// </summary>
        private static async Task<string> DetectBackgroundColorHex(string ffmpegPath, string imagePath)
        {
            string rawPath = imagePath + ".corners.raw";
            try
            {
                // Crop each corner to 1px (area-averaged), stack them, dump as raw RGBA (4px = 16 bytes).
                string fc =
                    "[0:v]crop=iw/8:ih/8:0:0,scale=1:1:flags=area[a];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:0,scale=1:1:flags=area[b];" +
                    "[0:v]crop=iw/8:ih/8:0:ih*7/8,scale=1:1:flags=area[c];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:ih*7/8,scale=1:1:flags=area[d];" +
                    "[a][b][c][d]hstack=inputs=4,format=rgba";
                string args = $"-y -i \"{imagePath}\" -filter_complex \"{fc}\" -f rawvideo -pix_fmt rgba \"{rawPath}\"";

                await Utils.ExecuteCommandAsync(ffmpegPath, args);

                if (!File.Exists(rawPath)) return "white";
                byte[] b = File.ReadAllBytes(rawPath);
                if (b.Length < 16) return "white";

                int r = 0, g = 0, bl = 0, a = 0;
                for (int p = 0; p < 4; p++) { r += b[p * 4]; g += b[p * 4 + 1]; bl += b[p * 4 + 2]; a += b[p * 4 + 3]; }
                if (a / 4 < 32) return null; // corners already transparent — image needs no keying

                return $"0x{r / 4:X2}{g / 4:X2}{bl / 4:X2}";
            }
            catch (Exception ex)
            {
                NanoBananaPlugin.Log.LogWarning($"[UniversalFix] bg colour detect failed, defaulting to white: {ex.Message}");
                return "white";
            }
            finally
            {
                try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
            }
        }

        private static async Task GenerateGeminiSpriteTask(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            string prompt = entImgSettings.GetFormatted(await geArg.GetGenerateImagePrompt());
            // Gemini doesn't remove backgrounds yet, so we just ask for a white background
            if (removeBg) prompt += ", white background, isolated, high quality sprite";

            GameEntity.ImgGenState state = await NanoBananaImageClient.GenerateGeminiImage(
                NanoBananaPlugin.Instance.GeminiApiKey, NanoBananaPlugin.Instance.GeminiModel, geArg, geArg.spGenInfo, prompt);

            if (state == GameEntity.ImgGenState.FINISHED && removeBg)
            {
                await PerformManualBackgroundRemoval(geArg);
            }

            lock (geArg.spGenInfo.imgGenLock)
            {
                geArg.spGenInfo.imgGenState = state;
                if (state == GameEntity.ImgGenState.FINISHED)
                {
                    geArg.spGenInfo.imgGenProgressAmount = 1f;
                    geArg.spGenInfo.imageDirtyBit = true;
                }
            }
            Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.spGenInfo);

            if (state == GameEntity.ImgGenState.REGULAR_FAILED)
            {
                NanoBananaPlugin.Log.LogWarning("NanoBanana: Sprite generation failed, but skipping exception to keep background task alive.");
            }
        }
    }
}
