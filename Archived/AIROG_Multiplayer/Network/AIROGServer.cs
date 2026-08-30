using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BepInEx.Logging;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// The TCP server side — runs on the host's machine.
    /// Manages incoming connections and relays events to all clients.
    /// All networking runs on background threads; game-thread callbacks are queued.
    /// </summary>
    public class AIROGServer
    {
        private static ManualLogSource Log => MultiplayerPlugin.Instance.Log;

        public bool IsRunning { get; private set; }
        public int Port { get; private set; }

        // Thread-safe list of connected clients
        private readonly List<ConnectedClient> _clients = new List<ConnectedClient>();
        private readonly object _clientLock = new object();

        // Disconnected players awaiting reconnection
        private readonly Dictionary<string, DisconnectedPlayer> _disconnectedPlayers
            = new Dictionary<string, DisconnectedPlayer>();
        private readonly object _disconnectedLock = new object();

        /// <summary>How long (minutes) a disconnected player slot is kept for reconnection.</summary>
        public float ReconnectTimeoutMinutes { get; set; } = 10f;

        private TcpListener _listener;
        private Thread _acceptThread;

        // Queued callbacks to invoke on Unity's main thread
        public readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        // Events (fired on main thread via MainThreadQueue)
        public event Action<ConnectedClient, HelloPayload> OnClientConnected;
        public event Action<ConnectedClient> OnClientDisconnected;
        public event Action<ConnectedClient, ActionRequestPayload> OnActionReceived;
        public event Action<ConnectedClient, ChatPayload> OnChatReceived;

        // v2.0: turn gate — fires on main thread when a client signals TurnReady
        public event Action<ConnectedClient> OnTurnReady;

        // Character stats update from a client (fires on main thread)
        public event Action<ConnectedClient, RemoteCharacterInfo> OnCharacterUpdateReceived;

        // Item transfer request from a client (fires on main thread)
        public event Action<ConnectedClient, ItemTransferPayload> OnItemTransferReceived;

        // Reconnection: fires when a client successfully reconnects (main thread)
        public event Action<ConnectedClient, ReconnectPayload> OnClientReconnected;

        // Private actions
        public event Action<ConnectedClient, PrivateActionPayload> OnPrivateActionReceived;

        // Combat
        public event Action<ConnectedClient, CombatActionPayload> OnCombatActionReceived;

        public void Start(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsRunning = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "AIROG-Server-Accept" };
            _acceptThread.Start();

            Log.LogInfo($"[Server] Listening on port {port}");
        }

        public void Stop()
        {
            IsRunning = false;
            _listener?.Stop();

            lock (_clientLock)
            {
                foreach (var c in _clients)
                    c.Disconnect();
                _clients.Clear();
            }

            Log.LogInfo("[Server] Stopped.");
        }

        // --- Broadcast helpers ---

        public void BroadcastStoryTurn(StoryEntry entry)
        {
            Broadcast(Packet.Create(PacketType.StoryTurn, entry));

            // Buffer for disconnected players awaiting reconnection
            lock (_disconnectedLock)
            {
                foreach (var dc in _disconnectedPlayers.Values)
                {
                    lock (dc.MissedStoryTurns)
                    {
                        dc.MissedStoryTurns.Add(entry);
                        // Cap at 100 to avoid unbounded memory growth
                        if (dc.MissedStoryTurns.Count > 100)
                            dc.MissedStoryTurns.RemoveAt(0);
                    }
                }
            }
        }

        public void BroadcastPartyUpdate(PartyUpdatePayload party)
        {
            Broadcast(Packet.Create(PacketType.PartyUpdate, party));
        }

        public void BroadcastChat(string senderName, string message, bool isSystem = false)
        {
            Broadcast(Packet.Create(PacketType.Chat, new ChatPayload
            {
                SenderName = senderName,
                Message = message,
                IsSystem = isSystem
            }));
        }

        /// <summary>v2.0: Tell all clients a new turn has begun — time to submit actions.</summary>
        public void BroadcastTurnBegin()
        {
            Broadcast(Packet.Create(PacketType.TurnBegin));
        }

        /// <summary>v2.0: Tell all clients how many party members have checked in.</summary>
        public void BroadcastWaitingForParty(int readyCount, int totalCount)
        {
            Broadcast(Packet.Create(PacketType.WaitingForParty, new WaitingForPartyPayload
            {
                ReadyCount = readyCount,
                TotalCount = totalCount
            }));
        }

        /// <summary>v2.0: Broadcast an AI-generated story image (PNG bytes as Base64) to all clients.</summary>
        public void BroadcastStoryImage(string pngBase64, string storyTurnText = "", string fileName = "")
        {
            Broadcast(Packet.Create(PacketType.StoryImage, new StoryImagePayload
            {
                PngBase64 = pngBase64,
                StoryTurnText = storyTurnText,
                FileName = fileName
            }));
        }

        /// <summary>
        /// Compresses the full save JSON with GZip and broadcasts it to all clients.
        /// Clients will write it to their local save directory and reload the game.
        /// hostSaveSubDir is the host's SS.I.saveSubDirAsArg — clients use it to rewrite image paths.
        /// </summary>
        public void BroadcastSaveFile(string saveJson, string placeName = "", string placeDesc = "", string hostSaveSubDir = "", Dictionary<string, string> polygonFiles = null)
        {
            try
            {
                string gzipB64 = PacketUtils.GzipCompress(saveJson);
                Broadcast(Packet.Create(PacketType.SaveData, new SaveDataPayload
                {
                    SaveFileGzipB64 = gzipB64,
                    SaveJson = null, // compressed replaces legacy field
                    SaveName = "my_save.txt",
                    HostSaveSubDir = hostSaveSubDir,
                    PolygonFiles = polygonFiles,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CurrentPlaceName = placeName,
                    CurrentPlaceDescription = placeDesc,
                    VisibleNPCNames = new string[0],
                    VisibleItemNames = new string[0]
                }));
                Log.LogInfo($"[Server] Broadcast save ({saveJson.Length} chars → {gzipB64.Length} B64 chars) at {placeName}, {polygonFiles?.Count ?? 0} polygon file(s)");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Server] BroadcastSaveFile error: {ex.Message}");
            }
        }

        /// <summary>Sends a compressed save snapshot to a single client (used on join).</summary>
        public void SendSaveFileTo(ConnectedClient client, string saveJson, string placeName = "", string placeDesc = "", string hostSaveSubDir = "", Dictionary<string, string> polygonFiles = null)
        {
            try
            {
                string gzipB64 = PacketUtils.GzipCompress(saveJson);
                SendTo(client, Packet.Create(PacketType.SaveData, new SaveDataPayload
                {
                    SaveFileGzipB64 = gzipB64,
                    SaveJson = null,
                    SaveName = "my_save.txt",
                    HostSaveSubDir = hostSaveSubDir,
                    PolygonFiles = polygonFiles,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CurrentPlaceName = placeName,
                    CurrentPlaceDescription = placeDesc,
                    VisibleNPCNames = new string[0],
                    VisibleItemNames = new string[0]
                }));
                Log.LogInfo($"[Server] Sent save to {client.PlayerName} ({saveJson.Length} chars → {gzipB64.Length} B64 chars), {polygonFiles?.Count ?? 0} polygon file(s)");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Server] SendSaveFileTo error: {ex.Message}");
            }
        }

        /// <summary>Broadcasts the full inventory database to all connected clients.</summary>
        public void BroadcastInventory(string inventoryJson)
        {
            Broadcast(Packet.Create(PacketType.InventorySync, new InventorySyncPayload
            {
                InventoryJson = inventoryJson
            }));
            Log.LogInfo($"[Server] Broadcast inventory ({inventoryJson?.Length ?? 0} chars).");
        }

        /// <summary>Sends the inventory database to a single client (used on join).</summary>
        public void SendInventoryTo(ConnectedClient client, string inventoryJson)
        {
            SendTo(client, Packet.Create(PacketType.InventorySync, new InventorySyncPayload
            {
                InventoryJson = inventoryJson
            }));
            Log.LogInfo($"[Server] Sent inventory to {client.PlayerName} ({inventoryJson?.Length ?? 0} chars).");
        }

        /// <summary>Broadcasts quest state to all connected clients.</summary>
        public void BroadcastQuestSync(MPQuestInfo[] quests)
        {
            Broadcast(Packet.Create(PacketType.QuestSync, new QuestSyncPayload
            {
                Quests = quests,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }));
        }

        /// <summary>Sends quest state to a single client (used on join/reconnect).</summary>
        public void SendQuestSyncTo(ConnectedClient client, MPQuestInfo[] quests)
        {
            SendTo(client, Packet.Create(PacketType.QuestSync, new QuestSyncPayload
            {
                Quests = quests,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }));
        }

        /// <summary>Sends a private action result to a specific client.</summary>
        public void SendPrivateResult(ConnectedClient client, string resultText)
        {
            SendTo(client, Packet.Create(PacketType.PrivateResult, new PrivateResultPayload
            {
                ResultText = resultText
            }));
        }

        /// <summary>Broadcasts any packet to all connected clients.</summary>
        public void BroadcastAll(Packet packet) => Broadcast(packet);

        public void SendTo(ConnectedClient client, Packet packet)
        {
            try { client.Send(packet); }
            catch (Exception ex) { Log.LogError($"[Server] SendTo failed: {ex.Message}"); }
        }

        public List<ConnectedClient> GetClients()
        {
            lock (_clientLock) { return new List<ConnectedClient>(_clients); }
        }

        private void Broadcast(Packet packet)
        {
            lock (_clientLock)
            {
                foreach (var c in _clients)
                {
                    try { c.Send(packet); }
                    catch (Exception ex) { Log.LogWarning($"[Server] Broadcast failed for {c.PlayerId}: {ex.Message}"); }
                }
            }
        }

        // --- Accept thread ---

        private void AcceptLoop()
        {
            while (IsRunning)
            {
                try
                {
                    TcpClient tcp = _listener.AcceptTcpClient();
                    tcp.NoDelay = true;

                    var client = new ConnectedClient(tcp, this);
                    Thread t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = $"AIROG-Client-{client.PlayerId}" };
                    t.Start();
                }
                catch (SocketException) when (!IsRunning)
                {
                    break; // Expected on Stop()
                }
                catch (Exception ex)
                {
                    Log.LogError($"[Server] Accept error: {ex.Message}");
                }
            }
        }

        // --- Per-client read thread ---

        private void ClientLoop(ConnectedClient client)
        {
            Log.LogInfo($"[Server] New connection from {client.RemoteEndPoint}");


            try
            {
                NetworkStream stream = client.GetStream();

                // First packet MUST be Hello or Reconnect
                Packet firstPkt = Packet.ReadFrom(stream);
                if (firstPkt == null)
                {
                    Log.LogWarning("[Server] Client sent no initial packet — rejecting.");
                    client.Send(Packet.Create(PacketType.Rejected));
                    client.Disconnect();
                    return;
                }

                if (firstPkt.Type == PacketType.Reconnect)
                {
                    var reconnPayload = firstPkt.GetPayload<ReconnectPayload>();
                    string prevId = reconnPayload.PreviousPlayerId;

                    DisconnectedPlayer dc = null;
                    lock (_disconnectedLock)
                    {
                        if (prevId != null && _disconnectedPlayers.TryGetValue(prevId, out dc))
                            _disconnectedPlayers.Remove(prevId);
                    }

                    if (dc == null)
                    {
                        // No slot found — tell client to do a fresh join
                        client.Send(Packet.Create(PacketType.ReconnectResult, new ReconnectResultPayload
                        {
                            Success = false,
                            Reason = "No previous session found. Please join normally."
                        }));
                        client.Disconnect();
                        return;
                    }

                    // Restore the original PlayerId
                    client.RestorePlayerId(dc.PlayerId);
                    client.CharacterInfo = reconnPayload.Character ?? dc.CharacterInfo;
                    client.PlayerName = reconnPayload.Character?.PlayerName ?? dc.PlayerName;
                    client.IsSpectator = reconnPayload.Character?.IsSpectator ?? dc.IsSpectator;
                    // reconnect path

                    // Send reconnect result with catch-up turns
                    StoryEntry[] catchUp;
                    lock (dc.MissedStoryTurns)
                    {
                        catchUp = dc.MissedStoryTurns.ToArray();
                    }

                    client.Send(Packet.Create(PacketType.ReconnectResult, new ReconnectResultPayload
                    {
                        Success = true,
                        AssignedPlayerId = dc.PlayerId,
                        CatchUpTurns = catchUp,
                        Reason = $"Reconnected! {catchUp.Length} missed turn(s)."
                    }));

                    lock (_clientLock) { _clients.Add(client); }
                    MainThreadQueue.Enqueue(() => OnClientReconnected?.Invoke(client, reconnPayload));
                    Log.LogInfo($"[Server] Client {client.PlayerId} ({client.PlayerName}) reconnected with {catchUp.Length} catch-up turns.");
                }
                else if (firstPkt.Type == PacketType.Hello)
                {
                    var helloPayload = firstPkt.GetPayload<HelloPayload>();
                    client.CharacterInfo = helloPayload.Character;
                    client.PlayerName = helloPayload.Character?.PlayerName ?? "Unknown";
                    client.IsSpectator = helloPayload.IsSpectator || (helloPayload.Character?.IsSpectator ?? false);

                    lock (_clientLock) { _clients.Add(client); }
                    MainThreadQueue.Enqueue(() => OnClientConnected?.Invoke(client, helloPayload));
                }
                else
                {
                    Log.LogWarning($"[Server] Client sent unexpected first packet: {firstPkt.Type} — rejecting.");
                    client.Send(Packet.Create(PacketType.Rejected));
                    client.Disconnect();
                    return;
                }

                // Read loop
                while (IsRunning && client.IsConnected)
                {
                    Packet pkt = Packet.ReadFrom(stream);
                    if (pkt == null) break;

                    HandleClientPacket(client, pkt);
                }
            }
            catch (Exception) when (!IsRunning || !client.IsConnected)
            {
                // Normal teardown
            }
            catch (Exception ex)
            {
                Log.LogError($"[Server] Client {client.PlayerId} error: {ex.Message}");
            }
            finally
            {
                lock (_clientLock) { _clients.Remove(client); }

                // Save disconnected player slot for potential reconnection
                lock (_disconnectedLock)
                {
                    CleanupExpiredDisconnects();
                    _disconnectedPlayers[client.PlayerId] = new DisconnectedPlayer
                    {
                        PlayerId = client.PlayerId,
                        PlayerName = client.PlayerName,
                        CharacterInfo = client.CharacterInfo,
                        IsSpectator = client.IsSpectator,
                        DisconnectedAt = DateTime.UtcNow
                    };
                }

                client.Disconnect();
                MainThreadQueue.Enqueue(() => OnClientDisconnected?.Invoke(client));
                Log.LogInfo($"[Server] Client {client.PlayerId} ({client.PlayerName}) disconnected. Reconnect slot saved.");
            }
        }

        private void CleanupExpiredDisconnects()
        {
            // Must be called under _disconnectedLock
            var expired = new List<string>();
            var cutoff = DateTime.UtcNow.AddMinutes(-ReconnectTimeoutMinutes);
            foreach (var kvp in _disconnectedPlayers)
            {
                if (kvp.Value.DisconnectedAt < cutoff)
                    expired.Add(kvp.Key);
            }
            foreach (var key in expired)
                _disconnectedPlayers.Remove(key);
        }

        private void HandleClientPacket(ConnectedClient client, Packet pkt)
        {
            switch (pkt.Type)
            {
                case PacketType.ActionRequest:
                    var action = pkt.GetPayload<ActionRequestPayload>();
                    action.PlayerId = client.PlayerId;
                    action.CharacterName = client.CharacterInfo?.CharacterName ?? client.PlayerName;
                    MainThreadQueue.Enqueue(() => OnActionReceived?.Invoke(client, action));
                    break;

                case PacketType.TurnReady:
                    // Client has submitted their action for this turn
                    client.SetTurnReady(true);
                    MainThreadQueue.Enqueue(() => OnTurnReady?.Invoke(client));
                    break;

                case PacketType.Chat:
                    var chat = pkt.GetPayload<ChatPayload>();
                    chat.SenderName = client.PlayerName;
                    MainThreadQueue.Enqueue(() => OnChatReceived?.Invoke(client, chat));
                    // Relay to all OTHER clients
                    lock (_clientLock)
                    {
                        foreach (var c in _clients)
                            if (c != client) c.Send(Packet.Create(PacketType.Chat, chat));
                    }
                    break;

                case PacketType.CharacterUpdate:
                    var charUpdate = pkt.GetPayload<RemoteCharacterInfo>();
                    client.CharacterInfo = charUpdate;
                    MainThreadQueue.Enqueue(() => OnCharacterUpdateReceived?.Invoke(client, charUpdate));
                    break;

                case PacketType.ItemTransfer:
                    var transfer = pkt.GetPayload<ItemTransferPayload>();
                    transfer.FromPlayerId = client.PlayerId; // Always authoritative from server
                    MainThreadQueue.Enqueue(() => OnItemTransferReceived?.Invoke(client, transfer));
                    break;

                case PacketType.PrivateAction:
                    var privAction = pkt.GetPayload<PrivateActionPayload>();
                    privAction.PlayerId = client.PlayerId;
                    MainThreadQueue.Enqueue(() => OnPrivateActionReceived?.Invoke(client, privAction));
                    break;

                case PacketType.CombatAction:
                    var combatAction = pkt.GetPayload<CombatActionPayload>();
                    combatAction.PlayerId = client.PlayerId;
                    MainThreadQueue.Enqueue(() => OnCombatActionReceived?.Invoke(client, combatAction));
                    break;

                case PacketType.Ping:
                    client.Send(Packet.Create(PacketType.Pong));
                    break;

                case PacketType.Disconnect:
                    client.Disconnect();
                    break;
            }
        }
    }

    /// <summary>
    /// Represents a single connected remote player on the host side.
    /// </summary>
    public class ConnectedClient
    {
        private static ManualLogSource Log => MultiplayerPlugin.Instance.Log;

        public string PlayerId { get; private set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string PlayerName { get; set; } = "Unknown";
        public RemoteCharacterInfo CharacterInfo { get; set; }
        public bool IsSpectator { get; set; }
        public bool IsConnected => _tcp?.Connected ?? false;
        public string RemoteEndPoint => _tcp?.Client?.RemoteEndPoint?.ToString() ?? "?";

        // Pending action from this client, consumed by GameplayPatch on next turn
        public string PendingAction { get; private set; }

        // v2.0: has this client signalled TurnReady for the current turn?
        public bool IsTurnReady { get; private set; }

        private readonly TcpClient _tcp;
        private readonly AIROGServer _server;
        private readonly object _sendLock = new object();

        public ConnectedClient(TcpClient tcp, AIROGServer server)
        {
            _tcp = tcp;
            _server = server;
        }

        /// <summary>Restores a previous PlayerId during reconnection.</summary>
        public void RestorePlayerId(string id) => PlayerId = id;

        public void SetPendingAction(string action) => PendingAction = action;
        public void ClearPendingAction() => PendingAction = null;
        public void SetTurnReady(bool ready) => IsTurnReady = ready;
        public void ResetTurn() { IsTurnReady = false; PendingAction = null; }

        public NetworkStream GetStream() => _tcp.GetStream();

        public void Send(Packet packet)
        {
            lock (_sendLock)
            {
                byte[] data = packet.Serialize();
                _tcp.GetStream().Write(data, 0, data.Length);
            }
        }

        public void Disconnect()
        {
            try { _tcp?.Close(); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Tracks a disconnected player's state for potential reconnection.
    /// </summary>
    public class DisconnectedPlayer
    {
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public RemoteCharacterInfo CharacterInfo { get; set; }
        public bool IsSpectator { get; set; }
        public DateTime DisconnectedAt { get; set; }
        public List<StoryEntry> MissedStoryTurns { get; } = new List<StoryEntry>();
    }
}
