using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GrandStrategy
{
    public class GrandStrategyData
    {
        public static DominionState State = new DominionState();

        public static void Reset()
        {
            State = new DominionState();
        }

        public static void Save(string dir, bool quiet = false)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "grand_strategy_data.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(State, Formatting.Indented));
                if (!quiet) Debug.Log($"[GrandStrategy] Saved dominion data to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GrandStrategy] Failed to save dominion data: {e.Message}");
            }
        }

        // Persists to the active save slot. GenContext's provider reads the JSON from disk,
        // so this must be called whenever dominion state changes mid-session (not just on game save).
        public static void SaveToCurrentDir(bool quiet = true)
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
            Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg), quiet);
        }

        public static void Load(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "grand_strategy_data.json");
                if (File.Exists(path))
                {
                    State = JsonConvert.DeserializeObject<DominionState>(File.ReadAllText(path)) ?? new DominionState();
                    EnsureCollections(State);
                    Debug.Log($"[GrandStrategy] Loaded dominion data from {path}");
                }
                else
                {
                    State = new DominionState();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GrandStrategy] Failed to load dominion data: {e.Message}");
                State = new DominionState();
            }
        }

        // Ensures all collections are non-null after deserialization (handles old saves missing new fields)
        private static void EnsureCollections(DominionState s)
        {
            if (s.Holdings == null)           s.Holdings           = new Dictionary<string, HoldingData>();
            if (s.Advisors == null)           s.Advisors           = new List<Advisor>();
            if (s.CasusBelli == null)         s.CasusBelli         = new HashSet<string>();
            if (s.VassalFactionUuids == null) s.VassalFactionUuids = new HashSet<string>();
            if (s.VassalNames == null)        s.VassalNames        = new Dictionary<string, string>();
            if (s.Wonders == null)            s.Wonders            = new List<string>();
            if (string.IsNullOrEmpty(s.TaxPolicy)) s.TaxPolicy     = "NORMAL";
            if (s.Deeds == null)              s.Deeds              = new List<DominionDeed>();
            if (s.CasusBelliExpiry == null)   s.CasusBelliExpiry   = new Dictionary<string, int>();
            foreach (var h in s.Holdings.Values)
                if (h.Improvements == null) h.Improvements = new List<string>();
        }

        public static HoldingData GetHolding(string placeUuid)
        {
            HoldingData h;
            return State.Holdings.TryGetValue(placeUuid, out h) ? h : null;
        }

        public static void LogDeed(string desc)
        {
            State.Deeds.Add(new DominionDeed { Turn = WorldExpansionTurn(), Description = desc });
            while (State.Deeds.Count > 50)
                State.Deeds.RemoveAt(0);
        }

        public static int WorldExpansionTurn()
        {
            return AIROG_WorldExpansion.WorldData.CurrentState != null
                ? AIROG_WorldExpansion.WorldData.CurrentState.CurrentTurn : 0;
        }

        // ── Chronicle integration (soft dependency via reflection, same pattern as AIROG_Insight) ──
        // Milestone dominion beats (founding, victory, wonders, vassalage) get recorded into
        // Chronicle's chapter history if the mod is installed; a no-op otherwise.
        public static void TryRecordChronicleBeat(string summary)
        {
            try
            {
                var mgrType = Type.GetType("AIROG_Chronicle.ChronicleManager, AIROG_Chronicle");
                var beatType = Type.GetType("AIROG_Chronicle.ChronicleBeat, AIROG_Chronicle");
                if (mgrType == null || beatType == null) return;

                int turn = 0;
                var state = mgrType.GetProperty("State")?.GetValue(null);
                if (state != null)
                    turn = (int)(state.GetType().GetProperty("GlobalTurn")?.GetValue(state) ?? 0);

                var beat = Activator.CreateInstance(beatType);
                beatType.GetProperty("Turn")?.SetValue(beat, turn);
                beatType.GetProperty("Summary")?.SetValue(beat, summary);
                beatType.GetProperty("IsMilestone")?.SetValue(beat, true);
                mgrType.GetMethod("RecordBeat")?.Invoke(null, new[] { beat });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GrandStrategy] Chronicle beat integration failed: {e.Message}");
            }
        }
    }

    [Serializable]
    public class DominionState
    {
        public bool   Founded          = false;
        public string DominionName     = "";
        public string FactionUuid      = "";  // native "Player" faction uuid, bridged into WorldExpansion's sim
        public string CapitalPlaceUuid = "";
        public string CapitalName      = "";
        public int    FoundedTurn      = 0;

        public int Treasury         = 50;
        public int ArmyStrength     = 10;
        public int CommandPoints    = 2;
        public int MaxCommandPoints = 3;

        // placeUuid → holding detail (names cached at claim time so UIs/providers avoid uuid lookups)
        public Dictionary<string, HoldingData> Holdings = new Dictionary<string, HoldingData>();

        public List<Advisor>   Advisors           = new List<Advisor>();      // recruited council members
        public HashSet<string> CasusBelli         = new HashSet<string>();    // faction uuids we hold claims against
        public Dictionary<string, int> CasusBelliExpiry = new Dictionary<string, int>(); // uuid → WorldExpansion turn the claim goes stale
        public HashSet<string> VassalFactionUuids = new HashSet<string>();
        public Dictionary<string, string> VassalNames = new Dictionary<string, string>(); // uuid → name, cached at vassalization

        public string TaxPolicy = "NORMAL";  // LOW | NORMAL | HIGH — persistent edict, applied every tick

        public List<string> Wonders          = new List<string>(); // completed great works (capital)
        public string       WonderInProgress = "";
        public int          WonderTicksLeft  = 0;

        public PetitionData PendingPetition = null; // at most one open petition at a time

        public List<DominionDeed> Deeds = new List<DominionDeed>();

        public string ActiveVictory = "";  // "", DOMINATION, HEGEMONY, GOLDEN_AGE
    }

    [Serializable]
    public class HoldingData
    {
        public string       Name         = "";
        public List<string> Improvements = new List<string>();
        public int          Unrest       = 0;
        public bool         IsCapital    = false;
    }

    [Serializable]
    public class Advisor
    {
        public string Role        = "";  // MARSHAL, STEWARD, SPYMASTER, CHANCELLOR
        public string Name        = "";
        public string Personality = "";
        public int    Loyalty     = 50;
        public string LastReport  = "";
    }

    [Serializable]
    public class DominionDeed
    {
        public int    Turn;
        public string Description;
    }

    // A court dilemma awaiting the sovereign's judgment. Effect deltas are resolved at
    // generation time so accept/reject is a flat application, no re-lookup needed.
    [Serializable]
    public class PetitionData
    {
        public string Text        = "";  // the petition as presented to the player
        public string AcceptText  = "";  // narrative outcome on accept
        public string RejectText  = "";  // narrative outcome on reject
        public int AcceptGold;           // deltas applied to treasury / all-holdings unrest / army
        public int AcceptUnrest;
        public int AcceptArmy;
        public int RejectGold;
        public int RejectUnrest;
        public int RejectArmy;
        public int ExpiresTurn;          // WorldExpansion turn after which the petition lapses (ignored = unrest)
        public string Role = "";         // MARSHAL/STEWARD/SPYMASTER/CHANCELLOR — biases selection toward a matching advisor
    }
}
