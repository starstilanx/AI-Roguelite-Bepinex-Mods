using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace AIROG_OpenAIImage
{
    [BepInPlugin("com.airog.openaiimage", "OpenAI Image", "1.0.0")]
    public class OpenAIImagePlugin : BaseUnityPlugin
    {
        public static OpenAIImagePlugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        
        public const string PREF_KEY_OPENAI_API_KEY = "PREF_KEY_OPENAI_IMG_GEN_API_KEY";

        public string OpenAIApiKey => PlayerPrefs.HasKey(PREF_KEY_OPENAI_API_KEY) 
            ? PlayerPrefs.GetString(PREF_KEY_OPENAI_API_KEY) 
            : Config.Bind("General", "OpenAIApiKey", "", "API Key for OpenAI Image Generation").Value;
            
        public string OpenAIBaseUrl => Config.Bind("General", "OpenAIBaseUrl", "https://api.openai.com/v1", "Base URL for OpenAI Image Generation API").Value;
            
        public string OpenAIModel => Config.Bind("General", "OpenAIModel", "dall-e-3", "Model ID to use for OpenAI Image Generation").Value;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            var harmony = new Harmony("com.airog.openaiimage");
            harmony.PatchAll();
            Logger.LogInfo("OpenAIImage loaded! Ready to generate some images.");
        }

        public async Task<GameEntity.ImgGenState> GenerateOpenAIImage(GameEntity geArg, GameEntity.ImgGenInfo imgGenInfo, string prompt)
        {
            try
            {
                string apiKey = OpenAIApiKey;
                string baseUrl = OpenAIBaseUrl;
                string model = OpenAIModel;
                
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://api.openai.com/v1";

                string url = baseUrl;
                if (!url.EndsWith("/")) url += "/";
                url += "images/generations";

                JObject body = new JObject();
                body["model"] = model;
                body["prompt"] = prompt;
                body["n"] = 1;
                body["size"] = "1024x1024";
                body["response_format"] = "b64_json"; // Request base64 directly to avoid secondary download

                Logger.LogInfo($"OpenAIImage: Sending request to {url} for {geArg.name} with model {model}");

                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                    }

                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Logger.LogError($"OpenAIImage: API Error: {request.error}\n{request.downloadHandler.text}");
                        return GameEntity.ImgGenState.REGULAR_FAILED;
                    }

                    JObject response = JObject.Parse(request.downloadHandler.text);
                    var data = response["data"];
                    if (data != null && data.HasValues)
                    {
                        var firstResult = data[0];
                        if (firstResult != null)
                        {
                            byte[] imageBytes = null;
                            if (firstResult["b64_json"] != null)
                            {
                                string base64Data = firstResult["b64_json"].ToString();
                                imageBytes = Convert.FromBase64String(base64Data);
                            }
                            else if (firstResult["url"] != null)
                            {
                                string imageUrl = firstResult["url"].ToString();
                                using (UnityWebRequest imageRequest = UnityWebRequestTexture.GetTexture(imageUrl))
                                {
                                    var imgOp = imageRequest.SendWebRequest();
                                    while (!imgOp.isDone) await Task.Yield();
                                    if (imageRequest.result == UnityWebRequest.Result.Success)
                                    {
                                        var tex = DownloadHandlerTexture.GetContent(imageRequest);
                                        imageBytes = tex.EncodeToPNG();
                                    }
                                    else
                                    {
                                        Logger.LogError($"OpenAIImage: Failed to download image from URL: {imageRequest.error}");
                                    }
                                }
                            }

                            if (imageBytes != null && imageBytes.Length > 0)
                            {
                                string filePathNoExt = geArg.GetImgPathNoExt(imgGenInfo.imgType);
                                string fullPath = filePathNoExt + ".png";
                                
                                string dir = Path.GetDirectoryName(fullPath);
                                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                                File.WriteAllBytes(fullPath, imageBytes);
                                Logger.LogInfo($"OpenAIImage: Successfully generated and saved image for {geArg.name} at {fullPath}");
                                return GameEntity.ImgGenState.FINISHED;
                            }
                        }
                    }

                    Logger.LogError($"OpenAIImage: No image data found in response: {request.downloadHandler.text}");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"OpenAIImage: Exception during image generation: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "Options")]
    public static class Patch_MainMenu_Options
    {
        public static void Postfix(MainMenu __instance)
        {
            List<TMP_Dropdown.OptionData> options = __instance.imageGenerationDropdown.options;
            if (!options.Any(o => o.text == "OpenAI Consistent"))
            {
                options.Add(new TMP_Dropdown.OptionData("OpenAI Consistent"));
            }

            int savedMode = PlayerPrefs.GetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8);
            if (savedMode == 98)
            {
                int openAIIndex = options.FindIndex(o => o.text == "OpenAI Consistent");
                if (openAIIndex != -1)
                {
                    __instance.imageGenerationDropdown.SetValueWithoutNotify(openAIIndex);
                }
                
                string openAIKey = PlayerPrefs.GetString(OpenAIImagePlugin.PREF_KEY_OPENAI_API_KEY, OpenAIImagePlugin.Instance.OpenAIApiKey);
                __instance.customerKeyTxtInputForImgGen.SetTextWithoutNotify(openAIKey);
                
                __instance.customerKeyTxtInput.SetTextWithoutNotify(PlayerPrefs.GetString("PREF_KEY_CUSTOMER_KEY2"));
                
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "SaveCurrentPrefs")]
    public static class Patch_SaveCurrentPrefs
    {
        public static void Postfix(MainMenu __instance)
        {
            int ind = __instance.imageGenerationDropdown.value;
            bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count && 
                            __instance.imageGenerationDropdown.options[ind].text == "OpenAI Consistent";
            
            if (isOpenAI)
            {
                string val = __instance.customerKeyTxtInputForImgGen.text;
                PlayerPrefs.SetString(OpenAIImagePlugin.PREF_KEY_OPENAI_API_KEY, val);
                PlayerPrefs.Save();
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "GetImageGenerationModeByDropdownInd")]
    public static class Patch_GetImageGenerationModeByDropdownInd
    {
        [HarmonyPrefix]
        public static bool Prefix(int ind, MainMenu __instance, ref SS.ImageGenerationMode __result)
        {
            if (ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count)
            {
                if (__instance.imageGenerationDropdown.options[ind].text == "OpenAI Consistent")
                {
                    __result = (SS.ImageGenerationMode)98;
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenerationDropdownChanged")]
    public static class Patch_OnImageGenerationDropdownChanged
    {
        private static string _originalKeyLabel = null;

        public static void Postfix(MainMenu __instance)
        {
            int ind = __instance.imageGenerationDropdown.value;
            bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count && 
                            __instance.imageGenerationDropdown.options[ind].text == "OpenAI Consistent";

            var labelComponent = __instance.customerKeyTxtInputForImgGenTrans.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(t => t.name.ToLower().Contains("label") || t.text.ToLower().Contains("key") || t.text.ToLower().Contains("customer"));

            if (isOpenAI)
            {
                if (_originalKeyLabel == null && labelComponent != null) _originalKeyLabel = labelComponent.text;

                __instance.imgGenExplanation.SetText("Generates images using an OpenAI-compatible API. \n\n<color=#00FF00>Note:</color> Enter your API Key below. Base URL and Model can be configured in BepInEx.", true);
                
                __instance.customerKeyTxtInputForImgGenTrans.gameObject.SetActive(true);
                if (labelComponent != null) labelComponent.text = "OpenAI API Key";

                string currentKey = PlayerPrefs.GetString(OpenAIImagePlugin.PREF_KEY_OPENAI_API_KEY, OpenAIImagePlugin.Instance.OpenAIApiKey);
                __instance.customerKeyTxtInputForImgGen.SetTextWithoutNotify(currentKey);

                __instance.womboStyleHolder.gameObject.SetActive(false);
                __instance.naiModelTransform.gameObject.SetActive(false);
                __instance.stableHordeKeyTransform.gameObject.SetActive(false);
                
                __instance.imgGenTweakHolder.gameObject.SetActive(true);
                __instance.exportImportImgGenSettingsTrans.gameObject.SetActive(true);
                
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }
            else
            {
                if (_originalKeyLabel != null && labelComponent != null)
                {
                    labelComponent.text = _originalKeyLabel;
                }

                var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    SS.ImageGenerationMode selectedMode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { ind });
                    if (selectedMode == SS.ImageGenerationMode.SAPPHIRE)
                    {
                        __instance.customerKeyTxtInputForImgGen.SetTextWithoutNotify(PlayerPrefs.GetString("PREF_KEY_CUSTOMER_KEY2"));
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateSsPrefsWithPlayerPrefs")]
    public static class Patch_PopulateSsPrefsWithPlayerPrefs
    {
        public static void Postfix()
        {
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)98)
            {
                if (SS.I.settingsPojo == null)
                {
                    SS.I.settingsPojo = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
                }
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateImageGenInputFieldsWithPlayerPrefs")]
    public static class Patch_PopulateImageGenInputFieldsWithPlayerPrefs
    {
        public static bool Prefix(SS.ImageGenerationMode generationMode)
        {
            if (generationMode == (SS.ImageGenerationMode)98)
            {
                return false; 
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(AIAsker), "getGeneratedImage")]
    public static class Patch_getGeneratedImage
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, ref Task __result)
        {
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)98)
            {
                __result = GenerateOpenAIImageTask(entImgSettings, geArg);
                return false;
            }
            return true;
        }

        private static async Task GenerateOpenAIImageTask(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg)
        {
            string prompt = entImgSettings.GetFormatted(await geArg.GetGenerateImagePrompt());
            GameEntity.ImgGenState state = await OpenAIImagePlugin.Instance.GenerateOpenAIImage(geArg, geArg.imgGenInfo, prompt);
            
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
                OpenAIImagePlugin.Log.LogWarning("OpenAIImage: Image generation failed, but skipping exception to keep background task alive.");
            }
        }
    }

    [HarmonyPatch(typeof(AIAsker), "getGeneratedSprite")]
    public static class Patch_getGeneratedSprite
    {
        [HarmonyPrefix]
        public static bool Prefix(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg, ref Task __result)
        {
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)98)
            {
                __result = GenerateOpenAISpriteTask(entImgSettings, geArg, removeBg);
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(ref Task __result, SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            if (SS.I.imageGenerationMode != (SS.ImageGenerationMode)98 && 
                SS.I.imageGenerationMode != (SS.ImageGenerationMode)99 && 
                SS.I.imageGenerationMode != SS.ImageGenerationMode.SAPPHIRE && 
                SS.I.imageGenerationMode != SS.ImageGenerationMode.AIRL_FREE &&
                removeBg)
            {
                // Rely on existing logic or other plugins for manual removal hook. We won't hook it for others to easily avoid conflicts.
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
                    string arguments = $"-y -i \"{originalPath}\" -vf \"colorkey=white:0.1:0.2\" \"{tempPath}\"";
                    await Utils.ExecuteCommandAsync(ffmpegPath, arguments);

                    if (File.Exists(tempPath))
                    {
                        File.Delete(originalPath);
                        File.Move(tempPath, originalPath);
                        OpenAIImagePlugin.Log.LogInfo($"[OpenAIImage] Removed background for {geArg.name}");
                        Utils.MarkEntityAsNeedingImgUpdate(geArg.uuid, geArg.spGenInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                OpenAIImagePlugin.Log.LogError($"[OpenAIImage] Error removing background: {ex.Message}");
            }
        }

        private static async Task GenerateOpenAISpriteTask(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            string prompt = entImgSettings.GetFormatted(await geArg.GetGenerateImagePrompt());
            if (removeBg) prompt += ", white background, isolated, high quality sprite";

            GameEntity.ImgGenState state = await OpenAIImagePlugin.Instance.GenerateOpenAIImage(geArg, geArg.spGenInfo, prompt);
            
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
                OpenAIImagePlugin.Log.LogWarning("OpenAIImage: Sprite generation failed, but skipping exception to keep background task alive.");
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "GetSettingsPojoByInd")]
    public static class Patch_GetSettingsPojoByInd
    {
        [HarmonyPrefix]
        public static bool Prefix(int ind, SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode == (SS.ImageGenerationMode)98)
            {
                __result = SS.I.defaultWomboSettings; 
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "GetDefaultSettingsPojoForImageGenMode")]
    public static class Patch_GetDefaultSettingsPojoForImageGenMode
    {
        [HarmonyPrefix]
        public static bool Prefix(SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode == (SS.ImageGenerationMode)98)
            {
                __result = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateImageGenPresetDropdown")]
    public static class Patch_PopulateImageGenPresetDropdown
    {
        [HarmonyPrefix]
        public static bool Prefix(MainMenu __instance)
        {
            var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return true;

            int dropdownVal = __instance.imageGenerationDropdown.value;
            SS.ImageGenerationMode mode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { dropdownVal });

            if (mode == (SS.ImageGenerationMode)98)
            {
                __instance.imgGenPresetDropdown.ClearOptions();
                __instance.imgGenPresetDropdown.AddOptions(new List<string> { "Default" });
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnCustomerKeyTxtInputChanged")]
    public static class Patch_OnCustomerKeyTxtInputChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(string s, MainMenu __instance)
        {
            int ind = __instance.imageGenerationDropdown.value;
            bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count && 
                            __instance.imageGenerationDropdown.options[ind].text == "OpenAI Consistent";

            if (isOpenAI)
            {
                GameObject selected = EventSystem.current?.currentSelectedGameObject;
                if (selected != null && selected == __instance.customerKeyTxtInputForImgGen.gameObject)
                {
                    return false; 
                }

                if (__instance.customerKeyTxtInput != null && __instance.customerKeyTxtInput.gameObject == selected)
                    __instance.customerKeyTxtInputForAudioGen.SetTextWithoutNotify(s);
                if (__instance.customerKeyTxtInputForAudioGen != null && __instance.customerKeyTxtInputForAudioGen.gameObject == selected)
                    __instance.customerKeyTxtInput.SetTextWithoutNotify(s);
                
                return false;
            }
            return true;
        }
    }
}
