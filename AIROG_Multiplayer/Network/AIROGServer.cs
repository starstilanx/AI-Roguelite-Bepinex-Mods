using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

                // First packet MUST be Hello
                Packet hello = Packet.ReadFrom(stream);
                if (hello == null || hello.Type != PacketType.Hello)
                {
                    Log.LogWarning("[Server] Client sent no Hello packet — rejecting.");
                    client.Send(Packet.Create(PacketType.Rejected));
                    client.Disconnect();
                    return;
                }

                var helloPayload = hello.GetPayload<HelloPayload>();
                client.CharacterInfo = helloPayload.Character;
                client.PlayerName = helloPayload.Character?.PlayerName ?? "Unknown";

                lock (_clientLock) { _clients.Add(client); }

                // Notify main thread
                MainThreadQueue.Enqueue(() => OnClientConnected?.Invoke(client, helloPayload));

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
                client.Disconnect();
                MainThreadQueue.Enqueue(() => OnClientDisconnected?.Invoke(client));
                Log.LogInfo($"[Server] Client {client.PlayerId} ({client.PlayerName}) disconnected.");
            }
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

        public string PlayerId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string PlayerName { get; set; } = "Unknown";
        public RemoteCharacterInfo CharacterInfo { get; set; }
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
}
