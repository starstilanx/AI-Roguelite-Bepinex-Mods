using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// Persistent A-Life state: the virtual squad population, and the ring buffer of
    /// offline events (battles, raids, migrations) that the GenContext provider surfaces
    /// as aftermath when the player arrives somewhere. Saved to alife_data.json in the
    /// active save dir (WorldExpansion pattern: written every turn tick, not just on save).
    /// </summary>
    public static class ALifeData
    {
        public const string FILE_NAME = "alife_data.json";
        private const int MAX_EVENTS = 80;

        public static ALifeState State = new ALifeState();

        public static void Reset()
        {
            State = new ALifeState();
            Debug.Log("[ALife] State reset.");
        }

        public static void Save(string saveDir)
        {
            try
            {
                if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir)) return;
                File.WriteAllText(Path.Combine(saveDir, FILE_NAME),
                    JsonConvert.SerializeObject(State, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ALife] Save failed: " + ex.Message);
            }
        }

        public static void SaveToCurrentDir()
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        public static void Load(string saveDir)
        {
            try
            {
                string path = Path.Combine(saveDir, FILE_NAME);
                if (!File.Exists(path))
                {
                    Reset();
                    return;
                }
                State = JsonConvert.DeserializeObject<ALifeState>(File.ReadAllText(path)) ?? new ALifeState();
                EnsureCollections(State);
                Debug.Log($"[ALife] Loaded: {State.Squads.Count} squads, {State.RecentEvents.Count} events, turn {State.CurrentTurn}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ALife] Load failed, resetting: " + ex.Message);
                Reset();
            }
        }

        private static void EnsureCollections(ALifeState s)
        {
            if (s.Squads == null) s.Squads = new List<VirtualSquad>();
            if (s.RecentEvents == null) s.RecentEvents = new List<ALifeEvent>();
            if (s.DreadMap == null) s.DreadMap = new Dictionary<string, int>();
            if (s.DreadNames == null) s.DreadNames = new Dictionary<string, string>();
            if (s.WarScores == null) s.WarScores = new Dictionary<string, int>();
            if (s.WarSeizures == null) s.WarSeizures = new Dictionary<string, int>();
            if (s.Knowledge == null) s.Knowledge = new Dictionary<string, SquadKnowledge>();
            foreach (var sq in s.Squads)
            {
                if (sq.MemberUuids == null) sq.MemberUuids = new List<string>();
                if (sq.Feuds == null) sq.Feuds = new List<FeudRecord>();
                if (sq.Chronicle == null) sq.Chronicle = new List<string>();
                // v1.0 saves have leaderless squads — give every squad a face.
                if (sq.Leader == null) sq.Leader = ALifeNames.MakeLeader(sq);
            }
            // Pre-v2.3 legend was uncapped and barely decayed, so long runs carry values
            // far past the top tier that would take hundreds of turns to shed.
            s.PlayerLegend = Mathf.Clamp(s.PlayerLegend, 0, ALifeLegend.LEGEND_MAX);
            PruneDuplicateEncounters(s);
        }

        /// <summary>
        /// v2.3 repair pass for saves written by earlier builds. Those re-announced a
        /// band's arrival on EVERY location change, so a single standoff could leave
        /// dozens of identical "is here — and hostile" notices in the feed — flooding
        /// the Whispers tab and, worse, the narrative prompt. Keep the newest telling
        /// of each and drop the rest.
        /// </summary>
        private static void PruneDuplicateEncounters(ALifeState s)
        {
            var seen = new HashSet<string>();
            var kept = new List<ALifeEvent>();
            for (int i = s.RecentEvents.Count - 1; i >= 0; i--)
            {
                ALifeEvent e = s.RecentEvents[i];
                if (e.Type == "ENCOUNTER" && !seen.Add(e.PlaceUuid + "|" + e.Description)) continue;
                kept.Add(e);
            }
            if (kept.Count == s.RecentEvents.Count) return;
            kept.Reverse();
            Debug.Log($"[ALife] Pruned {s.RecentEvents.Count - kept.Count} duplicate encounter notices from the feed.");
            s.RecentEvents = kept;
        }

        public static void LogEvent(string placeUuid, string placeName, string type, string desc)
        {
            State.RecentEvents.Add(new ALifeEvent
            {
                Turn = State.CurrentTurn,
                PlaceUuid = placeUuid,
                PlaceName = placeName,
                Type = type,
                Description = desc
            });
            if (State.RecentEvents.Count > MAX_EVENTS)
                State.RecentEvents.RemoveRange(0, State.RecentEvents.Count - MAX_EVENTS);
            Debug.Log($"[ALife] T{State.CurrentTurn} {type} @ {placeName}: {desc}");
        }

        /// <summary>
        /// Recent happenings at a place, oldest-first, for prompt injection.
        /// Repeats of the same line are collapsed to their most recent telling — a band
        /// standing in the road is one fact, not fifteen — and <paramref name="excludeType"/>
        /// drops a category entirely (the provider excludes ENCOUNTER, since who is
        /// standing here is already reported live rather than as aftermath).
        /// </summary>
        public static List<ALifeEvent> EventsAt(string placeUuid, int withinTurns, int max, string excludeType = null)
        {
            int cutoff = State.CurrentTurn - withinTurns;
            var list = State.RecentEvents
                .Where(e => e.PlaceUuid == placeUuid && e.Turn >= cutoff)
                .Where(e => excludeType == null || e.Type != excludeType)
                .ToList();

            var seen = new HashSet<string>();
            var deduped = new List<ALifeEvent>();
            for (int i = list.Count - 1; i >= 0; i--)
                if (seen.Add(list[i].Description))
                    deduped.Add(list[i]);
            deduped.Reverse();

            return deduped.Skip(Math.Max(0, deduped.Count - max)).ToList();
        }

        public static VirtualSquad SquadById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return State.Squads.FirstOrDefault(s => s.Id == id);
        }
    }

    public class ALifeState
    {
        public int CurrentTurn;
        public int NextSquadNum = 1;
        public List<VirtualSquad> Squads = new List<VirtualSquad>();
        public List<ALifeEvent> RecentEvents = new List<ALifeEvent>();

        // ── The Wake (v2.0): the world's reaction to the player ────────────────
        /// <summary>Last top-level place the player occupied — reconciliation anchor.</summary>
        public string LastPlayerTopUuid;
        /// <summary>Notoriety accrued from squad members killed around the player. Decays slowly.</summary>
        public int PlayerLegend;
        /// <summary>placeUuid → dread score. Places where the player has killed; squads route around them.</summary>
        public Dictionary<string, int> DreadMap = new Dictionary<string, int>();
        /// <summary>placeUuid → pretty name, for displaying dread zones after places prune.</summary>
        public Dictionary<string, string> DreadNames = new Dictionary<string, string>();

        // ── War Made Real (v2.1): the field decides WorldExpansion's wars ──────
        /// <summary>war key → net battle score. Positive favors the war's ActorUuid side.
        /// At ±3 the leading side pushes the front (seizes a territory).</summary>
        public Dictionary<string, int> WarScores = new Dictionary<string, int>();
        /// <summary>war key → net territories seized through squad warfare (signed like WarScores).
        /// At ±3, or when the loser is landless, the war ends decisively.</summary>
        public Dictionary<string, int> WarSeizures = new Dictionary<string, int>();

        // ── Whispers & Tracks (v2.2): what the PLAYER knows, fog-of-warred ─────
        /// <summary>squad Id → last intel the player has on it (sightings + rumors).</summary>
        public Dictionary<string, SquadKnowledge> Knowledge = new Dictionary<string, SquadKnowledge>();
    }

    /// <summary>
    /// v2.2: the player's (possibly stale) intel on one band. Only squads the player
    /// has met, or heard rumor of nearby, appear here — and the record freezes when
    /// the band moves out of earshot, so the Tracks lens shows last-known positions,
    /// not live ones.
    /// </summary>
    public class SquadKnowledge
    {
        public string SquadId;
        public string KnownName;
        public string KnownLeaderName;      // null until met or leader named in a rumor
        public string Archetype;
        public string LastKnownPlaceUuid;
        public string LastKnownPlaceName;
        public int LastKnownTurn;
        public int LastKnownSize;
        public string LastKnownActivity;
        public bool Met;                     // met face to face (full dossier unlocked)
    }

    /// <summary>Squad archetypes. Kept as string constants (not an enum) so the JSON stays forward-compatible.</summary>
    public static class SquadArchetype
    {
        public const string PATROL  = "PATROL";   // faction squad patrolling own territory
        public const string RAIDERS = "RAIDERS";  // faction outcasts/marauders, opportunistic hostiles
        public const string CARAVAN = "CARAVAN";  // traders shuttling between settlements
        public const string HUNTERS = "HUNTERS";  // wild pack / monsters, hostile to everyone
        public const string WARBAND = "WARBAND";  // war party heading for enemy territory
        public const string PILGRIMS = "PILGRIMS"; // neutral wanderers, pure flavor
    }

    public static class SquadGoal
    {
        public const string WANDER    = "WANDER";     // drift to random adjacent places
        public const string TRAVEL_TO = "TRAVEL_TO";  // head toward TargetPlaceUuid, then re-plan
        public const string RAID      = "RAID";       // head toward enemy place, raid on arrival
        public const string FLEE      = "FLEE";       // just lost a fight, running
        public const string TRADE     = "TRADE";      // shuttle between two places
        public const string HUNT      = "HUNT";       // v2.0: blood feud — tracking an enemy squad
        public const string GARRISON  = "GARRISON";   // v2.1: dug in, defending a place
    }

    /// <summary>
    /// v2.0: every squad has a leader — a persistent named figure with a record.
    /// Leaders earn epithets from deeds, die in battle, and are succeeded.
    /// </summary>
    public class SquadLeader
    {
        public string Name;
        public string Epithet = "";    // earned: "the Red", "Who Walked Away", "the Avenger"…
        public string Role;            // "war leader", "alpha", "caravan master"…
        public int Kills;              // enemies fallen to the squad under this leader
        public int Victories;
        public int Defeats;
        public string Uuid;            // real GameCharacter uuid while embodied, else null

        [JsonIgnore]
        public string FullName => string.IsNullOrEmpty(Epithet) ? Name : Name + " " + Epithet;
    }

    /// <summary>v2.0: a blood feud between two squads. Heat drives HUNT behavior; decays toward peace.</summary>
    public class FeudRecord
    {
        public string EnemySquadId;
        public string EnemySquadName;
        public int Heat;               // ≥50 → may take the HUNT goal
        public string Reason;
        public int StartedTurn;
    }

    public class VirtualSquad
    {
        public string Id;
        public string Name;            // e.g. "Redwater patrol", "the Gnashers"
        public string Archetype;       // SquadArchetype constant
        public string FactionUuid;     // null for HUNTERS/PILGRIMS
        public string FactionName;
        public int Size;               // living members
        public int AvgLevel;
        public int Morale = 100;       // drops on defeat; FLEE below 40
        public string CurrentPlaceUuid;
        public string CurrentPlaceName;
        public string GoalType = SquadGoal.WANDER;
        public string TargetPlaceUuid;
        public string TargetPlaceName;
        public string TargetSquadId;   // HUNT only: the feud enemy being tracked
        public string TradeHomeUuid;   // CARAVAN only: the other endpoint of the route
        public string HomePlaceUuid;   // where the squad formed; recruiting ground
        public int TurnsPerHop = 3;
        public int HopProgress;
        public int SpawnedTurn;
        public int LastEventTurn;
        public string Activity = "on the move"; // narrative snippet for prompt injection

        // ── v2.0 persistent identity ────────────────────────────────────────────
        public SquadLeader Leader;
        public int XP;                        // veterancy; levels up AvgLevel
        public List<FeudRecord> Feuds = new List<FeudRecord>();
        public List<string> Chronicle = new List<string>();  // squad history lines (cap 12)

        // ── v2.0 embodiment (two-way materialization) ───────────────────────────
        public bool IsEmbodied;               // members exist as real GameCharacters
        public List<string> MemberUuids = new List<string>(); // real entity uuids (incl. leader)
        public int DeathsThisVisit;           // deaths while sharing a place with the player
        public int LastRecruitTurn;

        // ── v2.0 regard toward the player (The Wake) ────────────────────────────
        public int FearOfPlayer;              // 0–100: ≥60 flees, 30–59 wary
        public int AweOfPlayer;               // 0–100: respect/goodwill from peaceful contact
        public bool MetPlayer;

        // ── v2.1 war made real ──────────────────────────────────────────────────
        public int GarrisonUntilTurn;         // GARRISON goal: dug in until this turn
        public string CourtFigureName;        // non-null: this squad is led by a faction-court lieutenant
        public string CourtFigureTitle;

        // ── v2.3 announcement / bubble bookkeeping ──────────────────────────────
        /// <summary>Turn this band's presence was last announced, so re-entering the
        /// same ground doesn't re-announce a standoff the player is already looking at.</summary>
        public int LastAnnouncedTurn = -999;
        /// <summary>Ground the last announcement was made on (paired with LastAnnouncedTurn).</summary>
        public string LastAnnouncedPlaceUuid;
        /// <summary>Turn the band became frozen in the player's online bubble, or -1 when
        /// it isn't. Bands that overstay walk out instead of being pinned there forever.</summary>
        public int BubbleSinceTurn = -1;

        [JsonIgnore]
        public int Strength => Math.Max(1, Size) * (AvgLevel + 2);

        public void AddChronicle(string line)
        {
            if (Chronicle == null) Chronicle = new List<string>();
            Chronicle.Add("T" + ALifeData.State.CurrentTurn + ": " + line);
            if (Chronicle.Count > 12) Chronicle.RemoveRange(0, Chronicle.Count - 12);
        }
    }

    public class ALifeEvent
    {
        public int Turn;
        public string PlaceUuid;
        public string PlaceName;
        public string Type;        // BATTLE, WIPE, RAID, MIGRATION, ENCOUNTER, SPAWN, FEUD, LEGEND, LIFECYCLE, WAR
        public string Description;
        /// <summary>v2.2: the player has heard of this (it happened near them, or they
        /// visited the site). Only Known events appear in the Rumors tab / Tracks lens.</summary>
        public bool Known;
    }
}
