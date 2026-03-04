using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.ContextProviders
{
    public class HistoryProvider : IContextProvider
    {
        public int Priority => 100; // High Priority
        public string Name => "World History";
        public string Description => "Injects a summary of the universe's generated history (Lorebook entries and major events).";

#pragma warning disable 0649
        private class HistoryEntry
        {
            public string HistoryText { get; set; }
            public string UniverseUuid { get; set; }
        }

        private Dictionary<string, string> _historyCache = new Dictionary<string, string>();
        private float _lastLoadTime = 0;
        private const float CACHE_REFRESH_RATE = 10f; // Refresh every 10 seconds max

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
            if (manager == null) return "";

            var universe = manager.GetCurrentUniverse();
            if (universe == null) return "";

            // Refresh cache if needed
            if (Time.time - _lastLoadTime > CACHE_REFRESH_RATE)
            {
                LoadHistory();
                _lastLoadTime = Time.time;
            }

            if (_historyCache.TryGetValue(universe.uuid, out string history))
            {
                if (string.IsNullOrEmpty(history)) return "";

                // Guard against overflow: int.MaxValue * 4 overflows to negative in C#
                // Use integer Math.Max (not Mathf.Max) to avoid float conversion issues
                int maxChars;
                if (maxTokens <= 0) return "";
                if (maxTokens >= int.MaxValue / 4)
                    maxChars = history.Length; // Effectively no limit
                else
                    maxChars = Math.Max(0, maxTokens * 4);

                if (maxChars <= 0) return "";

                if (history.Length > maxChars)
                {
                    return "[WORLD HISTORY]\n" + history.Substring(0, maxChars) + "...";
                }
                
                return "[WORLD HISTORY]\n" + history;
            }

            return "";
        }

        private void LoadHistory()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;

            string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "universe_history.json");
            if (!File.Exists(path)) return;

            try
            {
                var list = JsonConvert.DeserializeObject<List<HistoryEntry>>(File.ReadAllText(path));
                _historyCache.Clear();
                if (list != null)
                {
                    foreach (var entry in list)
                    {
                        if (!string.IsNullOrEmpty(entry.UniverseUuid))
                        {
                            _historyCache[entry.UniverseUuid] = entry.HistoryText;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GenContext] Failed to load history: {ex.Message}");
            }
        }
    }
}
