using System.Collections.Generic;

namespace AIROG_Chronicle
{
    // Serialized as its ordinal, and AIROG_Reverie reads "3" as Death: never reorder, only append.
    public enum BeatType { Narrative, Arrival, Combat, Death, LevelUp, Quest }

    public class ChronicleBeat
    {
        public int Turn { get; set; }
        public BeatType Type { get; set; }
        public string Summary { get; set; }
        public bool IsMilestone { get; set; }
    }

    public class Chapter
    {
        public int Number { get; set; }
        public string Title { get; set; }       // AI-generated on close; empty while open
        public int StartTurn { get; set; }
        public int EndTurn { get; set; } = -1;  // -1 while open
        public string Recap { get; set; }       // 1-2 sentence AI summary generated on close
        public List<ChronicleBeat> Beats { get; set; } = new List<ChronicleBeat>();

        // 3-6 names central to the chapter. Once the chapter has left the always-injected window,
        // one of these showing up in the current scene pulls its recap back into the prompt.
        // Null in files written before tags existed (filled in heuristically after load).
        public List<string> Tags { get; set; }
    }

    /// <summary>A run of older closed chapters condensed into one short summary.</summary>
    public class ChronicleAct
    {
        public int Number { get; set; }
        public int FirstChapter { get; set; }   // Chapter.Number range covered, inclusive
        public int LastChapter { get; set; }
        public int StartTurn { get; set; }
        public int EndTurn { get; set; }
        public string Summary { get; set; }     // AI-written; empty until generated
    }

    public class ChronicleState
    {
        public List<Chapter> ClosedChapters { get; set; } = new List<Chapter>();
        public Chapter CurrentChapter { get; set; } = new Chapter { Number = 1, StartTurn = 1, EndTurn = -1 };
        public int GlobalTurn { get; set; }
        public string LastSessionRecap { get; set; }
        public int LastSessionRecapTurn { get; set; } = -1;  // GlobalTurn when the recap was generated
        public List<ChronicleAct> Acts { get; set; } = new List<ChronicleAct>();

        /// <summary>Null-fill collections: older files lack some of them and hand-edited ones can null them.</summary>
        public void EnsureCollections()
        {
            if (ClosedChapters == null) ClosedChapters = new List<Chapter>();
            ClosedChapters.RemoveAll(c => c == null);
            if (CurrentChapter == null)
            {
                int last = ClosedChapters.Count > 0 ? ClosedChapters[ClosedChapters.Count - 1].Number : 0;
                CurrentChapter = new Chapter { Number = last + 1, StartTurn = GlobalTurn + 1, EndTurn = -1 };
            }
            foreach (var ch in ClosedChapters)
            {
                if (ch.Beats == null) ch.Beats = new List<ChronicleBeat>();
                ch.Beats.RemoveAll(b => b == null);
            }
            if (CurrentChapter.Beats == null) CurrentChapter.Beats = new List<ChronicleBeat>();
            CurrentChapter.Beats.RemoveAll(b => b == null);
            if (Acts == null) Acts = new List<ChronicleAct>();
            Acts.RemoveAll(a => a == null);
        }
    }
}
