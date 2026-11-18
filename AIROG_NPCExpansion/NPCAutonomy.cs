using System.Threading.Tasks;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Per-turn autonomy orchestrator for a single NPC. The actual systems live in
    /// NPCEquipmentAutonomy, NPCSelfPreservationAutonomy, NPCEconomicAutonomy,
    /// NPCWorldInteractionAutonomy, and NPCGoalAutonomy — this class is just the
    /// dispatch order, since NPCExpansionPlugin/NPCEquipmentUI/ScenarioUpdater all
    /// call NPCAutonomy.Process directly.
    /// </summary>
    public static class NPCAutonomy
    {
        public static async Task Process(GameCharacter npc, GameplayManager manager)
        {
            // Always started fire-and-forget: without this an exception just vanishes with the task.
            try
            {
                await ProcessCore(npc, manager);
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[NPCAutonomy] Autonomy step failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }

        private static async Task ProcessCore(GameCharacter npc, GameplayManager manager)
        {
            if (npc == null || manager == null) return;
            var data = NPCData.Load(npc.uuid);
            if (data == null) return;

            if (data.AllowAutoEquip)
            {
                bool hadGear = data.EquippedUuids.Count > 0;
                NPCEquipmentAutonomy.AutoEquip(npc, data, manager);
                // Reputation: if just started equipping combat gear, earn a rep tag
                if (!hadGear && data.EquippedUuids.ContainsKey("WEAPON1"))
                    _ = NPCReputationSystem.AddReputationFromAction(npc, data, "equipped combat gear for the first time");
            }

            if (data.AllowSelfPreservation)
            {
                bool wasLowHealth = npc.GetHealth() < npc.GetMaxHealth() * 0.5f;
                NPCSelfPreservationAutonomy.SelfPreservation(npc, data, manager);
                if (wasLowHealth && npc.GetHealth() >= npc.GetMaxHealth() * 0.5f)
                    _ = NPCReputationSystem.AddReputationFromAction(npc, data, "healed themselves when nearly dead");
            }

            if (data.AllowEconomicActivity)
            {
                int itemsBefore = npc.items?.Count ?? 0;
                NPCEconomicAutonomy.EconomicActivity(npc, data);
                int itemsAfter = npc.items?.Count ?? 0;
                if (itemsAfter < itemsBefore)
                    _ = NPCReputationSystem.AddReputationFromAction(npc, data, "sold surplus goods to make a profit");
            }

            // No goal yet: start from what they are currently doing. (This read Scenario while that
            // field held the activity; Scenario is the background now and makes no sense as a goal.)
            if (string.IsNullOrEmpty(data.CurrentGoal) && !string.IsNullOrEmpty(data.CurrentActivity))
            {
                data.CurrentGoal = data.CurrentActivity;
                NPCData.Save(npc.uuid, data);
            }

            // Pursue Narrative Goal
            if (!string.IsNullOrEmpty(data.CurrentGoal))
                NPCGoalAutonomy.PursueGoal(npc, data);

            NPCGoalAutonomy.PerformAbility(npc, data, manager);

            if (data.AllowWorldInteraction)
                await NPCWorldInteractionAutonomy.WorldInteraction(npc, data, manager);
        }
    }
}
