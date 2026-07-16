using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    internal static class NPCEconomicAutonomy
    {
        public static void EconomicActivity(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (npc.items == null || npc.items.Count < 5) return;

            // Simple logic: If not equipped and not "best", sell for half value
            List<GameItem> itemsToSell = new List<GameItem>();
            foreach (var item in npc.items)
            {
                if (IsItemEquipped(item, data)) continue;

                // Keep some variety but sell excess
                if (item.IsConsumable()) continue;

                itemsToSell.Add(item);
            }

            // Only sell if we have a lot of items
            if (itemsToSell.Count > 3)
            {
                var toSell = itemsToSell.Take(itemsToSell.Count - 3).ToList();
                foreach (var item in toSell)
                {
                    long val = Utils.GetItemGoldVal(item);
                    npc.numGold += val / 2;
                    npc.items.Remove(item);
                    Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()} sold surplus {item.GetPrettyName()} for {val/2} gold.");
                }
            }
        }

        private static bool IsItemEquipped(GameItem item, NPCData data)
        {
            return data.EquippedUuids.Values.Contains(item.uuid);
        }
    }
}
