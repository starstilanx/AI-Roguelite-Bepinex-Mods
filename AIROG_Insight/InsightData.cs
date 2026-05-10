using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Insight
{
    public class InsightData
    {
        public const int InsightThreshold = 3; // Conversations needed before AI generates an insight

        public Dictionary<string, string> NpcInsights { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> PlaceInsights { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> ConversationCounts { get; set; } = new Dictionary<string, int>();

        private static InsightData _instance;
        public static InsightData Instance => _instance ?? (_instance = new InsightData());
        public static void ResetInstance() => _instance = new InsightData();

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
