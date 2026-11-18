using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Chronicle
{
    public static class ChronicleUI
    {
        private static GameObject _panelObj;
        private static GameObject _btnObj;
        private static bool _isOpen;
        private static Transform _listContent;
        private static ScrollRect _scroll;
        private static TextMeshProUGUI _titleLabel;
        private static int _snapToLatestFrames;

        // Which chapters / acts the player expanded or collapsed (chapter number -> expanded).
        // Closed chapters and acts start collapsed: a long save has hundreds of chapters, and
        // only expanded ones get their rows built.
        private static readonly Dictionary<int, bool> _expanded = new Dictionary<int, bool>();
        private static readonly HashSet<int> _expandedActs = new HashSet<int>();

        private const float SCROLLBAR_WIDTH = 12f;

        // ---- Inject panel + button via MainLayouts.InitCommonAnchs ----

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance)
            {
                try
                {
                    // A co-op client runs neither the load nor the new-game hook, so the chronicle
                    // of whatever save was played before joining would still be in memory.
                    if (SS.I != null && SS.I.gameMode == SS.GameMode.MP_CLIENT)
                        ChronicleManager.Reset();

                    Create(__instance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Chronicle] UI injection error: {ex}");
                }
            }
        }

        public static void Create(MainLayouts layout)
        {
            if (layout == null) return;

            // Panel (on mainHolder — full-screen root)
            if (layout.mainHolder != null && layout.mainHolder.Find("ChroniclePanel") == null)
                CreatePanel(layout.mainHolder);

            // Button (on buttonsHolderHolder — the HUD button row)
            if (layout.buttonsHolderHolder != null)
            {
                Transform existing = layout.buttonsHolderHolder.Find("ChronicleBtn");
                if (existing == null) InjectButton(layout.buttonsHolderHolder);
                else _btnObj = existing.gameObject;
            }

            ModSwitch.Hook(ApplySwitch);
            ApplySwitch();
        }

        /// <summary>Shows or hides the HUD button (and closes the panel) to match this save's on/off switch. UI only.</summary>
        private static void ApplySwitch()
        {
            bool on = ModSwitch.Enabled;
            if (_btnObj != null && _btnObj.activeSelf != on) _btnObj.SetActive(on);
            if (!on && _isOpen && _panelObj != null)
            {
                _isOpen = false;
                _panelObj.SetActive(false);
            }
        }

        // ---- Panel construction ----

        private static void CreatePanel(Transform mainHolder)
        {
            // New scene: the panel starts closed, and expansion state belonged to the last save.
            _isOpen = false;
            _expanded.Clear();
            _expandedActs.Clear();

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
            var xTxt = MakeText("X", closeBtn.transform, "×", 16, FontStyles.Bold,
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
            SetAnchors(recapBtn.GetComponent<RectTransform>(), 0.02f, 0.1f, 0.66f, 0.9f);
            recapBtn.GetComponent<Image>().color = new Color(0.18f, 0.12f, 0.30f, 1f);
            recapBtn.GetComponent<Button>().onClick.AddListener(ShowRecap);
            var recapTxt = MakeText("Label", recapBtn.transform, "Session Recap", 12, FontStyles.Normal,
                new Color(0.90f, 0.82f, 1.0f), TextAlignmentOptions.Center);
            StretchFull(recapTxt.GetComponent<RectTransform>());

            // Jump-to-latest button
            GameObject latestBtn = MakeRect("LatestBtn", footer.transform, typeof(Image), typeof(Button));
            SetAnchors(latestBtn.GetComponent<RectTransform>(), 0.68f, 0.1f, 0.98f, 0.9f);
            latestBtn.GetComponent<Image>().color = new Color(0.18f, 0.12f, 0.30f, 1f);
            latestBtn.GetComponent<Button>().onClick.AddListener(ScrollToLatest);
            var latestTxt = MakeText("Label", latestBtn.transform, "Jump to Latest ▼", 12, FontStyles.Normal,
                new Color(0.90f, 0.82f, 1.0f), TextAlignmentOptions.Center);
            StretchFull(latestTxt.GetComponent<RectTransform>());

            _panelObj.AddComponent<ChroniclePanelBehaviour>();
            _panelObj.SetActive(false);
            Debug.Log("[Chronicle] Panel created.");
        }

        // ---- HUD button ----

        private static void InjectButton(Transform buttonsHolder)
        {
            var btnObj = new GameObject("ChronicleBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(buttonsHolder, false);
            _btnObj = btnObj;

            var img = btnObj.GetComponent<Image>();
            Sprite btnSprite = LoadSprite("ChronicleButton.png");
            if (btnSprite != null)
            {
                img.sprite = btnSprite;
                img.color = Color.white;
                img.preserveAspect = true;
            }
            else
            {
                img.color = new Color(0.12f, 0.08f, 0.22f, 0.9f);
                var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                label.transform.SetParent(btnObj.transform, false);
                var txt = label.GetComponent<TextMeshProUGUI>();
                txt.text = "Chron"; txt.fontSize = 11; txt.fontStyle = FontStyles.Bold;
                txt.color = new Color(0.85f, 0.75f, 1.0f);
                txt.alignment = TextAlignmentOptions.Center;
                txt.enableWordWrapping = false;
                StretchFull(label.GetComponent<RectTransform>());
            }

            var le = btnObj.AddComponent<LayoutElement>();
            le.preferredWidth = 60; le.minWidth = 60;
            le.preferredHeight = 60; le.minHeight = 60;

            var btn = btnObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(Toggle);
            btnObj.transform.SetAsLastSibling();
            Debug.Log("[Chronicle] HUD button injected.");
        }

        private static Sprite LoadSprite(string filename)
        {
            try
            {
                string path = System.IO.Path.Combine(Application.streamingAssetsPath, "Chronicle", filename);
                if (System.IO.File.Exists(path))
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    Texture2D tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
                else
                {
                    Debug.LogWarning($"[Chronicle] Button icon not found at {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Failed to load button icon: {ex}");
            }
            return null;
        }

        // ---- Toggle / Refresh ----

        public static void Toggle()
        {
            if (_panelObj == null) return;
            if (!_isOpen && !ModSwitch.Enabled) return;
            _isOpen = !_isOpen;
            _panelObj.SetActive(_isOpen);
            if (_isOpen)
            {
                ChronicleManager.ConsumeUiDirty();
                RefreshUI();
                ScrollToLatest(); // the newest entries are at the bottom
            }
        }

        public static void ScrollToLatest()
        {
            // The list is laid out at the end of the frame, so the jump is re-applied for a few
            // frames (see ChroniclePanelBehaviour) until the content height has settled.
            _snapToLatestFrames = 3;
        }

        internal static void ApplyPendingScroll()
        {
            if (_snapToLatestFrames <= 0 || _scroll == null) return;
            _snapToLatestFrames--;
            _scroll.velocity = Vector2.zero;
            _scroll.verticalNormalizedPosition = 0f;
        }

        public static void RefreshUI()
        {
            if (_panelObj == null || _listContent == null) return;

            // Update title with player name
            if (_titleLabel != null)
            {
                string playerName = ChronicleManager.PlayerName();
                _titleLabel.text = $"Chronicle of {(string.IsNullOrEmpty(playerName) ? "Unknown" : playerName)}";
            }

            // Clear existing rows. Deactivated first: Destroy only takes effect at the end of the
            // frame, and until then the old rows would still be measured by the layout.
            for (int i = _listContent.childCount - 1; i >= 0; i--)
            {
                GameObject child = _listContent.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }

            var state = ChronicleManager.State;
            if (state == null) return;

            ChronicleManager.EnsureDerived();

            var closed = state.ClosedChapters ?? new List<Chapter>();
            var acts = (state.Acts ?? new List<ChronicleAct>()).OrderBy(a => a.Number).ToList();

            var inAct = new HashSet<Chapter>();
            foreach (var act in acts)
            {
                var chapters = ChronicleComposer.ChaptersOf(act, closed);
                foreach (var ch in chapters) inAct.Add(ch);
                RenderAct(act, chapters, closed);
            }

            foreach (var ch in closed)
                if (!inAct.Contains(ch)) RenderChapter(ch, isCurrent: false, indent: 0f);

            if (state.CurrentChapter != null)
                RenderChapter(state.CurrentChapter, isCurrent: true, indent: 0f);
        }

        private static readonly Color ActColor      = new Color(0.95f, 0.86f, 0.60f);
        private static readonly Color RecapColor    = new Color(0.80f, 0.76f, 0.92f);
        private static readonly Color TagColor      = new Color(0.55f, 0.75f, 0.85f);
        private static readonly Color HintColor     = new Color(0.55f, 0.52f, 0.62f);

        private static void RenderAct(ChronicleAct act, List<Chapter> chapters, List<Chapter> closed)
        {
            bool isExpanded = _expandedActs.Contains(act.Number);
            string arrow = isExpanded ? "▼" : "▶";
            int capturedNum = act.Number;

            MakeHeaderRow($"Act_{act.Number}",
                $"{arrow} Act {act.Number}  (Ch.{act.FirstChapter}–{act.LastChapter}, T{act.StartTurn}–{act.EndTurn})",
                FontStyles.Bold, ActColor, new Color(0.20f, 0.15f, 0.10f, 0.55f), 6f,
                () =>
                {
                    if (!_expandedActs.Remove(capturedNum)) _expandedActs.Add(capturedNum);
                    RefreshUI();
                });

            if (!isExpanded) return;

            if (!string.IsNullOrWhiteSpace(act.Summary))
            {
                MakeWrappedRow("ActSummary", act.Summary, 11, FontStyles.Italic, RecapColor, 22f);
            }
            else if (ChronicleManager.IsActBusy(act))
            {
                MakeWrappedRow("ActSummary", "Summarising this act…", 11, FontStyles.Italic, HintColor, 22f);
            }
            else
            {
                MakeWrappedRow("ActSummary",
                    "No summary yet. Summaries are written automatically only for the newest acts, the ones the AI is still told about.",
                    11, FontStyles.Italic, HintColor, 22f);
                ChronicleAct capturedAct = act;
                MakeHeaderRow("ActSummarise", "[ Summarise this act now (one AI call) ]",
                    FontStyles.Normal, TagColor, new Color(0.10f, 0.14f, 0.18f, 0.55f), 22f,
                    () =>
                    {
                        ChronicleManager.RequestActSummary(capturedAct);
                        RefreshUI();
                    });
            }

            foreach (var ch in chapters)
                RenderChapter(ch, isCurrent: false, indent: 16f);
        }

        private static void RenderChapter(Chapter chapter, bool isCurrent, float indent)
        {
            bool isExpanded = _expanded.TryGetValue(chapter.Number, out bool stored) ? stored : isCurrent;
            string arrow   = isExpanded ? "▼" : "▶";
            string range   = isCurrent ? $"T{chapter.StartTurn}+" : $"T{chapter.StartTurn}–{chapter.EndTurn}";
            string titlePart = string.IsNullOrEmpty(chapter.Title)
                ? (isCurrent ? "(in progress)" : $"Chapter {chapter.Number}")
                : $"\"{chapter.Title}\"";

            int capturedNum = chapter.Number;
            bool capturedExpanded = isExpanded;
            MakeHeaderRow($"Ch_{chapter.Number}",
                $"{arrow} Ch.{chapter.Number} {titlePart}  ({range})",
                isCurrent ? FontStyles.Bold : FontStyles.Normal,
                isCurrent ? new Color(0.88f, 0.78f, 1.0f) : new Color(0.65f, 0.60f, 0.80f),
                isCurrent ? new Color(0.15f, 0.10f, 0.25f, 0.65f) : new Color(0.10f, 0.08f, 0.16f, 0.45f),
                6f + indent,
                () =>
                {
                    _expanded[capturedNum] = !capturedExpanded;
                    RefreshUI();
                });

            // Detail rows (only when expanded)
            if (!isExpanded) return;

            float inner = indent + 22f;
            if (!string.IsNullOrWhiteSpace(chapter.Recap))
                MakeWrappedRow("Recap", chapter.Recap, 11, FontStyles.Italic, RecapColor, inner);
            if (chapter.Tags != null && chapter.Tags.Count > 0)
                MakeWrappedRow("Tags", "Tags: " + string.Join(", ", chapter.Tags), 10, FontStyles.Normal, TagColor, inner);

            bool anyBeat = false;
            if (chapter.Beats != null)
            {
                foreach (var beat in chapter.Beats)
                {
                    if (beat == null) continue;
                    anyBeat = true;
                    Color beatColor = beat.IsMilestone
                        ? new Color(1.00f, 0.90f, 0.50f)
                        : new Color(0.72f, 0.70f, 0.78f);

                    MakeWrappedRow($"Beat_{beat.Turn}_{beat.Type}",
                        $"T{beat.Turn}  {beat.Summary}{(beat.IsMilestone ? "  ★" : "")}",
                        11, FontStyles.Normal, beatColor, inner);
                }
            }
            if (!anyBeat && isCurrent)
                MakeWrappedRow("Empty", "No beats recorded in this chapter yet.", 11, FontStyles.Italic, HintColor, inner);
        }

        // ---- Session Recap ----

        private static async void ShowRecap()
        {
            try
            {
                var logView = SS.I?.hackyManager?.gameLogView;
                if (logView == null) return;

                string recap = await ChronicleManager.GetOrGenerateSessionRecapAsync();

                // AI unavailable / nothing to recap / already generating — fall back gracefully
                if (string.IsNullOrEmpty(recap))
                {
                    var state = ChronicleManager.State;
                    recap = state?.LastSessionRecap;
                    if (string.IsNullOrEmpty(recap) && state?.ClosedChapters != null && state.ClosedChapters.Count > 0)
                        recap = state.ClosedChapters[state.ClosedChapters.Count - 1].Recap;
                    if (string.IsNullOrEmpty(recap))
                        recap = "No session recap available yet. Play a few turns to record story beats first.";
                }

                // The scene may have been left while the recap was being written
                if (logView == null) return;
                _ = logView.LogText($"\n\n— Chronicle Recap —\n{recap}\n");
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
            var vRect = viewport.GetComponent<RectTransform>();
            StretchFull(vRect);
            vRect.offsetMax = new Vector2(-SCROLLBAR_WIDTH, 0f); // keep the rows clear of the scrollbar

            GameObject content = MakeRect("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var vlg = content.GetComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childForceExpandWidth = true;
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

            // Vertical scrollbar: always shown, draggable
            GameObject barObj = MakeRect("Scrollbar", scrollObj.transform, typeof(Image), typeof(Scrollbar));
            var barRect = barObj.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(1f, 0.5f);
            barRect.sizeDelta = new Vector2(SCROLLBAR_WIDTH, 0f);
            barRect.anchoredPosition = Vector2.zero;
            barObj.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            GameObject sliding = MakeRect("Sliding Area", barObj.transform);
            StretchFull(sliding.GetComponent<RectTransform>());
            GameObject handle = MakeRect("Handle", sliding.transform, typeof(Image));
            StretchFull(handle.GetComponent<RectTransform>());
            handle.GetComponent<Image>().color = new Color(0.62f, 0.52f, 0.85f, 0.95f);

            var bar = barObj.GetComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle.GetComponent<RectTransform>();
            bar.targetGraphic = handle.GetComponent<Image>();
            var nav = bar.navigation;
            nav.mode = Navigation.Mode.None;
            bar.navigation = nav;

            var sr = scrollObj.GetComponent<ScrollRect>();
            sr.viewport = vRect;
            sr.content = cRect;
            sr.horizontal = false;
            sr.vertical = true;
            sr.scrollSensitivity = 35f;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.verticalScrollbar = bar;
            sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            _scroll = sr;

            return content.transform;
        }

        /// <summary>A one-line clickable row in the list.</summary>
        private static void MakeHeaderRow(string name, string text, FontStyles style, Color textColor, Color background,
            float indent, UnityEngine.Events.UnityAction onClick)
        {
            GameObject row = MakeRect(name, _listContent, typeof(LayoutElement), typeof(Image), typeof(Button));
            row.GetComponent<LayoutElement>().minHeight = 22f;
            var img = row.GetComponent<Image>();
            img.color = background;
            var btn = row.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);

            var label = MakeText("Label", row.transform, text, 12, style, textColor, TextAlignmentOptions.Left)
                .GetComponent<TextMeshProUGUI>();
            label.richText = false;
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.margin = new Vector4(indent, 0f, 4f, 0f);
            StretchFull(label.GetComponent<RectTransform>());
        }

        /// <summary>
        /// A text row that wraps and grows to fit. The text object is the row itself so the list's
        /// layout group reads its wrapped height (a fixed-height row let long entries spill over
        /// the row below).
        /// </summary>
        private static void MakeWrappedRow(string name, string text, int fontSize, FontStyles style, Color color, float indent)
        {
            var tmp = MakeText(name, _listContent, text, fontSize, style, color, TextAlignmentOptions.TopLeft)
                .GetComponent<TextMeshProUGUI>();
            tmp.richText = false;           // story text is data, not markup
            tmp.raycastTarget = false;      // let clicks and the wheel through to the scroll view
            tmp.enableWordWrapping = true;
            tmp.margin = new Vector4(indent, 1f, 6f, 1f);
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

        private void LateUpdate()
        {
            ChronicleUI.ApplyPendingScroll();
        }
    }
}
