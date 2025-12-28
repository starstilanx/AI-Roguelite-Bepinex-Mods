using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using TMPro;
using System.Collections.Generic;
using HarmonyLib;

namespace AIROG_HistoryTab
{
    public static class HistoryUI
    {
        public static NgHistory CurrentNgHistory { get; private set; }

        public static void InjectIntoNewWorldModal(NewWorldModal modal)
        {
            if (modal == null) { Debug.Log("[HistoryTab] Modal is null"); return; }
            InjectIntoNgUIInternal(
                modal.tabsManager,
                modal.loreTabBtnTrans,
                modal.ngLorebook,
                modal.univNameInput,
                modal.univDescInput,
                modal.worldNameInput,
                modal.worldBkgdInput,
                "NewWorldModal"
            );
        }

        public static void InjectIntoMainMenu(MainMenu menu)
        {
            if (menu == null) { Debug.Log("[HistoryTab] MainMenu is null"); return; }
            if (menu.menuModal == null) { Debug.Log("[HistoryTab] MainMenu menuModal is null"); return; }

            var tabsManager = menu.menuModal.GetComponentInChildren<TabsManager>();
            if (tabsManager == null) { Debug.Log("[HistoryTab] MainMenu TabsManager not found on menuModal"); return; }
            
            var ezTabs = tabsManager as EzTabsManager;
            if (ezTabs == null) { Debug.Log("[HistoryTab] MainMenu TabsManager is not an EzTabsManager"); return; }

            // Find Lore Button and Content manually if not in fields
            // In typical EzTabsManager, buttons are children of tabButtonHolder
            // and content is child of tabContentHolder.
            Transform loreContent = menu.ngLorebook.transform;
            while (loreContent != null && !loreContent.name.Contains("ContentHolder")) loreContent = loreContent.parent;
            
            if (loreContent == null) loreContent = menu.ngLorebook.transform; // Fallback

            // Find matching button index
            int loreIdx = -1;
            if (ezTabs.tabContentHolder != null)
            {
                for (int i = 0; i < ezTabs.tabContentHolder.childCount; i++)
                {
                    if (ezTabs.tabContentHolder.GetChild(i) == loreContent)
                    {
                        loreIdx = i;
                        break;
                    }
                }
            }

            Transform loreBtn = null;
            if (loreIdx != -1 && ezTabs.tabButtonHolder != null && loreIdx < ezTabs.tabButtonHolder.childCount)
            {
                loreBtn = tabsManager.tabButtonHolder.GetChild(loreIdx);
            }

            if (loreBtn == null)
            {
                Debug.Log("[HistoryTab] MainMenu could not find lore button by index. Using fallback search...");
                foreach (Transform t in tabsManager.tabButtonHolder)
                {
                    if (t.name.ToLower().Contains("lore")) { loreBtn = t; break; }
                }
            }

            if (loreBtn == null) loreBtn = tabsManager.tabButtonHolder.GetChild(0); // High risk fallback

            InjectIntoNgUIInternal(
                tabsManager,
                loreBtn,
                menu.ngLorebook,
                menu.univNameTextInput,
                menu.univDescTextInput,
                menu.worldNameTextInput,
                menu.worldBackgroundTextInput,
                "MainMenu"
            );
        }

        private static void InjectIntoNgUIInternal(
            TabsManager tabsManager, 
            Transform loreBtn, 
            NgLorebook ngLorebook,
            TMP_InputField univNameInput,
            TMP_InputField univDescInput,
            TMP_InputField worldNameInput,
            TMP_InputField worldBkgdInput,
            string logContext)
        {
            Debug.Log($"[HistoryTab] {logContext} InjectIntoNgUIInternal started. TM:{tabsManager!=null}, LB:{loreBtn!=null}, NL:{ngLorebook!=null}");

            if (tabsManager == null || loreBtn == null || ngLorebook == null)
            {
                Debug.Log($"[HistoryTab] {logContext} Injection aborted: Missing required components");
                return;
            }

            var ezTabs = tabsManager as EzTabsManager;
            if (ezTabs == null) return;

            // Check if already injected
            if (tabsManager.tabButtonHolder?.Find("HistoryTabButton") != null) return;
            
            Debug.Log($"[HistoryTab] {logContext} Commencing injection steps...");

            // 1. Clone Lore Tab Button
            GameObject historyBtnObj = Object.Instantiate(loreBtn.gameObject, tabsManager.tabButtonHolder);
            historyBtnObj.name = "HistoryTabButton";
            
            // Fix icon if it's currently a folder (folder is usually index 7/8, scroll is 2/3)
            var img = historyBtnObj.GetComponentInChildren<Image>();
            if (img != null && loreBtn.GetComponentInChildren<Image>() != null)
            {
                img.sprite = loreBtn.GetComponentInChildren<Image>().sprite;
                img.color = loreBtn.GetComponentInChildren<Image>().color;
            }
            
            var btnText = historyBtnObj.GetComponentInChildren<TMP_Text>();
            if (btnText != null) btnText.text = "History";
            
            // 2. Clone Lore Content Page
            Transform loreContent = ngLorebook.transform;
            while (loreContent != null && !loreContent.name.Contains("ContentHolder") && (ezTabs.tabContentHolder == null || loreContent.parent != ezTabs.tabContentHolder)) 
            {
                loreContent = loreContent.parent;
            }
            if (loreContent == null) loreContent = ngLorebook.transform;

            GameObject historyContentObj = Object.Instantiate(loreContent.gameObject, ezTabs.tabContentHolder);
            historyContentObj.name = "HistoryContentHolder";
            
            // 3. Setup NgHistory
            var ngHistory = historyContentObj.AddComponent<NgHistory>();
            CurrentNgHistory = ngHistory;
            ngHistory.univNameTextInput = univNameInput;
            ngHistory.univDescTextInput = univDescInput;
            ngHistory.worldNameTextInput = worldNameInput;
            ngHistory.worldBkgdTextInput = worldBkgdInput;

            // Cleanup the clone manually to ensure no mixed references
            var clonedLorebook = historyContentObj.GetComponent<NgLorebook>();
            Transform entriesParent = null;

            if (clonedLorebook != null)
            {
                entriesParent = clonedLorebook.entriesParent;
                
                // Set Header Title
                var titleTxt = historyContentObj.GetComponentsInChildren<TMP_Text>(true)
                    .FirstOrDefault(t => t.text.Contains("Lorebook") || t.gameObject.name.Contains("Title"));
                if (titleTxt != null) titleTxt.text = "World History";

                // Hide bits we don't want
                if (clonedLorebook.addBtnHolder != null) clonedLorebook.addBtnHolder.gameObject.SetActive(false);

                // Find and Hook up Header Buttons
                var allButtons = historyContentObj.GetComponentsInChildren<Button>(true);
                
                // ? Button
                var helpBtn = allButtons.FirstOrDefault(b => b.gameObject.name.Contains("Help") || b.gameObject.name.Contains("Info"));
                if (helpBtn != null)
                {
                    helpBtn.onClick.RemoveAllListeners();
                    helpBtn.onClick.AddListener(() => ngHistory.OnHistoryHelpClicked());
                    Debug.Log("[HistoryTab] Hooked up Help button.");
                }

                // * (Sparkle) Button
                var aiBtn = allButtons.FirstOrDefault(b => b.gameObject.name.Contains("AiGen") || b.gameObject.name.Contains("Regen") && b != clonedLorebook.clearBtn);
                if (aiBtn != null)
                {
                    aiBtn.onClick.RemoveAllListeners();
                    aiBtn.onClick.AddListener(() => ngHistory.OnRegenHistory());
                    Debug.Log("[HistoryTab] Hooked up Sparkle button.");
                }

                // Trash Button (clonedLorebook.clearBtn)
                if (clonedLorebook.clearBtn != null) 
                {
                    clonedLorebook.clearBtn.onClick.RemoveAllListeners();
                    clonedLorebook.clearBtn.onClick.AddListener(() => ngHistory.OnClearHistory());
                    Debug.Log("[HistoryTab] Hooked up Clear (Trash) button.");
                }

                Object.DestroyImmediate(clonedLorebook);
            }

            // If we didn't find entriesParent via the component, search by name
            if (entriesParent == null)
            {
                entriesParent = historyContentObj.transform.Find("Viewport/Content") ?? 
                                historyContentObj.transform.Find("Scroll View/Viewport/Content") ??
                                historyContentObj.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Content" || t.name == "EntriesParent");
            }

            if (entriesParent != null)
            {
                Debug.Log($"[HistoryTab] {logContext} Found entriesParent: {entriesParent.name}. Cleaning and adding HistoryInput.");
                foreach (Transform child in entriesParent) Object.Destroy(child.gameObject);
                
                // Configure Layout Groups
                var vlg = entriesParent.GetComponent<VerticalLayoutGroup>();
                if (vlg != null) 
                {
                    vlg.enabled = true;
                    vlg.childControlHeight = true;
                    vlg.childForceExpandHeight = true;
                    vlg.padding = new RectOffset(10, 10, 10, 10);
                }
                var csf = entriesParent.GetComponent<ContentSizeFitter>();
                if (csf != null) 
                {
                    csf.enabled = true;
                    csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }

                // Create the history input field
                GameObject historyInputObj = Object.Instantiate(worldBkgdInput.gameObject, entriesParent);
                historyInputObj.name = "HistoryInput";
                
                // Cleanup any existing layout elements from clone
                var oldLe = historyInputObj.GetComponent<LayoutElement>();
                if (oldLe != null) Object.DestroyImmediate(oldLe);

                var le = historyInputObj.AddComponent<LayoutElement>();
                le.minHeight = 600f; // Increased height
                le.flexibleHeight = 1f;

                var input = historyInputObj.GetComponent<TMP_InputField>();
                input.text = LoadTempHistory(); // Restore if crashed
                input.placeholder.GetComponent<TMP_Text>().text = "A world history will be generated or can be written here...";
                input.lineType = TMP_InputField.LineType.MultiLineNewline;
                ngHistory.historyInput = input;

                // Save temp on change
                input.onValueChanged.AddListener((val) => SaveTempHistory(val));

                // Refresh everything
                Utils.DeepRefreshLayout(entriesParent);
                Debug.Log($"[HistoryTab] {logContext} HistoryInput added and layout refreshed. Restore length: {input.text.Length}");
            }
            else
            {
                Debug.LogError($"[HistoryTab] {logContext} FAILED to find entriesParent for history input!");
            }

            // Re-initialize TabsManager
            var startMethod = AccessTools.Method(typeof(TabsManager), "Start");
            if (startMethod != null) startMethod.Invoke(tabsManager, null);
            
            Debug.Log($"[HistoryTab] {logContext} Injection complete.");
        }

        public static void InjectIntoJournalModal(JournalModal modal)
        {
            if (modal == null) return;
            if (modal.tabBtnsHolder == null) return;
            if (modal.tabBtnsHolder.Find("HistoryTabButton") != null) return;

            Debug.Log("[HistoryTab] Injecting into JournalModal...");

            // 1. Clone Ref Button (usually Quests or Lore)
            Transform refBtn = modal.tabBtnsHolder.GetChild(0);
            GameObject historyBtnObj = Object.Instantiate(refBtn.gameObject, modal.tabBtnsHolder);
            historyBtnObj.name = "HistoryTabButton";
            historyBtnObj.transform.SetAsLastSibling(); // Put it at the end
            
            // Remove localization so we can set text manually
            var loc = historyBtnObj.GetComponentInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>();
            if (loc != null) Object.DestroyImmediate(loc);

            var btnText = historyBtnObj.GetComponentInChildren<TMP_Text>();
            if (btnText != null) btnText.text = "History";

            // Grab a valid font asset from the button or modal to ensure we use the game's font
            TMP_FontAsset commonFont = btnText != null ? btnText.font : null;

            // 2. Add Content View (Must contain "TabView" in name for JournalModal.TabTranses to find it)
            GameObject historyViewObj = new GameObject("HistoryTabView_Mod", typeof(RectTransform));
            historyViewObj.transform.SetParent(modal.tabTransesHolder, false);
            historyViewObj.transform.SetAsLastSibling();
            historyViewObj.SetActive(false);
            
            RectTransform rt = historyViewObj.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            
            var bg = historyViewObj.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.15f, 0.15f, 0.95f); // Opaque background

            var scrollViewObj = new GameObject("Scroll View", typeof(RectTransform));
            scrollViewObj.transform.SetParent(historyViewObj.transform, false);
            var scrollRect = scrollViewObj.AddComponent<ScrollRect>();
            
            var scrollRectT = scrollViewObj.GetComponent<RectTransform>();
            scrollRectT.anchorMin = Vector2.zero;
            scrollRectT.anchorMax = Vector2.one;
            scrollRectT.offsetMin = new Vector2(20, 20); // Add some padding
            scrollRectT.offsetMax = new Vector2(-20, -20);

            var viewportObj = new GameObject("Viewport", typeof(RectTransform));
            viewportObj.transform.SetParent(scrollViewObj.transform, false);
            
            // USE RECTMASK2D INSTEAD OF IMAGE+MASK TO AVOID ALPHA ISSUES
            viewportObj.AddComponent<RectMask2D>();
            
            var viewportRect = viewportObj.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;

            var contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(viewportObj.transform, false);
            var contentRect = contentObj.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 500); 
            
            var fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            
            var layout = contentObj.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(20, 20, 20, 20);

            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 20f;
            scrollRect.movementType = ScrollRect.MovementType.Elastic;

            var histTextObj = new GameObject("HistoryText", typeof(RectTransform));
            histTextObj.transform.SetParent(contentObj.transform, false);
            
            var histText = histTextObj.AddComponent<TextMeshProUGUI>();
            histText.fontSize = 24; // Slightly larger
            histText.color = new Color(0.9f, 0.9f, 0.8f); // Off-white
            histText.alignment = TextAlignmentOptions.TopLeft;
            histText.text = "No history found for this universe.";
            if (commonFont != null) histText.font = commonFont;
            
            histText.enableWordWrapping = true;
            histText.overflowMode = TextOverflowModes.Overflow;
        }

        private static string GetTempPath() => System.IO.Path.Combine(Application.persistentDataPath, "temp_world_history_mod.txt");

        public static void SaveTempHistory(string text)
        {
            try { System.IO.File.WriteAllText(GetTempPath(), text); } catch { }
        }

        public static string LoadTempHistory()
        {
            try 
            { 
                string path = GetTempPath();
                if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
            } catch { }
            return "";
        }
    }
}
