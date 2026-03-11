using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;
using AIROG_Multiplayer.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// Small co-op status overlay shown in the top-right corner of the screen.
    /// Persists across scene loads (DontDestroyOnLoad).
    ///
    /// Now that clients load the host's save and see the actual game UI, this overlay
    /// only needs to show connection status, party roster, OOC chat, and action feedback.
    /// </summary>
    public class CoopStatusOverlay : MonoBehaviour
    {
        public static CoopStatusOverlay Instance { get; private set; }

        // Panel references
        private TMP_Text _statusText;
        private TMP_Text _partyText;
        private GameObject _chatPanel;
        private TMP_Text _chatLog;
        private ScrollRect _chatScroll;
        private TMP_InputField _chatInput;
        private TMP_Text _toastText;
        private float _toastHideTime;
        private MPInventoryUI _inventoryUI;

        // HP edit panel (client mode only)
        private GameObject _hpEditPanel;
        private TMP_InputField _hpInput;
        private TMP_InputField _maxHpInput;

        private bool _isHostMode = false;
        private bool _whisperMode = false;
        private bool _collapsed = false;
        private Button _whisperBtn;
        private Button _collapseBtn;
        private GameObject _bodyPanel;

        /// <summary>
        /// When true, the client's next action submission will be sent as a private/whisper action
        /// instead of a normal action. Resets to false after sending.
        /// </summary>
        public bool IsWhisperMode => _whisperMode;

        private readonly List<string> _chatLines = new List<string>();
        private const int MAX_CHAT_LINES = 40;

        // ---- Factory ----

        public static void Show(WelcomePayload welcome)
        {
            bool spectator = MultiplayerPlugin.LocalCharacterInfo?.IsSpectator ?? false;
            string statusMsg = spectator
                ? $"👁 Spectating — host at: {welcome?.CurrentLocation ?? "?"}"
                : $"Connected — host at: {welcome?.CurrentLocation ?? "?"}";

            if (Instance != null)
            {
                Instance.gameObject.SetActive(true);
                Instance.SetStatus(statusMsg);
                return;
            }

            var go = new GameObject("AIROG_CoopOverlay");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CoopStatusOverlay>();
            Instance._isHostMode = false;
            Instance.BuildUI();
            Instance.SetStatus(statusMsg);
        }

        /// <summary>Shows the overlay for the hosting player (no HP edit, Stop Hosting button).</summary>
        public static void ShowForHost(int port)
        {
            if (Instance != null)
            {
                Instance.gameObject.SetActive(true);
                Instance.SetStatus($"Hosting on port {port}");
                return;
            }

            var go = new GameObject("AIROG_CoopOverlay");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CoopStatusOverlay>();
            Instance._isHostMode = true;
            Instance.BuildUI();
            Instance.SetStatus($"Hosting on port {port}");
        }

        public static void Hide()
        {
            Instance?.gameObject.SetActive(false);
        }

        // ---- Public API ----

        public void SetStatus(string message, bool connected = true)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.color = connected
                ? new Color(0.4f, 1f, 0.5f)
                : new Color(1f, 0.45f, 0.3f);
        }

        public void UpdateParty(RemoteCharacterInfo[] members)
        {
            if (_partyText == null || members == null) return;
            var sb = new StringBuilder();

            // Active players first
            foreach (var m in members)
            {
                if (m.IsSpectator) continue;
                sb.Append($"<color=#88ccff>{EscRt(m.CharacterName)}</color>");
                if (!string.IsNullOrEmpty(m.CharacterClass))
                    sb.Append($" <color=#aaaaaa>({EscRt(m.CharacterClass)})</color>");
                sb.AppendLine();
                if (m.MaxHealth > 0)
                    sb.AppendLine($"  HP {m.Health}/{m.MaxHealth}");
            }

            // Spectators section
            bool hasSpectators = false;
            foreach (var m in members)
            {
                if (!m.IsSpectator) continue;
                if (!hasSpectators)
                {
                    sb.AppendLine("<color=#777777>Spectators:</color>");
                    hasSpectators = true;
                }
                string name = !string.IsNullOrEmpty(m.CharacterName) ? m.CharacterName : m.PlayerName ?? "Spectator";
                sb.AppendLine($"  <color=#999999>👁 {EscRt(name)}</color>");
            }

            _partyText.text = sb.ToString().TrimEnd();
        }

        public void AddChat(string senderName, string message, bool isSystem = false)
        {
            string color = isSystem ? "#88ccff" : "#aaffaa";
            _chatLines.Add($"<color={color}><b>{EscRt(senderName)}</b>: {EscRt(message)}</color>");
            if (_chatLines.Count > MAX_CHAT_LINES) _chatLines.RemoveAt(0);
            if (_chatLog != null)
            {
                _chatLog.text = string.Join("\n", _chatLines);
                Canvas.ForceUpdateCanvases();
                if (_chatScroll != null) _chatScroll.verticalNormalizedPosition = 0f;
            }
        }

        public void ShowActionQueued(string actionPreview)
        {
            ShowToast($"✓ Sent: {Truncate(actionPreview, 50)}", 3f);
        }

        public void ShowNotification(string message, float duration = 3f)
        {
            ShowToast(message, duration);
        }

        // ---- Unity Update ----

        private void Update()
        {
            // Hide toast when expired
            if (_toastText != null && _toastText.gameObject.activeSelf && Time.time > _toastHideTime)
                _toastText.gameObject.SetActive(false);

            // Fallback queue drain: if MultiplayerPlugin's MonoBehaviour was destroyed during
            // a scene transition (despite DontDestroyOnLoad), this overlay (also DDOL) keeps
            // draining the main-thread callback queue so events are never dropped.
            if (MultiplayerPlugin.Instance == null)
            {
                var client = MultiplayerPlugin.Client;
                if (client != null)
                    while (client.MainThreadQueue.TryDequeue(out System.Action act))
                        try { act?.Invoke(); } catch { }

                var server = MultiplayerPlugin.Server;
                if (server != null)
                    while (server.MainThreadQueue.TryDequeue(out System.Action act))
                        try { act?.Invoke(); } catch { }
            }
        }

        // ---- UI Builder ----

        private void BuildUI()
        {
            // Root canvas — screen-space overlay, high sorting order
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            gameObject.AddComponent<GraphicRaycaster>();

            // ---- Main panel — top-right corner, 240px wide ----
            var panel = MakeBox(transform, "Panel", new Color(0.06f, 0.06f, 0.10f, 0.88f));
            var panelRT = panel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(1f, 1f);
            panelRT.anchorMax = new Vector2(1f, 1f);
            panelRT.pivot = new Vector2(1f, 1f);
            panelRT.sizeDelta = new Vector2(240f, 0f); // height driven by content
            panelRT.anchoredPosition = new Vector2(-6f, -6f);

            // Use a vertical layout group so height is auto
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.spacing = 4f;
            var csf = panel.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Blue left accent bar
            var accent = new GameObject("Accent");
            accent.transform.SetParent(panel.transform, false);
            accent.AddComponent<Image>().color = new Color(0.3f, 0.55f, 1f, 0.85f);
            var aRT = accent.GetComponent<RectTransform>();
            aRT.anchorMin = new Vector2(0f, 0f);
            aRT.anchorMax = new Vector2(0f, 1f);
            aRT.pivot = new Vector2(0f, 0.5f);
            aRT.sizeDelta = new Vector2(3f, 0f);
            aRT.anchoredPosition = Vector2.zero;

            // Header row (label + collapse toggle)
            var headerRow = MakeHRow(panel.transform, "HeaderRow", 22f);
            var headerLabel = new GameObject("HeaderLabel");
            headerLabel.transform.SetParent(headerRow.transform, false);
            var headerTxt = headerLabel.AddComponent<TextMeshProUGUI>();
            headerTxt.text = "⚔ Co-op"; headerTxt.fontSize = 13f;
            headerTxt.fontStyle = FontStyles.Bold; headerTxt.color = Color.white;
            headerTxt.alignment = TextAlignmentOptions.MidlineLeft;
            headerLabel.AddComponent<LayoutElement>().flexibleWidth = 1f;

            _collapseBtn = MakeSmallButton(headerRow.transform, "▲", new Color(0.2f, 0.22f, 0.32f), ToggleCollapse);
            _collapseBtn.gameObject.AddComponent<LayoutElement>().preferredWidth = 20f;

            // Body panel — wraps everything below the header, toggled by collapse button
            _bodyPanel = new GameObject("BodyPanel");
            _bodyPanel.transform.SetParent(panel.transform, false);
            _bodyPanel.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var bodyVlg = _bodyPanel.AddComponent<VerticalLayoutGroup>();
            bodyVlg.childForceExpandWidth = true;
            bodyVlg.childForceExpandHeight = false;
            bodyVlg.spacing = 4f;
            _bodyPanel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Status line
            _statusText = AddLayoutLabel(_bodyPanel.transform, "Connecting...", 10f, FontStyles.Normal, new Color(0.4f, 1f, 0.5f));

            // Divider
            AddDivider(_bodyPanel.transform);

            // Party header
            AddLayoutLabel(_bodyPanel.transform, "Party", 10f, FontStyles.Bold, new Color(0.7f, 0.85f, 1f));

            // Party list
            _partyText = AddLayoutLabel(_bodyPanel.transform, "—", 9.5f, FontStyles.Normal, new Color(0.85f, 0.85f, 0.9f));
            _partyText.alignment = TextAlignmentOptions.TopLeft;

            // Divider
            AddDivider(_bodyPanel.transform);

            // Chat toggle button + inventory button + action button row
            var chatBtnRow = MakeHRow(_bodyPanel.transform, "ChatRow", 26f);
            MakeButton(chatBtnRow.transform, "ChatToggle", "💬 Chat", new Color(0.2f, 0.45f, 0.75f), () => ToggleChat());
            MakeButton(chatBtnRow.transform, "DiceBtn", "🎲", new Color(0.55f, 0.35f, 0.15f), () => OnDiceButtonClicked());
            MakeButton(chatBtnRow.transform, "InvToggle", "🎒 Inv", new Color(0.2f, 0.5f, 0.22f), () => ToggleInventory());
            MakeButton(chatBtnRow.transform, "QuestToggle", "📜 Quests", new Color(0.6f, 0.5f, 0.15f), () => ToggleQuests());
            MakeButton(chatBtnRow.transform, "MapToggle", "🗺 Map", new Color(0.2f, 0.35f, 0.6f), () => ToggleMap());

            if (_isHostMode)
            {
                MakeButton(chatBtnRow.transform, "StopHosting", "Stop Hosting", new Color(0.45f, 0.12f, 0.12f), () =>
                {
                    MultiplayerPlugin.StopHost();
                    SetStatus("Stopped hosting.", connected: false);
                    gameObject.SetActive(false);
                });
            }
            else
            {
                MakeButton(chatBtnRow.transform, "EditHP", "✎ HP", new Color(0.3f, 0.3f, 0.55f), () => ToggleHpEdit());
                _whisperBtn = MakeButton(chatBtnRow.transform, "WhisperBtn", "🤫 Whisper", new Color(0.4f, 0.25f, 0.5f), () => ToggleWhisper());
                MakeButton(chatBtnRow.transform, "Disconnect", "Disconnect", new Color(0.45f, 0.12f, 0.12f), () =>
                {
                    MultiplayerPlugin.StopClient();
                    SetStatus("Disconnected.", connected: false);
                });
            }

            // HP edit panel (client mode only, hidden by default)
            if (!_isHostMode)
            {
                _hpEditPanel = new GameObject("HpEditPanel");
                _hpEditPanel.transform.SetParent(_bodyPanel.transform, false);
                var hpVlg = _hpEditPanel.AddComponent<VerticalLayoutGroup>();
                hpVlg.childForceExpandWidth = true;
                hpVlg.childForceExpandHeight = false;
                hpVlg.padding = new RectOffset(4, 4, 2, 2);
                hpVlg.spacing = 2f;
                _hpEditPanel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var hpRow = new GameObject("HpRow");
                hpRow.transform.SetParent(_hpEditPanel.transform, false);
                var hpRowLE = hpRow.AddComponent<LayoutElement>();
                hpRowLE.preferredHeight = 26f;
                hpRowLE.flexibleWidth = 1f;
                var hpRowHlg = hpRow.AddComponent<HorizontalLayoutGroup>();
                hpRowHlg.childForceExpandHeight = true;
                hpRowHlg.spacing = 3f;

                // Label
                var hpLblGo = new GameObject("HpLabel");
                hpLblGo.transform.SetParent(hpRow.transform, false);
                var hpLbl = hpLblGo.AddComponent<TextMeshProUGUI>();
                hpLbl.text = "HP:"; hpLbl.fontSize = 10f; hpLbl.color = new Color(0.85f, 0.85f, 0.9f);
                hpLbl.alignment = TextAlignmentOptions.MidlineLeft;
                hpLblGo.AddComponent<LayoutElement>().preferredWidth = 28f;

                _hpInput = MakeInputField(hpRow.transform, "100");
                _hpInput.gameObject.AddComponent<LayoutElement>().preferredWidth = 52f;
                _hpInput.contentType = TMP_InputField.ContentType.IntegerNumber;

                var slashGo = new GameObject("Slash");
                slashGo.transform.SetParent(hpRow.transform, false);
                var slashTxt = slashGo.AddComponent<TextMeshProUGUI>();
                slashTxt.text = "/"; slashTxt.fontSize = 10f; slashTxt.color = new Color(0.6f, 0.6f, 0.7f);
                slashTxt.alignment = TextAlignmentOptions.Center;
                slashGo.AddComponent<LayoutElement>().preferredWidth = 10f;

                _maxHpInput = MakeInputField(hpRow.transform, "100");
                _maxHpInput.gameObject.AddComponent<LayoutElement>().preferredWidth = 52f;
                _maxHpInput.contentType = TMP_InputField.ContentType.IntegerNumber;

                var saveBtn = MakeSmallButton(hpRow.transform, "✓", new Color(0.2f, 0.55f, 0.25f), OnHpSaved);
                saveBtn.gameObject.AddComponent<LayoutElement>().preferredWidth = 28f;

                _hpEditPanel.SetActive(false);
            }

            // ---- Chat panel (hidden by default) ----
            _chatPanel = MakeBox(_bodyPanel.transform, "ChatPanel", new Color(0.04f, 0.04f, 0.08f, 0.95f));
            var chatVlg = _chatPanel.AddComponent<VerticalLayoutGroup>();
            chatVlg.childForceExpandWidth = true;
            chatVlg.childForceExpandHeight = false;
            chatVlg.padding = new RectOffset(4, 4, 4, 4);
            chatVlg.spacing = 4f;
            var chatCsf = _chatPanel.AddComponent<ContentSizeFitter>();
            chatCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Scrollable chat log (fixed height)
            var scrollGo = new GameObject("ChatScroll");
            scrollGo.transform.SetParent(_chatPanel.transform, false);
            var scrollLE = scrollGo.AddComponent<LayoutElement>();
            scrollLE.preferredHeight = 120f;
            scrollLE.flexibleWidth = 1f;
            scrollGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.10f, 0f);
            scrollGo.AddComponent<RectMask2D>();
            _chatScroll = scrollGo.AddComponent<ScrollRect>();
            _chatScroll.horizontal = false;
            _chatScroll.scrollSensitivity = 20f;

            var chatContent = new GameObject("Content");
            chatContent.transform.SetParent(scrollGo.transform, false);
            var chatContentRT = chatContent.AddComponent<RectTransform>();
            chatContentRT.anchorMin = new Vector2(0, 1);
            chatContentRT.anchorMax = new Vector2(1, 1);
            chatContentRT.pivot = new Vector2(0.5f, 1f);
            chatContentRT.sizeDelta = Vector2.zero;
            chatContent.AddComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            chatContent.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _chatLog = chatContent.AddComponent<TextMeshProUGUI>();
            _chatLog.fontSize = 9.5f;
            _chatLog.color = new Color(0.85f, 0.85f, 0.9f);
            _chatLog.alignment = TextAlignmentOptions.TopLeft;
            _chatLog.enableWordWrapping = true;
            _chatLog.overflowMode = TextOverflowModes.Overflow;
            chatContent.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _chatScroll.content = chatContentRT;

            // Chat input row
            var chatInputRow = new GameObject("ChatInputRow");
            chatInputRow.transform.SetParent(_chatPanel.transform, false);
            var rowLE = chatInputRow.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 26f;
            rowLE.flexibleWidth = 1f;
            var rowHlg = chatInputRow.AddComponent<HorizontalLayoutGroup>();
            rowHlg.childForceExpandHeight = true;
            rowHlg.spacing = 4f;

            _chatInput = MakeInputField(chatInputRow.transform, "Type a message...");
            var inputLE = _chatInput.gameObject.AddComponent<LayoutElement>();
            inputLE.flexibleWidth = 1f;
            inputLE.preferredHeight = 24f;

            var sendBtn = MakeSmallButton(chatInputRow.transform, "Send", new Color(0.2f, 0.55f, 0.25f), OnSendChat);
            var sendLE = sendBtn.gameObject.AddComponent<LayoutElement>();
            sendLE.preferredWidth = 40f;
            sendLE.preferredHeight = 24f;

            _chatInput.onSubmit.AddListener(_ => OnSendChat());
            _chatPanel.SetActive(false);

            // ---- Toast notification (centered, outside panel) ----
            _toastText = MakeToast(transform, "Toast");

            // Force initial layout update
            Canvas.ForceUpdateCanvases();
        }

        // ---- Collapse ----

        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_bodyPanel != null)
                _bodyPanel.SetActive(!_collapsed);

            // Update button label to indicate current state
            if (_collapseBtn != null)
            {
                var txt = _collapseBtn.GetComponentInChildren<TMP_Text>();
                if (txt != null) txt.text = _collapsed ? "▼" : "▲";
            }
        }

        // ---- Chat logic ----

        private void ToggleChat()
        {
            if (_chatPanel != null)
                _chatPanel.SetActive(!_chatPanel.activeSelf);
        }

        private void ToggleInventory()
        {
            if (_inventoryUI == null)
                _inventoryUI = MPInventoryUI.GetOrCreate(gameObject);
            _inventoryUI.Toggle();
        }

        private void ToggleQuests()
        {
            MPQuestUI.GetOrCreate(gameObject).Toggle();
        }

        private void ToggleMap()
        {
            MPMapOverlay.GetOrCreate(gameObject).Toggle();
        }

        private void ToggleWhisper()
        {
            _whisperMode = !_whisperMode;
            if (_whisperBtn != null)
            {
                var img = _whisperBtn.GetComponent<Image>();
                if (img != null)
                    img.color = _whisperMode ? new Color(0.6f, 0.2f, 0.7f) : new Color(0.4f, 0.25f, 0.5f);
                var txt = _whisperBtn.GetComponentInChildren<TMP_Text>();
                if (txt != null)
                    txt.text = _whisperMode ? "🤫 ON" : "🤫 Whisper";
            }
            AddChat("System", _whisperMode
                ? "<color=#CC88FF>Whisper mode ON — your next action will be private.</color>"
                : "<color=#AAAAAA>Whisper mode OFF — actions are normal.</color>", isSystem: true);
        }

        /// <summary>
        /// If whisper mode is active, consumes it (sets to false) and returns true.
        /// Used by the action submission patch to route the action as private.
        /// </summary>
        public bool ConsumeWhisperMode()
        {
            if (!_whisperMode) return false;
            _whisperMode = false;
            // Reset button appearance
            if (_whisperBtn != null)
            {
                var img = _whisperBtn.GetComponent<Image>();
                if (img != null) img.color = new Color(0.4f, 0.25f, 0.5f);
                var txt = _whisperBtn.GetComponentInChildren<TMP_Text>();
                if (txt != null) txt.text = "🤫 Whisper";
            }
            return true;
        }

        /// <summary>
        /// Displays a private action result in the chat log with distinct formatting.
        /// </summary>
        public void ShowPrivateResult(string resultText)
        {
            AddChat("Private", $"<color=#CC88FF>[Secret] {resultText}</color>", isSystem: true);
        }

        private void OnSendChat()
        {
            if (_chatInput == null) return;
            string msg = _chatInput.text.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            // Intercept /roll commands
            if (msg.StartsWith("/roll ", StringComparison.OrdinalIgnoreCase))
            {
                HandleDiceRoll(msg.Substring(6).Trim());
                _chatInput.text = "";
                return;
            }

            if (MultiplayerPlugin.IsClient)
            {
                MultiplayerPlugin.Client.SendChat(msg);
                AddChat(MultiplayerPlugin.LocalCharacterName, msg);
            }
            else if (MultiplayerPlugin.IsHost)
            {
                // Host relays OOC chat to all clients and shows it locally
                var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                string hostName = manager?.playerCharacter?.pcGameEntity?.name ?? "Host";
                MultiplayerPlugin.Server?.BroadcastChat(hostName, msg);
                AddChat(hostName, msg);
            }
            _chatInput.text = "";
        }

        private void HandleDiceRoll(string expression)
        {
            if (!DiceRoller.TryParse(expression, out string normalized))
            {
                AddChat("System", "Invalid dice expression. Use NdM+K format (e.g., 2d6+3, 1d20).", isSystem: true);
                return;
            }

            var result = DiceRoller.Roll(normalized);
            string senderName;

            if (MultiplayerPlugin.IsClient)
                senderName = MultiplayerPlugin.LocalCharacterName;
            else
            {
                var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                senderName = manager?.playerCharacter?.pcGameEntity?.name ?? "Host";
            }

            string rollMsg = $"🎲 {senderName} rolled {result.Expression}: {DiceRoller.FormatResult(result)}";

            if (MultiplayerPlugin.IsClient)
            {
                MultiplayerPlugin.Client.SendChat(rollMsg);
                AddChat(senderName, rollMsg);
            }
            else if (MultiplayerPlugin.IsHost)
            {
                MultiplayerPlugin.Server?.BroadcastChat(senderName, rollMsg);
                AddChat(senderName, rollMsg);
            }
        }

        private void OnDiceButtonClicked()
        {
            // Open chat panel if not already open, pre-fill with /roll
            if (_chatPanel != null && !_chatPanel.activeSelf)
                _chatPanel.SetActive(true);
            if (_chatInput != null)
            {
                _chatInput.text = "/roll 1d20";
                _chatInput.ActivateInputField();
                _chatInput.caretPosition = _chatInput.text.Length;
            }
        }

        private void ToggleHpEdit()
        {
            if (_hpEditPanel == null) return;
            bool next = !_hpEditPanel.activeSelf;
            _hpEditPanel.SetActive(next);

            // Pre-fill with current known values when opening
            if (next && MultiplayerPlugin.LocalCharacterInfo != null)
            {
                var info = MultiplayerPlugin.LocalCharacterInfo;
                if (_hpInput != null) _hpInput.text = info.Health.ToString();
                if (_maxHpInput != null) _maxHpInput.text = info.MaxHealth.ToString();
            }
        }

        private void OnHpSaved()
        {
            if (!MultiplayerPlugin.IsClient) return;
            var info = MultiplayerPlugin.LocalCharacterInfo;
            if (info == null) return;

            long.TryParse(_hpInput?.text ?? "", out long hp);
            long.TryParse(_maxHpInput?.text ?? "", out long maxHp);
            if (maxHp <= 0) maxHp = hp;

            info.Health = hp;
            info.MaxHealth = maxHp;

            MultiplayerPlugin.Client?.SendCharacterUpdate(info);
            ShowToast($"HP updated: {hp}/{maxHp}", 2.5f);
            _hpEditPanel?.SetActive(false);
        }

        // ---- Toast ----

        private void ShowToast(string message, float duration)
        {
            if (_toastText == null) return;
            _toastText.text = message;
            _toastText.gameObject.SetActive(true);
            _toastHideTime = Time.time + duration;
        }

        // ---- UI Helpers ----

        private static string EscRt(string s) =>
            s?.Replace("<", "\u003c").Replace(">", "\u003e") ?? "";

        private static string Truncate(string s, int max) =>
            s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";

        private TMP_Text AddLayoutLabel(Transform parent, string text, float size, FontStyles style, Color color)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            var lbl = go.AddComponent<TextMeshProUGUI>();
            lbl.text = text;
            lbl.fontSize = size;
            lbl.fontStyle = style;
            lbl.color = color;
            lbl.alignment = TextAlignmentOptions.TopLeft;
            lbl.enableWordWrapping = true;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return lbl;
        }

        private void AddDivider(Transform parent)
        {
            var go = new GameObject("Divider");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.3f, 0.35f, 0.45f, 0.5f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 1f;
            le.flexibleWidth = 1f;
        }

        private GameObject MakeBox(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            // Note: AddComponent<Image>() already adds a RectTransform implicitly.
            return go;
        }

        private GameObject MakeHRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1f;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandHeight = true;
            hlg.spacing = 4f;
            return go;
        }

        private Button MakeButton(Transform parent, string name, string label, Color color, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
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
            return btn;
        }

        private Button MakeSmallButton(Transform parent, string label, Color color, Action onClick)
        {
            var go = new GameObject("Btn_" + label);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = color;
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
            return btn;
        }

        private TMP_InputField MakeInputField(Transform parent, string placeholder)
        {
            var go = new GameObject("InputField");
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.16f, 0.95f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            textComp.fontSize = 10f; textComp.color = Color.white;
            var tRT = textGo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
            tRT.offsetMin = new Vector2(4, 2); tRT.offsetMax = new Vector2(-4, -2);

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var phComp = phGo.AddComponent<TextMeshProUGUI>();
            phComp.text = placeholder; phComp.fontSize = 10f;
            phComp.color = new Color(0.5f, 0.5f, 0.5f);
            phComp.fontStyle = FontStyles.Italic;
            var phRT = phGo.GetComponent<RectTransform>();
            phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
            phRT.offsetMin = new Vector2(4, 2); phRT.offsetMax = new Vector2(-4, -2);

            var field = go.AddComponent<TMP_InputField>();
            field.textComponent = textComp;
            field.placeholder = phComp;
            field.text = "";
            return field;
        }

        private TMP_Text MakeToast(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lbl = go.AddComponent<TextMeshProUGUI>();
            lbl.fontSize = 13f; lbl.fontStyle = FontStyles.Bold;
            lbl.color = new Color(0.4f, 1f, 0.45f);
            lbl.alignment = TextAlignmentOptions.Center;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(500f, 40f);
            rt.anchoredPosition = new Vector2(0f, 80f);
            go.SetActive(false);
            return lbl;
        }
    }
}
