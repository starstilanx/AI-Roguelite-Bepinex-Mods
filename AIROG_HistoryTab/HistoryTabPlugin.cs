using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using AIROG_Core;

namespace AIROG_HistoryTab
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class HistoryTabPlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.historytab";
        public const string PLUGIN_NAME = "History Tab";
        public const string PLUGIN_VERSION = "1.0.0";

        public static HistoryTabPlugin Instance { get; private set; }
        public static ConfigEntry<bool> EnableLogTruncation;

        // Each hook below is patched independently via SafePatch: if the game renames or
        // changes the signature of any one of these methods, only that hook is skipped
        // (logged as a warning) instead of an exception aborting the rest of Awake and
        // silently taking the unrelated fixes (log truncation, token fix, import/export,
        // prompt injection) down with it.
        protected override void Awake()
        {
            base.Awake();
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is starting Awake...");

            SafePatch(typeof(NewWorldModal), "PresentSelf",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_NewWorldModal_PresentSelf))));

            SafePatch(typeof(MainMenu), "NewGame",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_MainMenu_NewGame))));

            SafePatch(typeof(GameplayManager), "StartNewWorld",
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Prefix_StartNewWorld))));

            SafePatchCtor(typeof(UniverseInfo),
                new[] { typeof(string), typeof(string), typeof(VoronoiWorld), typeof(Lorebook), typeof(GameplayManager) },
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UniverseInfo_Constructor))));

            SafePatchCtor(typeof(UniverseInfo),
                new[] { typeof(string), typeof(Place), typeof(GameplayManager) },
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UniverseInfo_Constructor2))));

            SafePatch(typeof(JournalModal), "Init",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_JournalModal_Init))));

            SafePatch(typeof(JournalModal), "UnsetTabTransesAndBtns",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UnsetTabTransesAndBtns))));

            SafePatch(typeof(SaveIO), "WriteSaveFile",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_WriteSaveFile))));

            SafePatch(typeof(GameplayManager), "LoadGame",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_LoadGame))));

            // CRASH FIX: replaces DateTimeDisp.IncrTime entirely (returns false) rather than
            // augmenting it. If the game's IncrTime grows new behavior, this patch won't
            // reproduce it — worth periodically diffing against the decompiled source.
            SafePatch(typeof(DateTimeDisp), "IncrTime",
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Prefix_IncrTime))));

            Logger.LogInfo("HistoryTabPlugin Awake hook patching completed.");

            // Unrelated features bundled into this plugin — isolated from the UI/save hooks
            // above (and from each other) so a failure in one can't disable the others.
            SafeRun("EnableLogTruncation / ConsoleLogFix", () =>
            {
                EnableLogTruncation = Config.Bind("General", "EnableLogTruncation", true,
                    "Truncates extremely long log lines (e.g. from Chinese localization) preventing Windows Console crashes.");
                ConsoleLogFix.Patch(HarmonyInstance, EnableLogTruncation.Value);
            });

            SafeRun("ChineseTokenFix", () => ChineseTokenFix.Patch(HarmonyInstance));

            SafeRun("HistoryImportExport", () => HistoryImportExport.Patch(HarmonyInstance));

            SafeRun("gen_history prompt injection", () =>
            {
                if (SS.I != null && !SS.I.chatGptPromptsDict.ContainsKey("gen_history"))
                {
                    SS.I.chatGptPromptsDict["gen_history"] = "As a world-building AI, create a compelling and immersive history for the universe of '${universe_name}'.\n\n" +
                        "Universe Description:\n${universe_desc}\n\n" +
                        "Current World Context (${world_name}):\n${world_bkgd}\n\n" +
                        "Instructions:\n" +
                        "1. Write 3-5 paragraphs of history including major past events, the rise and fall of civilizations, or significant turning points.\n" +
                        "2. Ensure the tone matches the world background.${maybe_hint_str}${maybe_i18n_str}\n\n" +
                        "Output the history text directly. Do not include any meta-commentary or JSON.";
                    Logger.LogInfo("Injected default 'gen_history' prompt.");
                }
            });
        }

        public static void Postfix_NewWorldModal_PresentSelf(NewWorldModal __instance)
        {
            HistoryUI.InjectIntoNewWorldModal(__instance);
        }

        public static void Postfix_MainMenu_NewGame(MainMenu __instance)
        {
            HistoryUI.InjectIntoMainMenu(__instance);
        }

        public static void Prefix_StartNewWorld()
        {
            if (HistoryUI.CurrentNgHistory != null && HistoryUI.CurrentNgHistory.historyInput != null)
            {
                HistoryData.LastGeneratedHistory = HistoryUI.CurrentNgHistory.historyInput.text;
                Debug.Log($"[HistoryTab] Prefix_StartNewWorld: Captured history from UI. Length: {HistoryData.LastGeneratedHistory?.Length ?? 0}");
            }
            else
            {
                Debug.Log("[HistoryTab] Prefix_StartNewWorld: No NG history found in UI.");
            }
        }

        public static void Postfix_UniverseInfo_Constructor(UniverseInfo __instance)
        {
            Debug.Log($"[HistoryTab] UniverseInfo constructor (5-arg) postfix for: {__instance.name}. LastGeneratedHistory length: {HistoryData.LastGeneratedHistory?.Length ?? 0}");
            if (!string.IsNullOrEmpty(HistoryData.LastGeneratedHistory))
            {
                HistoryData.SetHistory(__instance, HistoryData.LastGeneratedHistory);
                Debug.Log("[HistoryTab] Successfully associated history with new Universe: " + __instance.name);
                HistoryData.LastGeneratedHistory = null;
            }
        }

        public static void Postfix_UniverseInfo_Constructor2(UniverseInfo __instance)
        {
            Debug.Log($"[HistoryTab] UniverseInfo constructor (3-arg-Place) postfix. LastGeneratedHistory length: {HistoryData.LastGeneratedHistory?.Length ?? 0}");
            if (!string.IsNullOrEmpty(HistoryData.LastGeneratedHistory))
            {
                HistoryData.SetHistory(__instance, HistoryData.LastGeneratedHistory);
                HistoryData.LastGeneratedHistory = null;
            }
        }

        public static void Postfix_JournalModal_Init(JournalModal __instance)
        {
            try {
                Debug.Log("[HistoryTab] Postfix_JournalModal_Init starting...");
                HistoryUI.InjectIntoJournalModal(__instance);
                
                Transform historyView = __instance.tabTransesHolder.Find("HistoryTabView_Mod");
                if (historyView == null) return;
                
                // Find/Create Edit/Gen Console under the text
                Transform controlsHolder = historyView.Find("ControlsHolder");
                if (controlsHolder == null)
                {
                    GameObject controlsObj = new GameObject("ControlsHolder", typeof(RectTransform));
                    controlsObj.transform.SetParent(historyView, false);
                    var rt = controlsObj.GetComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0, 0);
                    rt.anchorMax = new Vector2(1, 0);
                    rt.pivot = new Vector2(0.5f, 0);
                    rt.anchoredPosition = new Vector2(0, 0);
                    rt.sizeDelta = new Vector2(0, 50); // Height

                    var hlg = controlsObj.AddComponent<HorizontalLayoutGroup>();
                    hlg.childControlWidth = false;
                    hlg.childControlHeight = true;
                    hlg.childForceExpandWidth = false;
                    hlg.childForceExpandHeight = true;
                    hlg.spacing = 20;
                    hlg.padding = new RectOffset(10, 10, 5, 5);
                    hlg.childAlignment = TextAnchor.MiddleCenter;
                    
                    // Edit Button
                    GameObject editBtnObj = new GameObject("EditHistoryBtn", typeof(Image), typeof(Button));
                    editBtnObj.transform.SetParent(controlsObj.transform, false);
                    editBtnObj.GetComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f);
                    editBtnObj.GetComponent<RectTransform>().sizeDelta = new Vector2(120, 0);
                    var editBtn = editBtnObj.GetComponent<Button>();
                    
                    GameObject editTextObj = new GameObject("Text", typeof(TextMeshProUGUI));
                    editTextObj.transform.SetParent(editBtnObj.transform, false);
                    var editText = editTextObj.GetComponent<TextMeshProUGUI>();
                    editText.text = "Edit";
                    editText.alignment = TextAlignmentOptions.Center;
                    editText.fontSize = 18;
                    editText.color = Color.white;
                    ((RectTransform)editTextObj.transform).anchorMin = Vector2.zero;
                    ((RectTransform)editTextObj.transform).anchorMax = Vector2.one;

                    editBtn.onClick.AddListener(() => {
                        var universe = __instance.manager.GetCurrentUniverse();
                        string currentHist = HistoryData.GetHistory(universe);
                        __instance.manager.NTextPromptModal().PresentSelf(new List<NTextPromptModal.PromptArg> {
                            new NTextPromptModal.TextPromptArg("Edit History", currentHist ?? "", null, true, (val) => {
                                HistoryData.SetHistory(universe, val);
                                // Refresh View
                                var text = historyView.GetComponentInChildren<TextMeshProUGUI>();
                                if (text != null) text.text = val;
                                // Force Save
                                string editSaveDir = ModSaveFile.Dir();
                                if (editSaveDir != null) HistoryData.Save(editSaveDir);
                            })
                        }, null, null);
                    });
                    
                    controlsHolder = controlsObj.transform;
                }
                
                Transform historyTabBtnTrans = __instance.tabBtnsHolder.Find("HistoryTabButton");
                if (historyTabBtnTrans == null) return;
                Button historyTabBtn = historyTabBtnTrans.GetComponent<Button>();
                historyTabBtn.onClick.RemoveAllListeners();
                historyTabBtn.onClick.AddListener(() => {
                    Debug.Log("[HistoryTab] History Tab Clicked!");
                    // 07/11 build: manager.soundManager is no longer wired — use the singleton.
                    try { SoundManager.I.smallClickSoundFxObj.PlayNextSound(); } catch { }
                    __instance.UnsetTabTransesAndBtns();
                    
                    var img = historyTabBtn.GetComponentInChildren<Image>();
                    if (img != null) img.color = Utils.GetColorFromStr(JournalModal.SELECTED_TAB_COLOR_STR);
                    
                    Transform historyView = __instance.tabTransesHolder.Find("HistoryTabView_Mod");
                    if (historyView != null) 
                    {
                        historyView.gameObject.SetActive(true);
                        var text = historyView.GetComponentInChildren<TextMeshProUGUI>();
                        if (text != null)
                        {
                            var universe = __instance.manager.GetCurrentUniverse();
                            string history = HistoryData.GetHistory(universe);
                            text.text = string.IsNullOrEmpty(history) ? "No history found for this universe." : history;
                            Debug.Log($"[HistoryTab] Set history text for {universe?.name ?? "null"}. Length: {text.text.Length}");
                        }
                    }
                });
                Debug.Log("[HistoryTab] Listener bound successfully.");
            } catch (Exception e) {
                Debug.Log("[HistoryTab] ERROR in Postfix_JournalModal_Init: " + e);
            }
        }

        public static void Postfix_UnsetTabTransesAndBtns(JournalModal __instance)
        {
            Transform historyTabBtn = __instance.tabBtnsHolder.Find("HistoryTabButton");
            if (historyTabBtn != null)
            {
                var img = historyTabBtn.GetComponentInChildren<Image>();
                if (img != null) img.color = Utils.GetColorFromStr(JournalModal.UNSELECTED_TAB_COLOR_STR);
            }
            
            Transform historyView = __instance.tabTransesHolder.Find("HistoryTabView_Mod");
            if (historyView != null) historyView.gameObject.SetActive(false);
        }

        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            string saveDir = ModSaveFile.Dir();
            if (saveDir != null) HistoryData.Save(saveDir);
        }

        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            string saveDir = ModSaveFile.Dir();
            if (saveDir == null) return;

            Debug.Log($"[HistoryTab] Postfix_LoadGame running. Loading history from: {saveDir}");
            HistoryData.Load(saveDir);

            // Double check if it worked
            var univ = __instance.GetCurrentUniverse();
            string hist = HistoryData.GetHistory(univ);
            Debug.Log($"[HistoryTab] Loaded history verification for {univ?.name}: {(string.IsNullOrEmpty(hist) ? "EMPTY" : "FOUND (" + hist.Length + " chars)")}");
        }
        
        public static void Postfix_BuildPromptString(GameplayManager __instance, ref string __result)
        {
            // DISABLED: Logic moved to AIROG_GenContext to optimize token usage.
            /*
            var universe = __instance.GetCurrentUniverse();
            if (universe != null)
            {
                string history = HistoryData.GetHistory(universe);
                if (!string.IsNullOrEmpty(history))
                {
                    Debug.Log($"[HistoryTab] Injecting [WORLD HISTORY] into prompt ({history.Length} chars). Prompt Start: {(__result.Length > 50 ? __result.Substring(0, 50) : __result)}...");
                    __result = "[WORLD HISTORY]\n" + history + "\n\n" + __result;
                }
                else
                {
                    // Debug.Log($"[HistoryTab] History is empty for universe {universe.name}, skipping injection.");
                }
            }
            */
        }

        public static bool Prefix_IncrTime(DateTimeDisp __instance, long secs)
        {
            try
            {
                var manager = __instance.manager;
                if (manager == null) return true; // Fallback to original if manager missing

                // Using reflection to check UsesDatetime logic or just trust manager state
                // If config exists, we increment time. If config is null, UsesDatetime is false.
                
                // Original code: uses manager.GetCurrentUniverse()
                var currentUniverse = manager.GetCurrentUniverse();
                
                if (manager.UsesDatetime() && currentUniverse != null)
                {
                    currentUniverse.inGameElapsedSecs = Math.Max(0L, currentUniverse.inGameElapsedSecs + secs);
                    __instance.MaybeUpdateDisp();
                }
                
                // We SKIP the original execution to avoid the exception
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[HistoryTab] Error in Prefix_IncrTime: " + ex);
                return true; // Fallback to original
            }
        }
    }
}
