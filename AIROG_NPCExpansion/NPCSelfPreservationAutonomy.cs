using System;
using System.Linq;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    internal static class NPCSelfPreservationAutonomy
    {
        public static void SelfPreservation(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (npc.items == null) return;
            // GameCharacter is an InGameCpn: its own health field is legacy and stays 0. Real HP lives on
            // the parent InGameEntity, reached through GetHealth()/SetHealth().
            if (npc.GetHealth() < npc.GetMaxHealth() * 0.5f)
            {
                var healingItem = npc.items.FirstOrDefault(i => i.IsConsumable() && (i.equipmentType == EquipmentPanel.EquipmentType.CONSUMABLE_HEALING || i.consumableSurvivalBarId == "health"));
                if (healingItem != null)
                {
                    Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()} is using {healingItem.GetPrettyName()} for self-preservation.");

                    long healAmount = Utils.GetItemHealAmount(new CauseOfEvent(healingItem), npc.level, npc.GetMaxHealth());
                    npc.SetHealth(Math.Min(npc.GetMaxHealth(), npc.GetHealth() + healAmount));
                    npc.items.Remove(healingItem);

                    _ = manager.gameLogView.LogTextCompat(GameLogView.AiDecision($"{npc.GetPrettyName()} uses {healingItem.GetPrettyName()} to heal wounds."));
                }
            }
        }
    }
}
