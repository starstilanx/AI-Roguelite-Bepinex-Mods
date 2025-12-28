using BepInEx;
using HarmonyLib;
using UnityEngine;
using System;
using System.IO;

namespace AIROG_SkillWeb
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class SkillWebPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.skillweb";
        public const string PLUGIN_NAME = "Skill Web";
        public const string PLUGIN_VERSION = "1.0.0";

        public static SkillWebPlugin Instance { get; private set; }
        public SkillWebData Data { get; private set; }
        public SkillWebConfig SkillConfig { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is starting...");

            LoadConfig();
            
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(SkillWebPatches));

            Logger.LogInfo("Skill Web Patched successfully.");
        }

        public void LoadSaveData()
        {
            if (string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                Logger.LogError("Save directory is null or empty. Cannot load Skill Web data.");
                return;
            }

            string path = Path.Combine(SS.I.saveSubDirAsArg, "SkillWeb.json");
            Logger.LogInfo($"Loading Skill Web data from: {path}");
            
            Data = SkillWebData.Load(path);

            // Migration: Enable legacy nodes
            if (Data.nodes.Count > 0)
            {
                bool anyChange = false;
                foreach (var n in Data.nodes)
                {
                    if (!n.isUnlocked)
                    {
                        n.isUnlocked = true;
                        anyChange = true;
                    }
                }
                if (anyChange)
                {
                    Data.RecalculateStats();
                    SaveData();
                }
            }
            
            // If still empty (new game), create center node
            if (Data.nodes.Count == 0)
            {
                Data.AddNode(new SkillNode("root", "Genesis", "The starting point of your journey.", Vector2.zero) { isUnlocked = true });
                SaveData();
            }
        }

        public void SaveData()
        {
            if (string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                Logger.LogError("Save directory is null or empty. Cannot save Skill Web data.");
                return;
            }

            string path = Path.Combine(SS.I.saveSubDirAsArg, "SkillWeb.json");
            Logger.LogInfo($"Saving Skill Web data to: {path}");
            Data.Save(path);
        }

        public void LoadConfig()
        {
             // Load from BepInEx config folder or generic location
             string path = Path.Combine(Paths.ConfigPath, "SkillWebConfig.json");
             if (File.Exists(path))
             {
                 try
                 {
                     SkillConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<SkillWebConfig>(File.ReadAllText(path));
                 }
                 catch (Exception ex)
                 {
                     Logger.LogError($"Error loading config: {ex.Message}");
                     SkillConfig = new SkillWebConfig(); // Fallback
                 }
             }
             else
             {
                 SkillConfig = new SkillWebConfig();
                 File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(SkillConfig, Newtonsoft.Json.Formatting.Indented));
             }
        }
    }

    public static class SkillWebPatches
    {
        [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
        [HarmonyPostfix]
        public static void GameplayManager_AfterLoadOrNewGame_Postfix()
        {
            SkillWebPlugin.Instance.LoadSaveData();
            if (SkillWebPlugin.Instance.Data != null)
                SkillWebPlugin.Instance.Data.RecalculateStats();
        }

        [HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
        [HarmonyPrefix]
        public static void PlayerCharacter_GainXp_Prefix(PlayerCharacter __instance, out int __state)
        {
            __state = __instance.playerLevel;
        }

        [HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
        [HarmonyPostfix]
        public static void PlayerCharacter_GainXp_Postfix(PlayerCharacter __instance, int __state)
        {
            if (SkillWebPlugin.Instance.Data == null) return;
            
            // If level increased
            if (__instance.playerLevel > __state)
            {
                int levelsGained = __instance.playerLevel - __state;
                int points = levelsGained * SkillWebPlugin.Instance.SkillConfig.PointsPerLevel;
                SkillWebPlugin.Instance.Data.skillPoints += points;
                SkillWebPlugin.Instance.Data.lastKnownLevel = __instance.playerLevel;
                SkillWebPlugin.Instance.SaveData();
                UnityEngine.Debug.Log($"[SkillWeb] Gained {points} Skill Points! Total: {SkillWebPlugin.Instance.Data.skillPoints}");
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "GetAttributeValAfterItemBonuses")]
        [HarmonyPostfix]
        public static void GameplayManager_GetAttributeValAfterItemBonuses_Postfix(SS.PlayerAttribute attr, ref long __result)
        {
            if (SkillWebPlugin.Instance.Data != null && SkillWebPlugin.Instance.SkillConfig.AllowStatBonuses)
            {
                if (SkillWebPlugin.Instance.Data.CachedStats == null) 
                    SkillWebPlugin.Instance.Data.RecalculateStats();

                if (SkillWebPlugin.Instance.Data.CachedStats.TryGetValue(attr, out float bonus))
                {
                    __result += (long)bonus;
                }
            }
        }

        [HarmonyPatch(typeof(ItemPanel), "Start")]
        [HarmonyPostfix]
        public static void ItemPanel_Start_Postfix(ItemPanel __instance)
        {
            if (!(__instance is EquipmentPanel equipmentPanel)) return;
            
            // Check if button already exists to prevent duplicates
            if (__instance.transform.Find("OpenSkillWebBtn") != null) return;

            // Add a button to the equipment panel to open the skill web
            var btnObj = new GameObject("OpenSkillWebBtn", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            btnObj.transform.SetParent(equipmentPanel.transform, false);
            
            var rect = btnObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(0, 0);
            rect.anchoredPosition = new Vector2(100, 50); // Position it somewhere reasonable
            rect.sizeDelta = new Vector2(120, 40);

            btnObj.GetComponent<UnityEngine.UI.Image>().color = new Color(0.4f, 0.2f, 0.1f);

            var textObj = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            textObj.transform.SetParent(btnObj.transform, false);
            var text = textObj.GetComponent<TMPro.TextMeshProUGUI>();
            text.text = "Skill Web";
            text.fontSize = 18;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.rectTransform.sizeDelta = new Vector2(120, 40);

            var btn = btnObj.GetComponent<UnityEngine.UI.Button>();
            btn.onClick.AddListener(async () => {
                // Ensure data is loaded
                if (SkillWebPlugin.Instance.Data == null) SkillWebPlugin.Instance.LoadSaveData();
                
                // Check if root node needs image
                if (SkillWebPlugin.Instance.Data.nodes.Count > 0)
                {
                    var root = SkillWebPlugin.Instance.Data.nodes[0];
                    if (string.IsNullOrEmpty(root.imageUuid))
                    {
                        await SkillWebGenerator.GenerateImageForNode(equipmentPanel.manager, root);
                        SkillWebPlugin.Instance.SaveData();
                    }
                }

                SkillWebUI.Open(equipmentPanel.manager, SkillWebPlugin.Instance.Data);
            });
        }
    }
}
