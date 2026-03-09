using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using AIROG_Multiplayer.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIROG_Multiplayer
{
    /// <summary>
    /// The TCP client side — runs on the joining player's machine.
    /// Connects to the host, sends actions, receives story turns and save data.
    ///
    /// On receiving a SaveData packet:
    ///   • Decompresses the GZip save JSON
    ///   • Writes it to {saveTopLvlDir}/mp_client/my_save.txt
    ///   • First time: triggers SceneManager.LoadScene("Main Scene") so the game
    ///     engine loads the save normally via its own StartGame() flow.
    ///   • Subsequent times: calls SS.I.hackyManager.LoadGame(saveData) in-place.
    /// </summary>
    public class AIROGClient
    {
        private static ManualLogSource Log => MultiplayerPlugin.Instance?.Log;

        public bool IsConnected => _tcp?.Connected ?? false;
        public string AssignedPlayerId { get; private set; }
        public string HostAddress { get; private set; }
        public int Port { get; private set; }
        public WelcomePayload WelcomeInfo { get; private set; }

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _readThread;
        private readonly object _sendLock = new object();
        private bool _running = false;

        // True after the first save has been loaded into the game engine
        private bool _inGameSession = false;

        // Relative save subdirectory used for the client's session save
        private const string MP_CLIENT_SAVE_DIR = "mp_client";

        // Main-thread event queue
        public readonly System.Collections.Concurrent.ConcurrentQueue<Action> MainThreadQueue
            = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        // Events raised on Unity main thread
        public event Action<WelcomePayload> OnConnected;
        public event Action<string> OnDisconnected;       // reason string
        public event Action<StoryEntry> OnStoryTurnReceived;
        public event Action<RemoteCharacterInfo> OnCharacterUpdated;
        public event Action<PartyUpdatePayload> OnPartyUpdated;
        public event Action<ChatPayload> OnChatReceived;
        public event Action<LocationUpdatePayload> OnLocationUpdated;
        public event Action<SaveDataPayload> OnSaveDataReceived;

        // v2.0 events
        public event Action OnTurnBegin;
        public event Action<WaitingForPartyPayload> OnWaitingForParty;
        public event Action<StoryImagePayload> OnStoryImageReceived;

        // Inventory
        public event Action<InventorySyncPayload> OnInventoryReceived;

        /// <summary>
        /// Attempts to connect to the host. Fires OnConnected or OnDisconnected on the main thread.
        /// </summary>
        public void Connect(string host, int port, RemoteCharacterInfo myCharacter)
        {
            HostAddress = host;
            Port = port;
            _running = true;
            _inGameSession = false;

            // Connect on background thread so we don't freeze Unity
            Thread connectThread = new Thread(() =>
            {
                try
                {
                    _tcp = new TcpClient();
                    _tcp.NoDelay = true;
                    _tcp.Connect(host, port); // Blocking connect, 5s timeout handled by OS
                    _stream = _tcp.GetStream();

                    // Send Hello
                    var hello = Packet.Create(PacketType.Hello, new HelloPayload
                    {
                        PluginVersion = MultiplayerPlugin.VERSION,
                        Character = myCharacter
                    });
                    SendInternal(hello);

                    // Wait for Welcome or Rejected
                    Packet response = Packet.ReadFrom(_stream);
                    if (response == null || response.Type == PacketType.Rejected)
                    {
                        string reason = response == null ? "Connection lost during handshake." : "Host rejected connection.";
                        Cleanup(reason);
                        return;
                    }

                    if (response.Type != PacketType.Welcome)
                    {
                        Cleanup("Unexpected handshake response from host.");
                        return;
                    }

                    WelcomeInfo = response.GetPayload<WelcomePayload>();
                    AssignedPlayerId = WelcomeInfo.AssignedPlayerId;

                    MainThreadQueue.Enqueue(() => OnConnected?.Invoke(WelcomeInfo));

                    // Start read loop
                    _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "AIROG-Client-Read" };
                    _readThread.Start();
                }
                catch (Exception ex)
                {
                    Cleanup($"Failed to connect: {ex.Message}");
                }
            })
            { IsBackground = true, Name = "AIROG-Client-Connect" };

            connectThread.Start();
        }

        public void Disconnect(string reason = "Player disconnected.")
        {
            try
            {
                if (IsConnected)
                    SendInternal(Packet.Create(PacketType.Disconnect));
            }
            catch { /* ignore */ }
            Cleanup(reason);
        }

        /// <summary>Sends an action request to the host.</summary>
        public void SendAction(string actionText)
        {
            Send(Packet.Create(PacketType.ActionRequest, new ActionRequestPayload
            {
                ActionText = actionText,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }));
        }

        /// <summary>v2.0: Tells the host this client is ready for the current turn.</summary>
        public void SendTurnReady()
        {
            Send(Packet.Create(PacketType.TurnReady));
        }

        /// <summary>Sends an out-of-character chat message to the host.</summary>
        public void SendChat(string message)
        {
            Send(Packet.Create(PacketType.Chat, new ChatPayload
            {
                SenderName = MultiplayerPlugin.LocalCharacterName,
                Message = message
            }));
        }

        public void Send(Packet packet)
        {
            if (!IsConnected) return;
            try { SendInternal(packet); }
            catch (Exception ex) { Log?.LogWarning($"[Client] Send failed: {ex.Message}"); }
        }

        // --- Private ---

        private void SendInternal(Packet packet)
        {
            lock (_sendLock)
            {
                byte[] data = packet.Serialize();
                _stream.Write(data, 0, data.Length);
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (_running && IsConnected)
                {
                    Packet pkt = Packet.ReadFrom(_stream);
                    if (pkt == null) break;
                    HandlePacket(pkt);
                }
            }
            catch (Exception) when (!_running)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.Log($"[MP-DIAG] ReadLoop exception: {ex.GetType().Name}: {ex.Message}");
                Log?.LogError($"[Client] Read error: {ex.Message}");
            }
            finally
            {
                Cleanup("Lost connection to host.");
            }
        }

        private void HandlePacket(Packet pkt)
        {
            // Diagnostic: log every received packet type so we can verify the host is sending data.
            Log?.LogInfo($"[Client] Received packet: {pkt.Type}");

            switch (pkt.Type)
            {
                case PacketType.StoryTurn:
                    var entry = pkt.GetPayload<StoryEntry>();
                    MainThreadQueue.Enqueue(() => OnStoryTurnReceived?.Invoke(entry));
                    break;

                case PacketType.CharacterUpdate:
                    var charInfo = pkt.GetPayload<RemoteCharacterInfo>();
                    MainThreadQueue.Enqueue(() => OnCharacterUpdated?.Invoke(charInfo));
                    break;

                case PacketType.PartyUpdate:
                    var party = pkt.GetPayload<PartyUpdatePayload>();
                    MainThreadQueue.Enqueue(() => OnPartyUpdated?.Invoke(party));
                    break;

                case PacketType.Chat:
                    var chat = pkt.GetPayload<ChatPayload>();
                    MainThreadQueue.Enqueue(() => OnChatReceived?.Invoke(chat));
                    break;

                case PacketType.LocationUpdate:
                    var loc = pkt.GetPayload<LocationUpdatePayload>();
                    MainThreadQueue.Enqueue(() => OnLocationUpdated?.Invoke(loc));
                    break;

                case PacketType.SaveData:
                    HandleSaveData(pkt.GetPayload<SaveDataPayload>());
                    break;

                // --- v2.0 packets ---

                case PacketType.TurnBegin:
                    MainThreadQueue.Enqueue(() => OnTurnBegin?.Invoke());
                    break;

                case PacketType.WaitingForParty:
                    var waiting = pkt.GetPayload<WaitingForPartyPayload>();
                    MainThreadQueue.Enqueue(() => OnWaitingForParty?.Invoke(waiting));
                    break;

                case PacketType.StoryImage:
                    HandleStoryImage(pkt.GetPayload<StoryImagePayload>());
                    break;

                case PacketType.ActionQueued:
                    MainThreadQueue.Enqueue(() =>
                        CoopStatusOverlay.Instance?.ShowActionQueued("✓ Action queued — waiting for host turn..."));
                    break;

                case PacketType.InventorySync:
                    var invPayload = pkt.GetPayload<InventorySyncPayload>();
                    MainThreadQueue.Enqueue(() => OnInventoryReceived?.Invoke(invPayload));
                    break;

                case PacketType.Ping:
                    Send(Packet.Create(PacketType.Pong));
                    break;

                case PacketType.Disconnect:
                    Cleanup("Host ended the session.");
                    break;
            }
        }

        /// <summary>
        /// Receives the host's save file, writes it to disk, and triggers the game to load it.
        /// Runs on the background read thread; file I/O is safe here.
        /// Game load is dispatched to the main thread via MainThreadQueue.
        /// </summary>
        private void HandleSaveData(SaveDataPayload payload)
        {
            // Decompress or fall back to legacy uncompressed JSON
            string saveJson = null;
            if (!string.IsNullOrEmpty(payload.SaveFileGzipB64))
            {
                try { saveJson = PacketUtils.GzipDecompress(payload.SaveFileGzipB64); }
                catch (Exception ex) { Log?.LogError($"[Client] SaveData decompress error: {ex.Message}"); }
            }
            if (saveJson == null)
                saveJson = payload.SaveJson; // legacy fallback

            if (string.IsNullOrEmpty(saveJson))
            {
                Log?.LogWarning("[Client] Received empty SaveData — skipping.");
                // Still notify overlay with world summary
                MainThreadQueue.Enqueue(() => OnSaveDataReceived?.Invoke(payload));
                return;
            }

            // Rewrite image paths: replace "{hostSaveSubDir}/" with "mp_client/" so the game finds images
            string hostDir = payload.HostSaveSubDir;
            if (!string.IsNullOrEmpty(hostDir) && hostDir != MP_CLIENT_SAVE_DIR)
            {
                saveJson = saveJson.Replace(hostDir + "/", MP_CLIENT_SAVE_DIR + "/");
                // Also handle any backslash variants (Windows paths serialised into JSON)
                saveJson = saveJson.Replace(hostDir + "\\\\", MP_CLIENT_SAVE_DIR + "/");
            }

            // Write to disk (background thread — safe)
            try
            {
                string saveTopDir = MultiplayerPlugin.SaveTopLvlDir;
                string saveDirPath = Path.Combine(saveTopDir, MP_CLIENT_SAVE_DIR);
                Directory.CreateDirectory(saveDirPath);
                File.WriteAllText(Path.Combine(saveDirPath, "my_save.txt"), saveJson, Encoding.UTF8);
                Log?.LogInfo($"[Client] Save written: {saveJson.Length} chars → {saveDirPath}/my_save.txt");

                // Write Voronoi polygon files ({uuid}.txt) so the world map loads without error
                if (payload.PolygonFiles != null)
                {
                    foreach (var kvp in payload.PolygonFiles)
                        File.WriteAllText(Path.Combine(saveDirPath, kvp.Key + ".txt"), kvp.Value, Encoding.UTF8);
                    Log?.LogInfo($"[Client] Wrote {payload.PolygonFiles.Count} polygon file(s).");
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[Client] Failed to write save: {ex.Message}");
                MainThreadQueue.Enqueue(() => OnSaveDataReceived?.Invoke(payload));
                return;
            }

            // Determine whether this is the first load (needs scene change) or an in-place reload
            bool firstLoad = !_inGameSession;
            if (firstLoad) _inGameSession = true;

            // Queue the actual game load on the main thread
            MainThreadQueue.Enqueue(() =>
            {
                try
                {
                    if (firstLoad)
                    {
                        // Trigger the game's normal load flow: StartGame() will read mp_client/my_save.txt
                        // saveSubDirAsArg must be the full absolute path — the game uses it directly
                        // in BytesToTexture2 without ever combining it with saveTopLvlDir.
                        SS.I.saveSubDirAsArg = Path.Combine(SS.I.saveTopLvlDir, MP_CLIENT_SAVE_DIR);
                        SS.I.gameMode = SS.GameMode.LOAD;
                        SceneManager.LoadScene("Main Scene");
                        Log?.LogInfo("[Client] Triggered scene load with host's save.");
                    }
                    else
                    {
                        // Already in a game session — reload in place
                        var saveData = SaveIO.ReadSaveFile(MP_CLIENT_SAVE_DIR);
                        if (saveData == null)
                        {
                            Log?.LogWarning("[Client] In-place reload: ReadSaveFile returned null.");
                        }
                        else if (SS.I.hackyManager == null)
                        {
                            Log?.LogWarning("[Client] In-place reload: hackyManager is null — game not fully loaded yet.");
                        }
                        else
                        {
                            _ = SS.I.hackyManager.LoadGame(saveData);
                            Log?.LogInfo("[Client] Reloaded save in-place.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log?.LogError($"[Client] Game load error: {ex.Message}");
                }
            });

            // Notify overlay with the world summary fields
            MainThreadQueue.Enqueue(() => OnSaveDataReceived?.Invoke(payload));
        }

        /// <summary>
        /// Writes the story image to the client's save directory so the game engine can find it.
        /// Fires OnStoryImageReceived (on main thread) so MultiplayerPlugin can refresh the texture
        /// without a full LoadGame reload.
        /// </summary>
        private void HandleStoryImage(StoryImagePayload img)
        {
            if (img == null || string.IsNullOrEmpty(img.PngBase64)) return;

            // Write image file to client save dir so the game can load it from save references
            if (!string.IsNullOrEmpty(img.FileName))
            {
                try
                {
                    byte[] imgBytes = Convert.FromBase64String(img.PngBase64);
                    string saveDirPath = Path.Combine(MultiplayerPlugin.SaveTopLvlDir, MP_CLIENT_SAVE_DIR);
                    Directory.CreateDirectory(saveDirPath);
                    File.WriteAllBytes(Path.Combine(saveDirPath, img.FileName), imgBytes);
                    Log?.LogInfo($"[Client] Story image saved: {img.FileName} ({imgBytes.Length / 1024}KB)");
                }
                catch (Exception ex)
                {
                    Log?.LogWarning($"[Client] Failed to write story image: {ex.Message}");
                }
            }

            // Notify the plugin (fires on main thread) so it can refresh the displayed texture.
            // Do NOT call LoadGame here — a full reload to display an image is too heavy and
            // causes the "MaybeReEnablePlayerInteractions before Startup" stuck-UI issue.
            // Instead MultiplayerPlugin.OnStoryImageReceived finds the IllustratedStoryTurn
            // by UUID and calls UpdateMainImageWithXfade directly.
            MainThreadQueue.Enqueue(() => OnStoryImageReceived?.Invoke(img));
        }

        private void Cleanup(string reason)
        {
            _running = false;
            _inGameSession = false;
            // Use UnityEngine.Debug.Log unconditionally — Log might be null if Instance is null
            UnityEngine.Debug.Log($"[MP-DIAG] AIROGClient.Cleanup called: {reason}");
            try { _tcp?.Close(); } catch { /* ignore */ }
            MainThreadQueue.Enqueue(() => OnDisconnected?.Invoke(reason));
            Log?.LogInfo($"[Client] Disconnected: {reason}");
        }
    }
}
