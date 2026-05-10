using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;

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
        }

        private static void CreateUi(MainMenu __instance)
        {
            Transform parent = __instance.ttsOptions.ttsModeDropdown.transform.parent;
            int index = __instance.ttsOptions.ttsModeDropdown.transform.GetSiblingIndex();

            // Create Provider Dropdown
            GameObject providerGo = Object.Instantiate(__instance.ttsOptions.ttsModeDropdown.gameObject, parent);
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

        [HarmonyPrefix]
        [HarmonyPatch("SaveCurrentPrefs")]
        public static void SaveOptionsPrefix(MainMenu __instance)
        {
            DeepgramTtsPlugin.Instance.Config.Save();
        }
    }
}
