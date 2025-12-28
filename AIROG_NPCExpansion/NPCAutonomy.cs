using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks; // Added for async/await
using UnityEngine;

namespace AIROG_NPCExpansion
{
    public static class NPCAutonomy
    {
        public static async Task Process(GameCharacter npc, GameplayManager manager) // Changed to async Task
        {
            if (npc == null || manager == null) return;
            var data = NPCData.Load(npc.uuid);
            if (data == null) return;

            if (data.AllowAutoEquip) 
                AutoEquip(npc, data, manager);
            
            if (data.AllowSelfPreservation)
                SelfPreservation(npc, data, manager);

            if (data.AllowEconomicActivity)
                EconomicActivity(npc, data, manager);

            if (data.AllowWorldInteraction)
                await WorldInteraction(npc, data, manager); // Awaited WorldInteraction
        }

        private static void AutoEquip(GameCharacter npc, NPCData data, GameplayManager manager)
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
                var bestItem = FindBestItemForSlot(npc, slot, assignedUuids, manager);
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

        private static GameItem FindBestItemForSlot(GameCharacter npc, string slot, HashSet<string> assignedUuids, GameplayManager manager)
        {
            GameItem best = null;
            double bestPower = -1;

            foreach (var item in npc.items)
            {
                if (assignedUuids.Contains(item.uuid)) continue;
                if (!IsItemValidForSlot(item, slot)) continue;

                double power = CalculateItemPower(npc, item, manager);
                if (power > bestPower)
                {
                    bestPower = power;
                    best = item;
                }
            }

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

        private static double CalculateItemPower(GameCharacter npc, GameItem item, GameplayManager manager)
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

            return power;
        }

        private static void SelfPreservation(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (npc.health < npc.GetMaxHealth() * 0.5f)
            {
                var healingItem = npc.items.FirstOrDefault(i => i.IsConsumable() && (i.equipmentType == EquipmentPanel.EquipmentType.CONSUMABLE_HEALING || i.consumableSurvivalBarId == "health"));
                if (healingItem != null)
                {
                    Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()} is using {healingItem.GetPrettyName()} for self-preservation.");
                    
                    long healAmount = Utils.GetItemHealAmount(new CauseOfEvent(healingItem), npc.level, npc.GetMaxHealth());
                    npc.health = Math.Min(npc.maxHealth, npc.health + healAmount);
                    npc.items.Remove(healingItem);
                    
                    manager.gameLogView.LogText(GameLogView.AiDecision($"{npc.GetPrettyName()} uses {healingItem.GetPrettyName()} to heal wounds."));
                }
            }
        }

        private static void EconomicActivity(GameCharacter npc, NPCData data, GameplayManager manager)
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
        private static async Task WorldInteraction(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (manager.currentPlace == null) return;
            if (npc.items == null) npc.items = new List<GameItem>();

            // 1. Look for items to pick up (Smarter logic)
            if (npc.items.Count < 20)
            {
                var candidates = new List<(ThingGameEntity source, GameItem item, bool isFromStorage)>();

                foreach (var thing in manager.currentPlace.things)
                {
                    if (thing is StorageThingGameEntity storage && storage.items.Count > 0)
                    {
                        foreach (var stItem in storage.items)
                        {
                            candidates.Add((thing, stItem, true));
                        }
                    }
                    else if (thing.storedItemInfo != null)
                    {
                        // We partially hydrate to evaluate
                        GameItem tempItem = (GameItem)thing.storedItemInfo.GetPartiallyHydrated(manager);
                        candidates.Add((thing, tempItem, false));
                    }
                    else if (IsLikelyLooseItem(thing.GetPrettyName()))
                    {
                        // Create a temporary item for evaluation (not yet in global map)
                        GameItem tempItem = await GameItem.Create(thing.GetPrettyName(), thing.description, manager, npc.level, 0, GameItem.ItemQuality.COMMON, true);
                        candidates.Add((thing, tempItem, false));
                    }
                }

                if (candidates.Count > 0)
                {
                    // Sort by power and pick best
                    var winner = candidates
                        .OrderByDescending(c => CalculateItemPower(npc, c.item, manager))
                        .First();

                    GameItem itemToPick = winner.item;
                    
                    if (winner.isFromStorage)
                    {
                        ((StorageThingGameEntity)winner.source).items.Remove(itemToPick);
                    }
                    else if (winner.source.storedItemInfo != null)
                    {
                        winner.source.storedItemInfo = null;
                        // Ensure it's in the global map since GetPartiallyHydrated doesn't add it
                        if (!SS.I.uuidToGameEntityMap.ContainsKey(itemToPick.uuid))
                        {
                            SS.I.uuidToGameEntityMap[itemToPick.uuid] = itemToPick;
                        }
                    }
                    else
                    {
                        // Loose thing converted to item
                        manager.currentPlace.things.Remove(winner.source);
                        // Register item and its abilities properly
                        if (!SS.I.uuidToGameEntityMap.ContainsKey(itemToPick.uuid))
                        {
                            SS.I.uuidToGameEntityMap[itemToPick.uuid] = itemToPick;
                            manager.addToTopOfImgGenActionStack(itemToPick);
                            foreach (var abil in itemToPick.abilities)
                            {
                                 if (!SS.I.uuidToGameEntityMap.ContainsKey(abil.uuid))
                                     SS.I.uuidToGameEntityMap[abil.uuid] = abil;
                            }
                        }
                    }

                    npc.items.Add(itemToPick);
                    itemToPick.parentEnt = npc; // For serialization
                    itemToPick.SetParentEnt(npc); // For entity logic

                    string logMsg = $"{npc.GetPrettyName()} picks up {itemToPick.GetPrettyName()} from {winner.source.GetPrettyName()}.";
                    manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
                    Debug.Log($"[NPCAutonomy] {logMsg}");
                    return; // Interaction spent
                }
            }

            // 2. Interact with other things (Plausibility Check)
            // We want interaction nearly every turn IF something plausible exists.
            var intCandidates = manager.currentPlace.things
                .Select(t => new { Thing = t, Plaus = GetInteractionPlausibility(npc, data, t) })
                .Where(x => x.Plaus.isPlausible)
                .OrderByDescending(x => x.Plaus.score)
                .ThenBy(x => UnityEngine.Random.value)
                .ToList();

            if (intCandidates.Count > 0)
            {
                var best = intCandidates[0];
                string verb = best.Plaus.verb;
                string logMsg = $"{npc.GetPrettyName()} {verb} {best.Thing.GetPrettyName()}.";
                manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
                Debug.Log($"[NPCAutonomy] {logMsg}");
            }
        }

        private struct PlausResult
        {
            public bool isPlausible;
            public string verb;
            public float score;
        }

        private static PlausResult GetInteractionPlausibility(GameCharacter npc, NPCData data, ThingGameEntity thing)
        {
            if (thing == null) return new PlausResult { isPlausible = false };

            // System objects are never plausible
            string thingNameL = thing.GetPrettyName().ToLowerInvariant();
            if (thingNameL.Contains("spawner") || thingNameL.Contains("trigger") || thingNameL.Contains("logic") || thingNameL.Contains("teleport")) 
                return new PlausResult { isPlausible = false };

            if (thing.IsBarrier() || thing.isTrap) return new PlausResult { isPlausible = false };

            // Default verb
            string verb = "examines";
            float score = 1.0f;

            // Use NPC tags for logic
            bool isBeast = data.Tags != null && data.Tags.Any(t => t.ToLower().Contains("beast") || t.ToLower().Contains("animal") || t.ToLower().Contains("spider") || t.ToLower().Contains("monster"));
            bool isHumanoid = data.Tags != null && data.Tags.Any(t => t.ToLower().Contains("human") || t.ToLower().Contains("person") || t.ToLower().Contains("humanoid") || t.ToLower().Contains("civilized"));
            bool isPious = data.InteractionTraits != null && data.InteractionTraits.Any(t => t.ToLower().Contains("pious") || t.ToLower().Contains("religious") || t.ToLower().Contains("holy"));
            bool isCurious = data.InteractionTraits != null && data.InteractionTraits.Any(t => t.ToLower().Contains("curious") || t.ToLower().Contains("investigative"));

            string thingDesc = (thing.GetPotentiallyNullDescription() ?? "").ToLowerInvariant();
            string thingName = thing.GetPrettyName().ToLowerInvariant();

            // Example specific plausibility rules
            if (thingName.Contains("altar") || thingName.Contains("shrine") || thingName.Contains("statue of a deity"))
            {
                if (isBeast) return new PlausResult { isPlausible = false }; // "some random giant spider wouldn't care about an altar"
                if (isPious) { verb = "prays at"; score = 5.0f; }
                else if (isHumanoid) { verb = "ponderously looks at"; score = 2.0f; }
            }

            if (thingName.Contains("chest") || thingName.Contains("box") || thingName.Contains("barrel") || thingName.Contains("crate"))
            {
                if (isBeast) { verb = "sniffs at"; score = 2.0f; }
                else if (isHumanoid) { verb = "searches through"; score = 4.0f; }
            }

            if (thingName.Contains("book") || thingName.Contains("scroll") || thingName.Contains("tome") || thingName.Contains("shelf"))
            {
                if (isBeast) { verb = "confusedly paws at"; score = 1.0f; }
                else if (isHumanoid) { verb = "reads from"; score = 3.0f; }
            }
            
            if (thingName.Contains("fountain") || thingName.Contains("well") || thingName.Contains("stream") || thingName.Contains("pool"))
            {
                if (isBeast) { verb = "drinks from"; score = 4.0f; }
                else { verb = "washes hands in"; score = 2.0f; }
            }

            if (thingName.Contains("fire") || thingName.Contains("hearth") || thingName.Contains("campfire"))
            {
                if (isBeast) { verb = "warily circles"; score = 2.0f; }
                else { verb = "warms themselves by"; score = 4.0f; }
            }

            // High curiosity increases score
            if (isCurious) score *= 1.5f;

            return new PlausResult { isPlausible = true, verb = verb, score = score };
        }

        private static bool IsLikelyLooseItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("machete") || n.Contains("sword") || n.Contains("shield") || n.Contains("armor") || 
                   n.Contains("potion") || n.Contains("book") || n.Contains("dagger") || n.Contains("staff") || 
                   n.Contains("bow") || n.Contains("helmet") || n.Contains("boots") || n.Contains("gloves") || 
                   n.Contains("ring") || n.Contains("amulet") || n.Contains("scroll") || n.Contains("gem") ||
                   n.Contains("herb") || n.Contains("mushroom") || n.Contains("meat") || n.Contains("bread");
        }

        private static bool IsPlausibleToExamine(ThingGameEntity thing)
        {
            // This is now redundant but kept for any external references if needed, 
            // though we've integrated it into the new logic.
            return GetInteractionPlausibility(null, new NPCData(), thing).isPlausible;
        }

    }
}
