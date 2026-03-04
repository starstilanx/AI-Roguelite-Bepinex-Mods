using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    public static class ScenarioUpdater
    {
        private static int _turnCounter = 0;
        private static int _autonomyCounter = 0;
        private const int TURNS_PER_UPDATE = 1;           // How often to run expensive AI scenario updates
        private const int AUTONOMY_TURNS_PER_UPDATE = 3;  // How often to run NPC autonomy (equip, goal pursuit, world interaction)
        private static bool _isUpdating = false;

        public static void OnTurnHappened(int numTurns, long secs)
        {
            _turnCounter += numTurns;
            _autonomyCounter += numTurns;
            Debug.Log($"[AIROG_NPCExpansion] OnTurnHappened: {numTurns} turns. ScenarioCounter: {_turnCounter}/{TURNS_PER_UPDATE}, AutonomyCounter: {_autonomyCounter}/{AUTONOMY_TURNS_PER_UPDATE}");
            
            var manager = GameObject.FindObjectOfType<GameplayManager>();
            if (manager == null || manager.currentPlace == null) return;

            var nearbyNpcs = manager.GetCharsForNpcConvoSelectorDropdown()?.Where(c => c != null && c.corpseState == GameCharacter.CorpseState.NONE).ToList();

            // Run autonomy (equip decisions, goal pursuit, world interaction) on a separate, throttled cadence
            // to avoid launching expensive async AI calls on every single turn.
            if (_autonomyCounter >= AUTONOMY_TURNS_PER_UPDATE)
            {
                _autonomyCounter = 0;
                if (nearbyNpcs != null)
                {
                    foreach (var npc in nearbyNpcs)
                    {
                        _ = NPCAutonomy.Process(npc, manager);
                    }
                }
            }

            if (_turnCounter >= TURNS_PER_UPDATE)
            {
                _turnCounter = 0;
                
                if (nearbyNpcs == null || nearbyNpcs.Count == 0) 
                {
                    Debug.Log("[AIROG_NPCExpansion] No nearby NPCs to update.");
                    return;
                }

                string context = manager.GetContextForQuickActions();
                Debug.Log($"[AIROG_NPCExpansion] Triggering scenario update for {nearbyNpcs.Count} NPCs. Context length: {context.Length}");

                // Run update asynchronously without blocking the main thread
                _ = UpdateNpcsTask(nearbyNpcs, context);
            }
        }

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
                        Debug.Log($"[AIROG_NPCExpansion] Updating scenario for {npc.GetPrettyName()} (UUID: {npc.uuid})...");
                        bool success = await NPCGenerator.UpdateScenario(npc, data, context);
                        Debug.Log($"[AIROG_NPCExpansion] Update success for {npc.GetPrettyName()}: {success}");
                    }
                    else
                    {
                        // Debug.Log($"[AIROG_NPCExpansion] No data for {npc.GetPrettyName()}, skipping scenario update.");
                    }
                }
                Debug.Log($"[AIROG_NPCExpansion] ScenarioUpdater finished update task.");
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
