using System;
using System.Collections.Generic;
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
    }
}
