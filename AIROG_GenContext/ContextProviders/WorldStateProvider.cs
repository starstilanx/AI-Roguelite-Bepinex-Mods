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
        public int Priority    => 80;
        public string Name     => "World State";
        public string Description => "Injects season, economy, active wars, factions, and world events into the AI prompt.";

        // ── Stub classes (kept in sync with WorldData.cs in AIROG_WorldExpansion) ──
#pragma warning disable 0649
        private class WorldStateStub
        {
            public int CurrentTurn;
            public string CurrentSeason;
            public Dictionary<string, FactionExtDataStub> Factions;
            public List<WorldEventStub> Events;
            public Dictionary<string, string> FactionRelationships;
            public MarketStateStub Market;
            public List<string> MajorEventHistory;
            public Dictionary<string, WarDeclarationStub> ActiveWars;
            public Dictionary<string, int> GrievanceCounts;
            public List<string> EliminatedFactions; // HashSet serializes as array
        }

        private class MarketStateStub
        {
            public string GlobalCondition;
            public string PreviousCondition;
            public float PriceMultiplier;
            public float SellMultiplier;
        }

        private class FactionExtDataStub
        {
            public string Name;
            public int Resources;
            public List<string> ClaimedPlaceUuids;
            public string Tag;
        }

        private class WorldEventStub
        {
            public string Description;
            public string Type;
            public int Turn;
        }

        private class WarDeclarationStub
        {
            public string ActorName;
            public string TargetName;
            public string CasusBelli;
            public int StartTurn;
        }
#pragma warning restore 0649

        private WorldStateStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 8f;

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null) return "";

            string context = "";

            // ── Season ────────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(_cache.CurrentSeason))
                context += $"- Season: {_cache.CurrentSeason}\n";

            // ── Economy ───────────────────────────────────────────────────────────
            if (_cache.Market != null && !string.IsNullOrEmpty(_cache.Market.GlobalCondition))
            {
                string econ = _cache.Market.GlobalCondition;
                if (_cache.Market.PriceMultiplier != 1.0f || _cache.Market.SellMultiplier != 1.0f)
                    econ += $" (buy ×{_cache.Market.PriceMultiplier:0.##}, sell ×{_cache.Market.SellMultiplier:0.##})";
                context += $"- Economy: {econ}\n";
            }

            // ── Active Wars ───────────────────────────────────────────────────────
            if (_cache.ActiveWars != null && _cache.ActiveWars.Count > 0)
            {
                var warParts = _cache.ActiveWars.Values.Select(war =>
                {
                    int duration = _cache.CurrentTurn - war.StartTurn;
                    string dur = duration > 0 ? $", {duration}t" : "";
                    return $"{war.ActorName} vs {war.TargetName} [{war.CasusBelli}{dur}]";
                });
                context += $"- Wars: {string.Join("; ", warParts)}\n";
            }

            // ── Factions ──────────────────────────────────────────────────────────
            if (_cache.Factions != null && _cache.Factions.Count > 0)
            {
                var eliminated = _cache.EliminatedFactions ?? new List<string>();
                var notable = _cache.Factions
                    .Where(kv => !eliminated.Contains(kv.Key)
                               && !string.IsNullOrEmpty(kv.Value.Tag)
                               && kv.Value.Tag != "Neutral"
                               && !string.IsNullOrEmpty(kv.Value.Name))
                    .OrderByDescending(kv => kv.Value.Resources)
                    .Take(4)
                    .Select(kv =>
                    {
                        var f = kv.Value;
                        string regions = f.ClaimedPlaceUuids?.Count > 0 ? $", {f.ClaimedPlaceUuids.Count}r" : "";
                        return $"{f.Name} [{f.Tag}{regions}]";
                    });
                if (notable.Any())
                    context += $"- Factions: {string.Join(", ", notable)}\n";
            }

            // ── Recent Events (non-MAJOR, non-ECONOMY, non-SEASON) ────────────────
            if (_cache.Events != null && _cache.Events.Count > 0)
            {
                var recent = _cache.Events
                    .Where(e => e.Type != "MAJOR" && e.Type != "ECONOMY" && e.Type != "SEASON")
                    .OrderByDescending(e => e.Turn)
                    .Take(3)
                    .Select(e => e.Description);
                if (recent.Any())
                    context += $"- Events: {string.Join("; ", recent)}\n";
            }

            // ── Major History (last 2) ────────────────────────────────────────────
            if (_cache.MajorEventHistory != null && _cache.MajorEventHistory.Count > 0)
            {
                var majors = _cache.MajorEventHistory
                    .Skip(Math.Max(0, _cache.MajorEventHistory.Count - 2));
                context += $"- History: {string.Join("; ", majors)}\n";
            }

            if (string.IsNullOrEmpty(context)) return "";

            string turnStr = _cache.CurrentTurn > 0 ? $" — Turn {_cache.CurrentTurn}" : "";
            return $"\n[WORLD STATE{turnStr}]\n{context}";
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
