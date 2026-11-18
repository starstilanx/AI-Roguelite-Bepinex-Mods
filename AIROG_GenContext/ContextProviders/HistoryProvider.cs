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
        private string _loadedPath; // the file _historyCache was read from
        private const float CACHE_REFRESH_RATE = 10f; // Refresh every 10 seconds max

        private const string Header = "[WORLD HISTORY]\n";
        private const string Ellipsis = "...";

        public string GetContext(string prompt, int maxTokens)
        {
            var manager = SS.I != null ? SS.I.hackyManager : null;
            if (manager == null) return "";

            var universe = manager.GetCurrentUniverse();
            if (universe == null) return "";

            // Refresh cache if needed - at once when the save changed. Entries are keyed by
            // universe uuid, and games started from the same preset share those, so the last
            // save's history would otherwise be served to this one.
            string path = HistoryPath();
            if (path != _loadedPath || Time.time - _lastLoadTime > CACHE_REFRESH_RATE)
            {
                LoadHistory(path);
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
                    // The header and the ellipsis come out of the budget too; left out, the block
                    // overran it by their length on every prompt with a long history.
                    maxChars = Math.Max(0, maxTokens * 4 - Header.Length - Ellipsis.Length);

                if (maxChars <= 0) return "";

                if (history.Length > maxChars)
                {
                    return Header + history.Substring(0, maxChars) + Ellipsis;
                }

                return Header + history;
            }

            return "";
        }

        private static string HistoryPath()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "universe_history.json");
        }

        private void LoadHistory(string path)
        {
            bool sameFile = path == _loadedPath;
            _loadedPath = path;
            if (path == null || !File.Exists(path))
            {
                // This save has no history file: what is cached came from another save.
                _historyCache.Clear();
                return;
            }

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
                if (!sameFile) _historyCache.Clear();
                Debug.LogWarning($"[GenContext] Failed to load history: {ex.Message}");
            }
        }
    }
}
