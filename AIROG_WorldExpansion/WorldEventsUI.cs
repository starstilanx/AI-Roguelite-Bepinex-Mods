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
        private static GameObject _contentObj;
        private static TMP_FontAsset _commonFont;
        private static string _filterType = "All";

        public static void MarkDirty() => _isDirty = true;

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

            Transform view = modal.tabTransesHolder.Find("WorldNewsTabView_Mod");
            if (view == null)
            {
                Debug.Log("[WorldExpansion] Creating World News View...");
                GameObject viewObj = new GameObject("WorldNewsTabView_Mod", typeof(RectTransform));
                viewObj.transform.SetParent(modal.tabTransesHolder, false);
                viewObj.layer = modal.gameObject.layer;

                if (modal.tabTransesHolder.childCount >= 8)
                    viewObj.transform.SetSiblingIndex(7);

                RectTransform rt = viewObj.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                viewObj.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.12f, 0.95f);

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
                contentRect.pivot     = new Vector2(0.5f, 1);
                contentRect.sizeDelta = new Vector2(0, 500);

                var vlg = contentObj.AddComponent<VerticalLayoutGroup>();
                vlg.childControlHeight   = true;
                vlg.childControlWidth    = true;
                vlg.childForceExpandHeight = false;
                vlg.childForceExpandWidth  = true;
                vlg.spacing = 10;
                vlg.padding = new RectOffset(10, 10, 10, 10);

                var csf = contentObj.AddComponent<ContentSizeFitter>();
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                scrollRect.content     = contentRect;
                scrollRect.viewport    = viewportRect;
                scrollRect.vertical    = true;
                scrollRect.horizontal  = false;
                scrollRect.scrollSensitivity = 25f;

                _contentObj = contentObj;
                viewObj.SetActive(false);
            }
            else
            {
                _contentObj = view.Find("Scroll View/Viewport/Content")?.gameObject;
            }

            // Hijack / inject the 8th journal tab
            if (modal.tabBtnsHolder.childCount < 8)
            {
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
            tab8.name = "WorldNewsTabButton";
            SetupTabButton(tab8.GetComponent<Button>(), modal);
        }

        private static void SetupTabButton(Button btn, JournalModal modal)
        {
            var loc = btn.GetComponentInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>();
            if (loc != null) Object.DestroyImmediate(loc);

            var btnText = btn.GetComponentInChildren<TMP_Text>();
            if (btnText != null)
            {
                btnText.text = "World News";
                _commonFont  = btnText.font;
            }

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
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

        // ─── View Refresh ─────────────────────────────────────────────────────────
        private static void RefreshView()
        {
            if (!_isDirty || _contentObj == null) return;

            for (int i = _contentObj.transform.childCount - 1; i >= 0; i--)
                Object.Destroy(_contentObj.transform.GetChild(i).gameObject);

            var state  = WorldData.CurrentState;
            var market = state.Market;

            // ── Season & Turn header ──────────────────────────────────────────────
            string seasonIcon = SeasonIcon(state.CurrentSeason);
            CreateTextEntry($"<b>Turn {state.CurrentTurn}</b>   {seasonIcon} <b>{state.CurrentSeason}</b>", 26, Color.white);
            CreateSeparator();

            // ── Economy block ─────────────────────────────────────────────────────
            CreateTextEntry("<b>Global Economy</b>", 28, Color.white);

            string trendStr   = EconomyTrend(market.GlobalCondition, market.PreviousCondition);
            Color  econColor  = EconColor(market.GlobalCondition);
            CreateTextEntry($"Condition: <color=#{ColorUtility.ToHtmlStringRGB(econColor)}>{market.GlobalCondition}</color>  {trendStr}", 24, Color.gray);
            CreateTextEntry($"Buy: ×{market.PriceMultiplier:0.##}   Sell: ×{market.SellMultiplier:0.##}", 20, Color.gray);
            CreateSeparator();

            // ── Active Wars ───────────────────────────────────────────────────────
            if (state.ActiveWars.Count > 0)
            {
                CreateTextEntry("<b>Active Wars</b>", 26, new Color(1f, 0.4f, 0.4f));
                foreach (var war in state.ActiveWars.Values)
                {
                    int duration = state.CurrentTurn - war.StartTurn;
                    CreateTextEntry($"  ⚔ {war.ActorName}  vs  {war.TargetName}  [{war.CasusBelli}, {duration}t]", 22, new Color(1f, 0.5f, 0.5f));
                }
                CreateSeparator();
            }

            // ── Faction Leaderboard ───────────────────────────────────────────────
            var factionList = state.Factions.Values
                .Where(f => !string.IsNullOrEmpty(f.Name))
                .OrderByDescending(f => f.Resources)
                .Take(6)
                .ToList();

            if (factionList.Count > 0)
            {
                CreateTextEntry("<b>Faction Power Rankings</b>", 26, new Color(0.8f, 0.8f, 1f));
                int rank = 1;
                foreach (var f in factionList)
                {
                    // Check if eliminated
                    string uuid = state.Factions.FirstOrDefault(kv => kv.Value == f).Key;
                    bool eliminated = !string.IsNullOrEmpty(uuid) && state.EliminatedFactions.Contains(uuid);
                    string nameStr = eliminated ? $"<s>{f.Name}</s> [FALLEN]" : f.Name;
                    string tag = f.Tag != "Neutral" ? $" [{f.Tag}]" : "";
                    string regions = f.ClaimedPlaceUuids.Count > 0 ? $" | {f.ClaimedPlaceUuids.Count}r" : "";
                    Color rowColor = eliminated ? new Color(0.4f, 0.4f, 0.4f) : new Color(0.7f, 0.9f, 0.7f);
                    CreateTextEntry($"  {rank}. {nameStr}{tag}  —  {f.Resources} res{regions}", 21, rowColor);
                    rank++;
                }
                CreateSeparator();
            }

            // ── Filter buttons ────────────────────────────────────────────────────
            CreateTextEntry("<b>World Events</b>", 28, new Color(1f, 0.8f, 0.2f));
            CreateFilterBar();
            CreateSeparator();

            // ── Event list ────────────────────────────────────────────────────────
            var events = state.Events.AsEnumerable();
            if (_filterType != "All")
                events = events.Where(e => e.Type == _filterType);
            var eventList = events.OrderByDescending(e => e.Turn).ToList();

            if (eventList.Count == 0)
            {
                string hint = _filterType == "All"
                    ? "No world events yet. Skip turns or test with WORLD_SIM_TEST."
                    : $"No {_filterType} events on record.";
                CreateTextEntry(hint, 22, Color.gray);
            }
            else
            {
                foreach (var evt in eventList)
                {
                    Color c   = EventColor(evt.Type);
                    float sz  = evt.Type == "MAJOR" ? 30f : 22f;
                    string prefix = evt.Type == "MAJOR" ? "<b>[MAJOR] </b>" : "";
                    CreateTextEntry($"[T{evt.Turn}] {prefix}{evt.Description}", sz, c);
                }
            }

            _isDirty = false;
            Utils.DeepRefreshLayout(_contentObj.transform);
        }

        // ─── Filter Bar ───────────────────────────────────────────────────────────
        private static void CreateFilterBar()
        {
            string[] filters = { "All", "MAJOR", "WAR", "TRADE", "ECONOMY", "RUMOR", "SEASON" };

            GameObject barObj = new GameObject("FilterBar", typeof(RectTransform));
            barObj.transform.SetParent(_contentObj.transform, false);
            barObj.layer = _contentObj.layer;

            var le = barObj.AddComponent<LayoutElement>();
            le.minHeight = 34;
            le.preferredHeight = 34;

            var hlg = barObj.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth    = true;
            hlg.childForceExpandWidth = true;
            hlg.spacing  = 4;
            hlg.padding  = new RectOffset(0, 0, 2, 2);

            foreach (var filter in filters)
            {
                string captured = filter;

                GameObject btnObj = new GameObject("Filter_" + filter, typeof(RectTransform), typeof(Button), typeof(Image));
                btnObj.transform.SetParent(barObj.transform, false);
                btnObj.layer = _contentObj.layer;

                bool active = _filterType == filter;
                btnObj.GetComponent<Image>().color = active
                    ? new Color(0.45f, 0.45f, 0.7f)
                    : new Color(0.2f, 0.2f, 0.25f);

                GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                txtObj.transform.SetParent(btnObj.transform, false);
                txtObj.layer = _contentObj.layer;
                var txt = txtObj.GetComponent<TextMeshProUGUI>();
                txt.text      = filter;
                txt.fontSize  = 14;
                txt.alignment = TextAlignmentOptions.Center;
                txt.color     = Color.white;
                if (_commonFont != null) txt.font = _commonFont;

                var btnRt = btnObj.GetComponent<RectTransform>();
                btnRt.anchorMin = Vector2.zero;
                btnRt.anchorMax = Vector2.one;
                var txtRt = txtObj.GetComponent<RectTransform>();
                txtRt.anchorMin = Vector2.zero;
                txtRt.anchorMax = Vector2.one;
                txtRt.offsetMin = Vector2.zero;
                txtRt.offsetMax = Vector2.zero;

                btnObj.GetComponent<Button>().onClick.AddListener(() =>
                {
                    _filterType = captured;
                    _isDirty    = true;
                    RefreshView();
                });
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────
        private static void CreateTextEntry(string text, float fontSize, Color color)
        {
            GameObject textObj = new GameObject("EventText", typeof(RectTransform));
            textObj.transform.SetParent(_contentObj.transform, false);
            textObj.layer = _contentObj.layer;
            var txt = textObj.AddComponent<TextMeshProUGUI>();
            txt.text            = text;
            txt.fontSize        = fontSize;
            txt.color           = color;
            txt.enableWordWrapping = true;
            txt.alignment       = TextAlignmentOptions.TopLeft;
            if (_commonFont != null) txt.font = _commonFont;
        }

        private static void CreateSeparator()
        {
            CreateTextEntry("──────────────────────────────", 18, new Color(0.3f, 0.3f, 0.35f));
        }

        private static Color EventColor(string type)
        {
            switch (type)
            {
                case "MAJOR":   return new Color(1f, 0.8f, 0f);
                case "WAR":     return new Color(1f, 0.4f, 0.4f);
                case "TRADE":   return new Color(0.4f, 1f, 0.6f);
                case "ECONOMY": return new Color(0.6f, 1f, 1f);
                case "RUMOR":   return new Color(0.8f, 0.8f, 1f);
                case "SEASON":  return new Color(0.4f, 0.9f, 0.9f);
                default:        return Color.white;
            }
        }

        private static Color EconColor(string condition)
        {
            switch (condition)
            {
                case "Shortage":   return new Color(1f, 0.5f, 0.5f);
                case "Surplus":    return new Color(0.5f, 1f, 0.5f);
                case "Inflation":  return new Color(1f, 1f, 0.5f);
                case "Depression": return new Color(0.5f, 0.6f, 1f);
                default:           return Color.white;
            }
        }

        private static string EconomyTrend(string current, string previous)
        {
            // Rank: Surplus=0, Normal=1, Inflation=2, Shortage=3, Depression=4 (lower = better for player buying)
            int Rank(string c)
            {
                switch (c)
                {
                    case "Surplus":    return 0;
                    case "Normal":     return 1;
                    case "Inflation":  return 2;
                    case "Shortage":   return 3;
                    case "Depression": return 4;
                    default:           return 1;
                }
            }
            int cur = Rank(current);
            int prev = Rank(previous);
            if (cur < prev)  return "<color=#88FF88>↑ Improving</color>";
            if (cur > prev)  return "<color=#FF8888>↓ Worsening</color>";
            return "<color=#AAAAAA>→ Stable</color>";
        }

        private static string SeasonIcon(string season)
        {
            switch (season)
            {
                case "Spring": return "🌱";
                case "Summer": return "☀";
                case "Autumn": return "🍂";
                case "Winter": return "❄";
                default:       return "";
            }
        }
    }
}
