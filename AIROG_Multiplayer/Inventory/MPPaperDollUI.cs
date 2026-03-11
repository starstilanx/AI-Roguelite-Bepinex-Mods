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
    /// Paper-doll equipment viewer.
    /// Shows each player's equipped items organised by slot, plus a bag section for unequipped items.
    /// Attached to the same DontDestroyOnLoad root as CoopStatusOverlay (shares its Canvas).
    ///
    /// Usage:
    ///   MPPaperDollUI.GetOrCreate(coopOverlayRootGO);
    ///   MPPaperDollUI.Instance?.Toggle();
    ///   MPPaperDollUI.Instance?.Refresh();
    /// </summary>
    public class MPPaperDollUI : MonoBehaviour
    {
        public static MPPaperDollUI Instance { get; private set; }

        private GameObject _panel;
        private TMP_Text _contentText;
        private ScrollRect _scrollRect;
        private bool _isVisible;

        // Ordered slot display
        private static readonly string[] SlotOrder = new[]
        {
            "Head", "Face", "Necklace", "Torso", "Gloves", "Ring", "Pants", "Boots", "Wieldable"
        };

        private static readonly Dictionary<string, string> SlotLabel = new Dictionary<string, string>
        {
            { "Head",      "⛉ Head     " },
            { "Face",      "👁 Face     " },
            { "Necklace",  "💎 Necklace " },
            { "Torso",     "🛡 Torso    " },
            { "Gloves",    "🤜 Gloves   " },
            { "Ring",      "💍 Ring     " },
            { "Pants",     "👖 Pants    " },
            { "Boots",     "👢 Boots    " },
            { "Wieldable", "⚔ Weapon   " },
        };

        // Index of which player we are currently viewing (0 = first in DB)
        private int _selectedPlayerIdx = 0;

        // ---- Factory ----

        public static MPPaperDollUI GetOrCreate(GameObject canvasRoot)
        {
            if (Instance != null) return Instance;

            var go = new GameObject("AIROG_PaperDollUI");
            go.transform.SetParent(canvasRoot.transform, false);
            Instance = go.AddComponent<MPPaperDollUI>();
            Instance.BuildPanel();

            MPInventoryManager.OnInventoryChanged += () => { if (Instance != null && Instance._isVisible) Instance.Refresh(); };
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
            _panel?.SetActive(true);
            Refresh();
        }

        public void Hide()
        {
            _isVisible = false;
            _panel?.SetActive(false);
        }

        public void Refresh()
        {
            if (_contentText == null) return;
            try
            {
                _contentText.text = BuildDollText();
                BuildPlayerTabs();
                Canvas.ForceUpdateCanvases();
                if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[MPPaperDollUI] Refresh error: {ex.Message}");
            }
        }

        // ---- Unity ----

        private void OnDestroy()
        {
            MPInventoryManager.OnInventoryChanged -= Refresh;
            if (Instance == this) Instance = null;
        }

        // ---- Content building ----

        private string BuildDollText()
        {
            var db = MPInventoryManager.Database;
            if (db.Players.Count == 0)
                return "<color=#888888><i>No inventory data yet.\nData syncs after each turn.</i></color>";

            var playerList = new List<PlayerInventory>(db.Players.Values);
            if (_selectedPlayerIdx >= playerList.Count) _selectedPlayerIdx = 0;

            var inv = playerList[_selectedPlayerIdx];
            var sb = new StringBuilder();

            // Header
            sb.AppendLine($"<color=#88ccff><b>{Esc(inv.CharacterName)}</b></color>" +
                          $"<color=#ffd866>  {inv.Gold:N0} gold</color>");
            sb.AppendLine();

            // ─── Equipped slots ───────────────────────────────
            sb.AppendLine("<color=#aaaaaa>── EQUIPPED ──────────────────────────</color>");

            // Build a lookup of slot → equipped item (first match wins)
            var equippedBySlot = new Dictionary<string, MPItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in inv.Items)
            {
                if (!item.IsEquipped) continue;
                string slot = item.ItemType;
                if (!equippedBySlot.ContainsKey(slot))
                    equippedBySlot[slot] = item;
            }

            foreach (var slot in SlotOrder)
            {
                string label = SlotLabel.TryGetValue(slot, out string lbl) ? lbl : slot.PadRight(10);
                if (equippedBySlot.TryGetValue(slot, out var item))
                {
                    string qc = QualityColor(item.Quality);
                    string gv = item.GoldValue >= 0 ? $" <color=#ffd866>({item.GoldValue}g)</color>" : "";
                    sb.AppendLine($"<color=#888888>{label}</color><color={qc}>{Esc(item.Name)}</color>{gv}");
                    if (!string.IsNullOrEmpty(item.Description))
                    {
                        string desc = item.Description.Length > 90
                            ? item.Description.Substring(0, 90) + "…"
                            : item.Description;
                        sb.AppendLine($"           <color=#555566><i>{Esc(desc)}</i></color>");
                    }
                }
                else
                {
                    sb.AppendLine($"<color=#888888>{label}</color><color=#444455><i>——</i></color>");
                }
            }

            // ─── Bag (unequipped) ─────────────────────────────
            var bagItems = inv.Items.FindAll(i => !i.IsEquipped);
            if (bagItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"<color=#aaaaaa>── BAG ({bagItems.Count} item{(bagItems.Count == 1 ? "" : "s")}) ───────────────────────</color>");
                foreach (var item in bagItems)
                {
                    string qc = QualityColor(item.Quality);
                    string gv = item.GoldValue >= 0 ? $" <color=#ffd866>({item.GoldValue}g)</color>" : "";
                    sb.AppendLine($"  <color={qc}>{Esc(item.Name)}</color> <color=#777777>[{item.ItemType}]</color>{gv}");
                    if (!string.IsNullOrEmpty(item.Description))
                    {
                        string desc = item.Description.Length > 90
                            ? item.Description.Substring(0, 90) + "…"
                            : item.Description;
                        sb.AppendLine($"    <color=#555566><i>{Esc(desc)}</i></color>");
                    }
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("<color=#444455><i>Bag is empty.</i></color>");
            }

            return sb.ToString().TrimEnd();
        }

        // ---- Player tab buttons ----

        private readonly List<Button> _tabButtons = new List<Button>();
        private Transform _tabRow;

        private void BuildPlayerTabs()
        {
            if (_tabRow == null) return;

            // Destroy old buttons
            foreach (var btn in _tabButtons)
                if (btn != null) Destroy(btn.gameObject);
            _tabButtons.Clear();

            var db = MPInventoryManager.Database;
            var playerList = new List<PlayerInventory>(db.Players.Values);

            for (int i = 0; i < playerList.Count; i++)
            {
                int capturedIdx = i;
                var inv = playerList[i];
                bool isSelected = (i == _selectedPlayerIdx);

                Color btnColor = isSelected
                    ? new Color(0.25f, 0.55f, 0.85f)
                    : new Color(0.15f, 0.2f, 0.3f);

                var btnGo = new GameObject($"Tab_{i}");
                btnGo.transform.SetParent(_tabRow, false);
                btnGo.AddComponent<Image>().color = btnColor;
                var le = btnGo.AddComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.preferredHeight = 22f;
                var btn = btnGo.AddComponent<Button>();
                btn.onClick.AddListener(() =>
                {
                    _selectedPlayerIdx = capturedIdx;
                    Refresh();
                });

                var txtGo = new GameObject("Label");
                txtGo.transform.SetParent(btnGo.transform, false);
                var t = txtGo.AddComponent<TextMeshProUGUI>();
                // Truncate long names
                string name = inv.CharacterName;
                if (name.Length > 12) name = name.Substring(0, 11) + "…";
                t.text = name;
                t.fontSize = 9.5f;
                t.fontStyle = isSelected ? FontStyles.Bold : FontStyles.Normal;
                t.color = Color.white;
                t.alignment = TextAlignmentOptions.Center;
                var tRT = txtGo.GetComponent<RectTransform>();
                tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
                tRT.offsetMin = Vector2.zero; tRT.offsetMax = Vector2.zero;

                _tabButtons.Add(btn);
            }
        }

        // ---- UI Builder ----

        private void BuildPanel()
        {
            _panel = new GameObject("DollPanel");
            _panel.transform.SetParent(transform, false);
            _panel.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.96f);

            var panelRT = _panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot     = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(560f, 580f);
            panelRT.anchoredPosition = new Vector2(-180f, 0f); // Offset left so it doesn't overlap inventory

            // Left accent bar (purple tint to distinguish from green inventory)
            var accent = new GameObject("Accent");
            accent.transform.SetParent(_panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.55f, 0.3f, 0.85f, 0.9f);
            var aRT = accent.GetComponent<RectTransform>();
            aRT.anchorMin = new Vector2(0f, 0f); aRT.anchorMax = new Vector2(0f, 1f);
            aRT.pivot = new Vector2(0f, 0.5f);
            aRT.sizeDelta = new Vector2(3f, 0f); aRT.anchoredPosition = Vector2.zero;

            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 4f;

            // ─── Header row ───
            var headerRow = MakeHRow(_panel.transform, "Header", 26f);
            var title = new GameObject("Title");
            title.transform.SetParent(headerRow.transform, false);
            var titleLE = title.AddComponent<LayoutElement>();
            titleLE.flexibleWidth = 1f;
            var titleTxt = title.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "Paper Doll";
            titleTxt.fontSize = 14f; titleTxt.fontStyle = FontStyles.Bold;
            titleTxt.color = Color.white; titleTxt.alignment = TextAlignmentOptions.MidlineLeft;
            MakeSmallButton(headerRow.transform, "✕ Close", new Color(0.5f, 0.12f, 0.12f), () => Hide());

            AddDivider(_panel.transform);

            // ─── Player tab row ───
            _tabRow = MakeHRow(_panel.transform, "TabRow", 26f).transform;
            (_tabRow.parent.gameObject.GetComponent<LayoutElement>() ?? _tabRow.parent.gameObject.AddComponent<LayoutElement>()).preferredHeight = 26f;

            AddDivider(_panel.transform);

            // ─── Scrollable content ───
            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollLE = scrollGo.AddComponent<LayoutElement>();
            scrollLE.flexibleHeight = 1f; scrollLE.flexibleWidth = 1f;
            scrollGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            scrollGo.AddComponent<RectMask2D>();
            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false; _scrollRect.scrollSensitivity = 30f;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRT = contentGo.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0f, 1f); contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f); contentRT.sizeDelta = Vector2.zero;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _contentText = contentGo.AddComponent<TextMeshProUGUI>();
            _contentText.fontSize = 10.5f;
            _contentText.color = new Color(0.88f, 0.88f, 0.92f);
            _contentText.alignment = TextAlignmentOptions.TopLeft;
            _contentText.enableWordWrapping = true;
            _contentText.overflowMode = TextOverflowModes.Overflow;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scrollRect.content = contentRT;

            _panel.SetActive(false);
        }

        // ---- Helpers ----

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
                default:          return "#cccccc";
            }
        }

        private static string Esc(string s) =>
            s?.Replace("<", "\u003c").Replace(">", "\u003e") ?? "";

        private static GameObject MakeHRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height; le.flexibleWidth = 1f;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandHeight = true; hlg.spacing = 4f;
            return go;
        }

        private static void AddDivider(Transform parent)
        {
            var go = new GameObject("Divider");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.3f, 0.35f, 0.45f, 0.5f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 1f; le.flexibleWidth = 1f;
        }

        private static void MakeSmallButton(Transform parent, string label, Color color, Action onClick)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 80f; le.preferredHeight = 24f;
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
