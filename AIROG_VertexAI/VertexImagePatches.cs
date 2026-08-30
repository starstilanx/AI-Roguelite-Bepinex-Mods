using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Wires "Vertex AI (Gemini Image)" into the image-generation dropdown as
    /// SS.ImageGenerationMode value 98, and redirects AIAsker's image and sprite
    /// generation to <see cref="VertexImageClient"/> while it is selected.
    ///
    /// 98 is chosen so AIROG_NanoBanana (which claims 99 for the AI Studio Gemini
    /// endpoint) can stay installed alongside this mod; each set of patches only acts on
    /// its own mode value.
    ///
    /// Unlike the text side there is no existing enum value to hide behind, so several of
    /// the game's mode-switch helpers would fault on an unknown mode and are short-circuited.
    /// </summary>
    internal static class VertexImagePatches
    {
        private static bool _optionsInProgress;

        public static void Register(VertexAIPlugin plugin)
        {
            var m = typeof(VertexImagePatches);

            plugin.TryPatch(typeof(MainMenu), "Options",
                prefix: new HarmonyMethod(m, nameof(Options_Prefix)),
                postfix: new HarmonyMethod(m, nameof(Options_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "GetImageGenerationModeByDropdownInd",
                prefix: new HarmonyMethod(m, nameof(GetImageGenModeByInd_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "OnImageGenerationDropdownChanged",
                postfix: new HarmonyMethod(m, nameof(OnImageGenerationDropdownChanged_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "SaveCurrentPrefs",
                postfix: new HarmonyMethod(m, nameof(SaveCurrentPrefs_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "PopulateSsPrefsWithPlayerPrefs",
                prefix: new HarmonyMethod(m, nameof(PopulateSsPrefs_Prefix)),
                postfix: new HarmonyMethod(m, nameof(PopulateSsPrefs_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "PopulateImageGenInputFieldsWithPlayerPrefs",
                prefix: new HarmonyMethod(m, nameof(SkipForVertexMode_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "PopulateImageGenPresetDropdown",
                prefix: new HarmonyMethod(m, nameof(PopulateImageGenPresetDropdown_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "OnImageGenDropdownChanged",
                prefix: new HarmonyMethod(m, nameof(OnImageGenDropdownChanged_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "GetSettingsPojoByInd",
                prefix: new HarmonyMethod(m, nameof(GetSettingsPojoByInd_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "GetDefaultSettingsPojoForImageGenMode",
                prefix: new HarmonyMethod(m, nameof(GetDefaultSettingsPojo_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "OnCustomerKeyTxtInputChanged",
                prefix: new HarmonyMethod(m, nameof(OnCustomerKeyTxtInputChanged_Prefix)));

            plugin.TryPatch(typeof(AIAsker), "getGeneratedImage",
                prefix: new HarmonyMethod(m, nameof(GetGeneratedImage_Prefix)));

            plugin.TryPatch(typeof(AIAsker), "getGeneratedSprite",
                prefix: new HarmonyMethod(m, nameof(GetGeneratedSprite_Prefix)));
        }

        // ------------------------------------------------------------------
        // Generation interception
        // ------------------------------------------------------------------

        public static bool GetGeneratedImage_Prefix(
            SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool useNewImgFileList, ref Task __result)
        {
            if (!VertexAIPlugin.ImageBackendActive) return true;
            __result = RunImageGen(entImgSettings, geArg, useNewImgFileList);
            return false;
        }

        public static bool GetGeneratedSprite_Prefix(
            SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg, ref Task __result)
        {
            if (!VertexAIPlugin.ImageBackendActive) return true;
            __result = RunSpriteGen(entImgSettings, geArg, removeBg);
            return false;
        }

        private static async Task RunImageGen(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool useNewImgFileList)
        {
            string prompt = FormatPrompt(entImgSettings, await geArg.GetGenerateImagePrompt(), geArg);
            bool removeBg = entImgSettings.removeBkgd && geArg.CanRemoveBkgd();
            if (removeBg) prompt = AppendFlatBackgroundHint(prompt);

            // useNewImgFileList means "add another variant", not "replace": the game writes
            // it under a fresh uuid in the save dir and appends that to imgFileNames.
            string imgUuid = useNewImgFileList ? Guid.NewGuid().ToString() : null;
            string pathNoExt = imgUuid == null ? null : Path.Combine(SS.I.saveSubDirAsArg, imgUuid);

            GameEntity.ImgGenState state = await VertexImageClient.GenerateImage(
                geArg, geArg.imgGenInfo, ImageAspectRatio(geArg), prompt, Gcm.CurrentToken, pathNoExt);

            if (state == GameEntity.ImgGenState.FINISHED && removeBg)
                await VertexImageClient.RemoveFlatBackground(geArg, geArg.imgGenInfo, pathNoExt);

            geArg.pendingHqRegen = false;
            Publish(geArg, geArg.imgGenInfo, state, imgUuid);
        }

        private static async Task RunSpriteGen(SettingsPojo.EntImgSettings entImgSettings, GameEntity geArg, bool removeBg)
        {
            string prompt = FormatPrompt(entImgSettings, await geArg.GetGenerateImagePrompt(), geArg);
            if (removeBg) prompt = AppendFlatBackgroundHint(prompt);

            GameEntity.ImgGenState state = await VertexImageClient.GenerateImage(
                geArg, geArg.spGenInfo, SpriteAspectRatio(geArg), prompt, Gcm.CurrentToken);

            if (state == GameEntity.ImgGenState.FINISHED && removeBg)
                await VertexImageClient.RemoveFlatBackground(geArg, geArg.spGenInfo);

            Publish(geArg, geArg.spGenInfo, state);
        }

        /// <summary>Applies both the image settings format and the scenario's image prompt format, as AIAsker does.</summary>
        private static string FormatPrompt(SettingsPojo.EntImgSettings settings, string basePrompt, GameEntity entity)
        {
            return settings.GetFormatted(basePrompt, entity.Mm?.GetScenarioState()?.imagePromptFmt);
        }

        /// <summary>Gemini cannot emit an alpha channel, so ask for a backdrop flat enough to key out.</summary>
        private static string AppendFlatBackgroundHint(string prompt)
        {
            return prompt + ", plain flat solid white background, isolated subject, no shadow";
        }

        /// <summary>
        /// Reproduces the aspect ratio AIAsker.getGeneratedImage hands the official
        /// backends, so Vertex frames each entity the way the rest of the game expects.
        /// </summary>
        private static float ImageAspectRatio(GameEntity geArg)
        {
            try
            {
                MainLayouts layouts = SS.I.Manager()?.mainLayouts;
                if (geArg is Place)
                    return MainLayouts.GetAspectRatio(SS.I.enableClassicLocationImgUi ? layouts?.mainImgHolder : layouts?.sidebarHolder);
                if (geArg is IllustratedStoryTurn)
                    return SS.I.enableClassicLocationImgUi ? 1f : MainLayouts.GetAspectRatio(layouts?.mainImgHolder);
                if (geArg.IsMainPlayer())
                    return 5f / 6f;
                if (geArg is BackgroundImg)
                    return MainLayouts.GetAspectRatio(layouts?.sidebarHolder);
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogWarning($"[VertexAI] could not read layout aspect ratio, using 1:1: {ex.Message}");
            }
            return 1f;
        }

        /// <summary>Sprite framing, matching AIAsker.getGeneratedSprite.</summary>
        private static float SpriteAspectRatio(GameEntity geArg)
        {
            try
            {
                return geArg.PreferredGrdAspectRatio();
            }
            catch (Exception)
            {
                // The base implementation throws for entity types that never reach the grid.
                return 2f / 3f;
            }
        }

        /// <summary>Commits the result to the entity and flags the UI to reload the file.</summary>
        private static void Publish(GameEntity entity, GameEntity.ImgGenInfo info, GameEntity.ImgGenState state, string imgUuid = null)
        {
            lock (info.imgGenLock)
            {
                info.imgGenState = state;
                if (state == GameEntity.ImgGenState.FINISHED)
                {
                    info.imgGenProgressAmount = 1f;
                    info.SetImgDirty(b: true);
                    if (imgUuid != null) entity.imgFileNames.Add(imgUuid);
                }
            }
            Utils.MarkEntityAsNeedingImgUpdate(entity.uuid, info);

            // Deliberately not rethrowing on failure: these run as detached background
            // tasks, and an unobserved exception here kills the whole generation queue.
            if (state == GameEntity.ImgGenState.REGULAR_FAILED)
                VertexAIPlugin.Log.LogWarning($"[VertexAI] image generation failed for {entity.name} — see the error above.");
        }

        // ------------------------------------------------------------------
        // Options menu
        // ------------------------------------------------------------------

        public static void Options_Prefix()
        {
            _optionsInProgress = true;
            if (PlayerPrefs.GetInt(VertexAIPlugin.PREF_IMG_ACTIVE, 0) == 1)
            {
                // Options() re-reads the pref to position the dropdown; make sure it still says 98.
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)VertexAIPlugin.VERTEX_IMG_MODE);
                SS.I.imageGenerationMode = VertexAIPlugin.VERTEX_IMG_MODE;
            }
        }

        public static void Options_Postfix(MainMenu __instance)
        {
            try
            {
                VertexAIPlugin.RefreshCache();
                InjectDropdownOption(__instance);

                if (PlayerPrefs.GetInt(VertexAIPlugin.PREF_IMG_ACTIVE, 0) == 1)
                {
                    int ind = IndexOfVertexOption(__instance);
                    if (ind >= 0) __instance.imageGenerationDropdown.SetValueWithoutNotify(ind);
                    SS.I.imageGenerationMode = VertexAIPlugin.VERTEX_IMG_MODE;
                    PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)VertexAIPlugin.VERTEX_IMG_MODE);
                    EnsureSettingsPojo();
                }

                PopulateModelDropdown(__instance);
                ApplyUiState(__instance);
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] image Options postfix failed: {ex}");
            }
            finally
            {
                _optionsInProgress = false;
            }
        }

        public static bool GetImageGenModeByInd_Prefix(int ind, MainMenu __instance, ref SS.ImageGenerationMode __result)
        {
            TMP_Dropdown dropdown = __instance?.imageGenerationDropdown;
            if (dropdown == null || ind < 0 || ind >= dropdown.options.Count) return true;
            if (dropdown.options[ind].text != VertexAIPlugin.IMAGE_OPTION_LABEL) return true;

            __result = VertexAIPlugin.VERTEX_IMG_MODE;
            return false;
        }

        public static void OnImageGenerationDropdownChanged_Postfix(MainMenu __instance)
        {
            if (_optionsInProgress)
            {
                // Options() calls this before our row is injected; its own postfix finishes the job.
                return;
            }
            try
            {
                bool isVertex = IsVertexSelected(__instance);
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_ACTIVE, isVertex ? 1 : 0);
                if (isVertex)
                {
                    PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)VertexAIPlugin.VERTEX_IMG_MODE);
                    SS.I.imageGenerationMode = VertexAIPlugin.VERTEX_IMG_MODE;
                    EnsureSettingsPojo();
                }
                PlayerPrefs.Save();
                VertexAIPlugin.RefreshCache();
                ApplyUiState(__instance);
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] OnImageGenerationDropdownChanged postfix failed: {ex}");
            }
        }

        public static void SaveCurrentPrefs_Postfix(MainMenu __instance)
        {
            // Recovery point: if Options() threw, its postfix never ran and the guard is stuck.
            _optionsInProgress = false;

            if (__instance?.imageGenerationDropdown == null) return;
            try
            {
                bool isVertex = IsVertexSelected(__instance);
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_ACTIVE, isVertex ? 1 : 0);
                if (isVertex)
                {
                    TMP_InputField keyField = __instance.customerKeySlotForImgGen?.inputField;
                    if (keyField != null)
                        PlayerPrefs.SetString(VertexAIPlugin.PREF_API_KEY, (keyField.text ?? "").Trim());

                    VertexImageModel selected = SelectedModel(__instance);
                    if (selected != null)
                    {
                        PlayerPrefs.SetString(VertexAIPlugin.PREF_IMG_MODEL, selected.id);
                        PlayerPrefs.SetString(VertexAIPlugin.PREF_IMG_SIZE, selected.size ?? "");
                    }

                    PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)VertexAIPlugin.VERTEX_IMG_MODE);
                }
                PlayerPrefs.Save();
                VertexAIPlugin.RefreshCache();
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] image SaveCurrentPrefs postfix failed: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // Making mode 98 survive the game's own validation and mode switches
        // ------------------------------------------------------------------

        private static bool _modeWasVertexOnPopulate;

        public static void PopulateSsPrefs_Prefix()
        {
            _modeWasVertexOnPopulate =
                PlayerPrefs.GetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, 8) == (int)VertexAIPlugin.VERTEX_IMG_MODE;
            if (_modeWasVertexOnPopulate)
            {
                // The game resets any mode missing from its own list back to AIRL_FREE.
                // Stand in as AIRL_FREE for the duration, then restore in the postfix.
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)SS.ImageGenerationMode.AIRL_FREE);
            }
        }

        public static void PopulateSsPrefs_Postfix()
        {
            if (!_modeWasVertexOnPopulate) return;
            PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_GEN_MODE, (int)VertexAIPlugin.VERTEX_IMG_MODE);
            PlayerPrefs.SetInt(VertexAIPlugin.PREF_IMG_ACTIVE, 1);
            SS.I.imageGenerationMode = VertexAIPlugin.VERTEX_IMG_MODE;
            EnsureSettingsPojo();
        }

        /// <summary>Skips a mode-switch helper that has no branch for our value.</summary>
        public static bool SkipForVertexMode_Prefix(SS.ImageGenerationMode generationMode)
        {
            return generationMode != VertexAIPlugin.VERTEX_IMG_MODE;
        }

        public static bool GetSettingsPojoByInd_Prefix(SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode != VertexAIPlugin.VERTEX_IMG_MODE) return true;
            // Our preset dropdown holds models, not settings presets, so the original's
            // index lookup would dereference a null list.
            __result = DefaultPojo();
            return false;
        }

        public static bool GetDefaultSettingsPojo_Prefix(SS.ImageGenerationMode imageGenerationMode, ref SettingsPojo __result)
        {
            if (imageGenerationMode != VertexAIPlugin.VERTEX_IMG_MODE) return true;
            __result = DefaultPojo();
            return false;
        }

        public static bool OnImageGenDropdownChanged_Prefix(MainMenu __instance)
        {
            if (!IsVertexSelected(__instance)) return true;
            // The preset dropdown selects a model for us; there is no settings pojo to load.
            VertexImageModel selected = SelectedModel(__instance);
            if (selected != null)
            {
                PlayerPrefs.SetString(VertexAIPlugin.PREF_IMG_MODEL, selected.id);
                PlayerPrefs.SetString(VertexAIPlugin.PREF_IMG_SIZE, selected.size ?? "");
                PlayerPrefs.Save();
                VertexAIPlugin.RefreshCache();
                VertexAIPlugin.Log.LogInfo($"[VertexAI] image model set to {selected.id} ({selected.label}).");
            }
            return false;
        }

        public static bool PopulateImageGenPresetDropdown_Prefix(MainMenu __instance)
        {
            if (!IsVertexSelected(__instance)) return true;
            PopulateModelDropdown(__instance);
            return false;
        }

        public static bool OnCustomerKeyTxtInputChanged_Prefix(string s, MainMenu __instance)
        {
            if (!IsVertexSelected(__instance)) return true;

            // The original mirrors this box into the subscription key fields. Our box holds
            // a Google API key, so only let the two subscription boxes mirror each other.
            GameObject selected = EventSystem.current?.currentSelectedGameObject;
            TMP_InputField imgKeyField = __instance.customerKeySlotForImgGen?.inputField;
            if (imgKeyField != null && selected != null && selected == imgKeyField.gameObject)
            {
                VertexTextPatches.MirrorKeyToTextRow(s);
                return false;
            }

            if (__instance.customerKeyTxtInput != null && __instance.customerKeyTxtInput.gameObject == selected)
                __instance.customerKeyTxtInputForAudioGen?.SetTextWithoutNotify(s);
            if (__instance.customerKeyTxtInputForAudioGen != null && __instance.customerKeyTxtInputForAudioGen.gameObject == selected)
                __instance.customerKeyTxtInput?.SetTextWithoutNotify(s);

            return false;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static SettingsPojo DefaultPojo()
        {
            return SS.I.defaultAirlFreeImgSettings ?? SS.I.defaultWomboSettings ?? SS.I.defaultStableDiffusionSettings;
        }

        /// <summary>
        /// The game only assigns settingsPojo inside per-mode branches, so mode 98 would
        /// otherwise leave it null and every prompt-formatting call would throw.
        /// </summary>
        private static void EnsureSettingsPojo()
        {
            if (SS.I.settingsPojo == null) SS.I.settingsPojo = DefaultPojo();
        }

        private static void InjectDropdownOption(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.imageGenerationDropdown;
            if (dropdown == null) return;
            if (IndexOfVertexOption(menu) >= 0) return;
            dropdown.options.Add(new TMP_Dropdown.OptionData(VertexAIPlugin.IMAGE_OPTION_LABEL));
            dropdown.RefreshShownValue();
        }

        private static void PopulateModelDropdown(MainMenu menu)
        {
            if (menu?.imgGenPresetDropdown == null || !IsVertexSelected(menu)) return;

            menu.imgGenPresetDropdown.ClearOptions();
            menu.imgGenPresetDropdown.AddOptions(VertexAIPlugin.Catalogue.ImageModelLabels());
            menu.imgGenPresetDropdown.SetValueWithoutNotify(
                VertexAIPlugin.Catalogue.IndexOfImageModel(VertexAIPlugin.CachedImageModel, VertexAIPlugin.CachedImageSize));
            menu.imgGenPresetDropdown.RefreshShownValue();
        }

        /// <summary>
        /// Called as the user types in the Vertex text-gen key row. Only mirrors while we
        /// own the image key row, so it can't overwrite a subscription key.
        /// </summary>
        internal static void MirrorKeyToImageRow(string key)
        {
            if (!_tookOverKeyRow) return;
            MainMenu menu = SS.I?.hackyMenu;
            if (menu == null || !IsVertexSelected(menu)) return;
            menu.customerKeySlotForImgGen?.inputField?.SetTextWithoutNotify(key);
        }

        private static VertexImageModel SelectedModel(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.imgGenPresetDropdown;
            if (dropdown == null) return null;
            List<VertexImageModel> models = VertexAIPlugin.Catalogue.imageModels;
            int ind = dropdown.value;
            if (ind < 0 || ind >= models.Count) return null;
            return models[ind];
        }

        private static int IndexOfVertexOption(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.imageGenerationDropdown;
            if (dropdown == null) return -1;
            return dropdown.options.FindIndex(o => o.text == VertexAIPlugin.IMAGE_OPTION_LABEL);
        }

        private static bool IsVertexSelected(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.imageGenerationDropdown;
            if (dropdown == null) return false;
            int ind = dropdown.value;
            return ind >= 0 && ind < dropdown.options.Count
                && dropdown.options[ind].text == VertexAIPlugin.IMAGE_OPTION_LABEL;
        }

        /// <summary>
        /// OnImageGenerationDropdownChanged hides every backend-specific row before its
        /// switch, and has no case for mode 98 — so all we do is re-show the two rows we use.
        /// </summary>
        private static void ApplyUiState(MainMenu menu)
        {
            if (menu == null) return;
            if (!IsVertexSelected(menu))
            {
                RestoreKeyRow(menu);
                return;
            }

            if (menu.imgGenExplanation != null)
            {
                menu.imgGenExplanation.SetText(
                    "Generates images with the Gemini image models on Google Vertex AI (express mode).\n\n" +
                    "<color=#00FF00>Note:</color> paste your Vertex AI express-mode API key below and pick a model. " +
                    "The same key is shared with Vertex AI text generation.", true);
            }

            // Repurpose the subscription key row as our API key row.
            CustomerKeySlot keySlot = menu.customerKeySlotForImgGen;
            if (keySlot != null)
            {
                keySlot.gameObject.SetActive(true);
                HideSubscriptionStatus(keySlot);
                RelabelKeyRow(keySlot, "Vertex AI API Key");
                if (keySlot.inputField != null)
                {
                    if (_originalKeyPlaceholder == null && keySlot.inputField.placeholder is TMP_Text ph)
                        _originalKeyPlaceholder = ph.text;
                    VertexMenuUi.SetPlaceholder(keySlot.inputField, "Paste your Vertex AI express-mode API key");
                    keySlot.inputField.SetTextWithoutNotify(PlayerPrefs.GetString(VertexAIPlugin.PREF_API_KEY, ""));
                }
                _tookOverKeyRow = true;
            }

            // imgGenTweakHolder carries the prompt-format box, which still shapes our prompt.
            VertexMenuUi.SetActive(menu.imgGenTweakHolder, true);
            if (menu.imgGenPresetDropdown != null)
            {
                menu.imgGenPresetDropdown.gameObject.SetActive(true);
                VertexMenuUi.SetActive(menu.imgGenPresetDropdown.transform.parent, true);
            }
        }

        // Captured the first time we take over the shared key row, so switching back to a
        // subscription backend restores exactly what the prefab had.
        private static string _originalKeyLabel;
        private static string _originalKeyPlaceholder;
        private static bool _hidSubStatusRow;
        private static bool _tookOverKeyRow;

        private static void HideSubscriptionStatus(CustomerKeySlot slot)
        {
            TMP_Text subStatus = slot.subStatusTxt;
            if (subStatus == null || _hidSubStatusRow) return;
            SubStatusRow(slot, subStatus).SetActive(false);
            _hidSubStatusRow = true;
        }

        /// <summary>
        /// The status text sometimes sits in a labelled row of its own and sometimes
        /// directly under the slot; hide whichever is the right granularity.
        /// </summary>
        private static GameObject SubStatusRow(CustomerKeySlot slot, TMP_Text subStatus)
        {
            Transform rowParent = subStatus.transform.parent;
            bool inOwnRow = rowParent != null && rowParent != slot.transform;
            return inOwnRow ? rowParent.gameObject : subStatus.gameObject;
        }

        private static void RelabelKeyRow(CustomerKeySlot slot, string label)
        {
            TMP_Text labelText = KeyRowLabel(slot);
            if (labelText == null) return;
            if (_originalKeyLabel == null) _originalKeyLabel = labelText.text;
            labelText.text = label;
        }

        private static TMP_Text KeyRowLabel(CustomerKeySlot slot)
        {
            Transform inputTf = slot.inputField != null ? slot.inputField.transform : null;
            return slot.transform.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(t => (inputTf == null || !t.transform.IsChildOf(inputTf)) && t != slot.subStatusTxt);
        }

        /// <summary>
        /// Undoes our takeover of the shared image-gen key row. Without this, switching
        /// from Vertex to a subscription backend would leave the Google key sitting in a
        /// box labelled "Vertex AI API Key" with the subscription status still hidden.
        /// </summary>
        private static void RestoreKeyRow(MainMenu menu)
        {
            if (!_tookOverKeyRow) return;
            CustomerKeySlot slot = menu.customerKeySlotForImgGen;
            if (slot == null) return;
            _tookOverKeyRow = false;

            if (_originalKeyLabel != null)
            {
                TMP_Text labelText = KeyRowLabel(slot);
                if (labelText != null) labelText.text = _originalKeyLabel;
                _originalKeyLabel = null;
            }

            if (_originalKeyPlaceholder != null)
            {
                VertexMenuUi.SetPlaceholder(slot.inputField, _originalKeyPlaceholder);
                _originalKeyPlaceholder = null;
            }

            if (_hidSubStatusRow && slot.subStatusTxt != null)
            {
                SubStatusRow(slot, slot.subStatusTxt).SetActive(true);
                _hidSubStatusRow = false;
            }

            slot.inputField?.SetTextWithoutNotify(PlayerPrefs.GetString("PREF_KEY_CUSTOMER_KEY2", ""));
        }
    }
}
