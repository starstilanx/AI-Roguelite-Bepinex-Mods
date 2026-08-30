using System;
using System.Linq;
using System.Threading.Tasks;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;
using AIROG_Multiplayer.Patches;

namespace AIROG_Multiplayer
{
    // --- Server event handlers (main thread) ---
    public partial class MultiplayerPlugin
    {
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
            Toast.I.ShowToast($"{toastIcon} {charName} {joinVerb} the co-op session!");

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
            Toast.I.ShowToast($"⚔ {charName} left the co-op session.");
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
            Toast.I.ShowToast($"⚔ {charName}: {TruncateForToast(action.ActionText)}");

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
            Toast.I.ShowToast($"⚔ {charName} reconnected!");
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
    }
}
