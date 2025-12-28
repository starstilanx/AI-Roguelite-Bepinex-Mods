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
        private GameCharacter _currentNpc;
        private GameplayManager _manager;

        private Transform _scrollContent;
        private TextMeshProUGUI _titleText;
        private Sprite _customBgSprite;

        public static void Init()
        {
            if (Instance == null)
            {
                var obj = new GameObject("NPCExamineUI");
                Instance = obj.AddComponent<NPCExamineUI>();
                Instance.LoadAssets();
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
            
            _window.SetActive(true);
            _window.transform.SetAsLastSibling();
            Refresh();
        }

        public void RefreshIfNpc(string uuid)
        {
            if (_window != null && _window.activeSelf && _currentNpc != null && _currentNpc.uuid == uuid)
            {
                Refresh();
            }
        }

        private void CreateUI()
        {
            _window = new GameObject("NPCExamineWindow", typeof(RectTransform));
            _window.transform.SetParent(_manager.canvasTransform, false);
            
            var rect = _window.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(500, 700); 
            rect.anchoredPosition = Vector2.zero;

            // 1. Background (Custom Panel)
            var bgObj = new GameObject("Background", typeof(RectTransform));
            bgObj.transform.SetParent(_window.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;
            
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
                else { baseBg.color = new Color(0.85f, 0.8f, 0.7f, 1f); }
            }

            // 1b. Clarity Overlay (White wash to make text pop)
            var overlayObj = new GameObject("ClarityOverlay", typeof(RectTransform));
            overlayObj.transform.SetParent(_window.transform, false);
            var ovRect = overlayObj.GetComponent<RectTransform>();
            ovRect.anchorMin = new Vector2(0.1f, 0.1f); 
            ovRect.anchorMax = new Vector2(0.9f, 0.9f); 
            ovRect.sizeDelta = Vector2.zero;
            var ovImg = overlayObj.AddComponent<Image>();
            ovImg.color = new Color(1f, 1f, 1f, 0.2f); // Subtle wash

            // 2. Window Frame (Metallic) 
            if (_customBgSprite == null && _manager.textureStuff != null)
            {
                var frameObj = new GameObject("Frame", typeof(RectTransform));
                frameObj.transform.SetParent(_window.transform, false);
                var frameRect = frameObj.GetComponent<RectTransform>();
                frameRect.anchorMin = Vector2.zero;
                frameRect.anchorMax = Vector2.one;
                frameRect.sizeDelta = Vector2.zero;
                
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
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.15f, 0.1f, 0.05f, 1f);
            if (_manager.currentPlaceText != null) _titleText.font = _manager.currentPlaceText.font;
            var titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.anchoredPosition = new Vector2(0, -45);
            titleRect.sizeDelta = new Vector2(0, 50);

            // Close Button
            var closeBtnObj = new GameObject("CloseBtn", typeof(RectTransform));
            closeBtnObj.transform.SetParent(_window.transform, false);
            var closeImg = closeBtnObj.AddComponent<Image>();
            closeImg.color = new Color(0.7f, 0.2f, 0.2f, 1f);
            var closeBtn = closeBtnObj.AddComponent<Button>();
            closeBtn.onClick.AddListener(() => _window.SetActive(false));
            var closeRect = closeBtnObj.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1, 1);
            closeRect.anchorMax = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-30, -30);
            closeRect.sizeDelta = new Vector2(30, 30);
            var closeTxt = new GameObject("X").AddComponent<TextMeshProUGUI>();
            closeTxt.transform.SetParent(closeBtnObj.transform, false);
            closeTxt.text = "X";
            closeTxt.fontSize = 18;
            closeTxt.alignment = TextAlignmentOptions.Center;
            closeTxt.rectTransform.sizeDelta = new Vector2(30, 30);

            // Scroll View
            var scrollObj = new GameObject("ScrollView", typeof(RectTransform));
            scrollObj.transform.SetParent(_window.transform, false);
            var sRect = scrollObj.GetComponent<RectTransform>();
            sRect.anchorMin = Vector2.zero;
            sRect.anchorMax = Vector2.one;
            sRect.offsetMin = new Vector2(50, 80);
            sRect.offsetMax = new Vector2(-50, -110);

            var scrollRect = scrollObj.AddComponent<ScrollRect>();
            
            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(scrollObj.transform, false);
            var vRect = viewport.GetComponent<RectTransform>();
            vRect.anchorMin = Vector2.zero; vRect.anchorMax = Vector2.one; vRect.sizeDelta = Vector2.zero;
            viewport.AddComponent<RectMask2D>();
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
            vlg.spacing = 15; vlg.padding = new RectOffset(20, 20, 20, 20);
            content.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = (RectTransform)cRect;
        }

        private void LoadAssets()
        {
            string[] searchPaths = new string[]
            {
                Path.Combine(UnityEngine.Application.streamingAssetsPath, "NPCExpansion", "CharacterPanel.png"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Assets", "CharacterPanel.png"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CharacterPanel.png")
            };

            foreach (string filePath in searchPaths)
            {
                if (File.Exists(filePath))
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(filePath);
                        Texture2D tex = new Texture2D(2, 2);
                        if (tex.LoadImage(data))
                        {
                            _customBgSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                            Debug.Log("[NPCExamineUI] Successfully loaded custom background from: " + filePath);
                            return;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning("[NPCExamineUI] Error loading image at " + filePath + ": " + ex.Message);
                    }
                }
                else
                {
                    Debug.Log("[NPCExamineUI] Did not find asset at: " + filePath);
                }
            }
            Debug.LogWarning("[NPCExamineUI] Custom background 'CharacterPanel.png' not found in any search path.");
        }

        private void Refresh()
        {
            if (_currentNpc == null) return;
            _titleText.text = "Examining: " + _currentNpc.GetPrettyName();

            foreach (Transform child in _scrollContent) Destroy(child.gameObject);

            var data = NPCData.Load(_currentNpc.uuid);
            if (data == null) data = NPCData.CreateDefault(_currentNpc.GetPrettyName());
            
            if (string.IsNullOrEmpty(data.Personality))
            {
                AddActionBtn("Generate Full Profile", async (txt, btn) => {
                    await HandleGeneration(txt, btn);
                });
            }
            else
            {
                AddActionBtn("Regenerate Profile (Refreshes Stats)", async (txt, btn) => {
                    await HandleGeneration(txt, btn);
                });
            }

            // 1. Basic Stats
            AddHeader("Core Status");
            AddStatRow("Level", _currentNpc.level.ToString());
            AddStatRow("Health", $"{_currentNpc.health} / {_currentNpc.maxHealth}");
            AddStatRow("Damage", _currentNpc.damage.ToString());
            AddStatRow("Gold", _currentNpc.numGold.ToString());
            
            string affinityColor = "black";
            if (data.Affinity >= 50) affinityColor = "#008800";
            else if (data.Affinity <= -50) affinityColor = "#880000";
            AddStatRow("Affinity", $"<color={affinityColor}>{data.Affinity} ({data.RelationshipStatus})</color>");
            
            if (!string.IsNullOrEmpty(data.Scenario))
            {
                AddHeader("Current Situation");
                AddText(data.Scenario, 16, Color.black);
            }

            if (!string.IsNullOrEmpty(data.Personality))
            {
                AddHeader("Personality");
                AddText(data.Personality, 15, new Color(0.1f, 0.1f, 0.2f, 0.9f));
            }

            if (data.Tags != null && data.Tags.Count > 0)
            {
                AddHeader("Nature");
                AddText(string.Join(", ", data.Tags), 15, Color.black);
            }

            if (data.InteractionTraits != null && data.InteractionTraits.Count > 0)
            {
                AddHeader("Disposition");
                AddText(string.Join(", ", data.InteractionTraits), 15, Color.black);
            }

            // 2. Attributes
            AddHeader("Attributes");
            foreach (var attr in data.Attributes)
            {
                AddStatRow(attr.Key.ToString(), attr.Value.ToString());
            }

            // 3. Skills
            AddHeader("Skills");
            if (data.Skills.Count == 0) AddText("No specialized skills.", 15, Color.gray);
            else
            {
                foreach (var sk in data.Skills.Values)
                {
                    AddStatRow(sk.GetReadableSkillName(), "Lvl " + sk.level);
                }
            }

            // 4. Abilities
            AddHeader("Abilities");
            if (data.Abilities.Count == 0) AddText("No active abilities.", 15, Color.gray);
            else
            {
                foreach (var abil in data.Abilities)
                {
                    AddText("• " + abil, 16, Color.black);
                }
            }
        }

        private async Task HandleGeneration(TextMeshProUGUI txt, Button btn)
        {
            txt.text = "Generating...";
            string context = _manager.GetContextForQuickActions();
            bool success = await NPCGenerator.GenerateLore(_currentNpc, context);
            if (success) 
            {
                Refresh();
            }
            else 
            {
                txt.text = "Generation Failed";
            }
        }

        private void AddHeader(string text)
        {
            var obj = new GameObject("Header", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var txt = obj.AddComponent<TextMeshProUGUI>();
            txt.text = text.ToUpper();
            txt.fontSize = 20;
            txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(0.3f, 0.15f, 0.05f, 1f);
            if (_titleText != null) txt.font = _titleText.font;
            obj.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 35);
        }

        private void AddStatRow(string label, string value)
        {
            var obj = new GameObject("StatRow", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 25);

            var lObj = new GameObject("Label").AddComponent<TextMeshProUGUI>();
            lObj.transform.SetParent(obj.transform, false);
            lObj.text = label + ":";
            lObj.fontSize = 18;
            lObj.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
            if (_titleText != null) lObj.font = _titleText.font;
            lObj.rectTransform.anchorMin = Vector2.zero;
            lObj.rectTransform.anchorMax = new Vector2(0.4f, 1);
            lObj.rectTransform.offsetMin = Vector2.zero;
            lObj.rectTransform.offsetMax = Vector2.zero;
            lObj.alignment = TextAlignmentOptions.Left;

            var vObj = new GameObject("Value").AddComponent<TextMeshProUGUI>();
            vObj.transform.SetParent(obj.transform, false);
            vObj.text = value;
            vObj.fontSize = 18;
            vObj.color = new Color(0f, 0f, 0f, 1f);
            if (_titleText != null) vObj.font = _titleText.font;
            vObj.fontStyle = FontStyles.Bold;
            vObj.rectTransform.anchorMin = new Vector2(0.4f, 0);
            vObj.rectTransform.anchorMax = Vector2.one;
            vObj.rectTransform.offsetMin = Vector2.zero;
            vObj.rectTransform.offsetMax = Vector2.zero;
            vObj.alignment = TextAlignmentOptions.Right;
        }

        private void AddText(string text, int size, Color color)
        {
            var obj = new GameObject("Text", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var txt = obj.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = size;
            txt.color = color;
            txt.enableWordWrapping = true;
            if (_titleText != null) txt.font = _titleText.font;
            
            var csf = obj.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            csf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        }
        private void AddActionBtn(string label, System.Func<TextMeshProUGUI, Button, Task> asyncAction)
        {
            var obj = new GameObject("ActionBtn", typeof(RectTransform));
            obj.transform.SetParent(_scrollContent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 45);

            var img = obj.AddComponent<Image>();
            img.color = new Color(0.2f, 0.35f, 0.6f, 1f); 

            var btn = obj.AddComponent<Button>();
            
            var txtObj = new GameObject("Text");
            txtObj.transform.SetParent(obj.transform, false);
            var txt = txtObj.AddComponent<TextMeshProUGUI>();
            txt.text = label;
            txt.fontSize = 20;
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;
            if (_titleText != null) txt.font = _titleText.font;
            
            var txtRect = txtObj.GetComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero; txtRect.anchorMax = Vector2.one; 
            txtRect.offsetMin = Vector2.zero; txtRect.offsetMax = Vector2.zero;

            btn.onClick.AddListener(async () => {
                btn.interactable = false;
                await asyncAction(txt, btn);
                if (btn != null) btn.interactable = true;
            });
        }
    }
}
