using System;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer
{
    public partial class MultiplayerPlugin
    {
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
    }
}
