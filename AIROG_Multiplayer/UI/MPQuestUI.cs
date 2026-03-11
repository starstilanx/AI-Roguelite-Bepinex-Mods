using System;
using System.Text;
using AIROG_Multiplayer.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// Floating quest panel that shows the current quest state synced from the host.
    /// Follows the same pattern as MPInventoryUI: GetOrCreate / Toggle / Refresh.
    /// </summary>
    public class MPQuestUI : MonoBehaviour
    {
        public static MPQuestUI Instance { get; private set; }

        private GameObject _panel;
        private TMP_Text _contentText;
        private ScrollRect _scrollRect;
        private bool _isVisible;

        // Cached quest data from the last sync
        private MPQuestInfo[] _quests;

        // ---- Factory ----

        public static MPQuestUI GetOrCreate(GameObject canvasRoot)
        {
            if (Instance != null) return Instance;

            var go = new GameObject("AIROG_QuestUI");
            go.transform.SetParent(canvasRoot.transform, false);
            Instance = go.AddComponent<MPQuestUI>();
            Instance.BuildPanel();
            return Instance;
        }

        // ---- Public API ----

        public void Toggle()
        {
            _isVisible = !_isVisible;
            if (_panel != null) _panel.SetActive(_isVisible);
            if (_isVisible) Refresh();
        }

        public void UpdateQuests(MPQuestInfo[] quests)
        {
            _quests = quests;
            if (_isVisible) Refresh();
        }

        public void Refresh()
        {
            if (_contentText == null) return;
            try
            {
                _contentText.text = BuildQuestText();
                Canvas.ForceUpdateCanvases();
                if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[MPQuestUI] Refresh error: {ex.Message}");
            }
        }

        // ---- Unity ----

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- Text Builder ----

        private string BuildQuestText()
        {
            if (_quests == null || _quests.Length == 0)
                return "<color=#888>No quests available yet.\nThe host's quest log will appear here after the next turn.</color>";

            var sb = new StringBuilder();

            // Active quests first
            bool hasActive = false;
            foreach (var q in _quests)
            {
                if (q.Status != "Active") continue;
                if (!hasActive)
                {
                    sb.AppendLine("<color=#FFD700><b>Active Quests</b></color>");
                    sb.AppendLine();
                    hasActive = true;
                }
                AppendQuest(sb, q);
            }

            // Completed
            bool hasCompleted = false;
            foreach (var q in _quests)
            {
                if (q.Status != "Completed") continue;
                if (!hasCompleted)
                {
                    if (hasActive) sb.AppendLine();
                    sb.AppendLine("<color=#66CC66><b>Completed Quests</b></color>");
                    sb.AppendLine();
                    hasCompleted = true;
                }
                AppendQuest(sb, q);
            }

            // Failed
            bool hasFailed = false;
            foreach (var q in _quests)
            {
                if (q.Status != "Failed") continue;
                if (!hasFailed)
                {
                    sb.AppendLine();
                    sb.AppendLine("<color=#CC4444><b>Failed Quests</b></color>");
                    sb.AppendLine();
                    hasFailed = true;
                }
                AppendQuest(sb, q);
            }

            return sb.ToString().TrimEnd();
        }

        private void AppendQuest(StringBuilder sb, MPQuestInfo q)
        {
            string typeTag = q.QuestType == "Main" ? "<color=#FFD700>[Main]</color>" : "<color=#88AAFF>[Side]</color>";
            string statusColor = q.Status == "Active" ? "#FFFFFF" : q.Status == "Completed" ? "#66CC66" : "#CC4444";

            sb.AppendLine($"  {typeTag} <color={statusColor}><b>{q.Title ?? "Untitled"}</b></color>");
            if (!string.IsNullOrEmpty(q.Objective))
                sb.AppendLine($"    <color=#BBBBBB>{q.Objective}</color>");
            if (!string.IsNullOrEmpty(q.GiverName))
                sb.AppendLine($"    <color=#888888>From: {q.GiverName}</color>");
            sb.AppendLine();
        }

        // ---- UI Builder ----

        private void BuildPanel()
        {
            _panel = new GameObject("QuestPanel");
            _panel.transform.SetParent(transform, false);
            _panel.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.95f);

            var panelRT = _panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(550f, 480f);
            panelRT.anchoredPosition = Vector2.zero;

            // Left accent bar
            var accent = new GameObject("Accent");
            accent.transform.SetParent(_panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.8f, 0.65f, 0.2f, 0.9f);
            var aRT = accent.GetComponent<RectTransform>();
            aRT.anchorMin = new Vector2(0f, 0f);
            aRT.anchorMax = new Vector2(0f, 1f);
            aRT.pivot = new Vector2(0f, 0.5f);
            aRT.sizeDelta = new Vector2(3f, 0f);
            aRT.anchoredPosition = Vector2.zero;

            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 6f;

            // Header
            var headerRow = MakeHRow(_panel.transform, "Header", 26f);

            var title = new GameObject("Title");
            title.transform.SetParent(headerRow.transform, false);
            title.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var titleTxt = title.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "Quest Log";
            titleTxt.fontSize = 14f;
            titleTxt.fontStyle = FontStyles.Bold;
            titleTxt.color = Color.white;
            titleTxt.alignment = TextAlignmentOptions.MidlineLeft;

            MakeSmallButton(headerRow.transform, "✕ Close", new Color(0.5f, 0.12f, 0.12f), () =>
            {
                _isVisible = false;
                _panel.SetActive(false);
            });

            // Divider
            AddDivider(_panel.transform);

            // Scroll area
            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollLE = scrollGo.AddComponent<LayoutElement>();
            scrollLE.flexibleHeight = 1f;
            scrollLE.flexibleWidth = 1f;
            scrollGo.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.08f, 0f);
            scrollGo.AddComponent<RectMask2D>();

            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.scrollSensitivity = 30f;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRT = contentGo.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0f, 1f);
            contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.sizeDelta = Vector2.zero;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _contentText = contentGo.AddComponent<TextMeshProUGUI>();
            _contentText.fontSize = 11f;
            _contentText.color = new Color(0.88f, 0.88f, 0.92f);
            _contentText.alignment = TextAlignmentOptions.TopLeft;
            _contentText.enableWordWrapping = true;
            _contentText.overflowMode = TextOverflowModes.Overflow;
            _contentText.richText = true;

            _scrollRect.content = contentRT;

            _panel.SetActive(false);
        }

        // ---- UI Helpers (same pattern as MPInventoryUI) ----

        private GameObject MakeHRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.spacing = 6f;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            return go;
        }

        private void MakeSmallButton(Transform parent, string label, Color bgColor, Action onClick)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = bgColor;
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 70f;
            le.preferredHeight = 22f;

            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            var txtGo = new GameObject("Lbl");
            txtGo.transform.SetParent(go.transform, false);
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = label;
            txt.fontSize = 10f;
            txt.fontStyle = FontStyles.Bold;
            txt.color = Color.white;
            txt.alignment = TextAlignmentOptions.Center;
            var tRT = txtGo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero;
            tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;
        }

        private void AddDivider(Transform parent)
        {
            var div = new GameObject("Divider");
            div.transform.SetParent(parent, false);
            div.AddComponent<Image>().color = new Color(0.3f, 0.3f, 0.4f, 0.5f);
            var le = div.AddComponent<LayoutElement>();
            le.preferredHeight = 1f;
        }
    }
}
