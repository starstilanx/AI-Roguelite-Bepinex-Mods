using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AIROG_SkillWeb
{
    public class SkillWebUI : MonoBehaviour
    {
        public static SkillWebUI Instance { get; private set; }


        private RectTransform _contentRoot;
        private RectTransform _nodeContainer;
        private RectTransform _lineContainer;
        private RawImage _background;
        private GameplayManager _manager;
        private SkillWebData _data;
        private bool _isGenerating = false;
        private TextMeshProUGUI _statusText;

        private Vector2 _lastMousePos;
        private float _zoomLevel = 1.0f;
        private const float MIN_ZOOM = 0.5f;
        private const float MAX_ZOOM = 3.0f;

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
            _data = data;

            if (_window == null) CreateUI();

            _window.SetActive(true);
            _window.SetActive(true);
            if (_tooltipCanvas != null) _tooltipCanvas.gameObject.SetActive(true);
            Refresh();
        }

        private GameObject _window;
        private Canvas _tooltipCanvas;
        private Sprite _nodeFrameSprite;

        private void OnDestroy()
        {
            if (_window != null) Destroy(_window);
            if (_tooltipCanvas != null) Destroy(_tooltipCanvas.gameObject);
        }

        private void CreateUI()
        {
            _window = new GameObject("SkillWebWindow");
            // Set parent to null to ensure it's a root GameObject
            _window.transform.SetParent(null, false);
            
            _window.AddComponent<RectTransform>();
            var canvas = _window.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 500; // Lower than game modals

            _window.AddComponent<GraphicRaycaster>();
            
            var scaler = _window.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var rect = _window.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            // Mask/View Area
            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(_window.transform, false);
            viewport.GetComponent<Image>().color = new Color(0, 0, 0, 0.8f);
            var viewRect = viewport.GetComponent<RectTransform>();
            viewRect.anchorMin = new Vector2(0.05f, 0.05f);
            viewRect.anchorMax = new Vector2(0.95f, 0.95f);
            viewRect.sizeDelta = Vector2.zero;

            // Content Root (Pannable/Zoomable)
            var contentObj = new GameObject("Content", typeof(RectTransform));
            contentObj.transform.SetParent(viewport.transform, false);
            _contentRoot = contentObj.GetComponent<RectTransform>();
            _contentRoot.sizeDelta = new Vector2(5000, 5000); // Massive canvas
            _contentRoot.anchoredPosition = Vector2.zero;
            _contentRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _contentRoot.anchorMax = new Vector2(0.5f, 0.5f);

            // Background
            var bgObj = new GameObject("Background", typeof(RectTransform), typeof(RawImage));
            bgObj.transform.SetParent(_contentRoot, false);
            _background = bgObj.GetComponent<RawImage>();
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.sizeDelta = new Vector2(5000, 5000);
            bgRect.anchoredPosition = Vector2.zero;
            bgRect.sizeDelta = new Vector2(5000, 5000);
            bgRect.anchoredPosition = Vector2.zero;
            LoadBackground();
            LoadNodeFrame();

            // Layers
            _lineContainer = new GameObject("Lines", typeof(RectTransform)).GetComponent<RectTransform>();
            _lineContainer.transform.SetParent(_contentRoot, false);
            _lineContainer.anchorMin = new Vector2(0.5f, 0.5f);
            _lineContainer.anchorMax = new Vector2(0.5f, 0.5f);
            _lineContainer.sizeDelta = Vector2.zero;

            _nodeContainer = new GameObject("Nodes", typeof(RectTransform)).GetComponent<RectTransform>();
            _nodeContainer.transform.SetParent(_contentRoot, false);
            _nodeContainer.anchorMin = new Vector2(0.5f, 0.5f);
            _nodeContainer.anchorMax = new Vector2(0.5f, 0.5f);
            _nodeContainer.sizeDelta = Vector2.zero;

            // Close Button
            var closeBtn = CreateButton("Close", new Vector2(-20, -20), new Vector2(100, 40), 
                new Vector2(1, 1), new Vector2(1, 1), () => {
                    _window.SetActive(false);
                    if (_tooltipCanvas != null) _tooltipCanvas.gameObject.SetActive(false);
                });
            closeBtn.transform.SetParent(_window.transform, false);

            // Debug: Generate Node Button
            // Anchor Top-Left (0,1), Pivot Top-Left (0,1)
            var genBtn = CreateButton("Lore Node", new Vector2(20, -20), new Vector2(140, 40), 
                new Vector2(0, 1), new Vector2(0, 1), () => { if (!_isGenerating) AddLoreNode(); });
            genBtn.transform.SetParent(_window.transform, false);

            // Debug: Add Points Button
            var dbgPointsBtn = CreateButton("+10 Pts", new Vector2(20, -110), new Vector2(100, 40), 
                new Vector2(0, 1), new Vector2(0, 1), () => { 
                    if (_data != null) {
                        _data.skillPoints += 10;
                        SkillWebPlugin.Instance.SaveData();
                        Refresh();
                    }
                });
            dbgPointsBtn.transform.SetParent(_window.transform, false);

            // New: Plant Seed / New Discipline Button
            var seedBtn = CreateButton("New Discipline", new Vector2(20, -65), new Vector2(140, 40), 
                new Vector2(0, 1), new Vector2(0, 1), () => { PlantNewTree(); });
            seedBtn.transform.SetParent(_window.transform, false);

            // Status Text
            var statusObj = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusObj.transform.SetParent(_window.transform, false);
            _statusText = statusObj.GetComponent<TextMeshProUGUI>();
            _statusText.text = "";
            _statusText.fontSize = 18;
            _statusText.color = Color.yellow;
            _statusText.alignment = TextAlignmentOptions.Left;
            var stRect = statusObj.GetComponent<RectTransform>();
            stRect.anchorMin = new Vector2(0, 1);
            stRect.anchorMax = new Vector2(0, 1);
            stRect.pivot = new Vector2(0, 1);
            stRect.anchoredPosition = new Vector2(180, -20);
            stRect.sizeDelta = new Vector2(300, 40);

            // Points Text
            var ptsObj = new GameObject("Points", typeof(RectTransform), typeof(TextMeshProUGUI));
            ptsObj.transform.SetParent(_window.transform, false);
            _pointsText = ptsObj.GetComponent<TextMeshProUGUI>();
            _pointsText.fontSize = 20;
            _pointsText.color = Color.cyan;
            _pointsText.alignment = TextAlignmentOptions.Left;
            var ptsRect = ptsObj.GetComponent<RectTransform>();
            ptsRect.anchorMin = new Vector2(0, 1);
            ptsRect.anchorMax = new Vector2(0, 1);
            ptsRect.anchoredPosition = new Vector2(25, -70); // Below Generate Button
            ptsRect.sizeDelta = new Vector2(300, 40);

            // Create separate tooltip canvas
            var tcObj = new GameObject("SkillWebTooltipCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
             // Ensure it is a root object
            tcObj.transform.SetParent(null, false);
            _tooltipCanvas = tcObj.GetComponent<Canvas>();
            _tooltipCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _tooltipCanvas.overrideSorting = true;
            _tooltipCanvas.sortingOrder = 502; // Always above window

            var tcScaler = tcObj.GetComponent<CanvasScaler>();
            tcScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            tcScaler.referenceResolution = new Vector2(1920, 1080);
            tcScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            tcScaler.matchWidthOrHeight = 0.5f;

            CreateCustomTooltip();
        }

        private GameObject _customTooltip;
        private TextMeshProUGUI _customTooltipText;
        private TextMeshProUGUI _pointsText;

        private void CreateCustomTooltip()
        {
            _customTooltip = new GameObject("CustomTooltip", typeof(RectTransform), typeof(Image), typeof(ContentSizeFitter));
            _customTooltip.transform.SetParent(_tooltipCanvas.transform, false);
            var rect = _customTooltip.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0, 1); // Pivot Top-Left
            
            var img = _customTooltip.GetComponent<Image>();
            img.color = new Color(0, 0, 0, 0.9f);

            // Canvas already on parent

            var fitter = _customTooltip.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            
            // Set fixed width
            rect.sizeDelta = new Vector2(350, 0); 

            var textObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObj.transform.SetParent(_customTooltip.transform, false);
            
            _customTooltipText = textObj.GetComponent<TextMeshProUGUI>();
            _customTooltipText.enableWordWrapping = true;
            _customTooltipText.fontSize = 14;
            _customTooltipText.color = Color.white;
            _customTooltipText.margin = new Vector4(10, 10, 10, 10);
            _customTooltipText.alignment = TextAlignmentOptions.TopLeft;

            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            _customTooltip.SetActive(false);
        }

        private void Update()
        {
            if (_window == null || !_window.activeSelf) return;

            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            // Tooltip Update
            // Tooltip Update
             if (_customTooltip.activeSelf)
            {
                Vector2 mousePos = mouse.position.ReadValue();
                
                // Convert screen mouse pos to local point in TOOLTIP CANVAS
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _tooltipCanvas.GetComponent<RectTransform>(), 
                    mousePos, 
                    null, 
                    out Vector2 localPoint
                );
                
                RectTransform toolRect = _customTooltip.GetComponent<RectTransform>();
                // Keep offset so it doesn't block mouse
                toolRect.anchoredPosition = localPoint + new Vector2(15, -15); 
            }

            // Panning
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _lastMousePos = mouse.position.ReadValue();
            }
            
            if (mouse.leftButton.isPressed)
            {
                Vector2 currentMousePos = mouse.position.ReadValue();
                Vector2 delta = currentMousePos - _lastMousePos;
                _contentRoot.anchoredPosition += delta;
                _lastMousePos = currentMousePos;
            }

            // Zooming
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0)
            {
                // Normalize scroll delta between different systems (often 120 or 1.0)
                float zoomDelta = (scroll > 0 ? 1 : -1) * 0.1f;
                _zoomLevel = Mathf.Clamp(_zoomLevel + zoomDelta, MIN_ZOOM, MAX_ZOOM);
                _contentRoot.localScale = new Vector3(_zoomLevel, _zoomLevel, 1);
            }
        }

        // ... (TryLoadNodeImage is unchanged)

        private SkillNode _selectedNode;

        private void OnNodeClicked(SkillNode node)
        {
            Debug.Log($"[SkillWeb] Selected: {node.name}");
            _selectedNode = node;
            Refresh(); // Redraw with selection highlight
        }

        private GameObject CreateButton(string label, Vector2 pos, Vector2 size, Vector2 anchor, Vector2 pivot, Action onClick)
        {
            var obj = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            obj.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f);
            
            var btn = obj.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            var textObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObj.transform.SetParent(obj.transform, false);
            var text = textObj.GetComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 18;
            text.alignment = TextAlignmentOptions.Center;
            text.rectTransform.sizeDelta = size;

            return obj;
        }

        public void ShowTooltip(SkillNode node)
        {
             if (_customTooltip == null) return;
             
             string statsStr = "";
             foreach (var kvp in node.statModifiers) statsStr += $"\n<color=green>+{kvp.Value} {kvp.Key}</color>";

             var tree = _data.GetTree(node.treeId);
             string treeStr = tree != null ? $"<i><color={tree.colorHex}>[{tree.name}]</color></i>\n" : "";

             string traitsStr = "";
             if (node.narrativeTraits != null && node.narrativeTraits.Count > 0)
             {
                 foreach (var trait in node.narrativeTraits) traitsStr += $"\n<color=orange>✧ {trait}</color>";
             }

             _customTooltipText.text = $"{treeStr}<b>{node.name}</b>\n{node.description}\n{statsStr}{traitsStr}";
             _customTooltip.SetActive(true);
        }

        public void HideTooltip()
        {
            if (_customTooltip != null) _customTooltip.SetActive(false);
        }
        private async void AddLoreNode()
        {
            if (_isGenerating) return;
            
            int cost = SkillWebPlugin.Instance.SkillConfig.NodeCost;
            if (_data.skillPoints < cost)
            {
                _statusText.text = $" Need {cost} Skill Points!";
                await Task.Delay(2000);
                if (_statusText != null) _statusText.text = "";
                return;
            }

            _isGenerating = true;
            _statusText.text = " Divining Fate...";

            SkillNode parentNode = _selectedNode; 
            Vector2 pos = Vector2.zero;
            bool foundSpot = false;

            if (_data.nodes.Count > 0)
            {
                // If we have a selection, try to branch from it
                if (parentNode != null)
                {
                    for (int i = 0; i < 50; i++)
                    {
                        float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                        float dist = UnityEngine.Random.Range(220f, 350f);
                        Vector2 candPos = parentNode.position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;

                        if (!_data.CheckCollision(candPos))
                        {
                            pos = candPos;
                            foundSpot = true;
                            break;
                        }
                    }
                }

                if (!foundSpot)
                {
                    // Random fallback search
                    var candidates = _data.nodes.Where(n => n.connectedIds.Count < 4).ToList();
                    if (candidates.Count == 0) candidates = _data.nodes;

                    for (int i = 0; i < 50; i++)
                    {
                        var candParent = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                        float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                        float dist = UnityEngine.Random.Range(220f, 350f);
                        Vector2 candPos = candParent.position + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;

                        if (!_data.CheckCollision(candPos)) 
                        {
                            parentNode = candParent;
                            pos = candPos;
                            foundSpot = true;
                            break;
                        }
                    }
                }

                if (!foundSpot)
                {
                    parentNode = _data.nodes[_data.nodes.Count - 1];
                    pos = parentNode.position + new Vector2(0, -250);
                }

                var tree = _data.GetTree(parentNode.treeId);
                var newNode = await SkillWebGenerator.GenerateLoreBasedNode(_manager, parentNode, pos, tree);
                if (newNode != null)
                {
                    newNode.treeId = parentNode.treeId; // Ensure it stays in same discipline
                    newNode.isUnlocked = true;
                    _data.AddNode(newNode);
                    _data.AddConnection(parentNode.id, newNode.id);
                    
                    _data.skillPoints -= cost;
                    _data.RecalculateStats();
                    SkillWebPlugin.Instance.SaveData();
                    
                    Refresh();
                    _statusText.text = " Destiny Unlocked!";
                }
                else
                {
                    _statusText.text = " Generation Failed";
                }
            }
            else
            {
                var result = await SkillWebGenerator.GenerateStartingCluster(_manager, Vector2.zero);
                if (result.nodes != null && result.nodes.Count > 0)
                {
                    if (result.trees != null) _data.trees.AddRange(result.trees);
                    foreach(var n in result.nodes) 
                    {
                        n.isUnlocked = true;
                        _data.AddNode(n);
                    }
                    _data.skillPoints -= cost; 
                    _data.RecalculateStats();
                    SkillWebPlugin.Instance.SaveData();
                    Refresh();
                    _statusText.text = " The Web Awakens!";
                }
                else 
                {
                    _statusText.text = " Genesis Failed";
                }
            }

            await Task.Delay(2000);
            if (_statusText != null) _statusText.text = "";
            _isGenerating = false;
        }

        private void PlantNewTree()
        {
            if (_isGenerating) return;
            
            int cost = SkillWebPlugin.Instance.SkillConfig.NodeCost;
            if (_data.skillPoints < cost)
            {
                _statusText.text = $" Need {cost} Skill Points!";
                return;
            }

            if (_manager.nTextPromptModalWindow == null)
            {
                _statusText.text = " Prompt Modal Missing!";
                return;
            }

            var args = new List<NTextPromptModal.PromptArg>();
            args.Add(new NTextPromptModal.TextPromptArg("Discipline Name", "Technique", "e.g. Shadow Arts, Elven Heritage...", false, null));
            args.Add(new NTextPromptModal.TextPromptArg("Theme/Purpose", "", "e.g. Skills related to stealth, magic, or my race's history.", true, null));

            _manager.nTextPromptModalWindow.PresentSelf(args, "Establish New Discipline", () => {
                string treeName = _manager.nTextPromptModalWindow.HackyGetTxt(0);
                string treePurpose = _manager.nTextPromptModalWindow.HackyGetTxt(1);
                if (string.IsNullOrEmpty(treeName)) return;

                StartNewDiscipline(treeName, treePurpose);
            });
        }

        private async void StartNewDiscipline(string name, string purpose)
        {
            if (_isGenerating) return;
            _isGenerating = true;
            _statusText.text = " Rooting Discipline...";

            // Find a spot far away
            Vector2 pos = FindFreeSpotFarAway();

            // Create Tree with random color
            string[] colors = new string[] { "#FF5555", "#55FF55", "#5555FF", "#FFFF55", "#FF55FF", "#55FFFF", "#FFAA00", "#AA00FF", "#00FFAA" };
            string color = colors[UnityEngine.Random.Range(0, colors.Length)];
            var tree = new SkillTree(Guid.NewGuid().ToString(), name, purpose);
            tree.colorHex = color;
            _data.trees.Add(tree);

            // Generate Root Node (no parent)
            var newNode = await SkillWebGenerator.GenerateLoreBasedNode(_manager, null, pos, tree);
            if (newNode != null)
            {
                newNode.isUnlocked = true;
                _data.AddNode(newNode);
                
                _data.skillPoints -= SkillWebPlugin.Instance.SkillConfig.NodeCost;
                _data.RecalculateStats();
                SkillWebPlugin.Instance.SaveData();
                Refresh();
                _statusText.text = $" {name} Established!";
            }
            else
            {
                _statusText.text = " Establishment Failed";
            }

            await Task.Delay(2000);
            if (_statusText != null) _statusText.text = "";
            _isGenerating = false;
        }

        private Vector2 FindFreeSpotFarAway()
        {
            if (_data.nodes.Count == 0) return Vector2.zero;
            
            // Try in a ring far out
            for (int i = 0; i < 100; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = UnityEngine.Random.Range(800f, 1500f);
                Vector2 pos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                
                if (!_data.CheckCollision(pos)) return pos;
            }
            // Fallback: Offset from current center
            return new Vector2(UnityEngine.Random.Range(-1000, 1000), 1000);
        }

        private void LoadBackground()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "SkillWeb", "SkillWeb_bkg.png");
            if (!File.Exists(path))
            {
                path = Path.Combine(Path.GetDirectoryName(typeof(SkillWebUI).Assembly.Location), "Assets", "SkillWeb_bkg.png");
            }

            if (File.Exists(path))
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                ImageConversion.LoadImage(tex, data);
                _background.texture = tex;
            }
        }

        private void LoadNodeFrame()
        {
            if (_nodeFrameSprite != null) return;
            
            // Priority: StreamingAssets/SkillWeb/PassiveSkillRing.png
            string path = Path.Combine(Application.streamingAssetsPath, "SkillWeb", "PassiveSkillRing.png");
            
            if (!File.Exists(path))
            {
                // Fallback: Plugin Assets folder
                path = Path.Combine(Path.GetDirectoryName(typeof(SkillWebUI).Assembly.Location), "Assets", "PassiveSkillRing.png");
            }

            if (File.Exists(path))
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                ImageConversion.LoadImage(tex, data);
                _nodeFrameSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            else
            {
                UnityEngine.Debug.LogWarning($"[SkillWeb] PassiveSkillRing.png not found in StreamingAssets or Plugin folder.");
            }
        }

        public void Refresh()
        {
            if (_data == null) return;
            
            if (_pointsText != null) _pointsText.text = $"Points: {_data.skillPoints}";

            // Clear old
            foreach (Transform child in _nodeContainer) Destroy(child.gameObject);
            foreach (Transform child in _lineContainer) Destroy(child.gameObject);

            // Draw lines
            foreach (var node in _data.nodes)
            {
                var tree = _data.GetTree(node.treeId);
                Color treeColor = Color.white;
                if (tree != null && ColorUtility.TryParseHtmlString(tree.colorHex, out Color c)) treeColor = c;

                foreach (var targetId in node.connectedIds)
                {
                    var target = _data.nodes.Find(n => n.id == targetId);
                    if (target != null)
                    {
                        // To avoid double drawing, only draw if id is smaller
                        if (string.Compare(node.id, target.id) < 0)
                            DrawLine(node.position, target.position, treeColor);
                    }
                }
            }

            // Draw Nodes
            foreach (var node in _data.nodes)
            {
                DrawNode(node);
            }
        }

        private void DrawNode(SkillNode node)
        {
            // Container
            var obj = new GameObject($"Node_{node.id}", typeof(RectTransform), typeof(Button));
            obj.transform.SetParent(_nodeContainer, false);
            
            var rect = obj.GetComponent<RectTransform>();
            rect.anchoredPosition = node.position;
            rect.sizeDelta = new Vector2(160, 90); // 16:9 Aspect for Ring

            // IconLayer (Bottom)
            var iconObj = new GameObject("Icon", typeof(RectTransform), typeof(RawImage));
            iconObj.transform.SetParent(obj.transform, false);
            var iconRect = iconObj.GetComponent<RectTransform>();
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(65, 65); // Fit inside the ring's hole
            var rawImg = iconObj.GetComponent<RawImage>();
            
            if (!string.IsNullOrEmpty(node.imageUuid))
            {
                TryLoadNodeImage(node.imageUuid, rawImg);
            }
            else
            {
                 rawImg.color = new Color(0.1f, 0.1f, 0.1f, 0.5f); // Placeholder darkness
            }

            // FrameLayer (Top) - The Ring
            var frameObj = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            frameObj.transform.SetParent(obj.transform, false);
            var frameRect = frameObj.GetComponent<RectTransform>();
            frameRect.anchorMin = Vector2.zero;
            frameRect.anchorMax = Vector2.one;
            frameRect.offsetMin = Vector2.zero;
            frameRect.offsetMax = Vector2.zero;
            
            var frameImg = frameObj.GetComponent<Image>();
            if (_nodeFrameSprite != null)
            {
                frameImg.sprite = _nodeFrameSprite;
                frameImg.type = Image.Type.Simple;
                frameImg.preserveAspect = true;
            }

            // Tint frame by tree color
            var tree = _data.GetTree(node.treeId);
            Color treeColor = Color.white;
            if (tree != null && ColorUtility.TryParseHtmlString(tree.colorHex, out Color c)) treeColor = c;

            // Selection Check
            if (node == _selectedNode)
            {
                frameImg.color = Color.yellow; // Highlight if selected
            }
            else
            {
                frameImg.color = node.isUnlocked ? treeColor : (treeColor * 0.5f);
            }
            if (!node.isUnlocked && node != _selectedNode) frameImg.color = new Color(frameImg.color.r, frameImg.color.g, frameImg.color.b, 1f); // Ensure opaque

            // Interaction
            var btn = obj.GetComponent<Button>();
            btn.onClick.AddListener(() => OnNodeClicked(node));
            btn.targetGraphic = frameImg; // Highlight the frame on hover

            // EventTrigger for custom tooltip interactions
            var et = obj.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            
            var entryEnter = new UnityEngine.EventSystems.EventTrigger.Entry();
            entryEnter.eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter;
            entryEnter.callback.AddListener((data) => { ShowTooltip(node); });
            et.triggers.Add(entryEnter);

            var entryExit = new UnityEngine.EventSystems.EventTrigger.Entry();
            entryExit.eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit;
            entryExit.callback.AddListener((data) => { HideTooltip(); });
            et.triggers.Add(entryExit);

            // Name label
            var labelObj = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObj.transform.SetParent(obj.transform, false);
            var labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchoredPosition = new Vector2(0, -50);
            labelRect.sizeDelta = new Vector2(150, 30);
            
            var text = labelObj.GetComponent<TextMeshProUGUI>();
            text.text = node.name;
            text.fontSize = 14;
            text.alignment = TextAlignmentOptions.Center;
        }

        private async void TryLoadNodeImage(string uuid, RawImage target)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            
            // AIAsker saves sprites with _sprite suffix
            string path = Path.Combine(SS.I.saveSubDirAsArg, uuid + "_sprite.png");
            
            // Fallback for older or non-sprite images
            if (!File.Exists(path))
            {
                path = Path.Combine(SS.I.saveSubDirAsArg, uuid + ".png");
            }

            if (File.Exists(path))
            {
                Debug.Log($"[SkillWeb] Loading image from: {path}");
                byte[] data = await Task.Run(() => File.ReadAllBytes(path));
                Texture2D tex = new Texture2D(2, 2);
                ImageConversion.LoadImage(tex, data);
                target.texture = tex;
                
                // Fix for raw image color being grey/tinted
                target.color = Color.white; 
            }
            else
            {
                Debug.LogWarning($"[SkillWeb] Image file not found: {path}");
            }
        }

        private void DrawLine(Vector2 start, Vector2 end, Color color)
        {
            var lineObj = new GameObject("Line", typeof(RectTransform), typeof(Image));
            lineObj.transform.SetParent(_lineContainer, false);

            var rt = lineObj.GetComponent<RectTransform>();
            Vector2 dir = (end - start).normalized;
            float distance = Vector2.Distance(start, end);

            rt.sizeDelta = new Vector2(distance, 4); // 4px thickness
            rt.anchoredPosition = start + dir * (distance / 2);
            rt.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            
            color.a = 0.4f; // Semi-transparent lines
            lineObj.GetComponent<Image>().color = color;
        }

    }

    public class SkillWebNodeHoverable : HoverableWithDelay
    {
        public SkillNode node;

        public override void ShowThingy()
        {
            string statsStr = "";
            if (node.statModifiers != null)
            {
                foreach (var kvp in node.statModifiers) statsStr += $"\n<color=green>+{kvp.Value} {kvp.Key}</color>";
            }

            string traitsStr = "";
            if (node.narrativeTraits != null && node.narrativeTraits.Count > 0)
            {
                foreach (var trait in node.narrativeTraits) traitsStr += $"\n<color=orange>✧ {trait}</color>";
            }
            manager.itemTooltip.ShowTooltipSmall($"<b>{node.name}</b>\n{node.description}\n{statsStr}{traitsStr}");
        }
    }
}
