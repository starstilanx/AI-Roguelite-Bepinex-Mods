using System;
using System.Text;
using AIROG_Multiplayer.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// Map/location awareness overlay — shows current location details, NPCs, enemies, etc.
    /// Follows the MPInventoryUI pattern: GetOrCreate / Toggle / Refresh.
    /// </summary>
    public class MPMapOverlay : MonoBehaviour
    {
        public static MPMapOverlay Instance { get; private set; }

        private GameObject _panel;
        private TMP_Text _contentText;
        private ScrollRect _scrollRect;
        private bool _isVisible;

        // Cached location data from the last update
        private LocationUpdatePayload _location;

        // ---- Factory ----

        public static MPMapOverlay GetOrCreate(GameObject canvasRoot)
        {
            if (Instance != null) return Instance;

            var go = new GameObject("AIROG_MapOverlay");
            go.transform.SetParent(canvasRoot.transform, false);
            Instance = go.AddComponent<MPMapOverlay>();
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

        public void UpdateLocation(LocationUpdatePayload location)
        {
            _location = location;
            if (_isVisible) Refresh();
        }

        public void Refresh()
        {
            if (_contentText == null) return;
            try
            {
                _contentText.text = BuildLocationText();
                Canvas.ForceUpdateCanvases();
                if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[MPMapOverlay] Refresh error: {ex.Message}");
            }
        }

        // ---- Unity ----

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- Text Builder ----

        private string BuildLocationText()
        {
            if (_location == null)
                return "<color=#888>No location data yet.\nThe host's location will appear here when you join a game.</color>";

            var sb = new StringBuilder();

            // Location name and description
            sb.AppendLine($"<color=#FFD700><b>{_location.LocationName ?? "Unknown"}</b></color>");
            if (!string.IsNullOrEmpty(_location.ParentLocationName))
                sb.AppendLine($"<color=#888888>Region: {_location.ParentLocationName}</color>");
            if (_location.DangerLevel >= 0)
            {
                string dangerColor = _location.DangerLevel <= 2 ? "#66CC66" :
                                     _location.DangerLevel <= 5 ? "#FFD700" : "#CC4444";
                sb.AppendLine($"<color={dangerColor}>Danger Level: {_location.DangerLevel}</color>");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(_location.LocationDescription))
            {
                sb.AppendLine($"<color=#BBBBBB>{_location.LocationDescription}</color>");
                sb.AppendLine();
            }

            // NPCs
            if (_location.NPCNames != null && _location.NPCNames.Length > 0)
            {
                sb.AppendLine("<color=#88CCFF><b>NPCs Present</b></color>");
                foreach (var npc in _location.NPCNames)
                    sb.AppendLine($"  <color=#AADDFF>• {npc}</color>");
                sb.AppendLine();
            }

            // Enemies
            if (_location.EnemyNames != null && _location.EnemyNames.Length > 0)
            {
                sb.AppendLine("<color=#FF6666><b>Enemies</b></color>");
                foreach (var enemy in _location.EnemyNames)
                    sb.AppendLine($"  <color=#FF8888>• {enemy}</color>");
                sb.AppendLine();
            }

            // Items/Things
            if (_location.ThingNames != null && _location.ThingNames.Length > 0)
            {
                sb.AppendLine("<color=#AAFFAA><b>Items & Objects</b></color>");
                foreach (var thing in _location.ThingNames)
                    sb.AppendLine($"  <color=#BBFFBB>• {thing}</color>");
                sb.AppendLine();
            }

            // Adjacent locations
            if (_location.AdjacentLocationNames != null && _location.AdjacentLocationNames.Length > 0)
            {
                sb.AppendLine("<color=#CCCCFF><b>Connected Areas</b></color>");
                foreach (var loc in _location.AdjacentLocationNames)
                    sb.AppendLine($"  <color=#DDDDFF>→ {loc}</color>");
            }

            return sb.ToString().TrimEnd();
        }

        // ---- UI Builder ----

        private void BuildPanel()
        {
            _panel = new GameObject("MapPanel");
            _panel.transform.SetParent(transform, false);
            _panel.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.95f);

            var panelRT = _panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(480f, 440f);
            panelRT.anchoredPosition = Vector2.zero;

            // Left accent bar
            var accent = new GameObject("Accent");
            accent.transform.SetParent(_panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.3f, 0.5f, 0.9f, 0.9f);
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
            titleTxt.text = "Location Overview";
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

        // ---- UI Helpers ----

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
