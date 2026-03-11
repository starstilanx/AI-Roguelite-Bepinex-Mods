using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    public enum DiplomaticTier
    {
        War          = -3,
        Hostile      = -2,
        ColdWar      = -1,
        Neutral      =  0,
        NonAggression =  1,
        TradePact    =  2,
        Alliance     =  3,
    }

    [Serializable]
    public class DiplomaticRelation
    {
        public int    Tier              = (int)DiplomaticTier.Neutral;
        public int    TierChangedTurn   = 0;
        public string TierChangeReason  = "";
        // Names stored at creation so the UI can display them without UUID lookups
        public string FactionAName      = "";
        public string FactionBName      = "";
    }

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
                File.WriteAllText(path, JsonConvert.SerializeObject(CurrentState, Formatting.Indented));
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
                    CurrentState = JsonConvert.DeserializeObject<WorldState>(File.ReadAllText(path)) ?? new WorldState();
                    EnsureCollections(CurrentState);
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

        // Ensures all collections are non-null after deserialization (handles old saves missing new fields)
        private static void EnsureCollections(WorldState s)
        {
            if (s.Factions == null)             s.Factions             = new Dictionary<string, FactionExtData>();
            if (s.Events == null)               s.Events               = new List<WorldEvent>();
            if (s.FactionRelationships == null) s.FactionRelationships = new Dictionary<string, string>();
            if (s.Market == null)               s.Market               = new MarketState();
            if (s.LoreExtras == null)           s.LoreExtras           = new Dictionary<string, LoreExtraData>();
            if (s.ActiveWars == null)           s.ActiveWars           = new Dictionary<string, WarDeclaration>();
            if (s.GrievanceCounts == null)      s.GrievanceCounts      = new Dictionary<string, int>();
            if (s.EliminatedFactions == null)   s.EliminatedFactions   = new HashSet<string>();
            if (s.MajorEventHistory == null)    s.MajorEventHistory    = new List<string>();
            if (s.DiplomaticRelations == null)  s.DiplomaticRelations  = new Dictionary<string, DiplomaticRelation>();

            // Migrate legacy FactionRelationships → DiplomaticRelations for old saves
            foreach (var kvp in s.FactionRelationships)
            {
                if (s.DiplomaticRelations.ContainsKey(kvp.Key)) continue;
                DiplomaticTier tier = DiplomaticTier.Neutral;
                if (kvp.Value == "ALLIES")  tier = DiplomaticTier.Alliance;
                else if (kvp.Value == "ENEMIES") tier = DiplomaticTier.Hostile;
                s.DiplomaticRelations[kvp.Key] = new DiplomaticRelation { Tier = (int)tier };
            }
        }

        public static FactionExtData GetFactionData(string factionUuid)
        {
            if (!CurrentState.Factions.ContainsKey(factionUuid))
                CurrentState.Factions[factionUuid] = new FactionExtData();
            return CurrentState.Factions[factionUuid];
        }

        public static void LogEvent(string desc, string type)
        {
            CurrentState.Events.Add(new WorldEvent { Turn = CurrentState.CurrentTurn, Description = desc, Type = type });
            if (CurrentState.Events.Count > 150)
                CurrentState.Events.RemoveAt(0);
        }

        public static LoreExtraData GetLoreExtra(Lorebook.LoreEntry entry)
        {
            if (entry == null) return null;
            string key = $"{entry.ToUiKeysStr()}_{entry.val}";
            if (!CurrentState.LoreExtras.ContainsKey(key))
                CurrentState.LoreExtras[key] = new LoreExtraData();
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

        // Deterministic pair key, sorted so A↔B == B↔A
        public static string GetRelationshipKey(string uuidA, string uuidB)
        {
            return string.Compare(uuidA, uuidB) < 0 ? $"{uuidA}_{uuidB}" : $"{uuidB}_{uuidA}";
        }

        public static int GetGrievance(string pairKey) =>
            CurrentState.GrievanceCounts.TryGetValue(pairKey, out int v) ? v : 0;

        public static void AddGrievance(string pairKey, int amount = 1)
        {
            if (!CurrentState.GrievanceCounts.ContainsKey(pairKey))
                CurrentState.GrievanceCounts[pairKey] = 0;
            CurrentState.GrievanceCounts[pairKey] += amount;
        }

        // ─── Diplomatic Tier API ──────────────────────────────────────────────────

        public static DiplomaticTier GetTier(string pairKey)
        {
            if (CurrentState.DiplomaticRelations.TryGetValue(pairKey, out var rel))
                return (DiplomaticTier)rel.Tier;
            return DiplomaticTier.Neutral;
        }

        public static void SetTier(string pairKey, DiplomaticTier tier, string reason, int turn,
            string factionAName = null, string factionBName = null)
        {
            if (!CurrentState.DiplomaticRelations.ContainsKey(pairKey))
                CurrentState.DiplomaticRelations[pairKey] = new DiplomaticRelation();
            var rel = CurrentState.DiplomaticRelations[pairKey];
            rel.Tier             = (int)tier;
            rel.TierChangedTurn  = turn;
            rel.TierChangeReason = reason;
            if (factionAName != null) rel.FactionAName = factionAName;
            if (factionBName != null) rel.FactionBName = factionBName;
        }

        /// Shifts the tier by delta, clamped to [War, Alliance]. Returns true if tier actually changed.
        public static bool ShiftTier(string pairKey, int delta, string reason,
            string factionAName = null, string factionBName = null)
        {
            if (!CurrentState.DiplomaticRelations.ContainsKey(pairKey))
                CurrentState.DiplomaticRelations[pairKey] = new DiplomaticRelation();
            var rel = CurrentState.DiplomaticRelations[pairKey];
            int clamped = Math.Max((int)DiplomaticTier.War,
                          Math.Min((int)DiplomaticTier.Alliance, rel.Tier + delta));
            if (clamped == rel.Tier) return false;
            rel.Tier             = clamped;
            rel.TierChangedTurn  = CurrentState.CurrentTurn;
            rel.TierChangeReason = reason;
            if (factionAName != null) rel.FactionAName = factionAName;
            if (factionBName != null) rel.FactionBName = factionBName;
            return true;
        }

        public static string GetTierLabel(DiplomaticTier tier)
        {
            switch (tier)
            {
                case DiplomaticTier.War:           return "At War";
                case DiplomaticTier.Hostile:       return "Hostile";
                case DiplomaticTier.ColdWar:       return "Cold War";
                case DiplomaticTier.Neutral:       return "Neutral";
                case DiplomaticTier.NonAggression: return "Non-Aggression";
                case DiplomaticTier.TradePact:     return "Trade Pact";
                case DiplomaticTier.Alliance:      return "Alliance";
                default:                           return "Unknown";
            }
        }

        public static string GetTierIcon(DiplomaticTier tier)
        {
            switch (tier)
            {
                case DiplomaticTier.War:           return "⚔";
                case DiplomaticTier.Hostile:       return "🗡";
                case DiplomaticTier.ColdWar:       return "❄";
                case DiplomaticTier.Neutral:       return "·";
                case DiplomaticTier.NonAggression: return "🤝";
                case DiplomaticTier.TradePact:     return "💰";
                case DiplomaticTier.Alliance:      return "★";
                default:                           return "?";
            }
        }

        // ─── War API ──────────────────────────────────────────────────────────────

        public static void DeclareWar(string actorUuid, string actorName, string targetUuid, string targetName, string casusBelli)
        {
            string key = GetRelationshipKey(actorUuid, targetUuid);
            if (CurrentState.ActiveWars.ContainsKey(key)) return;

            CurrentState.ActiveWars[key] = new WarDeclaration
            {
                ActorUuid  = actorUuid,
                ActorName  = actorName,
                TargetUuid = targetUuid,
                TargetName = targetName,
                CasusBelli = casusBelli,
                StartTurn  = CurrentState.CurrentTurn
            };
            SetTier(key, DiplomaticTier.War, "war declared", CurrentState.CurrentTurn, actorName, targetName);
            LogEvent($"{actorName} has formally declared war on {targetName}! Casus belli: {casusBelli}.", "WAR");
        }

        public static void EndWar(string key, string reason)
        {
            if (!CurrentState.ActiveWars.TryGetValue(key, out var war)) return;
            LogEvent($"The war between {war.ActorName} and {war.TargetName} has ended — {reason}.", "WAR");
            CurrentState.ActiveWars.Remove(key);
            if (CurrentState.GrievanceCounts.ContainsKey(key))
                CurrentState.GrievanceCounts[key] = 0;
            SetTier(key, DiplomaticTier.Hostile, "post-war hostility", CurrentState.CurrentTurn, war.ActorName, war.TargetName);
            LogEvent($"{war.ActorName} and {war.TargetName} relations settled at: {GetTierLabel(DiplomaticTier.Hostile)}.", "DIPLOMACY");
        }
    }

    [Serializable]
    public class WorldState
    {
        public int    CurrentTurn             = 0;
        public string CurrentSeason           = "Spring";
        public int    SeasonTurnCounter       = 0;
        public bool   TerritoriesInitialized  = false;

        public Dictionary<string, FactionExtData> Factions           = new Dictionary<string, FactionExtData>();
        public List<WorldEvent>                    Events             = new List<WorldEvent>();
        public MarketState                         Market             = new MarketState();
        public Dictionary<string, LoreExtraData>  LoreExtras         = new Dictionary<string, LoreExtraData>();
        public Dictionary<string, WarDeclaration> ActiveWars         = new Dictionary<string, WarDeclaration>();
        public Dictionary<string, int>            GrievanceCounts    = new Dictionary<string, int>();
        public HashSet<string>                    EliminatedFactions = new HashSet<string>();
        public int                                NextMajorEventTurn = 50;
        public List<string>                       MajorEventHistory  = new List<string>();

        // Diplomatic tier system (replaces FactionRelationships for new saves)
        public Dictionary<string, DiplomaticRelation> DiplomaticRelations = new Dictionary<string, DiplomaticRelation>();

        // Legacy: kept for save-file backward compatibility only; new code writes DiplomaticRelations
        public Dictionary<string, string> FactionRelationships = new Dictionary<string, string>();
    }

    [Serializable]
    public class WarDeclaration
    {
        public string ActorUuid;
        public string ActorName;
        public string TargetUuid;
        public string TargetName;
        public string CasusBelli;
        public int    StartTurn;
    }

    [Serializable]
    public class LoreExtraData
    {
        public string Category  = "General";
        public string ImageUuid = "";
    }

    [Serializable]
    public class MarketState
    {
        public string GlobalCondition   = "Normal";
        public string PreviousCondition = "Normal";
        public float  PriceMultiplier   = 1.0f;
        public float  SellMultiplier    = 1.0f;
        public Dictionary<string, float> ItemTypeModifiers = new Dictionary<string, float>();
    }

    [Serializable]
    public class FactionExtData
    {
        public string       Name              = "";
        public int          Resources         = 100;
        public List<string> ClaimedPlaceUuids = new List<string>();
        public string       Tag               = "Neutral";
        // Population tracker
        public int          Population        = 500;
        public string       PopState          = "Normal"; // Thriving, Normal, Struggling, Razed
    }

    [Serializable]
    public class WorldEvent
    {
        public int    Turn;
        public string Description;
        public string Type;
        public long   Timestamp = DateTime.Now.Ticks;

        public string GetFormatted() => $"Turn {Turn}: {Description}";
    }
}
