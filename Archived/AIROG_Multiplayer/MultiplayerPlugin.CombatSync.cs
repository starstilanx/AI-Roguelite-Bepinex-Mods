using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_Multiplayer.Combat;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer
{
    // Host-side combat: collecting client combat actions and resolving rounds via the AI.
    public partial class MultiplayerPlugin
    {
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
    }
}
