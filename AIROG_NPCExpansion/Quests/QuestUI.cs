using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Scrollable quest log panel showing Active / Completed / Failed quests.
    /// Opened via the "Quests" button added to NPC action menus or the main mod menu.
    /// </summary>
    public class QuestUI : MonoBehaviour
    {
        public static QuestUI Instance { get; private set; }

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
                var obj = new GameObject("QuestUI");
                Instance = obj.AddComponent<QuestUI>();
            }
        }

        public static void Open(GameplayManager manager)
        {
            if (Instance == null)
            {
                var obj = new GameObject("QuestUI");
                Instance = obj.AddComponent<QuestUI>();
            }
            Instance.Show(manager);
        }

        private void Show(GameplayManager manager)
        {
            if (!NPCUI.TryResolveManager(manager, "QuestUI", out _manager)) return;

            // Rebuild UI if stale (e.g. scene reload destroyed scroll content but not the window)
            if (_window == null || _scrollContent == null)
            {
                if (_window != null) { Destroy(_window); _window = null; }
                if (_modalBlocker != null) { Destroy(_modalBlocker); _modalBlocker = null; }
                _scrollContent = null;
                CreateUI();
            }
            else
            {
                // Re-parent to the current manager's canvas (handles manager changes between opens)
                _window.transform.SetParent(_manager.canvasTransform, false);
                if (_modalBlocker != null) _modalBlocker.transform.SetParent(_manager.canvasTransform, false);
            }

            if (_modalBlocker != null) _modalBlocker.SetActive(true);
            _window.SetActive(true);
            _window.transform.SetAsLastSibling();
            if (_modalBlocker != null) _modalBlocker.transform.SetAsLastSibling();
            _window.transform.SetAsLastSibling();
            Refresh();
        }

        private void CreateUI()
        {
            // Modal blocker
            _modalBlocker = new GameObject("QuestModalBlocker", typeof(RectTransform));
            _modalBlocker.transform.SetParent(_manager.canvasTransform, false);
            var blockerRect = _modalBlocker.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.sizeDelta = Vector2.zero;
            var blockerImg = _modalBlocker.AddComponent<Image>();
            blockerImg.color = new Color(0, 0, 0, 0.45f);
            var blockerBtn = _modalBlocker.AddComponent<Button>();
            blockerBtn.onClick.AddListener(() =>
            {
                _window.SetActive(false);
                _modalBlocker.SetActive(false);
            });

            // Main window
            _window = new GameObject("QuestWindow", typeof(RectTransform));
            _window.transform.SetParent(_manager.canvasTransform, false);
            var rect = _window.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(480, 650);
            rect.anchoredPosition = Vector2.zero;

            // Background
            var bg = _window.AddComponent<Image>();
            bg.color = new Color(0.06f, 0.06f, 0.10f, 0.97f);

            // Title bar
            var titleBar = CreatePanel(_window.transform, new Vector2(480, 44), new Vector2(0, 303));
            var titleBg = titleBar.AddComponent<Image>();
            titleBg.color = new Color(0.55f, 0.45f, 0.05f, 1f);

            var titleTxt = CreateText(titleBar.transform, "Quest Log", 16, TextAlignmentOptions.Center);
            var titleRect = titleTxt.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.sizeDelta = Vector2.zero;
            titleRect.anchoredPosition = Vector2.zero;

            // Close button
            var closeBtn = CreateButton(_window.transform, "X", new Vector2(30, 30), new Vector2(229, 303),
                new Color(0.6f, 0.1f, 0.1f, 1f));
            closeBtn.onClick.AddListener(() => { _window.SetActive(false); _modalBlocker.SetActive(false); });

            // Scroll view
            var scrollGO = new GameObject("QuestScroll", typeof(RectTransform));
            scrollGO.transform.SetParent(_window.transform, false);
            var scrollRect2 = scrollGO.GetComponent<RectTransform>();
            scrollRect2.sizeDelta = new Vector2(460, 580);
            scrollRect2.anchoredPosition = new Vector2(0, -24);

            var scrollComp = scrollGO.AddComponent<ScrollRect>();
            var scrollImg = scrollGO.AddComponent<Image>();
            scrollImg.color = new Color(0, 0, 0, 0);

            // Viewport
            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(scrollGO.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.sizeDelta = Vector2.zero;
            vpRect.anchoredPosition = Vector2.zero;
            // RectMask2D, NOT Mask: a Mask driven by a fully transparent Image renders no
            // geometry (cullTransparentMesh), writes no stencil, and hides ALL children.
            viewport.AddComponent<RectMask2D>();
            viewport.AddComponent<Image>().color = Color.clear; // raycast target for scroll drag

            // Content
            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(viewport.transform, false);
            _scrollContent = contentGO.transform;
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0, 0);
            contentRect.anchoredPosition = Vector2.zero;
            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.spacing = 6;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var csf = contentGO.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollComp.content = contentRect;
            scrollComp.viewport = vpRect;
            scrollComp.horizontal = false;
            scrollComp.vertical = true;
            scrollComp.scrollSensitivity = 30f;
        }

        public void Refresh()
        {
            if (_scrollContent == null) return;
            foreach (Transform child in _scrollContent)
                Destroy(child.gameObject);

            var quests = QuestManager.AllQuests;
            if (quests.Count == 0)
            {
                QuestEntryRenderer.AddSectionHeader(_scrollContent, "No quests yet.");
                return;
            }

            var active = quests.Where(q => q.Status == QuestStatus.Active).ToList();
            var completed = quests.Where(q => q.Status == QuestStatus.Completed).ToList();
            var failed = quests.Where(q => q.Status == QuestStatus.Failed).ToList();

            if (active.Count > 0)
            {
                QuestEntryRenderer.AddSectionHeader(_scrollContent, "── Active Quests ──");
                foreach (var q in active) QuestEntryRenderer.AddQuestEntry(_scrollContent, q);
            }
            if (completed.Count > 0)
            {
                QuestEntryRenderer.AddSectionHeader(_scrollContent, "── Completed ──");
                foreach (var q in completed) QuestEntryRenderer.AddQuestEntry(_scrollContent, q);
            }
            if (failed.Count > 0)
            {
                QuestEntryRenderer.AddSectionHeader(_scrollContent, "── Failed ──");
                foreach (var q in failed) QuestEntryRenderer.AddQuestEntry(_scrollContent, q);
            }

            // Force immediate layout recalculation so entries are visible on first open
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(
                _scrollContent.GetComponent<RectTransform>());
        }

        // ─── UI helpers ────────────────────────────────────────────────────────────

        private static TextMeshProUGUI CreateText(Transform parent, string text, int size, TextAlignmentOptions align)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = size;
            txt.color = Color.white;
            txt.alignment = align;
            return txt;
        }

        private static Button CreateButton(Transform parent, string label, Vector2 size, Vector2 pos, Color bgColor)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = size;
            r.anchoredPosition = pos;
            var img = go.AddComponent<Image>();
            img.color = bgColor;
            var btn = go.AddComponent<Button>();
            var lbl = new GameObject("Label", typeof(RectTransform));
            lbl.transform.SetParent(go.transform, false);
            var lr = lbl.GetComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.sizeDelta = Vector2.zero;
            var txt = lbl.AddComponent<TextMeshProUGUI>();
            txt.text = label; txt.fontSize = 11; txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Center;
            return btn;
        }

        private static GameObject CreatePanel(Transform parent, Vector2 size, Vector2 pos)
        {
            var go = new GameObject("Panel", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = size;
            r.anchoredPosition = pos;
            return go;
        }
    }
}
