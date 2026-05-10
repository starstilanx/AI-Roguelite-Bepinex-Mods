using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

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
        [HarmonyPatch("Options")]
        public static void OptionsPostfix(MainMenu __instance)
        {
            if (ttsProviderDropdown == null)
            {
                CreateUi(__instance);
            }

            isInitializing = true;
            ttsProviderDropdown.value = GeminiTtsPlugin.UseGeminiTts.Value ? 1 : 0;
            UpdateGeminiVisibility();
            isInitializing = false;
        }

        private static void CreateUi(MainMenu __instance)
        {
            Transform parent = __instance.ttsOptions.ttsModeDropdown.transform.parent;
            int index = __instance.ttsOptions.ttsModeDropdown.transform.GetSiblingIndex();

            // Create Provider Dropdown
            GameObject providerGo = Object.Instantiate(__instance.ttsOptions.ttsModeDropdown.gameObject, parent);
            providerGo.name = "GeminiTTS_Provider_Dropdown";
            providerGo.transform.SetSiblingIndex(index);
            
            ttsProviderDropdown = providerGo.GetComponent<TMP_Dropdown>();
            ttsProviderDropdown.ClearOptions();
            ttsProviderDropdown.AddOptions(new List<string> { "TikTok (Default)", "Google Gemini" });
            ttsProviderDropdown.onValueChanged.AddListener(val => {
                if (isInitializing) return;
                GeminiTtsPlugin.UseGeminiTts.Value = (val == 1);
                GeminiTtsPlugin.Instance.Config.Save();
                UpdateGeminiVisibility();
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

        [HarmonyPrefix]
        [HarmonyPatch("SaveCurrentPrefs")]
        public static void SaveCurrentPrefsPrefix(MainMenu __instance)
        {
            GeminiTtsPlugin.Instance.Config.Save();
        }
    }
}
