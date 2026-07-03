using HarmonyLib;
using TMPro;
using UnityEngine;
using System.Collections.Generic;

namespace AIROG_Sapi5
{
    // The TTS settings panel moved its dropdowns out of MainMenu and into a
    // nested TtsOptions component, and dropped the old per-archetype voice
    // dropdowns (Narration/Male/Female/Monster/Robotic/Enemy) in favor of a
    // single narrator dropdown plus tag-based voice matching. We only re-add
    // the TikTok/SAPI5 provider toggle here; the per-archetype SAPI5 voice
    // overrides are still configurable via the BepInEx config file.
    [HarmonyPatch(typeof(MainMenu))]
    public static class UiPatches
    {
        private static TMP_Dropdown ttsProviderDropdown;
        private static bool isInitializing = false;

        [HarmonyPostfix]
        [HarmonyPatch("Options")]
        public static void OptionsPostfix(MainMenu __instance)
        {
            Debug.Log("[SAPI5] OptionsPostfix triggered");

            if (ttsProviderDropdown == null)
            {
                Debug.Log("[SAPI5] Creating TTS Provider UI...");
                CreateUi(__instance.ttsOptions);
            }

            isInitializing = true;
            UpdateProviderDropdownValue();
            isInitializing = false;
        }

        private static void CreateUi(TtsOptions ttsOptions)
        {
            if (ttsOptions == null || ttsOptions.ttsModeDropdown == null)
            {
                Debug.LogError("[SAPI5] ttsOptions.ttsModeDropdown is null, cannot create UI");
                return;
            }

            Transform parent = ttsOptions.ttsModeDropdown.transform.parent;
            int index = ttsOptions.ttsModeDropdown.transform.GetSiblingIndex();

            // Clone the TTS Mode dropdown to create our provider dropdown
            GameObject providerGo = Object.Instantiate(ttsOptions.ttsModeDropdown.gameObject, parent);
            providerGo.name = "Sapi5_Provider_Dropdown";

            // Place after the TTS Mode dropdown (index + 1)
            providerGo.transform.SetSiblingIndex(index + 1);

            ttsProviderDropdown = providerGo.GetComponent<TMP_Dropdown>();
            if (ttsProviderDropdown == null)
            {
                Debug.LogError("[SAPI5] Failed to get TMP_Dropdown component");
                return;
            }

            ttsProviderDropdown.onValueChanged.RemoveAllListeners();
            ttsProviderDropdown.ClearOptions();
            ttsProviderDropdown.AddOptions(new List<string> { "Cloud TTS (Game Default)", "SAPI5 (Windows)" });

            // Set position - move UP to appear above the TTS Mode dropdown
            RectTransform rt = providerGo.GetComponent<RectTransform>();
            RectTransform ttsModeRt = ttsOptions.ttsModeDropdown.GetComponent<RectTransform>();
            if (rt != null && ttsModeRt != null)
            {
                Vector3 pos = ttsModeRt.localPosition;
                rt.localPosition = new Vector3(pos.x, pos.y + 35, pos.z);
            }

            providerGo.SetActive(true);

            ttsProviderDropdown.onValueChanged.AddListener(val =>
            {
                if (isInitializing) return;
                Debug.Log($"[SAPI5] Provider dropdown changed to: {val}");
                Sapi5Plugin.UseSapi5.Value = (val == 1);
                Sapi5Plugin.Instance.Config.Save();
            });

            UpdateProviderDropdownValue();

            Debug.Log("[SAPI5] UI Created successfully");
        }

        private static void UpdateProviderDropdownValue()
        {
            if (ttsProviderDropdown == null) return;
            ttsProviderDropdown.SetValueWithoutNotify(Sapi5Plugin.UseSapi5.Value ? 1 : 0);
        }
    }
}
