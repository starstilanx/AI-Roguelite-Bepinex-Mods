using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Chronicle
{
    public static class ChronicleUI
    {
        private static GameObject _panelObj;
        private static bool _isOpen;
        private static Transform _listContent;
        private static TextMeshProUGUI _titleLabel;

        // Which chapters are currently expanded in the list (number -> expanded)
        private static readonly Dictionary<int, bool> _expanded = new Dictionary<int, bool>();

        // ---- Inject panel + button via MainLayouts.InitCommonAnchs ----

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance) => Create(__instance);
        }

        public static void Create(MainLayouts layout)
        {
            if (layout == null) return;

            // Panel (on mainHolder — full-screen root)
            if (layout.mainHolder != null && layout.mainHolder.Find("ChroniclePanel") == null)
                CreatePanel(layout.mainHolder);

            // Button (on buttonsHolderHolder — the HUD button row)
            if (layout.buttonsHolderHolder != null && layout.buttonsHolderHolder.Find("ChronicleBtn") == null)
                InjectButton(layout.buttonsHolderHolder);
        }

        // ---- Panel construction ----

        private static void CreatePanel(Transform mainHolder)
        {
            _panelObj = new GameObject("ChroniclePanel", typeof(RectTransform), typeof(Image));
            _panelObj.transform.SetParent(mainHolder, false);

            var root = _panelObj.GetComponent<RectTransform>();
            // Centered, 60% wide, 90% tall
            root.anchorMin = new Vector2(0.20f, 0.05f);
            root.anchorMax = new Vector2(0.80f, 0.95f);
            root.offsetMin = root.offsetMax = Vector2.zero;

            _panelObj.GetComponent<Image>().color = new Color(0.05f, 0.04f, 0.08f, 0.95f);

            // Header bar
            GameObject header = MakeRect("Header", _panelObj.transform, typeof(Image));
            SetAnchors(header.GetComponent<RectTransform>(), 0f, 0.935f, 1f, 1f);
            header.GetComponent<Image>().color = new Color(0.08f, 0.06f, 0.14f, 1f);

            _titleLabel = MakeText("Title", header.transform, "Chronicle", 14, FontStyles.Bold,
                new Color(0.85f, 0.75f, 1.0f), TextAlignmentOptions.Left).GetComponent<TextMeshProUGUI>();
            SetAnchors(_titleLabel.GetComponent<RectTransform>(), 0.03f, 0f, 0.82f, 1f);

            // Close button
            GameObject closeBtn = MakeRect("CloseBtn", header.transform, typeof(Image), typeof(Button));
            closeBtn.GetComponent<Image>().color = Color.clear;
            closeBtn.GetComponent<Button>().onClick.AddListener(Toggle);
            SetAnchors(closeBtn.GetComponent<RectTransform>(), 0.84f, 0.1f, 0.98f, 0.9f);
            var xTxt = MakeText("X", closeBtn.transform, "\u00d7", 16, FontStyles.Bold,
                new Color(0.9f, 0.3f, 0.3f), TextAlignmentOptions.Center);
            StretchFull(xTxt.GetComponent<RectTransform>());

            // Scrollable chapter list
            _listContent = MakeScrollList("ChronicleScroll", _panelObj.transform,
                new Vector2(0f, 0.07f), new Vector2(1f, 0.935f));

            // Footer bar
            GameObject footer = MakeRect("Footer", _panelObj.transform, typeof(Image));
            SetAnchors(footer.GetComponent<RectTransform>(), 0f, 0f, 1f, 0.07f);
            footer.GetComponent<Image>().color = new Color(0.08f, 0.06f, 0.14f, 1f);

            // Session Recap button
            GameObject recapBtn = MakeRect("RecapBtn", footer.transform, typeof(Image), typeof(Button));
            SetAnchors(recapBtn.GetComponent<RectTransform>(), 0.02f, 0.1f, 0.98f, 0.9f);
            recapBtn.GetComponent<Image>().color = new Color(0.18f, 0.12f, 0.30f, 1f);
            recapBtn.GetComponent<Button>().onClick.AddListener(ShowRecap);
            var recapTxt = MakeText("Label", recapBtn.transform, "Session Recap", 12, FontStyles.Normal,
                new Color(0.90f, 0.82f, 1.0f), TextAlignmentOptions.Center);
            StretchFull(recapTxt.GetComponent<RectTransform>());

            _panelObj.AddComponent<ChroniclePanelBehaviour>();
            _panelObj.SetActive(false);
            Debug.Log("[Chronicle] Panel created.");
        }

        // ---- HUD button ----

        private static void InjectButton(Transform buttonsHolder)
        {
            var btnObj = new GameObject("ChronicleBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(buttonsHolder, false);
            btnObj.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.22f, 0.9f);

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredWidth = 60; le.minWidth = 60;
            le.preferredHeight = 60; le.minHeight = 60;

            var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(btnObj.transform, false);
            var txt = label.GetComponent<TextMeshProUGUI>();
            txt.text = "Chron"; txt.fontSize = 11; txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(0.85f, 0.75f, 1.0f);
            txt.alignment = TextAlignmentOptions.Center;
            txt.enableWordWrapping = false;
            StretchFull(label.GetComponent<RectTransform>());

            btnObj.GetComponent<Button>().onClick.AddListener(Toggle);
            btnObj.transform.SetAsLastSibling();
            Debug.Log("[Chronicle] HUD button injected.");
        }

        // ---- Toggle / Refresh ----

        public static void Toggle()
        {
            if (_panelObj == null) return;
            _isOpen = !_isOpen;
            _panelObj.SetActive(_isOpen);
            if (_isOpen) RefreshUI();
        }

        public static void RefreshUI()
        {
            if (_panelObj == null || _listContent == null) return;

            // Update title with player name
            if (_titleLabel != null)
            {
                string playerName = "Unknown";
                try
                {
                    if (SS.I != null && SS.I.hackyManager != null && SS.I.hackyManager.playerCharacter != null)
                        playerName = SS.I.hackyManager.playerCharacter.name;
                }
                catch { }
                _titleLabel.text = $"Chronicle of {playerName}";
            }

            // Clear existing rows
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);

            var state = ChronicleManager.State;
            if (state == null) return;

            if (state.ClosedChapters != null)
                foreach (var ch in state.ClosedChapters)
                    RenderChapter(ch, isCurrent: false);

            if (state.CurrentChapter != null)
                RenderChapter(state.CurrentChapter, isCurrent: true);
        }

        private static void RenderChapter(Chapter chapter, bool isCurrent)
        {
            // Default: new chapters start expanded
            bool isExpanded = !_expanded.ContainsKey(chapter.Number) || _expanded[chapter.Number];
            string arrow   = isExpanded ? "\u25bc" : "\u25b6";
            string range   = isCurrent ? $"T{chapter.StartTurn}+" : $"T{chapter.StartTurn}\u2013{chapter.EndTurn}";
            string titlePart = string.IsNullOrEmpty(chapter.Title)
                ? (isCurrent ? "(in progress)" : $"Chapter {chapter.Number}")
                : $"\"{chapter.Title}\"";

            // Chapter header row
            GameObject chRow = MakeRect($"Ch_{chapter.Number}", _listContent, typeof(LayoutElement));
            chRow.GetComponent<LayoutElement>().minHeight = 22f;
            chRow.AddComponent<Image>().color = isCurrent
                ? new Color(0.15f, 0.10f, 0.25f, 0.65f)
                : new Color(0.10f, 0.08f, 0.16f, 0.45f);

            // Invisible button covers the whole row for toggling
            GameObject toggleBtn = MakeRect("Toggle", chRow.transform, typeof(Button));
            toggleBtn.AddComponent<Image>().color = Color.clear;
            StretchFull(toggleBtn.GetComponent<RectTransform>());
            int capturedNum = chapter.Number;
            toggleBtn.GetComponent<Button>().onClick.AddListener(() =>
            {
                bool cur2 = !_expanded.ContainsKey(capturedNum) || _expanded[capturedNum];
                _expanded[capturedNum] = !cur2;
                RefreshUI();
            });

            var headerTxt = MakeText("ChLabel", chRow.transform,
                $"  {arrow} Ch.{chapter.Number} {titlePart}  ({range})",
                12, isCurrent ? FontStyles.Bold : FontStyles.Normal,
                isCurrent ? new Color(0.88f, 0.78f, 1.0f) : new Color(0.65f, 0.60f, 0.80f),
                TextAlignmentOptions.Left).GetComponent<TextMeshProUGUI>();
            StretchFull(headerTxt.GetComponent<RectTransform>());

            // Beat rows (only when expanded)
            if (isExpanded && chapter.Beats != null)
            {
                foreach (var beat in chapter.Beats)
                {
                    GameObject beatRow = MakeRect($"Beat_{beat.Turn}_{beat.Type}", _listContent, typeof(LayoutElement));
                    beatRow.GetComponent<LayoutElement>().minHeight = 18f;

                    Color beatColor = beat.IsMilestone
                        ? new Color(1.00f, 0.90f, 0.50f)
                        : new Color(0.72f, 0.70f, 0.78f);

                    var beatTxt = MakeText("Txt", beatRow.transform,
                        $"      T{beat.Turn}  {beat.Summary}{(beat.IsMilestone ? "  \u2605" : "")}",
                        11, FontStyles.Normal, beatColor, TextAlignmentOptions.TopLeft)
                        .GetComponent<TextMeshProUGUI>();
                    beatTxt.enableWordWrapping = true;
                    StretchFull(beatTxt.GetComponent<RectTransform>());
                }
            }
        }

        // ---- Session Recap ----

        private static void ShowRecap()
        {
            var state = ChronicleManager.State;
            string recap = state?.LastSessionRecap;

            if (string.IsNullOrEmpty(recap) && state?.ClosedChapters != null && state.ClosedChapters.Count > 0)
                recap = state.ClosedChapters[state.ClosedChapters.Count - 1].Recap;

            if (string.IsNullOrEmpty(recap))
                recap = "No session recap available yet. Complete a few chapters to generate one.";

            try
            {
                var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                manager?.gameLogView?.LogText($"\n\n\u2014 Chronicle Recap \u2014\n{recap}\n");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] ShowRecap error: {ex.Message}");
            }
        }

        // ---- UI Helpers (mirrored from DmNotesPanel) ----

        private static Transform MakeScrollList(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
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
            vlg.spacing = 1f;
            vlg.padding = new RectOffset(2, 2, 2, 2);
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

    /// <summary>MonoBehaviour that polls for UI dirty flag and triggers a refresh.</summary>
    public class ChroniclePanelBehaviour : MonoBehaviour
    {
        private void Update()
        {
            if (ChronicleManager.ConsumeUiDirty())
                ChronicleUI.RefreshUI();
        }
    }
}
