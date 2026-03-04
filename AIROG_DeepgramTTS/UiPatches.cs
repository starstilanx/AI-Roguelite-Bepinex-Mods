using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace AIROG_DeepgramTTS
{
    [HarmonyPatch(typeof(MainMenu))]
    public static class UiPatches
    {
        private static TMP_Dropdown ttsProviderDropdown;
        private static GameObject deepgramSettingsGroup;
        private static TMP_InputField apiKeyInput;
        private static TextMeshProUGUI apiKeyLabel;

        private static bool isInitializing = false;

        [HarmonyPostfix]
        [HarmonyPatch("Options")]
        public static void OptionsPostfix(MainMenu __instance)
        {
            Debug.Log("[DeepgramTTS] Options Postfix Triggered");
            if (ttsProviderDropdown == null)
            {
                Debug.Log("[DeepgramTTS] Creating UI...");
                CreateUi(__instance);
            }

            isInitializing = true;
            UpdateProviderDropdownValue();
            UpdateDeepgramVisibility();
            isInitializing = false;
            
            if (DeepgramTtsPlugin.UseDeepgramTts.Value)
            {
                Debug.Log("[DeepgramTTS] Refreshing voice dropdowns");
                RefreshVoiceDropdowns(__instance);
            }
        }

        private static void CreateUi(MainMenu __instance)
        {
            Transform parent = __instance.ttsModeDropdown.transform.parent;
            int index = __instance.ttsModeDropdown.transform.GetSiblingIndex();

            // Create Provider Dropdown (following GeminiTTS pattern)
            GameObject providerGo = Object.Instantiate(__instance.ttsModeDropdown.gameObject, parent);
            providerGo.name = "DeepgramTTS_Provider_Dropdown";
            providerGo.transform.SetSiblingIndex(index);
            
            ttsProviderDropdown = providerGo.GetComponent<TMP_Dropdown>();
            ttsProviderDropdown.onValueChanged.RemoveAllListeners();
            ttsProviderDropdown.ClearOptions();
            
            // If Gemini is active, we might see it here, but they use their own static field.
            // For now, let's just add Deepgram.
            
            ttsProviderDropdown.AddOptions(new List<string> { "TikTok (Default)", "Deepgram Aura" });
            
            ttsProviderDropdown.onValueChanged.AddListener(val => {
                if (isInitializing) return;
                DeepgramTtsPlugin.UseDeepgramTts.Value = (val == 1);
                DeepgramTtsPlugin.Instance.Config.Save();
                UpdateDeepgramVisibility();
                RefreshVoiceDropdowns(__instance);
            });

            // Create API Key Input using the full transform (likely has label + input)
            CreateApiKeyInput(__instance, parent, index + 1);
        }

        private static void CreateApiKeyInput(MainMenu __instance, Transform parent, int index)
        {
            // Use the Trans version as it usually includes the label
            GameObject template = __instance.customerKeyTxtInputForAudioGenTrans != null ? 
                __instance.customerKeyTxtInputForAudioGenTrans.gameObject : 
                __instance.customerKeyTxtInputForAudioGen.gameObject;

            if (template == null)
            {
                Debug.LogError("[DeepgramTTS] Could not find template for API Key Input!");
                return;
            }

            GameObject keyGo = Object.Instantiate(template, parent);
            keyGo.name = "DeepgramTTS_ApiKey_Group";
            keyGo.transform.SetSiblingIndex(index);
            
            // SANITIZATION: Remove any logic scripts attached to the template 
            // that might be saving to the wrong PlayerPrefs key (e.g. Sapphire's key).
            var scripts = keyGo.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var script in scripts)
            {
                // Keep standard UI components
                if (script is TMP_InputField || script is TextMeshProUGUI || 
                    script is UnityEngine.UI.Image || script is UnityEngine.UI.Graphic ||
                    script is UnityEngine.UI.Selectable || script is UnityEngine.EventSystems.UIBehaviour)
                {
                    continue;
                }
                
                // Destroy everything else (custom logic scripts)
                Debug.Log($"[DeepgramTTS] Destroying helper script on clone: {script.GetType().Name}");
                Object.DestroyImmediate(script);
            }

            deepgramSettingsGroup = keyGo;
            apiKeyInput = keyGo.GetComponentInChildren<TMP_InputField>();
            
            if (apiKeyInput != null)
            {
                apiKeyInput.name = "DeepgramApiKeyInput"; // Rename to avoid finding by name "CustomerKeyInput"

                // Create new events to clear ALL listeners (including persistent ones set in Editor)
                apiKeyInput.onValueChanged = new TMP_InputField.OnChangeEvent();
                apiKeyInput.onEndEdit = new TMP_InputField.SubmitEvent();
                apiKeyInput.onSelect = new TMP_InputField.SelectionEvent();
                apiKeyInput.onDeselect = new TMP_InputField.SelectionEvent();
                apiKeyInput.onSubmit = new TMP_InputField.SubmitEvent();
                
                apiKeyInput.contentType = TMP_InputField.ContentType.Standard; 
                apiKeyInput.text = DeepgramTtsPlugin.DeepgramApiKey.Value;
                
                apiKeyInput.onValueChanged.AddListener(val => {
                    if (isInitializing) return;
                    DeepgramTtsPlugin.DeepgramApiKey.Value = val;
                    DeepgramTtsPlugin.Instance.Config.Save();
                });

                var placeholder = apiKeyInput.placeholder as TextMeshProUGUI;
                if (placeholder != null)
                {
                    placeholder.text = "Enter Deepgram API Key...";
                }
            }

            // Find and update the label text
            TextMeshProUGUI label = keyGo.GetComponentInChildren<TextMeshProUGUI>();
            // If the first TMP found is the placeholder, look for another one in children
            if (label != null && apiKeyInput != null && label == apiKeyInput.placeholder)
            {
                var allTops = keyGo.GetComponentsInChildren<TextMeshProUGUI>();
                label = allTops.FirstOrDefault(t => t != apiKeyInput.placeholder && t != apiKeyInput.textComponent);
            }

            if (label != null)
            {
                label.text = "Deepgram API Key";
            }
        }

        private static void UpdateProviderDropdownValue()
        {
            if (ttsProviderDropdown == null) return;
            ttsProviderDropdown.SetValueWithoutNotify(DeepgramTtsPlugin.UseDeepgramTts.Value ? 1 : 0);
        }

        private static void UpdateDeepgramVisibility()
        {
            if (deepgramSettingsGroup != null)
            {
                deepgramSettingsGroup.SetActive(DeepgramTtsPlugin.UseDeepgramTts.Value);
            }
        }

        private static void RefreshVoiceDropdowns(MainMenu __instance)
        {
            if (!DeepgramTtsPlugin.UseDeepgramTts.Value)
            {
                RestoreTikTokVoices(__instance);
                return;
            }

            Dictionary<string, string> deepgramVoices = new Dictionary<string, string>
            {
                { "aura-2-thalia-en", "Deepgram: Thalia (Female)" },
                { "aura-2-andromeda-en", "Deepgram: Andromeda (Female)" },
                { "aura-2-helena-en", "Deepgram: Helena (Female)" },
                { "aura-2-apollo-en", "Deepgram: Apollo (Male)" },
                { "aura-2-arcas-en", "Deepgram: Arcas (Male)" },
                { "aura-2-aries-en", "Deepgram: Aries (Male)" },
                { "aura-2-atlas-en", "Deepgram: Atlas (Male)" },
                { "aura-2-aurora-en", "Deepgram: Aurora (Female)" }
            };

            UpdateDropdown(__instance.ttsVoiceNarrationDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceNarration);
            UpdateDropdown(__instance.ttsVoiceMaleDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceMale);
            UpdateDropdown(__instance.ttsVoiceFemaleDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceFemale);
            UpdateDropdown(__instance.ttsVoiceMonsterDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceMonster);
            UpdateDropdown(__instance.ttsVoiceRoboticDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceRobot);
            UpdateDropdown(__instance.ttsVoiceEnemyDropdown, deepgramVoices, DeepgramTtsPlugin.VoiceEnemy);
        }

        private static void RestoreTikTokVoices(MainMenu __instance)
        {
            var dictField = AccessTools.Field(typeof(MainMenu), "tiktokVoiceNameToUiStringDict");
            if (dictField == null) return;
            var dict = (Dictionary<string, string>)dictField.GetValue(__instance);

            PopulateTikTok(__instance, __instance.ttsVoiceNarrationDropdown, dict, "PREF_KEY_VOICE_TIKTOK_NARRATION", "en_male_ghosthost");
            PopulateTikTok(__instance, __instance.ttsVoiceMaleDropdown, dict, "PREF_KEY_VOICE_TIKTOK_MALE", "en_uk_003");
            PopulateTikTok(__instance, __instance.ttsVoiceFemaleDropdown, dict, "PREF_KEY_VOICE_TIKTOK_FEMALE", "en_female_emotional");
            PopulateTikTok(__instance, __instance.ttsVoiceMonsterDropdown, dict, "PREF_KEY_VOICE_TIKTOK_MONSTER", "en_us_ghostface");
            PopulateTikTok(__instance, __instance.ttsVoiceRoboticDropdown, dict, "PREF_KEY_VOICE_TIKTOK_ROBOTIC", "en_us_c3po");
            PopulateTikTok(__instance, __instance.ttsVoiceEnemyDropdown, dict, "PREF_KEY_VOICE_TIKTOK_ENEMY", "en_us_ghostface");
        }

        private static void PopulateTikTok(MainMenu instance, TMP_Dropdown dropdown, Dictionary<string, string> dict, string prefKey, string fallback)
        {
            var method = AccessTools.Method(typeof(MainMenu), "PopulateVoiceDropdownOptions");
            if (method == null) return;
            method.Invoke(instance, new object[] { dropdown, dict, PlayerPrefs.GetString(prefKey, fallback), fallback });
        }

        private static void UpdateDropdown(TMP_Dropdown dropdown, Dictionary<string, string> voices, ConfigEntry<string> config)
        {
            if (dropdown == null) return;
            dropdown.ClearOptions();
            dropdown.AddOptions(voices.Values.ToList());
            
            int index = voices.Keys.ToList().IndexOf(config.Value);
            if (index == -1) index = 0;
            dropdown.SetValueWithoutNotify(index);

            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.onValueChanged.AddListener(val => {
                config.Value = voices.Keys.ToList()[val];
                DeepgramTtsPlugin.Instance.Config.Save();
            });
        }

        [HarmonyPrefix]
        [HarmonyPatch("SaveCurrentPrefs")]
        public static void SaveOptionsPrefix(MainMenu __instance)
        {
            DeepgramTtsPlugin.Instance.Config.Save();
        }
    }
}
