using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

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
        private Sprite        _nodeFrameSprite;

        // Header / HUD
        private TextMeshProUGUI _pointsText;
        private TextMeshProUGUI _levelText;
        private TextMeshProUGUI _statusText;

        // Side action panel
        private GameObject      _actionPanel;
        private TextMeshProUGUI _actionTitle;
        private TextMeshProUGUI _actionDesc;
        private Button          _unlockBtn;
        private TextMeshProUGUI _unlockBtnLabel;
        private Button          _upgradeBtn;
        private TextMeshProUGUI _upgradeBtnLabel;

        // Inline "New Discipline" input panel
        private GameObject          _disciplinePanel;
        private TMP_InputField      _disciplineNameInput;
        private TMP_InputField      _disciplineThemeInput;

        // Tooltip
        private GameObject      _tooltip;
        private TextMeshProUGUI _tooltipText;

        // ── State ───────────────────────────────────────────────────────────────
        private SkillNode _selectedNode;
        private bool      _isGenerating;
        private float     _zoomLevel   = 1f;
        private const float MIN_ZOOM   = 0.25f;
        private const float MAX_ZOOM   = 4f;
        private Vector2   _lastMousePos;
        private float     _dragDistance;
        private const float DRAG_THRESHOLD = 8f;

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
        }

        void OnDestroy()
        {
            if (_window != null)       Destroy(_window);
            if (_tooltipCanvas != null) Destroy(_tooltipCanvas.gameObject);
        }

        // ── UI construction ─────────────────────────────────────────────────────

        void BuildUI()
        {
            // Root canvas -------------------------------------------------------
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

            // Dim overlay -------------------------------------------------------
            var overlay = NewImg("Overlay", _window.transform, new Color(0, 0, 0, 0.72f));
            FullStretch(overlay.GetComponent<RectTransform>());

            // Header bar (top 8%) -----------------------------------------------
            BuildHeader();

            // Viewport (8% – 94%) -----------------------------------------------
            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            vp.transform.SetParent(_window.transform, false);
            vp.GetComponent<Image>().color = new Color(0, 0, 0, 0.01f);
            var vpRect = vp.GetComponent<RectTransform>();
            vpRect.anchorMin = new Vector2(0f,    0.06f);
            vpRect.anchorMax = new Vector2(0.77f, 0.92f);
            vpRect.offsetMin = vpRect.offsetMax = Vector2.zero;

            // Content (panning/zooming root) ------------------------------------
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

            LoadNodeFrame();

            // Bottom bar (0% – 6%) ----------------------------------------------
            BuildBottomBar();

            // Inline new-discipline input panel (hidden by default) -------------
            BuildDisciplineInputPanel();

            // Right side panel (77% – 100%) -------------------------------------
            BuildActionPanel();

            // Tooltip canvas (always on top) ------------------------------------
            BuildTooltipCanvas();
        }

        void BuildHeader()
        {
            var hdr = NewImg("Header", _window.transform, new Color(0.07f, 0.04f, 0.01f, 0.97f));
            var r   = hdr.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 0.92f);
            r.anchorMax = new Vector2(1, 1f);
            r.offsetMin = r.offsetMax = Vector2.zero;

            // Title
            var title = NewText("Title", hdr.transform, "✦  SKILL WEB", 26, TextAlignmentOptions.Left);
            title.color = new Color(1f, 0.85f, 0.4f);
            AnchorText(title.rectTransform, new Vector2(0, 0), new Vector2(0.32f, 1), new Vector2(14, 0), Vector2.zero);

            // Skill points
            _pointsText = NewText("Points", hdr.transform, "Skill Points: 0", 20, TextAlignmentOptions.Center);
            _pointsText.color = new Color(0.4f, 1f, 0.9f);
            AnchorText(_pointsText.rectTransform, new Vector2(0.32f, 0), new Vector2(0.62f, 1), Vector2.zero, Vector2.zero);

            // Level / node count
            _levelText = NewText("Level", hdr.transform, "Level 1", 17, TextAlignmentOptions.Right);
            _levelText.color = new Color(0.7f, 0.7f, 1f);
            AnchorText(_levelText.rectTransform, new Vector2(0.62f, 0), new Vector2(0.85f, 1), Vector2.zero, Vector2.zero);

            // Close button
            var closeObj = NewButton("Close [X]", hdr.transform, new Color(0.5f, 0.1f, 0.1f));
            var closeRect = closeObj.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.93f, 0.1f);
            closeRect.anchorMax = new Vector2(1f,    0.9f);
            closeRect.offsetMin = closeRect.offsetMax = Vector2.zero;
            closeRect.sizeDelta = Vector2.zero;
            closeObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                _window.SetActive(false);
                if (_tooltipCanvas != null) _tooltipCanvas.gameObject.SetActive(false);
            });
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
            AnchorText(_statusText.rectTransform, new Vector2(0, 0), new Vector2(0.55f, 1), new Vector2(10, 0), Vector2.zero);

            // Extend Web button
            var extBtn = NewButton("Extend Web", bar.transform, new Color(0.08f, 0.18f, 0.08f));
            var extRect = extBtn.GetComponent<RectTransform>();
            extRect.anchorMin = new Vector2(0.55f, 0.1f);
            extRect.anchorMax = new Vector2(0.75f, 0.9f);
            extRect.offsetMin = extRect.offsetMax = Vector2.zero;
            extRect.sizeDelta = Vector2.zero;
            extBtn.GetComponent<Button>().onClick.AddListener(AddLoreNodeAction);

            // New Discipline button
            var discBtn = NewButton("New Discipline", bar.transform, new Color(0.15f, 0.07f, 0.25f));
            var discRect = discBtn.GetComponent<RectTransform>();
            discRect.anchorMin = new Vector2(0.76f, 0.1f);
            discRect.anchorMax = new Vector2(0.99f, 0.9f);
            discRect.offsetMin = discRect.offsetMax = Vector2.zero;
            discRect.sizeDelta = Vector2.zero;
            discBtn.GetComponent<Button>().onClick.AddListener(PlantNewTree);
        }

        void BuildActionPanel()
        {
            _actionPanel = NewImg("ActionPanel", _window.transform, new Color(0.06f, 0.03f, 0.01f, 0.97f)).gameObject;
            var apRect = _actionPanel.GetComponent<RectTransform>();
            apRect.anchorMin = new Vector2(0.77f, 0f);
            apRect.anchorMax = new Vector2(1f,    0.92f);
            apRect.offsetMin = apRect.offsetMax = Vector2.zero;

            // Panel title
            _actionTitle = NewText("Title", _actionPanel.transform, "Select a node", 17, TextAlignmentOptions.Center);
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
            AnchorText(_actionDesc.rectTransform, new Vector2(0, 0.30f), new Vector2(1, 0.78f), new Vector2(8, 0), new Vector2(-8, 0));

            // Unlock button
            _unlockBtn = BuildPanelButton("Unlock", _actionPanel.transform,
                new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.29f),
                new Color(0.1f, 0.4f, 0.1f));
            _unlockBtnLabel = _unlockBtn.GetComponentInChildren<TextMeshProUGUI>();
            _unlockBtn.onClick.AddListener(TryUnlockSelected);

            // Upgrade button
            _upgradeBtn = BuildPanelButton("Upgrade", _actionPanel.transform,
                new Vector2(0.05f, 0.06f), new Vector2(0.95f, 0.17f),
                new Color(0.1f, 0.15f, 0.5f));
            _upgradeBtnLabel = _upgradeBtn.GetComponentInChildren<TextMeshProUGUI>();
            _upgradeBtn.onClick.AddListener(TryUpgradeSelected);

            _actionPanel.SetActive(false);
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

            // Pan tracking — accumulate drag distance to distinguish click vs drag
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
                    _contentRoot.anchoredPosition += delta;
                _lastMousePos = cur;
            }

            // Zoom
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0)
            {
                _zoomLevel = Mathf.Clamp(_zoomLevel + (scroll > 0 ? 1 : -1) * 0.1f, MIN_ZOOM, MAX_ZOOM);
                _contentRoot.localScale = new Vector3(_zoomLevel, _zoomLevel, 1f);
            }
        }

        // ── Node interaction ────────────────────────────────────────────────────

        void OnNodeClicked(SkillNode node)
        {
            if (_dragDistance > DRAG_THRESHOLD) return; // was panning
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
            var tree = _data.GetTree(node.treeId);

            // Title line
            string tierTag = node.isUnlocked ? " [T" + node.tier + "]" : " [Locked]";
            string treeTag = tree != null ? "\n<size=11><color=" + tree.colorHex + ">" + tree.name + "</color></size>" : "";
            _actionTitle.text = node.name + tierTag + treeTag;

            // Description + stats + traits
            string stats = "";
            float mult = node.isUnlocked ? node.tier : 1f;
            foreach (var kvp in node.statModifiers)
                stats += "\n<color=#88FF88>+" + (kvp.Value * mult).ToString("F0") + " " + kvp.Key + "</color>";
            string traits = "";
            foreach (var t in node.narrativeTraits)
                traits += "\n<color=#FFAA44>✧ " + t + "</color>";
            _actionDesc.text = node.description + stats + traits;

            // Unlock button
            bool canUnlock  = _data.CanUnlock(node);
            bool canAfford  = _data.skillPoints >= _data.UnlockCost(node);
            _unlockBtn.gameObject.SetActive(!node.isUnlocked);
            if (!node.isUnlocked)
            {
                _unlockBtnLabel.text      = canUnlock ? "Unlock (" + _data.UnlockCost(node) + " pts)" : "Path Blocked";
                _unlockBtn.interactable   = canUnlock && canAfford;
                _unlockBtn.GetComponent<Image>().color =
                    (canUnlock && canAfford) ? new Color(0.1f, 0.4f, 0.1f) : new Color(0.2f, 0.2f, 0.2f);
            }

            // Upgrade button
            bool canUpgrade      = _data.CanUpgrade(node);
            bool canAffordUpgrade = _data.skillPoints >= _data.UnlockCost(node);
            _upgradeBtn.gameObject.SetActive(node.isUnlocked && node.tier < 3);
            if (node.isUnlocked && node.tier < 3)
            {
                _upgradeBtnLabel.text    = "Upgrade T" + node.tier + "→" + (node.tier + 1) + " (" + _data.UnlockCost(node) + " pts)";
                _upgradeBtn.interactable  = canAffordUpgrade;
                _upgradeBtn.GetComponent<Image>().color =
                    canAffordUpgrade ? new Color(0.1f, 0.15f, 0.5f) : new Color(0.2f, 0.2f, 0.2f);
            }
        }

        void TryUnlockSelected()
        {
            if (_selectedNode == null || _isGenerating) return;
            if (_data.TryUnlock(_selectedNode))
            {
                SkillWebPlugin.Instance.SaveData();
                SetStatus("✦ " + _selectedNode.name + " Unlocked!");
                if (SkillWebPlugin.Instance.SkillConfig.AutoGenerateFrontier)
                    StartFrontierGeneration(_selectedNode);
                Refresh();
                RefreshActionPanel();
            }
            else
            {
                SetStatus("Cannot unlock — insufficient points or path blocked.");
            }
        }

        void TryUpgradeSelected()
        {
            if (_selectedNode == null) return;
            if (_data.TryUpgrade(_selectedNode))
            {
                SkillWebPlugin.Instance.SaveData();
                SetStatus("✦ " + _selectedNode.name + " upgraded to Tier " + _selectedNode.tier + "!");
                Refresh();
                RefreshActionPanel();
            }
            else
            {
                SetStatus("Cannot upgrade — insufficient points.");
            }
        }

        async void StartFrontierGeneration(SkillNode origin)
        {
            if (_isGenerating) return;
            _isGenerating = true;
            var tree  = _data.GetTree(origin.treeId);
            int count = SkillWebPlugin.Instance.SkillConfig.FrontierNodesPerUnlock;
            SetStatus("Expanding the web...");
            var newNodes = await SkillWebGenerator.GenerateFrontierNodes(_manager, origin, _data, tree, count);
            SkillWebPlugin.Instance.SaveData();
            SetStatus(newNodes.Count > 0 ? "+" + newNodes.Count + " new paths revealed." : "");
            Refresh();
            _isGenerating = false;
        }

        // ── Refresh / render ────────────────────────────────────────────────────

        public void Refresh()
        {
            if (_data == null) return;

            // Header HUD
            if (_pointsText != null) _pointsText.text = "Skill Points: " + _data.skillPoints;
            if (_levelText  != null && _manager?.playerCharacter != null)
                _levelText.text = "Level " + _manager.playerCharacter.playerLevel +
                                  "  |  Nodes: " + _data.totalNodesUnlocked;

            // Clear graph
            foreach (Transform c in _nodeContainer) Destroy(c.gameObject);
            foreach (Transform c in _lineContainer) Destroy(c.gameObject);

            if (_data.nodes.Count == 0)
            {
                if (_statusText != null && string.IsNullOrEmpty(_statusText.text))
                    _statusText.text = "Press Extend Web to generate your starting disciplines.";
                return;
            }

            // Draw connections
            foreach (var node in _data.nodes)
            {
                var tree  = _data.GetTree(node.treeId);
                Color col = GetTreeColor(tree);
                foreach (var tid in node.connectedIds)
                {
                    var target = _data.nodes.Find(n => n.id == tid);
                    if (target == null) continue;
                    if (string.Compare(node.id, target.id, StringComparison.Ordinal) >= 0) continue;
                    bool bright = node.isUnlocked && target.isUnlocked;
                    DrawLine(node.position, target.position, col, bright ? 0.75f : 0.22f, bright ? 5f : 3f);
                }
            }

            // Draw nodes
            foreach (var node in _data.nodes) DrawNode(node);
        }

        void DrawNode(SkillNode node)
        {
            var obj = new GameObject("Node_" + node.id, typeof(RectTransform), typeof(Button));
            obj.transform.SetParent(_nodeContainer, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchoredPosition = node.position;
            rect.sizeDelta        = new Vector2(160, 90); // matches PassiveSkillRing aspect ratio

            var tree     = _data.GetTree(node.treeId);
            Color col    = GetTreeColor(tree);
            bool canUnlk = _data.CanUnlock(node);
            bool isSel   = node == _selectedNode;

            // Icon (inside ring)
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(RawImage));
            iconObj.transform.SetParent(obj.transform, false);
            iconObj.GetComponent<RectTransform>().sizeDelta = new Vector2(58, 58);
            var rawImg = iconObj.GetComponent<RawImage>();
            if (!string.IsNullOrEmpty(node.imageUuid))
                TryLoadNodeImage(node.imageUuid, rawImg);
            else
                rawImg.color = node.isUnlocked ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.1f, 0.1f, 0.1f, 0.5f);

            // Frame ring
            var frameObj = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            frameObj.transform.SetParent(obj.transform, false);
            var frameRect = frameObj.GetComponent<RectTransform>();
            frameRect.anchorMin = Vector2.zero; frameRect.anchorMax = Vector2.one;
            frameRect.offsetMin = Vector2.zero; frameRect.offsetMax = Vector2.zero;
            var frameImg = frameObj.GetComponent<Image>();
            if (_nodeFrameSprite != null)
            {
                frameImg.sprite          = _nodeFrameSprite;
                frameImg.type            = Image.Type.Simple;
                frameImg.preserveAspect  = true;
            }

            // Color by state
            if (isSel)
                frameImg.color = Color.yellow;
            else if (node.isUnlocked)
            {
                if (node.tier >= 3) frameImg.color = new Color(1f, 0.85f, 0.15f); // gold mastery
                else                frameImg.color = col * (0.65f + 0.12f * node.tier);
            }
            else if (canUnlk)
                frameImg.color = col * 0.45f; // reachable but locked
            else
                frameImg.color = new Color(0.22f, 0.22f, 0.22f); // unreachable

            // Tier pips
            if (node.isUnlocked && node.tier > 0)
            {
                for (int t = 0; t < node.tier; t++)
                {
                    var pip = new GameObject("Pip" + t, typeof(RectTransform), typeof(Image));
                    pip.transform.SetParent(obj.transform, false);
                    var pr = pip.GetComponent<RectTransform>();
                    pr.anchoredPosition = new Vector2((t - (node.tier - 1) * 0.5f) * 12f, -52f);
                    pr.sizeDelta        = new Vector2(8, 8);
                    pip.GetComponent<Image>().color =
                        node.tier >= 3 ? new Color(1f, 0.85f, 0.15f) : col;
                }
            }

            // Name label
            var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            lbl.transform.SetParent(obj.transform, false);
            lbl.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -58f);
            lbl.GetComponent<RectTransform>().sizeDelta        = new Vector2(170, 28);
            var lText = lbl.GetComponent<TextMeshProUGUI>();
            lText.text      = node.name;
            lText.fontSize  = 11;
            lText.alignment = TextAlignmentOptions.Center;
            lText.color     = node.isUnlocked ? Color.white : new Color(0.55f, 0.55f, 0.55f);

            // Button + hover events
            var btn = obj.GetComponent<Button>();
            var cap = node; // closure capture
            btn.onClick.AddListener(() => OnNodeClicked(cap));
            btn.targetGraphic = frameImg;

            var et = obj.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, _ => ShowTooltip(cap));
            AddTrigger(et, EventTriggerType.PointerExit,  _ => HideTooltip());
        }

        void DrawLine(Vector2 start, Vector2 end, Color color, float alpha = 0.4f, float thickness = 4f)
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

        // ── Tooltip ─────────────────────────────────────────────────────────────

        public void ShowTooltip(SkillNode node)
        {
            if (_tooltip == null) return;
            var tree    = _data.GetTree(node.treeId);
            string tStr = tree != null ? "<size=11><color=" + tree.colorHex + ">" + tree.name + "</color></size>\n" : "";
            string state = node.isUnlocked
                ? "<color=#AAFFAA>Unlocked (Tier " + node.tier + ")</color>"
                : (_data.CanUnlock(node) ? "<color=#FFDD88>Reachable</color>" : "<color=#888888>Locked</color>");
            string stats = "";
            float m = node.isUnlocked ? node.tier : 1f;
            foreach (var kvp in node.statModifiers)
                stats += "\n<color=#88FF88>+" + (kvp.Value * m).ToString("F0") + " " + kvp.Key + "</color>";
            string traits = "";
            foreach (var t in node.narrativeTraits)
                traits += "\n<color=#FFAA44>✧ " + t + "</color>";
            _tooltipText.text = tStr + "<b>" + node.name + "</b>  " + state + "\n" + node.description + stats + traits;
            _tooltip.SetActive(true);
        }

        public void HideTooltip()
        {
            if (_tooltip != null) _tooltip.SetActive(false);
        }

        // ── Generate actions ────────────────────────────────────────────────────

        async void AddLoreNodeAction()
        {
            if (_isGenerating) return;
            int cost = SkillWebPlugin.Instance.SkillConfig.NodeCost;
            if (_data.skillPoints < cost) { SetStatus("Need " + cost + " Skill Points!"); return; }

            if (_data.nodes.Count == 0)
            {
                await GenerateStartingCluster();
                return;
            }

            _isGenerating = true;
            SetStatus("Generating new node...");

            SkillNode parent = _selectedNode ?? FindBestParent();
            Vector2   pos    = FindFreePosition(parent, 240f, 360f);
            var       tree   = _data.GetTree(parent?.treeId);
            var       newNode = await SkillWebGenerator.GenerateLoreBasedNode(_manager, parent, pos, tree);

            if (newNode != null)
            {
                newNode.treeId = parent?.treeId;
                if (_data.TryAddNode(newNode))
                {
                    if (parent != null) _data.AddConnection(parent.id, newNode.id);
                    _data.skillPoints -= cost;
                    SkillWebPlugin.Instance.SaveData();
                    SetStatus("✦ " + newNode.name + " added.");
                }
            }
            else
            {
                SetStatus("Generation failed. Try again.");
            }

            Refresh();
            _isGenerating = false;
        }

        async Task GenerateStartingCluster()
        {
            _isGenerating = true;
            SetStatus("Weaving the Skill Web...");
            var result = await SkillWebGenerator.GenerateStartingCluster(_manager);
            if (result.nodes != null && result.nodes.Count > 0)
            {
                if (result.trees != null) _data.trees.AddRange(result.trees);
                foreach (var n in result.nodes) _data.TryAddNode(n);
                SkillWebPlugin.Instance.SaveData();
                SetStatus("The Skill Web awakens! Unlock a starting node to begin your journey.");
                Refresh();
            }
            else
            {
                SetStatus("Generation failed. Try again.");
            }
            _isGenerating = false;
        }

        void PlantNewTree()
        {
            if (_isGenerating) return;
            _disciplineNameInput.text  = "";
            _disciplineThemeInput.text = "";
            _disciplinePanel.SetActive(true);
        }

        async Task StartNewDiscipline(string name, string purpose)
        {
            _isGenerating = true;
            SetStatus("Rooting " + name + "...");

            string[] colors = { "#E8734A", "#4A9BE8", "#7AE84A", "#E8D44A", "#C44AE8", "#4AE8C8", "#E84A7A", "#E8A84A" };
            string   color  = colors[UnityEngine.Random.Range(0, colors.Length)];
            var      tree   = new SkillTree(Guid.NewGuid().ToString(), name, purpose) { colorHex = color };
            _data.trees.Add(tree);

            Vector2 pos     = FindFreeSpotFarAway();
            var     newNode = await SkillWebGenerator.GenerateLoreBasedNode(_manager, null, pos, tree);

            if (newNode != null)
            {
                _data.TryAddNode(newNode);
                SkillWebPlugin.Instance.SaveData();
                SetStatus("✦ " + name + " established!");
            }
            else
            {
                SetStatus("Establishment failed.");
            }

            Refresh();
            _isGenerating = false;
        }

        // ── Discipline input panel ───────────────────────────────────────────────

        void BuildDisciplineInputPanel()
        {
            // Centered modal overlay parented to the main window canvas
            _disciplinePanel = new GameObject("DisciplinePanel", typeof(RectTransform), typeof(Image));
            _disciplinePanel.transform.SetParent(_window.transform, false);
            _disciplinePanel.GetComponent<Image>().color = new Color(0.06f, 0.03f, 0.01f, 0.97f);

            var dr = _disciplinePanel.GetComponent<RectTransform>();
            dr.anchorMin = new Vector2(0.3f, 0.35f);
            dr.anchorMax = new Vector2(0.7f, 0.70f);
            dr.offsetMin = dr.offsetMax = Vector2.zero;

            // Title
            var titleTmp = NewText("Title", _disciplinePanel.transform,
                "Establish New Discipline", 20, TextAlignmentOptions.Center);
            titleTmp.color = new Color(1f, 0.85f, 0.4f);
            AnchorText(titleTmp.rectTransform,
                new Vector2(0, 0.78f), new Vector2(1, 1f),
                new Vector2(8, 0), new Vector2(-8, 0));

            // Name label
            var nameLabel = NewText("NameLabel", _disciplinePanel.transform,
                "Name:", 15, TextAlignmentOptions.Left);
            nameLabel.color = Color.white;
            AnchorText(nameLabel.rectTransform,
                new Vector2(0.04f, 0.55f), new Vector2(0.25f, 0.72f),
                Vector2.zero, Vector2.zero);

            // Name input field
            _disciplineNameInput = BuildInputField("NameInput", _disciplinePanel.transform,
                new Vector2(0.26f, 0.55f), new Vector2(0.96f, 0.72f));

            // Theme label
            var themeLabel = NewText("ThemeLabel", _disciplinePanel.transform,
                "Theme:", 15, TextAlignmentOptions.Left);
            themeLabel.color = Color.white;
            AnchorText(themeLabel.rectTransform,
                new Vector2(0.04f, 0.24f), new Vector2(0.25f, 0.52f),
                Vector2.zero, Vector2.zero);

            // Theme input field (multiline)
            _disciplineThemeInput = BuildInputField("ThemeInput", _disciplinePanel.transform,
                new Vector2(0.26f, 0.24f), new Vector2(0.96f, 0.52f), multiline: true);

            // Cancel button
            var cancelObj = NewButton("Cancel", _disciplinePanel.transform, new Color(0.35f, 0.1f, 0.1f));
            var cr = cancelObj.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(0.05f, 0.04f); cr.anchorMax = new Vector2(0.45f, 0.20f);
            cr.offsetMin = cr.offsetMax = Vector2.zero; cr.sizeDelta = Vector2.zero;
            cancelObj.GetComponent<Button>().onClick.AddListener(() => _disciplinePanel.SetActive(false));

            // Confirm button
            var confirmObj = NewButton("Confirm", _disciplinePanel.transform, new Color(0.1f, 0.35f, 0.1f));
            var confR = confirmObj.GetComponent<RectTransform>();
            confR.anchorMin = new Vector2(0.55f, 0.04f); confR.anchorMax = new Vector2(0.95f, 0.20f);
            confR.offsetMin = confR.offsetMax = Vector2.zero; confR.sizeDelta = Vector2.zero;
            confirmObj.GetComponent<Button>().onClick.AddListener(() =>
            {
                string name    = _disciplineNameInput.text.Trim();
                string purpose = _disciplineThemeInput.text.Trim();
                _disciplinePanel.SetActive(false);
                if (!string.IsNullOrEmpty(name))
                    _ = StartNewDiscipline(name, purpose);
            });

            _disciplinePanel.SetActive(false);
        }

        static TMP_InputField BuildInputField(string name, Transform parent,
            Vector2 anchorMin, Vector2 anchorMax, bool multiline = false)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            obj.transform.SetParent(parent, false);
            obj.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.04f);
            var r = obj.GetComponent<RectTransform>();
            r.anchorMin = anchorMin; r.anchorMax = anchorMax;
            r.offsetMin = r.offsetMax = Vector2.zero; r.sizeDelta = Vector2.zero;

            // Text area
            var textAreaObj = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            textAreaObj.transform.SetParent(obj.transform, false);
            var taRect = textAreaObj.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero; taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(4, 2); taRect.offsetMax = new Vector2(-4, -2);

            // Placeholder
            var phObj = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
            phObj.transform.SetParent(textAreaObj.transform, false);
            var phTmp = phObj.GetComponent<TextMeshProUGUI>();
            phTmp.text = "..."; phTmp.fontSize = 13;
            phTmp.color = new Color(0.5f, 0.5f, 0.5f);
            phTmp.enableWordWrapping = multiline;
            FullStretch(phObj.GetComponent<RectTransform>());

            // Input text
            var inputObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            inputObj.transform.SetParent(textAreaObj.transform, false);
            var inputTmp = inputObj.GetComponent<TextMeshProUGUI>();
            inputTmp.fontSize = 13; inputTmp.color = Color.white;
            inputTmp.enableWordWrapping = multiline;
            FullStretch(inputObj.GetComponent<RectTransform>());

            var field = obj.GetComponent<TMP_InputField>();
            field.textViewport      = textAreaObj.GetComponent<RectTransform>();
            field.textComponent     = inputTmp;
            field.placeholder       = phTmp;
            field.lineType          = multiline
                ? TMP_InputField.LineType.MultiLineNewline
                : TMP_InputField.LineType.SingleLine;
            return field;
        }

        // ── Asset loading ────────────────────────────────────────────────────────

        async void TryLoadNodeImage(string uuid, RawImage target)
        {
            string path = SkillWebPlugin.GetImagePath(uuid);
            if (!File.Exists(path)) return;
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
            if (target == null) return;
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, bytes);
            target.texture = tex;
            target.color   = Color.white;
        }

        void LoadBackground(RawImage target)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "SkillWeb", "SkillWeb_bkg.png");
            if (!File.Exists(path))
                path = Path.Combine(Path.GetDirectoryName(typeof(SkillWebUI).Assembly.Location), "Assets", "SkillWeb_bkg.png");
            if (!File.Exists(path)) return;
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            target.texture = tex;
        }

        void LoadNodeFrame()
        {
            if (_nodeFrameSprite != null) return;
            string path = Path.Combine(Application.streamingAssetsPath, "SkillWeb", "PassiveSkillRing.png");
            if (!File.Exists(path))
                path = Path.Combine(Path.GetDirectoryName(typeof(SkillWebUI).Assembly.Location), "Assets", "PassiveSkillRing.png");
            if (!File.Exists(path)) return;
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(path));
            _nodeFrameSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        // ── Spatial helpers ───────────────────────────────────────────────────────

        SkillNode FindBestParent()
        {
            var candidates = _data.nodes.FindAll(n => n.isUnlocked && n.connectedIds.Count < 5);
            if (candidates.Count == 0) candidates = _data.nodes.FindAll(n => n.isUnlocked);
            if (candidates.Count == 0 && _data.nodes.Count > 0) return _data.nodes[0];
            return candidates.Count > 0 ? candidates[UnityEngine.Random.Range(0, candidates.Count)] : null;
        }

        Vector2 FindFreePosition(SkillNode origin, float minD, float maxD)
        {
            if (origin == null)
                return new Vector2(UnityEngine.Random.Range(-400f, 400f), UnityEngine.Random.Range(-400f, 400f));
            for (int i = 0; i < 60; i++)
            {
                float rad = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float d   = UnityEngine.Random.Range(minD, maxD);
                var   pos = origin.position + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * d;
                if (!_data.CheckCollision(pos)) return pos;
            }
            return origin.position + new Vector2(0, -maxD);
        }

        Vector2 FindFreeSpotFarAway()
        {
            for (int i = 0; i < 100; i++)
            {
                float rad = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float d   = UnityEngine.Random.Range(900f, 1800f);
                var   pos = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * d;
                if (!_data.CheckCollision(pos)) return pos;
            }
            return new Vector2(UnityEngine.Random.Range(-1500, 1500), 1500);
        }

        Color GetTreeColor(SkillTree tree)
        {
            if (tree == null) return Color.white;
            return ColorUtility.TryParseHtmlString(tree.colorHex, out Color c) ? c : Color.white;
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
            tmp.fontSize  = 14;
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
    }
}
