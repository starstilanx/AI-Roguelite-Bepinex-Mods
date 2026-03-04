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

            // Auto-migrate: Use Scenario as Goal if Goal is missing (for existing saves)
            if (string.IsNullOrEmpty(data.CurrentGoal) && !string.IsNullOrEmpty(data.Scenario))
            {
                data.CurrentGoal = data.Scenario;
                NPCData.Save(npc.uuid, data);
            }

            // Pursue Narrative Goal
            if (!string.IsNullOrEmpty(data.CurrentGoal))
                await PursueGoal(npc, data, manager);

            await PerformAbility(npc, data, manager);

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

            // --- ROLE SUITABILITY BONUS ---
            NPCData data = NPCData.Load(npc.uuid);
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

        private static HashSet<string> GetRoleKeywords(GameCharacter npc, NPCData data)
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
                    
                    _ = manager.gameLogView.LogText(GameLogView.AiDecision($"{npc.GetPrettyName()} uses {healingItem.GetPrettyName()} to heal wounds."));
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

            // --- ENEMY HOSTILITY EARLY BAIL-OUT ---
            // Hostile enemies should NOT be casually examining objects when the player is present.
            // They should be focused on combat, not admiring art or warming by fires.
            bool isHostile = npc.IsEnemyType() || npc.sentimentV2 <= -2.0f; // Scorned or worse
            bool playerIsInSamePlace = manager.playerCharacter != null && manager.currentPlace != null;
            
            if (isHostile && playerIsInSamePlace)
            {
                // Hostile enemies only do combat-relevant actions, skip all world interactions
                Debug.Log($"[NPCAutonomy] Skipping world interaction for hostile {npc.GetPrettyName()} - player present");
                return;
            }

            // --- 0. COMPANION ANTI-LOOT CHECK ---
            // If NPC is a follower, they should NOT auto-loot containers or loose items.
            // They wait for the player to distribute equipment.
            bool isFollower = false;
            if (manager.playerCharacter != null && manager.playerCharacter.pcGameEntity != null && manager.playerCharacter.pcGameEntity.followers != null)
            {
                 isFollower = manager.playerCharacter.pcGameEntity.followers.Contains(npc);
            }

            // 1. Look for items to pick up (Smarter logic)
            // Skip looting for followers
            if (!isFollower && npc.items.Count < 20)
            {
                var candidates = new List<(ThingGameEntity source, GameItem item, bool isFromStorage)>();

                // Ensure things collection is not null
                var things = manager.currentPlace.things;
                if (things == null) things = new List<ThingGameEntity>();

                foreach (var thing in things)
                {
                    if (thing is StorageThingGameEntity storage && storage.items != null && storage.items.Count > 0)
                    {
                        foreach (var stItem in storage.items)
                        {
                            if (stItem != null) candidates.Add((thing, stItem, true));
                        }
                    }
                    else if (thing.storedItemInfo != null)
                    {
                        // We partially hydrate to evaluate
                        try
                        {
                            GameItem tempItem = (GameItem)thing.storedItemInfo.GetPartiallyHydrated(manager);
                            if (tempItem != null) candidates.Add((thing, tempItem, false));
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[NPCAutonomy] Failed to hydrate storedItemInfo: {ex.Message}");
                        }
                    }
                    else if (IsLikelyLooseItem(thing.GetPrettyName()))
                    {
                        // Create a temporary item for evaluation (not yet in global map)
                        try
                        {
                            GameItem tempItem = await GameItem.Create(thing.GetPrettyName(), thing.description, manager, npc.level, 0, GameItem.ItemQuality.COMMON, true);
                            if (tempItem != null) candidates.Add((thing, tempItem, false));
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[NPCAutonomy] Failed to create temp item: {ex.Message}");
                        }
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
                        // Ensure it's in the global map
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
                    _ = manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
                    Debug.Log($"[NPCAutonomy] {logMsg}");
                    return; // Interaction spent
                }
            }

            // --- 2. IDLE CHANCE / FLAVOR BEHAVIOR ---
            // 50% chance to do NOTHING (or a flavor idle) instead of examining an object.
            // This prevents "machine gun" interactions every turn.
            if (UnityEngine.Random.value < 0.5f)
            {
                 // Optional: Very small chance to bark a thought
                 if (UnityEngine.Random.value < 0.05f) // 5% chance of idle thought
                 {
                     PerformIdleBark(npc, data, manager);
                 }
                 return;
            }

            // --- 3. INTERACT WITH THINGS ---
            // Use manager for combat checks
            var intCandidates = manager.currentPlace.things
                .Select(t => new { Thing = t, Plaus = GetInteractionPlausibility(npc, data, t, manager) })
                .Where(x => x.Plaus.isPlausible)
                // Add JITTER to score (0.8x to 1.2x) to prevent all NPCs swarming the same "best" object
                .OrderByDescending(x => x.Plaus.score * UnityEngine.Random.Range(0.8f, 1.2f)) 
                .ToList();

            if (intCandidates.Count > 0)
            {
                var best = intCandidates[0];
                string verb = best.Plaus.verb;
                string logMsg = $"{npc.GetPrettyName()} {verb} {best.Thing.GetPrettyName()}.";
                _ = manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
                Debug.Log($"[NPCAutonomy] {logMsg}");
            }
        }

        private static void PerformIdleBark(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            // Simple flavor thoughts based on traits
            string msg = "looks around.";
            if (data.InteractionTraits.Any(t => t.ToLower().Contains("paranoid"))) msg = "glances nervously over their shoulder.";
            else if (data.InteractionTraits.Any(t => t.ToLower().Contains("curious"))) msg = "inspects their surroundings closely.";
            else if (data.InteractionTraits.Any(t => t.ToLower().Contains("lazy"))) msg = "yawns.";
            else if (data.InteractionTraits.Any(t => t.ToLower().Contains("aggressive"))) msg = "clenches their fist.";

            string logMsg = $"{npc.GetPrettyName()} {msg}";
            _ = manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
        }

        private struct PlausResult
        {
            public bool isPlausible;
            public string verb;
            public float score;
        }

        private static PlausResult GetInteractionPlausibility(GameCharacter npc, NPCData data, ThingGameEntity thing, GameplayManager manager = null)
        {
            if (thing == null) return new PlausResult { isPlausible = false };

            // Combat Check
            bool inCombat = manager != null && manager.uiEncounter != null && manager.uiEncounter.IsEncounterActive();

            // System objects are never plausible
            string thingNameL = thing.GetPrettyName().ToLowerInvariant();
            if (thingNameL.Contains("spawner") || thingNameL.Contains("trigger") || thingNameL.Contains("logic") || thingNameL.Contains("teleport")) 
                return new PlausResult { isPlausible = false };

            if (thing.IsBarrier() || thing.isTrap) return new PlausResult { isPlausible = false };

            // Default verb
            string verb = "examines";
            float score = 1.0f;

            // --- HOSTILITY CHECK ---
            // If NPC is hostile and player is present, they shouldn't do "relaxed" things like sitting or warming hands.
            bool isHostile = npc.IsEnemyType() || npc.sentimentV2 <= -2.0f; // Scorned or worse
            bool playerIsHere = (manager != null && manager.currentPlace == npc.parentPlace);

            // Use NPC tags for logic
            bool isBeast = data.Tags != null && data.Tags.Any(t => t.ToLower().Contains("beast") || t.ToLower().Contains("animal") || t.ToLower().Contains("spider") || t.ToLower().Contains("monster"));
            bool isHumanoid = data.Tags != null && data.Tags.Any(t => t.ToLower().Contains("human") || t.ToLower().Contains("person") || t.ToLower().Contains("humanoid") || t.ToLower().Contains("civilized"));
            bool isPious = data.InteractionTraits != null && data.InteractionTraits.Any(t => t.ToLower().Contains("pious") || t.ToLower().Contains("religious") || t.ToLower().Contains("holy"));
            bool isCurious = data.InteractionTraits != null && data.InteractionTraits.Any(t => t.ToLower().Contains("curious") || t.ToLower().Contains("investigative"));
            
            // New Role Logic
            var roles = GetRoleKeywords(npc, data);
            bool isMagicUser = roles.Contains("mage") || roles.Contains("wizard") || roles.Contains("sorcerer") || roles.Contains("warlock") || roles.Contains("priest") || roles.Contains("cleric");
            bool isWarrior = roles.Contains("warrior") || roles.Contains("fighter") || roles.Contains("soldier") || roles.Contains("guard") || roles.Contains("mercenary");

            string thingDesc = (thing.GetPotentiallyNullDescription() ?? "").ToLowerInvariant();
            string thingName = thing.GetPrettyName().ToLowerInvariant();

            // --- SPECIFIC OBJECT RULES ---

            if (thingName.Contains("altar") || thingName.Contains("shrine") || thingName.Contains("statue of a deity"))
            {
                if (inCombat) return new PlausResult { isPlausible = false }; 
                if (isBeast) return new PlausResult { isPlausible = false }; 
                if (isPious) { verb = "prays at"; score = 5.0f; }
                else if (isMagicUser || isHumanoid) { verb = "inspects the runes on"; score = 2.0f; }
            }

            if (thingName.Contains("chest") || thingName.Contains("box") || thingName.Contains("barrel") || thingName.Contains("crate"))
            {
                if (inCombat) return new PlausResult { isPlausible = false };
                if (isBeast) { verb = "sniffs at"; score = 2.0f; }
                else if (isHumanoid) { verb = "carefully checks"; score = 4.0f; }
            }

            if (thingName.Contains("book") || thingName.Contains("scroll") || thingName.Contains("tome") || thingName.Contains("shelf"))
            {
                if (inCombat) return new PlausResult { isPlausible = false };
                if (isBeast) { verb = "confusedly paws at"; score = 1.0f; }
                else if (isMagicUser) { verb = "intently studies"; score = 10.0f; } // Huge bonus for mages
                else if (isHumanoid) { verb = "reads from"; score = 3.0f; }
            }
            
            if (thingName.Contains("fountain") || thingName.Contains("well") || thingName.Contains("stream") || thingName.Contains("pool"))
            {
                if (isBeast) { verb = "drinks from"; score = 4.0f; }
                else { verb = "washes hands in"; score = 2.0f; }
            }

            if (thingName.Contains("fire") || thingName.Contains("hearth") || thingName.Contains("campfire"))
            {
                if (inCombat) return new PlausResult { isPlausible = false };
                
                // HOSTILITY CHECK: Enemies won't cozy up to the fire if player is there
                if (isHostile && playerIsHere) return new PlausResult { isPlausible = false };

                if (isBeast) { verb = "warily circles"; score = 2.0f; }
                else { verb = "warms themselves by"; score = 4.0f; }
            }

            if (thingName.Contains("bed") || thingName.Contains("chair") || thingName.Contains("bench") || thingName.Contains("throne"))
            {
                 if (inCombat) return new PlausResult { isPlausible = false };
                 
                 // HOSTILITY CHECK: Enemies won't sit/sleep knowing player is there
                 if (isHostile && playerIsHere) return new PlausResult { isPlausible = false };

                 if (isBeast) return new PlausResult { isPlausible = false };
                 
                 if (isWarrior && thingName.Contains("throne")) { verb = "boldly sits upon"; score = 3.0f; }
                 else { verb = "rests on"; score = 3.0f; }
            }

            if (thingName.Contains("dummy") || thingName.Contains("target") || thingName.Contains("rack"))
            {
                if (inCombat) return new PlausResult { isPlausible = false };
                if (isWarrior) { verb = "practices strikes on"; score = 8.0f; } // Huge bonus for warriors
            }

            // High curiosity increases score
            if (isCurious) score *= 1.5f;

            return new PlausResult { isPlausible = true, verb = verb, score = score };
        }

        private static bool IsLikelyLooseItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            
            // Exclude furniture/static objects explicitly
            if (n.Contains("rack") || n.Contains("shelf") || n.Contains("cabinet") || n.Contains("chest") || 
                n.Contains("table") || n.Contains("altar") || n.Contains("stand") || n.Contains("pedestal"))
                return false;

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

        private static async Task PursueGoal(GameCharacter npc, NPCData data, GameplayManager manager)
        {
             // 10% chance to act on goal per turn, OR immediately if we have no thoughts yet (for UI)
             bool forceThink = (data.RecentThoughts == null || data.RecentThoughts.Count == 0);
             if (forceThink || UnityEngine.Random.value < 0.1f) 
             {
                 string progress = string.IsNullOrEmpty(data.GoalProgress) ? "starting" : data.GoalProgress;
                 string thought = $"Thinking about goal: {data.CurrentGoal} ({progress}).";
                 Debug.Log($"[NPCAutonomy] {npc.GetPrettyName()}: {thought}");
                 
                 if (data.RecentThoughts == null) data.RecentThoughts = new List<string>();
                 
                 // Fix duplicate spam: Don't add if it's identical to the last one
                 if (data.RecentThoughts.Count == 0 || data.RecentThoughts[0] != thought)
                 {
                     data.RecentThoughts.Insert(0, thought);
                     if (data.RecentThoughts.Count > 5) data.RecentThoughts.RemoveAt(data.RecentThoughts.Count - 1);
                 }
                 
                 NPCData.Save(npc.uuid, data);
             }
        }

        private static async Task PerformAbility(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            // 5% chance to perform an ability if one exists
            if (UnityEngine.Random.value > 0.05f) return;

            // Use DetailedAbilities directly to avoid the allocation overhead of the Abilities getter shim
            if (data.DetailedAbilities.Count == 0) return;

            var abil = data.DetailedAbilities[UnityEngine.Random.Range(0, data.DetailedAbilities.Count)];

            string logMsg = $"{npc.GetPrettyName()} uses {abil.Name}! ({abil.Description})";
            _ = manager.gameLogView.LogText(GameLogView.AiDecision(logMsg));
            Debug.Log($"[NPCAutonomy] {logMsg}");
        }

    }
}
