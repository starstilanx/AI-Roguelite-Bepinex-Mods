using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_Settlement
{
    /// <summary>One button on an event popup: a label plus the effect it applies. Resolve
    /// both mutates state and returns the outcome line to log, so choices with a random
    /// roll (e.g. "Fight back") can report which way it went.</summary>
    public class SettlementEventChoice
    {
        public string Label;
        public Func<SettlementState, string> Resolve;
    }

    public class SettlementEventDefinition
    {
        public string ID;
        public string Title;
        public string FlavorText;
        public Func<SettlementState, bool> Condition;
        public Func<SettlementState, float> GetWeight;
        public List<SettlementEventChoice> Choices;
    }

    public static class SettlementEventCatalog
    {
        private static int GoldOf(SettlementState s) => s.Resources.TryGetValue("Gold", out int g) ? g : 0;
        private static int ResOf(SettlementState s, string key) => s.Resources.TryGetValue(key, out int v) ? v : 0;

        public static readonly SettlementEventDefinition[] All =
        {
            new SettlementEventDefinition
            {
                ID = "raid", Title = "Bandit Raid",
                FlavorText = "A band of raiders has been spotted approaching the settlement's stores.",
                Condition = s => GoldOf(s) >= 10,
                GetWeight = s => s.HasBuilding("barracks") ? 1.5f : 3f,
                Choices = new List<SettlementEventChoice>
                {
                    new SettlementEventChoice
                    {
                        Label = "Pay tribute",
                        Resolve = s =>
                        {
                            int loss = Math.Min(GoldOf(s), 25 + 5 * s.Level);
                            s.AddResource("Gold", -loss);
                            return $"The raiders take {loss} gold and leave the rest of the settlement untouched.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Fight back",
                        Resolve = s =>
                        {
                            float chance = s.HasBuilding("barracks") ? 0.7f : 0.3f;
                            if (s.Researched.Contains("fortifications")) chance = Mathf.Min(0.95f, chance + 0.2f);

                            if (UnityEngine.Random.value < chance)
                            {
                                int loot = 10 + UnityEngine.Random.Range(0, 11);
                                s.AddResource("Gold", loot);
                                return $"The militia drives the raiders off and recovers {loot} gold from their camp.";
                            }

                            int goldLoss = Math.Min(GoldOf(s), 15 + 5 * s.Level);
                            int woodLoss = Math.Min(ResOf(s, "Wood"), 10);
                            int stoneLoss = Math.Min(ResOf(s, "Stone"), 10);
                            s.AddResource("Gold", -goldLoss);
                            s.AddResource("Wood", -woodLoss);
                            s.AddResource("Stone", -stoneLoss);
                            return $"The militia is overwhelmed. The raiders make off with {goldLoss} gold and haul away supplies.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Ignore and hope they pass",
                        Resolve = s =>
                        {
                            int goldLoss = Math.Min(GoldOf(s), 40 + 10 * s.Level);
                            int woodLoss = Math.Min(ResOf(s, "Wood"), 10);
                            int stoneLoss = Math.Min(ResOf(s, "Stone"), 10);
                            s.AddResource("Gold", -goldLoss);
                            s.AddResource("Wood", -woodLoss);
                            s.AddResource("Stone", -stoneLoss);
                            return $"With no one to stop them, the raiders help themselves: {goldLoss} gold and whatever supplies they can carry.";
                        }
                    }
                }
            },

            new SettlementEventDefinition
            {
                ID = "fire", Title = "Wildfire",
                FlavorText = "A fire has broken out and is threatening to spread to one of the settlement's buildings.",
                Condition = s => s.CompletedBuildingCount() > 0,
                GetWeight = s => 2f,
                Choices = new List<SettlementEventChoice>
                {
                    new SettlementEventChoice
                    {
                        Label = "Fight the blaze",
                        Resolve = s =>
                        {
                            int woodLoss = Math.Min(ResOf(s, "Wood"), 10);
                            int stoneLoss = Math.Min(ResOf(s, "Stone"), 5);
                            s.AddResource("Wood", -woodLoss);
                            s.AddResource("Stone", -stoneLoss);
                            return "Residents form a bucket line and beat back the flames, burning through some supplies in the process.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Let it burn, save your people",
                        Resolve = s =>
                        {
                            var built = s.Buildings.FindAll(b => b.IsComplete);
                            if (built.Count == 0) return "The fire burns out on its own with nothing left to catch.";
                            var hit = built[UnityEngine.Random.Range(0, built.Count)];
                            hit.Level = Math.Max(1, hit.Level - 1);
                            s.RecalculateLevel();
                            return $"No one is hurt, but the {hit.Name} is left charred and diminished.";
                        }
                    }
                }
            },

            new SettlementEventDefinition
            {
                ID = "merchant", Title = "Traveling Merchant",
                FlavorText = "A merchant's cart has rolled into the settlement, laden with goods.",
                Condition = s => true,
                GetWeight = s => 2.5f,
                Choices = new List<SettlementEventChoice>
                {
                    new SettlementEventChoice
                    {
                        Label = "Buy discounted supplies",
                        Resolve = s =>
                        {
                            if (GoldOf(s) < 30) return "The settlement can't spare the gold, and the merchant shrugs and moves on.";
                            s.AddResource("Gold", -30);
                            s.AddResource("Wood", 15);
                            s.AddResource("Stone", 15);
                            return "You trade 30 gold for a cartload of wood and stone, well below the going rate.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Sell surplus at premium",
                        Resolve = s =>
                        {
                            if (ResOf(s, "Wood") >= 15)
                            {
                                s.AddResource("Wood", -15);
                                s.AddResource("Gold", 30);
                                return "The merchant pays a premium price for 15 wood: 30 gold, well above market rate.";
                            }
                            if (ResOf(s, "Stone") >= 15)
                            {
                                s.AddResource("Stone", -15);
                                s.AddResource("Gold", 35);
                                return "The merchant pays a premium price for 15 stone: 35 gold, well above market rate.";
                            }
                            return "You don't have enough surplus stock to interest the merchant.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Decline",
                        Resolve = s => "The merchant continues on their way."
                    }
                }
            },

            new SettlementEventDefinition
            {
                ID = "festival", Title = "Festival",
                FlavorText = "The residents want to celebrate. How grand should it be?",
                Condition = s => s.Residents.Count >= 1,
                GetWeight = s => 2f,
                Choices = new List<SettlementEventChoice>
                {
                    new SettlementEventChoice
                    {
                        Label = "Host a grand feast",
                        Resolve = s =>
                        {
                            if (GoldOf(s) < 20) return "There isn't enough gold in the coffers for a proper feast.";
                            s.AddResource("Gold", -20);
                            foreach (var r in s.Residents) r.Happiness = Math.Min(100, r.Happiness + 15);

                            if (s.Residents.Count < s.GetPopulationCap() && UnityEngine.Random.value < 0.3f)
                            {
                                var newcomer = SettlementPlugin.CreateNewResident(s);
                                if (newcomer != null)
                                {
                                    s.Residents.Add(newcomer);
                                    return $"The feast draws travelers passing through. {newcomer.Name} decides to stay for good.";
                                }
                            }
                            return "The settlement celebrates late into the night. Spirits are lifted all around.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Keep it modest",
                        Resolve = s =>
                        {
                            foreach (var r in s.Residents) r.Happiness = Math.Min(100, r.Happiness + 5);
                            return "A small gathering lifts everyone's spirits without costing the settlement a thing.";
                        }
                    }
                }
            },

            new SettlementEventDefinition
            {
                ID = "harvest", Title = "Good Harvest",
                FlavorText = "The farm has yielded more than expected this season.",
                Condition = s => s.HasBuilding("farm"),
                GetWeight = s => 2f,
                Choices = new List<SettlementEventChoice>
                {
                    new SettlementEventChoice
                    {
                        Label = "Share the surplus",
                        Resolve = s =>
                        {
                            foreach (var r in s.Residents) r.Happiness = Math.Min(100, r.Happiness + 10);
                            return "The surplus is shared freely. The settlement eats well this week.";
                        }
                    },
                    new SettlementEventChoice
                    {
                        Label = "Stockpile the grain",
                        Resolve = s =>
                        {
                            s.AddResource("Gold", 15);
                            s.AddResource("Wood", 10);
                            return "The surplus is sold off and the proceeds put toward the settlement's stores.";
                        }
                    }
                }
            },
        };

        public static SettlementEventDefinition Get(string id) => Array.Find(All, e => e.ID == id);
    }

    public partial class SettlementPlugin
    {
        public SettlementEventDefinition PendingEvent;

        /// <summary>
        /// Rolls for a random settlement event, same compounding-chance shape as resident
        /// arrival. At most one event is ever pending at a time — if one is already awaiting
        /// a player choice, no new roll happens this tick. Rolling only queues the event
        /// (sets PendingEvent); it does NOT show the popup. The popup only appears once the
        /// player is actually at the settlement's location (checked every frame in Update()) —
        /// events shouldn't interrupt whatever the player is doing somewhere else in the story.
        /// </summary>
        private void TryTriggerEvent(int numTurns)
        {
            if (PendingEvent != null) return;
            if (EventPopupObj == null) return; // UI not built yet; don't strand a pending event with no way to resolve it
            var s = CurrentSettlement;
            if (string.IsNullOrEmpty(s.LocationUuid)) return;

            float chance = 1f - Mathf.Pow(1f - 0.15f, numTurns);
            if (UnityEngine.Random.value > chance) return;

            var eligible = new List<(SettlementEventDefinition def, float weight)>();
            float totalWeight = 0f;
            foreach (var def in SettlementEventCatalog.All)
            {
                if (def.Condition != null && !def.Condition(s)) continue;
                float w = def.GetWeight != null ? def.GetWeight(s) : 1f;
                if (w <= 0f) continue;
                eligible.Add((def, w));
                totalWeight += w;
            }
            if (eligible.Count == 0) return;

            float roll = UnityEngine.Random.value * totalWeight;
            SettlementEventDefinition chosen = eligible[eligible.Count - 1].def;
            float cursor = 0f;
            foreach (var (def, weight) in eligible)
            {
                cursor += weight;
                if (roll <= cursor) { chosen = def; break; }
            }

            PendingEvent = chosen;
        }
    }
}
