using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_Multiplayer.Network;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIROG_Multiplayer
{
    // Split via `partial` across MultiplayerPlugin.*.cs — every consumer references static
    // members as MultiplayerPlugin.X (AIROGClient, AIROGServer, the UI panels, the Harmony
    // patches), so this class and its public static member names must stay put. See
    // MultiplayerPlugin.TurnGate.cs (turn-gate + save broadcast), MultiplayerPlugin.HostApi.cs
    // / .ClientApi.cs (session start/stop), MultiplayerPlugin.ServerEvents.cs (host-side
    // network event handlers), MultiplayerPlugin.CombatSync.cs (host combat resolution),
    // MultiplayerPlugin.Helpers.cs (broadcast/snapshot helpers), MultiplayerPlugin.ClientImageSync.cs
    // (client-side story image application).
    [BepInPlugin(GUID, NAME, VERSION)]
    public partial class MultiplayerPlugin : BaseUnityPlugin
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
    }
}
