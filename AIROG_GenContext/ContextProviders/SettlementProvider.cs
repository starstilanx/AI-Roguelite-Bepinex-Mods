using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using System.Linq;

namespace AIROG_GenContext.ContextProviders
{
    public class SettlementProvider : IContextProvider
    {
        public int Priority => 90; // Medium-High (if present)
        public string Name => "Settlement Info";
        public string Description => "Injects data about the current player settlement, including resources and location type.";

        // SYNC NOTE: Keep in sync with SettlementData.cs in AIROG_Settlement
#pragma warning disable 0649
        private class SettlementStateStub
        {
            public string Name;
            public string LocationUuid;
            public string ImageUuid;
            public int Level;
            public Dictionary<string, int> Resources;
            public List<BuildingInstanceStub> Buildings;
            public List<ResidentDataStub> Residents;
        }

        private class BuildingInstanceStub
        {
            public string BuildingID;
            public string Name;
            public int Level;
            public bool IsComplete;
            public float ConstructionProgress;
        }

        private class ResidentDataStub
        {
            public string Name;
            public string Job;
            public string Uuid;
            public int Happiness;
        }

        private SettlementStateStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 5f; 

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
            if (manager == null || manager.currentPlace == null) return "";

            RefreshCacheIfNeeded();
            if (_cache == null) return "";

            // Check if we are AT the settlement
            if (_cache.LocationUuid != manager.currentPlace.uuid) return "";

            string context = $"\n\n[LOCATION: {_cache.Name}]\n";
            context += "Type: Player Settlement";
            
            // Add level if > 1
            if (_cache.Level > 1)
            {
                context += $" (Level {_cache.Level})";
            }
            context += "\n";
            
            // Resources
            if (_cache.Resources != null && _cache.Resources.Count > 0)
            {
               context += "Resources: " + string.Join(", ", _cache.Resources.Select(kvp => $"{kvp.Key}: {kvp.Value}")) + "\n";
            }
            
            // Buildings
            if (_cache.Buildings != null && _cache.Buildings.Count > 0)
            {
                var completedBuildings = _cache.Buildings.Where(b => b.IsComplete).ToList();
                var underConstruction = _cache.Buildings.Where(b => !b.IsComplete).ToList();
                
                if (completedBuildings.Count > 0)
                {
                    context += "Buildings: " + string.Join(", ", completedBuildings.Take(5).Select(b => b.Name)) + "\n";
                }
                if (underConstruction.Count > 0)
                {
                    context += "Under Construction: " + string.Join(", ", underConstruction.Select(b => $"{b.Name} ({b.ConstructionProgress:F0}%)")) + "\n";
                }
            }
            
            // Residents
            if (_cache.Residents != null && _cache.Residents.Count > 0)
            {
                int avgHappiness = (int)_cache.Residents.Average(r => r.Happiness);
                string mood = avgHappiness >= 70 ? "Content" : avgHappiness >= 40 ? "Neutral" : "Unhappy";
                
                var workers = _cache.Residents.Where(r => !string.IsNullOrEmpty(r.Job)).Take(3);
                context += $"Population: {_cache.Residents.Count} ({mood})\n";
                
                if (workers.Any())
                {
                    context += "Notable Residents: " + string.Join(", ", workers.Select(r => $"{r.Name} ({r.Job})")) + "\n";
                }
            }
            
            return context;
        }

        private void RefreshCacheIfNeeded()
        {
            if (Time.time - _lastLoadTime > CACHE_REFRESH_RATE)
            {
                LoadData();
                _lastLoadTime = Time.time;
            }
        }

        private void LoadData()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "settlement_data.json");
            if (!File.Exists(path)) return;

            try
            {
                _cache = JsonConvert.DeserializeObject<SettlementStateStub>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load settlement data: {ex.Message}");
            }
        }
    }
}
