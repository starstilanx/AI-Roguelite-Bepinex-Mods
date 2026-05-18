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
        // Suppresses ApplyUiState during Options() body to avoid the spurious
        // OnImageGenerationDropdownChanged(0) call the game fires internally.
        internal static bool _optionsOpenInProgress = false;
        
        public const string PREF_KEY_GEMINI_API_KEY   = "PREF_KEY_GEMINI_IMG_GEN_API_KEY";
        public const string PREF_KEY_GEMINI_MODEL      = "PREF_KEY_GEMINI_IMG_GEN_MODEL";
        public const string PREF_KEY_GEMINI_RESOLUTION = "PREF_KEY_GEMINI_IMG_GEN_RESOLUTION";

        // Preset table: (display name, model ID, output resolution — empty = model default)
        internal static readonly string[] PRESET_NAMES  = { "Gemini 2.5 Flash", "Gemini 3.1 Flash · 1K", "Gemini 3.1 Flash · 2K", "Gemini 3.1 Flash · 4K" };
        internal static readonly string[] PRESET_MODELS = { "gemini-2.5-flash-image", "gemini-3.1-flash-image-preview", "gemini-3.1-flash-image-preview", "gemini-3.1-flash-image-preview" };
        internal static readonly string[] PRESET_RES    = { "", "1K", "2K", "4K" };

        public string GeminiApiKey    => PlayerPrefs.HasKey(PREF_KEY_GEMINI_API_KEY)
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_API_KEY)
            : Config.Bind("General", "GeminiApiKey", "", "API Key for Gemini Image Generation").Value;
        public string GeminiModel      => PlayerPrefs.HasKey(PREF_KEY_GEMINI_MODEL)
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_MODEL)
            : Config.Bind("General", "GeminiModel", "gemini-2.5-flash-image", "Model ID").Value;
        public string GeminiResolution => PlayerPrefs.GetString(PREF_KEY_GEMINI_RESOLUTION, "");

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

                Logger.LogInfo($"NanoBanana: Sending request to Gemini ({GeminiModel}) for {geArg.name}");

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

        private static string GetAspectRatioForEntity(GameEntity entity, GameEntity.ImgGenInfo imgGenInfo)
        {
            if (imgGenInfo == entity.spGenInfo) return "2:3";   // sprites: upright character art
            if (entity is Place)                return "16:9";  // locations: landscape scene
            if (entity is GameCharacter)        return "3:4";   // characters/NPCs: portrait
            if (entity is GameItem)             return "1:1";   // inventory items: square
            return "4:3";                                        // static objects / fallback
        }
    }

    [HarmonyPatch(typeof(MainMenu), "Options")]
    public static class Patch_MainMenu_Options
    {
        [HarmonyPrefix]
        public static void Prefix(MainMenu __instance)
        {
            NanoBananaPlugin._optionsOpenInProgress = true;
            bool isGemini = PlayerPrefs.GetInt("PREF_KEY_NANO_BANANA_ACTIVE", 0) == 1
                         || SS.I.imageGenerationMode == (SS.ImageGenerationMode)99;
            NanoBananaPlugin.Log.LogInfo($"[NanoBanana] Options Prefix: isGemini={isGemini}, mode={SS.I.imageGenerationMode}, activeFlag={PlayerPrefs.GetInt("PREF_KEY_NANO_BANANA_ACTIVE",0)}");
            if (!isGemini || __instance.imageGenerationDropdown == null) return;

            // Protect the pref from being reset by PopulateSsPrefsWithPlayerPrefs guard
            PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 99);
            PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 1);
            SS.I.imageGenerationMode = (SS.ImageGenerationMode)99;
        }

        [HarmonyPostfix]
        public static void Postfix(MainMenu __instance)
        {
            // Always inject the Gemini option (it was wiped by ClearOptions inside Options())
            List<TMP_Dropdown.OptionData> options = __instance.imageGenerationDropdown.options;
            if (!options.Any(o => o.text == "Gemini (Nano Banana)"))
                options.Add(new TMP_Dropdown.OptionData("Gemini (Nano Banana)"));

            // Two-signal check: new flag OR mode already set to 99 by our other patches
            bool wasGemini = PlayerPrefs.GetInt("PREF_KEY_NANO_BANANA_ACTIVE", 0) == 1
                          || SS.I.imageGenerationMode == (SS.ImageGenerationMode)99;
            NanoBananaPlugin.Log.LogInfo($"[NanoBanana] Options Postfix: wasGemini={wasGemini}, dropdownCount={options.Count}");

            if (wasGemini)
            {
                int geminiIndex = options.FindIndex(o => o.text == "Gemini (Nano Banana)");
                NanoBananaPlugin.Log.LogInfo($"[NanoBanana] Options Postfix: geminiIndex={geminiIndex}");
                if (geminiIndex != -1)
                    __instance.imageGenerationDropdown.SetValueWithoutNotify(geminiIndex);

                SS.I.imageGenerationMode = (SS.ImageGenerationMode)99;
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 99);
                PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 1);
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }

            NanoBananaPlugin._optionsOpenInProgress = false;
            Patch_OnImageGenerationDropdownChanged.ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "SaveCurrentPrefs")]
    public static class Patch_SaveCurrentPrefs
    {
        public static void Postfix(MainMenu __instance)
        {
            // Guard: dropdown may be null if SaveCurrentPrefs is called outside the Options screen
            if (__instance == null || __instance.imageGenerationDropdown == null) return;

            // Save Gemini key if it's currently selected in the dropdown
            int ind = __instance.imageGenerationDropdown.value;
            bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                            __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";
            
            if (isGemini)
            {
                var keyInputField = __instance.customerKeySlotForImgGen?.inputField;
                if (keyInputField == null || __instance.imgGenPresetDropdown == null) return;

                string val = keyInputField.text;
                PlayerPrefs.SetString(NanoBananaPlugin.PREF_KEY_GEMINI_API_KEY, val);

                int presetInd = __instance.imgGenPresetDropdown.value;
                int safeInd = (presetInd >= 0 && presetInd < NanoBananaPlugin.PRESET_MODELS.Length) ? presetInd : 0;
                PlayerPrefs.SetString(NanoBananaPlugin.PREF_KEY_GEMINI_MODEL,      NanoBananaPlugin.PRESET_MODELS[safeInd]);
                PlayerPrefs.SetString(NanoBananaPlugin.PREF_KEY_GEMINI_RESOLUTION, NanoBananaPlugin.PRESET_RES[safeInd]);

                // Persist mode 99 so PopulateSsPrefsWithPlayerPrefs can restore it
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 99);
                PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 1);
                PlayerPrefs.Save();
            }
            else
            {
                // User switched away from Gemini — clear the active flag
                PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 0);
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
        private static string _originalPlaceholder = null;
        private static bool _hidWomboStyle = false;
        private static bool _hidNaiModel = false;
        private static bool _hidStableHordeKey = false;
        private static bool _hidSubStatusRow = false;
        private static bool _hidAirlImgGenStyle = false;

        public static void ApplyUiState(MainMenu __instance)
        {
            try
            {
                int ind = __instance.imageGenerationDropdown.value;
                bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                                __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";

                if (isGemini)
                {
                    if (__instance.imgGenExplanation != null)
                        __instance.imgGenExplanation.SetText("Generates images using Google Gemini.\n\n<color=#00FF00>Note:</color> Enter your Gemini API Key below. Select the model from the preset dropdown.", true);

                    // Re-activate the customer key slot (game hides it for unknown modes),
                    // then hide only the subscription status sub-row inside it.
                    if (__instance.customerKeySlotForImgGen != null)
                    {
                        __instance.customerKeySlotForImgGen.gameObject.SetActive(true);

                        var subStatus = __instance.customerKeySlotForImgGen.subStatusTxt;
                        if (subStatus != null && !_hidSubStatusRow)
                        {
                            Transform rowParent = subStatus.transform.parent;
                            bool inSubRow = rowParent != null && rowParent != __instance.customerKeySlotForImgGen.transform;
                            GameObject rowToHide = inSubRow ? rowParent.gameObject : subStatus.gameObject;
                            rowToHide.SetActive(false);
                            _hidSubStatusRow = true;

                            // Flat layout fallback: also hide the "Subscription status" label sibling
                            if (!inSubRow)
                            {
                                foreach (Transform child in __instance.customerKeySlotForImgGen.transform)
                                {
                                    var t = child.GetComponent<TMP_Text>();
                                    if (t != null && t != subStatus && t.text.ToLower().Contains("subscription"))
                                    {
                                        t.gameObject.SetActive(false);
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Hide AIRL/Obsidian-only image style dropdown (activated by SAPPHIRE init path)
                    if (__instance.airlImgGenStyleTrans != null && __instance.airlImgGenStyleTrans.gameObject.activeSelf && !_hidAirlImgGenStyle)
                    {
                        __instance.airlImgGenStyleTrans.gameObject.SetActive(false);
                        _hidAirlImgGenStyle = true;
                    }

                    // Update placeholder and load our key
                    var geminiInputField = __instance.customerKeySlotForImgGen?.inputField;
                    if (geminiInputField != null)
                    {
                        var placeholder = geminiInputField.placeholder as TMP_Text;
                        if (placeholder != null)
                        {
                            if (_originalPlaceholder == null) _originalPlaceholder = placeholder.text;
                            placeholder.text = "Enter your Gemini API Key";
                        }
                        string currentKey = PlayerPrefs.GetString(NanoBananaPlugin.PREF_KEY_GEMINI_API_KEY, "");
                        geminiInputField.SetTextWithoutNotify(currentKey);
                    }

                    // Change the row label from "Customer key" to "Gemini API Key".
                    if (__instance.customerKeySlotForImgGen != null)
                    {
                        var slot = __instance.customerKeySlotForImgGen;
                        Transform inputFieldTf = slot.inputField != null ? slot.inputField.transform : null;
                        TMP_Text labelComponent = slot.transform
                            .GetComponentsInChildren<TMP_Text>(true)
                            .FirstOrDefault(t =>
                                (inputFieldTf == null || !t.transform.IsChildOf(inputFieldTf)) &&
                                t != slot.subStatusTxt);
                        if (labelComponent != null)
                        {
                            if (_originalKeyLabel == null) _originalKeyLabel = labelComponent.text;
                            labelComponent.text = "Gemini API Key";
                        }
                    }

                    // Hide irrelevant elements
                    if (__instance.womboStyleHolder != null && __instance.womboStyleHolder.gameObject.activeSelf) { __instance.womboStyleHolder.gameObject.SetActive(false); _hidWomboStyle = true; }
                    if (__instance.naiModelTransform != null && __instance.naiModelTransform.gameObject.activeSelf) { __instance.naiModelTransform.gameObject.SetActive(false); _hidNaiModel = true; }
                    if (__instance.stableHordeKeyTransform != null && __instance.stableHordeKeyTransform.gameObject.activeSelf) { __instance.stableHordeKeyTransform.gameObject.SetActive(false); _hidStableHordeKey = true; }

                    // Show standard elements
                    if (__instance.imgGenTweakHolder != null) __instance.imgGenTweakHolder.gameObject.SetActive(true);
                    if (__instance.exportImportImgGenSettingsTrans != null) __instance.exportImportImgGenSettingsTrans.gameObject.SetActive(true);

                    if (__instance.imgGenPresetDropdown != null)
                    {
                        __instance.imgGenPresetDropdown.gameObject.SetActive(true);
                        if (__instance.imgGenPresetDropdown.transform.parent != null)
                            __instance.imgGenPresetDropdown.transform.parent.gameObject.SetActive(true);
                        var populateMethod = __instance.GetType().GetMethod("PopulateImageGenPresetDropdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        populateMethod?.Invoke(__instance, null);
                    }

                    if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
                }
                else
                {
                    // Restore subscription status row
                    if (_hidSubStatusRow && __instance.customerKeySlotForImgGen?.subStatusTxt != null)
                    {
                        var subStatus = __instance.customerKeySlotForImgGen.subStatusTxt;
                        Transform rowParent = subStatus.transform.parent;
                        bool inSubRow = rowParent != null && rowParent != __instance.customerKeySlotForImgGen.transform;
                        (inSubRow ? rowParent.gameObject : subStatus.gameObject).SetActive(true);
                        _hidSubStatusRow = false;

                        if (!inSubRow)
                        {
                            foreach (Transform child in __instance.customerKeySlotForImgGen.transform)
                            {
                                var t = child.GetComponent<TMP_Text>();
                                if (t != null && t != subStatus && t.text.ToLower().Contains("subscription"))
                                {
                                    t.gameObject.SetActive(true);
                                    break;
                                }
                            }
                        }
                    }

                    // Restore AIRL image style dropdown
                    if (_hidAirlImgGenStyle && __instance.airlImgGenStyleTrans != null)
                    {
                        __instance.airlImgGenStyleTrans.gameObject.SetActive(true);
                        _hidAirlImgGenStyle = false;
                    }

                    // Restore placeholder
                    if (_originalPlaceholder != null && __instance.customerKeySlotForImgGen?.inputField?.placeholder is TMP_Text ph)
                    {
                        ph.text = _originalPlaceholder;
                        _originalPlaceholder = null;
                    }

                    // Restore label
                    if (_originalKeyLabel != null && __instance.customerKeySlotForImgGen != null)
                    {
                        var slot = __instance.customerKeySlotForImgGen;
                        Transform inputFieldTf = slot.inputField != null ? slot.inputField.transform : null;
                        TMP_Text labelComponent = slot.transform
                            .GetComponentsInChildren<TMP_Text>(true)
                            .FirstOrDefault(t =>
                                (inputFieldTf == null || !t.transform.IsChildOf(inputFieldTf)) &&
                                t != slot.subStatusTxt);
                        if (labelComponent != null)
                        {
                            labelComponent.text = _originalKeyLabel;
                            _originalKeyLabel = null;
                        }
                    }

                    // Restore visibility of elements we previously hid
                    if (_hidWomboStyle && __instance.womboStyleHolder != null) { __instance.womboStyleHolder.gameObject.SetActive(true); _hidWomboStyle = false; }
                    if (_hidNaiModel && __instance.naiModelTransform != null) { __instance.naiModelTransform.gameObject.SetActive(true); _hidNaiModel = false; }
                    if (_hidStableHordeKey && __instance.stableHordeKeyTransform != null) { __instance.stableHordeKeyTransform.gameObject.SetActive(true); _hidStableHordeKey = false; }

                    // Restore original customer key value if switching back to SAPPHIRE
                    var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (method != null)
                    {
                        SS.ImageGenerationMode selectedMode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { ind });
                        if (selectedMode == SS.ImageGenerationMode.SAPPHIRE && __instance.customerKeySlotForImgGen?.inputField != null)
                            __instance.customerKeySlotForImgGen.inputField.SetTextWithoutNotify(PlayerPrefs.GetString("PREF_KEY_CUSTOMER_KEY2"));
                    }
                }
            }
            catch (Exception ex)
            {
                NanoBananaPlugin.Log.LogError($"[NanoBanana] ApplyUiState failed: {ex.Message}");
            }
        }

        public static void Postfix(MainMenu __instance)
        {
            // Suppress during Options() body — Patch_MainMenu_Options.Postfix calls ApplyUiState directly
            if (NanoBananaPlugin._optionsOpenInProgress) return;

            // If the user just selected Gemini from the dropdown, write the pref NOW (don't wait for Back/SaveCurrentPrefs).
            // This is the bootstrap fix: without this, PREF_KEY_IMAGE_GENERATION_MODE2 never gets set to 99
            // and PopulateSsPrefsWithPlayerPrefs can never detect Gemini on the next session.
            int ind = __instance.imageGenerationDropdown.value;
            bool isGemini = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                            __instance.imageGenerationDropdown.options[ind].text == "Gemini (Nano Banana)";
            if (isGemini)
            {
                NanoBananaPlugin.Log.LogInfo("[NanoBanana] User selected Gemini — writing pref 99 immediately.");
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 99);
                PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 1);
                SS.I.imageGenerationMode = (SS.ImageGenerationMode)99;
                PlayerPrefs.Save();
            }
            else
            {
                // User switched away — clear the active flag
                if (PlayerPrefs.GetInt("PREF_KEY_NANO_BANANA_ACTIVE", 0) == 1)
                {
                    NanoBananaPlugin.Log.LogInfo("[NanoBanana] User deselected Gemini — clearing active flag.");
                    PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 0);
                    PlayerPrefs.Save();
                }
            }

            ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateSsPrefsWithPlayerPrefs")]
    public static class Patch_PopulateSsPrefsWithPlayerPrefs
    {
        // Carries "was mode 99 saved" from Prefix → Postfix without relying on a pref
        private static bool _wasGeminiSaved = false;

        [HarmonyPrefix]
        public static void Prefix()
        {
            int saved = PlayerPrefs.GetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8);
            _wasGeminiSaved = (saved == 99);
            NanoBananaPlugin.Log.LogInfo($"[NanoBanana] PopulateSsPrefs Prefix: saved={saved}, wasGemini={_wasGeminiSaved}");
            if (_wasGeminiSaved)
            {
                // Temporarily swap to AIRL_FREE so the game's validation guard doesn't reset us
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8);
            }
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            NanoBananaPlugin.Log.LogInfo($"[NanoBanana] PopulateSsPrefs Postfix: _wasGeminiSaved={_wasGeminiSaved}");
            if (_wasGeminiSaved)
            {
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 99);
                PlayerPrefs.SetInt("PREF_KEY_NANO_BANANA_ACTIVE", 1);
                SS.I.imageGenerationMode = (SS.ImageGenerationMode)99;
                if (SS.I.settingsPojo == null)
                    SS.I.settingsPojo = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
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
                __instance.imgGenPresetDropdown.AddOptions(NanoBananaPlugin.PRESET_NAMES.ToList());

                string currentModel = PlayerPrefs.GetString(NanoBananaPlugin.PREF_KEY_GEMINI_MODEL, "gemini-2.5-flash-image");
                string currentRes   = PlayerPrefs.GetString(NanoBananaPlugin.PREF_KEY_GEMINI_RESOLUTION, "");
                int sel = 0;
                for (int i = 0; i < NanoBananaPlugin.PRESET_MODELS.Length; i++)
                {
                    if (NanoBananaPlugin.PRESET_MODELS[i] == currentModel && NanoBananaPlugin.PRESET_RES[i] == currentRes)
                    {
                        sel = i;
                        break;
                    }
                }
                __instance.imgGenPresetDropdown.SetValueWithoutNotify(sel);

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
                var imgKeyField = __instance.customerKeySlotForImgGen?.inputField;
                if (imgKeyField != null && selected != null && selected == imgKeyField.gameObject)
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
