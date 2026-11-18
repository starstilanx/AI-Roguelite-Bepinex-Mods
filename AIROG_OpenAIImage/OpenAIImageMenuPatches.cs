using HarmonyLib;
using TMPro;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.EventSystems;

namespace AIROG_OpenAIImage
{
    // Wires "OpenAI-Compatible API" into MainMenu's image-generation dropdown: injecting the
    // option, persisting the selection past the game's mode-validation guard, and showing the
    // key/model rows. Structure follows AIROG_NanoBanana's menu patches, which are the proven
    // shape for a custom SS.ImageGenerationMode.

    [HarmonyPatch(typeof(MainMenu), "Options")]
    public static class Patch_MainMenu_Options
    {
        [HarmonyPrefix]
        public static void Prefix(MainMenu __instance)
        {
            OpenAIImagePlugin._optionsOpenInProgress = true;
            Patch_OnImageGenerationDropdownChanged.CaptureOriginals(__instance);

            bool isOpenAI = PlayerPrefs.GetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 0) == 1
                         || SS.I.imageGenerationMode == OpenAIImagePlugin.OPENAI_IMG_MODE;
            if (!isOpenAI || __instance.imageGenerationDropdown == null) return;

            PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", (int)OpenAIImagePlugin.OPENAI_IMG_MODE);
            PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 1);
            SS.I.imageGenerationMode = OpenAIImagePlugin.OPENAI_IMG_MODE;
        }

        [HarmonyPostfix]
        public static void Postfix(MainMenu __instance)
        {
            // Always re-inject: Options() rebuilds the dropdown from scratch via ClearOptions.
            List<TMP_Dropdown.OptionData> options = __instance.imageGenerationDropdown.options;
            if (!options.Any(o => o.text == OpenAIImagePlugin.DROPDOWN_LABEL))
                options.Add(new TMP_Dropdown.OptionData(OpenAIImagePlugin.DROPDOWN_LABEL));

            bool wasOpenAI = PlayerPrefs.GetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 0) == 1
                          || SS.I.imageGenerationMode == OpenAIImagePlugin.OPENAI_IMG_MODE;

            if (wasOpenAI)
            {
                int ind = options.FindIndex(o => o.text == OpenAIImagePlugin.DROPDOWN_LABEL);
                if (ind != -1) __instance.imageGenerationDropdown.SetValueWithoutNotify(ind);

                SS.I.imageGenerationMode = OpenAIImagePlugin.OPENAI_IMG_MODE;
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", (int)OpenAIImagePlugin.OPENAI_IMG_MODE);
                PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 1);
                if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
            }

            OpenAIImagePlugin._optionsOpenInProgress = false;
            Patch_OnImageGenerationDropdownChanged.ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "SaveCurrentPrefs")]
    public static class Patch_SaveCurrentPrefs
    {
        public static void Postfix(MainMenu __instance)
        {
            // Also clears the stranded-guard case: Harmony postfixes are skipped when the
            // original throws, so Options' postfix isn't the only place this can be reset.
            OpenAIImagePlugin._optionsOpenInProgress = false;

            if (__instance == null || __instance.imageGenerationDropdown == null) return;

            int ind = __instance.imageGenerationDropdown.value;
            bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                            __instance.imageGenerationDropdown.options[ind].text == OpenAIImagePlugin.DROPDOWN_LABEL;

            if (isOpenAI)
            {
                var keyInputField = __instance.customerKeySlotForImgGen?.inputField;
                if (keyInputField != null)
                    PlayerPrefs.SetString(OpenAIImagePlugin.PREF_KEY_API_KEY, keyInputField.text.Trim());

                var models = OpenAIImagePlugin.Instance.Models;
                if (__instance.imgGenPresetDropdown != null && models.Length > 0)
                {
                    int presetInd = __instance.imgGenPresetDropdown.value;
                    int safeInd = (presetInd >= 0 && presetInd < models.Length) ? presetInd : 0;
                    PlayerPrefs.SetString(OpenAIImagePlugin.PREF_KEY_MODEL, models[safeInd]);
                }

                // Persist mode 97 so PopulateSsPrefsWithPlayerPrefs can restore it.
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", (int)OpenAIImagePlugin.OPENAI_IMG_MODE);
                PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 1);
                PlayerPrefs.Save();
            }
            else
            {
                PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 0);
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
            // Our injected row sits past the end of imgGenModeListWithNovelAi, so the original
            // would throw on ElementAt(ind). Resolve it ourselves.
            if (ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                __instance.imageGenerationDropdown.options[ind].text == OpenAIImagePlugin.DROPDOWN_LABEL)
            {
                __result = OpenAIImagePlugin.OPENAI_IMG_MODE;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateSsPrefsWithPlayerPrefs")]
    public static class Patch_PopulateSsPrefsWithPlayerPrefs
    {
        // MainMenu.cs:2343 resets any mode missing from imgGenModeListWithNovelAi back to 8.
        // Swap to AIRL_FREE for the duration of the original, then restore mode 97.
        private static bool _wasOpenAISaved = false;

        [HarmonyPrefix]
        public static void Prefix()
        {
            _wasOpenAISaved = PlayerPrefs.GetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8)
                              == (int)OpenAIImagePlugin.OPENAI_IMG_MODE;
            if (_wasOpenAISaved) PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", 8);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!_wasOpenAISaved) return;

            PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", (int)OpenAIImagePlugin.OPENAI_IMG_MODE);
            PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 1);
            SS.I.imageGenerationMode = OpenAIImagePlugin.OPENAI_IMG_MODE;

            // Prompt formatting NREs without a settingsPojo; the game only assigns it in per-mode branches.
            if (SS.I.settingsPojo == null)
                SS.I.settingsPojo = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenerationDropdownChanged")]
    public static class Patch_OnImageGenerationDropdownChanged
    {
        private static string _originalKeyLabel = null;
        private static string _originalPlaceholder = null;
        private static bool _hidSubStatusRow = false;
        private static bool _tookOverKeyRow = false;
        private static MainMenu _capturedFor = null;

        /// <summary>
        /// Records the shared key row's stock label and placeholder once per MainMenu, from the
        /// Options prefix, i.e. before any backend has relabelled it. Capturing lazily at
        /// takeover time picked up another custom backend's label when switching straight from
        /// Gemini/Vertex to this one, and later "restored" that onto the subscription key box.
        /// </summary>
        internal static void CaptureOriginals(MainMenu menu)
        {
            try
            {
                if (menu == null || _capturedFor == menu) return;
                _capturedFor = menu;
                _hidSubStatusRow = false;
                _tookOverKeyRow = false;

                var slot = menu.customerKeySlotForImgGen;
                if (slot == null) return;
                _originalKeyLabel = FindKeyLabel(slot)?.text;
                _originalPlaceholder = (slot.inputField?.placeholder as TMP_Text)?.text;
            }
            catch (System.Exception ex)
            {
                OpenAIImagePlugin.Log.LogError($"[OpenAIImage] CaptureOriginals failed: {ex.Message}");
            }
        }

        /// <summary>The key row's caption: not the input field's own child texts, not the sub-status line.</summary>
        private static TMP_Text FindKeyLabel(CustomerKeySlot slot)
        {
            Transform inputFieldTf = slot.inputField != null ? slot.inputField.transform : null;
            return slot.transform
                .GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(t => (inputFieldTf == null || !t.transform.IsChildOf(inputFieldTf)) && t != slot.subStatusTxt);
        }

        /// <summary>
        /// Puts a stock text back: the captured copy first, then, where the label is driven by
        /// a LocalizeStringEvent, a re-localise on top (the captured copy may predate
        /// localisation or a language change).
        /// </summary>
        private static void RestoreText(TMP_Text txt, string captured)
        {
            if (txt == null) return;
            if (captured != null) txt.text = captured;
            MonoBehaviour localizer = txt.GetComponents<MonoBehaviour>()
                .FirstOrDefault(c => c != null && c.GetType().Name == "LocalizeStringEvent");
            localizer?.GetType().GetMethod("RefreshString", System.Type.EmptyTypes)?.Invoke(localizer, null);
        }

        public static void ApplyUiState(MainMenu __instance)
        {
            try
            {
                int ind = __instance.imageGenerationDropdown.value;
                bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                                __instance.imageGenerationDropdown.options[ind].text == OpenAIImagePlugin.DROPDOWN_LABEL;

                if (isOpenAI)
                {
                    if (__instance.imgGenExplanation != null)
                        __instance.imgGenExplanation.SetText(
                            "Generates images through any OpenAI-compatible /images/generations endpoint " +
                            "(OpenAI, Venice, local servers).\n\n<color=#00FF00>Note:</color> Enter your API key below " +
                            "and pick a model from the preset dropdown. Base URL and the model list are set in the " +
                            "BepInEx config file.", true);

                    // The game deactivates the key slot for modes it doesn't recognise, so re-show it,
                    // then hide just the subscription sub-row (meaningless for a third-party key).
                    if (__instance.customerKeySlotForImgGen != null)
                    {
                        __instance.customerKeySlotForImgGen.gameObject.SetActive(true);

                        var subStatus = __instance.customerKeySlotForImgGen.subStatusTxt;
                        if (subStatus != null && !_hidSubStatusRow)
                        {
                            Transform rowParent = subStatus.transform.parent;
                            bool inSubRow = rowParent != null && rowParent != __instance.customerKeySlotForImgGen.transform;
                            (inSubRow ? rowParent.gameObject : subStatus.gameObject).SetActive(false);
                            _hidSubStatusRow = true;

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

                    // Hide the AIRL/Obsidian-only style dropdown (turned on by the SAPPHIRE path).
                    // Unconditional: Options() re-shows it on every open, so a "hidden once" flag
                    // left it visible from the second open on.
                    if (__instance.airlImgGenStyleTrans != null)
                        __instance.airlImgGenStyleTrans.gameObject.SetActive(false);

                    var keyInputField = __instance.customerKeySlotForImgGen?.inputField;
                    if (keyInputField != null)
                    {
                        if (keyInputField.placeholder is TMP_Text placeholder)
                        {
                            placeholder.text = "Enter your API key";
                        }
                        keyInputField.SetTextWithoutNotify(PlayerPrefs.GetString(OpenAIImagePlugin.PREF_KEY_API_KEY, ""));
                    }

                    if (__instance.customerKeySlotForImgGen != null)
                    {
                        TMP_Text label = FindKeyLabel(__instance.customerKeySlotForImgGen);
                        if (label != null) label.text = "API Key";
                        _tookOverKeyRow = true;
                    }

                    if (__instance.womboStyleHolder != null) __instance.womboStyleHolder.gameObject.SetActive(false);
                    if (__instance.naiModelTransform != null) __instance.naiModelTransform.gameObject.SetActive(false);
                    if (__instance.stableHordeKeyTransform != null) __instance.stableHordeKeyTransform.gameObject.SetActive(false);

                    if (__instance.imgGenTweakHolder != null) __instance.imgGenTweakHolder.gameObject.SetActive(true);
                    if (__instance.exportImportImgGenSettingsTrans != null) __instance.exportImportImgGenSettingsTrans.gameObject.SetActive(true);

                    if (__instance.imgGenPresetDropdown != null)
                    {
                        __instance.imgGenPresetDropdown.gameObject.SetActive(true);
                        if (__instance.imgGenPresetDropdown.transform.parent != null)
                            __instance.imgGenPresetDropdown.transform.parent.gameObject.SetActive(true);

                        var populate = __instance.GetType().GetMethod("PopulateImageGenPresetDropdown",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        populate?.Invoke(__instance, null);
                    }

                    if (SS.I.settingsPojo == null) SS.I.settingsPojo = SS.I.defaultWomboSettings;
                }
                else
                {
                    // Resolved through the game's (patched) mapper, so another mod's row answers for itself.
                    var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    SS.ImageGenerationMode selectedMode = method != null
                        ? (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { ind })
                        : SS.ImageGenerationMode.DISABLED;

                    if (_tookOverKeyRow)
                    {
                        _tookOverKeyRow = false;

                        // A custom mode from another mod (Vertex 98, Gemini 99) owns the shared key
                        // row now and relabels and refills it in its own postfix, which may run
                        // before or after this one; undoing ours would put the stock label over
                        // its key box.
                        bool otherCustomBackend = !System.Enum.IsDefined(typeof(SS.ImageGenerationMode), selectedMode);
                        var slot = __instance.customerKeySlotForImgGen;
                        if (!otherCustomBackend && slot != null)
                        {
                            if (_hidSubStatusRow && slot.subStatusTxt != null)
                            {
                                var subStatus = slot.subStatusTxt;
                                Transform rowParent = subStatus.transform.parent;
                                bool inSubRow = rowParent != null && rowParent != slot.transform;
                                (inSubRow ? rowParent.gameObject : subStatus.gameObject).SetActive(true);

                                if (!inSubRow)
                                {
                                    foreach (Transform child in slot.transform)
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

                            RestoreText(slot.inputField?.placeholder as TMP_Text, _originalPlaceholder);
                            RestoreText(FindKeyLabel(slot), _originalKeyLabel);
                        }
                        _hidSubStatusRow = false;
                    }

                    // Row visibility is not restored here: the game's own handler has already set
                    // every row for the new mode before this postfix runs.

                    // Put the real customer key back if the user switched to SAPPHIRE.
                    if (selectedMode == SS.ImageGenerationMode.SAPPHIRE && __instance.customerKeySlotForImgGen?.inputField != null)
                        __instance.customerKeySlotForImgGen.inputField.SetTextWithoutNotify(PlayerPrefs.GetString("PREF_KEY_CUSTOMER_KEY2"));
                }
            }
            catch (System.Exception ex)
            {
                OpenAIImagePlugin.Log.LogError($"[OpenAIImage] ApplyUiState failed: {ex.Message}");
            }
        }

        public static void Postfix(MainMenu __instance)
        {
            // Options() fires this before our row exists; its postfix calls ApplyUiState directly.
            if (OpenAIImagePlugin._optionsOpenInProgress) return;

            int ind = __instance.imageGenerationDropdown.value;
            bool isOpenAI = ind >= 0 && ind < __instance.imageGenerationDropdown.options.Count &&
                            __instance.imageGenerationDropdown.options[ind].text == OpenAIImagePlugin.DROPDOWN_LABEL;

            if (isOpenAI)
            {
                // Write the pref immediately rather than waiting for Back/SaveCurrentPrefs, so the
                // selection is detectable even if the user never completes the options flow.
                PlayerPrefs.SetInt("PREF_KEY_IMAGE_GENERATION_MODE2", (int)OpenAIImagePlugin.OPENAI_IMG_MODE);
                PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 1);
                SS.I.imageGenerationMode = OpenAIImagePlugin.OPENAI_IMG_MODE;
                PlayerPrefs.Save();
            }
            else if (PlayerPrefs.GetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 0) == 1)
            {
                PlayerPrefs.SetInt(OpenAIImagePlugin.PREF_KEY_ACTIVE, 0);
                PlayerPrefs.Save();
            }

            ApplyUiState(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateImageGenInputFieldsWithPlayerPrefs")]
    public static class Patch_PopulateImageGenInputFieldsWithPlayerPrefs
    {
        public static bool Prefix(SS.ImageGenerationMode generationMode)
        {
            // No stock input fields belong to our mode; the original would read null keys.
            return generationMode != OpenAIImagePlugin.OPENAI_IMG_MODE;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "GetSettingsPojoByInd")]
    public static class Patch_GetSettingsPojoByInd
    {
        [HarmonyPrefix]
        public static bool Prefix(int ind, SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode != OpenAIImagePlugin.OPENAI_IMG_MODE) return true;
            // The original NREs on a null preset list for an unknown mode.
            __result = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
            return false;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "GetDefaultSettingsPojoForImageGenMode")]
    public static class Patch_GetDefaultSettingsPojoForImageGenMode
    {
        [HarmonyPrefix]
        public static bool Prefix(SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode != OpenAIImagePlugin.OPENAI_IMG_MODE) return true;
            __result = SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
            return false;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "PopulateImageGenPresetDropdown")]
    public static class Patch_PopulateImageGenPresetDropdown
    {
        [HarmonyPrefix]
        public static bool Prefix(MainMenu __instance)
        {
            var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return true;

            var mode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { __instance.imageGenerationDropdown.value });
            if (mode != OpenAIImagePlugin.OPENAI_IMG_MODE) return true;
            if (__instance.imgGenPresetDropdown == null) return false;

            // Repurpose the preset dropdown as the model picker, driven by the config model list.
            string[] models = OpenAIImagePlugin.Instance.Models;
            __instance.imgGenPresetDropdown.ClearOptions();
            __instance.imgGenPresetDropdown.AddOptions(models.ToList());

            string current = PlayerPrefs.GetString(OpenAIImagePlugin.PREF_KEY_MODEL, "");
            int sel = System.Array.IndexOf(models, current);
            __instance.imgGenPresetDropdown.SetValueWithoutNotify(sel >= 0 ? sel : 0);
            return false;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenDropdownChanged")]
    public static class Patch_OnImageGenDropdownChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(MainMenu __instance)
        {
            var method = __instance.GetType().GetMethod("GetImageGenerationModeByDropdownInd",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method == null) return true;

            var mode = (SS.ImageGenerationMode)method.Invoke(__instance, new object[] { __instance.imageGenerationDropdown.value });

            // Our preset entries are models, not SettingsPojos — let the original load nothing.
            return mode != OpenAIImagePlugin.OPENAI_IMG_MODE;
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnCustomerKeyTxtInputChanged")]
    public static class Patch_OnCustomerKeyTxtInputChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(string s, MainMenu __instance)
        {
            if (!OpenAISelection.IsSelected(__instance)) return true;

            // A subscription key typed into one of the other boxes (edits to our own box are
            // stopped in Patch_CustomerKeySlot_OnTxtInputChanged and never get here). The
            // original mirrors it into every box, which would overwrite our API key; mirror it
            // into every box but ours instead.
            if (__instance.customerKeySlots != null)
            {
                foreach (CustomerKeySlot slot in __instance.customerKeySlots)
                {
                    if (slot != null && slot != __instance.customerKeySlotForImgGen && slot.inputField != null)
                        slot.inputField.SetTextWithoutNotify(s);
                }
            }
            return false;
        }
    }

    internal static class OpenAISelection
    {
        public static bool IsSelected(MainMenu menu)
        {
            if (menu == null || menu.imageGenerationDropdown == null) return false;
            int ind = menu.imageGenerationDropdown.value;
            return ind >= 0 && ind < menu.imageGenerationDropdown.options.Count &&
                   menu.imageGenerationDropdown.options[ind].text == OpenAIImagePlugin.DROPDOWN_LABEL;
        }

        /// <summary>True for the image-gen key box while it is holding our API key.</summary>
        public static bool OwnsSlot(CustomerKeySlot slot)
        {
            if (slot == null) return false;
            MainMenu menu = slot.menu != null ? slot.menu : SS.I?.hackyMenu;
            return menu != null && slot == menu.customerKeySlotForImgGen && IsSelected(menu);
        }
    }

    // The image-gen key box is a CustomerKeySlot whose inspector-wired callbacks treat its text
    // as the AI Roguelite subscription key. While it holds our API key both must be stopped.

    [HarmonyPatch(typeof(CustomerKeySlot), "OnTxtInputChanged")]
    public static class Patch_CustomerKeySlot_OnTxtInputChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(CustomerKeySlot __instance)
        {
            // Would copy our API key into the text/video/audio/TTS subscription key boxes.
            return !OpenAISelection.OwnsSlot(__instance);
        }
    }

    [HarmonyPatch(typeof(CustomerKeySlot), "OnEndEdit")]
    public static class Patch_CustomerKeySlot_OnEndEdit
    {
        [HarmonyPrefix]
        public static bool Prefix(CustomerKeySlot __instance)
        {
            // Would set SS.I.customerKey to our API key and re-check the subscription with it,
            // i.e. send the key to the game's own servers as a customer key.
            return !OpenAISelection.OwnsSlot(__instance);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnImageGenTextFieldChanged")]
    public static class Patch_OnImageGenTextFieldChanged
    {
        [HarmonyPrefix]
        public static bool Prefix(MainMenu __instance)
        {
            // The original re-selects the settings preset matching the prompt boxes, falling
            // back to the last entry ("Custom"). Our preset dropdown lists models, so editing
            // any prompt box silently switched the model to the last one in the list.
            return !OpenAISelection.IsSelected(__instance);
        }
    }
}
