using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using TMPro;
using System.Collections.Generic;
using HarmonyLib;

namespace AIROG_WorldExpansion
{
    public static class WorldEventsUI
    {
        private static bool _isDirty = true;
        private static GameObject _contentObj; // Reference to content holder to rebuild list
        private static TMP_FontAsset _commonFont;

        public static void MarkDirty()
        {
            _isDirty = true;
        }

        [HarmonyPatch(typeof(JournalModal), "Init")]
        [HarmonyPostfix]
        public static void Postfix_JournalModal_Init(JournalModal __instance)
        {
            InjectIntoJournalModal(__instance);
        }

        [HarmonyPatch(typeof(JournalModal), "UnsetTabTransesAndBtns")]
        [HarmonyPostfix]
        public static void Postfix_UnsetTabTransesAndBtns(JournalModal __instance)
        {
            Transform tabBtn = __instance.tabBtnsHolder.Find("WorldNewsTabButton");
            if (tabBtn != null)
            {
                var img = tabBtn.GetComponentInChildren<Image>();
                if (img != null) img.color = Utils.GetColorFromStr(JournalModal.UNSELECTED_TAB_COLOR_STR);
            }
            
            Transform view = __instance.tabTransesHolder.Find("WorldNewsTabView_Mod");
            if (view != null) view.gameObject.SetActive(false);
        }

        public static void InjectIntoJournalModal(JournalModal modal)
        {
            if (modal == null || modal.tabBtnsHolder == null || modal.tabTransesHolder == null) return;

            // 1. Ensure/Create our custom View
            Transform view = modal.tabTransesHolder.Find("WorldNewsTabView_Mod");
            if (view == null)
            {
                Debug.Log("[WorldExpansion] Creating World News View...");
                GameObject viewObj = new GameObject("WorldNewsTabView_Mod", typeof(RectTransform));
                viewObj.transform.SetParent(modal.tabTransesHolder, false);
                viewObj.layer = modal.gameObject.layer;
                
                // Position it as index 7 if possible to match the 8th button in simple TabTranses scans
                // (Though we will handle the click explicitly)
                if (modal.tabTransesHolder.childCount >= 8)
                {
                    viewObj.transform.SetSiblingIndex(7);
                }
                
                RectTransform rt = viewObj.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                
                var bg = viewObj.AddComponent<Image>();
                bg.color = new Color(0.1f, 0.1f, 0.12f, 0.95f);

                // Scroll View construction...
                var scrollViewObj = new GameObject("Scroll View", typeof(RectTransform));
                scrollViewObj.transform.SetParent(viewObj.transform, false);
                scrollViewObj.layer = viewObj.layer;
                var scrollRect = scrollViewObj.AddComponent<ScrollRect>();
                
                var scrollRectT = scrollViewObj.GetComponent<RectTransform>();
                scrollRectT.anchorMin = Vector2.zero;
                scrollRectT.anchorMax = Vector2.one;
                scrollRectT.offsetMin = new Vector2(24, 24);
                scrollRectT.offsetMax = new Vector2(-24, -24);

                var viewportObj = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
                viewportObj.transform.SetParent(scrollViewObj.transform, false);
                viewportObj.layer = viewObj.layer;
                var viewportRect = viewportObj.GetComponent<RectTransform>();
                viewportRect.anchorMin = Vector2.zero;
                viewportRect.anchorMax = Vector2.one;
                viewportRect.offsetMin = Vector2.zero;
                viewportRect.offsetMax = Vector2.zero;

                GameObject contentObj = new GameObject("Content", typeof(RectTransform));
                contentObj.transform.SetParent(viewportObj.transform, false);
                contentObj.layer = viewObj.layer;
                var contentRect = contentObj.GetComponent<RectTransform>();
                contentRect.anchorMin = new Vector2(0, 1);
                contentRect.anchorMax = new Vector2(1, 1);
                contentRect.pivot = new Vector2(0.5f, 1);
                contentRect.sizeDelta = new Vector2(0, 500);
                
                var vlg = contentObj.AddComponent<VerticalLayoutGroup>();
                vlg.childControlHeight = true;
                vlg.childControlWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.childForceExpandWidth = true;
                vlg.spacing = 10;
                vlg.padding = new RectOffset(10, 10, 10, 10);

                var csf = contentObj.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                scrollRect.content = contentRect;
                scrollRect.viewport = viewportRect;
                scrollRect.vertical = true;
                scrollRect.horizontal = false;
                scrollRect.scrollSensitivity = 25f;

                _contentObj = contentObj;
                viewObj.SetActive(false);
            }
            else
            {
                _contentObj = view.Find("Scroll View/Viewport/Content")?.gameObject;
            }

            // 2. Hijack the 8th Tab Button (Index 7)
            if (modal.tabBtnsHolder.childCount < 8)
            {
                Debug.LogWarning("[WorldExpansion] Journal does not have 8 tabs. Reverting to injection.");
                // Fallback to my previous logic of adding a button if somehow it's not there
                if (modal.tabBtnsHolder.Find("WorldNewsTabButton") == null)
                {
                    Transform refBtn = modal.tabBtnsHolder.GetChild(0);
                    GameObject newBtn = Object.Instantiate(refBtn.gameObject, modal.tabBtnsHolder);
                    newBtn.name = "WorldNewsTabButton";
                    SetupTabButton(newBtn.GetComponent<Button>(), modal);
                }
                return;
            }

            Transform tab8 = modal.tabBtnsHolder.GetChild(7);
            Debug.Log($"[WorldExpansion] Hijacking Journal Tab 8: {tab8.name}");
            tab8.name = "WorldNewsTabButton"; // Rename for consistency
            
            Button btn = tab8.GetComponent<Button>();
            SetupTabButton(btn, modal);
        }

        private static void SetupTabButton(Button btn, JournalModal modal)
        {
            // Remove Localization
            var loc = btn.GetComponentInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>();
            if (loc != null) Object.DestroyImmediate(loc);

            var btnText = btn.GetComponentInChildren<TMP_Text>();
            if (btnText != null) 
            {
                btnText.text = "World News";
                _commonFont = btnText.font;
            }

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => {
                modal.manager.soundManager.smallClickSoundFxObj.PlayNextSound();
                modal.UnsetTabTransesAndBtns();
                
                var img = btn.GetComponentInChildren<Image>();
                if (img != null) img.color = Utils.GetColorFromStr(JournalModal.SELECTED_TAB_COLOR_STR);
                
                Transform v = modal.tabTransesHolder.Find("WorldNewsTabView_Mod");
                if (v != null) 
                {
                    v.gameObject.SetActive(true);
                    _isDirty = true; 
                    RefreshView();
                }
            });
        }

        private static void RefreshView()
        {
            Debug.Log($"[WorldExpansion] Refreshing World News View. Dirty: {_isDirty}, ContentObj: {_contentObj != null}");
            if (!_isDirty || _contentObj == null) return;

            // Clear old children
            for (int i = _contentObj.transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(_contentObj.transform.GetChild(i).gameObject);
            }

            // Market Status Header
            var market = WorldData.CurrentState.Market;
            string marketColorHex = "#FFFFFF";
            if (market.GlobalCondition == "Shortage") marketColorHex = "#FF8888"; 
            else if (market.GlobalCondition == "Surplus") marketColorHex = "#88FF88"; 
            else if (market.GlobalCondition == "Inflation") marketColorHex = "#FFFF88"; 
            else if (market.GlobalCondition == "Depression") marketColorHex = "#8888FF"; 

            CreateTextEntry("<b>Global Economy</b>", 28, Color.white);
            CreateTextEntry($"Condition: <color={marketColorHex}>{market.GlobalCondition}</color>", 24, Color.gray);
            CreateTextEntry($"Buy Price: {market.PriceMultiplier:P0} | Sell Price: {market.SellMultiplier:P0}", 20, Color.gray);
            
            CreateTextEntry("------------------------------------------------", 20, Color.gray);

            CreateTextEntry("<b>Latest World Events</b>", 32, new Color(1f, 0.8f, 0.2f));
            CreateTextEntry("------------------------------------------------", 20, Color.gray);

            if (WorldData.CurrentState.Events.Count == 0)
            {
                CreateTextEntry("No significant world events have occurred yet. Try skipping some turns or using console commands like WORLD_SIM_TEST.", 24, Color.white);
            }
            else
            {
                var events = WorldData.CurrentState.Events.OrderByDescending(e => e.Turn).ToList();
                foreach (var evt in events)
                {
                    Color c = Color.white;
                    float size = 24;
                    string prefix = "";

                    if (evt.Type == "MAJOR") 
                    {
                        c = new Color(1f, 0.8f, 0f); // Gold
                        size = 32;
                        prefix = "<b>[MAJOR EVENT]</b> ";
                    }
                    else if (evt.Type == "WAR") c = new Color(1f, 0.4f, 0.4f);
                    else if (evt.Type == "TRADE") c = new Color(0.4f, 1f, 0.6f);
                    else if (evt.Type == "RUMOR") c = new Color(0.8f, 0.8f, 1f);
                    else if (evt.Type == "ECONOMY") c = new Color(0.6f, 1f, 1f);

                    CreateTextEntry($"[Turn {evt.Turn}] {prefix}{evt.Description}", size, c);
                }
            }

            _isDirty = false;
            Utils.DeepRefreshLayout(_contentObj.transform);
        }

        private static void CreateTextEntry(string text, float fontSize, Color color)
        {
            GameObject textObj = new GameObject("EventText", typeof(RectTransform));
            textObj.transform.SetParent(_contentObj.transform, false);
            textObj.layer = _contentObj.layer;
            
            var txt = textObj.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.color = color;
            if (_commonFont != null) txt.font = _commonFont;
            txt.enableWordWrapping = true;
            txt.alignment = TextAlignmentOptions.TopLeft;
        }
    }
}
