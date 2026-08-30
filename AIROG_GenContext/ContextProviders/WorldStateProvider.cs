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
            public Dictionary<string, DiplomaticRelationStub> DiplomaticRelations;
            public List<string> PlayerBounties; // HashSet serializes as array
        }

        private class DiplomaticRelationStub
        {
            public int    Tier;
            public int    TierChangedTurn;
            public string FactionAName;
            public string FactionBName;
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
            public int Population;
            public string PopState;
            public FactionFigureStub Leader;
            public List<FactionFigureStub> Lieutenants;
        }

        private class FactionFigureStub
        {
            public string Name;
            public string Title;
            public string Role;
            public string Trait;
            public bool   IsDead;
        }

        private class WorldEventStub
        {
            public string Description;
            public string Type;
            public int Turn;
        }

        private class WarDeclarationStub
        {
            public string ActorUuid;
            public string ActorName;
            public string TargetUuid;
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
                        string pop = !string.IsNullOrEmpty(f.PopState) && f.PopState != "Normal"
                            ? $", {f.PopState.ToLower()}" : "";
                        string led = LeaderStr(f);
                        return $"{f.Name} [{f.Tag}{regions}{pop}]{led}";
                    });
                if (notable.Any())
                    data += $"Factions: {string.Join(", ", notable)}\n";
            }

            // ── Diplomacy (non-neutral, non-war pacts and rivalries) ──────────────
            if (_cache.DiplomaticRelations != null && _cache.DiplomaticRelations.Count > 0)
            {
                var eliminatedForDiplomacy = _cache.EliminatedFactions ?? new List<string>();
                var pacts = _cache.DiplomaticRelations
                    // Pair key is "uuidA_uuidB" (WorldData.GetRelationshipKey) — skip relations
                    // where either side has fallen, so the AI doesn't treat a dead faction's
                    // stale Alliance/Hostile tier as still real (drift back to Neutral otherwise
                    // takes ~90 turns)
                    .Where(kv =>
                    {
                        var parts = kv.Key.Split('_');
                        return parts.Length != 2
                            || (!eliminatedForDiplomacy.Contains(parts[0]) && !eliminatedForDiplomacy.Contains(parts[1]));
                    })
                    .Select(kv => kv.Value)
                    .Where(r => r.Tier != 0 && r.Tier != -3
                             && !string.IsNullOrEmpty(r.FactionAName) && !string.IsNullOrEmpty(r.FactionBName))
                    .OrderByDescending(r => r.TierChangedTurn)
                    .Take(4)
                    .Select(r => $"{r.FactionAName} & {r.FactionBName}: {TierLabel(r.Tier)}");
                if (pacts.Any())
                    data += $"Diplomacy: {string.Join("; ", pacts)}\n";
            }

            // ── Current Location territory (live game state, not the JSON) ───────
            string locationDirective = null;
            try
            {
                var curPlace = SS.I?.hackyManager?.currentPlace;
                var topPlace = curPlace?.GetTopLvlPlace() ?? curPlace;
                var owner    = topPlace?.faction ?? curPlace?.faction;
                if (owner != null && topPlace != null)
                {
                    string line = $"Current Location: {topPlace.GetPrettyName()} — territory of {owner.GetPrettyName()} (disposition toward player: {owner.GetStandingPromptStr()})";
                    if (_cache.Factions != null && _cache.Factions.TryGetValue(owner.uuid, out var ownerData))
                    {
                        var l = ownerData.Leader;
                        if (l != null && !l.IsDead && !string.IsNullOrEmpty(l.Name))
                            line += $", ruled by {FigureDisplay(l)}{(string.IsNullOrEmpty(l.Trait) ? "" : $" ({l.Trait})")}";
                    }
                    var warHere = _cache.ActiveWars?.Values.FirstOrDefault(
                        w => w.ActorUuid == owner.uuid || w.TargetUuid == owner.uuid);
                    if (warHere != null)
                    {
                        string enemy = warHere.ActorUuid == owner.uuid ? warHere.TargetName : warHere.ActorName;
                        line += $", currently at war with {enemy}";
                        locationDirective = "this land is at war — patrols, checkpoints, conscription notices, wary locals";
                    }
                    data += line + "\n";
                }
            }
            catch { /* location context is best-effort; never break prompt building */ }

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

            if (locationDirective != null)
                directives.Add(locationDirective);

            // Faction courts exist — the named figures are real, persistent characters
            if (_cache.Factions != null && _cache.Factions.Values.Any(
                    f => f?.Leader != null && !string.IsNullOrEmpty(f.Leader.Name)))
                directives.Add("faction leaders/lieutenants named above are real characters in this world — NPCs know of them, they can appear in person, and their names/titles must stay consistent");

            // Active bounties on the player make the world dangerous in specific ways
            if (_cache.PlayerBounties != null && _cache.PlayerBounties.Count > 0 && _cache.Factions != null)
            {
                var bountyNames = _cache.PlayerBounties
                    .Select(u => _cache.Factions.TryGetValue(u, out var f) && !string.IsNullOrEmpty(f.Name) ? f.Name : null)
                    .Where(n => n != null)
                    .Take(3)
                    .ToList();
                if (bountyNames.Count > 0)
                    directives.Add($"ACTIVE BOUNTY on the player from {string.Join(", ", bountyNames)} — bounty hunters, informants, and ambushes are fair game");
            }

            string guidance = "";
            if (directives.Count > 0)
            {
                guidance = "\n[WORLD NARRATIVE GUIDANCE — weave into scene, show don't tell]\n";
                foreach (var d in directives)
                    guidance += $"• {d}\n";
            }

            // ── Pending World Alerts (up to 2 newest still-live events) ───────────
            string alert = "";
            if (_cache.PendingWorldEvents != null && _cache.PendingWorldEvents.Count > 0)
            {
                var active = _cache.PendingWorldEvents
                    .Where(e => e.TurnAdded + e.TtlTurns >= _cache.CurrentTurn)
                    .OrderByDescending(e => e.TurnAdded)
                    .Take(2)
                    .ToList();
                if (active.Count > 0)
                {
                    foreach (var evt in active)
                        alert += $"\n[WORLD ALERT — {evt.Type.Replace("_", " ")}: {evt.Description}]";
                    alert += "\nMention these naturally in the scene if any NPC would plausibly know about them.\n";
                }
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

        private static string FigureDisplay(FactionFigureStub f) =>
            string.IsNullOrEmpty(f.Title) ? f.Name : $"{f.Title} {f.Name}";

        // ", led by Warlord Kessek (ruthless)" or "" when the faction has no living leader
        private static string LeaderStr(FactionExtDataStub f)
        {
            var l = f.Leader;
            if (l == null || l.IsDead || string.IsNullOrEmpty(l.Name)) return "";
            string trait = !string.IsNullOrEmpty(l.Trait) ? $" ({l.Trait})" : "";
            return $", led by {FigureDisplay(l)}{trait}";
        }

        private static string TierLabel(int tier)
        {
            switch (tier)
            {
                case -3: return "At War";
                case -2: return "Hostile";
                case -1: return "Cold War";
                case  1: return "Non-Aggression Pact";
                case  2: return "Trade Pact";
                case  3: return "Alliance";
                default: return "Neutral";
            }
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
