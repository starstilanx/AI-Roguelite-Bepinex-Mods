using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class WorldContextProvider : IContextProvider
    {
        public int Priority => 80; // Medium-High
        public string Name => "World State";
        public string Description => "Injects global events, economy status (market), and major world news.";

        // SYNC NOTE: Keep in sync with WorldData.cs in AIROG_WorldExpansion
#pragma warning disable 0649
        private class WorldStateStub
        {
            public int CurrentTurn;
            public Dictionary<string, FactionExtDataStub> Factions;
            public List<WorldEventStub> Events;
            public Dictionary<string, string> FactionRelationships;
            public MarketStateStub Market;
            public List<string> MajorEventHistory;
        }

        private class MarketStateStub
        {
            public string GlobalCondition;
            public float PriceMultiplier;
            public float SellMultiplier;
        }

        private class FactionExtDataStub
        {
            public int Resources;
            public List<string> ClaimedPlaceUuids;
            public string Tag;
        }

        private class WorldEventStub
        {
            public string Description;
            public string Type;
            public int Turn;
            public long Timestamp;
        }

        private WorldStateStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 10f; // World events happen on turns

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null) return "";

            string context = "";

            // Current Turn (for temporal context)
            if (_cache.CurrentTurn > 0)
            {
                context += $"- Current Turn: {_cache.CurrentTurn}\n";
            }

            // Economy
            if (_cache.Market != null && !string.IsNullOrEmpty(_cache.Market.GlobalCondition))
            {
                string economyInfo = _cache.Market.GlobalCondition;
                if (_cache.Market.PriceMultiplier != 1.0f)
                {
                    economyInfo += $" (Prices: {_cache.Market.PriceMultiplier:P0})";
                }
                context += $"- Global Economy: {economyInfo}\n";
            }

            // Major Events (Contextually important)
            if (_cache.MajorEventHistory != null && _cache.MajorEventHistory.Count > 0)
            {
                var recentMajor = _cache.MajorEventHistory.Skip(Math.Max(0, _cache.MajorEventHistory.Count - 2));
                context += "- Major Events: " + string.Join("; ", recentMajor) + "\n";
            }

            // Recent Minor events
            if (_cache.Events != null && _cache.Events.Count > 0)
            {
                var recent = _cache.Events.Where(e => e.Type != "MAJOR" && e.Type != "ECONOMY")
                                          .OrderByDescending(e => e.Turn)
                                          .Take(3)
                                          .Select(e => e.Description);
                if (recent.Any())
                {
                    context += "- Recent News: " + string.Join("; ", recent) + "\n";
                }
            }

            // Faction Summary (if any significant factions)
            if (_cache.Factions != null && _cache.Factions.Count > 0)
            {
                var significantFactions = _cache.Factions
                    .Where(f => !string.IsNullOrEmpty(f.Value.Tag) && f.Value.Tag != "Neutral")
                    .Take(3)
                    .Select(f => $"{f.Value.Tag}");
                    
                if (significantFactions.Any())
                {
                    context += "- Active Factions: " + string.Join(", ", significantFactions) + "\n";
                }
            }

            if (!string.IsNullOrEmpty(context))
            {
                return "\n[WORLD STATE]\n" + context;
            }

            return "";
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
            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "world_expansion_data.json");
            if (!File.Exists(path)) return;

            try
            {
                _cache = JsonConvert.DeserializeObject<WorldStateStub>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load world data: {ex.Message}");
            }
        }
    }
}
