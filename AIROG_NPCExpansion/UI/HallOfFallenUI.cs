using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Memorial panel listing all lore-generated NPCs who have died during the playthrough.
    /// Opened via the "Fallen" button in NPCExamineUI or the main Mods menu.
    /// </summary>
    public class HallOfFallenUI : MonoBehaviour
    {
        public static HallOfFallenUI Instance { get; private set; }

        private GameObject _window;
        private GameObject _modalBlocker;
        private Transform _scrollContent;
        private GameplayManager _manager;

        private void Awake()
        {
            if (Instance == null) Instance = this;
        }

        public static void Init()
        {
            if (Instance == null)
            {
                var obj = new GameObject("HallOfFallenUI");
                Instance = obj.AddComponent<HallOfFallenUI>();
            }
        }

        public static void Open(GameplayManager manager)
        {
            if (Instance == null)
            {
                var obj = new GameObject("HallOfFallenUI");
                Instance = obj.AddComponent<HallOfFallenUI>();
            }
            Instance.Show(manager);
        }

        private void Show(GameplayManager manager)
        {
            _manager = manager;
            if (_window == null) CreateUI();
            if (_modalBlocker != null) _modalBlocker.SetActive(true);
            _window.SetActive(true);
            if (_modalBlocker != null) _modalBlocker.transform.SetAsLastSibling();
            _window.transform.SetAsLastSibling();
            Refresh();
        }

        private void CreateUI()
        {
            // Modal blocker
            _modalBlocker = new GameObject("FallenModalBlocker", typeof(RectTransform));
            _modalBlocker.transform.SetParent(_manager.canvasTransform, false);
            var blockerRect = _modalBlocker.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.sizeDelta = Vector2.zero;
            var blockerImg = _modalBlocker.AddComponent<Image>();
            blockerImg.color = new Color(0, 0, 0, 0.5f);
            var blockerBtn = _modalBlocker.AddComponent<Button>();
            blockerBtn.onClick.AddListener(() =>
            {
                _window.SetActive(false);
                _modalBlocker.SetActive(false);
            });

            // Main window
            _window = new GameObject("HallOfFallenWindow", typeof(RectTransform));
            _window.transform.SetParent(_manager.canvasTransform, false);
            var rect = _window.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(460, 620);
            rect.anchoredPosition = Vector2.zero;

            var bg = _window.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.04f, 0.06f, 0.97f);

            // Title bar
            var titleGO = new GameObject("TitleBar", typeof(RectTransform));
            titleGO.transform.SetParent(_window.transform, false);
            var tr = titleGO.GetComponent<RectTransform>();
            tr.sizeDelta = new Vector2(460, 44);
            tr.anchoredPosition = new Vector2(0, 288);
            var titleBg = titleGO.AddComponent<Image>();
            titleBg.color = new Color(0.25f, 0.05f, 0.05f, 1f);

            var titleTxt = titleGO.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "⚔  Hall of the Fallen  ⚔";
            titleTxt.fontSize = 14;
            titleTxt.color = new Color(0.9f, 0.75f, 0.75f);
            titleTxt.alignment = TextAlignmentOptions.Center;
            var ttRect = titleGO.GetComponent<RectTransform>();
            ttRect.sizeDelta = new Vector2(460, 44);

            // Close button
            var closeGO = new GameObject("CloseBtn", typeof(RectTransform));
            closeGO.transform.SetParent(_window.transform, false);
            var cr = closeGO.GetComponent<RectTransform>();
            cr.sizeDelta = new Vector2(30, 30);
            cr.anchoredPosition = new Vector2(214, 288);
            var closeBg = closeGO.AddComponent<Image>();
            closeBg.color = new Color(0.5f, 0.1f, 0.1f, 1f);
            var closeBtn = closeGO.AddComponent<Button>();
            closeBtn.onClick.AddListener(() => { _window.SetActive(false); _modalBlocker.SetActive(false); });
            var closeTxt = new GameObject("X", typeof(RectTransform));
            closeTxt.transform.SetParent(closeGO.transform, false);
            var ctr = closeTxt.GetComponent<RectTransform>();
            ctr.anchorMin = Vector2.zero; ctr.anchorMax = Vector2.one; ctr.sizeDelta = Vector2.zero;
            var ctxt = closeTxt.AddComponent<TextMeshProUGUI>();
            ctxt.text = "X"; ctxt.fontSize = 12; ctxt.color = Color.white;
            ctxt.alignment = TextAlignmentOptions.Center;

            // Scroll view
            var scrollGO = new GameObject("FallenScroll", typeof(RectTransform));
            scrollGO.transform.SetParent(_window.transform, false);
            var scrollR = scrollGO.GetComponent<RectTransform>();
            scrollR.sizeDelta = new Vector2(440, 555);
            scrollR.anchoredPosition = new Vector2(0, -25);
            var scrollComp = scrollGO.AddComponent<ScrollRect>();
            scrollGO.AddComponent<Image>().color = Color.clear;

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(scrollGO.transform, false);
            var vpR = viewport.GetComponent<RectTransform>();
            vpR.anchorMin = Vector2.zero; vpR.anchorMax = Vector2.one;
            vpR.sizeDelta = Vector2.zero; vpR.anchoredPosition = Vector2.zero;
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            viewport.AddComponent<Image>().color = Color.clear;

            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(viewport.transform, false);
            _scrollContent = contentGO.transform;
            var contR = contentGO.GetComponent<RectTransform>();
            contR.anchorMin = new Vector2(0, 1); contR.anchorMax = new Vector2(1, 1);
            contR.pivot = new Vector2(0.5f, 1f);
            contR.sizeDelta = Vector2.zero; contR.anchoredPosition = Vector2.zero;
            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.spacing = 8;
            vlg.childControlWidth = true; vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            contentGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollComp.content = contR;
            scrollComp.viewport = vpR;
            scrollComp.horizontal = false;
            scrollComp.vertical = true;
            scrollComp.scrollSensitivity = 30f;
        }

        public void Refresh()
        {
            if (_scrollContent == null) return;
            foreach (Transform child in _scrollContent)
                Destroy(child.gameObject);

            var fallen = NPCData.LoreCache
                .Where(kvp => kvp.Value != null && kvp.Value.IsDeceased)
                .OrderBy(kvp => kvp.Value.Name)
                .ToList();

            if (fallen.Count == 0)
            {
                AddText("No souls have fallen yet.", 12, new Color(0.6f, 0.6f, 0.6f));
                return;
            }

            AddText($"{fallen.Count} soul{(fallen.Count == 1 ? "" : "s")} remembered.", 11,
                new Color(0.6f, 0.5f, 0.5f));

            foreach (var kvp in fallen)
            {
                var data = kvp.Value;
                AddEntry(data);
            }
        }

        private void AddEntry(NPCData data)
        {
            var card = new GameObject("FallenCard", typeof(RectTransform));
            card.transform.SetParent(_scrollContent, false);
            card.AddComponent<Image>().color = new Color(0.10f, 0.06f, 0.08f, 0.9f);
            var vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 3;
            vlg.childControlWidth = true; vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            card.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Name
            string nameTag = data.IsNemesis ? " <color=#ff4444>[NEMESIS]</color>" : "";
            AddCardLine(card.transform, $"<b>{data.Name}</b>{nameTag}", 13, new Color(0.9f, 0.7f, 0.7f));

            // Death info
            if (!string.IsNullOrEmpty(data.DeathInfo))
                AddCardLine(card.transform, data.DeathInfo, 10, new Color(0.65f, 0.55f, 0.55f));

            // Epitaph (italic)
            if (!string.IsNullOrEmpty(data.Epitaph))
                AddCardLine(card.transform, $"<i>\"{data.Epitaph}\"</i>", 10, new Color(0.75f, 0.65f, 0.75f));

            // Personality snippet
            if (!string.IsNullOrEmpty(data.Personality))
            {
                string snippet = data.Personality.Length > 80
                    ? data.Personality.Substring(0, 80) + "..."
                    : data.Personality;
                AddCardLine(card.transform, snippet, 9, new Color(0.5f, 0.5f, 0.55f));
            }
        }

        private void AddText(string text, int size, Color color)
        {
            var go = new GameObject("InfoText", typeof(RectTransform));
            go.transform.SetParent(_scrollContent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text; txt.fontSize = size; txt.color = color;
            txt.alignment = TextAlignmentOptions.Center; txt.enableWordWrapping = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = size * 2f; le.flexibleWidth = 1;
        }

        private void AddCardLine(Transform parent, string text, int size, Color color)
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text; txt.fontSize = size; txt.color = color;
            txt.enableWordWrapping = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = size * 1.85f; le.flexibleWidth = 1;
        }
    }
}
