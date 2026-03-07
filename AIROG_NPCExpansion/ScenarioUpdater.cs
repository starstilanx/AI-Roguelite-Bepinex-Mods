using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    public static class ScenarioUpdater
    {
        // ─── Counters ──────────────────────────────────────────────────────────────
        private static int _globalTurn        = 0;
        private static int _turnCounter       = 0;
        private static int _autonomyCounter   = 0;
        private static int _barkCounter       = 0;
        private static int _rumorCounter      = 0;
        private static int _memoryCounter     = 0;

        private const int TURNS_PER_UPDATE          = 1;
        private const int AUTONOMY_TURNS_PER_UPDATE = 3;
        private const int BARK_INTERVAL             = 5;
        private const int RUMOR_INTERVAL            = 3;
        private const int MEMORY_INTERVAL           = 10;

        private static bool _isUpdating = false;

        /// <summary>Current global turn count. Used by QuestManager and new systems.</summary>
        public static int GlobalTurn => _globalTurn;

        // ─── Main Hook ─────────────────────────────────────────────────────────────
        public static void OnTurnHappened(int numTurns, long secs)
        {
            _globalTurn      += numTurns;
            _turnCounter     += numTurns;
            _autonomyCounter += numTurns;
            _barkCounter     += numTurns;
            _rumorCounter    += numTurns;
            _memoryCounter   += numTurns;

            Debug.Log($"[AIROG_NPCExpansion] OnTurnHappened: {numTurns} turns (global={_globalTurn}). " +
                      $"Scenario={_turnCounter}/{TURNS_PER_UPDATE}, Auto={_autonomyCounter}/{AUTONOMY_TURNS_PER_UPDATE}, " +
                      $"Bark={_barkCounter}/{BARK_INTERVAL}, Rumor={_rumorCounter}/{RUMOR_INTERVAL}, " +
                      $"Mem={_memoryCounter}/{MEMORY_INTERVAL}");

            var manager = GameObject.FindObjectOfType<GameplayManager>();
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

            // ── Scenario update (every 1 turn) ────────────────────────────────────
            if (_turnCounter >= TURNS_PER_UPDATE)
            {
                _turnCounter = 0;
                if (nearbyNpcs == null || nearbyNpcs.Count == 0)
                {
                    Debug.Log("[AIROG_NPCExpansion] No nearby NPCs to update.");
                    return;
                }
                string context = manager.GetContextForQuickActions();
                Debug.Log($"[AIROG_NPCExpansion] Triggering scenario update for {nearbyNpcs.Count} NPCs.");
                _ = UpdateNpcsTask(nearbyNpcs, context);
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

            // ── Rumor propagation (every 3 turns) ─────────────────────────────────
            if (_rumorCounter >= RUMOR_INTERVAL)
            {
                _rumorCounter = 0;
                if (nearbyNpcs != null) RumorNetwork.PropagateInPlace(nearbyNpcs);
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
