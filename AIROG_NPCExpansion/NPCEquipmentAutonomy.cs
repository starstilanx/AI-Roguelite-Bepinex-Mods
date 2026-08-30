using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>Auto-equip logic and item-power/role-suitability scoring, used both for
    /// equipping NPCs and (via CalculateItemPower/GetRoleKeywords) for evaluating loose
    /// items to pick up in NPCWorldInteractionAutonomy.</summary>
    internal static class NPCEquipmentAutonomy
    {
        public static void AutoEquip(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (npc.items == null) npc.items = new List<GameItem>();

            bool changed = false;
            // Ordered by importance
            string[] slots = { "WEAPON1", "WEAPON2", "HEAD", "TORSO", "PANTS", "BOOTS", "GLOVES", "FACE", "NECKLACE", "RING" };

            // 1. Cleanup: Remove entries for items no longer in inventory
            var currentItemUuids = new HashSet<string>(npc.items.Select(i => i.uuid));
            var slotsToRemove = new List<string>();
            foreach (var kvp in data.EquippedUuids)
            {
                if (!currentItemUuids.Contains(kvp.Value))
                {
                    slotsToRemove.Add(kvp.Key);
                }
            }
            foreach (var s in slotsToRemove)
            {
                data.EquippedUuids.Remove(s);
                changed = true;
                Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()} unequipped missing item from {s}");
            }

            // 2. Assign best items
            HashSet<string> assignedUuids = new HashSet<string>();
            foreach (var slot in slots)
            {
                var bestItem = FindBestItemForSlot(npc, data, slot, assignedUuids, manager);
                if (bestItem != null)
                {
                    assignedUuids.Add(bestItem.uuid);
                    if (!data.EquippedUuids.TryGetValue(slot, out string currentUuid) || currentUuid != bestItem.uuid)
                    {
                        data.EquippedUuids[slot] = bestItem.uuid;
                        changed = true;
                        Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()} equipped {bestItem.GetPrettyName()} in {slot}");
                    }
                }
                else
                {
                    if (data.EquippedUuids.ContainsKey(slot))
                    {
                        data.EquippedUuids.Remove(slot);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                NPCData.Save(npc.uuid, data);
            }
        }

        private static GameItem FindBestItemForSlot(GameCharacter npc, NPCData data, string slot, HashSet<string> assignedUuids, GameplayManager manager)
        {
            GameItem best = null;
            double bestPower = -1;

            foreach (var item in npc.items)
            {
                if (assignedUuids.Contains(item.uuid)) continue;
                if (!IsItemValidForSlot(item, slot)) continue;

                double power = CalculateItemPower(npc, data, item, manager);
                // Debug.Log($"[NPCAutonomy] Checking {item.GetPrettyName()} for {slot}: Power {power}"); // VERBOSE

                if (power > bestPower)
                {
                    bestPower = power;
                    best = item;
                }
            }

            if (best != null) Debug.Log($"[NPCAutonomy] Best for {slot} is {best.GetPrettyName()} (Pow: {bestPower})");

            return best;
        }

        private static bool IsItemValidForSlot(GameItem item, string slot)
        {
            switch (slot)
            {
                case "HEAD": return item.equipmentType == EquipmentPanel.EquipmentType.HEAD;
                case "TORSO": return item.equipmentType == EquipmentPanel.EquipmentType.TORSO;
                case "GLOVES": return item.equipmentType == EquipmentPanel.EquipmentType.GLOVES;
                case "BOOTS": return item.equipmentType == EquipmentPanel.EquipmentType.BOOTS;
                case "FACE": return item.equipmentType == EquipmentPanel.EquipmentType.FACE;
                case "NECKLACE": return item.equipmentType == EquipmentPanel.EquipmentType.NECKLACE;
                case "RING": return item.equipmentType == EquipmentPanel.EquipmentType.RING;
                case "PANTS": return item.equipmentType == EquipmentPanel.EquipmentType.PANTS;
                case "WEAPON1":
                case "WEAPON2":
                    return item.equipmentType == EquipmentPanel.EquipmentType.WIELDABLE || Utils.IsWeapon(item);
            }
            return false;
        }

        public static double CalculateItemPower(GameCharacter npc, NPCData data, GameItem item, GameplayManager manager)
        {
            double qualityMult = 1.0;
            switch (item.itemQuality)
            {
                case GameItem.ItemQuality.COMMON: qualityMult = 1.0; break;
                case GameItem.ItemQuality.UNCOMMON: qualityMult = 1.25; break;
                case GameItem.ItemQuality.RARE: qualityMult = 1.5; break;
                case GameItem.ItemQuality.EPIC: qualityMult = 2.0; break;
                case GameItem.ItemQuality.LEGENDARY: qualityMult = 3.0; break;
            }

            double power = item.itemLevel * qualityMult;

            if (item.IsArmorType())
            {
                power += Utils.GetDmgProtForItem(item, npc.level) * 100;
            }
            else if (Utils.IsWeapon(item))
            {
                power += Utils.CalculatePlayerDamage(npc.level, new CauseOfEvent(item), npc.level, manager.GetDifficulty());
            }

            // --- ROLE SUITABILITY BONUS ---
            if (data != null)
            {
                float suitability = GetItemSuitability(npc, data, item);
                power *= suitability;
            }

            return power;
        }

        private static float GetItemSuitability(GameCharacter npc, NPCData data, GameItem item)
        {
            float score = 1.0f;
            var roles = GetRoleKeywords(npc, data);
            string itemName = item.GetPrettyName().ToLowerInvariant();
            string itemDesc = (item.description ?? "").ToLowerInvariant();

            bool isMagicUser = roles.Contains("mage") || roles.Contains("wizard") || roles.Contains("sorcerer") || roles.Contains("warlock") || roles.Contains("priest") || roles.Contains("cleric") || roles.Contains("enchanter") || roles.Contains("scholar");
            bool isWarrior = roles.Contains("warrior") || roles.Contains("fighter") || roles.Contains("knight") || roles.Contains("soldier") || roles.Contains("barbarian") || roles.Contains("guard") || roles.Contains("paladin");
            bool isRogue = roles.Contains("rogue") || roles.Contains("thief") || roles.Contains("assassin") || roles.Contains("ranger") || roles.Contains("hunter") || roles.Contains("scout");
            bool isBeast = roles.Contains("beast") || roles.Contains("animal") || roles.Contains("monster");

            // --- MAGIC ITEM LOGIC ---
            if (itemName.Contains("scroll") || itemName.Contains("book") || itemName.Contains("tome") || itemName.Contains("staff") || itemName.Contains("wand") || itemName.Contains("orb") || itemName.Contains("robe") || itemName.Contains("hat") || itemName.Contains("hood"))
            {
                if (isMagicUser) score *= 3.0f; // Strongly preferred
                else if (isWarrior) score *= 0.5f; // Disliked
                else if (isBeast) score *= 0.1f; // Useless
            }

            // --- HEAVY WEAPON/ARMOR LOGIC ---
            if (itemName.Contains("plate") || itemName.Contains("mail") || itemName.Contains("shield") || itemName.Contains("sword") || itemName.Contains("axe") || itemName.Contains("hammer") || itemName.Contains("mace") || itemName.Contains("helm"))
            {
                if (isWarrior) score *= 2.0f;
                else if (isMagicUser) score *= 0.6f;
                else if (isBeast) score *= 0.1f;
            }

            // --- ROGUE LOGIC ---
            if (itemName.Contains("dagger") || itemName.Contains("knife") || itemName.Contains("bow") || itemName.Contains("arrow") || itemName.Contains("cloak") || itemName.Contains("leather") || itemName.Contains("poison"))
            {
                if (isRogue) score *= 2.5f;
                else if (isWarrior) score *= 1.0f; // Warriors can use bows/daggers
                else if (isMagicUser) score *= 0.8f;
            }

            // --- FOOD/CONSUMABLES ---
            if (item.IsConsumable())
            {
                if (isBeast && (itemName.Contains("meat") || itemName.Contains("raw") || itemName.Contains("flesh"))) score *= 5.0f;
            }

            return score;
        }

        public static HashSet<string> GetRoleKeywords(GameCharacter npc, NPCData data)
        {
            HashSet<string> keywords = new HashSet<string>();

            // 1. Tags
            if (data.Tags != null)
            {
                foreach (var t in data.Tags) keywords.Add(t.ToLowerInvariant());
            }

            // 2. Generation Instructions (Strongest Signal)
            if (!string.IsNullOrEmpty(data.GenerationInstructions))
            {
                var parts = data.GenerationInstructions.ToLowerInvariant().Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts) keywords.Add(p);
            }

            // 3. Name/Description Fallback
            if (keywords.Count == 0)
            {
                string combo = (npc.GetPrettyName() + " " + npc.description).ToLowerInvariant();
                var parts = combo.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts) keywords.Add(p);
            }

            return keywords;
        }
    }
}
