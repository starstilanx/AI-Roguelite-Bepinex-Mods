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
            public List<PendingWorldEventStub> PendingWorldEvents;
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

        private class PendingWorldEventStub
        {
            public string Description;
            public string Type;
            public int    TurnAdded;
            public int    TtlTurns;
        }
#pragma warning restore 0649

        private WorldStateStub _cache;
        private float _lastLoadTime;
        private const float CACHE_REFRESH_RATE = 8f;

        public string GetContext(string prompt, int maxTokens)
        {
            RefreshCacheIfNeeded();
            if (_cache == null) return "";

            string data = "";

            // ── Season + Economy ─────────────────────────────────────────────────
            string seasonPart = !string.IsNullOrEmpty(_cache.CurrentSeason) ? _cache.CurrentSeason : "";
            string econPart   = "";
            if (_cache.Market != null && !string.IsNullOrEmpty(_cache.Market.GlobalCondition))
            {
                econPart = _cache.Market.GlobalCondition;
                if (_cache.Market.PriceMultiplier != 1.0f || _cache.Market.SellMultiplier != 1.0f)
                    econPart += $" (×{_cache.Market.PriceMultiplier:0.##} buy, ×{_cache.Market.SellMultiplier:0.##} sell)";
            }
            if (!string.IsNullOrEmpty(seasonPart) || !string.IsNullOrEmpty(econPart))
                data += $"Season: {seasonPart} | Economy: {econPart}\n";

            // ── Active Wars ───────────────────────────────────────────────────────
            if (_cache.ActiveWars != null && _cache.ActiveWars.Count > 0)
            {
                var warParts = _cache.ActiveWars.Values.Select(war =>
                {
                    int duration = _cache.CurrentTurn - war.StartTurn;
                    return $"{war.ActorName} vs {war.TargetName} [{war.CasusBelli}, {duration}t]";
                });
                data += $"Active Wars: {string.Join("; ", warParts)}\n";
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
                    data += $"Factions: {string.Join(", ", notable)}\n";
            }

            // ── Recent Events ─────────────────────────────────────────────────────
            if (_cache.Events != null && _cache.Events.Count > 0)
            {
                var recent = _cache.Events
                    .Where(e => e.Type != "MAJOR" && e.Type != "ECONOMY" && e.Type != "SEASON")
                    .OrderByDescending(e => e.Turn)
                    .Take(3)
                    .Select(e => e.Description);
                if (recent.Any())
                    data += $"Recent: {string.Join("; ", recent)}\n";
            }

            // ── Major History ─────────────────────────────────────────────────────
            if (_cache.MajorEventHistory != null && _cache.MajorEventHistory.Count > 0)
            {
                var majors = _cache.MajorEventHistory
                    .Skip(Math.Max(0, _cache.MajorEventHistory.Count - 2));
                data += $"World Events: {string.Join(" | ", majors)}\n";
            }

            // ── Narrative Directive ───────────────────────────────────────────────
            var directives = new List<string>();

            if (_cache.Market != null)
            {
                switch (_cache.Market.GlobalCondition)
                {
                    case "Shortage":   directives.Add("goods are scarce — merchants stressed, shelves thin"); break;
                    case "Surplus":    directives.Add("markets overflow — traders jovial, deals easy to find"); break;
                    case "Inflation":  directives.Add("coin flows but buys less — costs higher than expected"); break;
                    case "Depression": directives.Add("economic collapse — poverty and desperation are visible"); break;
                }
            }

            if (_cache.ActiveWars != null && _cache.ActiveWars.Count > 0)
            {
                string warNames = string.Join(", ", _cache.ActiveWars.Values.Select(w => $"{w.ActorName} vs {w.TargetName}"));
                directives.Add($"ongoing war ({warNames}) — show conscription fear, refugees, battle anxiety in NPCs");
            }

            switch (_cache.CurrentSeason)
            {
                case "Winter": directives.Add("bitter winter — cold limits travel and comfort, shapes every scene"); break;
                case "Autumn": directives.Add("harvest season — abundance tempered by looming winter"); break;
                case "Summer": directives.Add("long summer days — easier travel, higher spirits overall"); break;
            }

            string guidance = "";
            if (directives.Count > 0)
            {
                guidance = "\n[WORLD NARRATIVE GUIDANCE — weave into scene, show don't tell]\n";
                foreach (var d in directives)
                    guidance += $"• {d}\n";
            }

            // ── Pending World Alert ───────────────────────────────────────────────
            string alert = "";
            if (_cache.PendingWorldEvents != null && _cache.PendingWorldEvents.Count > 0)
            {
                var active = _cache.PendingWorldEvents
                    .Where(e => e.TurnAdded + e.TtlTurns >= _cache.CurrentTurn)
                    .OrderByDescending(e => e.TurnAdded)
                    .FirstOrDefault();
                if (active != null)
                    alert = $"\n[WORLD ALERT — {active.Type.Replace("_", " ")}: {active.Description}]\n" +
                            "Mention this naturally in the scene if any NPC would plausibly know about it.\n";
            }

            string context = data + guidance + alert;
            if (string.IsNullOrEmpty(context.Trim())) return "";

            string turnStr = _cache.CurrentTurn > 0 ? $" — Turn {_cache.CurrentTurn}" : "";
            string result = $"[WORLD STATE{turnStr}]\n{context}";

            int maxChars = maxTokens * 4;
            if (result.Length > maxChars)
                result = result.Substring(0, maxChars) + "...";

            return "\n" + result;
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
