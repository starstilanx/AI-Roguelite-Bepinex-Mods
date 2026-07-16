using BepInEx;
using HarmonyLib;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_SkillWeb
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public partial class SkillWebPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.skillweb";
        public const string PLUGIN_NAME = "Skill Web";
        public const string PLUGIN_VERSION = "4.1.1";

        public static SkillWebPlugin Instance { get; private set; }
        public SkillWebData Data { get; private set; }
        public SkillWebConfig SkillConfig { get; private set; }

        /// <summary>True while a RefineViaAI drain pass is in flight.</summary>
        private bool _aiRefinementRunning;

        /// <summary>Perks awaiting AI refinement.</summary>
        private readonly Queue<PerkNode> _refineQueue = new Queue<PerkNode>();

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[SkillWeb] Plugin {PLUGIN_GUID} v{PLUGIN_VERSION} (Constellation Overhaul) starting...");
            LoadConfig();
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(SkillWebPatches));
            Logger.LogInfo("[SkillWeb] Patched successfully.");
        }

        public static string GetSavePath()
            => Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "SkillWeb.json");

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
        }

        public void SaveData()
        {
            if (Data == null || string.IsNullOrEmpty(SS.I?.saveSubDirAsArg)) return;
            Data.Save(GetSavePath());
        }

        private static GameCharacter CurrentActor()
            => SS.I?.hackyManager?.playerCharacter?.GetCurrentActor();

        /// <summary>
        /// TurnHappenedEvent callback tracking survival and exploration milestones.
        /// </summary>
        public void OnTurnHappened(int numTurns, long secs)
        {
            if (Data == null) return;

            // Increment survival turns
            Data.turnsSurvived += numTurns;

            // 1. Check survival milestone: 1 Resonance per 25 turns survived
            int milestone = Data.turnsSurvived / 25;
            for (int m = 1; m <= milestone; m++)
            {
                string key = $"turns:{m * 25}";
                AwardResonance(key, 1, $"surviving {m * 25} turns");
            }

            // 2. Check level ups: 2 Resonance per level
            var character = SS.I?.hackyManager?.playerCharacter;
            if (character != null)
            {
                int currentLevel = character.playerLevel;
                for (int l = 2; l <= currentLevel; l++)
                {
                    string key = $"level:{l}";
                    AwardResonance(key, 2, $"reaching level {l}");
                }
            }

            // 3. First visit to a new Place: 1 Resonance
            var place = SS.I?.hackyManager?.currentPlace;
            if (place != null && !string.IsNullOrEmpty(place.uuid))
            {
                string key = $"place:{place.uuid}";
                AwardResonance(key, 1, $"visiting place '{place.GetPrettyName()}'");
            }

            // 4. Cross-mod sources: Chronicle chapters, NPCExpansion quests, Settlement buildings, Reverie dreams
            CheckCrossModResonance();

            // 5. Persist granted-ability cooldowns (native GameAbility.TurnHappened already ticked them
            //    down this turn; copy the live value back onto the owning nodes so it survives save/load).
            PersistAbilityCooldowns();
            SkillAbilityBar.Instance?.RefreshIfShowing();
        }

        private async Task RefineViaAIAsync()
        {
            _aiRefinementRunning = true;
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager == null) return;

                bool changed = false;
                while (_refineQueue.Count > 0)
                {
                    var pn = _refineQueue.Dequeue();
                    if (pn == null || Data == null) continue;
                    if (!Data.perkBonuses.TryGetValue(pn.uuid, out PerkBonus pb) || pb.aiRefined) continue;

                    var stats = await PerkStatDeriver.ViaAI(manager, pn.GetPrettyName(),
                        pn.GetPotentiallyNullDescription() ?? "");
                    if (stats != null)
                    {
                        pb.stats = stats;
                        pb.aiRefined = true;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SyncBonuses();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] AI refinement pass failed: " + ex.Message);
            }
            finally
            {
                _aiRefinementRunning = false;
            }
        }

        public void LoadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, "SkillWebConfig.json");
            if (File.Exists(path))
            {
                try
                {
                    SkillConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<SkillWebConfig>(
                        File.ReadAllText(path)) ?? new SkillWebConfig();
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
        [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
        [HarmonyPostfix]
        public static void AfterLoadOrNewGame_Postfix()
        {
            SkillWebPlugin.Instance.LoadSaveData();
            SkillWebPlugin.Instance.SyncBonuses();

            // Register economy hooks on turn tick
            GameplayManager.TurnHappenedEvent -= SkillWebPlugin.Instance.OnTurnHappened;
            GameplayManager.TurnHappenedEvent += SkillWebPlugin.Instance.OnTurnHappened;

            // Register with GenContext if available
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.airog.gencontext"))
            {
                GenContextIntegration.Register();
            }
        }

        [HarmonyPatch(typeof(ViewPerksModal), "RefreshView")]
        [HarmonyPostfix]
        public static void ViewPerksModal_RefreshView_Postfix()
        {
            if (SkillWebPlugin.Instance?.Data == null) SkillWebPlugin.Instance?.LoadSaveData();
            SkillWebPlugin.Instance?.SyncBonuses();
        }

        [HarmonyPatch(typeof(GameplayManager), "GetAttributeValAfterItemBonuses")]
        [HarmonyPostfix]
        public static void GetAttributeValAfterItemBonuses_Postfix(SS.PlayerAttribute attr, ref long __result)
        {
            var plugin = SkillWebPlugin.Instance;
            if (plugin?.Data?.CachedStats == null || !plugin.SkillConfig.AllowStatBonuses) return;
            if (plugin.Data.CachedStats.TryGetValue(attr, out float bonus))
                __result += (long)bonus;
        }

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
            rect.sizeDelta = new Vector2(150f, 34f);
            btnObj.GetComponent<UnityEngine.UI.Image>().color = new Color(0.25f, 0.12f, 0.04f);

            var tObj = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            tObj.transform.SetParent(btnObj.transform, false);
            var tmp  = tObj.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.text = "✦ Skill Web";
            tmp.fontSize  = 14;
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
                SkillWebPlugin.Instance.SyncBonuses();
                SkillWebUI.Open(ep.manager, SkillWebPlugin.Instance.Data);
            });

            // "✦ Abilities" button — opens the usable-ability bar (Keystone/Confluence grants).
            if (SkillWebPlugin.Instance.SkillConfig.GrantUsableAbilities
                && __instance.transform.Find("OpenSkillAbilitiesBtn") == null)
            {
                var abilObj = new GameObject("OpenSkillAbilitiesBtn",
                    typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                abilObj.transform.SetParent(ep.transform, false);

                var arect = abilObj.GetComponent<RectTransform>();
                arect.anchorMin = new Vector2(0.5f, 0f);
                arect.anchorMax = new Vector2(0.5f, 0f);
                arect.pivot     = new Vector2(0.5f, 0f);
                arect.anchoredPosition = new Vector2(0f, 46f); // just above the Skill Web button
                arect.sizeDelta = new Vector2(150f, 34f);
                abilObj.GetComponent<UnityEngine.UI.Image>().color = new Color(0.20f, 0.10f, 0.24f);

                var atObj = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
                atObj.transform.SetParent(abilObj.transform, false);
                var atmp = atObj.GetComponent<TMPro.TextMeshProUGUI>();
                atmp.text = "✦ Abilities";
                atmp.fontSize  = 14;
                atmp.alignment = TMPro.TextAlignmentOptions.Center;
                var atRect = atObj.GetComponent<RectTransform>();
                atRect.anchorMin = Vector2.zero;
                atRect.anchorMax = Vector2.one;
                atRect.sizeDelta = Vector2.zero;

                abilObj.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() =>
                {
                    if (SkillWebPlugin.Instance.Data == null)
                        SkillWebPlugin.Instance.LoadSaveData();
                    SkillWebPlugin.Instance.SyncBonuses();
                    SkillAbilityBar.Open(ep.manager);
                });
            }
        }

        [HarmonyPatch(typeof(ViewPerksModal), "PresentSelf")]
        [HarmonyPostfix]
        public static void ViewPerksModal_PresentSelf_Postfix(ViewPerksModal __instance)
        {
            if (__instance == null) return;
            if (__instance.transform.Find("OpenConstellationBtn") != null) return;

            var btnObj = new GameObject("OpenConstellationBtn",
                typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
            btnObj.transform.SetParent(__instance.transform, false);

            var rect = btnObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.04f, 0.03f);
            rect.anchorMax = new Vector2(0.22f, 0.08f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            btnObj.GetComponent<UnityEngine.UI.Image>().color = new Color(0.15f, 0.07f, 0.25f);

            var tObj = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            tObj.transform.SetParent(btnObj.transform, false);
            var tmp  = tObj.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.text = "✦ View Constellation";
            tmp.fontSize  = 13;
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
                SkillWebPlugin.Instance.SyncBonuses();
                SkillWebUI.Open(__instance.manager, SkillWebPlugin.Instance.Data);
            });
        }
    }
}
