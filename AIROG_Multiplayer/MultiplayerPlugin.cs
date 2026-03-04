using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
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

            var clients = Server.GetClients();
            if (clients.Count == 0) return false; // No clients, no wait

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

            var clients = Server?.GetClients();
            if (clients == null) return;

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

            try
            {
                Server = new AIROGServer();

                Server.OnClientConnected += (client, hello) => OnClientConnected(client, hello);
                Server.OnClientDisconnected += (client) => OnClientDisconnected(client);
                Server.OnActionReceived += (client, action) => OnClientActionReceived(client, action);
                Server.OnChatReceived += (client, chat) => OnChatReceived_Host(client, chat);
                Server.OnTurnReady += (client) => OnClientTurnReady(client);

                Server.Start(port);
                onSuccess?.Invoke();

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

            Client = new AIROGClient();

            Client.OnConnected += (welcome) =>
            {
                Instance.Log.LogInfo($"[Client] Connected to {host}:{port}. Host location: {welcome.CurrentLocation}");
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
                    var manager = SS.I?.hackyManager;
                    if (manager == null) return;

                    // Extract UUID from filename (e.g. "abc123.png" → "abc123")
                    string uuid = System.IO.Path.GetFileNameWithoutExtension(img?.FileName ?? "");
                    if (string.IsNullOrEmpty(uuid)) return;

                    // Look up the IllustratedStoryTurn by UUID in the global entity map
                    GameEntity entity = null;
                    SS.I.uuidToGameEntityMap?.TryGetValue(uuid, out entity);
                    var illu = entity as IllustratedStoryTurn;

                    if (illu == null)
                    {
                        // Fallback: use whatever the last FINISHED illustrated turn is.
                        // This covers the case where the entity deserialized as IN_PROGRESS.
                        var pc = manager.playerCharacter?.pcGameEntity;
                        illu = pc?.lastIlluStoryTurns?.FindLast(
                            il => il != null && il.uuid == uuid);
                    }

                    if (illu != null)
                    {
                        illu.imgGenInfo.imgGenState = GameEntity.ImgGenState.FINISHED;
                        illu.imgGenInfo.imageDirtyBit = true;
                        _ = manager.mainImg.UpdateMainImageWithXfade(illu);
                        Instance?.Log.LogInfo($"[Client] Refreshed story image: {uuid}");
                    }
                    else
                    {
                        // Last resort: call UpdateMainImage on whatever turn is currently FINISHED.
                        // The save may not yet have the new IllustratedStoryTurn deserialized.
                        var lastFinished = manager.playerCharacter?.pcGameEntity?.GetLastFinishedIlluStoryTurn();
                        if (lastFinished != null)
                        {
                            lastFinished.imgGenInfo.imageDirtyBit = true;
                            _ = manager.mainImg.UpdateMainImageWithXfade(lastFinished);
                        }
                        Instance?.Log.LogWarning($"[Client] OnStoryImageReceived: UUID {uuid} not in entity map — used fallback.");
                    }
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogError($"[Client] OnStoryImageReceived display error: {ex.Message}");
                }
            };

            // Location updates are visible in the game UI — no overlay action needed
            Client.OnLocationUpdated += (_) => { };

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

            Server.BroadcastChat("Server", $"{charName} has joined the session!", isSystem: true);
            SendPartyUpdate(manager);
            manager?.toast?.ShowToast($"⚔ {charName} joined the co-op session!");

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
            string charName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
            Instance.Log.LogInfo($"[Host] Action from {charName}: {action.ActionText}");

            // Store action on the client object (used by BuildPromptString postfix)
            client.SetPendingAction($"{charName}: {action.ActionText}");
            GameplayMultiplayerPatch.AddPendingAction(client.PlayerId, charName, action.ActionText);

            Server.SendTo(client, Packet.Create(PacketType.ActionQueued));

            Server.BroadcastStoryTurn(new StoryEntry
            {
                Text = action.ActionText,
                AuthorName = charName,
                IsPlayerAction = true,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            var manager = FindObjectOfType<GameplayManager>();
            manager?.toast?.ShowToast($"⚔ {charName}: {TruncateForToast(action.ActionText)}");
        }

        private static void OnChatReceived_Host(ConnectedClient client, ChatPayload chat)
        {
            var manager = FindObjectOfType<GameplayManager>();
            manager?.gameLogView?.LogText($"[OOC] {chat.SenderName}: {chat.Message}");
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

            Server.BroadcastPartyUpdate(new PartyUpdatePayload { Members = members.ToArray() });
        }

        private static string TruncateForToast(string s, int maxLen = 60)
        {
            if (s == null) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
