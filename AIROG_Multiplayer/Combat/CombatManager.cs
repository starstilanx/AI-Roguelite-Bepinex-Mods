using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AIROG_Multiplayer.Network;

namespace AIROG_Multiplayer.Combat
{
    /// <summary>
    /// Static state machine for shared combat / initiative.
    /// Tracks turn order, collects player actions, and builds a combined prompt
    /// for the AI to resolve once all players have acted.
    ///
    /// States:
    ///   None            → no active combat
    ///   WaitingForActions → combat active, collecting actions from players in turn order
    ///   Resolving        → all actions collected, AI is resolving the round
    /// </summary>
    public static class CombatManager
    {
        public enum CombatState
        {
            None,
            WaitingForActions,
            Resolving
        }

        public static CombatState State { get; private set; } = CombatState.None;
        public static bool IsCombatActive => State != CombatState.None;
        public static int RoundNumber { get; private set; } = 0;

        // Turn order: character names in initiative sequence
        private static List<string> _turnOrder = new List<string>();
        // Enemy names for display
        private static List<string> _enemyNames = new List<string>();
        // Collected actions: characterName → actionText
        private static Dictionary<string, string> _collectedActions = new Dictionary<string, string>();
        // Set of player IDs that have submitted actions this round
        private static HashSet<string> _submittedPlayers = new HashSet<string>();
        // Total non-spectator players expected to act
        private static int _expectedPlayerCount = 0;

        // Events (fired on main thread by MultiplayerPlugin)
        public static event Action OnAllActionsCollected;
        public static event Action<string> OnCombatStarted;   // combined enemy names
        public static event Action OnCombatEnded;

        /// <summary>
        /// Starts a new combat encounter. Called by the host when combat is detected.
        /// </summary>
        public static void BeginCombat(string[] turnOrder, string[] enemyNames, int playerCount)
        {
            State = CombatState.WaitingForActions;
            RoundNumber = 1;
            _turnOrder = new List<string>(turnOrder ?? new string[0]);
            _enemyNames = new List<string>(enemyNames ?? new string[0]);
            _collectedActions.Clear();
            _submittedPlayers.Clear();
            _expectedPlayerCount = playerCount;

            OnCombatStarted?.Invoke(string.Join(", ", _enemyNames));
        }

        /// <summary>
        /// Advances to the next round (after AI resolution).
        /// </summary>
        public static void NextRound()
        {
            RoundNumber++;
            State = CombatState.WaitingForActions;
            _collectedActions.Clear();
            _submittedPlayers.Clear();
        }

        /// <summary>
        /// Records a player's combat action. Returns true if all actions are now collected.
        /// </summary>
        public static bool SubmitAction(string playerId, string characterName, string actionText)
        {
            if (State != CombatState.WaitingForActions) return false;
            if (_submittedPlayers.Contains(playerId)) return false;

            _submittedPlayers.Add(playerId);
            _collectedActions[characterName] = actionText;

            if (_submittedPlayers.Count >= _expectedPlayerCount)
            {
                State = CombatState.Resolving;
                OnAllActionsCollected?.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Builds a combined prompt for the AI to resolve all combat actions in one response.
        /// </summary>
        public static string BuildCombatPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[COMBAT ROUND {RoundNumber}]");
            sb.AppendLine($"Enemies: {string.Join(", ", _enemyNames)}");
            sb.AppendLine();

            // Actions in turn order
            foreach (var charName in _turnOrder)
            {
                if (_collectedActions.TryGetValue(charName, out string action))
                    sb.AppendLine($"- {charName}: {action}");
                else
                    sb.AppendLine($"- {charName}: (no action submitted)");
            }

            // Include any actions from characters not in the turn order (shouldn't happen, but safety)
            foreach (var kvp in _collectedActions)
            {
                if (!_turnOrder.Contains(kvp.Key))
                    sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("Resolve all combat actions simultaneously. Describe the outcome for each character and the enemies' responses. Be concise but dramatic.");

            return sb.ToString();
        }

        /// <summary>
        /// Ends the combat encounter.
        /// </summary>
        public static void EndCombat()
        {
            State = CombatState.None;
            RoundNumber = 0;
            _turnOrder.Clear();
            _enemyNames.Clear();
            _collectedActions.Clear();
            _submittedPlayers.Clear();
            _expectedPlayerCount = 0;
            OnCombatEnded?.Invoke();
        }

        /// <summary>
        /// Returns a display-friendly summary of the current combat state.
        /// </summary>
        public static string GetStatusSummary()
        {
            if (!IsCombatActive) return "";
            return $"Combat Round {RoundNumber} — {_submittedPlayers.Count}/{_expectedPlayerCount} actions submitted";
        }

        public static string[] GetTurnOrder() => _turnOrder.ToArray();
        public static string[] GetEnemyNames() => _enemyNames.ToArray();
        public static int GetSubmittedCount() => _submittedPlayers.Count;
        public static int GetExpectedCount() => _expectedPlayerCount;
    }
}
