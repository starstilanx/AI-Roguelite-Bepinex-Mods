using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    public class WorldData
    {
        public static WorldState CurrentState = new WorldState();

        public static void Reset()
        {
            CurrentState = new WorldState();
        }

        public static void Save(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "world_expansion_data.json");
                string json = JsonConvert.SerializeObject(CurrentState, Formatting.Indented);
                File.WriteAllText(path, json);
                Debug.Log($"[WorldExpansion] Saved world data to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[WorldExpansion] Failed to save world data: {e.Message}");
            }
        }

        public static void Load(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "world_expansion_data.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    CurrentState = JsonConvert.DeserializeObject<WorldState>(json) ?? new WorldState();
                    Debug.Log($"[WorldExpansion] Loaded world data from {path}");
                }
                else
                {
                    Debug.Log("[WorldExpansion] No existing world data found, creating new.");
                    CurrentState = new WorldState();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WorldExpansion] Failed to load world data: {e.Message}");
                CurrentState = new WorldState();
            }
        }
        
        public static FactionExtData GetFactionData(string factionUuid)
        {
            if (!CurrentState.Factions.ContainsKey(factionUuid))
            {
                CurrentState.Factions[factionUuid] = new FactionExtData();
            }
            return CurrentState.Factions[factionUuid];
        }

        public static void LogEvent(string desc, string type)
        {
            CurrentState.Events.Add(new WorldEvent 
            { 
                Turn = CurrentState.CurrentTurn, 
                Description = desc, 
                Type = type 
            });
            // Keep log size manageable? Maybe last 100 events
            if (CurrentState.Events.Count > 100)
            {
                CurrentState.Events.RemoveAt(0);
            }
        }

        public static LoreExtraData GetLoreExtra(Lorebook.LoreEntry entry)
        {
            if (entry == null) return null;
            string key = $"{entry.ToUiKeysStr()}_{entry.val}";
            if (!CurrentState.LoreExtras.ContainsKey(key))
            {
                CurrentState.LoreExtras[key] = new LoreExtraData();
            }
            return CurrentState.LoreExtras[key];
        }

        public static void UpdateLoreExtraKey(string oldKeyStr, string oldVal, Lorebook.LoreEntry entry)
        {
            string oldKey = $"{oldKeyStr}_{oldVal}";
            string newKey = $"{entry.ToUiKeysStr()}_{entry.val}";
            if (oldKey != newKey && CurrentState.LoreExtras.ContainsKey(oldKey))
            {
                var data = CurrentState.LoreExtras[oldKey];
                CurrentState.LoreExtras.Remove(oldKey);
                CurrentState.LoreExtras[newKey] = data;
            }
        }
    }

    [Serializable]
    public class WorldState
    {
        public int CurrentTurn = 0;
        public Dictionary<string, FactionExtData> Factions = new Dictionary<string, FactionExtData>();
        public List<WorldEvent> Events = new List<WorldEvent>();
        public Dictionary<string, string> FactionRelationships = new Dictionary<string, string>(); 
        public MarketState Market = new MarketState();
        public Dictionary<string, LoreExtraData> LoreExtras = new Dictionary<string, LoreExtraData>(); // Key: some unique identifier for LoreEntry
        
        // Major Event Tracking
        public int NextMajorEventTurn = 50;
        public List<string> MajorEventHistory = new List<string>();
    }

    [Serializable]
    public class LoreExtraData
    {
        public string Category = "General";
        public string ImageUuid = "";
    }

    [Serializable]
    public class MarketState
    {
        public string GlobalCondition = "Normal"; // Normal, Shortage, Surplus, Inflation, Depression
        public float PriceMultiplier = 1.0f;
        public float SellMultiplier = 1.0f;
        public Dictionary<string, float> ItemTypeModifiers = new Dictionary<string, float>(); // e.g. "Weapons" -> 1.2f
    }

    [Serializable]
    public class FactionExtData
    {
        public int Resources = 100; // Start with some resources
        // We moved dynamic relationship status to the main state dictionary for easier pair-management
        public List<string> ClaimedPlaceUuids = new List<string>(); 
        public string Tag = "Neutral"; // e.g. Holy, Demon, Clergy, Trade, Empire
    }

    [Serializable]
    public class WorldEvent
    {
        public int Turn;
        public string Description;
        public string Type;
        public long Timestamp = DateTime.Now.Ticks;
        
        public string GetFormatted()
        {
            return $"Turn {Turn}: {Description}";
        }
    }
}
