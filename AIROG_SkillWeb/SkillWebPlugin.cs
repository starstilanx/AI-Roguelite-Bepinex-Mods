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
        public const string PLUGIN_VERSION = "5.1.0";

        public static SkillWebPlugin Instance { get; private set; }
        public SkillWebData Data { get; private set; }
        public SkillWebConfig SkillConfig { get; private set; }

        /// <summary>True while a RefineViaAI drain pass is in flight.</summary>
        private bool _aiRefinementRunning;

        /// <summary>Perks awaiting AI refinement.</summary>
        private readonly Queue<PerkNode> _refineQueue = new Queue<PerkNode>();

        /// <summary>
        /// Perk uuids already queued for AI refinement this session. A perk whose bonus is still
        /// only heuristic is re-queued once per session (an interrupted or failed request used to
        /// leave it heuristic forever), but never more than once, so a failing backend is not
        /// asked again on every sync.
        /// </summary>
        private readonly HashSet<string> _refineRequested = new HashSet<string>();

        /// <summary>Set while a batch of awards is being applied; the single sync happens at the end.</summary>
        private bool _deferSync, _syncPending;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[SkillWeb] Plugin {PLUGIN_GUID} v{PLUGIN_VERSION} (Passive Atlas) starting...");
            LoadConfig();
            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(SkillWebPatches));
            Logger.LogInfo("[SkillWeb] Patched successfully.");
        }

        public static string GetSavePath()
            => Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "SkillWeb.json");

        /// <summary>
        /// Makes <see cref="Data"/> the active save's web. Returns true when the Data object was
        /// replaced (a different save, or the file changed on disk), false when it was kept.
        /// </summary>
        public bool LoadSaveData()
        {
            if (string.IsNullOrEmpty(SS.I?.saveSubDirAsArg))
            {
                Logger.LogError("[SkillWeb] Save directory is null or empty — cannot load.");
                return false;
            }
            string path = GetSavePath();

            // Same save, and the file on disk is still the one we last wrote: memory is already
            // current, so keep the Data object. This hook-driven "load" is far more often a
            // re-load than a new save - the game runs AfterLoadOrNewGame twice per menu load and
            // once for every Undo and Retry. Replacing Data each time threw away every in-flight
            // AI result (they check "is this still the Data I started with") and both refinement
            // queues, so on a first load nothing was ever AI-refined at all.
            if (Data != null && DataIsForActiveSave() && DiskIsWhatWeWrote(path))
                return false;

            Logger.LogInfo($"[SkillWeb] Loading from: {path}");
            Data = SkillWebData.Load(path);
            _dataPath = path;
            _dataSubDir = SS.I.saveSubDirAsArg;
            _dataTopDir = SS.I.saveTopLvlDir;
            _diskStampUtc = StampOf(path);
            // Session bookkeeping tied to the previous Data object.
            _grantedAbilities.Clear();
            _refineQueue.Clear();
            _refineRequested.Clear();
            _abilityDescRequested.Clear();
            LastGrownSectors.Clear();
            return true;
        }

        /// <summary>The SkillWeb.json that <see cref="Data"/> was read from - and the only one it may be written to.</summary>
        private string _dataPath;
        private string _dataSubDir, _dataTopDir;
        /// <summary>Last-write time of that file as of our last load or save; MinValue when it did not exist.</summary>
        private DateTime _diskStampUtc;

        private static DateTime StampOf(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// False when the file was deleted or replaced behind our back - a new game created in a
        /// folder of the same name, a restored backup - in which case memory must not be trusted.
        /// </summary>
        private bool DiskIsWhatWeWrote(string path)
        {
            DateTime now = StampOf(path);
            return now != DateTime.MinValue && now == _diskStampUtc;
        }

        /// <summary>
        /// True when <see cref="Data"/> belongs to the save that is active right now. It does not
        /// between the moment the game points at another save folder and the moment our load hook
        /// runs for it; anything that syncs or saves in that window would write one save's web into
        /// another save's folder. Cheap on purpose: the attribute postfix asks on every roll.
        /// </summary>
        private bool DataIsForActiveSave()
        {
            var ss = SS.I;
            if (Data == null || ss == null || string.IsNullOrEmpty(ss.saveSubDirAsArg)) return false;
            return ss.saveSubDirAsArg == _dataSubDir && ss.saveTopLvlDir == _dataTopDir;
        }

        internal bool HasLiveDataForActiveSave => DataIsForActiveSave();

        public void SaveData()
        {
            if (!DataIsForActiveSave()) return;
            string path = GetSavePath();
            Data.Save(path);
            _diskStampUtc = StampOf(path);
        }

        /// <summary>
        /// The character the web belongs to: always the main player character.
        ///
        /// This used to be "whoever the player currently controls". Taking control of a companion
        /// (or just opening a companion's perk screen, which triggers a sync) then imported THEIR
        /// perk trees into the web as sectors and fed their perks into the gates - one shared web
        /// silently re-pointed at a different character. The web is the player character's.
        /// </summary>
        internal static GameCharacter WebOwner()
        {
            try { return SS.I?.hackyManager?.playerCharacter?.GetPcGameCharacter(); }
            catch { return null; }
        }

        /// <summary>
        /// The main player character's level. NOT PlayerCharacter.playerLevel: that field is a
        /// pre-entity-system leftover the game only ever copies in and out of the save file. It is
        /// never incremented on level-up, so it reads 1 on every save made since.
        /// </summary>
        internal static int OwnerLevel()
        {
            int level = WebOwner()?.level ?? 1;
            return level < 1 ? 1 : level;
        }

        /// <summary>
        /// TurnHappenedEvent callback tracking survival and exploration milestones.
        /// </summary>
        public void OnTurnHappened(int numTurns, long secs)
        {
            // Subscribed to a game event: an exception here would propagate into the turn loop.
            try
            {
                if (Data == null || !DataIsForActiveSave() || !ModSwitch.Enabled) return;
                TurnTick(numTurns);
            }
            catch (Exception ex)
            {
                Logger.LogError("[SkillWeb] Turn tick failed: " + ex);
            }
        }

        private void TurnTick(int numTurns)
        {
            // The game raises this event with numTurns == 0 for every enemy attack, encounter
            // round, plot hook and task result. Those are not the player's turns.
            if (numTurns <= 0) return;

            // Every award below would otherwise run a full sync and rewrite the whole web file. The
            // first tick after updating from a version that never paid level-up Resonance pays
            // 2 x (level - 1) awards at once, so collect them and sync a single time.
            _deferSync = true;
            _syncPending = false;
            try { ApplyTurnAwards(numTurns); }
            finally { _deferSync = false; }
            if (_syncPending) { _syncPending = false; SyncBonuses(); }

            // Mirror granted-perk cooldowns onto their nodes (the game already ticked them this turn).
            PersistAbilityCooldowns();
            SkillAbilityBar.Instance?.RefreshIfShowing();
        }

        private void ApplyTurnAwards(int numTurns)
        {
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
            int currentLevel = OwnerLevel();
            for (int l = 2; l <= currentLevel; l++)
            {
                string key = $"level:{l}";
                AwardResonance(key, 2, $"reaching level {l}");
            }

            // 3. First visit to a new Place: 1 Resonance
            var place = SS.I?.hackyManager?.currentPlace;
            if (place != null && !string.IsNullOrEmpty(place.uuid))
            {
                string key = $"place:{place.uuid}";
                AwardResonance(key, 1, $"visiting place '{place.GetPrettyName()}'");
            }

            // 4. Cross-mod sources: Chronicle chapters, NPCExpansion quests, Settlement buildings, Reverie dreams
            if (SkillConfig.CrossModHooks) CheckCrossModResonance();
        }

        /// <summary>
        /// The player flipped the Mods-checklist switch for Skill Web during play.
        /// Off: stop contributing attributes, take the granted perks back, hide the buttons, close
        /// the windows. On: rebuild everything from the (untouched) save data.
        /// </summary>
        internal void OnModSwitchChanged()
        {
            try
            {
                bool on = ModSwitch.Enabled;
                SkillWebPatches.ApplyButtonVisibility();
                if (on)
                {
                    SyncBonuses();
                }
                else
                {
                    if (SkillWebUI.Instance != null && SkillWebUI.Instance.IsOpen) SkillWebUI.Instance.Close();
                    SkillAbilityBar.Instance?.Close();
                    if (Data != null)
                    {
                        Data.CachedStats.Clear();
                        SyncAbilities();
                        SaveData();
                    }
                    try { SS.I?.hackyManager?.playerCharacter?.UpdateStats(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[SkillWeb] Applying the mod switch failed: " + ex);
            }
        }

        private async Task RefineViaAIAsync()
        {
            _aiRefinementRunning = true;
            var dataAtStart = Data;
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

                    // A different save was loaded while the request was in flight; pb belongs to the old one.
                    if (!ReferenceEquals(Data, dataAtStart)) return;

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
                    // "UseAIStatDerivation" is the pre-v4 name of "UseAIGeneration", kept readable so
                    // old files still work. It used to be WRITTEN too, after the real key, and JSON
                    // members are applied in file order - so a player who set UseAIGeneration to false
                    // had it flipped back by the stale alias on the next line, and rewritten as true.
                    // The real key wins whenever it is present; the alias is no longer written.
                    var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                    if (json["UseAIGeneration"] != null) json.Remove("UseAIStatDerivation");
                    SkillConfig = json.ToObject<SkillWebConfig>() ?? new SkillWebConfig();
                    // Write back so options added since the file was created (e.g. WebSize) show up in it.
                    File.WriteAllText(path,
                        Newtonsoft.Json.JsonConvert.SerializeObject(SkillConfig, Newtonsoft.Json.Formatting.Indented));
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
                try
                {
                    File.WriteAllText(path,
                        Newtonsoft.Json.JsonConvert.SerializeObject(SkillConfig, Newtonsoft.Json.Formatting.Indented));
                }
                catch (Exception ex)
                {
                    // Not being able to write the default file must not stop the plugin loading.
                    Logger.LogWarning($"[SkillWeb] Could not write the default config: {ex.Message}");
                }
            }
        }
    }

    public static class SkillWebPatches
    {
        /// <summary>Buttons this mod injected into the game's panels, so the per-save switch can hide them.</summary>
        private static readonly List<GameObject> _buttons = new List<GameObject>();

        internal static void ApplyButtonVisibility()
        {
            bool on = ModSwitch.Enabled;
            _buttons.RemoveAll(b => b == null); // destroyed with their scene
            foreach (var b in _buttons)
            {
                bool show = on;
                if (b.name == "OpenSkillAbilitiesBtn") show = on && SkillWebPlugin.Instance.SkillConfig.GrantUsableAbilities;
                if (b.activeSelf != show) b.SetActive(show);
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
        [HarmonyPostfix]
        public static void AfterLoadOrNewGame_Postfix()
        {
            // A postfix on the load path: nothing in here may throw into the game.
            try
            {
                var plugin = SkillWebPlugin.Instance;
                if (plugin == null) return;

                // This hook runs twice per menu load and once per Undo/Retry. Only when the web in
                // memory was really replaced (another save, or its file changed on disk) is the
                // queued and in-flight work stale: sector ids and node ids repeat across saves.
                bool replaced = plugin.LoadSaveData();
                if (replaced)
                {
                    NodeIconGen.ClearRuntimeState();
                    WebGrower.ClearRefinementQueue();
                }

                // Register economy hooks on turn tick
                GameplayManager.TurnHappenedEvent -= plugin.OnTurnHappened;
                GameplayManager.TurnHappenedEvent += plugin.OnTurnHappened;

                // Register with GenContext if available
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.airog.gencontext"))
                {
                    GenContextIntegration.Register();
                }
                // The save-switched callback only re-applies button visibility. It can fire while
                // Data still holds the PREVIOUS save's web (GenContext notices the new save folder
                // on the first switch query, which may come before this hook has run): syncing or
                // saving from it would write the old save's web into the new save's folder.
                ModSwitch.Hook(plugin.OnModSwitchChanged, ApplyButtonVisibility);

                if (ModSwitch.Enabled)
                {
                    plugin.SyncBonuses();
                    // Pick up AI refinement a previous session did not finish (the queue lives in
                    // memory only). Rings with nothing left to refine cost nothing.
                    if (replaced && plugin.Data != null && plugin.Data.nodes.Count > 0 && plugin.SkillConfig.UseAIGeneration)
                        WebGrower.EnqueueInitialRefinements(plugin.Data);
                }
                else plugin.SyncAbilities(); // switched off for this save: take back any perks granted while it was on
                ApplyButtonVisibility();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Load hook failed: " + ex);
            }
        }

        [HarmonyPatch(typeof(ViewPerksModal), "RefreshView")]
        [HarmonyPostfix]
        public static void ViewPerksModal_RefreshView_Postfix()
        {
            try
            {
                if (!ModSwitch.Enabled) return;
                if (SkillWebPlugin.Instance?.Data == null) SkillWebPlugin.Instance?.LoadSaveData();
                SkillWebPlugin.Instance?.SyncBonuses();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Perk-screen sync failed: " + ex);
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "GetAttributeValAfterItemBonuses")]
        [HarmonyPostfix]
        public static void GetAttributeValAfterItemBonuses_Postfix(GameplayManager __instance, SS.PlayerAttribute attr, GameCharacter actor, ref long __result)
        {
            // Called for every roll and every stat readout. Must stay cheap and must never throw.
            try
            {
                var plugin = SkillWebPlugin.Instance;
                if (plugin?.Data?.CachedStats == null || !plugin.SkillConfig.AllowStatBonuses) return;
                if (plugin.Data.CachedStats.Count == 0 || !ModSwitch.Enabled) return;
                // Between a scene starting and our load hook, Data is still the previous save's web.
                if (!plugin.HasLiveDataForActiveSave) return;

                // The game asks this for companions and co-op partners too (actor == null means
                // "the character the local UI is showing"). The web only empowers its owner.
                var who = actor ?? __instance.GetLocalUiActor();
                if (who != null && who != SkillWebPlugin.WebOwner()) return;

                if (plugin.Data.CachedStats.TryGetValue(attr, out float bonus))
                    __result += (long)bonus;
            }
            catch { }
        }

        /// <summary>Frame in which one of this mod's windows handled Esc itself.</summary>
        internal static int EscConsumedFrame = -1;

        // The game also gets every Esc press (GameplayManager.EscKey): it closes whichever of its
        // own windows is open and otherwise OPENS THE SETTINGS MENU. Our windows are not among the
        // ones it knows, so the press that closed the Skill Web also popped the game menu behind
        // it. While one of our windows is up (or closed on this very frame - the two handlers run
        // in no guaranteed order) the game's handler is skipped. EscKey is a plain void method,
        // so skipping it is safe. A game modal on top keeps its normal handling.
        [HarmonyPatch(typeof(GameplayManager), "EscKey")]
        [HarmonyPrefix]
        public static bool EscKey_Prefix()
        {
            try
            {
                if (MessageModal.I != null && MessageModal.I.IsShowing()) return true;
                if (ModalWindow.I != null && ModalWindow.I.IsShowing()) return true;
                if (NTextPromptModal.I != null && NTextPromptModal.I.IsShowing()) return true;

                bool ours = (SkillWebUI.Instance != null && SkillWebUI.Instance.IsOpen)
                         || (SkillAbilityBar.Instance != null && SkillAbilityBar.Instance.IsShowing)
                         || EscConsumedFrame == Time.frameCount;
                return !ours;
            }
            catch { return true; }
        }

        [HarmonyPatch(typeof(ItemPanel), "Start")]
        [HarmonyPostfix]
        public static void ItemPanel_Start_Postfix(ItemPanel __instance)
        {
            try { InjectEquipmentPanelButtons(__instance); }
            catch (Exception ex) { Debug.LogError("[SkillWeb] Could not add the Skill Web buttons: " + ex); }
        }

        private static void InjectEquipmentPanelButtons(ItemPanel __instance)
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
                if (!ModSwitch.Enabled) return;
                if (SkillWebPlugin.Instance.Data == null)
                    SkillWebPlugin.Instance.LoadSaveData();
                SkillWebPlugin.Instance.SyncBonuses();
                SkillWebUI.Open(ep.manager, SkillWebPlugin.Instance.Data);
            });
            _buttons.Add(btnObj);

            // "✦ Abilities" button — opens the usable-ability list (Keystone/Confluence grants).
            if (__instance.transform.Find("OpenSkillAbilitiesBtn") == null)
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
                    if (!ModSwitch.Enabled) return;
                    if (SkillWebPlugin.Instance.Data == null)
                        SkillWebPlugin.Instance.LoadSaveData();
                    SkillWebPlugin.Instance.SyncBonuses();
                    SkillAbilityBar.Open(ep.manager);
                });
                _buttons.Add(abilObj);
            }

            ApplyButtonVisibility();
        }

        // The perks screen became a tab of the character panel in the Perks v2 build. PresentSelf()
        // now only asks the panel to switch tabs, so a player who clicks the Perks tab directly never
        // goes through it; OnShow is what runs every time the tab appears. Both are hooked (the
        // injection is idempotent) so the button exists whichever way the tab was reached.
        [HarmonyPatch(typeof(ViewPerksModal), "PresentSelf")]
        [HarmonyPostfix]
        public static void ViewPerksModal_PresentSelf_Postfix(ViewPerksModal __instance)
        {
            try { InjectPerksScreenButton(__instance); }
            catch (Exception ex) { Debug.LogError("[SkillWeb] Could not add the perk-screen button: " + ex); }
        }

        [HarmonyPatch(typeof(ViewPerksModal), "OnShow")]
        [HarmonyPostfix]
        public static void ViewPerksModal_OnShow_Postfix(ViewPerksModal __instance)
        {
            try { InjectPerksScreenButton(__instance); }
            catch (Exception ex) { Debug.LogError("[SkillWeb] Could not add the perk-screen button: " + ex); }
        }

        private static void InjectPerksScreenButton(ViewPerksModal __instance)
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
            tmp.text = "✦ View Skill Web";
            tmp.fontSize  = 13;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            var tRect = tObj.GetComponent<RectTransform>();
            tRect.anchorMin = Vector2.zero;
            tRect.anchorMax = Vector2.one;
            tRect.sizeDelta = Vector2.zero;

            var btn = btnObj.GetComponent<UnityEngine.UI.Button>();
            btn.onClick.AddListener(() =>
            {
                if (!ModSwitch.Enabled) return;
                if (SkillWebPlugin.Instance.Data == null)
                    SkillWebPlugin.Instance.LoadSaveData();
                SkillWebPlugin.Instance.SyncBonuses();
                SkillWebUI.Open(__instance.manager, SkillWebPlugin.Instance.Data);
            });
            _buttons.Add(btnObj);
            ApplyButtonVisibility();
        }
    }
}
