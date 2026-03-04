using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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

        public void AddResource(string key, int amount)
        {
            if (!Resources.ContainsKey(key)) Resources[key] = 0;
            Resources[key] += amount;
        }

        public bool HasBuilding(string id) =>
            Buildings.Exists(b => b.BuildingID == id && b.IsComplete);

        /// <summary>
        /// Called once per game turn (on WriteSaveFile) to produce resources from all built buildings.
        /// </summary>
        public void ProduceResources()
        {
            if (string.IsNullOrEmpty(LocationUuid)) return;
            foreach (var building in Buildings)
            {
                var def = BuildingCatalog.Get(building.BuildingID);
                if (def == null || !building.IsComplete) continue;
                foreach (var kv in def.Production)
                    AddResource(kv.Key, kv.Value * building.Level);
            }
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
    }

    /// <summary>
    /// Defines a buildable structure: cost to construct and resources produced per turn.
    /// </summary>
    public class BuildingDefinition
    {
        public string ID;
        public string Name;
        public string Description;
        public Dictionary<string, int> Cost;
        public Dictionary<string, int> Production;

        public bool CanAfford(SettlementState state)
        {
            foreach (var kv in Cost)
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
                Production = new Dictionary<string, int> { { "Wood", 5 } }
            },
            new BuildingDefinition
            {
                ID = "quarry", Name = "Quarry",
                Description = "Produces 3 stone per turn.",
                Cost = new Dictionary<string, int> { { "Gold", 60 } },
                Production = new Dictionary<string, int> { { "Stone", 3 } }
            },
            new BuildingDefinition
            {
                ID = "market", Name = "Market",
                Description = "Generates 15 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 20 }, { "Stone", 10 } },
                Production = new Dictionary<string, int> { { "Gold", 15 } }
            },
            new BuildingDefinition
            {
                ID = "barracks", Name = "Barracks",
                Description = "Trains militia for settlement defense.",
                Cost = new Dictionary<string, int> { { "Wood", 30 }, { "Stone", 20 } },
                Production = new Dictionary<string, int>()
            },
            new BuildingDefinition
            {
                ID = "tavern", Name = "Tavern",
                Description = "Attracts travelers. +5 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 25 }, { "Gold", 30 } },
                Production = new Dictionary<string, int> { { "Gold", 5 } }
            },
            new BuildingDefinition
            {
                ID = "farm", Name = "Farm",
                Description = "Sustains the population. +3 gold per turn.",
                Cost = new Dictionary<string, int> { { "Wood", 15 }, { "Gold", 20 } },
                Production = new Dictionary<string, int> { { "Gold", 3 } }
            },
        };

        public static BuildingDefinition Get(string id) =>
            Array.Find(All, b => b.ID == id);
    }
}
