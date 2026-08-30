using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace AIROG_SkillWeb
{
    public class SkillWebUI : MonoBehaviour
    {
        public static SkillWebUI Instance { get; private set; }

        // ── References ──────────────────────────────────────────────────────────
        private GameplayManager _manager;
        private SkillWebData    _data;

        // Canvas layers
        private GameObject    _window;
        private RectTransform _contentRoot;
        private RectTransform _nodeContainer;
        private RectTransform _lineContainer;
        private Canvas        _tooltipCanvas;
        private RectTransform _viewportRect;
        
        // Node sprites
        private Sprite        _spriteBasic;
        private Sprite        _spriteNotable;
        private Sprite        _spriteKeystone;
        private Sprite        _spriteAnchor;

        // Chrome sprites (title banner, button plate, side-panel parchment)
        private Sprite        _spriteTitle;
        private Sprite        _spriteButton;
        private Sprite        _spritePanelBg;

        // Per-node generated icons, loaded from disk lazily and cached in memory
        private readonly Dictionary<string, Sprite> _iconSpriteCache = new Dictionary<string, Sprite>();

        // Shared attribute glyphs for Basic nodes (PoE-style), cached by filename so every
        // node of the same dominant attribute reuses one sprite. Value may be null (missing asset).
        private readonly Dictionary<string, Sprite> _glyphSpriteCache = new Dictionary<string, Sprite>();

        // Header / HUD
        private TextMeshProUGUI _resonanceText;
        private TextMeshProUGUI _levelText;
        private TextMeshProUGUI _statusText;
        private TMP_InputField  _searchField;

        // Side action panel (Right)
        private GameObject      _actionPanel;
        private TextMeshProUGUI _actionTitle;
        private TextMeshProUGUI _actionDesc;
        private TextMeshProUGUI _actionProvenance;
        private Button          _unlockBtn;
        private TextMeshProUGUI _unlockBtnLabel;
        private Button          _upgradeBtn;
        private TextMeshProUGUI _upgradeBtnLabel;
        private Button          _refundBtn;
        private TextMeshProUGUI _refundBtnLabel;

        // Left stat panel
        private GameObject      _statPanel;
        private TextMeshProUGUI _statTitle;
        private TextMeshProUGUI _statDesc;

        // Tooltip
        private GameObject      _tooltip;
        private TextMeshProUGUI _tooltipText;

        // ── State ───────────────────────────────────────────────────────────────
        private WebNode   _selectedNode;
        private float     _zoomLevel   = 1f;
        private const float MIN_ZOOM   = 0.15f;
        // Floor for FrameWeb's fit-to-viewport zoom only. A large, many-ring constellation can
        // need to zoom out further than the manual scroll floor to actually fit — clamping the
        // fit itself to MIN_ZOOM would silently crop outer rings while still reporting "Web Framed".
        private const float MIN_FIT_ZOOM = 0.02f;
        private const float MAX_ZOOM = 4f;
        private Vector2   _lastMousePos;
        private float     _dragDistance;
        private const float DRAG_THRESHOLD = 8f;
        private string    _searchQuery = "";
        // Tracks which SkillWebData was last successfully auto-framed, so a different save/web
        // loaded into this persisting singleton gets framed again instead of inheriting a stale
        // pan/zoom left over from whatever was framed before it.
        private SkillWebData _framedData;

        // UI Widget Mapping (for performant culling & animation)
        private readonly Dictionary<string, GameObject> _nodeWidgets = new Dictionary<string, GameObject>();
        private Vector2   _lastAnchoredPos;
        private float     _lastZoomLevel;

        // Path preview state
        private List<WebNode> _previewPath;
        private int           _previewCost;

        // ── Entry point ─────────────────────────────────────────────────────────

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
            _data    = data;
            if (_window == null) BuildUI();
            _window.SetActive(true);
            if (_tooltipCanvas != null) _tooltipCanvas.gameObject.SetActive(true);
            Refresh();

            // Open on a view that shows the whole web; afterwards respect whatever the player panned to.
            // Re-frame whenever this is a different save's web than the one we last framed — FrameWeb
            // itself only marks _framedData on a real fit, so a bail-out (viewport not sized yet, or an
            // empty web) leaves this unset and gets another chance next time Show() runs.
            if (!ReferenceEquals(_data, _framedData))
            {
                FrameWeb();
            }
        }

        public void Close()
        {
            if (_window != null) _window.SetActive(false);
            if (_tooltipCanvas != null) _tooltipCanvas.gameObject.SetActive(false);
            HideTooltip();
        }

        void OnDestroy()
        {
            if (_window != null)       Destroy(_window);
            if (_tooltipCanvas != null) Destroy(_tooltipCanvas.gameObject);
        }

        // ── UI construction ─────────────────────────────────────────────────────

        void BuildUI()
        {
            // Root canvas
            _window = new GameObject("SkillWebWindow");
            _window.transform.SetParent(null, false);
            _window.AddComponent<RectTransform>();
            var canvas = _window.AddComponent<Canvas>();
            canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder  = 500;
            _window.AddComponent<GraphicRaycaster>();
            var scaler = _window.AddComponent<CanvasScaler>();
            scaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode    = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            FullStretch(_window.GetComponent<RectTransform>());

            // Load chrome sprites up front — the header/panels below consume them
            _spriteTitle   = LoadSprite("title_constellation.png");
            _spriteButton  = LoadSprite("button_apply.png");
            _spritePanelBg = LoadSprite("SkillWeb_bkg_1.png");

            // Dim overlay
            var overlay = NewImg("Overlay", _window.transform, new Color(0, 0, 0, 0.72f));
            FullStretch(overlay.GetComponent<RectTransform>());

            // Header bar (top 8%)
            BuildHeader();

            // Viewport (8% – 94%)
            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            vp.transform.SetParent(_window.transform, false);
            vp.GetComponent<Image>().color = new Color(0, 0, 0, 0.01f);
            _viewportRect = vp.GetComponent<RectTransform>();
            _viewportRect.anchorMin = new Vector2(0.23f,    0.06f);
            _viewportRect.anchorMax = new Vector2(0.77f, 0.92f);
            _viewportRect.offsetMin = _viewportRect.offsetMax = Vector2.zero;

            // Content (panning/zooming root)
            var contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(vp.transform, false);
            _contentRoot = contentObj.GetComponent<RectTransform>();
            _contentRoot.sizeDelta        = new Vector2(8000, 8000);
            _contentRoot.anchoredPosition = Vector2.zero;
            _contentRoot.anchorMin        = new Vector2(0.5f, 0.5f);
            _contentRoot.anchorMax        = new Vector2(0.5f, 0.5f);

            // Background texture
            var bgObj = new GameObject("Background", typeof(RectTransform), typeof(RawImage));
            bgObj.transform.SetParent(_contentRoot, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.sizeDelta        = new Vector2(8000, 8000);
            bgRect.anchoredPosition = Vector2.zero;
            LoadBackground(bgObj.GetComponent<RawImage>());

            // Layers
            _lineContainer = NewLayer("Lines", _contentRoot);
            _nodeContainer = NewLayer("Nodes", _contentRoot);

            LoadNodeFrames();

            // Bottom bar (0% – 6%)
            BuildBottomBar();

            // Right side panel (77% – 100%)
            BuildActionPanel();

            // Left stat panel (0% – 23%)
            BuildStatPanel();

            // Tooltip canvas (always on top)
            BuildTooltipCanvas();
        }

        void BuildHeader()
        {
            var hdr = NewImg("Header", _window.transform, new Color(0.07f, 0.04f, 0.01f, 0.97f));
            var r   = hdr.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0.92f);
            r.anchorMax = new Vector2(1, 1f);
            r.offsetMin = r.offsetMax = Vector2.zero;

            // Title banner (ornate art; falls back to text if the asset is missing)
            if (_spriteTitle != null)
            {
                var titleImg = NewImg("TitleBanner", hdr.transform, Color.white);
                titleImg.sprite         = _spriteTitle;
                titleImg.type           = Image.Type.Simple;
                titleImg.preserveAspect = true;
                AnchorText(titleImg.rectTransform, new Vector2(0, 0.05f), new Vector2(0.28f, 0.95f), new Vector2(8, 0), Vector2.zero);
            }
            else
            {
                var title = NewText("Title", hdr.transform, "✦  CONSTELLATION", 24, TextAlignmentOptions.Left);
                title.color = new Color(0.7f, 0.5f, 0.9f);
                AnchorText(title.rectTransform, new Vector2(0, 0), new Vector2(0.25f, 1), new Vector2(14, 0), Vector2.zero);
            }

            // Resonance
            _resonanceText = NewText("Resonance", hdr.transform, "Resonance: 0 ⟡", 20, TextAlignmentOptions.Center);
            _resonanceText.color = new Color(0.4f, 0.9f, 1f);
            AnchorText(_resonanceText.rectTransform, new Vector2(0.29f, 0), new Vector2(0.45f, 1), Vector2.zero, Vector2.zero);

            // Add ledger hover trigger to Resonance HUD text
            var et = _resonanceText.gameObject.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, _ => ShowLedgerTooltip());
            AddTrigger(et, EventTriggerType.PointerExit,  _ => HideTooltip());

            // Search box
            _searchField = BuildInputField("SearchField", hdr.transform, new Vector2(0.46f, 0.15f), new Vector2(0.66f, 0.85f));
            _searchField.placeholder.GetComponent<TextMeshProUGUI>().text = "Search stars/stats/traits...";
            _searchField.onValueChanged.AddListener((val) =>
            {
                _searchQuery = val.Trim();
                Refresh();
            });

            // Level / node count
            _levelText = NewText("Level", hdr.transform, "Level 1", 16, TextAlignmentOptions.Right);
            _levelText.color = new Color(0.7f, 0.7f, 1f);
            AnchorText(_levelText.rectTransform, new Vector2(0.68f, 0), new Vector2(0.88f, 1), Vector2.zero, Vector2.zero);

            // Close button
            var closeObj = NewButton("Close [X]", hdr.transform, new Color(0.5f, 0.1f, 0.1f));
            var closeRect = closeObj.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.90f, 0.15f);
            closeRect.anchorMax = new Vector2(0.98f, 0.85f);
            closeRect.offsetMin = closeRect.offsetMax = Vector2.zero;
            closeRect.sizeDelta = Vector2.zero;
            closeObj.GetComponent<Button>().onClick.AddListener(Close);
        }

        void BuildBottomBar()
        {
            var bar   = NewImg("BottomBar", _window.transform, new Color(0.07f, 0.04f, 0.01f, 0.97f));
            var bRect = bar.GetComponent<RectTransform>();
            bRect.anchorMin = new Vector2(0,    0);
            bRect.anchorMax = new Vector2(0.77f, 0.06f);
            bRect.offsetMin = bRect.offsetMax = Vector2.zero;

            // Status text (left)
            _statusText = NewText("Status", bar.transform, "", 15, TextAlignmentOptions.Left);
            _statusText.color = Color.yellow;
            AnchorText(_statusText.rectTransform, new Vector2(0, 0), new Vector2(0.60f, 1), new Vector2(10, 0), Vector2.zero);

            // Legend / Instructions
            var instructions = NewText("Instructions", bar.transform, "Hold LMB to drag  •  Scroll to zoom  •  [F] Frame web", 13, TextAlignmentOptions.Right);
            instructions.color = new Color(0.7f, 0.7f, 0.7f);
            AnchorText(instructions.rectTransform, new Vector2(0.62f, 0), new Vector2(0.98f, 1), Vector2.zero, Vector2.zero);
        }

        void BuildActionPanel()
        {
            var apImg = NewImg("ActionPanel", _window.transform, new Color(0.06f, 0.03f, 0.01f, 0.97f));
            ApplyPanelBackground(apImg);
            _actionPanel = apImg.gameObject;
            var apRect = _actionPanel.GetComponent<RectTransform>();
            apRect.anchorMin = new Vector2(0.77f, 0f);
            apRect.anchorMax = new Vector2(1f,    0.92f);
            apRect.offsetMin = apRect.offsetMax = Vector2.zero;

            // Panel title
            _actionTitle = NewText("Title", _actionPanel.transform, "Select a Star", 18, TextAlignmentOptions.Center);
            _actionTitle.color = new Color(1f, 0.85f, 0.4f);
            _actionTitle.enableWordWrapping = true;
            AnchorText(_actionTitle.rectTransform, new Vector2(0, 0.80f), new Vector2(1, 1), new Vector2(6, 0), new Vector2(-6, 0));

            // Separator
            var sep = NewImg("Sep", _actionPanel.transform, new Color(1f, 0.85f, 0.4f, 0.3f));
            var sRect = sep.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(0.05f, 0.79f);
            sRect.anchorMax = new Vector2(0.95f, 0.795f);
            sRect.offsetMin = sRect.offsetMax = Vector2.zero;

            // Description + stats + traits
            _actionDesc = NewText("Desc", _actionPanel.transform, "", 13, TextAlignmentOptions.TopLeft);
            _actionDesc.color = new Color(0.85f, 0.85f, 0.85f);
            _actionDesc.enableWordWrapping = true;
            AnchorText(_actionDesc.rectTransform, new Vector2(0, 0.45f), new Vector2(1, 0.78f), new Vector2(12, 0), new Vector2(-12, 0));

            // Provenance
            _actionProvenance = NewText("Provenance", _actionPanel.transform, "", 11, TextAlignmentOptions.BottomLeft);
            _actionProvenance.color = new Color(0.5f, 0.7f, 0.9f);
            AnchorText(_actionProvenance.rectTransform, new Vector2(0, 0.35f), new Vector2(1, 0.43f), new Vector2(12, 0), new Vector2(-12, 0));

            // Unlock button
            _unlockBtn = BuildPanelButton("Unlock", _actionPanel.transform,
                new Vector2(0.05f, 0.23f), new Vector2(0.95f, 0.32f),
                new Color(0.1f, 0.4f, 0.1f));
            _unlockBtnLabel = _unlockBtn.GetComponentInChildren<TextMeshProUGUI>();
            _unlockBtn.onClick.AddListener(TryUnlockSelected);
            ApplyButtonPlate(_unlockBtn);

            // Upgrade button
            _upgradeBtn = BuildPanelButton("Upgrade Mastery", _actionPanel.transform,
                new Vector2(0.05f, 0.13f), new Vector2(0.95f, 0.22f),
                new Color(0.1f, 0.15f, 0.5f));
            _upgradeBtnLabel = _upgradeBtn.GetComponentInChildren<TextMeshProUGUI>();
            _upgradeBtn.onClick.AddListener(TryUpgradeSelected);
            ApplyButtonPlate(_upgradeBtn);

            // Refund button
            _refundBtn = BuildPanelButton("Refund Star", _actionPanel.transform,
                new Vector2(0.05f, 0.03f), new Vector2(0.95f, 0.12f),
                new Color(0.5f, 0.1f, 0.1f));
            _refundBtnLabel = _refundBtn.GetComponentInChildren<TextMeshProUGUI>();
            _refundBtn.onClick.AddListener(TryRefundSelected);
            ApplyButtonPlate(_refundBtn);

            _actionPanel.SetActive(false);
        }

        void BuildStatPanel()
        {
            var spImg = NewImg("StatPanel", _window.transform, new Color(0.06f, 0.03f, 0.01f, 0.97f));
            ApplyPanelBackground(spImg);
            _statPanel = spImg.gameObject;
            var spRect = _statPanel.GetComponent<RectTransform>();
            spRect.anchorMin = new Vector2(0f,    0.06f);
            spRect.anchorMax = new Vector2(0.23f, 0.92f);
            spRect.offsetMin = spRect.offsetMax = Vector2.zero;

            // Panel title
            _statTitle = NewText("Title", _statPanel.transform, "Aggregated Web Bonuses", 16, TextAlignmentOptions.Center);
            _statTitle.color = new Color(0.7f, 0.5f, 0.9f);
            AnchorText(_statTitle.rectTransform, new Vector2(0, 0.92f), new Vector2(1, 1), new Vector2(6, 0), new Vector2(-6, 0));

            // Separator
            var sep = NewImg("Sep", _statPanel.transform, new Color(0.7f, 0.5f, 0.9f, 0.3f));
            var sRect = sep.GetComponent<RectTransform>();
            sRect.anchorMin = new Vector2(0.05f, 0.91f);
            sRect.anchorMax = new Vector2(0.95f, 0.915f);
            sRect.offsetMin = sRect.offsetMax = Vector2.zero;

            // Stats content
            _statDesc = NewText("Desc", _statPanel.transform, "", 14, TextAlignmentOptions.TopLeft);
            _statDesc.color = new Color(0.85f, 0.85f, 0.85f);
            _statDesc.enableWordWrapping = true;
            AnchorText(_statDesc.rectTransform, new Vector2(0, 0f), new Vector2(1, 0.89f), new Vector2(12, 0), new Vector2(-12, 0));
        }

        void RefreshStatPanel()
        {
            if (_statDesc == null || _data == null || _data.CachedStats == null) return;
            string text = "";
            foreach (var kvp in _data.CachedStats)
            {
                if (Mathf.Abs(kvp.Value) > 0.01f)
                {
                    string sign = kvp.Value >= 0 ? "+" : "";
                    text += $"<color=#88FF88>{kvp.Key}: {sign}{kvp.Value:F0}</color>\n";
                }
            }
            if (string.IsNullOrEmpty(text))
                text = "<color=#888888>No attribute bonuses active yet. Buy stars adjacent to starting anchors.</color>";

            // Append colorful Disciplines Legend
            string legendText = "\n\n<color=#7f5090><b>Disciplines Legend:</b></color>\n";
            foreach (var sector in _data.sectors)
            {
                legendText += $"<color={sector.colorHex}>■</color> {sector.name}\n";
            }
            
            _statDesc.text = text + legendText;
        }

        void BuildTooltipCanvas()
        {
            var tcObj = new GameObject("SkillWebTooltip",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            tcObj.transform.SetParent(null, false);
            _tooltipCanvas = tcObj.GetComponent<Canvas>();
            _tooltipCanvas.renderMode      = RenderMode.ScreenSpaceOverlay;
            _tooltipCanvas.overrideSorting = true;
            _tooltipCanvas.sortingOrder    = 502;

            var tcScaler = tcObj.GetComponent<CanvasScaler>();
            tcScaler.uiScaleMode        = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            tcScaler.referenceResolution = new Vector2(1920, 1080);
            tcScaler.screenMatchMode    = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            tcScaler.matchWidthOrHeight = 0.5f;

            _tooltip = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(ContentSizeFitter));
            _tooltip.transform.SetParent(_tooltipCanvas.transform, false);
            var ttRect = _tooltip.GetComponent<RectTransform>();
            ttRect.pivot     = new Vector2(0, 1);
            ttRect.sizeDelta = new Vector2(290, 0);
            _tooltip.GetComponent<Image>().color = new Color(0.04f, 0.02f, 0.01f, 0.95f);
            var fitter = _tooltip.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            var ttText = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            ttText.transform.SetParent(_tooltip.transform, false);
            _tooltipText = ttText.GetComponent<TextMeshProUGUI>();
            _tooltipText.fontSize         = 13;
            _tooltipText.color            = Color.white;
            _tooltipText.enableWordWrapping = true;
            _tooltipText.margin           = new Vector4(8, 8, 8, 8);
            var ttTRect = ttText.GetComponent<RectTransform>();
            ttTRect.anchorMin = Vector2.zero;
            ttTRect.anchorMax = Vector2.one;
            ttTRect.sizeDelta = Vector2.zero;

            _tooltip.SetActive(false);
        }

        // ── Input / Update ──────────────────────────────────────────────────────

        void Update()
        {
            if (_window == null || !_window.activeSelf) return;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            // Tooltip follows cursor
            if (_tooltip != null && _tooltip.activeSelf)
            {
                Vector2 mp = mouse.position.ReadValue();
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _tooltipCanvas.GetComponent<RectTransform>(), mp, null, out Vector2 lp);
                _tooltip.GetComponent<RectTransform>().anchoredPosition = lp + new Vector2(14, -14);
            }

            // Pan tracking
            bool panned = false;
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _lastMousePos = mouse.position.ReadValue();
                _dragDistance = 0f;
            }
            if (mouse.leftButton.isPressed)
            {
                Vector2 cur   = mouse.position.ReadValue();
                Vector2 delta = cur - _lastMousePos;
                _dragDistance += delta.magnitude;
                if (_dragDistance > DRAG_THRESHOLD)
                {
                    _contentRoot.anchoredPosition += delta;
                    panned = true;
                }
                _lastMousePos = cur;
            }

            // Zoom
            float scroll = mouse.scroll.ReadValue().y;
            bool zoomed = false;
            if (scroll != 0)
            {
                _zoomLevel = Mathf.Clamp(_zoomLevel + (scroll > 0 ? 1 : -1) * 0.1f, MIN_ZOOM, MAX_ZOOM);
                _contentRoot.localScale = new Vector3(_zoomLevel, _zoomLevel, 1f);
                zoomed = true;
            }

            // Viewport Culling update only on movement
            if (panned || zoomed || _contentRoot.anchoredPosition != _lastAnchoredPos || !Mathf.Approximately(_zoomLevel, _lastZoomLevel))
            {
                UpdateCulling();
                _lastAnchoredPos = _contentRoot.anchoredPosition;
                _lastZoomLevel = _zoomLevel;
            }

            // Keyboard shortcut [F] to frame/center web
            var kb = Keyboard.current;
            if (kb != null && kb.fKey.wasPressedThisFrame)
            {
                FrameWeb();
            }
        }

        void FrameWeb()
        {
            // Fit the whole constellation in the viewport rather than snapping back to 1:1 —
            // a grown web is several thousand units across and 1:1 only shows the core.
            Canvas.ForceUpdateCanvases();

            float vw = _viewportRect != null ? _viewportRect.rect.width  : 0f;
            float vh = _viewportRect != null ? _viewportRect.rect.height : 0f;

            if (_data == null || _data.nodes.Count == 0 || vw < 10f || vh < 10f)
            {
                _contentRoot.anchoredPosition = Vector2.zero;
                _zoomLevel = 1.0f;
                _contentRoot.localScale = Vector3.one;
                UpdateCulling();
                SetStatus("Web Centered");
                return; // not a real fit — leave _framedData unset so this gets retried next time
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var node in _data.nodes)
            {
                float half = WebLayout.NodeDiameter(node) * 0.5f + 30f; // + label band
                minX = Mathf.Min(minX, node.x - half); maxX = Mathf.Max(maxX, node.x + half);
                minY = Mathf.Min(minY, node.y - half); maxY = Mathf.Max(maxY, node.y + half);
            }

            float w = Mathf.Max(1f, maxX - minX);
            float h = Mathf.Max(1f, maxY - minY);

            _zoomLevel = Mathf.Clamp(Mathf.Min(vw / w, vh / h), MIN_FIT_ZOOM, 1f);
            _contentRoot.localScale = new Vector3(_zoomLevel, _zoomLevel, 1f);

            var center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            _contentRoot.anchoredPosition = -center * _zoomLevel;

            UpdateCulling();
            _lastAnchoredPos = _contentRoot.anchoredPosition;
            _lastZoomLevel   = _zoomLevel;
            _framedData = _data;
            SetStatus("Web Framed");
        }

        // ── Spatial Culling Logic ────────────────────────────────────────────────

        void UpdateCulling()
        {
            if (_viewportRect == null || _nodeWidgets.Count == 0) return;

            float w = _viewportRect.rect.width;
            float h = _viewportRect.rect.height;

            // Viewport bounds relative to center with a safety margin buffer
            float left = -w / 2f - 120f;
            float right = w / 2f + 120f;
            float bottom = -h / 2f - 120f;
            float top = h / 2f + 120f;

            foreach (var kvp in _nodeWidgets)
            {
                var node = _data.GetNode(kvp.Key);
                if (node == null) continue;

                Vector2 localPos = _contentRoot.anchoredPosition + new Vector2(node.x, node.y) * _zoomLevel;
                bool isVisible = localPos.x >= left && localPos.x <= right && localPos.y >= bottom && localPos.y <= top;
                kvp.Value.SetActive(isVisible);
            }
        }

        // ── Node interaction ────────────────────────────────────────────────────

        void OnNodeClicked(WebNode node)
        {
            if (_dragDistance > DRAG_THRESHOLD) return; // was panning
            if (node.name == "Unformed Star")
            {
                SetStatus("Ignite adjacent stars to reveal this constellation path.");
                return;
            }
            _selectedNode = (_selectedNode == node) ? null : node;
            HideTooltip();
            RefreshActionPanel();
            Refresh();
        }

        void RefreshActionPanel()
        {
            if (_selectedNode == null)
            {
                _actionPanel.SetActive(false);
                return;
            }
            _actionPanel.SetActive(true);
            var node = _selectedNode;
            var sector = _data.GetSector(node.sectorId);

            // Title line
            string typeTag = "";
            if (node.type == WebNodeType.Keystone) typeTag = "<color=#FFD700>[⬡ Keystone] </color>";
            else if (node.type == WebNodeType.Notable) typeTag = "<color=#AAD4FF>[◆ Notable] </color>";
            else if (node.type == WebNodeType.Anchor) typeTag = "<color=#FFAA44>[✦ Anchor Star] </color>";
            else if (node.type == WebNodeType.Confluence) typeTag = "<color=#C44AE8>[◈ Confluence] </color>";

            string stateTag = node.unlocked ? " [Active]" : " [Locked]";
            if (node.type == WebNodeType.Anchor && !node.unlocked) stateTag = " [Dormant]";

            string tierTag = (node.type == WebNodeType.Basic || node.type == WebNodeType.Notable) && node.unlocked ? $" (Tier {node.tier})" : "";
            string sectorTag = sector != null ? "\n<size=11><color=" + sector.colorHex + ">" + sector.name + "</color></size>" : "";
            
            _actionTitle.text = typeTag + node.name + stateTag + tierTag + sectorTag;

            // Description + stats + traits
            string descText = node.description;
            if (node.type == WebNodeType.Keystone && !string.IsNullOrEmpty(node.keystoneRule))
            {
                descText += $"\n\n<color=#FFD700>Rule: {node.keystoneRule}</color>";
            }

            string stats = "";
            float mult = node.unlocked ? (node.type == WebNodeType.Anchor ? 1f : (1f + (node.tier - 1) * 0.5f)) : 1f;
            foreach (var kvp in node.stats)
            {
                string sign = kvp.Value >= 0 ? "+" : "";
                stats += $"\n<color=#88FF88>{sign}{(kvp.Value * mult):F0} {kvp.Key}</color>";
            }
            
            string traits = "";
            foreach (var t in node.traits)
            {
                traits += $"\n<color=#FFAA44>✧ {t}</color>";
            }

            _actionDesc.text = descText + stats + traits;

            // Provenance
            _actionProvenance.text = !string.IsNullOrEmpty(node.originHook) ? $"Origin: {node.originHook}" : "";

            // Unlock button
            bool isAnchor = node.type == WebNodeType.Anchor;
            bool canUnlock  = WebGraph.CanUnlock(node, _data);
            bool canAfford  = _data.resonance >= WebGraph.GetUnlockCost(node);
            
            _unlockBtn.gameObject.SetActive(!node.unlocked && !isAnchor);
            if (!node.unlocked && !isAnchor)
            {
                _unlockBtnLabel.text      = canUnlock ? $"Unlock ({WebGraph.GetUnlockCost(node)} Resonance)" : "Path Blocked";
                _unlockBtn.interactable   = canUnlock && canAfford;
                _unlockBtn.GetComponent<Image>().color = ButtonTint(canUnlock && canAfford, new Color(0.1f, 0.4f, 0.1f));
            }

            // Upgrade button
            bool canUpgrade = WebGraph.CanUpgrade(node, SkillWebPlugin.Instance.SkillConfig);
            bool canAffordUpgrade = _data.resonance >= WebGraph.GetUpgradeCost(node);
            
            _upgradeBtn.gameObject.SetActive(node.unlocked && !isAnchor && node.type != WebNodeType.Keystone && node.tier < 3);
            if (node.unlocked && !isAnchor && node.type != WebNodeType.Keystone && node.tier < 3)
            {
                _upgradeBtnLabel.text    = $"Upgrade T{node.tier}→{node.tier + 1} ({WebGraph.GetUpgradeCost(node)} Resonance)";
                _upgradeBtn.interactable  = canAffordUpgrade;
                _upgradeBtn.GetComponent<Image>().color = ButtonTint(canAffordUpgrade, new Color(0.1f, 0.15f, 0.5f));
            }

            // Refund button
            bool canRefund = SkillWebPlugin.Instance.CanRefund(node);
            _refundBtn.gameObject.SetActive(node.unlocked && !isAnchor);
            if (node.unlocked && !isAnchor)
            {
                int refundCost = WebGraph.GetUnlockCost(node);
                if (node.type == WebNodeType.Keystone) refundCost /= 2;
                if (node.tier > 1)
                {
                    for (int t = 1; t < node.tier; t++) refundCost += t;
                }
                
                _refundBtnLabel.text = canRefund ? $"Refund (+{refundCost} Resonance)" : "Cannot Refund (Not Leaf)";
                _refundBtn.interactable = canRefund;
                _refundBtn.GetComponent<Image>().color = ButtonTint(canRefund, new Color(0.5f, 0.1f, 0.1f));
            }
        }

        void TryUnlockSelected()
        {
            if (_selectedNode == null) return;
            string id = _selectedNode.id;
            string name = _selectedNode.name;
            if (SkillWebPlugin.Instance.TryBuyNode(_selectedNode))
            {
                SetStatus($"✦ {name} Unlocked!");
                Refresh();
                RefreshActionPanel();
                TriggerUnlockFX(id);
            }
            else
            {
                SetStatus("Unlock failed.");
            }
        }

        void TryUpgradeSelected()
        {
            if (_selectedNode == null) return;
            if (SkillWebPlugin.Instance.TryUpgradeNode(_selectedNode))
            {
                SetStatus($"✦ {_selectedNode.name} upgraded to Tier {_selectedNode.tier}!");
                Refresh();
                RefreshActionPanel();
            }
            else
            {
                SetStatus("Upgrade failed.");
            }
        }

        void TryRefundSelected()
        {
            if (_selectedNode == null) return;
            if (SkillWebPlugin.Instance.TryRefundNode(_selectedNode))
            {
                SetStatus($"✦ refunded {_selectedNode.name}.");
                Refresh();
                RefreshActionPanel();
            }
            else
            {
                SetStatus("Refund failed.");
            }
        }

        // ── Graphics Effects (Unlock Animation Coroutine) ─────────────────────────

        public void TriggerUnlockFX(string nodeId)
        {
            StartCoroutine(UnlockFXCoroutine(nodeId));
        }

        private System.Collections.IEnumerator UnlockFXCoroutine(string nodeId)
        {
            if (_nodeWidgets.TryGetValue(nodeId, out var widget))
            {
                var frame = widget.transform.Find("Frame")?.GetComponent<Image>();
                var originalColor = frame != null ? frame.color : Color.white;
                Vector3 originalScale = Vector3.one;

                // Create a temporary bright overlay flash
                var flashObj = new GameObject("FlashGlow", typeof(RectTransform), typeof(Image));
                flashObj.transform.SetParent(widget.transform, false);
                var fRect = flashObj.GetComponent<RectTransform>();
                fRect.anchorMin = Vector2.zero; fRect.anchorMax = Vector2.one;
                fRect.offsetMin = Vector2.zero; fRect.offsetMax = Vector2.zero;
                var flashImg = flashObj.GetComponent<Image>();
                flashImg.sprite = frame != null ? frame.sprite : null;
                flashImg.color = new Color(1f, 1f, 1f, 0.8f);

                float duration = 0.4f;
                float elapsed = 0f;
                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    float t = elapsed / duration;

                    // A Refresh() mid-animation (e.g. an icon gen completing) destroys and rebuilds
                    // every node widget, leaving these references pointing at destroyed GameObjects.
                    // Touching them then throws NullReferenceException — bail out cleanly instead.
                    if (widget == null || flashImg == null) yield break;

                    // Scale node outward and fade the flash glow overlay
                    float scaleVal = 1f + Mathf.Sin(t * Mathf.PI) * 0.25f;
                    widget.transform.localScale = originalScale * scaleVal;
                    flashImg.color = new Color(1f, 1f, 1f, 1f - t);

                    if (frame != null)
                    {
                        frame.color = Color.Lerp(originalColor, Color.white, Mathf.Sin(t * Mathf.PI) * 0.5f);
                    }
                    yield return null;
                }

                if (widget != null) widget.transform.localScale = originalScale;
                if (frame != null) frame.color = originalColor;
                if (flashObj != null) Destroy(flashObj);
            }
        }

        // ── Refresh / render ────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_data == null) return;

            // Header HUD
            if (_resonanceText != null) _resonanceText.text = $"Resonance: {_data.resonance} ⟡";
            if (_levelText  != null && _manager?.playerCharacter != null)
                _levelText.text = $"Level {_manager.playerCharacter.playerLevel}  |  Stars Lit: {_data.nodes.Count(n => n.unlocked && n.ring > 0)}";

            RefreshStatPanel();

            // Clear containers
            foreach (Transform c in _nodeContainer) Destroy(c.gameObject);
            foreach (Transform c in _lineContainer) Destroy(c.gameObject);
            _nodeWidgets.Clear();

            // Draw connections
            foreach (var node in _data.nodes)
            {
                var sector  = _data.GetSector(node.sectorId);
                Color col = sector != null ? GetColor(sector.colorHex) : Color.white;
                
                foreach (var tid in node.edges)
                {
                    var target = _data.nodes.Find(n => n.id == tid);
                    if (target == null) continue;
                    if (string.Compare(node.id, target.id, StringComparison.Ordinal) >= 0) continue;

                    // Unformed stars connections are drawn extremely faint
                    bool targetIsUnformed = node.name == "Unformed Star" || target.name == "Unformed Star";
                    
                    bool bright = (node.unlocked || node.ring == 0) && (target.unlocked || target.ring == 0);
                    
                    // Check if edge is in preview path
                    bool isPreview = _previewPath != null &&
                        ((_previewPath.Contains(node) && _previewPath.Contains(target)) ||
                         (_previewPath.Contains(node) && (target.unlocked || target.ring == 0)) ||
                         (_previewPath.Contains(target) && (node.unlocked || node.ring == 0)));

                    if (isPreview)
                    {
                        DrawLine(node.Position, target.Position, new Color(0.2f, 0.9f, 1f), 1f, 6f);
                    }
                    else if (targetIsUnformed)
                    {
                        DrawLine(node.Position, target.Position, col, 0.05f, 1.5f);
                    }
                    else
                    {
                        DrawLine(node.Position, target.Position, col, bright ? 0.75f : 0.20f, bright ? 4f : 2f);
                    }
                }
            }

            // Draw nodes
            foreach (var node in _data.nodes)
            {
                DrawNode(node);
            }

            // Run culling pass to apply visibility deactivations
            UpdateCulling();
        }

        void DrawNode(WebNode node)
        {
            // Search filters
            bool searchMatches = true;
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                string q = _searchQuery.ToLowerInvariant();
                bool nameMatch = node.name != null && node.name.ToLowerInvariant().Contains(q);
                bool descMatch = node.description != null && node.description.ToLowerInvariant().Contains(q);
                bool statMatch = node.stats.Keys.Any(k => k.ToLowerInvariant().Contains(q));
                bool traitMatch = node.traits.Any(t => t.ToLowerInvariant().Contains(q));
                searchMatches = nameMatch || descMatch || statMatch || traitMatch;
            }

            var obj = new GameObject("Node_" + node.id, typeof(RectTransform), typeof(Button));
            obj.transform.SetParent(_nodeContainer, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchoredPosition = node.Position;

            // Must stay in sync with WebLayout.NodeDiameter — the packer reserves arc from these.
            float nodeSize = WebLayout.NodeDiameter(node);

            float halfSize = nodeSize * 0.5f;
            rect.sizeDelta = new Vector2(nodeSize, nodeSize);

            // Register widget mapping
            _nodeWidgets[node.id] = obj;

            var sector   = _data.GetSector(node.sectorId);
            Color col    = sector != null ? GetColor(sector.colorHex) : Color.white;
            bool canUnlk = WebGraph.CanUnlock(node, _data);
            bool isSel   = node == _selectedNode;

            // Image Icon
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconObj.transform.SetParent(obj.transform, false);
            iconObj.GetComponent<RectTransform>().sizeDelta = new Vector2(nodeSize * 0.55f, nodeSize * 0.55f);
            var iconImg = iconObj.GetComponent<Image>();

            bool usedBasicGlyph = false;

            Sprite generatedIcon = GetOrLoadNodeIcon(node);
            Sprite medallion = null;
            if (generatedIcon == null && node.name != "Unformed Star")
                medallion = SkillIconAtlas.PickForNode(node); // null if sheet missing or disabled

            if (generatedIcon != null)
            {
                iconImg.sprite = generatedIcon;
                iconImg.color = Color.white;
            }
            else if (medallion != null)
            {
                // Stock medallion from the hand-made sprite sheet: instant, deterministic art
                // for every node. The coin carries its own ring border, so it fills most of the
                // node; the state-colored frame ring still draws on top of its rim.
                iconImg.sprite         = medallion;
                iconImg.preserveAspect = true;
                iconImg.color          = (node.unlocked || node.ring == 0) ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                iconObj.GetComponent<RectTransform>().sizeDelta = new Vector2(nodeSize * 0.82f, nodeSize * 0.82f);
                // A bespoke AI icon still replaces the stock coin once generated.
                if (node.unlocked && node.type != WebNodeType.Basic) NodeIconGen.EnsureIconAsync(node);
            }
            else if (node.type == WebNodeType.Basic && node.name != "Unformed Star")
            {
                // Basic nodes use a shared attribute glyph keyed to their dominant stat rather
                // than a bespoke AI image — there are too many to justify a gen call each, and
                // at this size a unique image reads as mud. Locked nodes preview it dimmed.
                Sprite glyph = GetOrLoadBasicGlyph(node);
                if (glyph != null)
                {
                    iconImg.sprite         = glyph;
                    iconImg.preserveAspect = true; // art is 1.83:1 — don't stretch it into the square slot
                    iconImg.color          = node.unlocked ? Color.white : new Color(1f, 1f, 1f, 0.4f);
                    // The glyph carries its own ornate frame, so it replaces the node ring entirely
                    // and fills the node (oversized to absorb the art's transparent side-padding).
                    iconObj.GetComponent<RectTransform>().sizeDelta = new Vector2(nodeSize * 1.5f, nodeSize * 1.5f);
                    usedBasicGlyph = true;
                }
                else
                {
                    iconImg.color = new Color(0.12f, 0.08f, 0.04f);
                }
            }
            else if (node.name == "Unformed Star")
            {
                iconImg.color = new Color(0, 0, 0, 0); // no placeholder square — the faint frame dot represents it
            }
            else
            {
                iconImg.color = new Color(0.12f, 0.08f, 0.04f); // placeholder center fill until an icon exists
                if (node.unlocked) NodeIconGen.EnsureIconAsync(node); // retry (e.g. older save, or a prior gen attempt failed)
            }

            // Frame Ring
            var frameObj = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            frameObj.transform.SetParent(obj.transform, false);
            var frameRect = frameObj.GetComponent<RectTransform>();
            FullStretch(frameRect);
            var frameImg = frameObj.GetComponent<Image>();
            
            Sprite frameSprite = _spriteBasic;
            if (node.type == WebNodeType.Keystone) frameSprite = _spriteKeystone;
            else if (node.type == WebNodeType.Notable) frameSprite = _spriteNotable;
            else if (node.type == WebNodeType.Anchor) frameSprite = _spriteAnchor;

            if (frameSprite != null)
            {
                frameImg.sprite         = frameSprite;
                frameImg.type           = Image.Type.Simple;
                frameImg.preserveAspect = true;
            }

            // Set colors based on state
            float alpha = searchMatches ? 1.0f : 0.20f;

            if (node.name == "Unformed Star")
            {
                // Unformed frontier node: faint placeholder dot
                frameImg.color = new Color(col.r * 0.3f, col.g * 0.3f, col.b * 0.3f, 0.15f * alpha);
                if (frameImg.GetComponent<Image>() != null)
                {
                    frameImg.color = new Color(0.5f, 0.5f, 0.5f, 0.12f * alpha);
                }
            }
            else if (isSel)
            {
                frameImg.color = new Color(1f, 0.9f, 0f, alpha); // selected
            }
            else if (_previewPath != null && _previewPath.Contains(node))
            {
                frameImg.color = new Color(0.2f, 0.9f, 1f, alpha); // path preview
            }
            else if (node.unlocked || node.ring == 0)
            {
                if (node.type == WebNodeType.Anchor)
                {
                    frameImg.color = new Color(1f, 0.85f, 0.2f, alpha); // active gold anchor
                }
                else
                {
                    // Scale node brightness by mastery tier
                    float tierMult = 0.6f + (node.tier * 0.15f);
                    frameImg.color = new Color(col.r * tierMult, col.g * tierMult, col.b * tierMult, alpha);
                }
            }
            else if (canUnlk)
            {
                // Reachable but locked: dim version of sector color
                frameImg.color = new Color(col.r * 0.4f, col.g * 0.4f, col.b * 0.4f, alpha);
            }
            else
            {
                // Locked and unreachable
                frameImg.color = new Color(0.25f, 0.25f, 0.25f, alpha);
            }

            // Glyph nodes carry their own frame in the art, so hide the redundant ring — but keep it
            // for the selected node and path-preview so those highlights still read.
            if (usedBasicGlyph && !isSel && !(_previewPath != null && _previewPath.Contains(node)))
            {
                frameImg.enabled = false;
            }

            // Mastery Tier Pips
            if (node.unlocked && node.tier > 1 && node.name != "Unformed Star")
            {
                for (int t = 0; t < node.tier; t++)
                {
                    var pip = new GameObject("Pip" + t, typeof(RectTransform), typeof(Image));
                    pip.transform.SetParent(obj.transform, false);
                    var pr = pip.GetComponent<RectTransform>();
                    pr.anchoredPosition = new Vector2((t - (node.tier - 1) * 0.5f) * 12f, -(halfSize + 6f));
                    pr.sizeDelta        = new Vector2(8, 8);
                    pip.GetComponent<Image>().color = new Color(col.r, col.g, col.b, alpha);
                }
            }

            // Label (With LOD checks)
            var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            lbl.transform.SetParent(obj.transform, false);
            lbl.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -(halfSize + 14f));
            // Stay inside the arc the packer reserved for this node (diameter + WebLayout.NodePad),
            // otherwise long names bleed over the neighbouring star.
            lbl.GetComponent<RectTransform>().sizeDelta        = new Vector2(nodeSize + WebLayout.NodePad * 0.75f, 26);
            var lText = lbl.GetComponent<TextMeshProUGUI>();
            lText.alignment           = TextAlignmentOptions.Center;
            lText.enableWordWrapping  = false;
            lText.overflowMode        = TextOverflowModes.Ellipsis;
            lText.color     = (node.unlocked || node.ring == 0) ? new Color(1, 1, 1, alpha) : new Color(0.6f, 0.6f, 0.6f, alpha);

            // LOD: Hide basic node labels when zoomed out below 40%
            bool showLabel = (_zoomLevel >= 0.4f) || (node.type != WebNodeType.Basic && node.type != WebNodeType.Anchor);
            if (showLabel && node.name != "Unformed Star")
            {
                lText.text = node.name;
                lText.fontSize = node.type == WebNodeType.Keystone ? 12 : 10;
            }
            else
            {
                lText.text = "";
            }

            // Button callbacks
            var btn = obj.GetComponent<Button>();
            var cap = node;
            btn.onClick.AddListener(() => OnNodeClicked(cap));
            btn.targetGraphic = frameImg;

            // Hover triggers (tooltip disabled at low LOD)
            var et = obj.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, _ => {
                if (_zoomLevel >= 0.4f) ShowTooltip(cap);
            });
            AddTrigger(et, EventTriggerType.PointerExit,  _ => HideTooltip());
        }

        void DrawLine(Vector2 start, Vector2 end, Color color, float alpha = 0.4f, float thickness = 3f)
        {
            var obj = new GameObject("Line", typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(_lineContainer, false);
            var rt  = obj.GetComponent<RectTransform>();
            var dir = (end - start).normalized;
            rt.sizeDelta        = new Vector2(Vector2.Distance(start, end), thickness);
            rt.anchoredPosition = (start + end) * 0.5f;
            rt.rotation         = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            color.a             = alpha;
            obj.GetComponent<Image>().color = color;
        }

        // ── Tooltips ────────────────────────────────────────────────────────────

        public void ShowTooltip(WebNode node)
        {
            if (_tooltip == null) return;

            if (node.name == "Unformed Star")
            {
                _tooltipText.text = "<b>Unformed Star</b>\n<color=#888888>A faint star at the outer edges of the galaxy. Ignite adjacent nodes to reveal its potential.</color>";
                _tooltip.SetActive(true);
                return;
            }

            var sector = _data.GetSector(node.sectorId);
            string sectorTag = sector != null ? "<size=11><color=" + sector.colorHex + ">" + sector.name + "</color></size>\n" : "";
            
            string state = "";
            if (node.unlocked || node.ring == 0)
            {
                state = node.type == WebNodeType.Anchor ? "<color=#AAFFAA>Active Anchor Star</color>" : $"<color=#AAFFAA>Unlocked (Tier {node.tier})</color>";
            }
            else
            {
                state = WebGraph.CanUnlock(node, _data) ? "<color=#FFDD88>Reachable</color>" : "<color=#888888>Locked</color>";
            }

            string descText = node.description;
            if (node.type == WebNodeType.Keystone && !string.IsNullOrEmpty(node.keystoneRule))
            {
                descText += $"\n\n<color=#FFD700>Keystone Rule: {node.keystoneRule}</color>";
            }

            string stats = "";
            float m = node.unlocked ? (node.type == WebNodeType.Anchor ? 1f : (1f + (node.tier - 1) * 0.5f)) : 1f;
            foreach (var kvp in node.stats)
            {
                string sign = kvp.Value >= 0 ? "+" : "";
                stats += $"\n<color=#88FF88>{sign}{(kvp.Value * m):F0} {kvp.Key}</color>";
            }

            string traits = "";
            foreach (var t in node.traits)
            {
                traits += $"\n<color=#FFAA44>✧ {t}</color>";
            }

            string costText = "";
            if (!node.unlocked && node.type != WebNodeType.Anchor)
            {
                costText = $"\n\n<color=#4AE8C8>Unlock Cost: {WebGraph.GetUnlockCost(node)} Resonance</color>";
            }

            // Path preview calculation
            if (!node.unlocked && node.type != WebNodeType.Anchor)
            {
                var (path, pathCost) = WebGraph.FindCheapestPath(node, _data);
                if (path != null && path.Count > 1)
                {
                    costText += $"\n<color=yellow>Cheapest Path Cost: {pathCost} Resonance ({path.Count} steps)</color>";
                    _previewPath = path;
                    _previewCost = pathCost;
                    Refresh(); // redraw to show path preview lines
                }
            }

            _tooltipText.text = sectorTag + "<b>" + node.name + "</b>  " + state + "\n" + descText + stats + traits + costText;
            _tooltip.SetActive(true);
        }

        public void ShowLedgerTooltip()
        {
            if (_tooltip == null || _data == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("<b>Resonance Ledger</b>");
            sb.AppendLine("<size=11>Your character's earned Resonance logs:</size>\n");

            if (_data.economyLedger.Count == 0)
            {
                sb.AppendLine("<color=#888888>No logs recorded yet.</color>");
            }
            else
            {
                foreach (var kvp in _data.economyLedger)
                {
                    // Clean up source formatting for display
                    string source = kvp.Key;
                    if (source.StartsWith("level:")) source = "Reached Level " + source.Replace("level:", "");
                    else if (source.StartsWith("turns:")) source = "Survived turns: " + source.Replace("turns:", "");
                    else if (source.StartsWith("place:")) source = "Exploration milestone";
                    else if (source.StartsWith("anchor_import:")) source = "Ignited Anchor Star";
                    
                    sb.AppendLine($"<color=#79E84A>+{kvp.Value} ⟡</color> {source}");
                }
            }

            _tooltipText.text = sb.ToString();
            _tooltip.SetActive(true);
        }

        public void HideTooltip()
        {
            if (_tooltip != null) _tooltip.SetActive(false);
            if (_previewPath != null)
            {
                _previewPath = null;
                _previewCost = 0;
                Refresh(); // reset line colors
            }
        }

        // ── Asset loading ────────────────────────────────────────────────────────

        static string AssetsPath => Path.Combine(Application.streamingAssetsPath, "SkillWeb");

        void LoadBackground(RawImage target)
        {
            string path = Path.Combine(AssetsPath, "SkillWeb_bkg.png");
            if (!File.Exists(path))
            {
                Debug.LogError($"[SkillWeb] Background not found at {path}");
                return;
            }
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            target.texture = tex;
        }

        void LoadNodeFrames()
        {
            _spriteBasic    = LoadSprite("SkillRingBasic.png");
            _spriteNotable  = LoadSprite("PassiveSkillRingNotable.png");
            _spriteKeystone = LoadSprite("SkillRingKeystone.png");
            _spriteAnchor   = LoadSprite("PassiveSkillRing.png");
        }

        Sprite LoadSprite(string filename)
        {
            string path = Path.Combine(AssetsPath, filename);
            if (!File.Exists(path))
            {
                Debug.LogError($"[SkillWeb] Sprite not found at {path}");
                return null;
            }
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Returns the generated icon for this node if one exists on disk, caching it in memory. Null if none has been generated (yet).</summary>
        Sprite GetOrLoadNodeIcon(WebNode node)
        {
            if (_iconSpriteCache.TryGetValue(node.id, out Sprite cached)) return cached;

            string path = NodeIconGen.GetIconPath(node);
            if (!File.Exists(path)) return null;

            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            KnockOutBackground(tex); // the model often invents a solid bg the prompt didn't ask for
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _iconSpriteCache[node.id] = sprite;
            return sprite;
        }

        /// <summary>
        /// Makes a generated icon's background transparent by sampling the actual colour from the
        /// image corners and keying it out (with a feathered edge). Gemini/Imagen frequently return
        /// a solid non-white background despite the prompt, which the fixed ffmpeg colorkey=white
        /// can't touch — this handles any colour, runs in-memory at load, and no-ops on images that
        /// are already transparent (e.g. the native backend's server-side removal).
        /// </summary>
        static void KnockOutBackground(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            if (w < 8 || h < 8) return;

            Color32[] px = tex.GetPixels32();
            if (!TryEstimateCornerColor(px, w, h, out Color32 bg)) return; // already transparent

            // Squared RGB-distance thresholds: fully cut within keyR, feather out to featherR.
            const float keyR = 55f, featherR = 105f;
            float keyR2 = keyR * keyR, featherR2 = featherR * featherR, span = featherR2 - keyR2;

            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a == 0) continue;
                float dr = px[i].r - bg.r, dg = px[i].g - bg.g, db = px[i].b - bg.b;
                float d2 = dr * dr + dg * dg + db * db;
                if (d2 <= keyR2)
                {
                    px[i].a = 0;
                }
                else if (d2 < featherR2)
                {
                    byte a = (byte)(px[i].a * ((d2 - keyR2) / span)); // 0 at key edge → full at feather edge
                    if (a < px[i].a) px[i].a = a;
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
        }

        /// <summary>Averages the four corner patches. Returns false if the corners are already transparent.</summary>
        static bool TryEstimateCornerColor(Color32[] px, int w, int h, out Color32 bg)
        {
            bg = default;
            int patch = Mathf.Max(2, Mathf.Min(w, h) / 20); // ~5% corner patch
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
            if (n == 0 || (a / n) < 24) return false; // no pixels, or corners already transparent
            bg = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
            return true;
        }

        // Maps a canonical SS.PlayerAttribute name to its glyph asset. Cunning and Charisma have
        // no dedicated art yet, so they (and stat-less nodes) fall through to the generic glyph.
        // Note the filename says "intelligence" while the enum value is "Intellect".
        static readonly Dictionary<string, string> AttrGlyphFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Strength"]  = "attr_strength.png",
            ["Dexterity"] = "attr_dexterity.png",
            ["Intellect"] = "attr_intelligence.png",
        };
        const string GenericGlyphFile = "attr_generic.png";

        /// <summary>Picks the glyph asset for a Basic node from its highest-magnitude stat.</summary>
        static string GlyphFileForNode(WebNode node)
        {
            string dominant = null;
            float best = 0f;
            if (node.stats != null)
            {
                foreach (var kv in node.stats)
                {
                    float mag = Math.Abs(kv.Value);
                    if (mag > best) { best = mag; dominant = kv.Key; }
                }
            }
            if (dominant != null && AttrGlyphFiles.TryGetValue(dominant, out string file)) return file;
            return GenericGlyphFile;
        }

        /// <summary>Returns the shared attribute glyph for a Basic node, cached by filename. Null if the asset is missing.</summary>
        Sprite GetOrLoadBasicGlyph(WebNode node)
        {
            string file = GlyphFileForNode(node);
            if (_glyphSpriteCache.TryGetValue(file, out Sprite cached)) return cached;
            Sprite sprite = LoadSprite(file); // null (and logs) if the asset isn't present
            _glyphSpriteCache[file] = sprite; // cache null too, so we don't re-hit disk every redraw
            return sprite;
        }

        /// <summary>Applies the rune-bordered parchment behind a side panel, darkened so light text stays legible.</summary>
        void ApplyPanelBackground(Image img)
        {
            if (_spritePanelBg == null || img == null) return;
            img.sprite = _spritePanelBg;
            img.type   = Image.Type.Simple;
            img.color  = new Color(0.42f, 0.37f, 0.30f, 0.98f);
        }

        /// <summary>Applies the ornate button plate art behind a panel button's label.</summary>
        void ApplyButtonPlate(Button btn)
        {
            if (_spriteButton == null || btn == null) return;
            var img = btn.GetComponent<Image>();
            img.sprite         = _spriteButton;
            img.type           = Image.Type.Simple;
            img.preserveAspect = true;
            img.color          = Color.white;
        }

        /// <summary>
        /// Picks a button tint. With the plate art present, enabled/disabled reads as full-color vs
        /// dimmed (the art is red-themed, so state is carried by brightness + label rather than hue).
        /// Without the art, falls back to the old solid state colors.
        /// </summary>
        Color ButtonTint(bool enabled, Color fallbackEnabled)
        {
            if (_spriteButton != null) return enabled ? Color.white : new Color(0.4f, 0.4f, 0.4f, 1f);
            return enabled ? fallbackEnabled : new Color(0.2f, 0.2f, 0.2f);
        }

        Color GetColor(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : Color.white;
        }

        async void SetStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.text = msg;
            if (string.IsNullOrEmpty(msg)) return;
            string snapshot = msg;
            await Task.Delay(4000);
            if (_statusText != null && _statusText.text == snapshot) _statusText.text = "";
        }

        // ── UI factory helpers ───────────────────────────────────────────────────

        static Image NewImg(string name, Transform parent, Color color)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = color;
            return obj.GetComponent<Image>();
        }

        static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, TextAlignmentOptions align)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            var tmp = obj.GetComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.fontSize  = size;
            tmp.alignment = align;
            return tmp;
        }

        static GameObject NewButton(string label, Transform parent, Color bgColor)
        {
            var obj = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = bgColor;
            var t = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            t.transform.SetParent(obj.transform, false);
            var tmp = t.GetComponent<TextMeshProUGUI>();
            tmp.text      = label;
            tmp.fontSize  = 13;
            tmp.alignment = TextAlignmentOptions.Center;
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.sizeDelta = Vector2.zero;
            return obj;
        }

        static Button BuildPanelButton(string label, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, Color bgColor)
        {
            var obj  = NewButton(label, parent, bgColor);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            return obj.GetComponent<Button>();
        }

        static RectTransform NewLayer(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.transform.SetParent(parent, false);
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(8000, 8000);
            rt.anchoredPosition = Vector2.zero;
            return rt;
        }

        static void FullStretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        static void AnchorText(RectTransform r,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            r.anchorMin = anchorMin; r.anchorMax = anchorMax;
            r.offsetMin = offsetMin; r.offsetMax = offsetMax;
            r.sizeDelta = Vector2.zero;
        }

        static void AddTrigger(EventTrigger et, EventTriggerType type, Action<BaseEventData> cb)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(e => cb(e));
            et.triggers.Add(entry);
        }

        static TMP_InputField BuildInputField(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.04f);
            var r = obj.GetComponent<RectTransform>();
            r.anchorMin = anchorMin; r.anchorMax = anchorMax;
            r.offsetMin = r.offsetMax = Vector2.zero; r.sizeDelta = Vector2.zero;

            var textAreaObj = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            textAreaObj.transform.SetParent(obj.transform, false);
            var taRect = textAreaObj.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero; taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(8, 2); taRect.offsetMax = new Vector2(-8, -2);

            var phObj = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
            phObj.transform.SetParent(textAreaObj.transform, false);
            var phTmp = phObj.GetComponent<TextMeshProUGUI>();
            phTmp.text = ""; phTmp.fontSize = 13;
            phTmp.color = new Color(0.5f, 0.5f, 0.5f);
            FullStretch(phObj.GetComponent<RectTransform>());

            var inputObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            inputObj.transform.SetParent(textAreaObj.transform, false);
            var inputTmp = inputObj.GetComponent<TextMeshProUGUI>();
            inputTmp.fontSize = 13; inputTmp.color = Color.white;
            FullStretch(inputObj.GetComponent<RectTransform>());

            var field = obj.GetComponent<TMP_InputField>();
            field.textViewport      = textAreaObj.GetComponent<RectTransform>();
            field.textComponent     = inputTmp;
            field.placeholder       = phTmp;
            field.lineType          = TMP_InputField.LineType.SingleLine;
            return field;
        }
    }
}
