using System;
using System.IO;
using AIROG_Multiplayer.Combat;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;
using UnityEngine;

namespace AIROG_Multiplayer
{
    public partial class MultiplayerPlugin
    {
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
    }
}
