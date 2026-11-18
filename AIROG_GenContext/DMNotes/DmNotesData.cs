using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AIROG_GenContext.DMNotes
{
    [Serializable]
    public class DmNotesState
    {
        public string PlayerState = "Unknown";
        public string PacingDecision = "Medium";
        public string EngagementAnalysis = "";
        public List<string> PlotThreads = new List<string>();
        public List<string> PreferenceNotes = new List<string>();
        public List<DmNotesEntry> History = new List<DmNotesEntry>();

        // ---- Written by the player, never by the AI ----

        /// <summary>Where the player wants the story to head. Empty = no steering.</summary>
        public string Direction = "";
        /// <summary>Plot threads the player wants introduced at some point; not yet in the story.</summary>
        public List<DmPlannedThread> PlannedThreads = new List<DmPlannedThread>();
        /// <summary>Planned threads that have been introduced (newest last, capped).</summary>
        public List<DmPlannedThread> IntroducedThreads = new List<DmPlannedThread>();
        /// <summary>Ids are never reused within a save, so a late report from the AI cannot hit a newer thread.</summary>
        public int NextThreadNumber = 1;
        /// <summary>Player turns taken in this save while DM Notes was switched on. The clock thread windows run on.</summary>
        public int TurnCounter = 0;
    }

    [Serializable]
    public class DmNotesEntry
    {
        [JsonIgnore] public string Raw;
        public string PlayerState;
        public string PacingDecision;
        public string EngagementAnalysis;
        public List<string> PlotThreads = new List<string>();
        public List<string> PreferenceNotes = new List<string>();
        public string Timestamp;

        /// <summary>The AI wrote "plot_threads: none" (as opposed to leaving the line out or empty).</summary>
        [JsonIgnore] public bool PlotThreadsNone;
        /// <summary>Ids from the block's "introduced:" line.</summary>
        [JsonIgnore] public List<string> IntroducedIds = new List<string>();
    }

    [Serializable]
    public class DmPlannedThread
    {
        public string Id;
        public string Text;
        /// <summary>Value of <see cref="DmNotesState.TurnCounter"/> when the thread was added; both windows count from it.</summary>
        public int AddedTurn;
        /// <summary>Hold the thread back for this many turns after it was added. 0 = it may appear at once.</summary>
        public int NotBeforeTurns;
        /// <summary>The thread should be in the story within this many turns of being added. 0 = no deadline.</summary>
        public int ByTurns;
        /// <summary>Turn it was retired on; -1 while pending.</summary>
        public int IntroducedTurn = -1;
        /// <summary>Retired from the panel rather than reported by the AI.</summary>
        public bool RetiredByPlayer;
    }

    /// <summary>Turn-window arithmetic for planned threads. No Unity or game types: exercised by a console harness.</summary>
    public static class DmThreadLogic
    {
        public static bool HasDeadline(DmPlannedThread t) => t.ByTurns > 0;

        /// <summary>Turns still to wait before the thread may be shown to the AI (0 = may be shown).</summary>
        public static int WaitTurns(DmPlannedThread t, int turn)
            => t.NotBeforeTurns > 0 ? Math.Max(0, t.AddedTurn + t.NotBeforeTurns - turn) : 0;

        /// <summary>Prompts left inside the deadline, this one included. 1 = this is the last one, 0 or less = overdue.</summary>
        public static int TurnsLeft(DmPlannedThread t, int turn) => t.AddedTurn + t.ByTurns - turn;

        public static bool IsOverdue(DmPlannedThread t, int turn) => HasDeadline(t) && TurnsLeft(t, turn) <= 0;

        /// <summary>Overdue, or on the last turn of its window: the AI is told to introduce it now.</summary>
        public static bool IsUrgent(DmPlannedThread t, int turn) => HasDeadline(t) && TurnsLeft(t, turn) <= 1;

        public static int IdNumber(string id)
        {
            int n;
            return id != null && id.Length > 1 && int.TryParse(id.Substring(1), out n) ? n : int.MaxValue;
        }

        /// <summary>
        /// The pending threads shown to the AI this turn: not held back, most urgent first, then oldest,
        /// at most <paramref name="cap"/>. The response parser calls this too, so only a thread the AI
        /// was actually shown can be reported as introduced.
        /// </summary>
        public static List<DmPlannedThread> SelectForPrompt(DmNotesState state, int cap)
        {
            int turn = state.TurnCounter;
            return state.PlannedThreads
                .Where(t => t != null && !string.IsNullOrEmpty(t.Id) && WaitTurns(t, turn) == 0)
                .OrderBy(t => IsUrgent(t, turn) ? 0 : 1)
                .ThenBy(t => IsUrgent(t, turn) ? TurnsLeft(t, turn) : 0)
                .ThenBy(t => t.AddedTurn)
                .ThenBy(t => IdNumber(t.Id))
                .Take(Math.Max(1, cap))
                .ToList();
        }
    }
}
