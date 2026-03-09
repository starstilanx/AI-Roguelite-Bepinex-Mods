using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using System.Linq;
using System.IO;

namespace AIROG_NPCExpansion
{
    public class NPCExamineUI : MonoBehaviour
    {
        public static NPCExamineUI Instance { get; private set; }

        private GameObject _window;
        private GameObject _modalBlocker;
        private GameCharacter _currentNpc;
        private GameplayManager _manager;

        private Transform _scrollContent;
        private TextMeshProUGUI _titleText;
        private Sprite _customBgSprite;
        private Sprite _regenBtnSprite;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            LoadAssets();
        }

        public static void Init()
        {
            if (Instance == null)
            {
                var obj = new GameObject("NPCExamineUI");
                Instance = obj.AddComponent<NPCExamineUI>();
            }
        }

        public static void OpenFor(GameCharacter npc, GameplayManager manager)
        {
            if (Instance == null)
            {
                var obj = new GameObject("NPCExamineUI");
                Instance = obj.AddComponent<NPCExamineUI>();
            }
            Instance.Show(npc, manager);
        }

        private void Show(GameCharacter npc, GameplayManager manager)
        {
            _currentNpc = npc;
            _manager = manager;

            if (_window == null) CreateUI();

            if (_modalBlocker != null) _modalBlocker.SetActive(true);
            _window.SetActive(true);

            if (_modalBlocker != null) _modalBlocker.transform.SetAsLastSibling();
            _window.transform.SetAsLastSibling();

            Refresh();
        }

        public void RefreshIfNpc(string uuid)
        {
            if (_window != null && _window.activeSelf && _currentNpc != null && _currentNpc.uuid == uuid)
                Refresh();
        }

        private void CreateUI()
        {
            // Fullscreen modal blocker
            _modalBlocker = new GameObject("ExamineModalBlocker", typeof(RectTransform));
            _modalBlocker.transform.SetParent(_manager.canvasTransform, false);
            var blockerRect = _modalBlocker.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.sizeDelta = Vector2.zero;
            var blockerImg = _modalBlocker.AddComponent<Image>();
            blockerImg.color = new Color(0, 0, 0, 0.45f);
            var blockerBtn = _modalBlocker.AddComponent<Button>();
            blockerBtn.onClick.AddListener(() => { _window.SetActive(false); _modalBlocker.SetActive(false); });
            // Nested canvas ensures blocker sorts above all game UI
            var blockerCanvas = _modalBlocker.AddComponent<Canvas>();
            blockerCanvas.overrideSorting = true;
            blockerCanvas.sortingOrder = 99;
            _modalBlocker.AddComponent<GraphicRaycaster>();

            // Main window
            _window = new GameObject("NPCExamineWindow", typeof(RectTransform));
            _window.transform.SetParent(_manager.canvasTransform, false);
            var rect = _window.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(500, 700);
            rect.anchoredPosition = Vector2.zero;
            // Window canvas sits above the blocker; also prevents click-through to game panels
            var windowCanvas = _window.AddComponent<Canvas>();
            windowCanvas.overrideSorting = true;
            windowCanvas.sortingOrder = 100;
            _window.AddComponent<GraphicRaycaster>();

            // Background
            var bgObj = new GameObject("Background", typeof(RectTransform));
            bgObj.transform.SetParent(_window.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero; bgRect.anchorMax = Vector2.one; bgRect.sizeDelta = Vector2.zero;

            if (_customBgSprite != null)
            {
                var bgImg = bgObj.AddComponent<Image>();
                bgImg.sprite = _customBgSprite;
                bgImg.type = Image.Type.Simple;
                bgImg.preserveAspect = true;
            }
            else
            {
                var baseBg = bgObj.AddComponent<RawImage>();
                if (_manager.textureStuff != null && _manager.textureStuff.playerStatsBkgd != null)
                {
                    var playerBg = _manager.textureStuff.playerStatsBkgd.GetComponent<RawImage>();
                    if (playerBg != null) { baseBg.texture = playerBg.texture; baseBg.color = playerBg.color; }
                }
                else baseBg.color = new Color(0.85f, 0.8f, 0.7f, 1f);
            }

            // Clarity overlay
            var overlayObj = new GameObject("ClarityOverlay", typeof(RectTransform));
            overlayObj.transform.SetParent(_window.transform, false);
            var ovRect = overlayObj.GetComponent<RectTransform>();
            ovRect.anchorMin = new Vector2(0.08f, 0.08f);
            ovRect.anchorMax = new Vector2(0.92f, 0.92f);
            ovRect.sizeDelta = Vector2.zero;
            var ovImg = overlayObj.AddComponent<Image>();
            ovImg.color = new Color(1f, 1f, 1f, 0.18f);

            // Frame
            if (_customBgSprite == null && _manager.textureStuff != null)
            {
                var frameObj = new GameObject("Frame", typeof(RectTransform));
                frameObj.transform.SetParent(_window.transform, false);
                var frameRect = frameObj.GetComponent<RectTransform>();
                frameRect.anchorMin = Vector2.zero; frameRect.anchorMax = Vector2.one; frameRect.sizeDelta = Vector2.zero;
                var playerFrameImg = _manager.textureStuff.invFrame?.GetComponent<Image>();
                if (playerFrameImg != null)
                {
                    var frameImg = frameObj.AddComponent<Image>();
                    frameImg.sprite = playerFrameImg.sprite;
                    frameImg.type = playerFrameImg.type;
                    frameImg.color = new Color(1, 1, 1, 0.9f);
                }
            }

            // Title
            var titleObj = new GameObject("Title", typeof(RectTransform));
            titleObj.transform.SetParent(_window.transform, false);
            _titleText = titleObj.AddComponent<TextMeshProUGUI>();
            _titleText.fontSize = 28;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.15f, 0.1f, 0.05f, 1f);
            if (_manager.currentPlaceText != null) _titleText.font = _manager.currentPlaceText.font;
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1); titleRect.anchorMax = new Vector2(1, 1);
            titleRect.anchoredPosition = new Vector2(0, -52);
            titleRect.sizeDelta = new Vector2(-90, 46);

            // Close button
            var closeBtnObj = new GameObject("CloseBtn", typeof(RectTransform));
            closeBtnObj.transform.SetParent(_window.transform, false);
            var closeImg = closeBtnObj.AddComponent<Image>();
            closeImg.color = new Color(0.65f, 0.18f, 0.18f, 1f);
            var closeBtn = closeBtnObj.AddComponent<Button>();
            closeBtn.onClick.AddListener(() => { _window.SetActive(false); if (_modalBlocker != null) _modalBlocker.SetActive(false); });
            var closeRect = closeBtnObj.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1, 1); closeRect.anchorMax = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-34, -34);
            closeRect.sizeDelta = new Vector2(30, 30);
            var closeTxt = new GameObject("X").AddComponent<TextMeshProUGUI>();
            closeTxt.transform.SetParent(closeBtnObj.transform, false);
            closeTxt.text = "✕";
            closeTxt.fontSize = 16;
            closeTxt.fontStyle = FontStyles.Bold;
            closeTxt.alignment = TextAlignmentOptions.Center;
            closeTxt.color = Color.white;
            closeTxt.rectTransform.anchorMin = Vector2.zero;
            closeTxt.rectTransform.anchorMax = Vector2.one;
            closeTxt.rectTransform.offsetMin = Vector2.zero;
            closeTxt.rectTransform.offsetMax = Vector2.zero;

            // Scroll view
            var scrollObj = new GameObject("ScrollView", typeof(RectTransform));
            scrollObj.transform.SetParent(_window.transform, false);
            var sRect = scrollObj.GetComponent<RectTransform>();
            sRect.anchorMin = Vector2.zero; sRect.anchorMax = Vector2.one;
            sRect.offsetMin = new Vector2(55, 80);
            sRect.offsetMax = new Vector2(-55, -108);

            var scrollRect = scrollObj.AddComponent<ScrollRect>();
            scrollRect.scrollSensitivity = 35f;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            var scrollImg = scrollObj.AddComponent<Image>();
            scrollImg.color = new Color(0, 0, 0, 0);

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(scrollObj.transform, false);
            var vRect = viewport.GetComponent<RectTransform>();
            vRect.anchorMin = Vector2.zero; vRect.anchorMax = Vector2.one;
            vRect.sizeDelta = Vector2.zero; vRect.offsetMin = Vector2.zero; vRect.offsetMax = Vector2.zero;
            viewport.AddComponent<RectMask2D>();
            var vpImg = viewport.AddComponent<Image>();
            vpImg.color = new Color(0, 0, 0, 0);
            scrollRect.viewport = vRect;

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            _scrollContent = content.transform;
            var cRect = content.GetComponent<RectTransform>();
            cRect.anchorMin = new Vector2(0, 1); cRect.anchorMax = new Vector2(1, 1);
            cRect.sizeDelta = new Vector2(0, 0);
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childControlHeight = true; vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true; vlg.childForceExpandWidth = true;
            vlg.spacing = 8;
            vlg.padding = new RectOffset(16, 16, 14, 14);
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = cRect;
        }

        private void LoadAssets()
        {
            _customBgSprite = LoadRobustSprite("CharacterPanel.png");
            _regenBtnSprite = LoadRobustSprite("RegenProfile.png");
        }

        private Sprite LoadRobustSprite(string fileName)
        {
            string[] searchPaths = {
                Path.Combine(Application.streamingAssetsPath, "NPCExpansion", fileName),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Assets", fileName),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), fileName)
            };
            foreach (string filePath in searchPaths)
            {
                if (!File.Exists(filePath)) continue;
                try
                {
                    byte[] data = File.ReadAllBytes(filePath);
                    Texture2D tex = new Texture2D(2, 2);
                    if (tex.LoadImage(data))
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[NPCExamineUI] Error loading {fileName}: {ex.Message}");
                }
            }
            return null;
        }

        // ─── Layout ───────────────────────────────────────────────────────────────

        private void Refresh()
        {
            if (_currentNpc == null) return;
            _titleText.text = _currentNpc.GetPrettyName();

            foreach (Transform child in _scrollContent) Destroy(child.gameObject);

            var data = NPCData.Load(_currentNpc.uuid);
            if (data == null) data = NPCData.CreateDefault(_currentNpc.GetPrettyName());

            // ── GENERATION ────────────────────────────────────────────────────
            AddHeader("Generation Hint");
            AddInputField("E.g. secretly a retired assassin...", data.GenerationInstructions, (val) =>
            {
                data.GenerationInstructions = val;
                NPCData.Save(_currentNpc.uuid, data);
            });

            bool hasProfile = !string.IsNullOrEmpty(data.Personality);
            if (_regenBtnSprite != null)
                AddImageBtn(_regenBtnSprite, async (btn) => { await HandleGeneration(null, btn); });
            else
                AddActionBtn(hasProfile ? "Regenerate Profile" : "Generate Profile",
                    async (txt, btn) => { await HandleGeneration(txt, btn); });

            AddDivider();

            // ── STATUS ────────────────────────────────────────────────────────
            AddHeader("Status");
            AddInlineStats("Level", _currentNpc.level.ToString(),
                           "Health", $"{_currentNpc.health} / {_currentNpc.maxHealth}");
            AddInlineStats("Damage", _currentNpc.damage.ToString(),
                           "Gold", _currentNpc.numGold.ToString());

            string affinityHex = data.Affinity >= 50 ? "#005500"
                                : data.Affinity <= -50 ? "#880000"
                                : "#222222";
            AddStatRow("Affinity", $"<color={affinityHex}>{data.Affinity}  —  {data.RelationshipStatus}</color>");

            if (data.Tags != null && data.Tags.Count > 0)
                AddStatRow("Nature", string.Join(", ", data.Tags));
            if (data.InteractionTraits != null && data.InteractionTraits.Count > 0)
                AddStatRow("Disposition", string.Join(", ", data.InteractionTraits));

            // ── PROFILE ───────────────────────────────────────────────────────
            bool anyProfile = !string.IsNullOrEmpty(data.Personality)
                           || !string.IsNullOrEmpty(data.Scenario)
                           || !string.IsNullOrEmpty(data.FirstMessage);
            if (anyProfile)
            {
                AddDivider();

                if (!string.IsNullOrEmpty(data.Personality))
                {
                    AddHeader("Personality");
                    AddText(data.Personality, 15, new Color(0.1f, 0.1f, 0.2f));
                }

                if (!string.IsNullOrEmpty(data.Scenario))
                {
                    AddHeader("Current Situation");
                    AddText(data.Scenario, 15, new Color(0.08f, 0.08f, 0.08f));
                }

                if (!string.IsNullOrEmpty(data.FirstMessage))
                {
                    AddHeader("First Words");
                    AddText($"\"{data.FirstMessage}\"", 15, new Color(0.18f, 0.1f, 0.32f), italic: true);
                }
            }

            // ── GOALS & THOUGHTS ──────────────────────────────────────────────
            bool anyGoals = !string.IsNullOrEmpty(data.CurrentGoal)
                         || (data.RecentThoughts != null && data.RecentThoughts.Count > 0);
            if (anyGoals)
            {
                AddDivider();

                if (!string.IsNullOrEmpty(data.CurrentGoal))
                {
                    AddHeader("Current Goal");
                    AddText(data.CurrentGoal, 15, new Color(0.2f, 0.1f, 0.35f));
                }

                if (data.RecentThoughts != null && data.RecentThoughts.Count > 0)
                {
                    AddHeader("Recent Thoughts");
                    foreach (var thought in data.RecentThoughts)
                        AddText("• " + thought, 14, new Color(0.2f, 0.2f, 0.3f));
                }
            }

            // ── COMBAT STATS ──────────────────────────────────────────────────
            AddDivider();

            if (data.Attributes != null && data.Attributes.Count > 0)
            {
                AddHeader("Attributes");
                var attrs = data.Attributes.ToList();
                for (int i = 0; i < attrs.Count; i += 2)
                {
                    if (i + 1 < attrs.Count)
                        AddInlineStats(attrs[i].Key.ToString(), attrs[i].Value.ToString(),
                                       attrs[i + 1].Key.ToString(), attrs[i + 1].Value.ToString());
                    else
                        AddStatRow(attrs[i].Key.ToString(), attrs[i].Value.ToString());
                }
            }

            if (data.Skills != null && data.Skills.Count > 0)
            {
                AddHeader("Skills");
                foreach (var sk in data.Skills.Values)
                    AddStatRow(sk.GetReadableSkillName(), "Lv " + sk.level);
            }

            if (data.DetailedAbilities.Count > 0 || data.Abilities.Count > 0)
            {
                AddHeader("Abilities");
                if (data.DetailedAbilities.Count > 0)
                    foreach (var a in data.DetailedAbilities)
                        AddText($"• <b>{a.Name}</b>: {a.Description}", 15, new Color(0.05f, 0.05f, 0.05f));
                else
                    foreach (var a in data.Abilities)
                        AddText("• " + a, 15, new Color(0.05f, 0.05f, 0.05f));
            }
        }

        private async Task HandleGeneration(TextMeshProUGUI txt, Button btn)
        {
            if (txt != null) txt.text = "Generating...";
            string context = _manager.GetContextForQuickActions();
            bool success = await NPCGenerator.GenerateLore(_currentNpc, context);
            if (success)
                Refresh();
            else if (txt != null)
                txt.text = "Generation Failed";
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private void AddDivider()
        {
            var obj = new GameObject("Divider", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var img = obj.AddComponent<Image>();
            img.color = new Color(0.45f, 0.28f, 0.1f, 0.3f);
            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 2; le.preferredHeight = 2;
        }

        private void AddHeader(string text)
        {
            var obj = new GameObject("Header", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var txt = obj.AddComponent<TextMeshProUGUI>();
            txt.text = text.ToUpper();
            txt.fontSize = 13;
            txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(0.42f, 0.22f, 0.05f, 1f);
            txt.characterSpacing = 2f;
            if (_titleText != null) txt.font = _titleText.font;
            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 22; le.preferredHeight = 22;
        }

        private void AddStatRow(string label, string value)
        {
            var obj = new GameObject("StatRow", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 22; le.preferredHeight = 22;

            var lObj = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            lObj.transform.SetParent(obj.transform, false);
            lObj.text = label;
            lObj.fontSize = 15;
            lObj.color = new Color(0.3f, 0.2f, 0.1f);
            if (_titleText != null) lObj.font = _titleText.font;
            lObj.rectTransform.anchorMin = Vector2.zero;
            lObj.rectTransform.anchorMax = new Vector2(0.42f, 1);
            lObj.rectTransform.offsetMin = Vector2.zero;
            lObj.rectTransform.offsetMax = Vector2.zero;
            lObj.alignment = TextAlignmentOptions.Left;
            lObj.enableWordWrapping = false;
            lObj.overflowMode = TextOverflowModes.Ellipsis;

            var vObj = new GameObject("Value").AddComponent<TextMeshProUGUI>();
            vObj.transform.SetParent(obj.transform, false);
            vObj.text = value;
            vObj.fontSize = 15;
            vObj.color = new Color(0.05f, 0.05f, 0.05f);
            if (_titleText != null) vObj.font = _titleText.font;
            vObj.fontStyle = FontStyles.Bold;
            vObj.rectTransform.anchorMin = new Vector2(0.42f, 0);
            vObj.rectTransform.anchorMax = Vector2.one;
            vObj.rectTransform.offsetMin = Vector2.zero;
            vObj.rectTransform.offsetMax = Vector2.zero;
            vObj.alignment = TextAlignmentOptions.Left;
            vObj.enableWordWrapping = false;
            vObj.overflowMode = TextOverflowModes.Ellipsis;
        }

        private void AddInlineStats(string label1, string val1, string label2, string val2)
        {
            var row = new GameObject("InlineStats", typeof(RectTransform));
            row.transform.SetParent(_scrollContent, false);
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 22; le.preferredHeight = 22;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true; hlg.childForceExpandWidth = true;
            hlg.childControlHeight = true; hlg.childForceExpandHeight = true;
            hlg.spacing = 0;
            AddStatCell(row.transform, label1, val1);
            AddStatCell(row.transform, label2, val2);
        }

        private void AddStatCell(Transform parent, string label, string value)
        {
            var cell = new GameObject("Cell", typeof(RectTransform));
            cell.transform.SetParent(parent, false);

            var lTxt = new GameObject("L").AddComponent<TextMeshProUGUI>();
            lTxt.transform.SetParent(cell.transform, false);
            lTxt.text = label;
            lTxt.fontSize = 15;
            lTxt.color = new Color(0.3f, 0.2f, 0.1f);
            if (_titleText != null) lTxt.font = _titleText.font;
            lTxt.enableWordWrapping = false;
            lTxt.rectTransform.anchorMin = Vector2.zero;
            lTxt.rectTransform.anchorMax = new Vector2(0.48f, 1f);
            lTxt.rectTransform.offsetMin = Vector2.zero;
            lTxt.rectTransform.offsetMax = Vector2.zero;
            lTxt.alignment = TextAlignmentOptions.Left;

            var vTxt = new GameObject("V").AddComponent<TextMeshProUGUI>();
            vTxt.transform.SetParent(cell.transform, false);
            vTxt.text = value;
            vTxt.fontSize = 15;
            vTxt.color = new Color(0.05f, 0.05f, 0.05f);
            vTxt.fontStyle = FontStyles.Bold;
            if (_titleText != null) vTxt.font = _titleText.font;
            vTxt.enableWordWrapping = false;
            vTxt.rectTransform.anchorMin = new Vector2(0.48f, 0f);
            vTxt.rectTransform.anchorMax = Vector2.one;
            vTxt.rectTransform.offsetMin = Vector2.zero;
            vTxt.rectTransform.offsetMax = Vector2.zero;
            vTxt.alignment = TextAlignmentOptions.Left;
        }

        private void AddText(string text, int size, Color color, bool italic = false)
        {
            var obj = new GameObject("Text", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var txt = obj.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = size;
            txt.color = color;
            txt.enableWordWrapping = true;
            if (italic) txt.fontStyle = FontStyles.Italic;
            if (_titleText != null) txt.font = _titleText.font;
            obj.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void AddInputField(string placeholder, string currentVal, System.Action<string> onEndEdit)
        {
            var obj = new GameObject("InputField", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);

            var img = obj.AddComponent<Image>();
            img.color = new Color(0.92f, 0.9f, 0.86f, 0.9f);

            var input = obj.AddComponent<TMP_InputField>();

            var textArea = new GameObject("TextArea", typeof(RectTransform));
            textArea.transform.SetParent(obj.transform, false);
            var taRect = textArea.GetComponent<RectTransform>();
            taRect.anchorMin = Vector2.zero; taRect.anchorMax = Vector2.one;
            taRect.offsetMin = new Vector2(10, 5); taRect.offsetMax = new Vector2(-10, -5);

            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(textArea.transform, false);
            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = 15;
            text.color = Color.black;
            if (_titleText != null) text.font = _titleText.font;
            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one; textRect.sizeDelta = Vector2.zero;

            var phObj = new GameObject("Placeholder", typeof(RectTransform));
            phObj.transform.SetParent(textArea.transform, false);
            var phText = phObj.AddComponent<TextMeshProUGUI>();
            phText.text = placeholder;
            phText.fontSize = 15;
            phText.color = new Color(0.4f, 0.4f, 0.4f, 0.75f);
            phText.fontStyle = FontStyles.Italic;
            if (_titleText != null) phText.font = _titleText.font;
            var phRect = phObj.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero; phRect.anchorMax = Vector2.one; phRect.sizeDelta = Vector2.zero;

            input.textViewport = taRect;
            input.textComponent = text;
            input.placeholder = phText;
            input.text = currentVal;
            input.onEndEdit.AddListener((val) => onEndEdit(val));

            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 40; le.preferredHeight = 40; le.flexibleHeight = 0;
        }

        private void AddActionBtn(string label, System.Func<TextMeshProUGUI, Button, Task> asyncAction)
        {
            var obj = new GameObject("ActionBtn", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);

            var img = obj.AddComponent<Image>();
            img.color = new Color(0.22f, 0.38f, 0.62f, 1f);

            var btn = obj.AddComponent<Button>();

            var txtObj = new GameObject("Text");
            txtObj.transform.SetParent(obj.transform, false);
            var txt = txtObj.AddComponent<TextMeshProUGUI>();
            txt.text = label;
            txt.fontSize = 22;
            txt.fontStyle = FontStyles.Bold;
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;
            if (_titleText != null) txt.font = _titleText.font;
            var txtRect = txtObj.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero; txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero; txtRect.offsetMax = Vector2.zero;

            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 70; le.preferredHeight = 70;

            btn.onClick.AddListener(async () => {
                btn.interactable = false;
                await asyncAction(txt, btn);
                if (btn != null) btn.interactable = true;
            });
        }

        private void AddImageBtn(Sprite sprite, System.Func<Button, Task> asyncAction)
        {
            var obj = new GameObject("ImageActionBtn", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);

            var img = obj.AddComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.preserveAspect = true;

            var btn = obj.AddComponent<Button>();
            btn.onClick.AddListener(async () => {
                btn.interactable = false;
                await asyncAction(btn);
                if (btn != null) btn.interactable = true;
            });

            var le = obj.AddComponent<LayoutElement>();
            le.preferredHeight = 80; le.minHeight = 60;
        }
    }
}
