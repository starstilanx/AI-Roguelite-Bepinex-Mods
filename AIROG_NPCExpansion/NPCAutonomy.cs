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
                bool wasLowHealth = npc.health < npc.GetMaxHealth() * 0.5f;
                NPCSelfPreservationAutonomy.SelfPreservation(npc, data, manager);
                if (wasLowHealth && npc.health >= npc.GetMaxHealth() * 0.5f)
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

            // Auto-migrate: Use Scenario as Goal if Goal is missing (for existing saves)
            if (string.IsNullOrEmpty(data.CurrentGoal) && !string.IsNullOrEmpty(data.Scenario))
            {
                data.CurrentGoal = data.Scenario;
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
