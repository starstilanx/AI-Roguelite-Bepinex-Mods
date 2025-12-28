using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace AIROG_GeminiTTS
{
    [HarmonyPatch(typeof(MainMenu))]
    public static class UiPatches
    {
        private static TMP_Dropdown ttsProviderDropdown;
        private static GameObject geminiSettingsGroup;
        private static Slider speakingRateSlider;
        private static TextMeshProUGUI speakingRateText;

        private static bool isInitializing = false;

        [HarmonyPostfix]
        [HarmonyPatch("PopulateOptions")]
        public static void PopulateOptionsPostfix(MainMenu __instance)
        {
            if (ttsProviderDropdown == null)
            {
                CreateUi(__instance);
            }

            isInitializing = true;
            ttsProviderDropdown.value = GeminiTtsPlugin.UseGeminiTts.Value ? 1 : 0;
            UpdateGeminiVisibility();
            isInitializing = false;
            
            // If Gemini is enabled, we need to refresh the voice dropdowns with Gemini voices
            if (GeminiTtsPlugin.UseGeminiTts.Value)
            {
                RefreshVoiceDropdowns(__instance);
            }
        }

        private static void CreateUi(MainMenu __instance)
        {
            // Parent of ttsModeDropdown is likely a content group
            Transform parent = __instance.ttsModeDropdown.transform.parent;
            int index = __instance.ttsModeDropdown.transform.GetSiblingIndex();

            // Create Provider Dropdown
            GameObject providerGo = Object.Instantiate(__instance.ttsModeDropdown.gameObject, parent);
            providerGo.name = "GeminiTTS_Provider_Dropdown";
            providerGo.transform.SetSiblingIndex(index);
            
            ttsProviderDropdown = providerGo.GetComponent<TMP_Dropdown>();
            ttsProviderDropdown.ClearOptions();
            ttsProviderDropdown.AddOptions(new List<string> { "TikTok (Default)", "Google Gemini" });
            
            // Add Label
            Transform labelTransform = providerGo.transform.Find("Label"); // Common structure for TMP_Dropdown
            if (labelTransform == null) labelTransform = providerGo.transform.Find("Label (TMP)");

            // We need a proper label above it. The game usually has TextMeshProUGUI elements as labels.
            // Let's look for a sibling that looks like a label.
            // Usually, they are in a horizontal layout or just vertical list.

            // Just after creating it, add listener
            ttsProviderDropdown.onValueChanged.AddListener(val => {
                if (isInitializing) return;
                GeminiTtsPlugin.UseGeminiTts.Value = (val == 1);
                GeminiTtsPlugin.Instance.Config.Save();
                UpdateGeminiVisibility();
                RefreshVoiceDropdowns(__instance);
            });

            // Create Settings Group/Slider
            CreateSettingsSlider(__instance, parent, index + 2);
        }

        private static void CreateSettingsSlider(MainMenu __instance, Transform parent, int index)
        {
            // Clone ttsVolumeSlider for speaking rate
            if (__instance.ttsVolumeSlider == null) return;

            GameObject sliderGo = Object.Instantiate(__instance.ttsVolumeSlider.gameObject, parent);
            sliderGo.name = "GeminiTTS_SpeakingRate_Slider";
            sliderGo.transform.SetSiblingIndex(index);
            
            geminiSettingsGroup = sliderGo;
            speakingRateSlider = sliderGo.GetComponent<Slider>();
            speakingRateSlider.minValue = 0.25f;
            speakingRateSlider.maxValue = 4.0f;
            speakingRateSlider.value = GeminiTtsPlugin.SpeakingRate.Value;
            
            speakingRateSlider.onValueChanged.AddListener(val => {
                if (isInitializing) return;
                GeminiTtsPlugin.SpeakingRate.Value = val;
                GeminiTtsPlugin.Instance.Config.Save();
                if (speakingRateText != null) speakingRateText.text = $"Gemini Speed: {val:F2}";
            });

            // Try to find or add a text label
            speakingRateText = sliderGo.GetComponentInChildren<TextMeshProUGUI>();
            if (speakingRateText != null)
            {
                speakingRateText.text = $"Gemini Speed: {GeminiTtsPlugin.SpeakingRate.Value:F2}";
            }
        }

        private static void UpdateGeminiVisibility()
        {
            if (geminiSettingsGroup != null)
            {
                geminiSettingsGroup.SetActive(GeminiTtsPlugin.UseGeminiTts.Value);
            }
        }

        private static void RefreshVoiceDropdowns(MainMenu __instance)
        {
            if (!GeminiTtsPlugin.UseGeminiTts.Value)
            {
                RestoreTikTokVoices(__instance);
                return;
            }

            Dictionary<string, string> geminiVoices = new Dictionary<string, string>
            {
                { "Charon", "Gemini: Charon (Narrator)" },
                { "Kore", "Gemini: Kore (Male 1)" },
                { "Aoede", "Gemini: Aoede (Female 1)" },
                { "Fenrir", "Gemini: Fenrir (Monster)" },
                { "Puck", "Gemini: Puck (Robot)" }
            };

            UpdateDropdown(__instance.ttsVoiceNarrationDropdown, geminiVoices, GeminiTtsPlugin.VoiceNarration);
            UpdateDropdown(__instance.ttsVoiceMaleDropdown, geminiVoices, GeminiTtsPlugin.VoiceMale);
            UpdateDropdown(__instance.ttsVoiceFemaleDropdown, geminiVoices, GeminiTtsPlugin.VoiceFemale);
            UpdateDropdown(__instance.ttsVoiceMonsterDropdown, geminiVoices, GeminiTtsPlugin.VoiceMonster);
            UpdateDropdown(__instance.ttsVoiceRoboticDropdown, geminiVoices, GeminiTtsPlugin.VoiceRobot);
            UpdateDropdown(__instance.ttsVoiceEnemyDropdown, geminiVoices, GeminiTtsPlugin.VoiceEnemy);
        }

        private static void RestoreTikTokVoices(MainMenu __instance)
        {
            var dictField = AccessTools.Field(typeof(MainMenu), "tiktokVoiceNameToUiStringDict");
            if (dictField == null) return;
            var dict = (Dictionary<string, string>)dictField.GetValue(__instance);

            // Directly call the private method using reflection or just re-implement the logic
            // Since we're in a Patch class, we can just use the public dropdowns
            PopulateTikTok(__instance, __instance.ttsVoiceNarrationDropdown, dict, "PREF_KEY_VOICE_TIKTOK_NARRATION", "en_male_ghosthost");
            PopulateTikTok(__instance, __instance.ttsVoiceMaleDropdown, dict, "PREF_KEY_VOICE_TIKTOK_MALE", "en_uk_003");
            PopulateTikTok(__instance, __instance.ttsVoiceFemaleDropdown, dict, "PREF_KEY_VOICE_TIKTOK_FEMALE", "en_female_emotional");
            PopulateTikTok(__instance, __instance.ttsVoiceMonsterDropdown, dict, "PREF_KEY_VOICE_TIKTOK_MONSTER", "en_us_ghostface");
            PopulateTikTok(__instance, __instance.ttsVoiceRoboticDropdown, dict, "PREF_KEY_VOICE_TIKTOK_ROBOTIC", "en_us_c3po");
            PopulateTikTok(__instance, __instance.ttsVoiceEnemyDropdown, dict, "PREF_KEY_VOICE_TIKTOK_ENEMY", "en_us_ghostface");
        }

        private static void PopulateTikTok(MainMenu instance, TMP_Dropdown dropdown, Dictionary<string, string> dict, string prefKey, string fallback)
        {
            // Use reflection to call the private PopulateVoiceDropdownOptions
            var method = AccessTools.Method(typeof(MainMenu), "PopulateVoiceDropdownOptions");
            method.Invoke(instance, new object[] { dropdown, dict, PlayerPrefs.GetString(prefKey, fallback), fallback });
            
            // Re-add original listeners? Actually the game adds listeners in its own way.
            // But when we switch back, we want to make sure the game's original logic is what's triggered on change.
        }

        private static void UpdateDropdown(TMP_Dropdown dropdown, Dictionary<string, string> voices, ConfigEntry<string> config)
        {
            if (dropdown == null) return;
            dropdown.ClearOptions();
            dropdown.AddOptions(voices.Values.ToList());
            
            int index = voices.Keys.ToList().IndexOf(config.Value);
            if (index == -1) index = 0;
            dropdown.SetValueWithoutNotify(index);

            // Add listener to save back to BepInEx config
            dropdown.onValueChanged.RemoveAllListeners();
            dropdown.onValueChanged.AddListener(val => {
                config.Value = voices.Keys.ToList()[val];
                GeminiTtsPlugin.Instance.Config.Save();
            });
        }

        [HarmonyPrefix]
        [HarmonyPatch("SaveOptions")]
        public static void SaveOptionsPrefix(MainMenu __instance)
        {
            // Ensure our configs are saved when the user clicks save
            GeminiTtsPlugin.Instance.Config.Save();
        }
    }
}
