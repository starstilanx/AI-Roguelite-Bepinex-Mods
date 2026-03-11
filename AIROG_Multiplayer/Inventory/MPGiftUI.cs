using System;
using System.Collections.Generic;
using AIROG_Multiplayer.Inventory;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// Two-step gift panel: pick an item from YOUR inventory, then pick a recipient.
    ///
    /// Step 1 — Item selection: scrollable list of your items as buttons.
    /// Step 2 — Recipient selection: one button per other connected player.
    ///
    /// On confirm, calls:
    ///   Client: Client.SendItemTransfer(toPlayerId, itemName)
    ///   Host:   MPInventoryManager.TransferItem directly + BroadcastInventory
    ///
    /// Attach to the same DontDestroyOnLoad root as CoopStatusOverlay.
    /// </summary>
    public class MPGiftUI : MonoBehaviour
    {
        public static MPGiftUI Instance { get; private set; }

        private GameObject _panel;
        private ScrollRect _scrollRect;
        private Transform _listContent;
        private TMP_Text _stepLabel;
        private bool _isVisible;

        private string _selectedItemName;
        private string _myPlayerId;

        // ---- Factory ----

        public static MPGiftUI GetOrCreate(GameObject canvasRoot)
        {
            if (Instance != null) return Instance;

            var go = new GameObject("AIROG_GiftUI");
            go.transform.SetParent(canvasRoot.transform, false);
            Instance = go.AddComponent<MPGiftUI>();
            Instance.BuildPanel();
            return Instance;
        }

        // ---- Public API ----

        public void Toggle()
        {
            _isVisible = !_isVisible;
            _panel?.SetActive(_isVisible);
            if (_isVisible) ShowItemStep();
        }

        public void Show()
        {
            _isVisible = true;
            _panel?.SetActive(true);
            ShowItemStep();
        }

        public void Hide()
        {
            _isVisible = false;
            _panel?.SetActive(false);
        }

        // ---- Unity ----

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- Step logic ----

        private void ShowItemStep()
        {
            _selectedItemName = null;
            _myPlayerId = GetMyPlayerId();

            _stepLabel.text = "Select an item to gift:";
            ClearList();

            var db = MPInventoryManager.Database;
            if (!db.Players.TryGetValue(_myPlayerId, out var myInv) || myInv.Items.Count == 0)
            {
                AddInfoRow("<color=#888888><i>Your inventory is empty.</i></color>");
                return;
            }

            foreach (var item in myInv.Items)
            {
                string capturedName = item.Name;
                string qualColor = QualityColor(item.Quality);
                string label = $"<color={qualColor}>{Esc(item.Name)}</color> <color=#777777>[{item.ItemType}]</color>";
                if (item.IsEquipped) label += " <color=#88ff88>[E]</color>";
                AddItemRow(label, () => OnItemSelected(capturedName));
            }
        }

        private void ShowRecipientStep()
        {
            _stepLabel.text = $"Gift '<color=#cccccc>{Esc(_selectedItemName)}</color>' to:";
            ClearList();

            // Back button
            AddItemRow("<color=#aaaaaa>← Back</color>", ShowItemStep);

            bool anyRecipient = false;

            if (MultiplayerPlugin.IsHost && MultiplayerPlugin.Server != null)
            {
                foreach (var client in MultiplayerPlugin.Server.GetClients())
                {
                    string toId = client.PlayerId;
                    string toName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
                    AddItemRow($"→ <color=#88ccff>{Esc(toName)}</color>", () => ConfirmGift(toId, toName));
                    anyRecipient = true;
                }
            }
            else if (MultiplayerPlugin.IsClient)
            {
                // Client sees the full party from the last PartyUpdate; we iterate the inventory DB
                // for other player IDs (the host is "host", other clients have GUID IDs)
                string myId = GetMyPlayerId();
                foreach (var kvp in MPInventoryManager.Database.Players)
                {
                    if (kvp.Key == myId) continue;
                    string toId = kvp.Key;
                    string toName = kvp.Value.CharacterName;
                    AddItemRow($"→ <color=#88ccff>{Esc(toName)}</color>", () => ConfirmGift(toId, toName));
                    anyRecipient = true;
                }
            }

            if (!anyRecipient)
                AddInfoRow("<color=#888888><i>No other players in session.</i></color>");
        }

        private void OnItemSelected(string itemName)
        {
            _selectedItemName = itemName;
            ShowRecipientStep();
        }

        private void ConfirmGift(string toPlayerId, string toName)
        {
            if (string.IsNullOrEmpty(_selectedItemName)) return;

            if (MultiplayerPlugin.IsHost)
            {
                // Host transfers directly
                bool ok = MPInventoryManager.TransferItem(MPInventoryManager.HOST_PLAYER_ID, toPlayerId, _selectedItemName);
                if (ok)
                {
                    MPInventoryManager.Save();
                    MultiplayerPlugin.BroadcastInventory();
                    string hostName = UnityEngine.Object.FindObjectOfType<GameplayManager>()
                        ?.playerCharacter?.pcGameEntity?.name ?? "Host";
                    MultiplayerPlugin.Server?.BroadcastChat("Server",
                        $"{hostName} gifted '{_selectedItemName}' to {toName}!", isSystem: true);
                    CoopStatusOverlay.Instance?.ShowNotification($"Gifted '{_selectedItemName}' to {toName}.", 3f);
                }
                else
                {
                    CoopStatusOverlay.Instance?.ShowNotification("Gift failed — item not found.", 3f);
                }
            }
            else if (MultiplayerPlugin.IsClient)
            {
                MultiplayerPlugin.Client?.SendItemTransfer(toPlayerId, _selectedItemName);
                CoopStatusOverlay.Instance?.ShowNotification($"Gift request sent for '{_selectedItemName}'.", 3f);
            }

            Hide();
        }

        // ---- List helpers ----

        private void ClearList()
        {
            if (_listContent == null) return;
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                Destroy(_listContent.GetChild(i).gameObject);
        }

        private void AddItemRow(string richText, Action onClick)
        {
            var row = new GameObject("Row");
            row.transform.SetParent(_listContent, false);
            var le = row.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f; le.preferredHeight = 28f;
            row.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.14f, 0.9f);
            var btn = row.AddComponent<Button>();
            var cs = btn.colors;
            cs.highlightedColor = new Color(0.15f, 0.2f, 0.3f);
            cs.pressedColor = new Color(0.1f, 0.14f, 0.22f);
            btn.colors = cs;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var txtGo = new GameObject("Label");
            txtGo.transform.SetParent(row.transform, false);
            var t = txtGo.AddComponent<TextMeshProUGUI>();
            t.text = richText; t.fontSize = 10.5f;
            t.color = new Color(0.88f, 0.88f, 0.92f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.enableWordWrapping = false;
            var tRT = txtGo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = new Vector2(8, 0); tRT.offsetMax = new Vector2(-4, 0);
        }

        private void AddInfoRow(string richText)
        {
            var go = new GameObject("InfoRow");
            go.transform.SetParent(_listContent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 28f;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = richText; t.fontSize = 10.5f;
            t.color = new Color(0.7f, 0.7f, 0.8f);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            var tRT = go.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = new Vector2(8, 0); tRT.offsetMax = new Vector2(-4, 0);
        }

        // ---- Helpers ----

        private static string GetMyPlayerId()
        {
            if (MultiplayerPlugin.IsHost) return MPInventoryManager.HOST_PLAYER_ID;
            return MultiplayerPlugin.Client?.AssignedPlayerId ?? "";
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
                default:          return "#cccccc";
            }
        }

        private static string Esc(string s) =>
            s?.Replace("<", "\u003c").Replace(">", "\u003e") ?? "";

        // ---- UI Builder ----

        private void BuildPanel()
        {
            _panel = new GameObject("GiftPanel");
            _panel.transform.SetParent(transform, false);
            _panel.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.10f, 0.96f);

            var panelRT = _panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot     = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(420f, 460f);
            panelRT.anchoredPosition = new Vector2(200f, 0f); // Offset right

            // Orange left accent
            var accent = new GameObject("Accent");
            accent.transform.SetParent(_panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.85f, 0.55f, 0.1f, 0.9f);
            var aRT = accent.GetComponent<RectTransform>();
            aRT.anchorMin = new Vector2(0f, 0f); aRT.anchorMax = new Vector2(0f, 1f);
            aRT.pivot = new Vector2(0f, 0.5f);
            aRT.sizeDelta = new Vector2(3f, 0f); aRT.anchoredPosition = Vector2.zero;

            var vlg = _panel.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 4f;

            // ─── Header ───
            var headerRow = MakeHRow(_panel.transform, "Header", 26f);
            var title = new GameObject("Title");
            title.transform.SetParent(headerRow.transform, false);
            var titleLE = title.AddComponent<LayoutElement>();
            titleLE.flexibleWidth = 1f;
            var titleTxt = title.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "🎁 Gift Equipment";
            titleTxt.fontSize = 13f; titleTxt.fontStyle = FontStyles.Bold;
            titleTxt.color = new Color(1f, 0.85f, 0.5f);
            titleTxt.alignment = TextAlignmentOptions.MidlineLeft;
            MakeSmallButton(headerRow.transform, "✕ Close", new Color(0.5f, 0.12f, 0.12f), () => Hide());

            AddDivider(_panel.transform);

            // ─── Step label ───
            var stepLblGo = new GameObject("StepLabel");
            stepLblGo.transform.SetParent(_panel.transform, false);
            var stepLE = stepLblGo.AddComponent<LayoutElement>();
            stepLE.preferredHeight = 22f; stepLE.flexibleWidth = 1f;
            _stepLabel = stepLblGo.AddComponent<TextMeshProUGUI>();
            _stepLabel.text = "Select an item to gift:";
            _stepLabel.fontSize = 11f; _stepLabel.color = new Color(0.8f, 0.8f, 0.9f);
            _stepLabel.alignment = TextAlignmentOptions.MidlineLeft;

            AddDivider(_panel.transform);

            // ─── Scrollable item list ───
            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(_panel.transform, false);
            var scrollLE = scrollGo.AddComponent<LayoutElement>();
            scrollLE.flexibleHeight = 1f; scrollLE.flexibleWidth = 1f;
            scrollGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            scrollGo.AddComponent<RectMask2D>();
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.scrollSensitivity = 30f;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(scrollGo.transform, false);
            var contentRT = contentGo.AddComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0f, 1f); contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f); contentRT.sizeDelta = Vector2.zero;

            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;
            contentVlg.spacing = 2f;
            contentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRT;
            _listContent = contentGo.transform;
            _scrollRect = scroll;

            _panel.SetActive(false);
        }

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
