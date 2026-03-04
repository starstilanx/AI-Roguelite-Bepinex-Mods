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

namespace AIROG_NanoBanana
{
    [BepInPlugin("com.airog.nanobanana", "NanoBanana", "1.0.0")]
    public class NanoBananaPlugin : BaseUnityPlugin
    {
        public static NanoBananaPlugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        
        public const string PREF_KEY_GEMINI_API_KEY = "PREF_KEY_GEMINI_IMG_GEN_API_KEY";
        public const string PREF_KEY_GEMINI_MODEL = "PREF_KEY_GEMINI_IMG_GEN_MODEL";

        // Configuration for the API Key and Model
        // Modified to check PlayerPrefs first
        public string GeminiApiKey => PlayerPrefs.HasKey(PREF_KEY_GEMINI_API_KEY) 
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_API_KEY) 
            : Config.Bind("General", "GeminiApiKey", "", "API Key for Gemini Image Generation").Value;
        public string GeminiModel => PlayerPrefs.HasKey(PREF_KEY_GEMINI_MODEL) 
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_MODEL) 
            : Config.Bind("General", "GeminiModel", "gemini-2.5-flash-image", "Model ID to use for Gemini Image Generation").Value;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            var harmony = new Harmony("com.maxloh.nanobanana");
            harmony.PatchAll();
            Logger.LogInfo("NanoBanana loaded! Ready to generate some nano bananas.");
        }

        /// <summary>
        /// Core image generation logic using Gemini API.
        /// </summary>
        public async Task<GameEntity.ImgGenState> GenerateGeminiImage(GameEntity geArg, GameEntity.ImgGenInfo imgGenInfo, string prompt)
        {
            try
            {
                string apiKey = GeminiApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    Logger.LogError("NanoBanana: Gemini API Key is missing! Please set it in the options menu or BepInEx config.");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }

                // Construct the URL (API key is passed as a header, not query param)
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent";

                // Build the request payload following official docs:
                // https://ai.google.dev/gemini-api/docs/image-generation
                JObject body = new JObject();
                
                // Add Generation Config with Image Modality
                // Must include both TEXT and IMAGE for image generation models
                body["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray { "TEXT", "IMAGE" }
                };
                // Note: safetySettings with BLOCK_NONE are NOT included here because
                // Gemini 3 Pro Image Preview rejects requests that use that threshold.
                // The image generation models have their own default safety filtering.

                JArray contents = new JArray();
                JObject content = new JObject { ["role"] = "user" };
                JArray parts = new JArray();
                parts.Add(new JObject { ["text"] = prompt });
                content["parts"] = parts;
                contents.Add(content);
                body["contents"] = contents;

                Logger.LogInfo($"NanoBanana: Sending request to Gemini ({GeminiModel}) for {geArg.name}");

                using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 120; // 120 second timeout (Gemini 3 Pro thinking can be slow)
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
                        Logger.LogError($"NanoBanana: Gemini API Error ({request.responseCode}): {request.error}\n{errBody}");
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
                                // Skip "thought" parts from Gemini 3 Pro's thinking mode
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
                                        Logger.LogInfo($"NanoBanana: Successfully generated and saved image for {geArg.name} at {fullPath}");
                                        return GameEntity.ImgGenState.FINISHED;
                                    }
                                }
                            }
                        }
                    }

                    // Log only the first 800 chars of the response for diagnosis
                    string respPreview = request.downloadHandler.text;
                    if (respPreview.Length > 800) respPreview = respPreview.Substring(0, 800) + "...[truncated]";
                    Logger.LogError($"NanoBanana: No image data found in Gemini response: {respPreview}");
                    return GameEntity.ImgGenState.REGULAR_FAILED;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"NanoBanana: Exception during image generation: {ex.Message}\n{ex.StackTrace}");
                return GameEntity.ImgGenState.REGULAR_FAILED;
            }
        }
    }

    // --- Harmony Patches ---

    [HarmonyPatch(typeof(MainMenu), "Options")]
    public static class Patch_MainMenu_Options
    {
        public static void Postfix(MainMenu __instance)
        {
            // Inject "Gemini (Nano Banana)" into the image generation dropdown options
            List<TMP_Dropdown.OptionData> options = __instance.imageGenerationDropdown.options;
            if (!options.Any(o => o.text == "Gemini (Nano Banana)"))
            {
                options.Add(new TMP_Dropdown.OptionData("Gemini (Nano Banana)"));
            }

            // Sync the dropdown value from prefs
            int savedMode = PlayerPrefs.GetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8);
            if (savedMode == 99)
            {
                int geminiIndex = options.FindIndex(o => o.text == "Gemini (Nano Banana)");
                if (geminiIndex != -1)
                {
                    // Use SetValueWithoutNotify to set the index, then manually fire our UI update
                    __instance.imageGenerationDropdown.SetValueWithoutNotify(geminiIndex);
                }
                
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }

            // Always fire the dropdown postfix logic to ensure UI is consistent with current selection
            // This handles the case where we load Options with Gemini already selected
            Patch_OnImageGenerationDropdownChanged.ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "SaveCurrentPrefs")]
    public static class Patch_SaveCurrentPrefs
    {
        public static void Postfix(MainMenu __instance)
        {
            // Save Gemini key if it's currently selected in the dropdown
            int ind = __instance.imageGenerationDropdown.value;
            bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count && 
                            __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";
            
            if (isGemini)
            {
                string val = __instance.customerKeyTxtInputForImgGen.text;
                PlayerPrefs.SetString(NanoBananaPlugin.PREF_KEY_GEMINI_API_KEY, val);

                int presetInd = __instance.imgGenPresetDropdown.value;
                string model = presetInd == 1 ? "gemini-3-pro-image-preview" : "gemini-2.5-flash-image";
                PlayerPrefs.SetString(NanoBananaPlugin.PREF_KEY_GEMINI_MODEL, model);

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
            // Map the injected dropdown index back to our custom enum value 99
            // We must do this in a Prefix because the original method will throw an exception
            // if it tries to index into its own list with our new (extra) index.
            if (ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count)
            {
                if (__instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)")
                {
                    __result = (SS.ImageGenerationMode)99;
                    return false; // Skip original method
                }
            }
            return true; // Run original method
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenerationDropdownChanged")]
    public static class Patch_OnImageGenerationDropdownChanged
    {
        private static string _originalKeyLabel = null;
        // Track which elements we've hidden so we can restore them
        private static bool _hidWomboStyle = false;
        private static bool _hidNaiModel = false;
        private static bool _hidStableHordeKey = false;

        // Public method so Patch_MainMenu_Options can call it directly
        public static void ApplyUiState(MainMenu __instance)
        {
            int ind = __instance.imageGenerationDropdown.value;
            bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                            __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";

            // Find label component for the imgGen key field
            var labelComponent = __instance.customerKeyTxtInputForImgGenTrans.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(t => t.name.ToLower().Contains("label") || t.text.ToLower().Contains("key") || t.text.ToLower().Contains("customer") || t.text == "Gemini API Key");

            if (isGemini)
            {
                // Store original label if we haven't yet
                if (_originalKeyLabel == null && labelComponent != null) _originalKeyLabel = labelComponent.text;

                __instance.imgGenExplanation.SetText("Generates images using Google Gemini.\n\n<color=#00FF00>Note:</color> Enter your Gemini API Key below. Select the model from the preset dropdown.", true);
                
                // Repurpose the Sapphire Key field
                __instance.customerKeyTxtInputForImgGenTrans.gameObject.SetActive(true);
                if (labelComponent != null) labelComponent.text = "Gemini API Key";

                // Load our key
                string currentKey = PlayerPrefs.GetString(NanoBananaPlugin.PREF_KEY_GEMINI_API_KEY, NanoBananaPlugin.Instance.GeminiApiKey);
                __instance.customerKeyTxtInputForImgGen.SetTextWithoutNotify(currentKey);

                // Hide irrelevant elements (and track what we hid so we can restore them)
                if (__instance.womboStyleHolder.gameObject.activeSelf) { __instance.womboStyleHolder.gameObject.SetActive(false); _hidWomboStyle = true; }
                if (__instance.naiModelTransform.gameObject.activeSelf) { __instance.naiModelTransform.gameObject.SetActive(false); _hidNaiModel = true; }
                if (__instance.stableHordeKeyTransform.gameObject.activeSelf) { __instance.stableHordeKeyTransform.gameObject.SetActive(false); _hidStableHordeKey = true; }
                
                // Show standard stuff
                __instance.imgGenTweakHolder.gameObject.SetActive(true);
                __instance.exportImportImgGenSettingsTrans.gameObject.SetActive(true);

                if (__instance.imgGenPresetDropdown != null)
                {
                    __instance.imgGenPresetDropdown.gameObject.SetActive(true);
                    if (__instance.imgGenPresetDropdown.transform.parent != null)
                    {
                        __instance.imgGenPresetDropdown.transform.parent.gameObject.SetActive(true);
                    }
                    var populateMethod = __instance.GetType().GetMethod("PopulateImageGenPresetDropdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    populateMethod?.Invoke(__instance, null);
                }
                
                // Ensure settingsPojo is not null for this mode
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }
            else
            {
                // Restore label if we changed it
                if (_originalKeyLabel != null && labelComponent != null)
                {
                    labelComponent.text = _originalKeyLabel;
                }

                // Restore visibility of elements we previously hid
                // The original OnImageGenerationDropdownChanged manages these but only for its own modes;
                // since we forced them hidden, we need to explicitly undo that.
                if (_hidWomboStyle) { __instance.womboStyleHolder.gameObject.SetActive(true); _hidWomboStyle = false; }
                if (_hidNaiModel) { __instance.naiModelTransform.gameObject.SetActive(true); _hidNaiModel = false; }
                if (_hidStableHordeKey) { __instance.stableHordeKeyTransform.gameObject.SetActive(true); _hidStableHordeKey = false; }

                // Restore original value for shared field if switching back TO sapphire mode
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

        public static void Postfix(MainMenu __instance)
        {
            ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateSsPrefsWithPlayerPrefs")]
    public static class Patch_PopulateSsPrefsWithPlayerPrefs
    {
        public static void Postfix()
        {
            // Ensure settingsPojo is initialized if we start the game in Gemini mode
            if (SS.I.imageGenerationMode == (SS.ImageGenerationMode)99)
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
            // Avoid issues with null keys in PopulateImageGenInputFieldsWithPlayerPrefs for our custom mode
            if (generationMode == (SS.ImageGenerationMode)99)
            {
                return false; // Skip original method
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
            GameEntity.ImgGenState state = await NanoBananaPlugin.Instance.GenerateGeminiImage(geArg, geArg.imgGenInfo, prompt);
            
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
            // For everyone else (Local, Wombo, etc), we need to do it manually if removeBg is requested.
            if (SS.I.imageGenerationMode != (SS.ImageGenerationMode)99 && 
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
                    // The image might have already been padded by the original method.
                    // We apply color keying to remove white.
                    string arguments = $"-y -i \"{originalPath}\" -vf \"colorkey=white:0.1:0.2\" \"{tempPath}\"";
                    
                    await Utils.ExecuteCommandAsync(ffmpegPath, arguments);

                    if (File.Exists(tempPath))
                    {
                        File.Delete(originalPath);
                        File.Move(tempPath, originalPath);
                        NanoBananaPlugin.Log.LogInfo($"[UniversalFix] Removed background for {geArg.name}");
                        
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

        private static async Task GenerateGeminiSpriteTask(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            string prompt = entImgSettings.GetFormatted(await geArg.GetGenerateImagePrompt());
            // Gemini doesn't remove backgrounds yet, so we just ask for a white background
            if (removeBg) prompt += ", white background, isolated, high quality sprite";

            GameEntity.ImgGenState state = await NanoBananaPlugin.Instance.GenerateGeminiImage(geArg, geArg.spGenInfo, prompt);
            
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

    [HarmonyPatch(typeof(MainMenu), "GetSettingsPojoByInd")]
    public static class Patch_GetSettingsPojoByInd
    {
        [HarmonyPrefix]
        public static bool Prefix(int ind, SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode == (SS.ImageGenerationMode)99)
            {
                __result = SS.I.defaultWomboSettings; // Return a default one for now
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
            if (imageGenerationMode == (SS.ImageGenerationMode)99)
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

            if (mode == (SS.ImageGenerationMode)99)
            {
                __instance.imgGenPresetDropdown.ClearOptions();
                __instance.imgGenPresetDropdown.AddOptions(new List<string> { "Gemini 2.5 Flash Image", "Gemini 3 Pro Image Preview" });
                
                string currentModel = PlayerPrefs.GetString(NanoBananaPlugin.PREF_KEY_GEMINI_MODEL, NanoBananaPlugin.Instance.GeminiModel);
                if (currentModel == "gemini-3-pro-image-preview")
                    __instance.imgGenPresetDropdown.SetValueWithoutNotify(1);
                else
                    __instance.imgGenPresetDropdown.SetValueWithoutNotify(0);

                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenDropdownChanged")]
    public static class Patch_OnImageGenDropdownChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(MainMenu __instance)
        {
            var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return true;

            int dropdownVal = __instance.imageGenerationDropdown.value;
            SS.ImageGenerationMode mode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { dropdownVal });

            if (mode == (SS.ImageGenerationMode)99)
            {
                return false; // Skip original method to avoid exceptions/resets natively 
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
            // Detect if Gemini is active in the dropdown
            int ind = __instance.imageGenerationDropdown.value;
            bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count && 
                            __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";

            if (isGemini)
            {
                // Decouple ImgGen box (Gemini) from Text/Audio (Sapphire)
                GameObject selected = EventSystem.current?.currentSelectedGameObject;
                if (selected != null && selected == __instance.customerKeyTxtInputForImgGen.gameObject)
                {
                    // User is typing in THE GEMINI BOX. 
                    // We DO NOT want to sync this to Sapphire fields.
                    return false; 
                }

                // User is typing in one of the SAPPHIRE boxes (Text or Audio).
                // We sync them to each other, but NOT to the Gemini box.
                if (__instance.customerKeyTxtInput != null && __instance.customerKeyTxtInput.gameObject == selected)
                    __instance.customerKeyTxtInputForAudioGen.SetTextWithoutNotify(s);
                if (__instance.customerKeyTxtInputForAudioGen != null && __instance.customerKeyTxtInputForAudioGen.gameObject == selected)
                    __instance.customerKeyTxtInput.SetTextWithoutNotify(s);
                
                return false; // Skip original method to avoid touching customerKeyTxtInputForImgGen
            }
            return true; // Use default behavior for other modes
        }
    }
}
