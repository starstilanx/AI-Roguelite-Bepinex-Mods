using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using System.Threading.Tasks;
using System;

namespace AIROG_WorldExpansion
{
    public class WorldSimulation
    {
        private const int SIMULATION_INTERVAL = 10;
        private static System.Random rng = new System.Random();

        [HarmonyPatch(typeof(GameplayManager), "InvokeTurnHappened")]
        [HarmonyPostfix]
        public static void OnTurnHappened(GameplayManager __instance, int numTurns, long secs)
        {
            if (__instance == null) return;
            
            // Advance internal turn counter
            WorldData.CurrentState.CurrentTurn += numTurns;
            int turn = WorldData.CurrentState.CurrentTurn;

            // Minor Tick (Actions) - Every 5 turns (User requested)
            if (turn % 5 == 0)
            {
                RunMinorTick(__instance);
            }

            // Major Event - Randomly between 10-100 turns
            if (turn >= WorldData.CurrentState.NextMajorEventTurn)
            {
                RunMajorTick(__instance);
            }

            // Medium Tick (Economy) - Every 25 turns
            if (turn % 25 == 0)
            {
                RunEconomyTick(__instance);
            }
        }

        public static void RunMajorTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running MAJOR Tick at Turn {WorldData.CurrentState.CurrentTurn}");
            
            string[] majorEvents = {
                "The Great Plague has sweeped across the lands, deciminating populations and halting trade.",
                "An Age of Discovery has begun! Explorers are finding new lands and ancient artifacts.",
                "A Global War has broken out as old alliances crumble and new powers rise.",
                "The Stars Have Aligned, bringing a surge of magical energy to the world.",
                "A Great Depression has hit the global economy, making gold scarce and desperation high.",
                "The Rise of a Dark Lord has been prophesied, causing fear to spread through every kingdom.",
                "A Holy Crusade has been declared, uniting many factions under a single banner."
            };

            string desc = majorEvents[rng.Next(majorEvents.Length)];
            WorldData.LogEvent(desc, "MAJOR");
            WorldData.CurrentState.MajorEventHistory.Add(desc);
            
            // Re-schedule next major event: 10-100 turns from now
            WorldData.CurrentState.NextMajorEventTurn = WorldData.CurrentState.CurrentTurn + rng.Next(10, 101);
            
            // Record in major event history and log, but not lorebook per user request
            // WorldLoreExpansion.RecordHistoricalEvent(manager, desc, "History", new List<string> { "Major", "Global" });
            WorldEventsUI.MarkDirty();
        }

        public static void RunEconomyTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running Economy Tick at Turn {WorldData.CurrentState.CurrentTurn}");
            
            // Randomly shift market condition
            double r = rng.NextDouble();
            var market = WorldData.CurrentState.Market;
            string desc = "";
            string type = "ECONOMY";

            // 20% chance to change state
            if (r < 0.20)
            {
                int state = rng.Next(5);
                switch (state)
                {
                    case 0: // Normal
                        market.GlobalCondition = "Normal";
                        market.PriceMultiplier = 1.0f;
                        market.SellMultiplier = 1.0f;
                        desc = "The global markets have stabilized.";
                        break;
                    case 1: // Shortage
                        market.GlobalCondition = "Shortage";
                        market.PriceMultiplier = 1.4f;
                        market.SellMultiplier = 1.2f;
                        desc = "Resources are scarce! Prices for goods have skyrocketed.";
                        break;
                    case 2: // Surplus
                        market.GlobalCondition = "Surplus";
                        market.PriceMultiplier = 0.7f;
                        market.SellMultiplier = 0.6f;
                        desc = "The markets are flooded with goods. Prices have dropped.";
                        break;
                    case 3: // Inflation
                        market.GlobalCondition = "Inflation";
                        market.PriceMultiplier = 1.25f; // Buy expensive
                        market.SellMultiplier = 1.2f;
                        desc = "Inflation is rising. Currency is flowing freely.";
                        break;
                    case 4: // Depression
                        market.GlobalCondition = "Depression";
                        market.PriceMultiplier = 0.6f;
                        market.SellMultiplier = 0.4f;
                        desc = "Economic depression has hit. Trade has ground to a halt.";
                        break;
                }
                
                WorldData.LogEvent(desc, type);
                // WorldLoreExpansion.RecordHistoricalEvent(manager, desc, "Economy");
                WorldEventsUI.MarkDirty();
            }
        }

        public static void RunMinorTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running Minor Tick at Turn {WorldData.CurrentState.CurrentTurn}");
            
            var factions = manager.GetCurrentFactions(); // Get all active factions
            if (factions == null || factions.Count == 0) return;

            // Simple Income Processing
            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                WorldData.GetFactionData(faction.uuid).Resources += 5; 
            }

            // Decisions - Pick ONE faction to act per cycle to avoid spam and lag
            var activeFactions = factions.Where(f => f.GetPrettyName() != "Player").ToList();
            if (activeFactions.Count == 0) return;

            var actingFaction = activeFactions[rng.Next(activeFactions.Count)];
            var actingData = WorldData.GetFactionData(actingFaction.uuid);

            // Slightly lowered threshold for testing
            if (actingData.Resources > 30) 
            {
                var others = activeFactions.Where(f => f.uuid != actingFaction.uuid).ToList();
                if (others.Count == 0) return;

                var targetFaction = others[rng.Next(others.Count)];
                PerformPlausibleAction(actingFaction, targetFaction, actingData, manager);
            }
        }

        private static void PerformPlausibleAction(Faction acting, Faction target, FactionExtData actingData, GameplayManager manager)
        {
            // 1. Establish/Update Tags
            UpdateFactionTag(acting);
            UpdateFactionTag(target);
            
            string actingTag = WorldData.GetFactionData(acting.uuid).Tag;
            string targetTag = WorldData.GetFactionData(target.uuid).Tag;

            // 2. Determine Relationship
            string relKey = GetRelationshipKey(acting, target);
            if (!WorldData.CurrentState.FactionRelationships.ContainsKey(relKey))
            {
                Debug.Log($"[WorldExpansion] Establishing relationship between {acting.GetPrettyName()} ({actingTag}) and {target.GetPrettyName()} ({targetTag})...");
                
                // Plausibility logic for initial relationship
                string rel = "NEUTRAL";
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    rel = "ENEMIES";
                else if (actingTag == targetTag && actingTag != "Neutral")
                    rel = "ALLIES";
                else if (rng.NextDouble() > 0.8)
                    rel = rng.NextDouble() > 0.5 ? "ALLIES" : "ENEMIES";

                WorldData.CurrentState.FactionRelationships[relKey] = rel;
            }

            string relation = WorldData.CurrentState.FactionRelationships[relKey]; // ALLIES, ENEMIES, NEUTRAL

            // 3. Decide Action based on Relationship and Tags
            // Weights: [Raid/War, Trade, Rumor]
            double[] weights = new double[] { 33, 33, 33 };

            if (relation == "ENEMIES") weights = new double[] { 80, 0, 20 };
            else if (relation == "ALLIES") weights = new double[] { 0, 80, 20 };
            else // NEUTRAL
            {
                weights = new double[] { 30, 30, 40 };
                // Tag-based bias for neutral factions
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    weights[0] += 50; // High chance of war
                if ((actingTag == "Trade" || targetTag == "Trade"))
                    weights[1] += 40; // High chance of trade
            }

            int actionType = PickWeighted(weights); // 0=War, 1=Trade, 2=Rumor

            // 4. Execute
            var targetData = WorldData.GetFactionData(target.uuid);
            string eventDesc = "";
            string eventType = "INFO";

            if (actionType == 0) // WAR
            {
                int cost = 30;
                if (actingData.Resources >= cost)
                {
                    actingData.Resources -= cost;
                    bool success = (rng.NextDouble() + (actingData.Resources * 0.01)) > (rng.NextDouble() + (targetData.Resources * 0.01));
                    if (success)
                    {
                        int stolen = rng.Next(10, 30);
                        targetData.Resources = System.Math.Max(0, targetData.Resources - stolen);
                        actingData.Resources += stolen;
                        eventDesc = $"{acting.GetPrettyName()} raided {target.GetPrettyName()} ({relation}) and plundered {stolen} resources!";
                        eventType = "WAR";
                    }
                    else
                    {
                        eventDesc = $"{acting.GetPrettyName()} tried to raid {target.GetPrettyName()} ({relation}) but failed.";
                        eventType = "WAR";
                    }
                }
            }
            else if (actionType == 1) // TRADE
            {
                if (actingData.Resources >= 10 && targetData.Resources >= 10)
                {
                    actingData.Resources += 15;
                    targetData.Resources += 15;
                    eventDesc = $"{acting.GetPrettyName()} and {target.GetPrettyName()} ({relation}) strengthened their economic ties through trade.";
                    eventType = "TRADE";
                }
            }
            else // RUMOR
            {
                 string[] flavors = (relation == "ENEMIES") 
                     ? new string[] { "denounced", "mocked", "threatened", "spied on" }
                     : ((relation == "ALLIES") 
                        ? new string[] { "praised", "sent gifts to", "held a feast for", "supported" }
                        : new string[] { "sent diplomats to", "is ignoring", "has concerns about", "observed" });
                
                string flavor = flavors[rng.Next(flavors.Length)];
                eventDesc = $"{acting.GetPrettyName()} {flavor} {target.GetPrettyName()}.";
                eventType = "RUMOR";
            }

            if (!string.IsNullOrEmpty(eventDesc))
            {
                WorldData.LogEvent(eventDesc, eventType);
                
                // Record significant events
                if (eventType == "WAR" || eventType == "TRADE")
                {
                    // var keys = new List<string> { acting.GetPrettyName(), target.GetPrettyName(), actingTag, targetTag, "war", "trade" };
                    // WorldLoreExpansion.RecordHistoricalEvent(manager, eventDesc, "History", keys);
                }
                
                WorldEventsUI.MarkDirty(); // Ensure UI updates
            }
        }

        private static void UpdateFactionTag(Faction f)
        {
            var data = WorldData.GetFactionData(f.uuid);
            if (data.Tag != "Neutral") return;

            string name = f.GetPrettyName().ToLower();

            // Religious / Divine
            if (name.Contains("holy") || name.Contains("church") || name.Contains("clergy") || name.Contains("divine") || 
                name.Contains("temple") || name.Contains("cult") || name.Contains("faith") || name.Contains("order of"))
                data.Tag = "Holy";
            
            // Demonic / Evil / Dark
            else if (name.Contains("demon") || name.Contains("hell") || name.Contains("abyss") || name.Contains("dark") || 
                     name.Contains("shadow") || name.Contains("evil") || name.Contains("chaos") || name.Contains("fiend"))
                data.Tag = "Demon";
            
            // Trade / Economy
            else if (name.Contains("trade") || name.Contains("merchant") || name.Contains("guild") || name.Contains("commerce") || 
                     name.Contains("bank") || name.Contains("cartel") || name.Contains("company") || name.Contains("exchange"))
                data.Tag = "Trade";
            
            // Political / Order
            else if (name.Contains("empire") || name.Contains("kingdom") || name.Contains("realm") || name.Contains("state") || 
                     name.Contains("republic") || name.Contains("alliance") || name.Contains("federation") || name.Contains("dominion"))
                data.Tag = "Empire";
            
            // Nature / Tribal
            else if (name.Contains("nature") || name.Contains("wild") || name.Contains("tribe") || name.Contains("clan") || 
                     name.Contains("druid") || name.Contains("forest") || name.Contains("beast") || name.Contains("horde"))
                data.Tag = "Nature";
            
            // Arcane / Magic
            else if (name.Contains("arcane") || name.Contains("mage") || name.Contains("wizard") || name.Contains("academy") || 
                     name.Contains("magic") || name.Contains("circle") || name.Contains("enclave"))
                data.Tag = "Arcane";

            // Undead
            else if (name.Contains("undead") || name.Contains("necromancer") || name.Contains("death") || name.Contains("lich") ||
                     name.Contains("grave") || name.Contains("crypt"))
                data.Tag = "Undead";
        }

        private static string GetRelationshipKey(Faction a, Faction b)
        {
            return string.Compare(a.uuid, b.uuid) < 0 
                ? $"{a.uuid}_{b.uuid}" 
                : $"{b.uuid}_{a.uuid}";
        }

        private static int PickWeighted(double[] weights)
        {
            double total = 0;
            foreach (var w in weights) total += w;
            
            double r = rng.NextDouble() * total;
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i];
                if (r <= sum) return i;
            }
            return weights.Length - 1;
        }
    }
}
