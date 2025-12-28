using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using System.Linq;

namespace AIROG_PresetExporter
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class PresetExporterPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.presetexporter";
        public const string PLUGIN_NAME = "Preset Exporter";
        public const string PLUGIN_VERSION = "1.0.0";

        public static PresetExporterPlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is starting...");
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(MainMenu), "NewGame")]
        public static class MainMenu_NewGame_Patch
        {
            public static void Postfix(MainMenu __instance)
            {
                InjectUI(__instance);
            }
        }

        private static void InjectUI(MainMenu mainMenu)
        {
            try
            {
                if (mainMenu == null) return;

                // 1. Find the big "Auto-generate" button (usually ABOVE the dropdown row)
                Transform autoGenBtn = null;
                if (mainMenu.menuModal != null)
                {
                    autoGenBtn = mainMenu.menuModal.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name.Contains("AutoGenerate Button"));
                }
                
                if (autoGenBtn == null)
                {
                    autoGenBtn = mainMenu.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(t => t.name.Contains("AutoGenerate Button"));
                }

                if (autoGenBtn == null)
                {
                    Debug.LogWarning("[PresetExporter] Could not find AutoGenerate Button to use as anchor.");
                    // Last resort anchor
                    if (mainMenu.promptPresetDropdown != null) autoGenBtn = mainMenu.promptPresetDropdown.transform;
                }

                if (autoGenBtn == null) return;

                // Anti-duplication: check if already exists in the same parent
                if (autoGenBtn.parent.Find("ExportPresetsBtn") != null) return;

                Debug.Log($"[PresetExporter] Injecting Export UI next to: {autoGenBtn.name}");

                // 2. Create the button as a sibling of AutoGenerate
                GameObject exportBtnObj = UnityEngine.Object.Instantiate(autoGenBtn.gameObject, autoGenBtn.parent);
                exportBtnObj.name = "ExportPresetsBtn";

                // Clean the clone
                ScrubLogic(exportBtnObj);

                // Add fresh Button
                var btn = exportBtnObj.AddComponent<Button>();
                btn.onClick.AddListener(() => ExportPresets(mainMenu));

                // Update text
                var txt = exportBtnObj.GetComponentInChildren<TMP_Text>();
                if (txt != null)
                {
                    txt.text = "Export All";
                    txt.enableAutoSizing = true;
                    txt.fontSizeMin = 8;
                    txt.fontSizeMax = 18;
                }

                // POSITIONING & SIZE
                RectTransform autoGenRT = autoGenBtn.GetComponent<RectTransform>();
                RectTransform myRT = exportBtnObj.GetComponent<RectTransform>();

                // Make it a reasonable size (don't copy the potentially huge width of Auto-generate)
                myRT.sizeDelta = new Vector2(150, autoGenRT.rect.height > 0 ? autoGenRT.rect.height : 45);

                if (autoGenBtn.parent.GetComponent<LayoutGroup>() != null)
                {
                    // If it's in a layout group, just move it next to it
                    exportBtnObj.transform.SetSiblingIndex(autoGenBtn.GetSiblingIndex() + 1);
                }
                else
                {
                    // Manual offset to the right
                    myRT.anchorMin = autoGenRT.anchorMin;
                    myRT.anchorMax = autoGenRT.anchorMax;
                    myRT.pivot = autoGenRT.pivot;

                    float spacing = 20f;
                    // Calculate based on AutoGen's width + my width
                    float offsetX = (autoGenRT.rect.width * (1f - autoGenRT.pivot.x)) + (myRT.rect.width * myRT.pivot.x) + spacing;
                    myRT.anchoredPosition = new Vector2(autoGenRT.anchoredPosition.x + offsetX, autoGenRT.anchoredPosition.y);
                }

                // Force layout update of parent
                if (autoGenBtn.parent is RectTransform parentRT)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(parentRT);
                }

                Debug.Log("[PresetExporter] UI Injected successfully.");
            }
            catch (System.Exception ex) { Debug.LogError($"[PresetExporter] Failed to inject UI: {ex}"); }
        }

        private static void ScrubLogic(GameObject obj)
        {
            var components = obj.GetComponents<Component>();
            foreach (var comp in components)
            {
                if (comp is Transform || comp is RectTransform || comp is CanvasRenderer || comp is Image || comp is LayoutElement) continue;
                if (comp is TMP_Text || comp is TextMeshProUGUI || comp is MeshRenderer || comp is MeshFilter) continue;
                
                UnityEngine.Object.DestroyImmediate(comp);
            }

            for (int i = obj.transform.childCount - 1; i >= 0; i--)
            {
                ScrubLogic(obj.transform.GetChild(i).gameObject);
            }
        }

        private static void ExportPresets(MainMenu mainMenu)
        {
            Debug.Log("[PresetExporter] Exporting presets...");
            
            var presetsField = AccessTools.Field(typeof(MainMenu), "presets");
            if (presetsField == null) return;

            var presets = (List<MainMenu.PromptPreset>)presetsField.GetValue(mainMenu);

            if (presets == null || presets.Count == 0)
            {
                MainMenu.MessageModal()?.ShowModal("No presets found to export.");
                return;
            }

            string exportDir = Path.Combine(Paths.PluginPath, "AIROG_PresetExporter");
            if (!Directory.Exists(exportDir)) Directory.CreateDirectory(exportDir);

            int count = 0;
            foreach (var preset in presets)
            {
                try
                {
                    string safeName = string.Join("_", preset.presetName.Split(Path.GetInvalidFileNameChars()));
                    if (string.IsNullOrEmpty(safeName)) safeName = "Unnamed_Preset_" + count;
                    File.WriteAllText(Path.Combine(exportDir, $"{safeName}.json"), JsonConvert.SerializeObject(preset, Formatting.Indented));
                    count++;
                }
                catch {}
            }
            
            MainMenu.MessageModal()?.ShowModal($"Exported {count} presets to:\n{exportDir}");
        }
    }
}
