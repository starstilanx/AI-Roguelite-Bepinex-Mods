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
    public class SkillWebPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.skillweb";
        public const string PLUGIN_NAME = "Skill Web";
        public const string PLUGIN_VERSION = "3.1.0";

        public static SkillWebPlugin Instance { get; private set; }
        public SkillWebData Data { get; private set; }
        public SkillWebConfig SkillConfig { get; private set; }

        /// <summary>True while a RefineViaAI drain pass is in flight (only one runs at a time).</summary>
        private bool _aiRefinementRunning;

        /// <summary>Perks awaiting AI refinement. SyncBonuses enqueues; the drain pass consumes.</summary>
        private readonly Queue<PerkNode> _refineQueue = new Queue<PerkNode>();

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[SkillWeb] Plugin {PLUGIN_GUID} v{PLUGIN_VERSION} (native-perk bridge) starting...");
            LoadConfig();
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(SkillWebPatches));
            Logger.LogInfo("[SkillWeb] Patched successfully.");
        }

        /// <summary>Full path to the per-save SkillWeb.json file.</summary>
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

        // ── Native-perk bridge ────────────────────────────────────────────────────

        /// <summary>The character whose attributes the game computes (and we augment).</summary>
        private static GameCharacter CurrentActor()
            => SS.I?.hackyManager?.playerCharacter?.GetCurrentActor();

        /// <summary>
        /// Reads the current actor's native perk trees, ensures every learned perk has a derived
        /// bonus (heuristic immediately, AI refinement queued if enabled), recomputes the cached
        /// attribute totals, and saves. Safe to call on load and after every perk-tree interaction.
        /// </summary>
        public void SyncBonuses()
        {
            if (Data == null) return;
            var pdata = CurrentActor()?.playableData;
            if (pdata?.perkTrees == null) return;

            var snapshots = new List<PerkSnapshot>();
            var newlyDerived = new List<PerkNode>();

            foreach (var pt in pdata.perkTrees)
            {
                if (pt?.rootPerkNode == null) continue;
                foreach (var pn in pt.GetAllPerkNodes())
                {
                    if (pn == null || !pn.isLearned) continue;

                    string name = pn.GetPrettyName();
                    string desc = pn.GetPotentiallyNullDescription() ?? "";
                    var pb = Data.GetOrCreate(pn.uuid, name);

                    if (!pb.derived)
                    {
                        pb.stats = PerkStatDeriver.Heuristic(name, desc, SkillConfig.HeuristicBudget);
                        pb.derived = true;
                        if (SkillConfig.UseAIStatDerivation) newlyDerived.Add(pn);
                    }

                    snapshots.Add(new PerkSnapshot { uuid = pn.uuid, isActivated = pn.isActivated });
                }
            }

            Data.RecalculateStats(snapshots, SkillConfig);
            SaveData();

            if (newlyDerived.Count > 0)
            {
                // Always enqueue — a pass already in flight will drain these too, so perks
                // learned mid-pass are no longer dropped from refinement.
                foreach (var pn in newlyDerived) _refineQueue.Enqueue(pn);
                if (!_aiRefinementRunning)
                    _ = RefineViaAIAsync();
            }
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
                        pb.stats = stats;        // AI result replaces the heuristic estimate
                        pb.aiRefined = true;
                        changed = true;
                    }
                }

                if (changed)
                {
                    // Rebuild from the live tree so newly-toggled active states are reflected too.
                    var pdata = CurrentActor()?.playableData;
                    var snapshots = new List<PerkSnapshot>();
                    if (pdata?.perkTrees != null)
                    {
                        foreach (var pt in pdata.perkTrees)
                        {
                            if (pt?.rootPerkNode == null) continue;
                            foreach (var p in pt.GetAllPerkNodes())
                                if (p != null && p.isLearned)
                                    snapshots.Add(new PerkSnapshot { uuid = p.uuid, isActivated = p.isActivated });
                        }
                    }
                    Data.RecalculateStats(snapshots, SkillConfig);
                    SaveData();
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
        // ── Game lifecycle ──────────────────────────────────────────────────────

        [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
        [HarmonyPostfix]
        public static void AfterLoadOrNewGame_Postfix()
        {
            SkillWebPlugin.Instance.LoadSaveData();
            SkillWebPlugin.Instance.SyncBonuses();
        }

        // ── Keep the sidecar in sync with the native perk tree ──────────────────
        // RefreshView fires after every learn/activate/respec/regen in the native modal.

        [HarmonyPatch(typeof(ViewPerksModal), "RefreshView")]
        [HarmonyPostfix]
        public static void ViewPerksModal_RefreshView_Postfix()
        {
            if (SkillWebPlugin.Instance?.Data == null) SkillWebPlugin.Instance?.LoadSaveData();
            SkillWebPlugin.Instance?.SyncBonuses();
        }

        // ── Stat injection (the mechanical layer native perks lack) ─────────────

        [HarmonyPatch(typeof(GameplayManager), "GetAttributeValAfterItemBonuses")]
        [HarmonyPostfix]
        public static void GetAttributeValAfterItemBonuses_Postfix(SS.PlayerAttribute attr, ref long __result)
        {
            var plugin = SkillWebPlugin.Instance;
            if (plugin?.Data?.CachedStats == null || !plugin.SkillConfig.AllowStatBonuses) return;
            if (plugin.Data.CachedStats.TryGetValue(attr, out float bonus))
                __result += (long)bonus;
        }

        // ── Equipment-panel button → "Skill Web Bonuses" summary ────────────────

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
            tmp.text = "✦ Skill Web Bonuses";
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
        }
    }
}
