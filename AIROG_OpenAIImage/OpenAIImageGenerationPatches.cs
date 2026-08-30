using HarmonyLib;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_OpenAIImage
{
    // Intercepts AIAsker's image/sprite entry points and routes them to OpenAIImageClient when
    // our mode is active. Deliberately does NOT touch other backends' generation — AIROG_NanoBanana
    // already installs a universal background-removal postfix, and a second one would run ffmpeg twice.

    [HarmonyPatch(typeof(AIAsker), "getGeneratedImage")]
    public static class Patch_getGeneratedImage
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg,
                                  bool useNewImgFileList, ref Task __result)
        {
            if (SS.I.imageGenerationMode != OpenAIImagePlugin.OPENAI_IMG_MODE) return true;
            __result = GenerateImageTask(entImgSettings, geArg, useNewImgFileList);
            return false;
        }

        private static async Task GenerateImageTask(SettingsPojo.EntImgSettings entImgSettings,
                                                    GameEntity geArg, bool useNewImgFileList)
        {
            // Two-arg GetFormatted: the second arg carries the scenario's image prompt format,
            // which the original AIAsker path applies and a one-arg call would silently drop.
            string prompt = entImgSettings.GetFormatted(
                await geArg.GetGenerateImagePrompt(), geArg.Mm?.GetScenarioState()?.imagePromptFmt);

            // "Regenerate" variants are written under a fresh uuid and appended to imgFileNames,
            // rather than overwriting the entity's current image.
            string imgUuid = null;
            string pathOverride = null;
            if (useNewImgFileList)
            {
                imgUuid = Guid.NewGuid().ToString();
                pathOverride = Path.Combine(SS.I.saveSubDirAsArg, imgUuid);
            }

            GameEntity.ImgGenState state = await OpenAIImageClient.GenerateImage(
                geArg, geArg.imgGenInfo, prompt, wantTransparent: false, imgPathNoExtOverride: pathOverride);

            lock (geArg.imgGenInfo.imgGenLock)
            {
                geArg.imgGenInfo.imgGenState = state;
                if (state == GameEntity.ImgGenState.FINISHED)
                {
                    geArg.imgGenInfo.imgGenProgressAmount = 1f;
                    geArg.imgGenInfo.SetImgDirty(true);
                    if (imgUuid != null) geArg.imgFileNames.Add(imgUuid);
                }
            }
            Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.imgGenInfo);

            if (state == GameEntity.ImgGenState.REGULAR_FAILED)
                OpenAIImagePlugin.Log.LogWarning("OpenAIImage: image generation failed; not throwing, to keep the background task alive.");
        }
    }

    [HarmonyPatch(typeof(AIAsker), "getGeneratedSprite")]
    public static class Patch_getGeneratedSprite
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg,
                                  bool removeBg, ref Task __result)
        {
            if (SS.I.imageGenerationMode != OpenAIImagePlugin.OPENAI_IMG_MODE) return true;
            __result = GenerateSpriteTask(entImgSettings, geArg, removeBg);
            return false;
        }

        private static async Task GenerateSpriteTask(SettingsPojo.EntImgSettings entImgSettings,
                                                     GameEntity geArg, bool removeBg)
        {
            string prompt = entImgSettings.GetFormatted(
                await geArg.GetGenerateImagePrompt(), geArg.Mm?.GetScenarioState()?.imagePromptFmt);

            // gpt-image-* can return a real alpha channel, which beats keying a flat colour after
            // the fact. Everything else gets asked for a plain background and keyed with ffmpeg.
            bool nativeTransparency = removeBg && OpenAIImageClient.SupportsTransparency(OpenAIImagePlugin.Instance.ActiveModel);
            if (removeBg && !nativeTransparency)
                prompt += ", plain flat solid-colour background, isolated subject, high quality sprite";

            GameEntity.ImgGenState state = await OpenAIImageClient.GenerateImage(
                geArg, geArg.spGenInfo, prompt, wantTransparent: nativeTransparency);

            if (state == GameEntity.ImgGenState.FINISHED && removeBg && !nativeTransparency)
                await RemoveBackground(geArg);

            lock (geArg.spGenInfo.imgGenLock)
            {
                geArg.spGenInfo.imgGenState = state;
                if (state == GameEntity.ImgGenState.FINISHED)
                {
                    geArg.spGenInfo.imgGenProgressAmount = 1f;
                    geArg.spGenInfo.SetImgDirty(true);
                }
            }
            Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.spGenInfo);

            if (state == GameEntity.ImgGenState.REGULAR_FAILED)
                OpenAIImagePlugin.Log.LogWarning("OpenAIImage: sprite generation failed; not throwing, to keep the background task alive.");
        }

        private static async Task RemoveBackground(GameEntity geArg)
        {
            try
            {
                string filePathNoExt = geArg.GetImgPathNoExt(GameEntity.ImgType.SPRITE);
                string originalPath = filePathNoExt + ".png";
                string tempPath = filePathNoExt + "_transparent_pp.png";
                string ffmpegPath = Path.Combine(SS.I.toolsDir, "ffmpeg.exe");

                if (!File.Exists(originalPath) || !File.Exists(ffmpegPath)) return;

                // Models routinely ignore "white background" and pick their own colour, so keying a
                // hardcoded white removes nothing. Detect the real background from the corners.
                string bgColor = await DetectBackgroundColorHex(ffmpegPath, originalPath);
                if (bgColor == null)
                {
                    OpenAIImagePlugin.Log.LogInfo($"[OpenAIImage] {geArg.name} already transparent — skipping bg removal");
                    return;
                }

                await Utils.ExecuteCommandAsync(ffmpegPath,
                    $"-y -i \"{originalPath}\" -vf \"colorkey={bgColor}:0.15:0.1\" \"{tempPath}\"");

                if (File.Exists(tempPath))
                {
                    File.Delete(originalPath);
                    File.Move(tempPath, originalPath);
                    OpenAIImagePlugin.Log.LogInfo($"[OpenAIImage] Removed {bgColor} background for {geArg.name}");
                    Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.spGenInfo);
                }
            }
            catch (Exception ex)
            {
                OpenAIImagePlugin.Log.LogError($"[OpenAIImage] Error removing background: {ex.Message}");
            }
        }

        /// <summary>
        /// Area-averages the four corners and returns an ffmpeg-ready hex like "0x3a3a3a".
        /// Returns null when the corners are already transparent (nothing to key), or "white" if
        /// detection fails. ffmpeg only — no Unity texture calls — so it is safe off the main thread.
        /// </summary>
        private static async Task<string> DetectBackgroundColorHex(string ffmpegPath, string imagePath)
        {
            string rawPath = imagePath + ".corners.raw";
            try
            {
                string fc =
                    "[0:v]crop=iw/8:ih/8:0:0,scale=1:1:flags=area[a];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:0,scale=1:1:flags=area[b];" +
                    "[0:v]crop=iw/8:ih/8:0:ih*7/8,scale=1:1:flags=area[c];" +
                    "[0:v]crop=iw/8:ih/8:iw*7/8:ih*7/8,scale=1:1:flags=area[d];" +
                    "[a][b][c][d]hstack=inputs=4,format=rgba";

                await Utils.ExecuteCommandAsync(ffmpegPath,
                    $"-y -i \"{imagePath}\" -filter_complex \"{fc}\" -f rawvideo -pix_fmt rgba \"{rawPath}\"");

                if (!File.Exists(rawPath)) return "white";
                byte[] b = File.ReadAllBytes(rawPath);
                if (b.Length < 16) return "white";

                int r = 0, g = 0, bl = 0, a = 0;
                for (int p = 0; p < 4; p++) { r += b[p * 4]; g += b[p * 4 + 1]; bl += b[p * 4 + 2]; a += b[p * 4 + 3]; }
                if (a / 4 < 32) return null; // corners already transparent

                return $"0x{r / 4:X2}{g / 4:X2}{bl / 4:X2}";
            }
            catch (Exception ex)
            {
                OpenAIImagePlugin.Log.LogWarning($"[OpenAIImage] bg colour detect failed, defaulting to white: {ex.Message}");
                return "white";
            }
            finally
            {
                try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
            }
        }
    }
}
