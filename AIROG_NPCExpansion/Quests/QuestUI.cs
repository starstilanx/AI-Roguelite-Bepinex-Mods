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
            _manager = manager;

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
                _window.transform.SetParent(manager.canvasTransform, false);
                if (_modalBlocker != null) _modalBlocker.transform.SetParent(manager.canvasTransform, false);
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
            var vpMask = viewport.AddComponent<Mask>();
            vpMask.showMaskGraphic = false;
            viewport.AddComponent<Image>().color = Color.clear;

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
                AddSectionHeader("No quests yet.");
                return;
            }

            var active = quests.Where(q => q.Status == QuestStatus.Active).ToList();
            var completed = quests.Where(q => q.Status == QuestStatus.Completed).ToList();
            var failed = quests.Where(q => q.Status == QuestStatus.Failed).ToList();

            if (active.Count > 0)
            {
                AddSectionHeader("── Active Quests ──");
                foreach (var q in active) AddQuestEntry(q);
            }
            if (completed.Count > 0)
            {
                AddSectionHeader("── Completed ──");
                foreach (var q in completed) AddQuestEntry(q);
            }
            if (failed.Count > 0)
            {
                AddSectionHeader("── Failed ──");
                foreach (var q in failed) AddQuestEntry(q);
            }

            // Force immediate layout recalculation so entries are visible on first open
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(
                _scrollContent.GetComponent<RectTransform>());
        }

        private void AddSectionHeader(string text)
        {
            var go = new GameObject("Header", typeof(RectTransform));
            go.transform.SetParent(_scrollContent, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 22);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = 12;
            txt.color = new Color(0.8f, 0.7f, 0.3f, 1f);
            txt.fontStyle = FontStyles.Bold;
            txt.alignment = TextAlignmentOptions.Center;
        }

        private void AddQuestEntry(QuestData quest)
        {
            var card = new GameObject("QuestCard", typeof(RectTransform));
            card.transform.SetParent(_scrollContent, false);
            var cardRect = card.GetComponent<RectTransform>();
            cardRect.sizeDelta = new Vector2(0, 80);

            var cardBg = card.AddComponent<Image>();
            cardBg.color = quest.Status switch
            {
                QuestStatus.Completed => new Color(0.05f, 0.15f, 0.05f, 0.9f),
                QuestStatus.Failed    => new Color(0.15f, 0.05f, 0.05f, 0.9f),
                _                     => new Color(0.08f, 0.08f, 0.15f, 0.9f)
            };

            var vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 6, 6);
            vlg.spacing = 3;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            card.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Giver + status badge
            string statusColor = quest.Status switch
            {
                QuestStatus.Completed => "#55ff55",
                QuestStatus.Failed    => "#ff5555",
                _                     => "#ffd700"
            };
            string statusLabel = quest.Status switch
            {
                QuestStatus.Completed => "[DONE]",
                QuestStatus.Failed    => "[FAILED]",
                _                     => "[ACTIVE]"
            };
            AddCardLine(card.transform,
                $"<color={statusColor}>{statusLabel}</color> <b>{quest.GiverName}</b>", 11);

            AddCardLine(card.transform, quest.ObjectiveText, 10);

            if (!string.IsNullOrEmpty(quest.CompletionCondition))
                AddCardLine(card.transform, $"<color=#aaaaaa>Condition: {quest.CompletionCondition}</color>", 9);

            if (!string.IsNullOrEmpty(quest.RewardText) || quest.RewardGold > 0)
            {
                string reward = quest.RewardText ?? "";
                if (quest.RewardGold > 0) reward += $" (+{quest.RewardGold}g)";
                AddCardLine(card.transform, $"<color=#aaaaff>Reward: {reward.Trim()}</color>", 9);
            }
        }

        private void AddCardLine(Transform parent, string text, int fontSize)
        {
            var go = new GameObject("Line", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var txt = go.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = fontSize;
            txt.color = Color.white;
            txt.enableWordWrapping = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = fontSize * 1.8f;
            le.flexibleWidth = 1;
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
