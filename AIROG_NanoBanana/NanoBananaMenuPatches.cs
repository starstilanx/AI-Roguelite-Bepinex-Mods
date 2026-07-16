using HarmonyLib;
using TMPro;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.EventSystems;

namespace AIROG_NanoBanana
{
    // Harmony patches that wire "Gemini (Nano Banana)" into MainMenu's image-generation
    // options dropdown: injecting the option, persisting the selection to PlayerPrefs,
    // and showing/hiding the relevant UI rows. Split out of NanoBananaPlugin.cs — this
    // is the bulk of that file and is unrelated to the actual Gemini HTTP call
    // (see NanoBananaImageClient.cs) or the generation-trigger patches
    // (see NanoBananaGenerationPatches.cs).

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
            catch (System.Exception ex)
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
