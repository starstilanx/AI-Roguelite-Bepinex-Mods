using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    public static class ScenarioUpdater
    {
        // ─── Counters ──────────────────────────────────────────────────────────────
        private static int _globalTurn        = 0;
        private static int _autonomyCounter   = 0;
        private static int _barkCounter       = 0;
        private static int _rumorCounter      = 0;
        private static int _memoryCounter     = 0;

        // Per-NPC scenario update scheduling: each NPC gets a random 2-5 turn cooldown
        // after each update so they stagger naturally instead of all firing together.
        private static readonly Dictionary<string, int> _npcNextUpdateTurn = new Dictionary<string, int>();
        private const int SCENARIO_MIN_INTERVAL     = 2;
        private const int SCENARIO_MAX_INTERVAL     = 5;
        private const int AUTONOMY_TURNS_PER_UPDATE = 3;
        private const int BARK_INTERVAL             = 5;
        private const int RUMOR_INTERVAL            = 3;
        private const int MEMORY_INTERVAL           = 10;

        private static bool _isUpdating = false;

        private const string STATE_FILE = "npcexpansion_state.json";

        /// <summary>Current global turn count. Used by QuestManager and new systems.</summary>
        public static int GlobalTurn => _globalTurn;

        private class PersistedState
        {
            public int GlobalTurn;
        }

        // ─── Persistence ───────────────────────────────────────────────────────────
        // GlobalTurn is referenced by saved quest data (TurnGiven/TurnDeadline) and
        // per-NPC cooldowns (LastBarkTurn, MemorySynthesisTurn), so it must survive
        // save/load instead of drifting with the app session.

        public static void SaveState(string saveDir)
        {
            try
            {
                if (!Directory.Exists(saveDir)) return;
                File.WriteAllText(Path.Combine(saveDir, STATE_FILE),
                    JsonConvert.SerializeObject(new PersistedState { GlobalTurn = _globalTurn }));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] SaveState failed: {ex.Message}");
            }
        }

        public static void LoadState(string saveDir)
        {
            // Fresh session: clear per-NPC scheduling and interval counters so stale
            // UUIDs from a previously loaded save don't linger.
            _npcNextUpdateTurn.Clear();
            _autonomyCounter = 0;
            _barkCounter     = 0;
            _rumorCounter    = 0;
            _memoryCounter   = 0;
            _globalTurn      = 0;

            try
            {
                string path = Path.Combine(saveDir, STATE_FILE);
                if (!File.Exists(path)) return;
                var state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(path));
                if (state != null) _globalTurn = state.GlobalTurn;
                Debug.Log($"[AIROG_NPCExpansion] Restored global turn {_globalTurn}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] LoadState failed: {ex.Message}");
            }
        }

        // ─── Main Hook ─────────────────────────────────────────────────────────────
        public static void OnTurnHappened(int numTurns, long secs)
        {
            _globalTurn      += numTurns;
            _autonomyCounter += numTurns;
            _barkCounter     += numTurns;
            _rumorCounter    += numTurns;
            _memoryCounter   += numTurns;

            Debug.Log($"[AIROG_NPCExpansion] OnTurnHappened: {numTurns} turns (global={_globalTurn}). " +
                      $"Auto={_autonomyCounter}/{AUTONOMY_TURNS_PER_UPDATE}, " +
                      $"Bark={_barkCounter}/{BARK_INTERVAL}, Rumor={_rumorCounter}/{RUMOR_INTERVAL}, " +
                      $"Mem={_memoryCounter}/{MEMORY_INTERVAL}");

            var manager = SS.I?.hackyManager;
            if (manager == null || manager.currentPlace == null) return;

            var nearbyNpcs = manager.GetCharsForNpcConvoSelectorDropdown()
                ?.Where(c => c != null && c.corpseState == GameCharacter.CorpseState.NONE)
                .ToList();

            // ── Autonomy (every 3 turns) ──────────────────────────────────────────
            if (_autonomyCounter >= AUTONOMY_TURNS_PER_UPDATE)
            {
                _autonomyCounter = 0;
                if (nearbyNpcs != null)
                {
                    foreach (var npc in nearbyNpcs)
                        _ = NPCAutonomy.Process(npc, manager);
                }
            }

            // ── Scenario update (per-NPC staggered, random 2-5 turn cooldown) ────
            if (nearbyNpcs != null && nearbyNpcs.Count > 0)
            {
                var dueNpcs = nearbyNpcs.Where(npc =>
                {
                    if (!_npcNextUpdateTurn.TryGetValue(npc.uuid, out int nextTurn))
                    {
                        // First time seeing this NPC — stagger initial update randomly
                        _npcNextUpdateTurn[npc.uuid] = _globalTurn + UnityEngine.Random.Range(SCENARIO_MIN_INTERVAL, SCENARIO_MAX_INTERVAL + 1);
                        return false;
                    }
                    return _globalTurn >= nextTurn;
                }).ToList();

                if (dueNpcs.Count > 0)
                {
                    // Assign next update times before the async task so they don't re-fire
                    foreach (var npc in dueNpcs)
                        _npcNextUpdateTurn[npc.uuid] = _globalTurn + UnityEngine.Random.Range(SCENARIO_MIN_INTERVAL, SCENARIO_MAX_INTERVAL + 1);

                    string context = manager.GetContextForQuickActions();
                    Debug.Log($"[AIROG_NPCExpansion] Triggering scenario update for {dueNpcs.Count}/{nearbyNpcs.Count} NPCs (staggered).");
                    _ = UpdateNpcsTask(dueNpcs, context);
                }
            }

            // ── NPC Barks (every 5 turns) ─────────────────────────────────────────
            if (_barkCounter >= BARK_INTERVAL)
            {
                _barkCounter = 0;
                if (nearbyNpcs != null)
                {
                    foreach (var npc in nearbyNpcs)
                    {
                        var data = NPCData.Load(npc.uuid);
                        if (data != null) _ = NPCBarkSystem.TryBark(npc, data, _globalTurn, manager);
                    }
                }
            }

            // ── Rumor + Gossip propagation (every 3 turns) ────────────────────────
            if (_rumorCounter >= RUMOR_INTERVAL)
            {
                _rumorCounter = 0;
                if (nearbyNpcs != null)
                {
                    WorldNewsGossip.SeedWorldNews(nearbyNpcs); // world-sim events enter the rumor pool
                    RumorNetwork.PropagateInPlace(nearbyNpcs);
                    WorldGossipSystem.ProcessGossipInPlace(nearbyNpcs);
                }
            }

            // ── Memory Synthesis + Quest Deadlines (every 10 turns) ───────────────
            if (_memoryCounter >= MEMORY_INTERVAL)
            {
                _memoryCounter = 0;
                if (nearbyNpcs != null)
                {
                    foreach (var npc in nearbyNpcs)
                    {
                        var data = NPCData.Load(npc.uuid);
                        if (data != null) _ = NPCMemorySynthesis.SynthesizeForNpc(npc, data, _globalTurn);
                    }
                }
                QuestManager.CheckDeadlines(_globalTurn, manager);
            }

            // One bundle write per turn covering everything the tick touched (barks, rumors,
            // gossip, memories, reputation). GenContext injects from npcexpansion_lore.json,
            // so without this the whole simulation stays invisible to the AI until the game
            // next autosaves — which, on non-EVERY_TURN autosave modes, may be a long while.
            // No-op when nothing was modified.
            NPCData.FlushSessionLore();
        }

        // ─── Scenario Update Task ──────────────────────────────────────────────────
        private static async Task UpdateNpcsTask(List<GameCharacter> npcs, string context)
        {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                Debug.Log($"[AIROG_NPCExpansion] ScenarioUpdater starting background update for {npcs.Count} NPCs.");
                foreach (var npc in npcs)
                {
                    var data = NPCData.Load(npc.uuid);
                    if (data == null)
                    {
                        data = NPCData.CreateDefault(npc.GetPrettyName());
                        NPCData.Save(npc.uuid, data);
                    }
                    if (data != null)
                    {
                        Debug.Log($"[AIROG_NPCExpansion] Updating scenario for {npc.GetPrettyName()}...");
                        bool success = await NPCGenerator.UpdateScenario(npc, data, context);
                        Debug.Log($"[AIROG_NPCExpansion] Update success for {npc.GetPrettyName()}: {success}");
                    }
                }
                Debug.Log("[AIROG_NPCExpansion] ScenarioUpdater finished update task.");
                // Runs after the turn tick returned, so it needs its own flush — refreshed
                // goals and thoughts are the highest-value context this mod produces.
                NPCData.FlushSessionLore();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Error in ScenarioUpdater: {ex}");
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }
}
