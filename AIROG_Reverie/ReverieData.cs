using System.Collections.Generic;

namespace AIROG_Reverie
{
    public enum DreamPhase { Awake, Dreaming }

    public enum WakeOutcome { None, Triumph, Neutral, Nightmare }

    /// <summary>A single in-flight (or archived) dream.</summary>
    public class DreamRecord
    {
        public string Theme { get; set; }          // theme table entry name, e.g. "The Pursuit"
        public string Premise { get; set; }        // composed dreamscape description
        public string Core { get; set; }           // the central confrontation to resolve
        public int Progress { get; set; }          // 0-100 toward confronting the core
        public int Lucidity { get; set; }          // mind meter; 0 = nightmare
        public int DreamTurnsRemaining { get; set; }
        public int StartedTurn { get; set; }       // GlobalTurn at entry
        public long HpSnapshot { get; set; }       // body HP at entry, restored on wake
        public List<string> Events { get; set; } = new List<string>();
    }

    /// <summary>A prophecy earned from a dream triumph. Injected until fulfilled by TTL.</summary>
    public class Omen
    {
        public string Text { get; set; }
        public int CreatedTurn { get; set; }
        public int ExpiresTurn { get; set; }
    }

    /// <summary>Something that followed the dreamer out of a nightmare.</summary>
    public class Haunting
    {
        public string Text { get; set; }
        public int CreatedTurn { get; set; }
        public int ExpiresTurn { get; set; }
    }

    public class ReverieState
    {
        public int GlobalTurn { get; set; }
        public int LastDreamTurn { get; set; } = -999;
        public DreamPhase Phase { get; set; } = DreamPhase.Awake;
        public DreamRecord CurrentDream { get; set; }

        // Set at wake; consumed by the provider as a one-shot [WAKING] directive
        public WakeOutcome PendingWake { get; set; } = WakeOutcome.None;
        public string PendingWakeSummary { get; set; }

        public List<Omen> Omens { get; set; } = new List<Omen>();
        public Haunting ActiveHaunting { get; set; }

        // Lifetime stats
        public int TotalDreams { get; set; }
        public int TotalTriumphs { get; set; }
        public int TotalNightmares { get; set; }

        /// <summary>Null-fill collections for saves written by older versions.</summary>
        public void EnsureCollections()
        {
            if (Omens == null) Omens = new List<Omen>();
            if (CurrentDream != null && CurrentDream.Events == null)
                CurrentDream.Events = new List<string>();
        }
    }

    // ---- Read-only stubs for Chronicle's chronicle.json (no hard dependency) ----
    // Keep in sync with AIROG_Chronicle/ChronicleData.cs.

    public class RvChronicleBeat
    {
        public int Turn { get; set; }
        public string Type { get; set; }       // enum serialized; read as string tolerant
        public string Summary { get; set; }
        public bool IsMilestone { get; set; }
    }

    public class RvChapter
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string Recap { get; set; }
        public List<RvChronicleBeat> Beats { get; set; } = new List<RvChronicleBeat>();
    }

    public class RvChronicleState
    {
        public List<RvChapter> ClosedChapters { get; set; } = new List<RvChapter>();
        public RvChapter CurrentChapter { get; set; }
    }
}
