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
        public const string PLUGIN_VERSION = "2.0.0";

        public static SkillWebPlugin Instance { get; private set; }
        public SkillWebData Data { get; private set; }
        public SkillWebConfig SkillConfig { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[SkillWeb] Plugin {PLUGIN_GUID} starting...");
            LoadConfig();
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(SkillWebPatches));
            Logger.LogInfo("[SkillWeb] Patched successfully.");
        }

        /// <summary>Full path to the per-save SkillWeb.json file.</summary>
        public static string GetSavePath()
            => Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "SkillWeb.json");

        /// <summary>Full path to the AI-generated icon for a node UUID. Prefers _sprite.png.</summary>
        public static string GetImagePath(string uuid)
        {
            string dir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
            string sprite = Path.Combine(dir, uuid + "_sprite.png");
            return File.Exists(sprite) ? sprite : Path.Combine(dir, uuid + ".png");
        }

        public void LoadSaveData()
        {
            if (string.IsNullOrEmpty(SS.I?.saveSubDirAsArg))
            {
                Logger.LogError("[SkillWeb] Save directory is null or empty — cannot load.");
                return;
            }
            string path = GetSavePath();
            Logger.LogInfo($"[SkillWeb] Loading from: {path}");
            Data = SkillWebData.Load(path);
            Data.RecalculateStats();
        }

        public void SaveData()
        {
            if (Data == null || string.IsNullOrEmpty(SS.I?.saveSubDirAsArg)) return;
            Data.Save(GetSavePath());
        }

        public void LoadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, "SkillWebConfig.json");
            if (File.Exists(path))
            {
                try
                {
                    SkillConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<SkillWebConfig>(
                        File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[SkillWeb] Config load error: {ex.Message}");
                    SkillConfig = new SkillWebConfig();
                }
            }
            else
            {
                SkillConfig = new SkillWebConfig();
                File.WriteAllText(path,
                    Newtonsoft.Json.JsonConvert.SerializeObject(SkillConfig, Newtonsoft.Json.Formatting.Indented));
            }
        }
    }

    public static class SkillWebPatches
    {
        // ── Game lifecycle ──────────────────────────────────────────────────────

        [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
        [HarmonyPostfix]
        public static void AfterLoadOrNewGame_Postfix()
        {
            SkillWebPlugin.Instance.LoadSaveData();
        }

        // ── XP / levelling ─────────────────────────────────────────────────────

        [HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
        [HarmonyPrefix]
        public static void GainXp_Prefix(PlayerCharacter __instance, out int __state)
        {
            __state = __instance.playerLevel;
        }

        [HarmonyPatch(typeof(PlayerCharacter), "GainXp")]
        [HarmonyPostfix]
        public static void GainXp_Postfix(PlayerCharacter __instance, int __state)
        {
            var data = SkillWebPlugin.Instance.Data;
            if (data == null) return;

            if (__instance.playerLevel > __state)
            {
                int gained = __instance.playerLevel - __state;
                int pts = gained * SkillWebPlugin.Instance.SkillConfig.PointsPerLevel;
                data.skillPoints += pts;
                data.totalPointsEarned += pts;
                data.lastKnownLevel = __instance.playerLevel;
                SkillWebPlugin.Instance.SaveData();
                Debug.Log($"[SkillWeb] Level up! +{pts} skill point(s). Total: {data.skillPoints}");
            }
        }

        // ── Stat injection ──────────────────────────────────────────────────────

        [HarmonyPatch(typeof(GameplayManager), "GetAttributeValAfterItemBonuses")]
        [HarmonyPostfix]
        public static void GetAttributeValAfterItemBonuses_Postfix(SS.PlayerAttribute attr, ref long __result)
        {
            var plugin = SkillWebPlugin.Instance;
            if (plugin?.Data == null || !plugin.SkillConfig.AllowStatBonuses) return;
            if (plugin.Data.CachedStats == null) plugin.Data.RecalculateStats();
            if (plugin.Data.CachedStats.TryGetValue(attr, out float bonus))
                __result += (long)bonus;
        }

        // ── Equipment panel button ──────────────────────────────────────────────

        [HarmonyPatch(typeof(ItemPanel), "Start")]
        [HarmonyPostfix]
        public static void ItemPanel_Start_Postfix(ItemPanel __instance)
        {
            if (!(__instance is EquipmentPanel ep)) return;
            if (__instance.transform.Find("OpenSkillWebBtn") != null) return;

            var btnObj = new GameObject("OpenSkillWebBtn",
                typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            btnObj.transform.SetParent(ep.transform, false);

            var rect = btnObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot    = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 8f);
            rect.sizeDelta = new Vector2(140f, 34f);
            btnObj.GetComponent<UnityEngine.UI.Image>().color = new Color(0.25f, 0.12f, 0.04f);

            var tObj = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            tObj.transform.SetParent(btnObj.transform, false);
            var tmp  = tObj.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.text = "✦ Skill Web";
            tmp.fontSize  = 15;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            var tRect = tObj.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;

            var btn = btnObj.GetComponent<UnityEngine.UI.Button>();
            btn.onClick.AddListener(() =>
            {
                if (SkillWebPlugin.Instance.Data == null)
                    SkillWebPlugin.Instance.LoadSaveData();
                SkillWebUI.Open(ep.manager, SkillWebPlugin.Instance.Data);
            });
        }
    }
}
