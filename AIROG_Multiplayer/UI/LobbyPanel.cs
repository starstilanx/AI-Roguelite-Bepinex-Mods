using System;
using System.Linq;
using AIROG_Multiplayer.Network;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// The Host/Join lobby panel shown from the main menu.
    /// Built entirely from UGUI primitives at runtime — no prefab required.
    /// </summary>
    public class LobbyPanel : MonoBehaviour
    {
        private static LobbyPanel _instance;

        // UI elements
        private TMP_InputField _ipInput;
        private TMP_InputField _portInput;
        private TMP_InputField _charNameInput;
        private TMP_InputField _charClassInput;
        private TMP_InputField _charBgInput;
        private TMP_InputField _charPersonalityInput;
        private TMP_InputField _charAppearanceInput;
        private TMP_InputField _charHpInput;
        private TMP_InputField _charMaxHpInput;
        private TMP_InputField _charLevelInput;
            private TMP_Text _statusText;
        private Button _hostBtn;
        private Button _joinBtn;
        private Button _spectateBtn;
        private Button _reconnectBtn;
        private Button _closeBtn;
        private MainMenu _mainMenu;

        public static void Show(MainMenu mainMenu)
        {
            if (_instance != null)
            {
                _instance.gameObject.SetActive(true);
                return;
            }

            GameObject go = new GameObject("AIROG_LobbyPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<LobbyPanel>();
            _instance._mainMenu = mainMenu;
            _instance.BuildUI(mainMenu.transform.root);
        }

        public static void Hide()
        {
            _instance?.gameObject.SetActive(false);
        }

        private void BuildUI(Transform root)
        {
            // ---- Root Canvas overlay ----
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            // ---- Dark background overlay ----
            GameObject overlay = CreatePanel(transform, "Overlay",
                new Color(0, 0, 0, 0.75f),
                new Vector2(0, 0), new Vector2(1, 1));
            var overlayRect = overlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            // ---- Dialog panel ----
            GameObject dialog = CreatePanel(overlay.transform, "Dialog",
                new Color(0.12f, 0.12f, 0.18f, 0.97f));
            RectTransform dialogRect = dialog.GetComponent<RectTransform>();
            dialogRect.anchorMin = new Vector2(0.5f, 0.5f);
            dialogRect.anchorMax = new Vector2(0.5f, 0.5f);
            dialogRect.pivot = new Vector2(0.5f, 0.5f);
            dialogRect.sizeDelta = new Vector2(520, 760);

            var outline = dialog.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0.4f, 0.6f, 1f, 0.8f);
            outline.effectDistance = new Vector2(2, 2);

            Transform dlg = dialog.transform;

            // --- Title ---
            CreateLabel(dlg, "Title", "⚔  Multiplayer Co-op", 26,
                new Vector2(0, 350), new Vector2(480, 40), FontStyles.Bold, Color.white);

            // --- Subtitle ---
            CreateLabel(dlg, "Sub", "Host your game or join a friend's save.", 13,
                new Vector2(0, 318), new Vector2(480, 24), FontStyles.Normal,
                new Color(0.7f, 0.85f, 1f));

            // --- Info notes ---
            CreateLabel(dlg, "InfoHost",
                "Host: After clicking Host Game, load or continue a save in-game to begin. Clients take a moment to sync on join.",
                10.5f, new Vector2(0, 284), new Vector2(480, 30), FontStyles.Normal,
                new Color(1f, 0.85f, 0.45f));
            CreateLabel(dlg, "InfoTip",
                "Tip: Radmin VPN is a free LAN tunnel and works as an alternative to port forwarding.",
                10.5f, new Vector2(0, 257), new Vector2(480, 24), FontStyles.Normal,
                new Color(0.55f, 0.9f, 1f));

            float y = 230f;
            float rowH = 38f;
            Color labelColor = new Color(0.8f, 0.8f, 0.9f);

            // --- IP Field ---
            CreateLabel(dlg, "IpLabel", "Host IP Address:", 13,
                new Vector2(-90f, y), new Vector2(160, 28), FontStyles.Normal, labelColor);
            _ipInput = CreateInputField(dlg, "IpInput", MultiplayerPlugin.Instance.LastIP.Value,
                new Vector2(100f, y), new Vector2(220, 30));
            y -= rowH;

            // --- Port Field ---
            CreateLabel(dlg, "PortLabel", "Port:", 13,
                new Vector2(-90f, y), new Vector2(160, 28), FontStyles.Normal, labelColor);
            _portInput = CreateInputField(dlg, "PortInput", MultiplayerPlugin.Instance.LastPort.Value.ToString(),
                new Vector2(100f, y), new Vector2(220, 30));
            y -= rowH;

            // --- Character Name ---
            string savedName = PlayerPrefs.GetString("MP_CharName", "");
            CreateLabel(dlg, "NameLabel", "Your Character Name:", 13,
                new Vector2(-90f, y), new Vector2(160, 28), FontStyles.Normal, labelColor);
            _charNameInput = CreateInputField(dlg, "NameInput", savedName,
                new Vector2(100f, y), new Vector2(220, 30));
            y -= rowH;

            // --- Character Class ---
            string savedClass = PlayerPrefs.GetString("MP_CharClass", "");
            CreateLabel(dlg, "ClassLabel", "Your Class / Role:", 13,
                new Vector2(-90f, y), new Vector2(160, 28), FontStyles.Normal, labelColor);
            _charClassInput = CreateInputField(dlg, "ClassInput", savedClass,
                new Vector2(100f, y), new Vector2(220, 30));
            y -= rowH;

            // --- HP / Max HP / Level ---
            string savedHp = PlayerPrefs.GetString("MP_CharHp", "100");
            string savedMaxHp = PlayerPrefs.GetString("MP_CharMaxHp", "100");
            string savedLevel = PlayerPrefs.GetString("MP_CharLevel", "1");
            CreateLabel(dlg, "StatsLabel", "HP / Max HP / Level:", 13,
                new Vector2(-90f, y), new Vector2(160, 28), FontStyles.Normal, labelColor);
            _charHpInput    = CreateInputField(dlg, "HpInput",    savedHp,    new Vector2(30f,  y), new Vector2(72, 30));
            _charMaxHpInput = CreateInputField(dlg, "MaxHpInput", savedMaxHp, new Vector2(115f, y), new Vector2(72, 30));
            _charLevelInput = CreateInputField(dlg, "LevelInput", savedLevel, new Vector2(195f, y), new Vector2(56, 30));
            y -= rowH + 6f;

            // --- Character Background (multi-line) ---
            string savedBg = PlayerPrefs.GetString("MP_CharBg", "");
            CreateLabel(dlg, "BgLabel", "Background (optional):", 13,
                new Vector2(-90f, y), new Vector2(160, 24), FontStyles.Normal, labelColor);
            y -= 28f;
            _charBgInput = CreateMultilineInputField(dlg, "BgInput", savedBg,
                new Vector2(10f, y - 30f), new Vector2(450, 68));
            y -= (68f + 14f);

            // --- Personality / Goals (multi-line) ---
            string savedPersonality = PlayerPrefs.GetString("MP_CharPersonality", "");
            CreateLabel(dlg, "PersonalityLabel", "Personality / Goals:", 13,
                new Vector2(-90f, y), new Vector2(160, 24), FontStyles.Normal, labelColor);
            y -= 28f;
            _charPersonalityInput = CreateMultilineInputField(dlg, "PersonalityInput", savedPersonality,
                new Vector2(10f, y - 24f), new Vector2(450, 54));
            y -= (54f + 18f);

            // --- Physical Appearance (multi-line) ---
            string savedAppearance = PlayerPrefs.GetString("MP_CharAppearance", "");
            CreateLabel(dlg, "AppearanceLabel", "Physical Appearance:", 13,
                new Vector2(-90f, y), new Vector2(160, 24), FontStyles.Normal, labelColor);
            y -= 28f;
            _charAppearanceInput = CreateMultilineInputField(dlg, "AppearanceInput", savedAppearance,
                new Vector2(10f, y - 24f), new Vector2(450, 54));
            y -= (54f + 18f);

            // --- Status text ---
            _statusText = CreateLabel(dlg, "Status", "Ready.", 12,
                new Vector2(0, y), new Vector2(460, 24), FontStyles.Normal, new Color(0.6f, 0.9f, 0.6f));
            y -= rowH;

            // --- Buttons row ---
            _hostBtn = CreateButton(dlg, "HostBtn", "Host Game",
                new Vector2(-120, y - 10), new Vector2(150, 36),
                new Color(0.2f, 0.5f, 0.85f), OnHostClicked);

            _joinBtn = CreateButton(dlg, "JoinBtn", "Join Game",
                new Vector2(40, y - 10), new Vector2(150, 36),
                new Color(0.2f, 0.7f, 0.45f), OnJoinClicked);

            _spectateBtn = CreateButton(dlg, "SpectateBtn", "👁 Spectate",
                new Vector2(40, y - 50), new Vector2(150, 32),
                new Color(0.4f, 0.4f, 0.55f), OnSpectateClicked);

            _reconnectBtn = CreateButton(dlg, "ReconnectBtn", "🔄 Reconnect",
                new Vector2(-120, y - 50), new Vector2(150, 32),
                new Color(0.6f, 0.45f, 0.2f), OnReconnectClicked);
            // Only show if there's a saved PlayerId from a previous session
            _reconnectBtn.gameObject.SetActive(!string.IsNullOrEmpty(PlayerPrefs.GetString("MP_LastPlayerId", "")));

            _closeBtn = CreateButton(dlg, "CloseBtn", "✕",
                new Vector2(225, 360), new Vector2(32, 32),
                new Color(0.55f, 0.15f, 0.15f), () => Hide());

            RefreshButtonStates();
        }

        private void RefreshButtonStates()
        {
            if (_hostBtn == null) return;

            bool hosting = MultiplayerPlugin.IsHost;
            bool joined = MultiplayerPlugin.IsClient;

            _hostBtn.interactable = !joined;
            _joinBtn.interactable = !hosting;
            if (_spectateBtn != null) _spectateBtn.interactable = !hosting;
            if (_reconnectBtn != null)
            {
                _reconnectBtn.interactable = !hosting && !joined;
                _reconnectBtn.gameObject.SetActive(!string.IsNullOrEmpty(PlayerPrefs.GetString("MP_LastPlayerId", "")));
            }

            if (hosting)
            {
                var clients = MultiplayerPlugin.Server.GetClients();
                SetStatus($"✔ Hosting on port {MultiplayerPlugin.Server.Port} — {clients.Count} player(s) connected.", new Color(0.4f, 1f, 0.4f));
            }
            else if (joined)
            {
                SetStatus($"✔ Connected to {MultiplayerPlugin.Client.HostAddress}:{MultiplayerPlugin.Client.Port}", new Color(0.4f, 1f, 0.4f));
            }
            else
            {
                SetStatus("Ready.", new Color(0.6f, 0.9f, 0.6f));
            }
        }

        private void OnHostClicked()
        {
            if (MultiplayerPlugin.IsHost)
            {
                MultiplayerPlugin.StopHost();
                SetStatus("Stopped hosting.", new Color(1f, 0.8f, 0.4f));
                RefreshButtonStates();
                return;
            }

            if (!int.TryParse(_portInput.text, out int port)) port = 7777;
            MultiplayerPlugin.Instance.LastPort.Value = port;

            SetStatus("Starting server...", Color.yellow);
            MultiplayerPlugin.StartHost(port,
                onSuccess: () =>
                {
                    SetStatus($"✔ Hosting on port {port}. Share your IP with friends!", new Color(0.4f, 1f, 0.4f));
                    _hostBtn.GetComponentInChildren<TMP_Text>().text = "Stop Hosting";
                    RefreshButtonStates();
                },
                onError: (err) => SetStatus($"✖ {err}", new Color(1f, 0.4f, 0.4f))
            );
        }

        private void OnJoinClicked()
        {
            if (MultiplayerPlugin.IsClient)
            {
                MultiplayerPlugin.StopClient();
                SetStatus("Disconnected.", new Color(1f, 0.8f, 0.4f));
                _joinBtn.GetComponentInChildren<TMP_Text>().text = "Join Game";
                return;
            }

            string ip = _ipInput.text.Trim();
            if (string.IsNullOrEmpty(ip)) { SetStatus("✖ Enter a host IP address.", new Color(1f, 0.4f, 0.4f)); return; }
            if (!int.TryParse(_portInput.text, out int port)) port = 7777;

            string charName = _charNameInput.text.Trim();
            if (string.IsNullOrEmpty(charName)) { SetStatus("✖ Enter a character name.", new Color(1f, 0.4f, 0.4f)); return; }

            PlayerPrefs.SetString("MP_CharName", charName);
            PlayerPrefs.SetString("MP_CharClass", _charClassInput.text.Trim());
            PlayerPrefs.SetString("MP_CharBg", _charBgInput.text.Trim());
            PlayerPrefs.SetString("MP_CharPersonality", _charPersonalityInput.text.Trim());
            PlayerPrefs.SetString("MP_CharAppearance", _charAppearanceInput.text.Trim());
            PlayerPrefs.SetString("MP_CharHp", _charHpInput.text.Trim());
            PlayerPrefs.SetString("MP_CharMaxHp", _charMaxHpInput.text.Trim());
            PlayerPrefs.SetString("MP_CharLevel", _charLevelInput.text.Trim());

            long hp    = long.TryParse(_charHpInput.text,    out long hpVal)    ? hpVal    : 100;
            long maxHp = long.TryParse(_charMaxHpInput.text, out long maxHpVal) ? maxHpVal : 100;
            int  level = int.TryParse (_charLevelInput.text, out int  lvlVal)   ? lvlVal   : 1;

            MultiplayerPlugin.Instance.LastIP.Value = ip;
            MultiplayerPlugin.Instance.LastPort.Value = port;
            MultiplayerPlugin.LocalCharacterName = charName;

            SetStatus($"Connecting to {ip}:{port}...", Color.yellow);

            var charInfo = new RemoteCharacterInfo
            {
                PlayerName = charName,
                CharacterName = charName,
                CharacterClass = _charClassInput.text.Trim(),
                CharacterBackground = _charBgInput.text.Trim(),
                Personality = _charPersonalityInput.text.Trim(),
                CharacterAppearance = _charAppearanceInput.text.Trim(),
                Health = hp,
                MaxHealth = maxHp,
                Level = level
            };

            MultiplayerPlugin.StartClient(ip, port, charInfo,
                onConnected: (welcome) =>
                {
                    SetStatus($"✔ Connected! Loading host's world...", new Color(0.4f, 1f, 0.4f));
                    _joinBtn.GetComponentInChildren<TMP_Text>().text = "Disconnect";
                    CoopStatusOverlay.Show(welcome);
                    Hide();
                },
                onDisconnected: (reason) =>
                {
                    SetStatus($"✖ {reason}", new Color(1f, 0.4f, 0.4f));
                    _joinBtn.GetComponentInChildren<TMP_Text>().text = "Join Game";
                    RefreshButtonStates();
                }
            );
        }

        private void OnSpectateClicked()
        {
            if (MultiplayerPlugin.IsClient)
            {
                MultiplayerPlugin.StopClient();
                SetStatus("Disconnected.", new Color(1f, 0.8f, 0.4f));
                return;
            }

            string ip = _ipInput.text.Trim();
            if (string.IsNullOrEmpty(ip)) { SetStatus("✖ Enter a host IP address.", new Color(1f, 0.4f, 0.4f)); return; }
            if (!int.TryParse(_portInput.text, out int port)) port = 7777;

            MultiplayerPlugin.Instance.LastIP.Value = ip;
            MultiplayerPlugin.Instance.LastPort.Value = port;
            MultiplayerPlugin.LocalCharacterName = "Spectator";

            SetStatus($"Connecting as spectator to {ip}:{port}...", Color.yellow);

            var charInfo = new RemoteCharacterInfo
            {
                PlayerName = "Spectator",
                CharacterName = "Spectator",
                CharacterClass = "",
                Health = 0,
                MaxHealth = 0,
                Level = 0,
                IsSpectator = true
            };

            MultiplayerPlugin.StartClient(ip, port, charInfo,
                onConnected: (welcome) =>
                {
                    SetStatus($"✔ Spectating!", new Color(0.4f, 1f, 0.4f));
                    CoopStatusOverlay.Show(welcome);
                    Hide();
                },
                onDisconnected: (reason) =>
                {
                    SetStatus($"✖ {reason}", new Color(1f, 0.4f, 0.4f));
                    RefreshButtonStates();
                }
            );
        }

        private void OnReconnectClicked()
        {
            if (MultiplayerPlugin.IsClient || MultiplayerPlugin.IsHost) return;

            string previousId = PlayerPrefs.GetString("MP_LastPlayerId", "");
            if (string.IsNullOrEmpty(previousId))
            {
                SetStatus("✖ No previous session to reconnect to.", new Color(1f, 0.4f, 0.4f));
                return;
            }

            // Auto-fill IP/port from saved values if the fields are empty
            string savedHost = PlayerPrefs.GetString("MP_LastHost", "");
            int savedPort = PlayerPrefs.GetInt("MP_LastPort", 7777);
            if (!string.IsNullOrEmpty(savedHost) && string.IsNullOrEmpty(_ipInput.text.Trim()))
                _ipInput.text = savedHost;
            if (string.IsNullOrEmpty(_portInput.text.Trim()))
                _portInput.text = savedPort.ToString();

            string ip = _ipInput.text.Trim();
            if (string.IsNullOrEmpty(ip)) { SetStatus("✖ Enter a host IP address.", new Color(1f, 0.4f, 0.4f)); return; }
            if (!int.TryParse(_portInput.text, out int port)) port = 7777;

            // Build character info from form fields (or saved PlayerPrefs)
            string charName = _charNameInput.text.Trim();
            if (string.IsNullOrEmpty(charName)) charName = PlayerPrefs.GetString("MP_CharName", "Player");

            long hp    = long.TryParse(_charHpInput.text,    out long hpVal)    ? hpVal    : 100;
            long maxHp = long.TryParse(_charMaxHpInput.text, out long maxHpVal) ? maxHpVal : 100;
            int  level = int.TryParse (_charLevelInput.text, out int  lvlVal)   ? lvlVal   : 1;

            MultiplayerPlugin.Instance.LastIP.Value = ip;
            MultiplayerPlugin.Instance.LastPort.Value = port;
            MultiplayerPlugin.LocalCharacterName = charName;

            SetStatus($"Reconnecting to {ip}:{port}...", Color.yellow);

            var charInfo = new RemoteCharacterInfo
            {
                PlayerName = charName,
                CharacterName = charName,
                CharacterClass = _charClassInput.text.Trim(),
                CharacterBackground = _charBgInput.text.Trim(),
                Personality = _charPersonalityInput.text.Trim(),
                CharacterAppearance = _charAppearanceInput.text.Trim(),
                Health = hp,
                MaxHealth = maxHp,
                Level = level
            };

            MultiplayerPlugin.StartClientReconnect(ip, port, previousId, charInfo,
                onReconnected: (result) =>
                {
                    SetStatus($"✔ Reconnected! Replayed {result.CatchUpTurns?.Length ?? 0} missed turn(s).", new Color(0.4f, 1f, 0.4f));
                    CoopStatusOverlay.Show(new WelcomePayload
                    {
                        AssignedPlayerId = result.AssignedPlayerId,
                        HostCharacterName = "Host",
                        CurrentLocation = ""
                    });
                    Hide();
                },
                onDisconnected: (reason) =>
                {
                    SetStatus($"✖ {reason}", new Color(1f, 0.4f, 0.4f));
                    RefreshButtonStates();
                }
            );
        }

        private void SetStatus(string msg, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = msg;
            _statusText.color = color;
        }

        // ---- UI Builder Helpers ----

        private GameObject CreatePanel(Transform parent, string name, Color color,
            Vector2? anchorMin = null, Vector2? anchorMax = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin ?? new Vector2(0.5f, 0.5f);
            rt.anchorMax = anchorMax ?? new Vector2(0.5f, 0.5f);
            return go;
        }

        private TMP_Text CreateLabel(Transform parent, string name, string text, float size,
            Vector2 pos, Vector2 sizeDelta, FontStyles style, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lbl = go.AddComponent<TextMeshProUGUI>();
            lbl.text = text;
            lbl.fontSize = size;
            lbl.fontStyle = style;
            lbl.color = color;
            lbl.alignment = TextAlignmentOptions.Center;
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
            return lbl;
        }

        private TMP_InputField CreateInputField(Transform parent, string name, string defaultValue,
            Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.12f, 0.95f);

            var outline = go.AddComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0.3f, 0.5f, 0.8f, 0.6f);

            var inputField = go.AddComponent<TMP_InputField>();

            // Viewport clips text to the field's visible area (prevents overflow into adjacent UI)
            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(go.transform, false);
            viewportGo.AddComponent<RectMask2D>();
            var viewportRT = viewportGo.GetComponent<RectTransform>();
            viewportRT.anchorMin = Vector2.zero;
            viewportRT.anchorMax = Vector2.one;
            viewportRT.offsetMin = new Vector2(5, 2);
            viewportRT.offsetMax = new Vector2(-5, -2);
            inputField.textViewport = viewportRT;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(viewportGo.transform, false);
            var textComp = textGo.AddComponent<TextMeshProUGUI>();
            textComp.fontSize = 13;
            textComp.color = Color.white;
            var textRT = textGo.GetComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;

            inputField.textComponent = textComp;
            inputField.text = defaultValue;

            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;

            return inputField;
        }

        private TMP_InputField CreateMultilineInputField(Transform parent, string name, string defaultValue,
            Vector2 pos, Vector2 sizeDelta)
        {
            var field = CreateInputField(parent, name, defaultValue, pos, sizeDelta);
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
            // Align text to the top inside the box
            field.textComponent.alignment = TextAlignmentOptions.TopLeft;
            return field;
        }

        private Button CreateButton(Transform parent, string name, string label,
            Vector2 pos, Vector2 sizeDelta, Color color, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = color;

            var btn = go.AddComponent<Button>();
            var cs = btn.colors;
            cs.highlightedColor = color * 1.2f;
            cs.pressedColor = color * 0.8f;
            btn.colors = cs;
            btn.onClick.AddListener(() => onClick?.Invoke());

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 14;
            text.fontStyle = FontStyles.Bold;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            var tRT = textGo.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero;
            tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;

            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;

            return btn;
        }
    }
}
