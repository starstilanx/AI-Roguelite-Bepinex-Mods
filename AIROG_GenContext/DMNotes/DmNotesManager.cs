using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.DMNotes
{
    public static class DmNotesManager
    {
        public static DmNotesState CurrentState = new DmNotesState();
        private static bool _uiDirty = false;

        public static void ProcessNotes(string block)
        {
            try
            {
                var entry = ParseBlock(block);
                if (!string.IsNullOrEmpty(entry.PlayerState))
                    CurrentState.PlayerState = entry.PlayerState;
                if (!string.IsNullOrEmpty(entry.PacingDecision))
                    CurrentState.PacingDecision = entry.PacingDecision;
                if (!string.IsNullOrEmpty(entry.EngagementAnalysis))
                    CurrentState.EngagementAnalysis = entry.EngagementAnalysis;

                // Accumulate plot threads (no duplicates)
                foreach (var thread in entry.PlotThreads)
                {
                    if (!string.IsNullOrWhiteSpace(thread) &&
                        !CurrentState.PlotThreads.Any(t => t.Equals(thread, StringComparison.OrdinalIgnoreCase)))
                        CurrentState.PlotThreads.Add(thread);
                }

                // Accumulate preferences (no duplicates)
                foreach (var pref in entry.PreferenceNotes)
                {
                    if (!string.IsNullOrWhiteSpace(pref) &&
                        !CurrentState.PreferenceNotes.Any(p => p.Equals(pref, StringComparison.OrdinalIgnoreCase)))
                        CurrentState.PreferenceNotes.Add(pref);
                }

                CurrentState.History.Add(entry);
                // Keep history capped to last 50 entries
                if (CurrentState.History.Count > 50)
                    CurrentState.History.RemoveAt(0);

                _uiDirty = true;
                SaveState();
                Debug.Log($"[GenContext] DM Notes extracted: state={entry.PlayerState}, pacing={entry.PacingDecision}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.ProcessNotes error: {ex.Message}");
            }
        }

        private static DmNotesEntry ParseBlock(string block)
        {
            var entry = new DmNotesEntry
            {
                Raw = block,
                Timestamp = DateTime.Now.ToString("o")
            };

            foreach (var rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key = line.Substring(0, colon).Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
                string value = line.Substring(colon + 1).Trim();

                switch (key)
                {
                    case "player_state":
                        entry.PlayerState = value;
                        break;
                    case "pacing":
                        entry.PacingDecision = value;
                        break;
                    case "engagement":
                        entry.EngagementAnalysis = value;
                        break;
                    case "plot_threads":
                        entry.PlotThreads = value.ToLowerInvariant() == "none"
                            ? new List<string>()
                            : value.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        break;
                    case "preferences":
                        entry.PreferenceNotes = value.ToLowerInvariant() == "none"
                            ? new List<string>()
                            : value.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        break;
                }
            }

            return entry;
        }

        public static void RemovePlotThread(int index)
        {
            if (index >= 0 && index < CurrentState.PlotThreads.Count)
            {
                CurrentState.PlotThreads.RemoveAt(index);
                _uiDirty = true;
                SaveState();
            }
        }

        public static void RemovePreference(int index)
        {
            if (index >= 0 && index < CurrentState.PreferenceNotes.Count)
            {
                CurrentState.PreferenceNotes.RemoveAt(index);
                _uiDirty = true;
                SaveState();
            }
        }

        public static void ResetState()
        {
            CurrentState = new DmNotesState();
            _uiDirty = true;
            Debug.Log("[GenContext] DM Notes state reset for new game.");
        }

        public static bool ConsumeUiDirty()
        {
            bool v = _uiDirty;
            _uiDirty = false;
            return v;
        }

        public static void SaveState()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "dm_notes_state.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(CurrentState, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.SaveState error: {ex.Message}");
            }
        }

        public static void LoadState(string saveSubDir)
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, saveSubDir, "dm_notes_state.json");
                if (File.Exists(path))
                {
                    CurrentState = JsonConvert.DeserializeObject<DmNotesState>(File.ReadAllText(path))
                                   ?? new DmNotesState();
                }
                else
                {
                    CurrentState = new DmNotesState();
                }
                _uiDirty = true;
                Debug.Log($"[GenContext] DM Notes state loaded from {saveSubDir}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.LoadState error: {ex.Message}");
                CurrentState = new DmNotesState();
            }
        }
    }
}
