using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIROG_Multiplayer.Network;
using UnityEngine;

namespace AIROG_Multiplayer
{
    // Turn gate (v2.0: hold host's turn until all clients submit, or timeout) and the
    // save/broadcast plumbing that feeds it.
    public partial class MultiplayerPlugin
    {
        /// <summary>
        /// Called from GameplayMultiplayerPatch prefix — returns true if we should BLOCK the turn.
        /// </summary>
        public static bool ShouldBlockTurn(out string reason)
        {
            reason = null;
            if (!IsHost || Server == null) return false;
            if (!(Instance?.WaitForParty?.Value ?? true)) return false;

            var clients = Server.GetClients().Where(c => !c.IsSpectator).ToList();
            if (clients.Count == 0) return false; // No non-spectator clients, no wait

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

            var allClients = Server?.GetClients();
            if (allClients == null) return;

            var clients = allClients.Where(c => !c.IsSpectator).ToList();
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
    }
}
