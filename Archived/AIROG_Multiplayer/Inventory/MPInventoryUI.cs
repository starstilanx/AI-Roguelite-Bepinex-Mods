using System;
using System.Collections.Generic;
using System.Text;
using AIROG_Multiplayer.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// Floating inventory panel that shows all players' items.
    /// Attaches to the same DontDestroyOnLoad root as CoopStatusOverlay (shares its Canvas).
    ///
    /// Usage:
    ///   MPInventoryUI.GetOrCreate(coopOverlayRootGO);  // attach once
    ///   MPInventoryUI.Instance?.Toggle();               // show/hide
    ///   MPInventoryUI.Instance?.Refresh();              // redraw from MPInventoryManager.Database
    /// </summary>
    public class MPInventoryUI : MonoBehaviour
    {
        public static MPInventoryUI Instance { get; private set; }

        private GameObject _panel;
        private TMP_Text _contentText;
        private ScrollRect _scrollRect;
        private bool _isVisible;

        private MPPaperDollUI _paperDollUI;
        private MPGiftUI _giftUI;

        // ---- Factory ----

        public static MPInventoryUI GetOrCreate(GameObject canvasRoot)
        {
            if (Instance != null) return Instance;

            var go = new GameObject("AIROG_InventoryUI");
            go.transform.SetParent(canvasRoot.transform, false);
            Instance = go.AddComponent<MPInventoryUI>();
            Instance.BuildPanel();

            MPInventoryManager.OnInventoryChanged += () => Instance?.Refresh();
            return Instance;
        }

        // ---- Public API ----

        public void Toggle()
        {
            _isVisible = !_isVisible;
            if (_panel != null) _panel.SetActive(_isVisible);
            if (_isVisible) Refresh();
        }

        public void Show()
        {
            _isVisible = true;
            if (_panel != null) _panel.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            _isVisible = false;
            if (_panel != null) _panel.SetActive(false);
        }

        /// <summary>Rebuilds the displayed text from the current MPInventoryManager.Database.</summary>
        public void Refresh()
        {
            if (_contentText == null) return;
            try
            {
                _contentText.text = BuildInventoryText();
                Canvas.ForceUpdateCanvases();
                if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[MPInventoryUI] Refresh error: {ex.Message}");
            }
        }

        // ---- Unity ----

        private void OnDestroy()
        {
            MPInventoryManager.OnInventoryChanged -= Refresh;
            if (Instance == this) Instance = null;
        }

        // ---- UI Builder ----

        private void BuildPanel()
        {
            // Outer panel — centered, 680×520 in the 1920×1080 reference space
            _panel = new GameObject("InvPanel");
            _panel.transform.SetParent(transform, false);
            _panel.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.95f);

            var panelRT = _panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot     = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(680f, 520f);
            panelRT.anchoredPosition = Vector2.zero;

            // Left accent bar
            var accent = new GameObject("Accent");
            accent.transform.SetParent(_panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.25f, 0.7f, 0.35f, 0.9f);
            var aRT = accent.GetComponent<RectTransform>();
            aRT.anchorMin = new Vector2(0f, 0f);
            aRT.anchorMax = new Vector2(0f, 1f);
            aRT.pivot     = new Vector2(0f, 0.5f);
            aRT.sizeDelta = new Vector2(3f, 0f);
            aRT.anchoredPosition = Vector2.zero;

            // Root vertical layout
            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 6f;

            // --- Header row ---
            var headerRow = MakeHRow(_panel.transform, "Header", 26f);

            var title = new GameObject("Title");
            title.transform.SetParent(headerRow.transform, false);
            var titleLE = title.AddComponent<LayoutElement>();
            titleLE.flexibleWidth = 1f;
            var titleTxt = title.AddComponent<TextMeshProUGUI>();
            titleTxt.text      = "Party Inventory";
            titleTxt.fontSize  = 14f;
            titleTxt.fontStyle = FontStyles.Bold;
            titleTxt.color     = Color.white;
            titleTxt.alignment = TextAlignmentOptions.MidlineLeft;

            MakeSmallButton(headerRow.transform, "✕ Close", new Color(0.5f, 0.12f, 0.12f), () => Hide());

            // --- Divider ---
            AddDivider(_panel.transform);

            // --- Scrollable content area ---
            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollLE = scrollGo.AddComponent<LayoutElement>();
            scrollLE.flexibleHeight = 1f;
            scrollLE.flexibleWidth  = 1f;
            scrollGo.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.08f, 0f);
            scrollGo.AddComponent<RectMask2D>();

            _scrollRect              = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal   = false;
            _scrollRect.scrollSensitivity = 30f;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRT = contentGo.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0f, 1f);
            contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot     = new Vector2(0.5f, 1f);
            contentRT.sizeDelta = Vector2.zero;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _contentText = contentGo.AddComponent<TextMeshProUGUI>();
            _contentText.fontSize      = 10.5f;
            _contentText.color         = new Color(0.88f, 0.88f, 0.92f);
            _contentText.alignment     = TextAlignmentOptions.TopLeft;
            _contentText.enableWordWrapping = true;
            _contentText.overflowMode  = TextOverflowModes.Overflow;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scrollRect.content = contentRT;

            // --- Divider ---
            AddDivider(_panel.transform);

            // --- Footer buttons (host only: Refresh) ---
            var footerRow = MakeHRow(_panel.transform, "Footer", 26f);

            var spacer = new GameObject("Spacer");
            spacer.transform.SetParent(footerRow.transform, false);
            spacer.AddComponent<LayoutElement>().flexibleWidth = 1f;

            MakeSmallButton(footerRow.transform, "↺ Refresh", new Color(0.2f, 0.4f, 0.65f), () =>
            {
                if (MultiplayerPlugin.IsHost)
                {
                    var manager = SS.I?.hackyManager;
                    if (manager != null)
                    {
                        MPInventoryManager.SyncHostFromGame(manager);
                        MPInventoryManager.Save();
                        MultiplayerPlugin.BroadcastInventory();
                    }
                }
                Refresh();
            });

            MakeSmallButton(footerRow.transform, "👤 Doll", new Color(0.35f, 0.18f, 0.55f), () =>
            {
                if (_paperDollUI == null)
                    _paperDollUI = MPPaperDollUI.GetOrCreate(gameObject);
                _paperDollUI.Toggle();
            });

            MakeSmallButton(footerRow.transform, "🎁 Gift", new Color(0.55f, 0.3f, 0.08f), () =>
            {
                if (_giftUI == null)
                    _giftUI = MPGiftUI.GetOrCreate(gameObject);
                _giftUI.Toggle();
            });

            _panel.SetActive(false);
        }

        // ---- Content building ----

        private static string BuildInventoryText()
        {
            var db = MPInventoryManager.Database;
            if (db.Players.Count == 0)
                return "<color=#888888><i>No inventory data yet. It syncs after each turn.</i></color>";

            var sb = new StringBuilder();
            bool first = true;
            foreach (var kvp in db.Players)
            {
                var inv = kvp.Value;
                if (!first) sb.AppendLine();
                first = false;

                // Player header
                sb.AppendLine($"<color=#88ccff><b>{Esc(inv.CharacterName)}</b></color>" +
                              $"<color=#aaaaaa>  ({Esc(inv.PlayerId)})</color>");
                sb.AppendLine($"  <color=#ffd866>Gold: {inv.Gold:N0}</color>   " +
                              $"<color=#aaaaaa>{inv.Items.Count} item(s)</color>");

                if (inv.Items.Count == 0)
                {
                    sb.AppendLine("  <color=#666666><i>— empty —</i></color>");
                    continue;
                }

                foreach (var item in inv.Items)
                {
                    string qualColor = QualityColor(item.Quality);
                    string equippedTag = item.IsEquipped ? " <color=#88ff88>[E]</color>" : "";
                    string goldTag = item.GoldValue >= 0
                        ? $" <color=#ffd866>({item.GoldValue}g)</color>"
                        : "";
                    sb.Append($"  <color={qualColor}>{Esc(item.Name)}</color>");
                    sb.Append($" <color=#777777>[{item.ItemType}]</color>");
                    sb.Append(equippedTag);
                    sb.AppendLine(goldTag);
                    if (!string.IsNullOrEmpty(item.Description))
                    {
                        string desc = item.Description.Length > 120
                            ? item.Description.Substring(0, 120) + "…"
                            : item.Description;
                        sb.AppendLine($"    <color=#666666><i>{Esc(desc)}</i></color>");
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string QualityColor(string quality)
        {
            switch (quality)
            {
                case "Legendary": return "#ff9500";
                case "Epic":      return "#cc66ff";
                case "Rare":      return "#3399ff";
                case "Uncommon":  return "#55cc55";
                case "Mundane":
                case "Trash":     return "#888888";
                default:          return "#cccccc"; // Common
            }
        }

        private static string Esc(string s) =>
            s?.Replace("<", "\u003c").Replace(">", "\u003e") ?? "";

        // ---- UI helpers (mirrors ClientHUD patterns) ----

        private static GameObject MakeHRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth   = 1f;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandHeight = true;
            hlg.spacing = 6f;
            return go;
        }

        private static void AddDivider(Transform parent)
        {
            var go = new GameObject("Divider");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.3f, 0.35f, 0.45f, 0.5f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 1f;
            le.flexibleWidth   = 1f;
        }

        private static void MakeSmallButton(Transform parent, string label, Color color, Action onClick)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth  = 80f;
            le.preferredHeight = 24f;
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            var txtGo = new GameObject("Label");
            txtGo.transform.SetParent(go.transform, false);
            var t = txtGo.AddComponent<TextMeshProUGUI>();
            t.text = label; t.fontSize = 10f; t.fontStyle = FontStyles.Bold;
            t.color = Color.white; t.alignment = TextAlignmentOptions.Center;
            var tRT = txtGo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero; tRT.offsetMax = Vector2.zero;
        }
    }
}
