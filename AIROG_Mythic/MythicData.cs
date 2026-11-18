using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Mythic
{
    /// <summary>
    /// Persistent Mythic state: the Chaos Factor, the current scene ledger, pending
    /// director events / oracle rulings awaiting injection, and the event log.
    /// Saved to mythic_data.json in the active save dir (ALife pattern).
    /// </summary>
    public static class MythicData
    {
        public const string FILE_NAME = "mythic_data.json";
        private const int MAX_LOG = 40;
        private const int MAX_PENDING = 3;

        public static MythicState State = new MythicState();

        /// <summary>
        /// True from the start of GameplayManager.LoadGame until our postfix has read the save's
        /// file. LoadGame re-enters the current place (ApplyLocationChange) and may autosave before
        /// that postfix runs, i.e. while State still belongs to whatever was loaded before.
        /// </summary>
        public static bool Loading;

        /// <summary>
        /// Save folder the in-memory State belongs to. Null = a fresh state (plugin start, New
        /// Game) that adopts the first save folder it is written to. State is never written to
        /// any other folder: from the moment the game points at a different save until our
        /// LoadGame postfix has read that save's file, memory still holds the previous save's
        /// director, and writing it out would overwrite the new save's file with it.
        /// </summary>
        private static string _stateDir;

        public static void Reset()
        {
            State = new MythicState { ChaosFactor = MythicPlugin.CfgStartingCF?.Value ?? 5 };
            _stateDir = null;
            Debug.Log("[Mythic] State reset.");
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
                Debug.LogWarning("[Mythic] Save failed: " + ex.Message);
            }
        }

        public static void SaveToCurrentDir()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
            string dir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
            if (_stateDir == null) _stateDir = dir;
            else if (!string.Equals(_stateDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            Save(dir);
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
                State = JsonConvert.DeserializeObject<MythicState>(File.ReadAllText(path)) ?? new MythicState();
                EnsureCollections(State);
                Debug.Log($"[Mythic] Loaded: CF {State.ChaosFactor}, scene {State.SceneNumber}, {State.PendingEvents.Count} pending events, turn {State.CurrentTurn}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Mythic] Load failed, resetting: " + ex.Message);
                Reset();
            }
            finally
            {
                _stateDir = saveDir;
            }
        }

        private static void EnsureCollections(MythicState s)
        {
            if (s.PendingEvents == null) s.PendingEvents = new List<MythicEvent>();
            if (s.PendingRulings == null) s.PendingRulings = new List<OracleRuling>();
            if (s.EventLog == null) s.EventLog = new List<string>();
            if (s.VisitedTopPlaces == null) s.VisitedTopPlaces = new List<string>();
            s.ChaosFactor = Mathf.Clamp(s.ChaosFactor, 1, 9);
        }

        public static void LogLine(string line)
        {
            State.EventLog.Add($"T{State.CurrentTurn}: {line}");
            if (State.EventLog.Count > MAX_LOG)
                State.EventLog.RemoveRange(0, State.EventLog.Count - MAX_LOG);
            Debug.Log("[Mythic] " + line);
        }

        public static void QueueEvent(MythicEvent ev)
        {
            State.PendingEvents.Add(ev);
            while (State.PendingEvents.Count > MAX_PENDING)
                State.PendingEvents.RemoveAt(0);
        }

        /// <summary>Drop events/rulings whose injection window has passed.</summary>
        public static void ExpirePending()
        {
            State.PendingEvents.RemoveAll(e => State.CurrentTurn > e.ExpiresTurn);
            State.PendingRulings.RemoveAll(r => State.CurrentTurn > r.ExpiresTurn);
        }
    }

    public class MythicState
    {
        // ── Core ────────────────────────────────────────────────────────────────
        public int ChaosFactor = 5;
        public int CurrentTurn;

        // ── Current scene ledger (a scene = one stay at a top-level place) ─────
        public int SceneNumber;
        public string ScenePlaceUuid;
        public string ScenePlaceName;
        public int SceneControlScore;      // >0 at scene end → CF−1; <0 → CF+1
        public int SceneKillCredits;       // capped credit from kills, folded into score
        public bool SceneNearDeathFlagged; // near-death only penalized once per scene
        public int SceneEventCount;        // director events fired this scene

        // ── Random event pacing ─────────────────────────────────────────────────
        public int LastEventTurn = -999;

        // ── Injection queues (consumed by expiry turn, not by mutation) ────────
        public List<MythicEvent> PendingEvents = new List<MythicEvent>();
        public List<OracleRuling> PendingRulings = new List<OracleRuling>();

        // ── Quest polling watermarks (NPCExpansion bridge) ─────────────────────
        public int SeenQuestsCompleted;
        public int SeenQuestsFailed;

        // ── Scene testing ───────────────────────────────────────────────────────
        public List<string> VisitedTopPlaces = new List<string>();

        // ── Log & totals ────────────────────────────────────────────────────────
        public List<string> EventLog = new List<string>();
        public int TotalEvents;
        public int TotalAsks;
    }

    /// <summary>A director event awaiting injection into upcoming AI generations.</summary>
    public class MythicEvent
    {
        public string Focus;       // short label for log/status (e.g. "NPC ACTION")
        public string Directive;   // full injected instruction text
        public string Meaning;     // "Action + Descriptor" words, for the log
        public int QueuedTurn;
        public int ExpiresTurn;    // injected while CurrentTurn <= ExpiresTurn
        public bool Significant;
    }

    /// <summary>A MYTHIC ASK result awaiting injection as established fact.</summary>
    public class OracleRuling
    {
        public string Question;
        public string Result;      // "YES", "NO", "EXCEPTIONAL YES", "EXCEPTIONAL NO"
        public int ExpiresTurn;
    }
}
