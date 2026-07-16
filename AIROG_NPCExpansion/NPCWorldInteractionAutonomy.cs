using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>Looting, idle flavor barks, and plausibility-scored interaction with
    /// world objects (altars, fires, workbenches, etc.) during an NPC's autonomy turn.</summary>
    internal static class NPCWorldInteractionAutonomy
    {
        public static async Task WorldInteraction(GameCharacter npc, NPCData data, GameplayManager manager)
        {
            if (manager.currentPlace == null) return;
            if (npc.items == null) npc.items = new List<GameItem>();

            // --- ENEMY HOSTILITY EARLY BAIL-OUT ---
            // Hostile enemies should NOT be casually examining objects when the player is present.
            // They should be focused on combat, not admiring art or warming by fires.
            bool isHostile = npc.IsEnemyType() || npc.sentimentV2 <= -2.0f; // Scorned or worse
            bool playerIsInSamePlace = manager.playerCharacter != null && manager.currentPlace == npc.parentPlace;

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
                var candidates = new List<(ThingGameEntity source, GameItem item)>();

                // Ensure things collection is not null
                var things = manager.currentPlace.things;
                if (things == null) things = new List<ThingGameEntity>();

                foreach (var thing in things)
                {
                    // Skip storage containers entirely — NPCs should not loot chests, racks, or other player storage
                    if (thing is StorageThingGameEntity)
                        continue;

                    if (thing.storedItemInfo != null)
                    {
                        // We partially hydrate to evaluate
                        try
                        {
                            GameItem tempItem = (GameItem)thing.storedItemInfo.GetPartiallyHydrated(manager);
                            if (tempItem != null) candidates.Add((thing, tempItem));
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
                            if (tempItem != null) candidates.Add((thing, tempItem));
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
                        .OrderByDescending(c => NPCEquipmentAutonomy.CalculateItemPower(npc, c.item, manager))
                        .First();

                    GameItem itemToPick = winner.item;

                    if (winner.source.storedItemInfo != null)
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
                            AssetGenService.I.EnqueueImgAndSprite(itemToPick);
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
                    _ = manager.gameLogView.LogTextCompat(GameLogView.AiDecision(logMsg));
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
                _ = manager.gameLogView.LogTextCompat(GameLogView.AiDecision(logMsg));
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
            _ = manager.gameLogView.LogTextCompat(GameLogView.AiDecision(logMsg));
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
            var roles = NPCEquipmentAutonomy.GetRoleKeywords(npc, data);
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

            // Exclude furniture, containers, and static objects
            if (n.Contains("rack") || n.Contains("shelf") || n.Contains("cabinet") || n.Contains("chest") ||
                n.Contains("table") || n.Contains("altar") || n.Contains("stand") || n.Contains("pedestal") ||
                n.Contains("crate") || n.Contains("barrel") || n.Contains("box") || n.Contains("bin") ||
                n.Contains("locker") || n.Contains("vault") || n.Contains("coffer") || n.Contains("reliquary") ||
                n.Contains("storage") || n.Contains("container") || n.Contains("sarcophagus") || n.Contains("urn"))
                return false;

            return n.Contains("machete") || n.Contains("sword") || n.Contains("shield") || n.Contains("armor") ||
                   n.Contains("potion") || n.Contains("book") || n.Contains("dagger") || n.Contains("staff") ||
                   n.Contains("bow") || n.Contains("helmet") || n.Contains("boots") || n.Contains("gloves") ||
                   n.Contains("ring") || n.Contains("amulet") || n.Contains("scroll") || n.Contains("gem") ||
                   n.Contains("herb") || n.Contains("mushroom") || n.Contains("meat") || n.Contains("bread");
        }
    }
}
