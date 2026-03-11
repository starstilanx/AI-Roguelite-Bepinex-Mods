using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using AIROG_Multiplayer.Combat;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;
using AIROG_Multiplayer.Patches;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIROG_Multiplayer
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class MultiplayerPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.airog.multiplayer";
        public const string NAME = "AIROG Multiplayer";
        public const string VERSION = "2.0.0";

        public static MultiplayerPlugin Instance { get; private set; }
        public ManualLogSource Log => base.Logger;

        // Config
        public ConfigEntry<int> LastPort;
        public ConfigEntry<string> LastIP;
        public ConfigEntry<bool> WaitForParty;    // v2.0: gate host turn until all clients submit
        public ConfigEntry<float> PartyWaitTimeout;  // seconds before auto-proceeding

        // Network state
        public static AIROGServer Server { get; private set; }
        public static AIROGClient Client { get; private set; }

        public static bool IsHost => Server != null && Server.IsRunning;
        public static bool IsClient => Client != null && Client.IsConnected;
        public static bool IsMultiplayer => IsHost || IsClient;

        /// <summary>
        /// True when this instance is a joining client (not the host).
        /// Patches use this to redirect actions to the host and block local AI turns.
        /// </summary>
        public static bool IsClientMode { get; private set; }

        /// <summary>
        /// Cached save top-level directory for use from background threads.
        /// Set when starting or joining a session (on main thread).
        /// </summary>
        public static string SaveTopLvlDir { get; private set; }

        // Local client's display character name (set before connecting)
        public static string LocalCharacterName { get; set; } = "Player";

        /// <summary>
        /// Full character info for the local client (set before connecting).
        /// Clients update this when they edit their HP and send CharacterUpdate packets.
        /// </summary>
        public static RemoteCharacterInfo LocalCharacterInfo { get; set; }

        private Harmony _harmony;
        private bool _applicationQuitting = false;

        // --- Story chain polling ---
        private static int _lastStoryTurnCount = 0;
        private static GameplayManager _cachedManager = null;
        // True when a save-broadcast coroutine is already queued (avoids duplicate coroutines)
        private static bool _saveBroadcastPending = false;

        // --- v2.0: Turn gate state ---
        private static bool _waitingForParty = false;
        private static readonly HashSet<string> _clientsReady = new HashSet<string>();
        private static float _partyWaitStartTime = -1f;

        // --- Unity lifecycle ---

        private void Awake()
        {
            // Prevent duplicate instances if BepInEx somehow re-runs Awake after a scene load.
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // Explicitly mark the root GameObject as DontDestroyOnLoad.
            // BepInEx normally does this, but some games tear down all objects including
            // BepInEx's root on scene transitions — this makes it explicit.
            DontDestroyOnLoad(transform.root.gameObject);

            LastPort = Config.Bind("Network", "Port", 7777, "Default port for hosting/joining.");
            LastIP = Config.Bind("Network", "LastIP", "127.0.0.1", "Last IP used to join a game.");
            WaitForParty = Config.Bind("Multiplayer", "WaitForParty", true,
                "If true, host's turn is held until all clients submit an action (or timeout expires).");
            PartyWaitTimeout = Config.Bind("Multiplayer", "PartyWaitTimeoutSeconds", 60f,
                "Seconds to wait for party before auto-proceeding.");

            _harmony = new Harmony(GUID);
            _harmony.PatchAll();

            // Reset the cached manager when a scene loads so we re-fetch from the new scene.
            SceneManager.sceneLoaded += OnSceneLoaded;

            Logger.LogInfo($"{NAME} v{VERSION} loaded.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Re-fetch GameplayManager reference after every scene transition.
            _cachedManager = null;
            bool clientConnected = Client?.IsConnected ?? false;
            UnityEngine.Debug.Log($"[MP-DIAG] OnSceneLoaded: scene={scene.name} IsClientMode={IsClientMode} IsHost={IsHost} Client={(Client != null ? "set" : "null")} IsConnected={clientConnected}");
            Logger.LogInfo($"[Multiplayer] Scene '{scene.name}' loaded. IsClientMode={IsClientMode}, IsHost={IsHost}");
        }

        private void Update()
        {
            var server = Server;
            var client = Client;

            // Drain server's main-thread callback queue
            if (server != null)
            {
                while (server.MainThreadQueue.TryDequeue(out Action act))
                    act.Invoke();
            }

            // Always drain client's main-thread callback queue (CoopStatusOverlay does not drain it)
            if (client != null)
            {
                while (client.MainThreadQueue.TryDequeue(out Action act))
                    act.Invoke();
            }

            // Poll StoryChain for new turns (host only)
            if (IsHost && server != null)
                PollAndBroadcastNewStoryTurns(server);

            // Party wait timeout
            if (_waitingForParty && _partyWaitStartTime > 0f)
            {
                if (Time.time - _partyWaitStartTime >= (PartyWaitTimeout?.Value ?? 60f))
                {
                    Instance?.Log.LogWarning("[Host] Party wait timed out — proceeding with available actions.");
                    ReleasePartyGate();
                }
            }
        }

        // --- Turn gate (v2.0) ---

        /// <summary>
        /// Called from GameplayMultiplayerPatch prefix — returns true if we should BLOCK the turn.
        /// </summary>
        public static bool ShouldBlockTurn(out string reason)
        {
            reason = null;
            if (!IsHost || Server == null) return false;
            if (!(Instance?.WaitForParty?.Value ?? true)) return false;

            var clients = Server.GetClients().Where(c => !c.IsSpectator).ToList();
            if (clients.Count == 0) return false; // No non-spectator clients, no wait

            // Find clients that haven't checked in yet
            var notReady = clients.Where(c => !c.IsTurnReady).ToList();
            if (notReady.Count == 0) return false; // Everyone is ready

            // Start the gate if not already waiting
            if (!_waitingForParty)
            {
                _waitingForParty = true;
                _partyWaitStartTime = Time.time;

                // Notify all clients it's their turn
                Server.BroadcastTurnBegin();
                Instance?.Log.LogInfo($"[Host] Waiting for {notReady.Count} client(s) to submit actions...");
            }

            // Update waiting status broadcast
            int readyCount = clients.Count - notReady.Count;
            Server.BroadcastWaitingForParty(readyCount, clients.Count);

            reason = $"Waiting for party ({readyCount}/{clients.Count})...";
            return true;
        }

        /// <summary>
        /// Called when a client sends TurnReady — checks if all are in.
        /// </summary>
        public static void OnClientTurnReady(ConnectedClient client)
        {
            if (!_waitingForParty) return;

            var allClients = Server?.GetClients();
            if (allClients == null) return;

            var clients = allClients.Where(c => !c.IsSpectator).ToList();
            int readyCount = clients.Count(c => c.IsTurnReady);
            int totalCount = clients.Count;

            Instance?.Log.LogInfo($"[Host] {client.PlayerName} is ready ({readyCount}/{totalCount}).");
            Server?.BroadcastWaitingForParty(readyCount, totalCount);

            if (readyCount >= totalCount)
                ReleasePartyGate();
        }

        private static void ReleasePartyGate()
        {
            _waitingForParty = false;
            _partyWaitStartTime = -1f;
            Server?.GetClients().ForEach(c => c.ResetTurn());
            Instance?.Log.LogInfo("[Host] Party gate released.");
        }

        /// <summary>
        /// Called post-turn: resets gate state.
        /// The actual save broadcast is handled by Postfix_WriteSaveFile which fires
        /// AFTER the async AI work and file write complete (correct timing).
        /// </summary>
        public static void OnTurnCompleted(GameplayManager manager)
        {
            _waitingForParty = false;
            _partyWaitStartTime = -1f;
            Server?.GetClients().ForEach(c => c.ResetTurn());
        }

        /// <summary>
        /// Waits for the async LogText body (AddLastIlluStoryTurn) to complete, then forces
        /// a save write. The Postfix_WriteSaveFile patch picks this up and broadcasts to clients.
        /// Started from Postfix_LogText_StoryTurn so saves are event-driven, not polling-based.
        /// </summary>
        public static System.Collections.IEnumerator SaveAndBroadcastAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            _saveBroadcastPending = false;
            UnityEngine.Debug.Log($"[MP-DIAG] SaveAndBroadcastAfterDelay firing: IsHost={IsHost}, _cachedManager={(_cachedManager == null ? "null" : "set")}");
            Instance?.Log.LogInfo($"[Host] SaveAndBroadcastAfterDelay: IsHost={IsHost}, _cachedManager={(_cachedManager == null ? "null" : "set")}");
            if (!IsHost || _cachedManager == null) yield break;
            try { SaveIO.WriteSaveFile(_cachedManager); }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] SaveAndBroadcastAfterDelay error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the host's current save file, compresses it, and broadcasts to all clients.
        /// Called from Postfix_WriteSaveFile so the data is always fresh.
        /// </summary>
        public static void BroadcastSaveData(GameplayManager manager)
        {
            if (Server == null || manager == null)
            {
                Instance?.Log.LogWarning($"[Host] BroadcastSaveData: skipped (Server={Server != null}, manager={manager != null})");
                return;
            }
            try
            {
                string hostSaveSubDir = SS.I.saveSubDirAsArg;
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, hostSaveSubDir);
                string savePath = Path.Combine(saveDir, "my_save.txt");
                Instance?.Log.LogInfo($"[Host] BroadcastSaveData: saveSubDirAsArg='{hostSaveSubDir}' savePath='{savePath}' exists={File.Exists(savePath)}");
                if (!File.Exists(savePath)) return;

                string saveJson = File.ReadAllText(savePath);
                string placeName = manager.currentPlace?.name ?? "";
                string placeDesc = manager.currentPlace?.description ?? "";
                var polygonFiles = ReadPolygonFiles(saveDir);

                Server.BroadcastSaveFile(saveJson, placeName, placeDesc, hostSaveSubDir, polygonFiles);
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] BroadcastSaveData error: {ex.Message}");
            }
        }

        private static readonly HashSet<string> _nonPolygonFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "my_save", "my_save_lite", "my_save_backup" };

        private static Dictionary<string, string> ReadPolygonFiles(string saveDir)
        {
            var result = new Dictionary<string, string>();
            try
            {
                foreach (string file in Directory.GetFiles(saveDir, "*.txt"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (_nonPolygonFileNames.Contains(name)) continue;
                    result[name] = File.ReadAllText(file);
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogWarning($"[Host] ReadPolygonFiles error: {ex.Message}");
            }
            return result;
        }

        private static void PollAndBroadcastNewStoryTurns(AIROGServer server)
        {
            try
            {
                if (_cachedManager == null)
                    _cachedManager = FindObjectOfType<GameplayManager>();
                if (_cachedManager == null) return;

                var turns = _cachedManager.playerCharacter?.pcGameEntity?.storyChain?.storyTurns;
                if (turns == null) return;

                int currentCount = turns.Count;
                if (currentCount <= _lastStoryTurnCount) return;

                for (int i = _lastStoryTurnCount; i < currentCount; i++)
                {
                    string text = turns[i].getCombinedStr()?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    UnityEngine.Debug.Log($"[MP-DIAG] PollAndBroadcast: new story turn {i} ({text.Length} chars)");
                    Instance?.Log.LogInfo($"[Host] Polling: broadcasting story turn {i} ({text.Length} chars)");
                    server.BroadcastStoryTurn(new StoryEntry
                    {
                        Text = text,
                        AuthorName = "Narrator",
                        IsPlayerAction = false,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }

                _lastStoryTurnCount = currentCount;

                // Trigger a save broadcast so clients receive the updated game state.
                // This is needed for the non-unified game flow where LogText(StoryTurn,IllustratedStoryTurn)
                // is never called (so Postfix_LogText_StoryTurn never fires).
                // The 2-second delay lets the game finish entity updates before we snapshot the save.
                if (!_saveBroadcastPending && Instance != null)
                {
                    _saveBroadcastPending = true;
                    Instance.StartCoroutine(SaveAndBroadcastAfterDelay(2.0f));
                    Instance?.Log.LogInfo("[Host] Scheduled save broadcast after new story turn detected.");
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] PollStoryTurns error: {ex.Message}");
                _cachedManager = null;
            }
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            // Use UnityEngine.Debug.Log (not BepInEx Logger) so this is unconditionally visible
            // even if the BepInEx logging infrastructure is tearing down during scene transition.
            UnityEngine.Debug.Log($"[MP-DIAG] OnDestroy called. _applicationQuitting={_applicationQuitting} IsClientMode={IsClientMode}");

            if (!_applicationQuitting)
            {
                // This is a Unity scene transition, NOT an application exit.
                // Preserve all TCP network state — Client/Server/IsClientMode stay alive.
                // The static fields and background threads continue running.
                // Update() will keep draining MainThreadQueue as long as this MonoBehaviour lives.
                Logger.LogInfo("[Multiplayer] Scene transition detected — network state preserved.");
                if (Instance == this) Instance = null;
                UnityEngine.Debug.Log($"[MP-DIAG] OnDestroy: Instance set to null, IsClientMode still={IsClientMode}");
                return;
            }

            // True application exit — clean everything up.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            StopHost();
            StopClient();
            _harmony?.UnpatchSelf();
        }

        // --- Host API ---

        public static void StartHost(int port,
            Action onSuccess = null,
            Action<string> onError = null)
        {
            if (IsHost) return;

            _lastStoryTurnCount = 0;
            _cachedManager = null;
            _saveBroadcastPending = false;
            _waitingForParty = false;
            _partyWaitStartTime = -1f;
            _clientsReady.Clear();
            UnityEngine.Debug.Log("[MP-DIAG] IsClientMode = false (StartHost)");
            IsClientMode = false;
            SaveTopLvlDir = SS.I?.saveTopLvlDir ?? "";

            // Initialize inventory database in the host's save directory
            MPInventoryManager.Initialize(SaveTopLvlDir, SS.I?.saveSubDirAsArg ?? "save");

            try
            {
                Server = new AIROGServer();

                Server.OnClientConnected += (client, hello) => OnClientConnected(client, hello);
                Server.OnClientDisconnected += (client) => OnClientDisconnected(client);
                Server.OnActionReceived += (client, action) => OnClientActionReceived(client, action);
                Server.OnChatReceived += (client, chat) => OnChatReceived_Host(client, chat);
                Server.OnTurnReady += (client) => OnClientTurnReady(client);
                Server.OnCharacterUpdateReceived += (client, info) => OnClientCharacterUpdated(client, info);
                Server.OnItemTransferReceived += (client, transfer) => OnItemTransferReceived_Host(client, transfer);
                Server.OnClientReconnected += (client, reconnPayload) => OnClientReconnectedHandler(client, reconnPayload);
                Server.OnPrivateActionReceived += (client, action) => OnPrivateActionReceived_Host(client, action);
                Server.OnCombatActionReceived += (client, action) => OnCombatActionReceived_Host(client, action);

                Server.Start(port);
                onSuccess?.Invoke();

                // Show the host's own co-op overlay
                CoopStatusOverlay.ShowForHost(port);

                Instance.Log.LogInfo($"[Host] Server started on port {port}.");
            }
            catch (Exception ex)
            {
                Server = null;
                string err = $"Failed to start server: {ex.Message}";
                Instance.Log.LogError($"[Host] {err}");
                onError?.Invoke(err);
            }
        }

        public static void StopHost()
        {
            if (Server == null) return;
            Server.BroadcastChat("Server", "Host has ended the session.", isSystem: true);
            Server.Stop();
            Server = null;
            Instance?.Log.LogInfo("[Host] Server stopped.");
        }

        // --- Client API ---

        public static void StartClient(string host, int port,
            RemoteCharacterInfo character,
            Action<WelcomePayload> onConnected = null,
            Action<string> onDisconnected = null)
        {
            if (IsClient) return;

            UnityEngine.Debug.Log("[MP-DIAG] IsClientMode = true (StartClient)");
            IsClientMode = true;

            // Cache the save path for background thread access in AIROGClient
            SaveTopLvlDir = SS.I?.saveTopLvlDir
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "MaxLoh", "AI Roguelite", "save");

            // Initialize inventory database in the client's save directory
            MPInventoryManager.Initialize(SaveTopLvlDir, "mp_client");

            LocalCharacterInfo = character;

            Client = new AIROGClient();

            Client.OnConnected += (welcome) =>
            {
                Instance.Log.LogInfo($"[Client] Connected to {host}:{port}. Host location: {welcome.CurrentLocation}");
                // Save PlayerId for reconnection
                PlayerPrefs.SetString("MP_LastPlayerId", Client.AssignedPlayerId ?? "");
                PlayerPrefs.SetString("MP_LastHost", host);
                PlayerPrefs.SetInt("MP_LastPort", port);
                onConnected?.Invoke(welcome);
            };
            Client.OnDisconnected += (reason) =>
            {
                UnityEngine.Debug.Log($"[MP-DIAG] IsClientMode = false (OnDisconnected: {reason})");
                Instance?.Log.LogWarning($"[Client] Disconnected: {reason}");
                Client = null;
                IsClientMode = false;
                onDisconnected?.Invoke(reason);
                CoopStatusOverlay.Instance?.SetStatus($"Disconnected: {reason}", connected: false);
            };

            // Route party/chat/status events to the lightweight overlay
            Client.OnPartyUpdated += (party) => CoopStatusOverlay.Instance?.UpdateParty(party.Members);
            Client.OnChatReceived += (chat) => CoopStatusOverlay.Instance?.AddChat(chat.SenderName, chat.Message, chat.IsSystem);
            Client.OnWaitingForParty += (w) => CoopStatusOverlay.Instance?.SetStatus($"⏳ {w.ReadyCount}/{w.TotalCount} ready");
            Client.OnTurnBegin += () => CoopStatusOverlay.Instance?.SetStatus("⚔ Your turn — submit an action!");

            // Save data is handled entirely in AIROGClient (triggers scene load / game reload)
            // We just notify the overlay of the place summary
            Client.OnSaveDataReceived += (save) =>
            {
                if (!string.IsNullOrEmpty(save.CurrentPlaceName))
                    CoopStatusOverlay.Instance?.SetStatus($"📍 {save.CurrentPlaceName}");
            };

            // Image file is already saved to disk by AIROGClient.HandleStoryImage.
            // Refresh the displayed texture by finding the IllustratedStoryTurn and marking it dirty.
            // This avoids a full LoadGame reload (which causes the stuck-UI issue).
            Client.OnStoryImageReceived += (img) =>
            {
                try
                {
                    if (!IsClientMode) return;
                    string uuid = System.IO.Path.GetFileNameWithoutExtension(img?.FileName ?? "");
                    if (string.IsNullOrEmpty(uuid)) return;

                    UnityEngine.Debug.Log($"[MP-DIAG] OnStoryImageReceived: uuid='{uuid}'");

                    // Try immediate lookup. If the entity map isn't populated yet (save reload
                    // still in progress), kick off a coroutine that retries for up to 10 seconds.
                    if (!TryApplyClientImage(uuid))
                    {
                        Instance?.Log.LogInfo($"[Client] Image {uuid} not in entity map yet — will retry.");
                        Instance?.StartCoroutine(RetryApplyClientImage(uuid));
                    }
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnStoryImageReceived error: {ex.Message}");
                }
            };

            // Location updates feed the map overlay
            Client.OnLocationUpdated += (loc) => MPMapOverlay.Instance?.UpdateLocation(loc);

            // Inventory sync: update the local DB and refresh the UI panel
            Client.OnInventoryReceived += (inv) =>
            {
                try
                {
                    MPInventoryManager.LoadFromJson(inv?.InventoryJson);
                    MPInventoryManager.Save();
                    MPInventoryUI.Instance?.Refresh();
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnInventoryReceived error: {ex.Message}");
                }
            };

            // Quest sync: update the quest UI panel
            Client.OnQuestSyncReceived += (qs) =>
            {
                MPQuestUI.Instance?.UpdateQuests(qs?.Quests);
            };

            // Private action results
            Client.OnPrivateResultReceived += (pr) =>
            {
                CoopStatusOverlay.Instance?.ShowPrivateResult(pr?.ResultText ?? "");
            };

            // Combat events
            Client.OnCombatBegin += (cb) =>
            {
                CombatManager.BeginCombat(cb.TurnOrder, cb.EnemyNames, cb.TurnOrder?.Length ?? 0);
                CoopStatusOverlay.Instance?.AddChat("Combat", $"<color=#FF6666>⚔ Combat! Enemies: {string.Join(", ", cb.EnemyNames ?? new string[0])}</color>", isSystem: true);
            };
            Client.OnCombatTurnNotify += (ct) =>
            {
                CoopStatusOverlay.Instance?.SetStatus($"⚔ Round {ct.RoundNumber} — Submit your action!");
            };
            Client.OnCombatResult += (cr) =>
            {
                CoopStatusOverlay.Instance?.AddChat("Combat", $"<color=#FFAA66>{cr.NarrativeText}</color>", isSystem: true);
            };
            Client.OnCombatEnd += () =>
            {
                CombatManager.EndCombat();
                CoopStatusOverlay.Instance?.AddChat("Combat", "<color=#66CC66>⚔ Combat has ended.</color>", isSystem: true);
            };

            // Relay story turns into the game's own log view when in-game,
            // or into the chat panel when still on the main menu.
            Client.OnStoryTurnReceived += (entry) =>
            {
                try
                {
                    var logView = SS.I?.hackyManager?.gameLogView;
                    if (logView != null)
                        logView.QueueLogText(entry.Text);
                    else
                        CoopStatusOverlay.Instance?.AddChat(entry.AuthorName ?? "Narrator", entry.Text);
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnStoryTurnReceived error: {ex.Message}");
                }
            };

            // Reconnection: replay catch-up story turns when reconnect succeeds
            Client.OnReconnected += (result) =>
            {
                Instance.Log.LogInfo($"[Client] Reconnected! PlayerId restored: {result.AssignedPlayerId}");
                PlayerPrefs.SetString("MP_LastPlayerId", result.AssignedPlayerId ?? "");

                // Replay missed story turns
                if (result.CatchUpTurns != null)
                {
                    foreach (var turn in result.CatchUpTurns)
                    {
                        var logView = SS.I?.hackyManager?.gameLogView;
                        if (logView != null)
                            logView.QueueLogText(turn.Text);
                        else
                            CoopStatusOverlay.Instance?.AddChat(turn.AuthorName ?? "Narrator", turn.Text);
                    }
                    Instance.Log.LogInfo($"[Client] Replayed {result.CatchUpTurns.Length} catch-up story turn(s).");
                }
            };

            Client.Connect(host, port, character);
        }

        public static void StopClient()
        {
            if (Client == null) return;
            Client.Disconnect("Player left.");
            Client = null;
            UnityEngine.Debug.Log("[MP-DIAG] IsClientMode = false (StopClient)");
            IsClientMode = false;
            Instance?.Log.LogInfo("[Client] Disconnected.");
        }

        /// <summary>
        /// Reconnects to a host using a previously saved PlayerId.
        /// Reuses the same event wiring as StartClient but sends a Reconnect packet instead of Hello.
        /// </summary>
        public static void StartClientReconnect(string host, int port, string previousPlayerId,
            RemoteCharacterInfo character,
            Action<ReconnectResultPayload> onReconnected = null,
            Action<string> onDisconnected = null)
        {
            if (IsClient) return;

            IsClientMode = true;
            SaveTopLvlDir = SS.I?.saveTopLvlDir
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "MaxLoh", "AI Roguelite", "save");

            MPInventoryManager.Initialize(SaveTopLvlDir, "mp_client");
            LocalCharacterInfo = character;

            Client = new AIROGClient();

            Client.OnReconnected += (result) =>
            {
                Instance.Log.LogInfo($"[Client] Reconnected! PlayerId: {result.AssignedPlayerId}");
                PlayerPrefs.SetString("MP_LastPlayerId", result.AssignedPlayerId ?? "");

                // Replay missed story turns
                if (result.CatchUpTurns != null)
                {
                    foreach (var turn in result.CatchUpTurns)
                    {
                        var logView = SS.I?.hackyManager?.gameLogView;
                        if (logView != null)
                            logView.QueueLogText(turn.Text);
                        else
                            CoopStatusOverlay.Instance?.AddChat(turn.AuthorName ?? "Narrator", turn.Text);
                    }
                    Instance.Log.LogInfo($"[Client] Replayed {result.CatchUpTurns.Length} catch-up turn(s).");
                }

                onReconnected?.Invoke(result);
            };

            Client.OnDisconnected += (reason) =>
            {
                Instance?.Log.LogWarning($"[Client] Disconnected: {reason}");
                Client = null;
                IsClientMode = false;
                onDisconnected?.Invoke(reason);
                CoopStatusOverlay.Instance?.SetStatus($"Disconnected: {reason}", connected: false);
            };

            // Wire the same events as StartClient
            Client.OnPartyUpdated += (party) => CoopStatusOverlay.Instance?.UpdateParty(party.Members);
            Client.OnChatReceived += (chat) => CoopStatusOverlay.Instance?.AddChat(chat.SenderName, chat.Message, chat.IsSystem);
            Client.OnWaitingForParty += (w) => CoopStatusOverlay.Instance?.SetStatus($"⏳ {w.ReadyCount}/{w.TotalCount} ready");
            Client.OnTurnBegin += () => CoopStatusOverlay.Instance?.SetStatus("⚔ Your turn — submit an action!");

            Client.OnSaveDataReceived += (save) =>
            {
                if (!string.IsNullOrEmpty(save.CurrentPlaceName))
                    CoopStatusOverlay.Instance?.SetStatus($"📍 {save.CurrentPlaceName}");
            };

            Client.OnStoryImageReceived += (img) =>
            {
                try
                {
                    if (!IsClientMode) return;
                    string uuid = System.IO.Path.GetFileNameWithoutExtension(img?.FileName ?? "");
                    if (string.IsNullOrEmpty(uuid)) return;
                    if (!TryApplyClientImage(uuid))
                        Instance?.StartCoroutine(RetryApplyClientImage(uuid));
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnStoryImageReceived error: {ex.Message}");
                }
            };

            Client.OnLocationUpdated += (loc) => MPMapOverlay.Instance?.UpdateLocation(loc);

            Client.OnInventoryReceived += (inv) =>
            {
                try
                {
                    MPInventoryManager.LoadFromJson(inv?.InventoryJson);
                    MPInventoryManager.Save();
                    MPInventoryUI.Instance?.Refresh();
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnInventoryReceived error: {ex.Message}");
                }
            };

            Client.OnQuestSyncReceived += (qs) =>
            {
                MPQuestUI.Instance?.UpdateQuests(qs?.Quests);
            };

            Client.OnPrivateResultReceived += (pr) =>
            {
                CoopStatusOverlay.Instance?.ShowPrivateResult(pr?.ResultText ?? "");
            };

            Client.OnCombatBegin += (cb) =>
            {
                CombatManager.BeginCombat(cb.TurnOrder, cb.EnemyNames, cb.TurnOrder?.Length ?? 0);
                CoopStatusOverlay.Instance?.AddChat("Combat", $"<color=#FF6666>⚔ Combat! Enemies: {string.Join(", ", cb.EnemyNames ?? new string[0])}</color>", isSystem: true);
            };
            Client.OnCombatTurnNotify += (ct) =>
            {
                CoopStatusOverlay.Instance?.SetStatus($"⚔ Round {ct.RoundNumber} — Submit your action!");
            };
            Client.OnCombatResult += (cr) =>
            {
                CoopStatusOverlay.Instance?.AddChat("Combat", $"<color=#FFAA66>{cr.NarrativeText}</color>", isSystem: true);
            };
            Client.OnCombatEnd += () =>
            {
                CombatManager.EndCombat();
                CoopStatusOverlay.Instance?.AddChat("Combat", "<color=#66CC66>⚔ Combat has ended.</color>", isSystem: true);
            };

            Client.OnStoryTurnReceived += (entry) =>
            {
                try
                {
                    var logView = SS.I?.hackyManager?.gameLogView;
                    if (logView != null)
                        logView.QueueLogText(entry.Text);
                    else
                        CoopStatusOverlay.Instance?.AddChat(entry.AuthorName ?? "Narrator", entry.Text);
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnStoryTurnReceived error: {ex.Message}");
                }
            };

            Client.ConnectReconnect(host, port, previousPlayerId, character);
        }

        // --- Server event handlers (main thread) ---

        private static void OnClientConnected(ConnectedClient client, HelloPayload hello)
        {
            var charInfo = hello.Character;
            string charName = charInfo?.CharacterName ?? "Unknown";

            Instance.Log.LogInfo($"[Host] {charName} connected (ID: {client.PlayerId}).");

            var manager = FindObjectOfType<GameplayManager>();
            string location = manager?.currentPlace?.name ?? "Unknown";
            string hostCharName = manager?.playerCharacter?.pcGameEntity?.name ?? "Host";

            StoryEntry[] recentTurns = GetRecentTurns(manager, 20);

            Server.SendTo(client, Packet.Create(PacketType.Welcome, new WelcomePayload
            {
                AssignedPlayerId = client.PlayerId,
                HostCharacterName = hostCharName,
                CurrentLocation = location,
                RecentTurns = recentTurns
            }));

            // Send compressed save snapshot for the client to load
            SendSaveSnapshot(client, manager);

            // Ensure this client has an inventory entry, then send the current database
            MPInventoryManager.GetOrCreate(client.PlayerId, charName);
            Server.SendInventoryTo(client, MPInventoryManager.SerializeToJson());
            Server.SendQuestSyncTo(client, ExtractQuestState(manager));

            string joinVerb = hello.IsSpectator ? "is spectating" : "has joined";
            Server.BroadcastChat("Server", $"{charName} {joinVerb} the session!", isSystem: true);
            SendPartyUpdate(manager);
            string toastIcon = hello.IsSpectator ? "👁" : "⚔";
            manager?.toast?.ShowToast($"{toastIcon} {charName} {joinVerb} the co-op session!");

            Instance.Log.LogInfo($"[Host] Sent Welcome + save snapshot to {charName}.");
        }

        private static void OnClientDisconnected(ConnectedClient client)
        {
            string charName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance.Log.LogInfo($"[Host] {charName} disconnected.");

            Server.BroadcastChat("Server", $"{charName} has left the session.", isSystem: true);

            // If this client was the last one we were waiting on, release the gate
            if (_waitingForParty)
            {
                var remaining = Server?.GetClients().Where(c => !c.IsTurnReady).ToList();
                if (remaining == null || remaining.Count == 0)
                    ReleasePartyGate();
            }

            var manager = FindObjectOfType<GameplayManager>();
            manager?.toast?.ShowToast($"⚔ {charName} left the co-op session.");
            SendPartyUpdate(manager);
        }

        private static void OnClientActionReceived(ConnectedClient client, ActionRequestPayload action)
        {
            if (client.IsSpectator) return; // Spectators cannot submit actions

            string charName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance.Log.LogInfo($"[Host] Action from {charName}: {action.ActionText}");

            // Store action on the client object (used by BuildPromptString postfix)
            client.SetPendingAction($"{charName}: {action.ActionText}");
            GameplayMultiplayerPatch.AddPendingAction(client.PlayerId, charName, action.ActionText);

            Server.SendTo(client, Packet.Create(PacketType.ActionQueued));

            Server.BroadcastStoryTurn(new StoryEntry
            {
                Text = $"{charName}: {action.ActionText}",
                AuthorName = charName,
                IsPlayerAction = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            var manager = FindObjectOfType<GameplayManager>();
            manager?.toast?.ShowToast($"⚔ {charName}: {TruncateForToast(action.ActionText)}");

            // Log to the host's story view so they can read and respond before taking their turn
            manager?.gameLogView?.LogText($"{charName}: {action.ActionText}");
        }

        private static void OnChatReceived_Host(ConnectedClient client, ChatPayload chat)
        {
            var manager = FindObjectOfType<GameplayManager>();
            manager?.gameLogView?.LogText($"[OOC] {chat.SenderName}: {chat.Message}");
            // Relay OOC chat to the host's overlay as well
            CoopStatusOverlay.Instance?.AddChat(chat.SenderName, chat.Message);
        }

        private static void OnClientCharacterUpdated(ConnectedClient client, RemoteCharacterInfo info)
        {
            // CharacterInfo was already updated by the server before this fires
            Instance?.Log.LogInfo($"[Host] CharacterUpdate from {client.PlayerName}: HP={info.Health}/{info.MaxHealth}");
            var manager = FindObjectOfType<GameplayManager>();
            SendPartyUpdate(manager);
        }

        private static void OnClientReconnectedHandler(ConnectedClient client, ReconnectPayload reconnPayload)
        {
            string charName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance.Log.LogInfo($"[Host] {charName} reconnected (ID: {client.PlayerId}).");

            // Send current save snapshot
            var manager = FindObjectOfType<GameplayManager>();
            SendSaveSnapshot(client, manager);

            // Send inventory and quest state
            Server.SendInventoryTo(client, MPInventoryManager.SerializeToJson());
            Server.SendQuestSyncTo(client, ExtractQuestState(manager));

            Server.BroadcastChat("Server", $"{charName} has reconnected!", isSystem: true);
            SendPartyUpdate(manager);
            manager?.toast?.ShowToast($"⚔ {charName} reconnected!");
        }

        private static void OnItemTransferReceived_Host(ConnectedClient client, ItemTransferPayload transfer)
        {
            string fromId = transfer.FromPlayerId; // Set to client.PlayerId server-side
            string toId = transfer.ToPlayerId;
            string itemName = transfer.ItemName;

            Instance?.Log.LogInfo($"[Host] ItemTransfer: '{itemName}' from {fromId} → {toId}");

            bool ok = MPInventoryManager.TransferItem(fromId, toId, itemName);
            if (ok)
            {
                MPInventoryManager.Save();
                BroadcastInventory();
                string fromName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
                var toClient = Server?.GetClients().Find(c => c.PlayerId == toId);
                string toName = toClient?.CharacterInfo?.CharacterName ?? toId;
                Server?.BroadcastChat("Server", $"{fromName} gifted '{itemName}' to {toName}!", isSystem: true);
            }
            else
            {
                Instance?.Log.LogWarning($"[Host] ItemTransfer failed: '{itemName}' not found in {fromId}'s inventory.");
            }
        }

        /// <summary>
        /// Handles a private/whisper action from a client.
        /// Makes a separate AI call with the private action and sends the result only to the originating player.
        /// Other players see a generic "takes a secretive action" message.
        /// </summary>
        private static async void OnPrivateActionReceived_Host(ConnectedClient client, PrivateActionPayload action)
        {
            string charName = action.CharacterName ?? client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance?.Log.LogInfo($"[Host] Private action from {charName}: {action.ActionText}");

            // Broadcast a generic message to other players
            Server?.BroadcastChat("Narrator", $"{charName} takes a secretive action...", isSystem: true);

            // Make a separate AI call for the private action
            try
            {
                var manager = FindObjectOfType<GameplayManager>();
                if (manager == null)
                {
                    Server?.SendPrivateResult(client, "[Private action failed — no active game session.]");
                    return;
                }

                string prompt = $"[PRIVATE ACTION]\nThe player character '{charName}' secretly attempts: {action.ActionText}\n\nDescribe the outcome of this secret action in 2-3 sentences. Only {charName} can see this result. Keep it concise.";
                string result = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER, prompt,
                    AIAsker.ChatGptPostprocessingType.NONE, forceConcise: true);

                if (string.IsNullOrEmpty(result))
                    result = "The secretive action yields no clear outcome.";

                Server?.SendPrivateResult(client, result);
                Instance?.Log.LogInfo($"[Host] Private result sent to {charName}: {result.Substring(0, Math.Min(100, result.Length))}...");
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] Private action AI error: {ex.Message}");
                Server?.SendPrivateResult(client, $"[Private action failed: {ex.Message}]");
            }
        }

        /// <summary>
        /// Handles a combat action from a client. Collects it and resolves when all players have acted.
        /// </summary>
        private static void OnCombatActionReceived_Host(ConnectedClient client, CombatActionPayload action)
        {
            string charName = action.CharacterName ?? client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance?.Log.LogInfo($"[Host] Combat action from {charName}: {action.ActionText}");

            if (!CombatManager.IsCombatActive)
            {
                Instance?.Log.LogWarning("[Host] Received combat action but no combat is active.");
                return;
            }

            bool allReady = CombatManager.SubmitAction(client.PlayerId, charName, action.ActionText);

            // Broadcast waiting status
            Server?.BroadcastAll(Packet.Create(PacketType.WaitingForParty, new WaitingForPartyPayload
            {
                ReadyCount = CombatManager.GetSubmittedCount(),
                TotalCount = CombatManager.GetExpectedCount()
            }));

            if (allReady)
            {
                ResolveCombatRound();
            }
        }

        /// <summary>
        /// Starts a combat encounter. Called when the host detects enemies in the current location.
        /// </summary>
        public static void StartCombat(string[] enemyNames)
        {
            if (!IsHost || CombatManager.IsCombatActive) return;

            var manager = FindObjectOfType<GameplayManager>();
            var clients = Server?.GetClients();
            if (clients == null) return;

            // Build turn order: host first, then clients (excluding spectators)
            var turnOrder = new List<string>();
            string hostName = manager?.playerCharacter?.pcGameEntity?.name ?? "Host";
            turnOrder.Add(hostName);
            foreach (var c in clients.Where(c => !c.IsSpectator))
                turnOrder.Add(c.CharacterInfo?.CharacterName ?? c.PlayerName);

            int playerCount = turnOrder.Count;
            CombatManager.BeginCombat(turnOrder.ToArray(), enemyNames, playerCount);

            // Broadcast CombatBegin to all clients
            Server?.BroadcastAll(Packet.Create(PacketType.CombatBegin, new CombatBeginPayload
            {
                TurnOrder = turnOrder.ToArray(),
                EnemyNames = enemyNames,
                RoundNumber = 1
            }));

            Server?.BroadcastChat("Server", $"⚔ Combat started! Enemies: {string.Join(", ", enemyNames)}", isSystem: true);
            Instance?.Log.LogInfo($"[Host] Combat started: {string.Join(", ", enemyNames)} — {playerCount} players");
        }

        /// <summary>
        /// Resolves the current combat round by building a combined prompt and calling the AI.
        /// </summary>
        private static async void ResolveCombatRound()
        {
            try
            {
                string combatPrompt = CombatManager.BuildCombatPrompt();
                Instance?.Log.LogInfo($"[Host] Resolving combat round {CombatManager.RoundNumber}...");

                string result = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    combatPrompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(result))
                    result = "The combat round concludes with no clear outcome.";

                // Broadcast the result to all players
                Server?.BroadcastAll(Packet.Create(PacketType.CombatResult, new CombatResultPayload
                {
                    NarrativeText = result,
                    RoundNumber = CombatManager.RoundNumber
                }));

                // Also add to story feed
                Server?.BroadcastStoryTurn(new StoryEntry
                {
                    Text = result,
                    AuthorName = "Combat",
                    IsPlayerAction = false,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                Instance?.Log.LogInfo($"[Host] Combat round {CombatManager.RoundNumber} resolved.");

                // Advance to next round (or end combat if host chooses)
                CombatManager.NextRound();

                Server?.BroadcastAll(Packet.Create(PacketType.CombatTurnNotify, new CombatTurnNotifyPayload
                {
                    ActiveCharacterName = "",
                    RoundNumber = CombatManager.RoundNumber
                }));
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] Combat resolution error: {ex.Message}");
                Server?.BroadcastChat("Server", "Combat resolution failed — ending combat.", isSystem: true);
                EndCombat();
            }
        }

        /// <summary>
        /// Ends the current combat encounter and notifies all players.
        /// </summary>
        public static void EndCombat()
        {
            if (!CombatManager.IsCombatActive) return;
            CombatManager.EndCombat();
            Server?.BroadcastAll(Packet.Create(PacketType.CombatEnd));
            Server?.BroadcastChat("Server", "⚔ Combat has ended.", isSystem: true);
            Instance?.Log.LogInfo("[Host] Combat ended.");
        }

        // --- Helpers ---

        public static RemoteCharacterInfo GetLocalCharacterInfo(GameplayManager manager)
        {
            var pc = manager?.playerCharacter?.pcGameEntity;
            return new RemoteCharacterInfo
            {
                PlayerName = "Host",
                CharacterName = pc?.name ?? "Host",
                CharacterClass = pc?.playerClass?.ToSingleLineStr() ?? "",
                CharacterBackground = pc?.GetPotentiallyNullDescription() ?? "",
                Health = pc?.health ?? 100,
                MaxHealth = pc?.maxHealth ?? 100,
                Level = 0,
                CurrentLocation = manager?.currentPlace?.name ?? ""
            };
        }

        /// <summary>
        /// Serializes the full MPInventoryDatabase and broadcasts it to all connected clients.
        /// Safe to call from the main thread (e.g. from WriteSaveFilePatch.Postfix).
        /// </summary>
        public static void BroadcastInventory()
        {
            if (!IsHost || Server == null) return;
            try
            {
                string json = MPInventoryManager.SerializeToJson();
                Server.BroadcastInventory(json);
                Instance?.Log.LogInfo($"[Host] BroadcastInventory: {json.Length} chars.");
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] BroadcastInventory error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts quest info from the game's QuestLogV4 into lightweight MPQuestInfo[] for network sync.
        /// </summary>
        public static MPQuestInfo[] ExtractQuestState(GameplayManager manager)
        {
            var quests = new List<MPQuestInfo>();
            try
            {
                var questLog = manager?.playerCharacter?.pcGameEntity?.questLogV4;
                if (questLog == null) return quests.ToArray();

                // Main quest chain
                if (questLog.mainQuestChain?.questEles != null)
                {
                    foreach (var ele in questLog.mainQuestChain.questEles)
                    {
                        quests.Add(new MPQuestInfo
                        {
                            Id = ele.uuid ?? "",
                            Title = ele.questTitle ?? "Main Quest",
                            Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                            Status = "Active",
                            QuestType = "Main"
                        });
                    }
                }

                // Active side quests
                if (questLog.sideQuestChains != null)
                {
                    foreach (var chain in questLog.sideQuestChains)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Side Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Active",
                                QuestType = "Side"
                            });
                        }
                    }
                }

                // Completed quests
                if (questLog.completedQuests != null)
                {
                    foreach (var chain in questLog.completedQuests)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Completed",
                                QuestType = "Side"
                            });
                        }
                    }
                }

                // Failed quests
                if (questLog.failedQuests != null)
                {
                    foreach (var chain in questLog.failedQuests)
                    {
                        if (chain?.questEles == null) continue;
                        foreach (var ele in chain.questEles)
                        {
                            quests.Add(new MPQuestInfo
                            {
                                Id = ele.uuid ?? "",
                                Title = ele.questTitle ?? "Quest",
                                Objective = (ele as QuestEleV4)?.objectiveStr ?? "",
                                Status = "Failed",
                                QuestType = "Side"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] ExtractQuestState error: {ex.Message}");
            }
            return quests.ToArray();
        }

        /// <summary>
        /// Broadcasts current quest state to all connected clients.
        /// </summary>
        public static void BroadcastQuestSync()
        {
            if (!IsHost || Server == null) return;
            try
            {
                var manager = FindObjectOfType<GameplayManager>();
                var quests = ExtractQuestState(manager);
                Server.BroadcastQuestSync(quests);
                Instance?.Log.LogInfo($"[Host] BroadcastQuestSync: {quests.Length} quest(s).");
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Host] BroadcastQuestSync error: {ex.Message}");
            }
        }

        public static void BroadcastLocation(string locationName, string locationDescription = "")
        {
            if (!IsHost) return;
            Server?.BroadcastAll(Packet.Create(PacketType.LocationUpdate, new LocationUpdatePayload
            {
                LocationName = locationName,
                LocationDescription = locationDescription
            }));
            Instance?.Log.LogInfo($"[Host] Broadcast location update: {locationName}");
        }

        /// <summary>
        /// Broadcasts a full LocationUpdatePayload with extended map data.
        /// </summary>
        public static void BroadcastLocationPayload(LocationUpdatePayload payload)
        {
            if (!IsHost) return;
            Server?.BroadcastAll(Packet.Create(PacketType.LocationUpdate, payload));
            Instance?.Log.LogInfo($"[Host] Broadcast location: {payload.LocationName} (NPCs: {payload.NPCNames?.Length ?? 0}, Enemies: {payload.EnemyNames?.Length ?? 0})");
        }

        private static void SendSaveSnapshot(ConnectedClient client, GameplayManager manager)
        {
            try
            {
                if (manager == null) return;
                string hostSaveSubDir = SS.I.saveSubDirAsArg;
                string savePath = Path.Combine(SS.I.saveTopLvlDir, hostSaveSubDir, "my_save.txt");
                if (!File.Exists(savePath))
                {
                    Instance.Log.LogWarning("[Host] Save file not found, skipping snapshot.");
                    return;
                }

                string saveDir = Path.Combine(SS.I.saveTopLvlDir, hostSaveSubDir);
                string saveJson = File.ReadAllText(savePath);
                string placeName = manager.currentPlace?.name ?? "";
                string placeDesc = manager.currentPlace?.description ?? "";
                var polygonFiles = ReadPolygonFiles(saveDir);

                Server.SendSaveFileTo(client, saveJson, placeName, placeDesc, hostSaveSubDir, polygonFiles);
            }
            catch (Exception ex)
            {
                Instance.Log.LogError($"[Host] Failed to send save snapshot: {ex.Message}");
            }
        }

        private static StoryEntry[] GetRecentTurns(GameplayManager manager, int count)
        {
            try
            {
                var storyChain = manager?.playerCharacter?.pcGameEntity?.storyChain;
                if (storyChain == null) return new StoryEntry[0];

                var turns = storyChain.storyTurns;
                int skip = Math.Max(0, turns.Count - count);
                var entries = new List<StoryEntry>();
                for (int i = skip; i < turns.Count; i++)
                {
                    entries.Add(new StoryEntry
                    {
                        Text = turns[i].getCombinedStr()?.Trim() ?? "",
                        AuthorName = "Narrator",
                        IsPlayerAction = false,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                return entries.ToArray();
            }
            catch (Exception ex)
            {
                Instance.Log.LogError($"[Host] GetRecentTurns error: {ex.Message}");
                return new StoryEntry[0];
            }
        }

        private static void SendPartyUpdate(GameplayManager manager)
        {
            if (Server == null) return;

            var members = new List<RemoteCharacterInfo>();
            if (manager != null) members.Add(GetLocalCharacterInfo(manager));

            foreach (var c in Server.GetClients())
                if (c.CharacterInfo != null) members.Add(c.CharacterInfo);

            var payload = new PartyUpdatePayload { Members = members.ToArray() };
            Server.BroadcastPartyUpdate(payload);

            // Also update the host's own overlay (it doesn't receive broadcast packets)
            CoopStatusOverlay.Instance?.UpdateParty(payload.Members);
        }

        private static string TruncateForToast(string s, int maxLen = 60)
        {
            if (s == null) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }

        // -----------------------------------------------------------------------
        // Client-side image application helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Looks up the IllustratedStoryTurn by UUID, marks it finished, and calls
        /// UpdateMainImageWithXfade. Returns true if the entity was found and applied.
        /// Safe to call from main thread only.
        /// </summary>
        private static bool TryApplyClientImage(string uuid)
        {
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager == null) return false;

                // Primary: entity map lookup (populated after save reload)
                GameEntity entity = null;
                SS.I.uuidToGameEntityMap?.TryGetValue(uuid, out entity);
                var illu = entity as IllustratedStoryTurn;

                // Secondary: scan lastIlluStoryTurns (covers the entity-map deserialization lag)
                if (illu == null)
                {
                    var pc = manager.playerCharacter?.pcGameEntity;
                    illu = pc?.lastIlluStoryTurns?.FindLast(il => il != null && il.uuid == uuid);
                }

                if (illu == null) return false;

                illu.imgGenInfo.imgGenState = GameEntity.ImgGenState.FINISHED;
                illu.imgGenInfo.imageDirtyBit = true;
                _ = manager.mainImg.UpdateMainImageWithXfade(illu);
                Instance?.Log.LogInfo($"[Client] Applied story image: {uuid}");
                UnityEngine.Debug.Log($"[MP-DIAG] TryApplyClientImage success: {uuid}");
                return true;
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Client] TryApplyClientImage error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Retries TryApplyClientImage every second for up to 10 seconds.
        /// Used when the StoryImage packet arrives before the save reload completes
        /// (entity map not yet populated).
        /// </summary>
        private static System.Collections.IEnumerator RetryApplyClientImage(string uuid)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                yield return new UnityEngine.WaitForSeconds(1f);

                if (!IsClientMode) yield break; // Disconnected

                if (TryApplyClientImage(uuid))
                    yield break;

                UnityEngine.Debug.Log($"[MP-DIAG] RetryApplyClientImage attempt {attempt + 1}/10 for {uuid}");
            }

            // Final fallback: display whatever the last finished turn is
            Instance?.Log.LogWarning($"[Client] UUID {uuid} not found after 10 retries — using last finished turn.");
            try
            {
                var manager = SS.I?.hackyManager;
                var lastFinished = manager?.playerCharacter?.pcGameEntity?.GetLastFinishedIlluStoryTurn();
                if (lastFinished != null)
                {
                    lastFinished.imgGenInfo.imageDirtyBit = true;
                    _ = manager.mainImg.UpdateMainImageWithXfade(lastFinished);
                }
            }
            catch (Exception ex)
            {
                Instance?.Log.LogError($"[Client] RetryApplyClientImage fallback error: {ex.Message}");
            }
        }
    }
}
