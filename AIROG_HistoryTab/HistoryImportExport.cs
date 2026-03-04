using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SFB;
using System;
using System.IO;
using UnityEngine;

namespace AIROG_HistoryTab
{
    public static class HistoryImportExport
    {
        public static void Patch(Harmony harmony)
        {
            var onExport = AccessTools.Method(typeof(MainMenu), "OnExportCurrentPresetClicked");
            if (onExport != null)
                harmony.Patch(onExport, new HarmonyMethod(typeof(HistoryImportExport), nameof(Prefix_OnExportCurrentPresetClicked)));

            var onSaveConfirm = AccessTools.Method(typeof(MainMenu), "OnSaveCurrentPresetConfirm");
            if (onSaveConfirm != null)
                harmony.Patch(onSaveConfirm, new HarmonyMethod(typeof(HistoryImportExport), nameof(Prefix_OnSaveCurrentPresetConfirm)));

            var onImport = AccessTools.Method(typeof(MainMenu), "OnImportCurrentPresetClicked");
            if (onImport != null)
                harmony.Patch(onImport, new HarmonyMethod(typeof(HistoryImportExport), nameof(Prefix_OnImportCurrentPresetClicked)));
        }

        public static bool Prefix_OnExportCurrentPresetClicked(MainMenu __instance)
        {
            try
            {
                __instance.customInputTxt.text = LS.I.GetLocStr("name-prompt-preset-header");
                __instance.customInputModal.gameObject.SetActive(value: true);
                __instance.customInputConfirmButton.onClick.RemoveAllListeners();
                __instance.customInputConfirmButton.onClick.AddListener(delegate
                {
                    __instance.OnAddCustomModelCancel();
                    string text = __instance.customTxtInput.text;
                    string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string text2 = StandaloneFileBrowser.SaveFilePanel("Save File", folderPath + "\\Downloads", "my_prompt_preset.json", "json");
                    if (!Utils.IsNullOrEmpty(text2))
                    {
                        var preset = (MainMenu.PromptPreset)AccessTools.Method(typeof(MainMenu), "GetCurrentNgSettingsAsPreset").Invoke(__instance, new object[] { text });
                        var jo = JObject.FromObject(preset);

                        if (HistoryUI.CurrentNgHistory != null && HistoryUI.CurrentNgHistory.historyInput != null)
                        {
                            string history = HistoryUI.CurrentNgHistory.historyInput.text;
                            if (!string.IsNullOrEmpty(history))
                            {
                                jo["worldHistory_Mod"] = history;
                                Debug.Log("[HistoryTab] Exporting history: " + history.Length + " chars");
                            }
                        }

                        File.WriteAllText(text2, jo.ToString());
                    }
                });
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[HistoryTab] Error in Prefix_OnExportCurrentPresetClicked: " + e);
                return true; 
            }
        }

        public static bool Prefix_OnSaveCurrentPresetConfirm(MainMenu __instance)
        {
            try
            {
                __instance.OnAddCustomModelCancel();
                string text = __instance.customTxtInput.text;
                var preset = (MainMenu.PromptPreset)AccessTools.Method(typeof(MainMenu), "GetCurrentNgSettingsAsPreset").Invoke(__instance, new object[] { text });
                var jo = JObject.FromObject(preset);

                if (HistoryUI.CurrentNgHistory != null && HistoryUI.CurrentNgHistory.historyInput != null)
                {
                    string history = HistoryUI.CurrentNgHistory.historyInput.text;
                    if (!string.IsNullOrEmpty(history))
                    {
                        jo["worldHistory_Mod"] = history;
                        Debug.Log("[HistoryTab] Saving history to preset: " + history.Length + " chars");
                    }
                }

                string contents = jo.ToString();
                // FIX: SS.I.presetsDir usually points to persistentDataPath/world_presets
                string presetsDir = Path.Combine(Application.persistentDataPath, "world_presets");
                string path = Path.Combine(presetsDir, text + ".json");
                if (File.Exists(path))
                {
                    // FIX: ConfirmationModal is static
                    // FIX: ShowModal -> ShowTextPromptModal
                    MainMenu.ConfirmationModal().ShowTextPromptModal(LS.I.GetLocStr("overwrite-preset-confirm"), true, true, delegate
                    {
                        File.WriteAllText(path, contents);
                        // FIX: RepopulatePresets is private
                        Traverse.Create(__instance).Method("RepopulatePresets").GetValue();
                    });
                }
                else
                {
                    File.WriteAllText(path, contents);
                    // FIX: RepopulatePresets is private
                    Traverse.Create(__instance).Method("RepopulatePresets").GetValue();
                }
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[HistoryTab] Error in Prefix_OnSaveCurrentPresetConfirm: " + e);
                return true;
            }
        }

        public static bool Prefix_OnImportCurrentPresetClicked(MainMenu __instance)
        {
            try
            {
                string text = Utils.OpenFileBrowserForJsonSelection();
                if (text == null)
                {
                    Debug.Log("filepath was null.");
                    return false;
                }
                Debug.Log("OnImportCurrentPresetClicked: " + text);
                string json = File.ReadAllText(text);
                var jo = JObject.Parse(json);
                var promptPreset = jo.ToObject<MainMenu.PromptPreset>();

                if (promptPreset.initialLocationInfo == null && promptPreset.regions != null)
                {
                    Debug.LogWarning("grandfathering legacy region logic into new initial location logic.");
                    promptPreset.initialLocationInfo = new MainMenu.InitialLocationInfo(promptPreset.regions, null, 0f);
                }

                // FIX: PopulateTextAndSurvivalBarTogglesWithPreset is private
                Traverse.Create(__instance).Method("PopulateTextAndSurvivalBarTogglesWithPreset", new object[] { promptPreset }).GetValue();

                if (jo.ContainsKey("worldHistory_Mod"))
                {
                    string history = jo["worldHistory_Mod"].ToString();
                    Debug.Log("[HistoryTab] Importing history: " + history.Length + " chars");
                    if (HistoryUI.CurrentNgHistory != null)
                    {
                        HistoryUI.CurrentNgHistory.PopulateHistory(history);
                    }
                }
                else
                {
                    if (HistoryUI.CurrentNgHistory != null)
                    {
                        HistoryUI.CurrentNgHistory.OnClearHistory();
                    }
                }

                __instance.OnPromptPresetChanged();
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError("[HistoryTab] Error in Prefix_OnImportCurrentPresetClicked: " + e);
                return true;
            }
        }
    }
}
