using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_GenContext.DMNotes
{
    public static class DmNotesPanel
    {
        private static GameObject _panelObj;
        private static bool _isOpen = false;

        // Analysis section labels
        private static TextMeshProUGUI _stateLabel;
        private static TextMeshProUGUI _pacingLabel;
        private static TextMeshProUGUI _engagementLabel;

        // Scrollable list containers
        private static Transform _plotListContent;
        private static Transform _prefListContent;

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance) => Create(__instance);
        }

        public static void Create(MainLayouts layout)
        {
            if (layout == null || layout.mainHolder == null) return;
            if (layout.mainHolder.Find("DmNotesPanel") != null) return;

            // ---- Root panel ----
            _panelObj = new GameObject("DmNotesPanel", typeof(RectTransform), typeof(Image));
            _panelObj.transform.SetParent(layout.mainHolder, false);

            var root = _panelObj.GetComponent<RectTransform>();
            // Anchor: top-right corner area, 260px wide, tall
            root.anchorMin = new Vector2(0.738f, 0.014f);  // x≈756/1024, y≈8/559
            root.anchorMax = new Vector2(0.992f, 0.986f);  // x≈1016/1024, y≈551/559
            root.offsetMin = root.offsetMax = Vector2.zero;

            var bg = _panelObj.GetComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.10f, 0.92f);

            // ---- Header ----
            GameObject header = MakeRect("Header", _panelObj.transform);
            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 0.93f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.offsetMin = headerRect.offsetMax = Vector2.zero;
            header.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.18f, 1f);

            var titleObj = MakeText("Title", header.transform, "DM Director", 14, FontStyles.Bold,
                new Color(0.95f, 0.85f, 0.5f), TextAlignmentOptions.Left);
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.04f, 0f);
            titleRect.anchorMax = new Vector2(0.78f, 1f);
            titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;

            // Close button
            GameObject closeBtn = MakeRect("CloseBtn", header.transform, typeof(Image), typeof(Button));
            closeBtn.GetComponent<Image>().color = Color.clear;
            closeBtn.GetComponent<Button>().onClick.AddListener(Toggle);
            var closeRect = closeBtn.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.82f, 0.1f);
            closeRect.anchorMax = new Vector2(0.98f, 0.9f);
            closeRect.offsetMin = closeRect.offsetMax = Vector2.zero;
            var xTxt = MakeText("X", closeBtn.transform, "×", 16, FontStyles.Bold,
                new Color(0.9f, 0.3f, 0.3f), TextAlignmentOptions.Center);
            StretchFull(xTxt.GetComponent<RectTransform>());

            // ---- Analysis section (top 30%) ----
            GameObject analysisSection = MakeRect("AnalysisSection", _panelObj.transform);
            var aRect = analysisSection.GetComponent<RectTransform>();
            aRect.anchorMin = new Vector2(0f, 0.63f);
            aRect.anchorMax = new Vector2(1f, 0.93f);
            aRect.offsetMin = aRect.offsetMax = Vector2.zero;

            var aHeader = MakeText("AHeader", analysisSection.transform, "▸ Analysis", 11,
                FontStyles.Bold, new Color(0.7f, 0.9f, 0.7f), TextAlignmentOptions.Left);
            var ahRect = aHeader.GetComponent<RectTransform>();
            ahRect.anchorMin = new Vector2(0.03f, 0.82f);
            ahRect.anchorMax = new Vector2(1f, 1f);
            ahRect.offsetMin = ahRect.offsetMax = Vector2.zero;

            _stateLabel = MakeText("StateLabel", analysisSection.transform, "Engagement: —", 10,
                FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft).GetComponent<TextMeshProUGUI>();
            SetAnchors(_stateLabel.GetComponent<RectTransform>(), 0.03f, 0.57f, 1f, 0.82f);
            _stateLabel.enableWordWrapping = true;

            _pacingLabel = MakeText("PacingLabel", analysisSection.transform, "Pacing: —", 10,
                FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft).GetComponent<TextMeshProUGUI>();
            SetAnchors(_pacingLabel.GetComponent<RectTransform>(), 0.03f, 0.32f, 1f, 0.57f);

            _engagementLabel = MakeText("EngageLabel", analysisSection.transform, "", 9,
                FontStyles.Normal, new Color(0.75f, 0.75f, 0.75f), TextAlignmentOptions.TopLeft).GetComponent<TextMeshProUGUI>();
            SetAnchors(_engagementLabel.GetComponent<RectTransform>(), 0.03f, 0f, 1f, 0.32f);
            _engagementLabel.enableWordWrapping = true;

            // ---- Plot Threads section (middle 33%) ----
            GameObject plotSection = MakeRect("PlotSection", _panelObj.transform);
            var pRect = plotSection.GetComponent<RectTransform>();
            pRect.anchorMin = new Vector2(0f, 0.31f);
            pRect.anchorMax = new Vector2(1f, 0.63f);
            pRect.offsetMin = pRect.offsetMax = Vector2.zero;

            var pHeader = MakeText("PHeader", plotSection.transform, "▸ Plot Threads", 11,
                FontStyles.Bold, new Color(0.7f, 0.85f, 1f), TextAlignmentOptions.Left);
            SetAnchors(pHeader.GetComponent<RectTransform>(), 0.03f, 0.88f, 1f, 1f);

            _plotListContent = MakeScrollList("PlotScroll", plotSection.transform,
                new Vector2(0f, 0f), new Vector2(1f, 0.88f));

            // ---- Preferences section (bottom 31%) ----
            GameObject prefSection = MakeRect("PrefSection", _panelObj.transform);
            var prRect = prefSection.GetComponent<RectTransform>();
            prRect.anchorMin = new Vector2(0f, 0f);
            prRect.anchorMax = new Vector2(1f, 0.31f);
            prRect.offsetMin = prRect.offsetMax = Vector2.zero;

            var prHeader = MakeText("PrHeader", prefSection.transform, "▸ Player Profile", 11,
                FontStyles.Bold, new Color(1f, 0.85f, 0.6f), TextAlignmentOptions.Left);
            SetAnchors(prHeader.GetComponent<RectTransform>(), 0.03f, 0.88f, 1f, 1f);

            _prefListContent = MakeScrollList("PrefScroll", prefSection.transform,
                new Vector2(0f, 0f), new Vector2(1f, 0.88f));

            // ---- Behaviour component for Update loop ----
            _panelObj.AddComponent<DmNotesPanelBehaviour>();

            _panelObj.SetActive(false);
            RefreshUI();
        }

        private static Transform MakeScrollList(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            GameObject scrollObj = MakeRect(name, parent, typeof(ScrollRect), typeof(Image));
            scrollObj.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);
            SetAnchors(scrollObj.GetComponent<RectTransform>(), anchorMin.x, anchorMin.y, anchorMax.x, anchorMax.y);

            GameObject viewport = MakeRect("Viewport", scrollObj.transform, typeof(RectMask2D));
            StretchFull(viewport.GetComponent<RectTransform>());

            GameObject content = MakeRect("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 2f;
            vlg.padding = new RectOffset(4, 4, 2, 2);
            var csf = content.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var cRect = content.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0f, 1f);
            cRect.anchorMax = new Vector2(1f, 1f);
            cRect.pivot = new Vector2(0.5f, 1f);
            cRect.offsetMin = cRect.offsetMax = Vector2.zero;

            var sr = scrollObj.GetComponent<ScrollRect>();
            sr.viewport = viewport.GetComponent<RectTransform>();
            sr.content = cRect;
            sr.horizontal = false;
            sr.vertical = true;
            sr.scrollSensitivity = 15f;
            sr.movementType = ScrollRect.MovementType.Clamped;

            return content.transform;
        }

        public static void Toggle()
        {
            if (_panelObj == null) return;
            _isOpen = !_isOpen;
            _panelObj.SetActive(_isOpen);
            if (_isOpen) RefreshUI();
        }

        public static void RefreshUI()
        {
            if (_panelObj == null) return;

            var state = DmNotesManager.CurrentState;

            if (_stateLabel != null)
                _stateLabel.text = $"Engagement: <b>{state.PlayerState}</b>";
            if (_pacingLabel != null)
                _pacingLabel.text = $"Pacing: <b>{state.PacingDecision}</b>";
            if (_engagementLabel != null)
                _engagementLabel.text = state.EngagementAnalysis;

            RefreshList(_plotListContent, state.PlotThreads, isPlot: true);
            RefreshList(_prefListContent, state.PreferenceNotes, isPlot: false);
        }

        private static void RefreshList(Transform content, List<string> items, bool isPlot)
        {
            if (content == null) return;

            for (int i = content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);

            if (items.Count == 0)
            {
                MakeText("Empty", content, "(none)", 9, FontStyles.Italic,
                    new Color(0.5f, 0.5f, 0.5f), TextAlignmentOptions.Left);
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                int idx = i;
                GameObject row = MakeRect($"Row_{i}", content, typeof(LayoutElement));
                row.GetComponent<LayoutElement>().minHeight = 18f;

                var rowTxt = MakeText("T", row.transform, items[i], 9, FontStyles.Normal,
                    Color.white, TextAlignmentOptions.TopLeft);
                rowTxt.GetComponent<TextMeshProUGUI>().enableWordWrapping = true;
                var rTxtRect = rowTxt.GetComponent<RectTransform>();
                rTxtRect.anchorMin = Vector2.zero;
                rTxtRect.anchorMax = new Vector2(0.82f, 1f);
                rTxtRect.offsetMin = rTxtRect.offsetMax = Vector2.zero;

                // Delete button
                GameObject delBtn = MakeRect("Del", row.transform, typeof(Image), typeof(Button));
                delBtn.GetComponent<Image>().color = new Color(0.5f, 0.1f, 0.1f, 0.7f);
                var delRect = delBtn.GetComponent<RectTransform>();
                delRect.anchorMin = new Vector2(0.84f, 0.1f);
                delRect.anchorMax = new Vector2(1f, 0.9f);
                delRect.offsetMin = delRect.offsetMax = Vector2.zero;
                var delTxt = MakeText("X", delBtn.transform, "×", 9, FontStyles.Bold,
                    Color.white, TextAlignmentOptions.Center);
                StretchFull(delTxt.GetComponent<RectTransform>());

                delBtn.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (isPlot) DmNotesManager.RemovePlotThread(idx);
                    else DmNotesManager.RemovePreference(idx);
                    RefreshUI();
                });
            }
        }

        // ---- Helpers ----

        private static GameObject MakeRect(string name, Transform parent, params Type[] components)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            foreach (var c in components) obj.AddComponent(c);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static GameObject MakeText(string name, Transform parent, string text,
            int fontSize, FontStyles style, Color color, TextAlignmentOptions align)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.alignment = align;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            return obj;
        }

        private static void StretchFull(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private static void SetAnchors(RectTransform r, float minX, float minY, float maxX, float maxY)
        {
            r.anchorMin = new Vector2(minX, minY);
            r.anchorMax = new Vector2(maxX, maxY);
            r.offsetMin = r.offsetMax = Vector2.zero;
        }
    }

    public class DmNotesPanelBehaviour : MonoBehaviour
    {
        private void Update()
        {
            if (DmNotesManager.ConsumeUiDirty())
                DmNotesPanel.RefreshUI();
        }
    }
}
