using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Settlement
{
    [Serializable]
    public class SettlementState
    {
        public string Name = "New Settlement";
        public string LocationUuid;
        public string ImageUuid;
        public int Level = 1;
        public Dictionary<string, int> Resources = new Dictionary<string, int>
        {
            {"Wood", 10},
            {"Stone", 5},
            {"Gold", 100}
        };

        public List<BuildingInstance> Buildings = new List<BuildingInstance>();
        public List<ResidentData> Residents = new List<ResidentData>();
        public HashSet<string> Researched = new HashSet<string>();
        public Dictionary<string, float> TradePrices = new Dictionary<string, float>
        {
            {"Wood", 1f},
            {"Stone", 1f}
        };

        public void AddResource(string key, int amount)
        {
            if (!Resources.ContainsKey(key)) Resources[key] = 0;
            Resources[key] += amount;
        }

        public bool HasBuilding(string id) =>
            Buildings.Exists(b => b.BuildingID == id && b.IsComplete);

        public BuildingInstance GetBuilding(string id) =>
            Buildings.Find(b => b.BuildingID == id && b.IsComplete);

        public int CompletedBuildingCount() =>
            Buildings.FindAll(b => b.IsComplete).Count;

        /// <summary>Max residents the settlement can hold: one per completed building, capped by UI space
        /// (raised by 2 once "civic_planning" is researched).</summary>
        public int GetPopulationCap()
        {
            int cap = 6 + (Researched.Contains("civic_planning") ? 2 : 0);
            return Math.Min(CompletedBuildingCount(), cap);
        }

        /// <summary>Town-wide happiness baseline from amenities, before any per-resident trait.
        /// Shared by UpdateHappiness and new-resident creation so the two never drift apart.</summary>
        public int GetBaseHappiness()
        {
            int happiness = 40;
            if (HasBuilding("tavern")) happiness += 20;
            if (HasBuilding("farm"))   happiness += 15;
            if (HasBuilding("market")) happiness += 10;
            if (HasBuilding("barracks")) happiness += 5; // Safety
            return Math.Min(100, happiness);
        }

        /// <summary>
        /// Produces resources from all built buildings and residents.
        /// Called from the game's TurnHappenedEvent; numTurns scales production
        /// when the game advances several turns at once.
        /// </summary>
        public void ProduceResources(int numTurns = 1)
        {
            if (string.IsNullOrEmpty(LocationUuid) || numTurns <= 0) return;

            bool guildCharter = Researched.Contains("guild_charter");
            foreach (var building in Buildings)
            {
                var def = BuildingCatalog.Get(building.BuildingID);
                if (def == null || !building.IsComplete) continue;
                foreach (var kv in def.Production)
                {
                    int amount = kv.Value * building.Level * numTurns;
                    if (kv.Key == "Gold" && guildCharter) amount = Mathf.RoundToInt(amount * 1.1f);
                    AddResource(kv.Key, amount);
                }

                if (building.BuildingID == "quarry" && Researched.Contains("masonry"))
                    AddResource("Stone", 2 * building.Level * numTurns);
                if (building.BuildingID == "farm" && Researched.Contains("irrigation"))
                    AddResource("Gold", Mathf.RoundToInt(3 * building.Level * numTurns * (guildCharter ? 1.1f : 1f)));
            }

            // Residents pay taxes: 1 gold/turn each, 2 if genuinely happy
            foreach (var resident in Residents)
                AddResource("Gold", (resident.Happiness >= 70 ? 2 : 1) * numTurns);

            // Knowledge trickles in from the population itself, not a building
            AddResource("Knowledge", Residents.Count * numTurns);
        }

        /// <summary>
        /// Recomputes each resident's happiness target from settlement amenities plus their
        /// own trait, then drifts current happiness toward that target (at most 5/turn) so
        /// temporary swings (a festival, a raid) fade out instead of vanishing next tick.
        /// The target computation itself stays deterministic (no RNG) so it's still safe to
        /// run every turn.
        /// </summary>
        public void UpdateHappiness()
        {
            int baseHappiness = GetBaseHappiness();

            foreach (var resident in Residents)
            {
                if (string.IsNullOrEmpty(resident.Trait))
                    resident.Trait = TraitCatalog.Random(); // self-heal residents from pre-1.2.0 saves

                int target = Math.Max(0, Math.Min(100, baseHappiness + TraitCatalog.GetModifier(resident.Trait)));
                int delta = target - resident.Happiness;
                int step = Math.Min(Math.Abs(delta), 5);
                resident.Happiness += Math.Sign(delta) * step;
            }
        }

        /// <summary>Settlement level derives from total construction (base 1 + one per two building levels).</summary>
        public void RecalculateLevel()
        {
            int totalLevels = 0;
            foreach (var b in Buildings)
                if (b.IsComplete) totalLevels += b.Level;
            Level = 1 + totalLevels / 2;
        }
    }

    [Serializable]
    public class BuildingInstance
    {
        public string BuildingID;
        public string Name;
        public int Level = 1;
        public bool IsComplete = true;
        public float ConstructionProgress = 100f;
    }

    [Serializable]
    public class ResidentData
    {
        public string Name;
        public string Job;
        public string Uuid; // References GameCharacter if applicable
        public int Happiness = 50;
        public string Trait;
    }

    /// <summary>
    /// Flavor personality traits that push a resident's happiness target above or below
    /// the settlement-wide baseline, giving each resident their own trajectory instead of
    /// everyone sharing one number.
    /// </summary>
    public static class TraitCatalog
    {
        private static readonly (string Name, int Modifier, string Flavor)[] All =
        {
            ("Hardy", 8, "shrugs off hardship"),
            ("Sociable", 5, "thrives around neighbors"),
            ("Content", 3, "easy to please"),
            ("Restless", -3, "itches for something new"),
            ("Grumbling", -5, "grumbles more than most"),
            ("Anxious", -8, "worries constantly"),
        };

        public static string Random() => All[UnityEngine.Random.Range(0, All.Length)].Name;

        public static int GetModifier(string trait)
        {
            foreach (var t in All) if (t.Name == trait) return t.Modifier;
            return 0;
        }

        public static string GetFlavor(string trait)
        {
            foreach (var t in All) if (t.Name == trait) return t.Flavor;
            return "";
        }
    }

    /// <summary>
    /// Defines a buildable structure: cost to construct and resources produced per turn.
    /// </summary>
    public class BuildingDefinition
    {
        public const int MAX_LEVEL = 3;

        public string ID;
        public string Name;
        public string Description;
        public Dictionary<string, int> Cost;
        public Dictionary<string, int> Production;
        public string ResidentJob; // Job title granted to residents this building employs

        public bool CanAfford(SettlementState state) => CanAffordCost(state, Cost);

        /// <summary>Upgrade to the next level costs the base cost multiplied by that level.</summary>
        public Dictionary<string, int> GetUpgradeCost(int currentLevel)
        {
            var scaled = new Dictionary<string, int>();
            foreach (var kv in Cost)
                scaled[kv.Key] = kv.Value * (currentLevel + 1);
            return scaled;
        }

        public static bool CanAffordCost(SettlementState state, Dictionary<string, int> cost)
        {
            foreach (var kv in cost)
            {
                if (!state.Resources.TryGetValue(kv.Key, out int have) || have < kv.Value)
                    return false;
            }
            return true;
        }
    }

    public static class BuildingCatalog
    {
        public static readonly BuildingDefinition[] All = new[]
        {
            new BuildingDefinition
            {
                ID = "woodcutter", Name = "Woodcutter's Hut",
                Description = "Produces 5 wood per turn.",
                Cost = new Dictionary<string, int> { { "Gold", 40 } },
                Production = new Dictionary<string, int> { { "Wood", 5 } },
                ResidentJob = "Lumberjack"
            },
            new BuildingDefinition
            {
                ID = "quarry", Name = "Quarry",
                Description = "Produces 3 stone per turn.",
                Cost = new Dictionary<string, int> { { "Gold", 60 } },
                Production = new Dictionary<string, int> { { "Stone", 3 } },
                ResidentJob = "Stonemason"
            },
            new BuildingDefinition
            {
                ID = "market", Name = "Market",
                Description = "Generates 15 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 20 }, { "Stone", 10 } },
                Production = new Dictionary<string, int> { { "Gold", 15 } },
                ResidentJob = "Merchant"
            },
            new BuildingDefinition
            {
                ID = "barracks", Name = "Barracks",
                Description = "Trains militia for settlement defense.",
                Cost = new Dictionary<string, int> { { "Wood", 30 }, { "Stone", 20 } },
                Production = new Dictionary<string, int>(),
                ResidentJob = "Militia Guard"
            },
            new BuildingDefinition
            {
                ID = "tavern", Name = "Tavern",
                Description = "Attracts travelers. +5 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 25 }, { "Gold", 30 } },
                Production = new Dictionary<string, int> { { "Gold", 5 } },
                ResidentJob = "Barkeep"
            },
            new BuildingDefinition
            {
                ID = "farm", Name = "Farm",
                Description = "Sustains the population. +3 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 15 }, { "Gold", 20 } },
                Production = new Dictionary<string, int> { { "Gold", 3 } },
                ResidentJob = "Farmer"
            },
        };

        public static BuildingDefinition Get(string id) =>
            Array.Find(All, b => b.ID == id);
    }
}
