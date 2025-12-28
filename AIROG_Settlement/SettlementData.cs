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
}
