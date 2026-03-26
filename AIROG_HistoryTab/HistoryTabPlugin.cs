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

namespace AIROG_HistoryTab
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class HistoryTabPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.historytab";
        public const string PLUGIN_NAME = "History Tab";
        public const string PLUGIN_VERSION = "1.0.0";

        public static HistoryTabPlugin Instance { get; private set; }
        public static ConfigEntry<bool> EnableLogTruncation;

        private void Awake()
        {
            try {
                Instance = this;
                Logger.LogInfo($"Plugin {PLUGIN_GUID} is starting Awake...");

                var harmony = new Harmony(PLUGIN_GUID);
                Logger.LogInfo("Harmony instance created.");
                
                // 1. BuildPromptString — postfix is disabled (logic lives in GenContext now),
                // but we still resolve the method so the patch call doesn't error-log.
                Logger.LogInfo("Attempting to find BuildPromptString...");
                var buildPromptMethod =
                    AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                        typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                        typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                        typeof(List<Faction>), typeof(List<string>), typeof(bool), typeof(bool) // current (12 params)
                    }) ??
                    AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                        typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                        typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                        typeof(List<Faction>), typeof(List<string>), typeof(bool) // alpha (11 params)
                    }) ??
                    AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                        typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                        typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                        typeof(List<Faction>), typeof(List<string>) // stable (10 params)
                    });

                if (buildPromptMethod != null)
                {
                    harmony.Patch(buildPromptMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_BuildPromptString))));
                    Logger.LogInfo("Patched BuildPromptString successfully.");
                }
                else Logger.LogError("Failed to find BuildPromptString!");

                // 2. NewWorldModal.PresentSelf
                Logger.LogInfo("Attempting to find NewWorldModal.PresentSelf...");
                var presentSelfMethod = AccessTools.Method(typeof(NewWorldModal), "PresentSelf");
                if (presentSelfMethod != null)
                {
                    harmony.Patch(presentSelfMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_NewWorldModal_PresentSelf))));
                    Logger.LogInfo("Patched NewWorldModal.PresentSelf successfully.");
                }
                else Logger.LogError("Failed to find NewWorldModal.PresentSelf!");

                // 3. MainMenu.NewGame
                Logger.LogInfo("Attempting to find MainMenu.NewGame...");
                var newGameMethod = AccessTools.Method(typeof(MainMenu), "NewGame");
                if (newGameMethod != null)
                {
                    harmony.Patch(newGameMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_MainMenu_NewGame))));
                    Logger.LogInfo("Patched MainMenu.NewGame successfully.");
                }
                else Logger.LogError("Failed to find MainMenu.NewGame!");

                // 4. GameplayManager.StartNewWorld
                Logger.LogInfo("Attempting to find GameplayManager.StartNewWorld...");
                var startNewWorldMethod = AccessTools.Method(typeof(GameplayManager), "StartNewWorld");
                if (startNewWorldMethod != null)
                {
                    harmony.Patch(startNewWorldMethod, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Prefix_StartNewWorld))));
                    Logger.LogInfo("Patched GameplayManager.StartNewWorld successfully.");
                }
                else Logger.LogError("Failed to find GameplayManager.StartNewWorld!");

                // 5. UniverseInfo constructor
                Logger.LogInfo("Attempting to find UniverseInfo constructors...");
                var uniConstructor = AccessTools.Constructor(typeof(UniverseInfo), new Type[] { 
                    typeof(string), typeof(string), typeof(VoronoiWorld), typeof(Lorebook), typeof(GameplayManager) 
                });
                if (uniConstructor != null)
                {
                    harmony.Patch(uniConstructor, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UniverseInfo_Constructor))));
                    Logger.LogInfo("Patched UniverseInfo constructor (5-arg) successfully.");
                }
                
                var uniConstructor2 = AccessTools.Constructor(typeof(UniverseInfo), new Type[] { 
                    typeof(string), typeof(Place), typeof(GameplayManager) 
                });
                if (uniConstructor2 != null)
                {
                    harmony.Patch(uniConstructor2, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UniverseInfo_Constructor2))));
                    Logger.LogInfo("Patched UniverseInfo constructor (3-arg-Place) successfully.");
                }

                // 6. JournalModal
                Logger.LogInfo("Attempting to find JournalModal methods...");
                var journalInitMethod = AccessTools.Method(typeof(JournalModal), "Init");
                if (journalInitMethod != null)
                {
                    harmony.Patch(journalInitMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_JournalModal_Init))));
                    Logger.LogInfo("Patched JournalModal.Init successfully.");
                }
                var unsetTabsMethod = AccessTools.Method(typeof(JournalModal), "UnsetTabTransesAndBtns");
                if (unsetTabsMethod != null)
                {
                    harmony.Patch(unsetTabsMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_UnsetTabTransesAndBtns))));
                    Logger.LogInfo("Patched JournalModal.UnsetTabTransesAndBtns successfully.");
                }

                // 7. SaveIO (Saving)
                Logger.LogInfo("Attempting to find SaveIO methods...");
                var writeSaveMethod = AccessTools.Method(typeof(SaveIO), "WriteSaveFile");
                if (writeSaveMethod != null)
                {
                    harmony.Patch(writeSaveMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_WriteSaveFile))));
                    Logger.LogInfo("Patched SaveIO.WriteSaveFile successfully.");
                }

                // 8. GameplayManager.LoadGame (Loading)
                Logger.LogInfo("Attempting to find GameplayManager.LoadGame...");
                var loadGameMethod = AccessTools.Method(typeof(GameplayManager), "LoadGame");
                if (loadGameMethod != null)
                {
                    harmony.Patch(loadGameMethod, null, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Postfix_LoadGame))));
                    Logger.LogInfo("Patched GameplayManager.LoadGame successfully.");
                }

                // 9. DateTimeDisp IncrTime (CRASH FIX)
                Logger.LogInfo("Attempting to find DateTimeDisp.IncrTime...");
                var incrTimeMethod = AccessTools.Method(typeof(DateTimeDisp), "IncrTime");
                if (incrTimeMethod != null)
                {
                    harmony.Patch(incrTimeMethod, new HarmonyMethod(AccessTools.Method(typeof(HistoryTabPlugin), nameof(Prefix_IncrTime))));
                    Logger.LogInfo("Patched DateTimeDisp.IncrTime successfully.");
                }


                Logger.LogInfo("HistoryTabPlugin Awake completed successfully.");
                
                // Truncate large logs to prevent IndexOutOfRangeException in ConsoleEncoding
                EnableLogTruncation = Config.Bind("General", "EnableLogTruncation", true, "Truncates extremely long log lines (e.g. from Chinese localization) preventing Windows Console crashes.");
                ConsoleLogFix.Patch(harmony, EnableLogTruncation.Value);

                // Adjust token limits for Chinese users to prevent truncated responses and JSON errors
                ChineseTokenFix.Patch(harmony);

                // Handle History Import/Export
                HistoryImportExport.Patch(harmony);
                
                
                // Inject default prompts
                if (SS.I != null)
                {
                    if (!SS.I.chatGptPromptsDict.ContainsKey("gen_history"))
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
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"EXCEPTION in HistoryTabPlugin Awake: {e.Message}\n{e.StackTrace}");
            }
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
                                HistoryData.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
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
                    __instance.manager.soundManager.smallClickSoundFxObj.PlayNextSound();
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
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                HistoryData.Save(saveDir);
            }
        }

        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            if (SS.I != null)
            {
                // LoadGame doesn't take saveDir as arg anymore in Postfix context easily, 
                // but SS.I.saveSubDirAsArg is usually set before LoadGame.
                // Or we can just use the global config.
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                
                Debug.Log($"[HistoryTab] Postfix_LoadGame running. Loading history from: {saveDir}");
                HistoryData.Load(saveDir);
                
                // Double check if it worked
                var univ = __instance.GetCurrentUniverse();
                string hist = HistoryData.GetHistory(univ);
                Debug.Log($"[HistoryTab] Loaded history verification for {univ?.name}: {(string.IsNullOrEmpty(hist) ? "EMPTY" : "FOUND (" + hist.Length + " chars)")}");
            }
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
