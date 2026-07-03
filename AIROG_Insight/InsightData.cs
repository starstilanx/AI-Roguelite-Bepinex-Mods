using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Insight
{
    public class InsightData
    {
        public const int InsightThreshold = 3;      // Conversations needed before AI generates an insight
        public const int DeepenThreshold = 4;       // Additional conversations after an insight before it can deepen
        public const int PlaceInsightThreshold = 5; // Interactions at a place before AI generates a location insight
        public const int MaxInsightLength = 600;    // Cap on accumulated insight text per NPC

        public Dictionary<string, string> NpcInsights { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> PlaceInsights { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> ConversationCounts { get; set; } = new Dictionary<string, int>();

        /// <summary>Conversation count at which the NPC's latest insight was gained (drives deepening).</summary>
        public Dictionary<string, int> InsightGainedAtCount { get; set; } = new Dictionary<string, int>();

        /// <summary>Player interactions taken while at a place, keyed by place UUID.</summary>
        public Dictionary<string, int> PlaceInteractionCounts { get; set; } = new Dictionary<string, int>();

        private static InsightData _instance;
        public static InsightData Instance => _instance ?? (_instance = new InsightData());
        public static void ResetInstance() => _instance = new InsightData();

        /// <summary>Appends a new insight layer to an NPC's accumulated insight text, keeping the tail under the cap.</summary>
        public void AddNpcInsight(string uuid, string insightText)
        {
            string combined = NpcInsights.TryGetValue(uuid, out string existing) && !string.IsNullOrEmpty(existing)
                ? existing.TrimEnd() + " " + insightText
                : insightText;
            if (combined.Length > MaxInsightLength)
                combined = "…" + combined.Substring(combined.Length - MaxInsightLength);
            NpcInsights[uuid] = combined;
            ConversationCounts.TryGetValue(uuid, out int count);
            InsightGainedAtCount[uuid] = count;
        }

        public void Save(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir)) return;
            string path = Path.Combine(saveDir, "insight_data.json");
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AIROG_Insight] Failed to save: " + ex.Message);
            }
        }

        public void Load(string saveDir)
        {
            _instance = new InsightData();
            if (string.IsNullOrEmpty(saveDir)) return;
            string path = Path.Combine(saveDir, "insight_data.json");
            if (File.Exists(path))
            {
                try
                {
                    _instance = JsonConvert.DeserializeObject<InsightData>(File.ReadAllText(path)) ?? new InsightData();
                }
                catch (System.Exception ex)
                {
                    Debug.LogError("[AIROG_Insight] Failed to load: " + ex.Message);
                }
            }
        }
    }
}
