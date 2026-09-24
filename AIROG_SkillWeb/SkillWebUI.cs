using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// The passive-web screen, Path of Exile style: a pannable, zoomable star map of the whole
    /// procedural web, a stats sidebar, and a floating card for the selected node. Allocation is
    /// planned first (pending, cyan) and committed with Apply.
    ///
    /// Performance model: node widgets are built once per web and restyled in place; edges and
    /// far-zoom dots are single meshes (WebLineGraphic / WebDotGraphic); node widgets outside the
    /// viewport, and small nodes when zoomed far out, are deactivated.
    /// </summary>
    public class SkillWebUI : MonoBehaviour
    {
        public static SkillWebUI Instance { get; private set; }

        public bool IsOpen => _window != null && _window.activeSelf;

        // ── Palette (matches the design mockup) ─────────────────────────────────
        static readonly Color CBg = Hex("#0B0A08"), CPanel = Hex("#100E0B"), CHeader = Hex("#12100C"), CTile = Hex("#17140F");
        static readonly Color CBorder = Hex("#3A3226"), CBorder2 = Hex("#4A3F2E"), CFrame = Hex("#6B5A3A");
        static readonly Color CText = Hex("#D8D0BF"), CTextDim = Hex("#A79C86"), CTextBright = Hex("#F3ECDC"), CTextSoft = Hex("#CFC6B3");
        static readonly Color CGold = Hex("#E6C46A"), CGoldDeep = Hex("#C9A14A"), CGoldEdge = Hex("#D9B45A"), CCyan = Hex("#6FD3E8");
        static readonly Color CBlue = Hex("#9AA8FF"), CAmber = Hex("#D9975C"), COrange = Hex("#E0894A"), CConf = Hex("#8FD6E3");
        static readonly Color CLockFill = Hex("#15130F"), CLockRing = Hex("#3A352C"), CReachFill = Hex("#221E17"), CReachRing = Hex("#C9B27F");
        static readonly Color CPendFill = Hex("#3A5A62"), CPathFill = Hex("#1F3238"), CBronze = Hex("#6B5A3A");

        static readonly Dictionary<string, Color> AttrColors = new Dictionary<string, Color>
        {
            ["Strength"] = Hex("#D8704F"), ["Dexterity"] = Hex("#6FBF7A"), ["Intellect"] = Hex("#6F95E0"),
            ["Cunning"] = Hex("#A887D6"), ["Charisma"] = Hex("#D9B454"),
        };

        const float HeaderH = 72f, SidebarW = 340f;
        const float MinZoom = 0.06f, MaxZoom = 1.8f;
        /// <summary>Below this zoom, Basic and Travel nodes are drawn as batched dots instead of widgets.</summary>
        const float LodZoom = 0.4f;
        /// <summary>Below this zoom, only Keystone and Anchor Star labels stay visible.</summary>
        const float LabelZoom = 0.75f;
        const float DragThreshold = 6f;
        const float CardWidth = 360f;

        // ── References ──────────────────────────────────────────────────────────
        GameplayManager _manager;
        SkillWebData _data;
        SkillWebConfig Config => SkillWebPlugin.Instance.SkillConfig;

        GameObject _window;
        Canvas _rootCanvas;
        RectTransform _viewport, _content, _nodeLayer, _labelLayer;
        RawImage _starfield;
        WebLineGraphic _guides, _edges;
        WebDotGraphic _dots;

        // Header
        TextMeshProUGUI _subtitle, _resonanceText, _pendingText, _matchText;
        TMP_InputField _search;
        Button _applyBtn, _resetBtn, _nextBtn;

        // Sidebar
        RectTransform _sideContent;

        // Overlays
        TextMeshProUGUI _zoomText, _toastText;
        GameObject _toast;
        RawImage _minimap;
        Texture2D _miniTex;
        RectTransform _miniView;
        readonly List<RectTransform> _overlayBlockers = new List<RectTransform>();

        // Card + tooltip canvas
        Canvas _cardCanvas;
        RectTransform _card, _tooltip;
        TextMeshProUGUI _tooltipText;

        // ── State ───────────────────────────────────────────────────────────────
        readonly Dictionary<string, NodeWidget> _widgets = new Dictionary<string, NodeWidget>();
        SkillWebData _builtFor;
        int _builtNodeCount;
        readonly Dictionary<string, TextMeshProUGUI> _sectorLabels = new Dictionary<string, TextMeshProUGUI>();

        float _zoom = 0.5f;
        Vector2 _pan;
        bool _viewDirty = true;
        bool _dragging;
        Vector2 _dragStart, _lastMouse;
        float _dragDistance;

        WebNode _selected;
        WebNode _hovered;
        readonly HashSet<string> _pathPreview = new HashSet<string>();
        readonly List<string> _pending = new List<string>();
        readonly HashSet<string> _pendingSet = new HashSet<string>();
        readonly HashSet<string> _reachable = new HashSet<string>();
        string _query = "";
        List<WebNode> _matches = new List<WebNode>();
        int _matchIdx;
        bool _lodActive;
        Coroutine _toastRoutine;

        // Icon caches
        readonly Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        readonly Dictionary<string, float> _iconMissUntil = new Dictionary<string, float>();

        class NodeWidget
        {
            public WebNode node;
            public RectTransform rt;
            public CanvasGroup group;
            public Image glow, fill, inner, icon, ring, sel, hi;
            public TextMeshProUGUI label;
            public RectTransform pips;
            public bool small;
        }

        // ── Entry points ────────────────────────────────────────────────────────

        public static void Open(GameplayManager manager, SkillWebData data)
        {
            if (Instance == null)
            {
                var obj = new GameObject("SkillWebUI");
                Instance = obj.AddComponent<SkillWebUI>();
            }
            Instance.Show(manager, data);
        }

        public void Show(GameplayManager manager, SkillWebData data)
        {
            _manager = manager;
            if (_window == null) BuildUI();

            bool newWeb = !ReferenceEquals(data, _builtFor) || data == null || data.nodes.Count != _builtNodeCount;
            _data = data;
            _window.SetActive(true);
            _cardCanvas.gameObject.SetActive(true);

            if (newWeb)
            {
                ClearPending();
                _selected = null;
                BuildWeb();
                Canvas.ForceUpdateCanvases();
                FrameCharted();
            }
            Refresh();
        }

        public void Close()
        {
            ClearPending();
            _selected = null;
            _hovered = null;
            _pathPreview.Clear();
            if (_window != null) _window.SetActive(false);
            if (_cardCanvas != null) _cardCanvas.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_window != null) Destroy(_window);
            if (_cardCanvas != null) Destroy(_cardCanvas.gameObject);
            if (_miniTex != null) Destroy(_miniTex);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Construction
        // ══════════════════════════════════════════════════════════════════════

        void BuildUI()
        {
            _window = new GameObject("SkillWebWindow", typeof(RectTransform));
            _rootCanvas = _window.AddComponent<Canvas>();
            _rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _rootCanvas.overrideSorting = true;
            _rootCanvas.sortingOrder = 500;
            _window.AddComponent<GraphicRaycaster>();
            ConfigureScaler(_window.AddComponent<CanvasScaler>());

            var backdrop = NewImage("Backdrop", _window.transform, new Color(CBg.r, CBg.g, CBg.b, 0.98f));
            Stretch(backdrop.rectTransform);

            BuildViewport();
            BuildSidebar();
            BuildHeader();
            BuildCardCanvas();
        }

        static void ConfigureScaler(CanvasScaler scaler)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }

        void BuildHeader()
        {
            var hdr = NewImage("Header", _window.transform, CHeader);
            var r = hdr.rectTransform;
            r.anchorMin = new Vector2(0, 1); r.anchorMax = new Vector2(1, 1); r.pivot = new Vector2(0.5f, 1);
            r.sizeDelta = new Vector2(0, HeaderH); r.anchoredPosition = Vector2.zero;
            var line = NewImage("Rule", hdr.transform, CBorder).rectTransform;
            line.anchorMin = new Vector2(0, 0); line.anchorMax = new Vector2(1, 0); line.pivot = new Vector2(0.5f, 0);
            line.sizeDelta = new Vector2(0, 1); line.anchoredPosition = Vector2.zero;

            var title = NewText("Title", hdr.transform, "SKILL WEB", 26, CGold, TextAlignmentOptions.BottomLeft);
            title.fontStyle = FontStyles.Bold; title.characterSpacing = 10;
            Place(title.rectTransform, new Vector2(0, 0.5f), new Vector2(24, 2), new Vector2(300, 32), new Vector2(0, 0));
            _subtitle = NewText("Sub", hdr.transform, "", 14, CTextDim, TextAlignmentOptions.TopLeft);
            Place(_subtitle.rectTransform, new Vector2(0, 0.5f), new Vector2(24, -2), new Vector2(300, 22), new Vector2(0, 1));

            // Search
            _search = BuildInput("Search", hdr.transform, "Search nodes, stats or rules");
            Place((RectTransform)_search.transform, new Vector2(0, 0.5f), new Vector2(SidebarW, 0), new Vector2(400, 42), new Vector2(0, 0.5f));
            _search.onValueChanged.AddListener(v => { _query = v.Trim(); _matchIdx = 0; Refresh(); });
            _matchText = NewText("Matches", hdr.transform, "", 13, CGold, TextAlignmentOptions.MidlineLeft);
            Place(_matchText.rectTransform, new Vector2(0, 0.5f), new Vector2(SidebarW + 412, 0), new Vector2(96, 30), new Vector2(0, 0.5f));
            _nextBtn = NewButton("Next", hdr.transform, "Next", ButtonStyle.Ghost, 13);
            Place((RectTransform)_nextBtn.transform, new Vector2(0, 0.5f), new Vector2(SidebarW + 508, 0), new Vector2(64, 34), new Vector2(0, 0.5f));
            _nextBtn.onClick.AddListener(NextMatch);

            // Right cluster, laid out right-to-left.
            var right = new GameObject("Right", typeof(RectTransform)).GetComponent<RectTransform>();
            right.SetParent(hdr.transform, false);
            right.anchorMin = right.anchorMax = new Vector2(1, 0.5f); right.pivot = new Vector2(1, 0.5f);
            right.anchoredPosition = new Vector2(-16, 0); right.sizeDelta = new Vector2(900, 48);
            var h = right.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleRight; h.spacing = 12;
            h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = false;

            var resBox = new GameObject("Resonance", typeof(RectTransform)).GetComponent<RectTransform>();
            resBox.SetParent(right, false);
            Pref(resBox.gameObject, 150, 48);
            var star = NewText("Glyph", resBox, "*", 30, CCyan, TextAlignmentOptions.Center);
            Place(star.rectTransform, new Vector2(0, 0.5f), Vector2.zero, new Vector2(26, 40), new Vector2(0, 0.5f));
            _resonanceText = NewText("Value", resBox, "0", 26, CTextBright, TextAlignmentOptions.BottomLeft);
            _resonanceText.fontStyle = FontStyles.Bold;
            Place(_resonanceText.rectTransform, new Vector2(0, 0.5f), new Vector2(32, 0), new Vector2(118, 28), new Vector2(0, 0));
            var resLbl = NewText("Label", resBox, "RESONANCE", 11, CTextDim, TextAlignmentOptions.TopLeft);
            resLbl.characterSpacing = 6;
            Place(resLbl.rectTransform, new Vector2(0, 0.5f), new Vector2(32, 0), new Vector2(118, 18), new Vector2(0, 1));
            AddHover(resBox.gameObject, () => ShowTooltipText(LedgerText()), HideTooltip);

            var rule = NewImage("Rule", right, CBorder);
            Pref(rule.gameObject, 1, 32);

            _pendingText = NewText("Pending", right, "", 14, CCyan, TextAlignmentOptions.MidlineLeft);
            Pref(_pendingText.gameObject, 190, 40);

            _resetBtn = NewButton("Reset", right, "Reset", ButtonStyle.Ghost, 14);
            Pref(_resetBtn.gameObject, 90, 42);
            _resetBtn.onClick.AddListener(() => { ClearPending(); Refresh(); });

            _applyBtn = NewButton("Apply", right, "Apply", ButtonStyle.Gold, 15);
            Pref(_applyBtn.gameObject, 100, 42);
            _applyBtn.onClick.AddListener(ApplyPending);

            if (Config.GrantUsableAbilities)
            {
                var ab = NewButton("Abilities", right, "Abilities", ButtonStyle.Ghost, 14);
                Pref(ab.gameObject, 110, 42);
                ab.onClick.AddListener(() =>
                {
                    // The ability bar draws below this window; step out of the web to show it.
                    var mgr = _manager;
                    Close();
                    if (mgr != null) SkillAbilityBar.Open(mgr);
                });
            }

            var close = NewButton("Close", right, "X", ButtonStyle.Ghost, 16);
            Pref(close.gameObject, 44, 42);
            close.onClick.AddListener(Close);
        }

        void BuildSidebar()
        {
            var side = NewImage("Sidebar", _window.transform, CPanel);
            var r = side.rectTransform;
            r.anchorMin = new Vector2(0, 0); r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.offsetMin = new Vector2(0, 0); r.offsetMax = new Vector2(SidebarW, -HeaderH);
            var edge = NewImage("Rule", side.transform, CBorder).rectTransform;
            edge.anchorMin = new Vector2(1, 0); edge.anchorMax = new Vector2(1, 1); edge.pivot = new Vector2(1, 0.5f);
            edge.sizeDelta = new Vector2(1, 0); edge.anchoredPosition = Vector2.zero;

            var scroll = side.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 30f;

            var vp = new GameObject("View", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            vp.SetParent(side.transform, false);
            Stretch(vp);
            vp.offsetMax = new Vector2(-1, 0);

            _sideContent = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            _sideContent.SetParent(vp, false);
            _sideContent.anchorMin = new Vector2(0, 1); _sideContent.anchorMax = new Vector2(1, 1); _sideContent.pivot = new Vector2(0.5f, 1);
            _sideContent.sizeDelta = Vector2.zero;
            var v = _sideContent.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(20, 20, 20, 24); v.spacing = 22;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            _sideContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = vp;
            scroll.content = _sideContent;
        }

        void BuildViewport()
        {
            var vpObj = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vpObj.transform.SetParent(_window.transform, false);
            _viewport = vpObj.GetComponent<RectTransform>();
            _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(SidebarW, 0); _viewport.offsetMax = new Vector2(0, -HeaderH);

            _starfield = new GameObject("Starfield", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            _starfield.transform.SetParent(_viewport, false);
            Stretch(_starfield.rectTransform);
            _starfield.texture = WebSprites.Starfield;
            _starfield.raycastTarget = true; // gives the viewport something to hit so drags start on empty space

            // Content: a nested canvas so panning only rebatches the web, not the whole window.
            var contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(_viewport, false);
            _content = contentObj.GetComponent<RectTransform>();
            _content.anchorMin = _content.anchorMax = new Vector2(0.5f, 0.5f);
            _content.pivot = new Vector2(0.5f, 0.5f);
            _content.sizeDelta = Vector2.zero;
            contentObj.AddComponent<Canvas>();
            contentObj.AddComponent<GraphicRaycaster>();

            _guides = NewLayerGraphic<WebLineGraphic>("Guides", _content);
            _edges = NewLayerGraphic<WebLineGraphic>("Edges", _content);
            _dots = NewLayerGraphic<WebDotGraphic>("Dots", _content);
            _nodeLayer = new GameObject("Nodes", typeof(RectTransform)).GetComponent<RectTransform>();
            _nodeLayer.SetParent(_content, false);
            _nodeLayer.sizeDelta = Vector2.zero;

            _labelLayer = new GameObject("SectorLabels", typeof(RectTransform)).GetComponent<RectTransform>();
            _labelLayer.SetParent(_viewport, false);
            Stretch(_labelLayer);

            BuildLegend();
            BuildZoomControls();
            BuildToast();
            BuildMinimap();

            var hint = NewText("Hint", _viewport, "Drag to pan  ·  scroll to zoom  ·  F frames the web  ·  Esc closes", 13, Hex("#8A8070"), TextAlignmentOptions.BottomLeft);
            hint.raycastTarget = false;
            Place(hint.rectTransform, new Vector2(0, 0), new Vector2(18, 14), new Vector2(700, 22), new Vector2(0, 0));
        }

        T NewLayerGraphic<T>(string name, Transform parent) where T : MaskableGraphic
        {
            var g = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(T)).GetComponent<T>();
            g.transform.SetParent(parent, false);
            g.raycastTarget = false;
            g.color = Color.white;
            return g;
        }

        void BuildLegend()
        {
            var box = NewImage("Legend", _viewport, new Color(CPanel.r, CPanel.g, CPanel.b, 0.92f));
            box.raycastTarget = false;
            Place(box.rectTransform, new Vector2(0, 1), new Vector2(16, -16), new Vector2(250, 150), new Vector2(0, 1));
            var v = box.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(14, 14, 12, 12); v.spacing = 6;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LegendRow(box.transform, AttrColors["Strength"], CGold, "Allocated");
            LegendRow(box.transform, CPendFill, CCyan, "Pending, not yet applied");
            LegendRow(box.transform, CReachFill, CReachRing, "Reachable");
            LegendRow(box.transform, CLockFill, CLockRing, "Locked");
            LegendRow(box.transform, Hex("#2C2822"), Hex("#2C2822"), "Uncharted, revealed as you reach it");
        }

        void LegendRow(Transform parent, Color fill, Color ring, string text)
        {
            var row = new GameObject("Row", typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(parent, false);
            Pref(row.gameObject, -1, 20);
            var f = NewImage("Fill", row, fill); f.sprite = WebSprites.Disc; f.raycastTarget = false;
            Place(f.rectTransform, new Vector2(0, 0.5f), new Vector2(2, 0), new Vector2(14, 14), new Vector2(0, 0.5f));
            var rr = NewImage("Ring", f.transform, ring); rr.sprite = WebSprites.RingThick; rr.raycastTarget = false;
            Stretch(rr.rectTransform);
            var t = NewText("Text", row, text, 13, CTextSoft, TextAlignmentOptions.MidlineLeft);
            t.raycastTarget = false;
            Place(t.rectTransform, new Vector2(0, 0.5f), new Vector2(26, 0), new Vector2(200, 20), new Vector2(0, 0.5f));
        }

        void BuildZoomControls()
        {
            var box = NewImage("Zoom", _viewport, new Color(CPanel.r, CPanel.g, CPanel.b, 0.92f));
            Place(box.rectTransform, new Vector2(1, 1), new Vector2(-16, -16), new Vector2(360, 52), new Vector2(1, 1));
            _overlayBlockers.Add(box.rectTransform);
            var h = box.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(4, 4, 4, 4); h.spacing = 2; h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false;

            var minus = NewButton("ZoomOut", box.transform, "-", ButtonStyle.Flat, 22); Pref(minus.gameObject, 44, 44);
            minus.onClick.AddListener(() => ZoomAround(_zoom / 1.25f, Vector2.zero));
            _zoomText = NewText("Pct", box.transform, "100%", 14, CText, TextAlignmentOptions.Center); Pref(_zoomText.gameObject, 56, 44);
            var plus = NewButton("ZoomIn", box.transform, "+", ButtonStyle.Flat, 22); Pref(plus.gameObject, 44, 44);
            plus.onClick.AddListener(() => ZoomAround(_zoom * 1.25f, Vector2.zero));
            var rule = NewImage("Rule", box.transform, CBorder); Pref(rule.gameObject, 1, 26);
            var whole = NewButton("Whole", box.transform, "Whole web", ButtonStyle.Flat, 14); Pref(whole.gameObject, 104, 44);
            whole.onClick.AddListener(FrameWhole);
            var heart = NewButton("Heart", box.transform, "Heart", ButtonStyle.Flat, 14); Pref(heart.gameObject, 70, 44);
            heart.onClick.AddListener(() => CenterOn(Vector2.zero, 0.8f));
        }

        void BuildToast()
        {
            var img = NewImage("Toast", _viewport, CHeader);
            img.raycastTarget = false;
            _toast = img.gameObject;
            Place(img.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -16), new Vector2(560, 44), new Vector2(0.5f, 1));
            var o = _toast.AddComponent<Outline>(); o.effectColor = CCyan; o.effectDistance = new Vector2(1, -1);
            _toastText = NewText("Text", _toast.transform, "", 15, CTextBright, TextAlignmentOptions.Center);
            _toastText.raycastTarget = false;
            Stretch(_toastText.rectTransform);
            _toast.SetActive(false);
        }

        void BuildMinimap()
        {
            var frame = NewImage("Minimap", _viewport, CFrame);
            Place(frame.rectTransform, new Vector2(1, 0), new Vector2(-16, 16), new Vector2(202, 202), new Vector2(1, 0));
            _overlayBlockers.Add(frame.rectTransform);

            _minimap = new GameObject("Map", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            _minimap.transform.SetParent(frame.transform, false);
            Stretch(_minimap.rectTransform);
            _minimap.rectTransform.offsetMin = new Vector2(1, 1); _minimap.rectTransform.offsetMax = new Vector2(-1, -1);
            _miniTex = new Texture2D(200, 200, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _minimap.texture = _miniTex;

            _miniView = new GameObject("View", typeof(RectTransform)).GetComponent<RectTransform>();
            _miniView.SetParent(_minimap.transform, false);
            _miniView.anchorMin = _miniView.anchorMax = new Vector2(0.5f, 0.5f);
            foreach (var (amin, amax, size) in new[]
            {
                (new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1.5f)), (new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1.5f)),
                (new Vector2(0, 0), new Vector2(0, 1), new Vector2(1.5f, 0)), (new Vector2(1, 0), new Vector2(1, 1), new Vector2(1.5f, 0)),
            })
            {
                var e = NewImage("Edge", _miniView, CCyan);
                e.raycastTarget = false;
                e.rectTransform.anchorMin = amin; e.rectTransform.anchorMax = amax;
                e.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                e.rectTransform.sizeDelta = size; e.rectTransform.anchoredPosition = Vector2.zero;
            }

            var btn = _minimap.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnMinimapClick);
        }

        void BuildCardCanvas()
        {
            var obj = new GameObject("SkillWebCard", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _cardCanvas = obj.GetComponent<Canvas>();
            _cardCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _cardCanvas.overrideSorting = true;
            _cardCanvas.sortingOrder = 502;
            ConfigureScaler(obj.GetComponent<CanvasScaler>());

            // Floating node card (rebuilt on selection/refresh, positioned every frame).
            var card = NewImage("Card", obj.transform, new Color(0.047f, 0.043f, 0.035f, 0.97f));
            _card = card.rectTransform;
            _card.anchorMin = _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0, 1);
            _card.sizeDelta = new Vector2(CardWidth, 100);
            var outline = card.gameObject.AddComponent<Outline>(); outline.effectColor = CFrame; outline.effectDistance = new Vector2(1, -1);
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            card.gameObject.SetActive(false);

            // Hover tooltip (text only, follows the cursor).
            var tip = NewImage("Tooltip", obj.transform, new Color(0.047f, 0.043f, 0.035f, 0.97f));
            tip.raycastTarget = false;
            _tooltip = tip.rectTransform;
            _tooltip.anchorMin = _tooltip.anchorMax = new Vector2(0.5f, 0.5f);
            _tooltip.pivot = new Vector2(0, 1);
            _tooltip.sizeDelta = new Vector2(320, 60);
            var to = tip.gameObject.AddComponent<Outline>(); to.effectColor = CFrame; to.effectDistance = new Vector2(1, -1);
            var tv = tip.gameObject.AddComponent<VerticalLayoutGroup>();
            tv.padding = new RectOffset(12, 12, 10, 10);
            tv.childControlWidth = true; tv.childControlHeight = true; tv.childForceExpandHeight = false;
            tip.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _tooltipText = NewText("Text", _tooltip, "", 14, CText, TextAlignmentOptions.TopLeft);
            _tooltipText.enableWordWrapping = true;
            _tooltipText.raycastTarget = false;
            tip.gameObject.SetActive(false);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Web widgets (built once per web)
        // ══════════════════════════════════════════════════════════════════════

        void BuildWeb()
        {
            ClearChildren(_nodeLayer);
            ClearChildren(_labelLayer);
            _widgets.Clear();
            _sectorLabels.Clear();
            _builtFor = _data;
            _builtNodeCount = _data?.nodes.Count ?? 0;
            if (_data == null) return;

            float world = Mathf.Max(1000f, _data.worldRadius) * 2f;
            foreach (var g in new MaskableGraphic[] { _guides, _edges, _dots })
                g.rectTransform.sizeDelta = new Vector2(world, world); // RectMask2D culls by rect; cover the web

            BuildGuides();

            foreach (var node in _data.nodes)
                _widgets[node.id] = BuildNodeWidget(node);

            foreach (var s in _data.sectors)
            {
                var t = NewText("Label_" + s.id, _labelLayer, "", 15, GetColor(s.colorHex), TextAlignmentOptions.Center);
                t.fontStyle = FontStyles.Bold; t.characterSpacing = 12; t.raycastTarget = false;
                t.rectTransform.sizeDelta = new Vector2(360, 44);
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                _sectorLabels[s.id] = t;
            }
        }

        void BuildGuides()
        {
            var segs = _guides.Segments;
            segs.Clear();
            var orbit = new Color32(230, 196, 106, 22);
            for (int ring = 1; ring <= _data.maxRing; ring++)
            {
                float r = WebGenerator.RingRadius(ring);
                const int n = 120;
                for (int i = 0; i < n; i++)
                {
                    float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                    segs.Add(new WebLineGraphic.Segment
                    {
                        a = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r,
                        b = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r,
                        width = 3f, color = orbit,
                    });
                }
            }
            var bound = new Color32(230, 196, 106, 16);
            foreach (var s in _data.sectors)
            {
                if (_data.sectors.Count < 2) break;
                float a = s.angleCenter - s.angleSpan / 2;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                segs.Add(new WebLineGraphic.Segment { a = dir * 330f, b = dir * _data.worldRadius, width = 4f, color = bound });
            }
            _guides.SetSegments();
        }

        NodeWidget BuildNodeWidget(WebNode node)
        {
            float d = WebGenerator.NodeDiameter(node);
            var w = new NodeWidget { node = node, small = node.type == WebNodeType.Basic && !node.IsOrigin || node.type == WebNodeType.Travel };

            var root = new GameObject("N_" + node.id, typeof(RectTransform));
            root.transform.SetParent(_nodeLayer, false);
            w.rt = (RectTransform)root.transform;
            w.rt.sizeDelta = new Vector2(d, d);
            w.rt.anchoredPosition = new Vector2(node.x, node.y);
            w.group = root.AddComponent<CanvasGroup>();

            bool square = node.type == WebNodeType.Anchor;
            w.glow = Layer(root.transform, "Glow", WebSprites.Glow, 2.3f, false);
            w.hi = Layer(root.transform, "Search", WebSprites.Ring, 1.55f, false);
            w.fill = Layer(root.transform, "Fill", square ? WebSprites.Square : WebSprites.Disc, 1f, square);
            w.fill.raycastTarget = true;
            if (node.type == WebNodeType.Mastery)
                w.inner = Layer(root.transform, "Inner", WebSprites.RingThick, 0.74f, false);
            if (!w.small && !node.IsOrigin)
            {
                w.icon = Layer(root.transform, "Icon", null, square ? 0.66f : 0.72f, false);
                w.icon.preserveAspect = true;
                w.icon.enabled = false;
            }
            Sprite ringSprite = square ? WebSprites.SquareRing
                : node.type == WebNodeType.Confluence ? WebSprites.RingDouble
                : (node.type == WebNodeType.Keystone || node.type == WebNodeType.Notable || node.IsOrigin) ? WebSprites.RingThick
                : WebSprites.Ring;
            w.ring = Layer(root.transform, "Ring", ringSprite, 1f, square);
            w.sel = Layer(root.transform, "Selected", WebSprites.Ring, 1.4f, false);
            w.sel.color = Hex("#F3E3B0");

            if (!w.small && !node.IsOrigin)
            {
                w.label = NewText("Label", root.transform, "", (node.type == WebNodeType.Keystone || node.IsGate) ? 26 : 20, CText, TextAlignmentOptions.Top);
                w.label.raycastTarget = false;
                w.label.enableWordWrapping = true;
                w.label.rectTransform.sizeDelta = new Vector2(Mathf.Max(220f, d * 2.4f), 60);
                w.label.rectTransform.anchoredPosition = new Vector2(0, -(d * 0.5f + 34f));
            }

            var btn = root.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = w.fill;
            var cap = node;
            btn.onClick.AddListener(() => OnNodeClicked(cap));
            AddHover(root, () => OnNodeHover(cap), () => OnNodeHoverExit(cap));
            return w;
        }

        static Image Layer(Transform parent, string name, Sprite sprite, float scale, bool rotate45)
        {
            var img = NewImage(name, parent, Color.white);
            img.sprite = sprite;
            img.raycastTarget = false;
            var r = img.rectTransform;
            r.anchorMin = new Vector2(0.5f - scale / 2, 0.5f - scale / 2);
            r.anchorMax = new Vector2(0.5f + scale / 2, 0.5f + scale / 2);
            r.offsetMin = r.offsetMax = Vector2.zero;
            if (rotate45) r.localRotation = Quaternion.Euler(0, 0, 45);
            return img;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Refresh / styling
        // ══════════════════════════════════════════════════════════════════════

        public void Refresh()
        {
            if (_window == null || _data == null) return;
            if (!ReferenceEquals(_data, _builtFor) || _data.nodes.Count != _builtNodeCount)
            {
                BuildWeb();
            }

            // Drop pending nodes that are no longer valid (e.g. the web grew or a refund happened elsewhere).
            _pending.RemoveAll(id => { var n = _data.GetNode(id); return n == null || n.unlocked; });
            _pendingSet.Clear();
            foreach (var id in _pending) _pendingSet.Add(id);
            if (_selected != null && _data.GetNode(_selected.id) != _selected) _selected = null;

            ReportGrowth();

            _reachable.Clear();
            foreach (var n in _data.nodes)
                if (WebGraph.CanUnlock(n, _data, _pendingSet)) _reachable.Add(n.id);

            RebuildMatches();
            foreach (var w in _widgets.Values) StyleNode(w);
            RebuildEdges();
            RebuildDots();
            RedrawMinimap();
            RefreshHeader();
            RefreshSidebar();
            RebuildCard();
            _viewDirty = true;
        }

        int PendingCost => _pending.Sum(id => WebGraph.GetUnlockCost(_data.GetNode(id)));
        int Available => (_data?.resonance ?? 0) - PendingCost;

        bool Taken(WebNode n) => WebGraph.IsTaken(n, _pendingSet);

        void StyleNode(NodeWidget w)
        {
            var n = w.node;
            bool charted = _data.IsCharted(n);
            Color sector = SectorColor(n);
            bool alloc = n.unlocked && !n.IsGate && !n.IsOrigin;
            bool pend = _pendingSet.Contains(n.id);
            bool reach = _reachable.Contains(n.id);
            bool path = _pathPreview.Contains(n.id);

            Color fill = CLockFill, ring = CLockRing, glow = Color.clear;
            if (n.IsOrigin) { fill = CGold; ring = Hex("#FFF3C8"); glow = new Color(CGold.r, CGold.g, CGold.b, 0.55f); }
            else if (n.IsGate)
            {
                if (n.feedingPerks.Count > 0) { fill = sector; ring = CGold; glow = new Color(sector.r, sector.g, sector.b, 0.5f); }
                else { fill = CLockFill; ring = new Color(sector.r, sector.g, sector.b, 0.7f); }
            }
            else if (alloc) { fill = sector; ring = CGold; glow = new Color(sector.r, sector.g, sector.b, 0.45f); }
            else if (pend) { fill = CPendFill; ring = CCyan; glow = new Color(CCyan.r, CCyan.g, CCyan.b, 0.4f); }
            else if (path) { fill = CPathFill; ring = CCyan; }
            else if (reach) { fill = CReachFill; ring = CReachRing; }

            if (n.type == WebNodeType.Mastery)
            {
                // Masteries read as a dark seal with a coloured inner band.
                var band = alloc ? sector : pend ? CPendFill : reach ? CBronze : Hex("#221E17");
                if (w.inner != null) w.inner.color = band;
                fill = CLockFill;
            }
            if (n.type == WebNodeType.Confluence && !alloc && !pend && !path && !reach) ring = Hex("#3A4A4E");

            w.fill.color = fill;
            w.ring.color = ring;
            w.glow.color = glow;
            w.glow.enabled = glow.a > 0f;

            bool match = !string.IsNullOrEmpty(_query) && Matches(n);
            w.hi.enabled = match;
            w.hi.color = Hex("#F3E3B0");
            w.sel.enabled = _selected == n;

            float alpha = 1f;
            if (!string.IsNullOrEmpty(_query) && !match && !n.IsOrigin) alpha = 0.25f;
            w.group.alpha = alpha;

            if (w.icon != null)
            {
                Sprite icon = charted ? IconFor(n) : null;
                w.icon.enabled = icon != null;
                if (icon != null)
                {
                    w.icon.sprite = icon;
                    bool lit = alloc || (n.IsGate && n.feedingPerks.Count > 0) || pend;
                    w.icon.color = lit ? Color.white : new Color(0.62f, 0.6f, 0.56f, 0.75f);
                }
            }

            if (w.label != null)
            {
                w.label.text = n.IsGate ? n.name.ToUpperInvariant() : n.name;
                w.label.color = (alloc || (n.IsGate && n.feedingPerks.Count > 0)) ? CTextBright : reach ? CText : CTextDim;
            }

            // Tier pips under upgraded nodes.
            int tier = alloc && n.IsTiered ? n.tier : 0;
            if (tier > 1 && w.pips == null) w.pips = BuildPips(w);
            if (w.pips != null)
            {
                w.pips.gameObject.SetActive(tier > 1);
                for (int i = 0; i < w.pips.childCount; i++)
                    w.pips.GetChild(i).GetComponent<Image>().color = i < tier ? CGold : new Color(CGold.r, CGold.g, CGold.b, 0.25f);
            }
        }

        RectTransform BuildPips(NodeWidget w)
        {
            float d = WebGenerator.NodeDiameter(w.node);
            var pr = new GameObject("Pips", typeof(RectTransform)).GetComponent<RectTransform>();
            pr.SetParent(w.rt, false);
            pr.anchoredPosition = new Vector2(0, -(d * 0.5f + 12f));
            for (int i = 0; i < 3; i++)
            {
                var p = NewImage("Pip", pr, CGold);
                p.raycastTarget = false;
                p.rectTransform.sizeDelta = new Vector2(9, 9);
                p.rectTransform.anchoredPosition = new Vector2((i - 1) * 15f, 0);
                p.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            }
            return pr;
        }

        void RebuildEdges()
        {
            var segs = _edges.Segments;
            segs.Clear();
            var seen = new HashSet<string>();
            foreach (var a in _data.nodes)
            {
                foreach (var bid in a.edges)
                {
                    if (string.CompareOrdinal(a.id, bid) >= 0) continue;
                    var b = _data.GetNode(bid);
                    if (b == null) continue;

                    bool ca = _data.IsCharted(a), cb = _data.IsCharted(b);
                    if (!ca && !cb)
                    {
                        if (Config.FrontierPreview) segs.Add(Seg(a, b, 4f, new Color32(58, 53, 44, 60)));
                        continue;
                    }
                    if (!ca || !cb) { segs.Add(Seg(a, b, 4f, new Color32(58, 53, 44, 140))); continue; }

                    bool ta = Taken(a), tb = Taken(b);
                    bool pa = _pendingSet.Contains(a.id), pb = _pendingSet.Contains(b.id);
                    bool path = (_pathPreview.Contains(a.id) || ta) && (_pathPreview.Contains(b.id) || tb) && (_pathPreview.Contains(a.id) || _pathPreview.Contains(b.id));

                    if (ta && tb) segs.Add(Seg(a, b, pa || pb ? 9f : 10f, pa || pb ? (Color32)CCyan : (Color32)CGoldEdge));
                    else if (path) segs.Add(Seg(a, b, 8f, new Color32(111, 211, 232, 170)));
                    else if ((ta && _reachable.Contains(b.id)) || (tb && _reachable.Contains(a.id))) segs.Add(Seg(a, b, 7f, CBronze));
                    else segs.Add(Seg(a, b, 6f, new Color32(46, 42, 34, 255)));
                }
            }
            _edges.SetSegments();
        }

        static WebLineGraphic.Segment Seg(WebNode a, WebNode b, float width, Color32 c) =>
            new WebLineGraphic.Segment { a = new Vector2(a.x, a.y), b = new Vector2(b.x, b.y), width = width, color = c };

        void RebuildDots()
        {
            var dots = _dots.Dots;
            dots.Clear();
            foreach (var n in _data.nodes)
            {
                bool charted = _data.IsCharted(n);
                if (!charted)
                {
                    if (Config.FrontierPreview)
                        dots.Add(new WebDotGraphic.Dot { pos = new Vector2(n.x, n.y), size = Mathf.Max(20f, WebGenerator.NodeDiameter(n) * 0.55f), color = new Color32(44, 40, 34, 255) });
                    continue;
                }
                if (!_lodActive || !_widgets.TryGetValue(n.id, out var w) || !w.small || _selected == n) continue;

                Color c = n.unlocked ? CGold : _pendingSet.Contains(n.id) ? CCyan : _reachable.Contains(n.id) ? CReachRing : Hex("#4A443A");
                if (!string.IsNullOrEmpty(_query) && !Matches(n)) c.a = 0.25f;
                dots.Add(new WebDotGraphic.Dot { pos = new Vector2(n.x, n.y), size = WebGenerator.NodeDiameter(n) * 1.15f, color = c });
            }
            _dots.SetDots();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  View: pan, zoom, culling, overlays
        // ══════════════════════════════════════════════════════════════════════

        void Update()
        {
            if (!IsOpen) return;
            var mouse = Mouse.current;
            var kb = Keyboard.current;

            if (kb != null && !(_search != null && _search.isFocused))
            {
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    if (_selected != null) { _selected = null; RebuildCard(); RestyleAll(); }
                    else { Close(); return; }
                }
                if (kb.fKey.wasPressedThisFrame) FrameWhole();
            }

            if (mouse != null)
            {
                Vector2 mp = mouse.position.ReadValue();
                bool overView = RectTransformUtility.RectangleContainsScreenPoint(_viewport, mp, null) && !OverBlocker(mp);

                if (mouse.leftButton.wasPressedThisFrame)
                {
                    _dragging = overView;
                    _dragStart = _lastMouse = mp;
                    _dragDistance = 0f;
                }
                if (_dragging && mouse.leftButton.isPressed)
                {
                    Vector2 delta = mp - _lastMouse;
                    _lastMouse = mp;
                    _dragDistance = Mathf.Max(_dragDistance, (mp - _dragStart).magnitude);
                    if (_dragDistance > DragThreshold && delta != Vector2.zero)
                    {
                        _pan += delta / Mathf.Max(0.01f, _rootCanvas.scaleFactor);
                        _viewDirty = true;
                    }
                }
                if (mouse.leftButton.wasReleasedThisFrame) _dragging = false;

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f && overView)
                {
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, mp, null, out Vector2 local);
                    ZoomAround(_zoom * (scroll > 0 ? 1.15f : 1f / 1.15f), local - _viewport.rect.center);
                }

                if (_tooltip.gameObject.activeSelf)
                {
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_cardCanvas.transform, mp, null, out Vector2 lp);
                    var cr = ((RectTransform)_cardCanvas.transform).rect;
                    float x = Mathf.Min(lp.x + 18f, cr.xMax - _tooltip.rect.width - 8f);
                    float y = Mathf.Max(lp.y - 18f, cr.yMin + _tooltip.rect.height + 8f);
                    _tooltip.anchoredPosition = new Vector2(x, y);
                }
            }

            if (_viewDirty) ApplyView();
        }

        void LateUpdate()
        {
            if (!IsOpen) return;
            PositionCard();
        }

        bool OverBlocker(Vector2 screen)
        {
            foreach (var r in _overlayBlockers)
                if (r != null && r.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(r, screen, null)) return true;
            if (_card != null && _card.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(_card, screen, null)) return true;
            return false;
        }

        void ZoomAround(float newZoom, Vector2 viewLocal)
        {
            newZoom = Mathf.Clamp(newZoom, MinZoom, MaxZoom);
            Vector2 world = (viewLocal - _pan) / _zoom;
            _zoom = newZoom;
            _pan = viewLocal - world * _zoom;
            _viewDirty = true;
        }

        void CenterOn(Vector2 world, float zoom)
        {
            _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            _pan = -world * _zoom;
            _viewDirty = true;
        }

        void FrameWhole()
        {
            if (_data == null) return;
            var r = _viewport.rect;
            _zoom = Mathf.Clamp(Mathf.Min(r.width, r.height) / (2f * Mathf.Max(500f, _data.worldRadius)), MinZoom, MaxZoom);
            _pan = Vector2.zero;
            _viewDirty = true;
        }

        /// <summary>Opening view: the charted part of the web, fitted to the viewport.</summary>
        void FrameCharted()
        {
            if (_data == null || _data.sectors.Count == 0) { FrameWhole(); return; }
            int charted = _data.sectors.Max(s => s.chartedRing);
            float radius = WebGenerator.RingRadius(charted + 0.6f);
            var r = _viewport.rect;
            if (r.width < 10f) { _zoom = 0.3f; _pan = Vector2.zero; _viewDirty = true; return; }
            _zoom = Mathf.Clamp(Mathf.Min(r.width, r.height) / (2f * radius), MinZoom, MaxZoom);
            _pan = Vector2.zero;
            _viewDirty = true;
        }

        void ApplyView()
        {
            _viewDirty = false;
            if (_data == null) return;
            _content.localScale = new Vector3(_zoom, _zoom, 1f);
            _content.anchoredPosition = _pan;
            _zoomText.text = Mathf.RoundToInt(_zoom * 100f) + "%";

            // Parallax starfield.
            var vr = _viewport.rect;
            float tile = 512f;
            _starfield.uvRect = new Rect(-_pan.x * 0.25f / tile, -_pan.y * 0.25f / tile, vr.width / tile, vr.height / tile);

            bool lod = _zoom < LodZoom;
            if (lod != _lodActive) { _lodActive = lod; RebuildDots(); }

            // Cull: widgets inside the viewport (+margin) only; small nodes become dots when zoomed out.
            float margin = 140f;
            float left = (vr.xMin - margin - _pan.x) / _zoom, right = (vr.xMax + margin - _pan.x) / _zoom;
            float bottom = (vr.yMin - margin - _pan.y) / _zoom, top = (vr.yMax + margin - _pan.y) / _zoom;
            bool showLabels = _zoom >= LabelZoom;
            foreach (var w in _widgets.Values)
            {
                var n = w.node;
                bool visible = n.x >= left && n.x <= right && n.y >= bottom && n.y <= top
                               && _data.IsCharted(n)
                               && !(_lodActive && w.small && _selected != n);
                if (w.rt.gameObject.activeSelf != visible) w.rt.gameObject.SetActive(visible);
                if (visible && w.label != null)
                {
                    bool show = showLabels || ((n.type == WebNodeType.Keystone || n.IsGate) && _zoom >= 0.35f);
                    if (w.label.enabled != show) w.label.enabled = show;
                }
            }

            // Sector names float over their wedge when zoomed out.
            foreach (var s in _data.sectors)
            {
                if (!_sectorLabels.TryGetValue(s.id, out var t)) continue;
                float lr = WebGenerator.RingRadius(_data.maxRing * 0.55f);
                Vector2 p = _pan + new Vector2(Mathf.Cos(s.angleCenter), Mathf.Sin(s.angleCenter)) * lr * _zoom + vr.center;
                bool show = _zoom < 0.5f && vr.Contains(p);
                t.gameObject.SetActive(show);
                if (!show) continue;
                t.rectTransform.anchoredPosition = p - vr.center;
                t.text = s.name.ToUpperInvariant() + "\n<size=11><color=#A79C86>RING " + s.chartedRing + " OF " + _data.maxRing + " CHARTED</color></size>";
            }

            UpdateMinimapView();
        }

        // ── Minimap ─────────────────────────────────────────────────────────────

        void RedrawMinimap()
        {
            if (_miniTex == null || _data == null) return;
            int s = _miniTex.width;
            var px = new Color32[s * s];
            var bg = new Color32(12, 11, 9, 245);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            float R = Mathf.Max(500f, _data.worldRadius);
            var dim = new Color32(90, 82, 68, 255);
            var dark = new Color32(44, 40, 34, 255);
            var gold = (Color32)CGold;
            var cyan = (Color32)CCyan;

            foreach (var n in _data.nodes)
            {
                int x = Mathf.RoundToInt((n.x / R * 0.5f + 0.5f) * (s - 1));
                int y = Mathf.RoundToInt((n.y / R * 0.5f + 0.5f) * (s - 1));
                if (n.unlocked || n.ring == 0) Plot(px, s, x, y, 1, gold);
                else if (_pendingSet.Contains(n.id)) Plot(px, s, x, y, 1, cyan);
                else if (_data.IsCharted(n)) Plot(px, s, x, y, 0, dim);
                else Plot(px, s, x, y, 0, dark);
            }
            _miniTex.SetPixels32(px);
            _miniTex.Apply();
        }

        static void Plot(Color32[] px, int s, int cx, int cy, int r, Color32 c)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                    if (x >= 0 && y >= 0 && x < s && y < s) px[y * s + x] = c;
        }

        void UpdateMinimapView()
        {
            if (_miniView == null || _data == null) return;
            float R = Mathf.Max(500f, _data.worldRadius);
            var mr = _minimap.rectTransform.rect;
            var vr = _viewport.rect;
            // Visible world rect -> minimap units.
            Vector2 wMin = (new Vector2(vr.xMin, vr.yMin) - _pan) / _zoom;
            Vector2 wMax = (new Vector2(vr.xMax, vr.yMax) - _pan) / _zoom;
            float k = mr.width / (2f * R);
            _miniView.anchoredPosition = (wMin + wMax) * 0.5f * k;
            _miniView.sizeDelta = new Vector2(Mathf.Min(mr.width * 2, (wMax.x - wMin.x) * k), Mathf.Min(mr.height * 2, (wMax.y - wMin.y) * k));
        }

        void OnMinimapClick()
        {
            var mouse = Mouse.current;
            if (mouse == null || _data == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_minimap.rectTransform, mouse.position.ReadValue(), null, out Vector2 local);
            var mr = _minimap.rectTransform.rect;
            float R = Mathf.Max(500f, _data.worldRadius);
            Vector2 world = new Vector2(local.x / mr.width, local.y / mr.height) * 2f * R;
            _pan = -world * _zoom;
            _viewDirty = true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Interaction
        // ══════════════════════════════════════════════════════════════════════

        void OnNodeClicked(WebNode node)
        {
            if (_dragDistance > DragThreshold) return; // that was a pan
            if (!_data.IsCharted(node)) return;
            _selected = _selected == node ? null : node;
            HideTooltip();
            SetPathPreview(null);
            RebuildCard();
            RestyleAll();
            if (_lodActive) RebuildDots();
            _viewDirty = true;
        }

        void OnNodeHover(WebNode node)
        {
            if (_dragging && _dragDistance > DragThreshold) return;
            _hovered = node;
            if (node != _selected) ShowTooltipText(TooltipText(node));
            SetPathPreview(node);
        }

        void OnNodeHoverExit(WebNode node)
        {
            if (_hovered != node) return;
            _hovered = null;
            HideTooltip();
            SetPathPreview(null);
        }

        /// <summary>PoE-style: hovering a locked node lights the cheapest path to it.</summary>
        void SetPathPreview(WebNode target)
        {
            var before = new HashSet<string>(_pathPreview);
            _pathPreview.Clear();
            if (target != null && !Taken(target) && !_reachable.Contains(target.id))
            {
                var (path, _) = WebGraph.FindCheapestPath(target, _data, _pendingSet);
                if (path != null) foreach (var n in path) _pathPreview.Add(n.id);
            }
            if (before.SetEquals(_pathPreview)) return;
            foreach (var id in before.Union(_pathPreview))
                if (_widgets.TryGetValue(id, out var w)) StyleNode(w);
            RebuildEdges();
        }

        void RestyleAll()
        {
            foreach (var w in _widgets.Values) StyleNode(w);
        }

        void QueueNode(WebNode n)
        {
            if (!WebGraph.CanUnlock(n, _data, _pendingSet)) return;
            int cost = WebGraph.GetUnlockCost(n);
            if (Available < cost) { Toast($"You need {cost - Available} more Resonance."); return; }
            _pending.Add(n.id);
            _pendingSet.Add(n.id);
            ChartForPending();
            Refresh();
        }

        void QueuePath(WebNode target)
        {
            var (path, cost) = WebGraph.FindCheapestPath(target, _data, _pendingSet);
            if (path == null || path.Count == 0) return;
            if (Available < cost) { Toast($"That path costs {cost}; you have {Available} Resonance to spend."); return; }
            foreach (var n in path)
            {
                if (_pendingSet.Add(n.id)) _pending.Add(n.id);
            }
            ChartForPending();
            Refresh();
        }

        /// <summary>Planning into the edge of the charted web reveals the next ring right away.</summary>
        void ChartForPending()
        {
            var grown = WebGrower.EnsureFrontierCharted(_data, Config, _pendingSet);
            if (grown.Count == 0) return;
            SkillWebPlugin.Instance.SaveData();
            foreach (var s in grown)
                if (!SkillWebPlugin.Instance.LastGrownSectors.Contains(s)) SkillWebPlugin.Instance.LastGrownSectors.Add(s);
        }

        void UnqueueNode(WebNode n)
        {
            var keep = new HashSet<string>();
            foreach (var x in _data.nodes) if (x.unlocked && x.ring > 0) keep.Add(x.id);
            foreach (var id in _pending) if (id != n.id) keep.Add(id);
            if (!WebGraph.AllConnected(_data, keep)) { Toast("Other pending nodes depend on this one."); return; }
            _pending.Remove(n.id);
            _pendingSet.Remove(n.id);
            Refresh();
        }

        void ClearPending()
        {
            _pending.Clear();
            _pendingSet.Clear();
        }

        void ApplyPending()
        {
            if (_pending.Count == 0) return;
            var nodes = _pending.Select(id => _data.GetNode(id)).Where(n => n != null).ToList();
            int count = nodes.Count;
            if (SkillWebPlugin.Instance.TryBuyNodes(nodes))
            {
                ClearPending();
                Toast(count == 1 ? $"Allocated {nodes[0].name}." : $"Allocated {count} nodes.");
                StartCoroutine(FlashNodes(nodes.Select(n => n.id).ToList()));
            }
            else Toast("Could not apply: something changed. Check the pending nodes.");
            Refresh();
        }

        void ReportGrowth()
        {
            var grown = SkillWebPlugin.Instance?.LastGrownSectors;
            if (grown == null || grown.Count == 0) return;
            string names = string.Join(", ", grown.Select(s => s.name));
            Toast(grown.Count == 1
                ? $"The web grows: ring {grown[0].chartedRing} of {grown[0].name} is now charted."
                : $"The web grows: new rings charted in {names}.");
            grown.Clear();
        }

        void NextMatch()
        {
            if (_matches.Count == 0) return;
            var m = _matches[_matchIdx % _matches.Count];
            _matchIdx++;
            _selected = m;
            CenterOn(new Vector2(m.x, m.y), Mathf.Max(_zoom, 0.7f));
            RebuildCard();
            RestyleAll();
        }

        void RebuildMatches()
        {
            _matches.Clear();
            if (string.IsNullOrEmpty(_query)) { _matchText.text = ""; SetEnabled(_nextBtn, false); return; }
            foreach (var n in _data.nodes)
                if (n.type != WebNodeType.Travel && _data.IsCharted(n) && Matches(n)) _matches.Add(n);
            _matches.Sort((a, b) => a.radius.CompareTo(b.radius));
            _matchText.text = _matches.Count + (_matches.Count == 1 ? " match" : " matches");
            SetEnabled(_nextBtn, _matches.Count > 0);
        }

        bool Matches(WebNode n)
        {
            if (string.IsNullOrEmpty(_query) || !_data.IsCharted(n)) return false;
            string q = _query.ToLowerInvariant();
            bool Has(string s) => !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(q);
            if (Has(n.name) || Has(n.description) || Has(n.keystoneRule) || Has(n.abilityName)) return true;
            foreach (var k in n.stats.Keys) if (Has(k)) return true;
            foreach (var t in n.traits) if (Has(t)) return true;
            if (n.masteryOptions != null) foreach (var o in n.masteryOptions) if (Has(o)) return true;
            return false;
        }

        IEnumerator FlashNodes(List<string> ids)
        {
            float t = 0f;
            while (t < 0.45f)
            {
                t += Time.unscaledDeltaTime;
                float s = 1f + Mathf.Sin(t / 0.45f * Mathf.PI) * 0.3f;
                foreach (var id in ids)
                    if (_widgets.TryGetValue(id, out var w) && w.rt != null) w.rt.localScale = new Vector3(s, s, 1);
                yield return null;
            }
            foreach (var id in ids)
                if (_widgets.TryGetValue(id, out var w) && w.rt != null) w.rt.localScale = Vector3.one;
        }

        void Toast(string msg)
        {
            if (_toast == null) return;
            if (_toastRoutine != null) StopCoroutine(_toastRoutine);
            _toastRoutine = StartCoroutine(ToastRoutine(msg));
        }

        IEnumerator ToastRoutine(string msg)
        {
            _toastText.text = msg;
            _toast.SetActive(true);
            yield return new WaitForSecondsRealtime(4f);
            _toast.SetActive(false);
            _toastRoutine = null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Header and sidebar
        // ══════════════════════════════════════════════════════════════════════

        void RefreshHeader()
        {
            int level = _manager?.playerCharacter?.playerLevel ?? 1;
            int allocated = _data.nodes.Count(n => n.unlocked && n.ring > 0);
            _subtitle.text = $"Level {level}  ·  {allocated} of {_data.nodes.Count - 1 - _data.sectors.Count} nodes allocated";
            _resonanceText.text = Available.ToString();
            _pendingText.text = _pending.Count == 0 ? "No pending changes" : $"{_pending.Count} pending  ·  {PendingCost} Resonance";
            SetEnabled(_applyBtn, _pending.Count > 0);
            SetEnabled(_resetBtn, _pending.Count > 0);
        }

        void RefreshSidebar()
        {
            ClearChildren(_sideContent);

            // THE WEB
            var web = Section("THE WEB");
            var tiles = new GameObject("Tiles", typeof(RectTransform)).GetComponent<RectTransform>();
            tiles.SetParent(web, false);
            var th = tiles.gameObject.AddComponent<HorizontalLayoutGroup>();
            th.spacing = 8; th.childControlWidth = true; th.childControlHeight = true; th.childForceExpandWidth = true;
            Pref(tiles.gameObject, -1, 58);
            int charted = _data.nodes.Count(n => n.ring > 0 && _data.IsCharted(n));
            Tile(tiles, (_data.nodes.Count - 1 - _data.sectors.Count).ToString(), "nodes", CTextBright);
            Tile(tiles, charted.ToString(), "charted", CTextBright);
            Tile(tiles, _data.nodes.Count(n => n.unlocked && n.ring > 0).ToString(), "allocated", CGold);

            // ATTRIBUTES
            var attrs = Section("ATTRIBUTES FROM THE WEB");
            var pend = new Dictionary<string, float>();
            foreach (var id in _pending)
            {
                var n = _data.GetNode(id);
                if (n == null) continue;
                foreach (var kv in n.stats) pend[kv.Key] = (pend.TryGetValue(kv.Key, out float v) ? v : 0f) + kv.Value;
            }
            foreach (var a in ThemeLexicon.Attributes)
            {
                float val = 0f;
                if (Enum.TryParse(a, out SS.PlayerAttribute pa)) _data.CachedStats.TryGetValue(pa, out val);
                pend.TryGetValue(a, out float p);
                AttributeRow(attrs, a, val, p);
            }

            // TRAITS AND RULES
            var traits = Section("TRAITS AND RULES");
            int shown = 0;
            foreach (var n in _data.nodes)
            {
                if (!n.unlocked || n.ring == 0) continue;
                string text = null; Color c = CGold;
                if (n.type == WebNodeType.Keystone && !string.IsNullOrEmpty(n.keystoneRule)) { text = n.keystoneRule; c = COrange; }
                else if (n.type == WebNodeType.Mastery && n.masteryOptions != null && n.masteryChoice >= 0 && n.masteryChoice < n.masteryOptions.Count) { text = n.masteryOptions[n.masteryChoice]; c = Hex("#E8E0CF"); }
                else if (n.traits.Count > 0) text = n.traits[0];
                if (text == null) continue;
                TraitCard(traits, n.name, text, c);
                shown++;
            }
            if (shown == 0)
                Para(traits, "Notables, Masteries and Keystones add lines here. The narrator reads every one.", CTextDim);

            // SECTORS
            var secs = Section("SECTORS");
            foreach (var s in _data.sectors)
            {
                int got = _data.nodes.Count(n => n.sectorId == s.id && n.unlocked && n.ring > 0);
                int all = _data.nodes.Count(n => n.sectorId == s.id && n.ring > 0);
                var gate = _data.GetNode("gate:" + s.id);
                string sub = $"Ring {s.chartedRing} of {_data.maxRing} charted";
                if (gate != null && gate.feedingPerks.Count > 0) sub += $"  ·  {gate.feedingPerks.Count} perk{(gate.feedingPerks.Count == 1 ? "" : "s")}";
                SectorRow(secs, s, sub, got + " / " + all);
            }

            // LEDGER
            var ledger = Section("RESONANCE");
            long lv = 0, turns = 0, places = 0, perks = 0, other = 0;
            foreach (var kv in _data.economyLedger)
            {
                if (kv.Key.StartsWith("level:")) lv += kv.Value;
                else if (kv.Key.StartsWith("turns:")) turns += kv.Value;
                else if (kv.Key.StartsWith("place:")) places += kv.Value;
                else if (kv.Key.StartsWith("anchor_import:")) perks += kv.Value;
                else other += kv.Value;
            }
            LedgerRow(ledger, "Level-ups", "+" + lv);
            LedgerRow(ledger, "Turn milestones", "+" + turns);
            LedgerRow(ledger, "Places discovered", "+" + places);
            LedgerRow(ledger, "Perks learned", "+" + perks);
            if (other != 0) LedgerRow(ledger, "Other deeds", "+" + other);
            int spent = _data.nodes.Where(n => n.unlocked && n.ring > 0).Sum(n => WebGraph.TotalSpent(n));
            LedgerRow(ledger, "Spent on the web", "-" + spent);
        }

        RectTransform Section(string title)
        {
            var sec = new GameObject("Section", typeof(RectTransform)).GetComponent<RectTransform>();
            sec.SetParent(_sideContent, false);
            var v = sec.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 10; v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            var h = NewText("Title", sec, title, 13, CGold, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold; h.characterSpacing = 8;
            Pref(h.gameObject, -1, 18);
            return sec;
        }

        void Tile(RectTransform parent, string value, string label, Color c)
        {
            var bg = NewImage("Tile", parent, CTile);
            var o = bg.gameObject.AddComponent<Outline>(); o.effectColor = CBorder; o.effectDistance = new Vector2(1, -1);
            var v = NewText("Value", bg.transform, value, 20, c, TextAlignmentOptions.TopLeft);
            v.fontStyle = FontStyles.Bold;
            Place(v.rectTransform, new Vector2(0, 1), new Vector2(10, -6), new Vector2(90, 26), new Vector2(0, 1));
            var l = NewText("Label", bg.transform, label, 11, CTextDim, TextAlignmentOptions.TopLeft);
            Place(l.rectTransform, new Vector2(0, 1), new Vector2(10, -32), new Vector2(90, 18), new Vector2(0, 1));
        }

        void AttributeRow(RectTransform parent, string name, float value, float pending)
        {
            var row = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(parent, false);
            Pref(row.gameObject, -1, 30);
            var n = NewText("Name", row, name, 15, CText, TextAlignmentOptions.TopLeft);
            Place(n.rectTransform, new Vector2(0, 1), Vector2.zero, new Vector2(150, 22), new Vector2(0, 1));
            var val = NewText("Value", row, Signed(value), 16, CTextBright, TextAlignmentOptions.TopRight);
            val.fontStyle = FontStyles.Bold;
            Place(val.rectTransform, new Vector2(1, 1), Vector2.zero, new Vector2(70, 22), new Vector2(1, 1));
            if (Mathf.Abs(pending) > 0.001f)
            {
                var p = NewText("Pending", row, Signed(pending) + " pending", 12, CCyan, TextAlignmentOptions.TopRight);
                Place(p.rectTransform, new Vector2(1, 1), new Vector2(-76, -2), new Vector2(110, 20), new Vector2(1, 1));
            }
            var track = NewImage("Track", row, CReachFill);
            track.rectTransform.anchorMin = new Vector2(0, 0); track.rectTransform.anchorMax = new Vector2(1, 0);
            track.rectTransform.pivot = new Vector2(0.5f, 0); track.rectTransform.sizeDelta = new Vector2(0, 4); track.rectTransform.anchoredPosition = Vector2.zero;
            float max = Mathf.Max(1f, Config.MaxBonusPerAttribute * 0.4f);
            var bar = NewImage("Bar", track.transform, AttrColors.TryGetValue(name, out var ac) ? ac : CGold);
            bar.rectTransform.anchorMin = Vector2.zero; bar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(value / max), 1);
            bar.rectTransform.offsetMin = bar.rectTransform.offsetMax = Vector2.zero;
        }

        void TraitCard(RectTransform parent, string name, string text, Color c)
        {
            var bg = NewImage("Trait", parent, CTile);
            var o = bg.gameObject.AddComponent<Outline>(); o.effectColor = CBorder2; o.effectDistance = new Vector2(1, -1);
            var v = bg.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(12, 12, 9, 10); v.spacing = 3;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            var t = NewText("Name", bg.transform, name, 13, c, TextAlignmentOptions.TopLeft);
            t.fontStyle = FontStyles.Bold;
            var b = NewText("Text", bg.transform, text, 13, CTextSoft, TextAlignmentOptions.TopLeft);
            b.enableWordWrapping = true;
        }

        void SectorRow(RectTransform parent, WebSector s, string sub, string count)
        {
            var btn = NewButton("Sector", parent, "", ButtonStyle.Row, 14);
            Pref(btn.gameObject, -1, 48);
            var sw = NewImage("Swatch", btn.transform, GetColor(s.colorHex));
            sw.raycastTarget = false;
            Place(sw.rectTransform, new Vector2(0, 0.5f), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 0.5f));
            sw.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            var n = NewText("Name", btn.transform, s.name, 14, CText, TextAlignmentOptions.BottomLeft);
            n.raycastTarget = false;
            Place(n.rectTransform, new Vector2(0, 0.5f), new Vector2(30, 1), new Vector2(200, 22), new Vector2(0, 0));
            var sb = NewText("Sub", btn.transform, sub, 11, CTextDim, TextAlignmentOptions.TopLeft);
            sb.raycastTarget = false;
            Place(sb.rectTransform, new Vector2(0, 0.5f), new Vector2(30, -1), new Vector2(220, 18), new Vector2(0, 1));
            var c = NewText("Count", btn.transform, count, 13, CTextDim, TextAlignmentOptions.MidlineRight);
            c.raycastTarget = false;
            Place(c.rectTransform, new Vector2(1, 0.5f), new Vector2(-8, 0), new Vector2(70, 22), new Vector2(1, 0.5f));
            var cap = s;
            btn.onClick.AddListener(() =>
            {
                float r = WebGenerator.RingRadius(Mathf.Min(cap.chartedRing, 3) - 0.5f);
                CenterOn(new Vector2(Mathf.Cos(cap.angleCenter), Mathf.Sin(cap.angleCenter)) * r, 0.6f);
            });
        }

        void LedgerRow(RectTransform parent, string label, string value)
        {
            var row = new GameObject(label, typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(parent, false);
            Pref(row.gameObject, -1, 20);
            var l = NewText("L", row, label, 13, CTextDim, TextAlignmentOptions.MidlineLeft);
            Stretch(l.rectTransform);
            var v = NewText("V", row, value, 13, CText, TextAlignmentOptions.MidlineRight);
            Stretch(v.rectTransform);
        }

        void Para(RectTransform parent, string text, Color c)
        {
            var t = NewText("Para", parent, text, 13, c, TextAlignmentOptions.TopLeft);
            t.enableWordWrapping = true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Node card
        // ══════════════════════════════════════════════════════════════════════

        void RebuildCard()
        {
            ClearChildren(_card);
            var n = _selected;
            if (n == null || _data == null || !_data.IsCharted(n)) { _card.gameObject.SetActive(false); return; }
            _card.gameObject.SetActive(true);

            var sector = _data.GetSector(n.sectorId);
            bool alloc = n.unlocked && n.ring > 0;
            bool pend = _pendingSet.Contains(n.id);
            bool reach = _reachable.Contains(n.id);

            // Header
            var head = NewImage("Head", _card, Hex("#1C1812"));
            var hv = head.gameObject.AddComponent<VerticalLayoutGroup>();
            hv.padding = new RectOffset(16, 40, 12, 10); hv.spacing = 2;
            hv.childControlWidth = true; hv.childControlHeight = true; hv.childForceExpandHeight = false;
            var name = NewText("Name", head.transform, n.name, 21, TitleColor(n), TextAlignmentOptions.Center);
            name.fontStyle = FontStyles.Bold; name.enableWordWrapping = true;
            var type = NewText("Type", head.transform, TypeLabel(n).ToUpperInvariant(), 11, CTextDim, TextAlignmentOptions.Center);
            type.characterSpacing = 6; type.enableWordWrapping = true;
            var x = NewButton("Close", head.transform, "X", ButtonStyle.Flat, 14);
            var xle = x.gameObject.AddComponent<LayoutElement>(); xle.ignoreLayout = true;
            Place((RectTransform)x.transform, new Vector2(1, 1), new Vector2(-4, -4), new Vector2(32, 32), new Vector2(1, 1));
            x.onClick.AddListener(() => { _selected = null; RebuildCard(); RestyleAll(); });

            // Body
            var body = CardBlock("Body", 16, 12, 8);
            float mult = alloc && n.IsTiered ? 1f + (n.tier - 1) * 0.5f : 1f;
            foreach (var kv in n.stats)
                CardText(body, Signed(kv.Value * mult) + " " + kv.Key, 17, CBlue, TextAlignmentOptions.Center);

            string flavor = n.traits.Count > 0 ? n.traits[0] : (n.type == WebNodeType.Keystone ? null : n.description);
            if (n.IsGate) flavor = n.description;
            if (!string.IsNullOrEmpty(flavor))
            {
                var f = CardText(body, flavor, 15, CAmber, TextAlignmentOptions.Center);
                f.fontStyle = FontStyles.Italic;
            }

            if (n.IsGate) GatePerks(body, n);

            if (n.type == WebNodeType.Mastery && n.masteryOptions != null)
            {
                var hdr = CardText(body, alloc ? "CHOOSE ONE EFFECT" : "ALLOCATE TO CHOOSE ONE EFFECT", 11, CGold, TextAlignmentOptions.Left);
                hdr.characterSpacing = 6;
                for (int i = 0; i < n.masteryOptions.Count; i++)
                {
                    int idx = i;
                    bool on = n.masteryChoice == i;
                    var ob = NewButton("Option", body, "", on ? ButtonStyle.Chosen : ButtonStyle.Option, 13);
                    var ot = ob.GetComponentInChildren<TextMeshProUGUI>();
                    ot.text = n.masteryOptions[i];
                    ot.alignment = TextAlignmentOptions.MidlineLeft;
                    ot.enableWordWrapping = true;
                    ot.color = on ? CBlue : CTextSoft;
                    ot.margin = new Vector4(10, 6, 10, 6);
                    var le = ob.gameObject.AddComponent<LayoutElement>();
                    le.minHeight = 44; le.preferredHeight = Mathf.Max(44, ot.GetPreferredValues(n.masteryOptions[i], CardWidth - 52, 0).y + 14);
                    SetEnabled(ob, alloc);
                    ob.onClick.AddListener(() => { SkillWebPlugin.Instance.TryChooseMastery(n, idx); Refresh(); });
                }
            }

            if (n.type == WebNodeType.Keystone && !string.IsNullOrEmpty(n.keystoneRule))
            {
                var blk = CardInset(body, "KEYSTONE RULE", COrange);
                var t = NewText("Rule", blk, n.keystoneRule, 14, Hex("#E8E0CF"), TextAlignmentOptions.TopLeft);
                t.enableWordWrapping = true;
            }

            bool grants = n.type == WebNodeType.Keystone || (n.type == WebNodeType.Confluence && Config.AbilitiesFromConfluences);
            if (grants && Config.GrantUsableAbilities)
            {
                var blk = CardInset(body, "GRANTS ABILITY", CCyan);
                var an = NewText("Name", blk, string.IsNullOrWhiteSpace(n.abilityName) ? n.name : n.abilityName, 15, CTextBright, TextAlignmentOptions.TopLeft);
                an.fontStyle = FontStyles.Bold;
                string desc = !string.IsNullOrEmpty(n.grantedAbilityDesc) ? n.grantedAbilityDesc
                    : "Usable from the Abilities bar once allocated: click it, then pick a target.";
                var ad = NewText("Desc", blk, desc, 13, CTextSoft, TextAlignmentOptions.TopLeft);
                ad.enableWordWrapping = true;
            }

            if (alloc && n.IsTiered)
                CardText(body, $"Tier {n.tier} of 3", 12, CTextDim, TextAlignmentOptions.Center);

            // Footer
            var foot = CardBlock("Foot", 16, 10, 8);
            var rule = NewImage("Rule", foot, CBorder); Pref(rule.gameObject, -1, 1);
            var (status, statusColor) = StatusFor(n, alloc, pend, reach);
            CardText(foot, status, 14, statusColor, TextAlignmentOptions.Center);

            var row = new GameObject("Buttons", typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(foot, false);
            var rh = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            rh.spacing = 8; rh.childControlWidth = true; rh.childControlHeight = true; rh.childForceExpandWidth = true;
            Pref(row.gameObject, -1, 44);
            int buttons = 0;

            if (reach && !pend)
            {
                int cost = WebGraph.GetUnlockCost(n);
                var b = NewButton("Allocate", row, $"Allocate  ·  {cost} Resonance", ButtonStyle.Gold, 14);
                SetEnabled(b, Available >= cost);
                b.onClick.AddListener(() => QueueNode(n));
                buttons++;
            }
            else if (!alloc && !pend && !n.IsGate && n.ring > 0 && n.type != WebNodeType.Mastery)
            {
                var (path, cost) = WebGraph.FindCheapestPath(n, _data, _pendingSet);
                if (path != null && path.Count > 1)
                {
                    var b = NewButton("Path", row, $"Plan path  ·  {path.Count} nodes, {cost}", ButtonStyle.Gold, 13);
                    SetEnabled(b, Available >= cost);
                    b.onClick.AddListener(() => QueuePath(n));
                    buttons++;
                }
            }
            if (pend)
            {
                var b = NewButton("Undo", row, "Undo", ButtonStyle.Cyan, 14);
                b.onClick.AddListener(() => UnqueueNode(n));
                buttons++;
            }
            if (alloc && n.IsTiered && Config.MasteryTiersEnabled && n.tier < 3)
            {
                int cost = WebGraph.GetUpgradeCost(n);
                var b = NewButton("Upgrade", row, $"Upgrade ({cost})", ButtonStyle.Gold, 13);
                SetEnabled(b, Available >= cost);
                b.onClick.AddListener(() =>
                {
                    if (SkillWebPlugin.Instance.TryUpgradeNode(n)) Toast($"{n.name} is now tier {n.tier}.");
                    Refresh();
                });
                buttons++;
            }
            if (alloc)
            {
                bool can = SkillWebPlugin.Instance.CanRefund(n);
                var b = NewButton("Refund", row, can ? $"Refund (+{SkillWebPlugin.RefundValue(n)})" : "Refund", ButtonStyle.Ghost, 13);
                SetEnabled(b, can);
                b.onClick.AddListener(() =>
                {
                    if (SkillWebPlugin.Instance.TryRefundNode(n)) Toast($"Refunded {n.name}.");
                    Refresh();
                });
                buttons++;
            }
            if (buttons == 0) row.gameObject.SetActive(false);
        }

        void GatePerks(RectTransform body, WebNode gate)
        {
            if (gate.feedingPerks.Count == 0) return;
            var blk = CardInset(body, "PERKS FEEDING THIS STAR", CGold);
            foreach (var p in gate.feedingPerks)
            {
                string stats = string.Join(", ", p.stats.Select(kv => Signed(kv.Value) + " " + kv.Key));
                string line = p.name + (p.active ? "  <color=#6FD3E8>(active, x" + Config.ActiveBonusMultiplier.ToString("0.#") + ")</color>" : "")
                            + (stats.Length > 0 ? "\n<color=#9AA8FF>" + stats + "</color>" : "");
                var t = NewText("Perk", blk, line, 13, CTextSoft, TextAlignmentOptions.TopLeft);
                t.enableWordWrapping = true;
            }
        }

        (string, Color) StatusFor(WebNode n, bool alloc, bool pend, bool reach)
        {
            if (n.IsOrigin) return ("The centre of the web. Always yours.", CTextDim);
            if (n.IsGate)
                return n.feedingPerks.Count > 0
                    ? ("Awake. Learn more perks in the native Perks window to strengthen it.", CTextSoft)
                    : ("Dormant. Learn a perk in this sector's native perk tree to light it. Its paths are open either way.", CTextDim);
            if (alloc)
            {
                if (n.type == WebNodeType.Mastery && n.masteryChoice < 0) return ("Allocated. Choose an effect above.", CGold);
                return SkillWebPlugin.Instance.CanRefund(n)
                    ? ("Allocated", CGold)
                    : ("Allocated. Only the end of a path can be refunded.", CGold);
            }
            if (pend) return ("Pending. Press Apply to lock it in.", CCyan);
            if (reach)
            {
                int cost = WebGraph.GetUnlockCost(n);
                if (Available < cost) return ($"You need {cost - Available} more Resonance.", COrange);
                return (n.type == WebNodeType.Mastery ? "Unlocked by a node in this cluster." : "Reachable from your path.", CTextSoft);
            }
            if (n.type == WebNodeType.Mastery) return ("Allocate any node in this cluster to unlock it.", CTextDim);
            return ("Path blocked. Allocate a connected node first.", CTextDim);
        }

        RectTransform CardBlock(string name, int padX, int padY, float spacing)
        {
            var b = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            b.SetParent(_card, false);
            var v = b.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(padX, padX, padY, padY + 4); v.spacing = spacing;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return b;
        }

        RectTransform CardInset(RectTransform parent, string title, Color titleColor)
        {
            var bg = NewImage("Inset", parent, CTile);
            var o = bg.gameObject.AddComponent<Outline>(); o.effectColor = CBorder2; o.effectDistance = new Vector2(1, -1);
            var v = bg.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(12, 12, 10, 10); v.spacing = 4;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            var t = NewText("Title", bg.transform, title, 11, titleColor, TextAlignmentOptions.TopLeft);
            t.characterSpacing = 6;
            return bg.rectTransform;
        }

        TextMeshProUGUI CardText(RectTransform parent, string text, float size, Color c, TextAlignmentOptions align)
        {
            var t = NewText("Line", parent, text, size, c, align);
            t.enableWordWrapping = true;
            return t;
        }

        void PositionCard()
        {
            if (_selected == null || !_card.gameObject.activeSelf) return;
            if (!_widgets.TryGetValue(_selected.id, out var w)) return;

            var canvasRt = (RectTransform)_cardCanvas.transform;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, w.rt.position);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, screen, null, out Vector2 p);

            // Viewport bounds in card-canvas space.
            var corners = new Vector3[4];
            _viewport.GetWorldCorners(corners);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, RectTransformUtility.WorldToScreenPoint(null, corners[0]), null, out Vector2 vmin);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, RectTransformUtility.WorldToScreenPoint(null, corners[2]), null, out Vector2 vmax);

            float off = WebGenerator.NodeDiameter(_selected) * 0.5f * _zoom + 22f;
            float h = _card.rect.height;
            bool rightSide = p.x < (vmin.x + vmax.x) * 0.5f + 80f;
            float x = rightSide ? p.x + off : p.x - off - CardWidth;
            x = Mathf.Clamp(x, vmin.x + 12f, vmax.x - CardWidth - 12f);
            float y = Mathf.Clamp(p.y + 90f, vmin.y + h + 12f, vmax.y - 12f);
            _card.anchoredPosition = new Vector2(x, y);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Tooltip text
        // ══════════════════════════════════════════════════════════════════════

        string TooltipText(WebNode n)
        {
            if (!_data.IsCharted(n)) return "<color=#A79C86>Uncharted. This part of the web reveals itself as you reach it.</color>";
            var sb = new System.Text.StringBuilder();
            sb.Append("<b><color=#").Append(ColorUtility.ToHtmlStringRGB(TitleColor(n))).Append(">").Append(n.name).Append("</color></b>\n");
            sb.Append("<size=11><color=#A79C86>").Append(TypeLabel(n).ToUpperInvariant()).Append("</color></size>");
            bool alloc = n.unlocked && n.ring > 0;
            float mult = alloc && n.IsTiered ? 1f + (n.tier - 1) * 0.5f : 1f;
            foreach (var kv in n.stats) sb.Append("\n<color=#9AA8FF>").Append(Signed(kv.Value * mult)).Append(' ').Append(kv.Key).Append("</color>");
            if (n.traits.Count > 0) sb.Append("\n<i><color=#D9975C>").Append(n.traits[0]).Append("</color></i>");
            if (!string.IsNullOrEmpty(n.keystoneRule)) sb.Append("\n<color=#E0894A>Rule:</color> ").Append(n.keystoneRule);
            if (n.type == WebNodeType.Mastery && n.masteryOptions != null)
            {
                for (int i = 0; i < n.masteryOptions.Count; i++)
                    sb.Append(i == n.masteryChoice ? "\n<color=#9AA8FF>> " : "\n<color=#CFC6B3>- ").Append(n.masteryOptions[i]).Append("</color>");
            }
            if (n.IsGate && n.feedingPerks.Count > 0) sb.Append("\n<color=#CFC6B3>Fed by ").Append(n.feedingPerks.Count).Append(" learned perk(s).</color>");

            if (!alloc && !n.IsGate && !n.IsOrigin && !_pendingSet.Contains(n.id))
            {
                if (_reachable.Contains(n.id)) sb.Append("\n\n<color=#6FD3E8>").Append(WebGraph.GetUnlockCost(n)).Append(" Resonance. Click for details.</color>");
                else if (_pathPreview.Count > 0)
                {
                    int cost = _pathPreview.Sum(id => WebGraph.GetUnlockCost(_data.GetNode(id)));
                    sb.Append("\n\n<color=#6FD3E8>Path: ").Append(_pathPreview.Count).Append(" nodes, ").Append(cost).Append(" Resonance.</color>");
                }
            }
            return sb.ToString();
        }

        string LedgerText()
        {
            if (_data == null) return "";
            var sb = new System.Text.StringBuilder("<b>Resonance</b>\n<size=12><color=#A79C86>Earned from levels, time survived, new places, learned perks and deeds recorded by other mods.</color></size>\n");
            sb.Append("\nAvailable: ").Append(Available);
            sb.Append("\nEarned in total: ").Append(_data.resonanceEarnedTotal);
            return sb.ToString();
        }

        void ShowTooltipText(string text)
        {
            if (_tooltip == null) return;
            _tooltipText.text = text;
            _tooltip.gameObject.SetActive(true);
        }

        void HideTooltip()
        {
            if (_tooltip != null) _tooltip.gameObject.SetActive(false);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Node descriptors
        // ══════════════════════════════════════════════════════════════════════

        Color SectorColor(WebNode n)
        {
            if (n.type == WebNodeType.Confluence) return Hex("#5FB8C8");
            var s = _data.GetSector(n.sectorId);
            return s != null ? GetColor(s.colorHex) : CGold;
        }

        Color TitleColor(WebNode n)
        {
            if (n.IsOrigin) return CTextBright;
            switch (n.type)
            {
                case WebNodeType.Anchor: return SectorColor(n);
                case WebNodeType.Notable: return CGold;
                case WebNodeType.Keystone: return COrange;
                case WebNodeType.Confluence: return CConf;
                case WebNodeType.Mastery: return Hex("#E8E0CF");
                default: return CText;
            }
        }

        string TypeLabel(WebNode n)
        {
            if (n.IsOrigin) return "Origin";
            var s = _data.GetSector(n.sectorId);
            string sn = s?.name ?? "";
            switch (n.type)
            {
                case WebNodeType.Anchor: return "Anchor Star · " + sn;
                case WebNodeType.Confluence: return "Confluence · " + sn + " + " + (_data.GetSector(n.sectorId2)?.name ?? "");
                case WebNodeType.Travel: return "Attribute passive · " + sn;
                case WebNodeType.Mastery: return "Mastery · " + sn;
                case WebNodeType.Keystone: return "Keystone · " + sn;
                case WebNodeType.Notable: return "Notable passive · " + sn;
                default: return "Basic passive · " + sn;
            }
        }

        static string Signed(float v)
        {
            float r = Mathf.Round(v * 10f) / 10f;
            string s = Mathf.Abs(r - Mathf.Round(r)) < 0.01f ? Mathf.Abs(Mathf.Round(r)).ToString("0") : Mathf.Abs(r).ToString("0.0");
            return (r < 0 ? "-" : "+") + s;
        }

        // ── Icons ───────────────────────────────────────────────────────────────

        Sprite IconFor(WebNode n)
        {
            var generated = GetOrLoadNodeIcon(n);
            if (generated != null) return generated;
            if (n.IsGate && n.feedingPerks.Count == 0) return null;
            return SkillIconAtlas.PickForNode(n);
        }

        /// <summary>Called by NodeIconGen when a node's icon lands on disk.</summary>
        public void OnIconReady(string nodeId)
        {
            _iconMissUntil.Remove(nodeId);
            _iconCache.Remove(nodeId);
            if (IsOpen && _widgets.TryGetValue(nodeId, out var w)) StyleNode(w);
        }

        Sprite GetOrLoadNodeIcon(WebNode node)
        {
            if (_iconCache.TryGetValue(node.id, out Sprite cached) && cached != null) return cached;
            if (_iconMissUntil.TryGetValue(node.id, out float until) && Time.realtimeSinceStartup < until) return null;

            string path = NodeIconGen.GetIconPath(node);
            if (!File.Exists(path))
            {
                _iconMissUntil[node.id] = Time.realtimeSinceStartup + 10f; // re-check later: a gen may be in flight
                return null;
            }
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            KnockOutBackground(tex); // the model often invents a solid bg the prompt didn't ask for
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _iconCache[node.id] = sprite;
            return sprite;
        }

        /// <summary>
        /// Keys out a generated icon's solid background by sampling its corners (feathered edge).
        /// No-ops on images that are already transparent.
        /// </summary>
        static void KnockOutBackground(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            if (w < 8 || h < 8) return;
            Color32[] px = tex.GetPixels32();
            if (!TryEstimateCornerColor(px, w, h, out Color32 bg)) return;

            const float keyR = 55f, featherR = 105f;
            float keyR2 = keyR * keyR, featherR2 = featherR * featherR, span = featherR2 - keyR2;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a == 0) continue;
                float dr = px[i].r - bg.r, dg = px[i].g - bg.g, db = px[i].b - bg.b;
                float d2 = dr * dr + dg * dg + db * db;
                if (d2 <= keyR2) px[i].a = 0;
                else if (d2 < featherR2)
                {
                    byte a = (byte)(px[i].a * ((d2 - keyR2) / span));
                    if (a < px[i].a) px[i].a = a;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
        }

        static bool TryEstimateCornerColor(Color32[] px, int w, int h, out Color32 bg)
        {
            bg = default;
            int patch = Mathf.Max(2, Mathf.Min(w, h) / 20);
            long r = 0, g = 0, b = 0, a = 0; int n = 0;
            for (int corner = 0; corner < 4; corner++)
            {
                int x0 = (corner & 1) == 0 ? 0 : w - patch;
                int y0 = (corner & 2) == 0 ? 0 : h - patch;
                for (int y = y0; y < y0 + patch; y++)
                    for (int x = x0; x < x0 + patch; x++)
                    {
                        Color32 c = px[y * w + x];
                        r += c.r; g += c.g; b += c.b; a += c.a; n++;
                    }
            }
            if (n == 0 || (a / n) < 24) return false;
            bg = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Factory helpers
        // ══════════════════════════════════════════════════════════════════════

        enum ButtonStyle { Gold, Ghost, Cyan, Flat, Row, Option, Chosen }

        static Image NewImage(string name, Transform parent, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            var img = obj.GetComponent<Image>();
            img.color = color;
            return img;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, Color color, TextAlignmentOptions align)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var t = obj.GetComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.richText = true;
            return t;
        }

        static Button NewButton(string name, Transform parent, string label, ButtonStyle style, float fontSize)
        {
            Color bg, fg, edge = Color.clear;
            switch (style)
            {
                case ButtonStyle.Gold: bg = CGoldDeep; fg = Hex("#17130C"); edge = Hex("#F0D68C"); break;
                case ButtonStyle.Cyan: bg = Hex("#10191B"); fg = CCyan; edge = CCyan; break;
                case ButtonStyle.Flat: bg = Hex("#16130F"); fg = CText; break;
                case ButtonStyle.Row: bg = CPanel; fg = CText; break;
                case ButtonStyle.Option: bg = Hex("#110F0C"); fg = CTextSoft; edge = CBorder; break;
                case ButtonStyle.Chosen: bg = CReachFill; fg = CBlue; edge = CGold; break;
                default: bg = Hex("#1A1712"); fg = CText; edge = Hex("#5A4E3A"); break;
            }

            var img = NewImage(name, parent, bg);
            if (edge.a > 0f)
            {
                var o = img.gameObject.AddComponent<Outline>();
                o.effectColor = edge; o.effectDistance = new Vector2(1, -1);
            }
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = style == ButtonStyle.Gold ? new Color(1.12f, 1.12f, 1.12f) : new Color(1.6f, 1.6f, 1.6f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            cb.selectedColor = Color.white;
            cb.disabledColor = new Color(1f, 1f, 1f, 0.45f);
            cb.colorMultiplier = 1.5f;
            btn.colors = cb;

            var t = NewText("Label", img.transform, label, fontSize, fg, TextAlignmentOptions.Center);
            t.fontStyle = style == ButtonStyle.Gold ? FontStyles.Bold : FontStyles.Normal;
            t.raycastTarget = false;
            Stretch(t.rectTransform);
            return btn;
        }

        static void SetEnabled(Button b, bool on)
        {
            if (b == null) return;
            b.interactable = on;
            var t = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) { var c = t.color; c.a = on ? 1f : 0.45f; t.color = c; }
        }

        TMP_InputField BuildInput(string name, Transform parent, string placeholder)
        {
            var bg = NewImage(name, parent, CBg);
            var o = bg.gameObject.AddComponent<Outline>(); o.effectColor = CBorder2; o.effectDistance = new Vector2(1, -1);
            var field = bg.gameObject.AddComponent<TMP_InputField>();

            var area = new GameObject("Area", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            area.SetParent(bg.transform, false);
            Stretch(area);
            area.offsetMin = new Vector2(14, 4); area.offsetMax = new Vector2(-10, -4);

            var ph = NewText("Placeholder", area, placeholder, 15, Hex("#8A8070"), TextAlignmentOptions.MidlineLeft);
            Stretch(ph.rectTransform);
            var txt = NewText("Text", area, "", 15, CText, TextAlignmentOptions.MidlineLeft);
            Stretch(txt.rectTransform);

            field.textViewport = area;
            field.textComponent = txt;
            field.placeholder = ph;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.caretColor = CGold;
            field.selectionColor = new Color(CGold.r, CGold.g, CGold.b, 0.3f);
            return field;
        }

        static void AddHover(GameObject obj, Action enter, Action exit)
        {
            var et = obj.GetComponent<EventTrigger>() ?? obj.AddComponent<EventTrigger>();
            var e1 = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            e1.callback.AddListener(_ => enter());
            et.triggers.Add(e1);
            var e2 = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            e2.callback.AddListener(_ => exit());
            et.triggers.Add(e2);
        }

        static void Pref(GameObject obj, float width, float height)
        {
            var le = obj.GetComponent<LayoutElement>() ?? obj.AddComponent<LayoutElement>();
            if (width >= 0) { le.preferredWidth = width; le.minWidth = width; }
            if (height >= 0) { le.preferredHeight = height; le.minHeight = height; }
        }

        /// <summary>Detaches before destroying: Destroy is deferred, and a layout group would otherwise lay out the dying children for one more frame.</summary>
        static void ClearChildren(Transform t)
        {
            var kids = new List<GameObject>();
            foreach (Transform c in t) kids.Add(c.gameObject);
            foreach (var k in kids)
            {
                k.SetActive(false);
                k.transform.SetParent(null, false);
                Destroy(k);
            }
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        /// <summary>Anchors a rect at a single point with a fixed size.</summary>
        static void Place(RectTransform r, Vector2 anchor, Vector2 pos, Vector2 size, Vector2 pivot)
        {
            r.anchorMin = r.anchorMax = anchor;
            r.pivot = pivot;
            r.sizeDelta = size;
            r.anchoredPosition = pos;
        }

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.white;
        static Color GetColor(string hex) => Hex(hex ?? "#FFFFFF");
    }
}
