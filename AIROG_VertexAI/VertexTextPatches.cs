using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Wires "Vertex AI (Google)" into the text-generation dropdown and redirects the
    /// resulting requests away from OpenaiApiClient.
    ///
    /// The injected row reports itself as OPENAI_API to the game, which is what keeps
    /// mode-keyed lookups such as SS.I.summaryceptionByMode and
    /// Utils.GetFullPromptStrCharLimit working — a genuinely new enum value would throw a
    /// KeyNotFoundException the first time the game sized a prompt. PREF_TEXT_ACTIVE is
    /// what actually distinguishes "Vertex" from "some OpenAI-compatible server".
    /// </summary>
    internal static class VertexTextPatches
    {
        private static GameObject _keyRow;
        private static TMP_InputField _keyField;
        private static GameObject _modelRow;
        private static TMP_Dropdown _modelDropdown;
        private static MainMenu _boundMenu;

        /// <summary>
        /// Options() rebuilds the dropdown from scratch and fires
        /// OnTextGenerationDropdownChanged before our row exists. Without this guard that
        /// callback would see "Vertex not selected" and clear the saved preference.
        /// </summary>
        private static bool _optionsInProgress;
        private static bool _wasActiveOnOptionsOpen;

        public static void Register(VertexAIPlugin plugin)
        {
            var m = typeof(VertexTextPatches);

            plugin.TryPatch(typeof(MainMenu), "Options",
                prefix: new HarmonyMethod(m, nameof(Options_Prefix)),
                postfix: new HarmonyMethod(m, nameof(Options_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "GetTextGenerationModeByDropdownInd",
                prefix: new HarmonyMethod(m, nameof(GetTextGenModeByInd_Prefix)));

            plugin.TryPatch(typeof(MainMenu), "OnTextGenerationDropdownChanged",
                postfix: new HarmonyMethod(m, nameof(OnTextGenChanged_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "SaveCurrentPrefs",
                postfix: new HarmonyMethod(m, nameof(SaveCurrentPrefs_Postfix)));

            plugin.TryPatch(typeof(MainMenu), "UpdateKeysVisibilityBasedOnPref",
                postfix: new HarmonyMethod(m, nameof(UpdateKeysVisibility_Postfix)));

            plugin.TryPatch(typeof(OpenaiApiClient), "GetGeneratedTextChatgpt",
                prefix: new HarmonyMethod(m, nameof(GetGeneratedTextChatgpt_Prefix)));

            plugin.TryPatch(typeof(OpenaiApiClient), "GetGeneratedText",
                prefix: new HarmonyMethod(m, nameof(GetGeneratedText_Prefix)),
                argTypes: new[] { typeof(string), typeof(int), typeof(double), typeof(double) });
        }

        // ------------------------------------------------------------------
        // Generation interception
        // ------------------------------------------------------------------

        public static bool GetGeneratedTextChatgpt_Prefix(
            AIAsker.ChatGptPromptType chatGptPromptType, string userMsg, List<string> allDisallowedTokens,
            string langOverride, FirebaseClient.HighCostMode hcMode, InteractionInfo interactionInfo,
            CancellationToken ct, ref Task<string> __result)
        {
            if (!VertexAIPlugin.TextBackendActive) return true;
            __result = VertexTextClient.GenerateText(chatGptPromptType, userMsg, langOverride, hcMode, interactionInfo, ct);
            return false;
        }

        public static bool GetGeneratedText_Prefix(
            string prompt, int maxTokens, double temperature, ref Task<string> __result)
        {
            if (!VertexAIPlugin.TextBackendActive) return true;
            __result = VertexTextClient.GenerateCompletion(prompt, maxTokens, temperature, Gcm.CurrentToken);
            return false;
        }

        // ------------------------------------------------------------------
        // Options menu
        // ------------------------------------------------------------------

        public static void Options_Prefix()
        {
            _optionsInProgress = true;
            _wasActiveOnOptionsOpen = PlayerPrefs.GetInt(VertexAIPlugin.PREF_TEXT_ACTIVE, 0) == 1;
        }

        public static void Options_Postfix(MainMenu __instance)
        {
            try
            {
                VertexAIPlugin.RefreshCache();
                EnsureRows(__instance);
                InjectDropdownOption(__instance);
                PopulateModelDropdown();

                if (_keyField != null)
                    _keyField.SetTextWithoutNotify(PlayerPrefs.GetString(VertexAIPlugin.PREF_API_KEY, ""));

                if (_wasActiveOnOptionsOpen)
                {
                    int ind = IndexOfVertexOption(__instance);
                    if (ind >= 0) __instance.textGenerationDropdown.SetValueWithoutNotify(ind);
                    SS.I.textGenerationMode = SS.TextGenerationMode.OPENAI_API;
                }

                ApplyUiState(__instance);
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] Options postfix failed: {ex}");
            }
            finally
            {
                _optionsInProgress = false;
            }
        }

        public static bool GetTextGenModeByInd_Prefix(int ind, MainMenu __instance, ref SS.TextGenerationMode __result)
        {
            TMP_Dropdown dropdown = __instance?.textGenerationDropdown;
            if (dropdown == null || ind < 0 || ind >= dropdown.options.Count) return true;
            if (dropdown.options[ind].text != VertexAIPlugin.TEXT_OPTION_LABEL) return true;

            // Our row lives past the end of the game's txtGenDropdownOptionsList, so the
            // original would throw on ElementAt(ind).
            __result = SS.TextGenerationMode.OPENAI_API;
            return false;
        }

        public static void OnTextGenChanged_Postfix(MainMenu __instance)
        {
            if (_optionsInProgress) return;
            try
            {
                bool isVertex = IsVertexSelected(__instance);
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_TEXT_ACTIVE, isVertex ? 1 : 0);
                if (isVertex)
                {
                    PlayerPrefs.SetInt("PREF_KEY_TEXT_GENERATION_MODE2", (int)SS.TextGenerationMode.OPENAI_API);
                    SS.I.textGenerationMode = SS.TextGenerationMode.OPENAI_API;
                }
                PlayerPrefs.Save();
                VertexAIPlugin.RefreshCache();
                ApplyUiState(__instance);
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] OnTextGenerationDropdownChanged postfix failed: {ex}");
            }
        }

        public static void SaveCurrentPrefs_Postfix(MainMenu __instance)
        {
            // Harmony postfixes are skipped when the original throws, so a fault inside
            // Options() would strand the guard and mute our dropdown handler. Leaving the
            // options screen is a safe point to clear it.
            _optionsInProgress = false;

            if (__instance?.textGenerationDropdown == null) return;
            try
            {
                bool isVertex = IsVertexSelected(__instance);
                PlayerPrefs.SetInt(VertexAIPlugin.PREF_TEXT_ACTIVE, isVertex ? 1 : 0);

                if (isVertex)
                {
                    if (_keyField != null)
                        PlayerPrefs.SetString(VertexAIPlugin.PREF_API_KEY, (_keyField.text ?? "").Trim());

                    VertexTextModel selected = SelectedModel();
                    if (selected != null)
                        PlayerPrefs.SetString(VertexAIPlugin.PREF_TEXT_MODEL, selected.id);

                    // The game already wrote this via our GetTextGenerationModeByDropdownInd
                    // prefix; restating it keeps the two prefs consistent if that ever changes.
                    PlayerPrefs.SetInt("PREF_KEY_TEXT_GENERATION_MODE2", (int)SS.TextGenerationMode.OPENAI_API);
                }

                PlayerPrefs.Save();
                VertexAIPlugin.RefreshCache();
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] SaveCurrentPrefs postfix failed: {ex}");
            }
        }

        public static void UpdateKeysVisibility_Postfix(MainMenu __instance)
        {
            if (_keyField == null || __instance == null) return;
            _keyField.contentType = __instance.keysVisible
                ? TMP_InputField.ContentType.Standard
                : TMP_InputField.ContentType.Password;
            _keyField.ForceLabelUpdate();
        }

        // ------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------

        private static void EnsureRows(MainMenu menu)
        {
            if (_boundMenu == menu && _keyField != null && _modelDropdown != null) return;

            _boundMenu = menu;
            _keyRow = null; _keyField = null;
            _modelRow = null; _modelDropdown = null;

            GameObject keyRow = VertexMenuUi.CloneRow(menu.openaiApiAuthTransform, "VertexApiKeyRow");
            if (keyRow != null)
            {
                _keyRow = keyRow;
                _keyField = keyRow.GetComponentInChildren<TMP_InputField>(true);
                VertexMenuUi.SetRowLabel(keyRow, "Vertex AI API Key");
                VertexMenuUi.SetPlaceholder(_keyField, "Paste your Vertex AI express-mode API key");
                // Text and images share one express-mode key. Mirroring as the user types
                // keeps both rows in agreement, so whichever SaveCurrentPrefs postfix runs
                // last can't persist a stale copy.
                _keyField?.onValueChanged.AddListener(VertexImagePatches.MirrorKeyToImageRow);
            }

            GameObject modelRow = VertexMenuUi.CloneRow(menu.koboldApiDropdownHolderTransform, "VertexModelRow");
            if (modelRow != null)
            {
                _modelRow = modelRow;
                _modelDropdown = modelRow.GetComponentInChildren<TMP_Dropdown>(true);
                VertexMenuUi.SetRowLabel(modelRow, "Vertex AI Model");
            }

            if (_keyField == null || _modelDropdown == null)
                VertexAIPlugin.Log.LogError($"[VertexAI] Could not build Options rows " +
                                            $"(key={_keyField != null}, model={_modelDropdown != null}). " +
                                            "The backend still works via PlayerPrefs, but is not configurable in-game.");

            VertexMenuUi.SetActive(_keyRow, false);
            VertexMenuUi.SetActive(_modelRow, false);
        }

        private static void InjectDropdownOption(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.textGenerationDropdown;
            if (dropdown == null) return;
            if (IndexOfVertexOption(menu) >= 0) return;
            dropdown.options.Add(new TMP_Dropdown.OptionData(VertexAIPlugin.TEXT_OPTION_LABEL));
            dropdown.RefreshShownValue();
        }

        private static void PopulateModelDropdown()
        {
            if (_modelDropdown == null) return;
            _modelDropdown.ClearOptions();
            _modelDropdown.AddOptions(VertexAIPlugin.Catalogue.TextModelLabels());
            _modelDropdown.SetValueWithoutNotify(
                VertexAIPlugin.Catalogue.IndexOfTextModel(VertexAIPlugin.CachedTextModel));
            _modelDropdown.RefreshShownValue();
        }

        /// <summary>Called by the image patches when the user edits the shared key in the image row.</summary>
        internal static void MirrorKeyToTextRow(string key)
        {
            _keyField?.SetTextWithoutNotify(key);
        }

        private static VertexTextModel SelectedModel()
        {
            if (_modelDropdown == null) return null;
            List<VertexTextModel> models = VertexAIPlugin.Catalogue.textModels;
            int ind = _modelDropdown.value;
            if (ind < 0 || ind >= models.Count) return null;
            return models[ind];
        }

        private static int IndexOfVertexOption(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.textGenerationDropdown;
            if (dropdown == null) return -1;
            return dropdown.options.FindIndex(o => o.text == VertexAIPlugin.TEXT_OPTION_LABEL);
        }

        private static bool IsVertexSelected(MainMenu menu)
        {
            TMP_Dropdown dropdown = menu?.textGenerationDropdown;
            if (dropdown == null) return false;
            int ind = dropdown.value;
            return ind >= 0 && ind < dropdown.options.Count
                && dropdown.options[ind].text == VertexAIPlugin.TEXT_OPTION_LABEL;
        }

        /// <summary>
        /// Swaps the OPENAI_API rows (url / auth / model) the game just revealed for our
        /// own key + model rows. Runs after the game's own handler, which has already set
        /// the row visibility for whichever mode it thinks is active.
        /// </summary>
        private static void ApplyUiState(MainMenu menu)
        {
            if (menu == null) return;
            bool isVertex = IsVertexSelected(menu);

            VertexMenuUi.SetActive(_keyRow, isVertex);
            VertexMenuUi.SetActive(_modelRow, isVertex);

            if (!isVertex) return;

            // The game routed us through its OPENAI_API branch, so these three are showing.
            VertexMenuUi.SetActive(menu.openaiApiUrlTransform, false);
            VertexMenuUi.SetActive(menu.openaiApiAuthTransform, false);
            VertexMenuUi.SetActive(menu.openaiApiModelTransform, false);

            if (menu.textGenExplanation != null)
            {
                menu.textGenExplanation.SetText(
                    "Generates text using Google Vertex AI in express mode.\n\n" +
                    "<color=#00FF00>Note:</color> paste your Vertex AI express-mode API key below and pick a model. " +
                    "No Google Cloud project or region is needed. Edit " +
                    VertexCatalogue.FILE_NAME + " in BepInEx/config to change the model list.", true);
            }
        }
    }
}
