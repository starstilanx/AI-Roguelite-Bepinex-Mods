using System.Collections.Generic;

namespace AIROG_Chronicle
{
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
    }

    public class ChronicleState
    {
        public List<Chapter> ClosedChapters { get; set; } = new List<Chapter>();
        public Chapter CurrentChapter { get; set; } = new Chapter { Number = 1, StartTurn = 1, EndTurn = -1 };
        public int GlobalTurn { get; set; }
        public string LastSessionRecap { get; set; }
    }
}
