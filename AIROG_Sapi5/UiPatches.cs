using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using System.Speech.Synthesis;

namespace AIROG_Sapi5
{
    [HarmonyPatch(typeof(MainMenu))]
    public static class UiPatches
    {
        private static TMP_Dropdown ttsProviderDropdown;
        private static bool isInitializing = false;
        private static List<string> cachedVoiceNames;

        [HarmonyPostfix]
        [HarmonyPatch("Options")]
        public static void OptionsPostfix(MainMenu __instance)
        {
            Debug.Log("[SAPI5] OptionsPostfix triggered");
            
            if (ttsProviderDropdown == null)
            {
                Debug.Log("[SAPI5] Creating TTS Provider UI...");
                CreateUi(__instance);
            }

            isInitializing = true;
            UpdateProviderDropdownValue();
            isInitializing = false;
            
            if (Sapi5Plugin.UseSapi5.Value)
            {
                Debug.Log("[SAPI5] SAPI5 is enabled, refreshing voice dropdowns");
                RefreshVoiceDropdowns(__instance);
            }
        }

        private static void CreateUi(MainMenu __instance)
        {
            if (__instance.ttsModeDropdown == null)
            {
                Debug.LogError("[SAPI5] ttsModeDropdown is null, cannot create UI");
                return;
            }

            Transform parent = __instance.ttsModeDropdown.transform.parent;
            int index = __instance.ttsModeDropdown.transform.GetSiblingIndex();
            
            Debug.Log($"[SAPI5] ttsModeDropdown parent: {parent.name}, sibling index: {index}, total children: {parent.childCount}");

            // Clone the TTS Mode dropdown to create our provider dropdown
            GameObject providerGo = Object.Instantiate(__instance.ttsModeDropdown.gameObject, parent);
            providerGo.name = "Sapi5_Provider_Dropdown";
            
            // Place after the TTS Mode dropdown (index + 1)
            providerGo.transform.SetSiblingIndex(index + 1);
            
            Debug.Log($"[SAPI5] Created dropdown, set sibling index to: {index + 1}");
            
            ttsProviderDropdown = providerGo.GetComponent<TMP_Dropdown>();
            if (ttsProviderDropdown == null)
            {
                Debug.LogError("[SAPI5] Failed to get TMP_Dropdown component");
                return;
            }

            ttsProviderDropdown.onValueChanged.RemoveAllListeners();
            ttsProviderDropdown.ClearOptions();
            
            // Add TTS Provider options
            ttsProviderDropdown.AddOptions(new List<string> { "TikTok (Default)", "SAPI5 (Windows)" });
            
            // Find and update the label text if there is one
            var labels = providerGo.GetComponentsInChildren<TextMeshProUGUI>(true);
            Debug.Log($"[SAPI5] Found {labels.Length} TextMeshProUGUI components in dropdown");
            foreach (var label in labels)
            {
                Debug.Log($"[SAPI5] - Label: '{label.text}' (name: {label.gameObject.name})");
            }
            
            // Set position - move UP to appear above the Enable TTS dropdown
            RectTransform rt = providerGo.GetComponent<RectTransform>();
            RectTransform ttsModeRt = __instance.ttsModeDropdown.GetComponent<RectTransform>();
            if (rt != null && ttsModeRt != null)
            {
                Vector3 pos = ttsModeRt.localPosition;
                Debug.Log($"[SAPI5] ttsModeDropdown position: {pos}");
                // Move UP by the height of one row (approximately 30-35 pixels)
                rt.localPosition = new Vector3(pos.x, pos.y + 35, pos.z);
                Debug.Log($"[SAPI5] SAPI5 dropdown new position: {rt.localPosition}");
            }
            
            // Make sure it's active
            providerGo.SetActive(true);
            
            ttsProviderDropdown.onValueChanged.AddListener(val => {
                if (isInitializing) return;
                Debug.Log($"[SAPI5] Provider dropdown changed to: {val}");
                Sapi5Plugin.UseSapi5.Value = (val == 1);
                Sapi5Plugin.Instance.Config.Save();
                
                // Find the MainMenu instance again since we're in a callback
                var mainMenu = Object.FindObjectOfType<MainMenu>();
                if (mainMenu != null)
                {
                    if (Sapi5Plugin.UseSapi5.Value)
                    {
                        RefreshVoiceDropdowns(mainMenu);
                    }
                    else
                    {
                        RestoreTikTokVoices(mainMenu);
                    }
                }
            });

            // Set the initial value
            UpdateProviderDropdownValue();
            
            Debug.Log("[SAPI5] UI Created successfully");
        }

        private static void UpdateProviderDropdownValue()
        {
            if (ttsProviderDropdown == null) return;
            ttsProviderDropdown.SetValueWithoutNotify(Sapi5Plugin.UseSapi5.Value ? 1 : 0);
        }

        private static void RefreshVoiceDropdowns(MainMenu __instance)
        {
            if (cachedVoiceNames == null)
            {
                cachedVoiceNames = new List<string>();
                try {
                    using (SpeechSynthesizer synth = new SpeechSynthesizer())
                    {
                        var voices = synth.GetInstalledVoices();
                        if (voices != null)
                        {
                            Debug.Log($"[SAPI5] GetInstalledVoices() returned {voices.Count} voices");
                            foreach (var v in voices)
                            {
                                if (v != null && v.VoiceInfo != null && !string.IsNullOrEmpty(v.VoiceInfo.Name))
                                {
                                    cachedVoiceNames.Add(v.VoiceInfo.Name);
                                    Debug.Log($"[SAPI5] Found voice: {v.VoiceInfo.Name}");
                                }
                            }
                        }
                    }
                } catch (System.Exception e) {
                    Debug.LogWarning($"[SAPI5] Dynamic voice detection failed: {e.GetType().Name}: {e.Message}");
                }
                
                // Fallback: Use common Windows SAPI5 voices if detection failed
                if (cachedVoiceNames.Count == 0)
                {
                    Debug.Log("[SAPI5] Using fallback voice list (common Windows SAPI5 voices)");
                    cachedVoiceNames = new List<string> {
                        "Microsoft David Desktop",
                        "Microsoft Zira Desktop",
                        "Microsoft Mark",
                        "Microsoft David",
                        "Microsoft Zira",
                        "Microsoft Hazel Desktop",
                        "Microsoft George Desktop",
                        "Microsoft Anna",
                        // Add the config values as options too
                        Sapi5Plugin.VoiceNarration.Value,
                        Sapi5Plugin.VoiceMale.Value,
                        Sapi5Plugin.VoiceFemale.Value
                    };
                    // Remove duplicates
                    cachedVoiceNames = cachedVoiceNames.Distinct().ToList();
                }
            }

            if (cachedVoiceNames.Count == 0)
            {
                Debug.LogWarning("[SAPI5] No SAPI5 voices available!");
                return;
            }

            Debug.Log($"[SAPI5] Refreshing voice dropdowns with {cachedVoiceNames.Count} voices");

            UpdateDropdown(__instance.ttsVoiceNarrationDropdown, cachedVoiceNames, Sapi5Plugin.VoiceNarration);
            UpdateDropdown(__instance.ttsVoiceMaleDropdown, cachedVoiceNames, Sapi5Plugin.VoiceMale);
            UpdateDropdown(__instance.ttsVoiceFemaleDropdown, cachedVoiceNames, Sapi5Plugin.VoiceFemale);
            UpdateDropdown(__instance.ttsVoiceMonsterDropdown, cachedVoiceNames, Sapi5Plugin.VoiceMonster);
            UpdateDropdown(__instance.ttsVoiceRoboticDropdown, cachedVoiceNames, Sapi5Plugin.VoiceRobot);
            UpdateDropdown(__instance.ttsVoiceEnemyDropdown, cachedVoiceNames, Sapi5Plugin.VoiceEnemy);
        }

        private static void UpdateDropdown(TMP_Dropdown dropdown, List<string> voices, ConfigEntry<string> config)
        {
             if (dropdown == null) return;
             dropdown.ClearOptions();
             dropdown.AddOptions(voices);

             int index = voices.IndexOf(config.Value);
             if (index == -1) index = 0;
             dropdown.SetValueWithoutNotify(index);

             dropdown.onValueChanged.RemoveAllListeners();
             dropdown.onValueChanged.AddListener(val => {
                 if (val >= 0 && val < voices.Count)
                 {
                     config.Value = voices[val];
                     Sapi5Plugin.Instance.Config.Save();
                 }
             });
        }
        
        private static void RestoreTikTokVoices(MainMenu __instance)
        {
            Debug.Log("[SAPI5] Restoring TikTok voices");
            var dictField = AccessTools.Field(typeof(MainMenu), "tiktokVoiceNameToUiStringDict");
            if (dictField == null)
            {
                Debug.LogError("[SAPI5] Could not find tiktokVoiceNameToUiStringDict field");
                return;
            }
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
            if (method == null)
            {
                Debug.LogError("[SAPI5] Could not find PopulateVoiceDropdownOptions method");
                return;
            }
            // The signature of PopulateVoiceDropdownOptions in AIRL is: 
            // void PopulateVoiceDropdownOptions(TMP_Dropdown dropdown, Dictionary<string, string> options, string currentVal, string defaultVal)
            method.Invoke(instance, new object[] { dropdown, dict, PlayerPrefs.GetString(prefKey, fallback), fallback });
        }
    }
}
