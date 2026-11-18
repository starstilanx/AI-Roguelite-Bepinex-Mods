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

        private const string FileName = "dm_notes_state.json";

        public const int MaxDirectionChars = 600;
        public const int MaxThreadChars = 240;
        public const int MaxPendingThreads = 30;
        private const int MaxIntroducedKept = 12;
        private const int MaxTurnWindow = 9999;

        // What the AI writes about itself comes back in every prompt, so none of it may grow freely.
        private const int MaxLabelChars = 40;
        private const int MaxAnalysisChars = 400;
        private const int MaxNoteItemChars = 200;

        /// <summary>Pending planned threads shown to the AI per prompt. Set from BepInEx config by GenContextPlugin.</summary>
        public static int MaxThreadsPerPrompt = 3;

        // Save folder CurrentState belongs to (null = none). The game points SS.I.saveSubDirAsArg at
        // the next save before that save is read, and things do run in between (GCHelper trims the
        // history on scene load, a late AI reply can still arrive): saving then would write the
        // previous save's notes into the new save's folder, where LoadState would promptly adopt them.
        private static string _stateDir;

        /// <summary>Both the global "DM Notes" setting and this save's switch are on. Off = the whole layer is inert.</summary>
        public static bool IsActive
            => ContextManager.GetGlobalSetting("DMNotes") && ModToggles.IsEnabled(ModToggles.Keys.DmNotes);

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

                // Replace lists — AI outputs the full canonical list each turn, handling consolidation itself.
                // A missing or empty line means no change (parse failure safety). An explicit "none" for
                // plot threads does clear them: without that the last thread could never be dropped, and
                // the AI was told to keep weaving in a thread it had already resolved.
                if (entry.PlotThreads.Count > 0)
                    CurrentState.PlotThreads = entry.PlotThreads.Take(8).ToList();
                else if (entry.PlotThreadsNone)
                    CurrentState.PlotThreads = new List<string>();
                if (entry.PreferenceNotes.Count > 0)
                    CurrentState.PreferenceNotes = entry.PreferenceNotes.Take(6).ToList();

                int retired = RetireReported(entry.IntroducedIds);

                CurrentState.History.Add(entry);
                // Keep history capped to last 50 entries
                if (CurrentState.History.Count > 50)
                    CurrentState.History.RemoveAt(0);

                _uiDirty = true;
                SaveState();
                Debug.Log($"[GenContext] DM Notes extracted: state={entry.PlayerState}, pacing={entry.PacingDecision}"
                          + (retired > 0 ? $", planned threads introduced={retired}" : ""));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.ProcessNotes error: {ex.Message}");
            }
        }

        internal static DmNotesEntry ParseBlock(string block)
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

                string key = DmNotesText.NormalizeKey(line.Substring(0, colon));
                string value = Unbracket(line.Substring(colon + 1));

                switch (key)
                {
                    case "player_state":
                        entry.PlayerState = Shorten(value, MaxLabelChars);
                        break;
                    case "pacing":
                        entry.PacingDecision = Shorten(value, MaxLabelChars);
                        break;
                    case "engagement":
                        entry.EngagementAnalysis = Shorten(value, MaxAnalysisChars);
                        break;
                    case "plot_threads":
                        entry.PlotThreadsNone = IsNone(value);
                        entry.PlotThreads = SplitList(value);
                        break;
                    case "preferences":
                        entry.PreferenceNotes = SplitList(value);
                        break;
                    case "introduced":
                        entry.IntroducedIds = ParseIds(value);
                        break;
                }
            }

            return entry;
        }

        // The format shown to the AI writes its placeholders in square brackets, and models copy
        // them: "player_state: [Engaged]", "plot_threads: [none]".
        private static string Unbracket(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
                value = value.Substring(1, value.Length - 2).Trim();
            return value;
        }

        private static bool IsNone(string value)
            => string.Equals(value.Trim().TrimEnd('.'), "none", StringComparison.OrdinalIgnoreCase);

        private static List<string> SplitList(string value)
        {
            if (IsNone(value)) return new List<string>();
            return value.Split(';')
                .Select(s => Shorten(s.Trim(), MaxNoteItemChars))
                .Where(s => !string.IsNullOrEmpty(s) && !IsNone(s))
                .ToList();
        }

        private static string Shorten(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max - 3).TrimEnd() + "...";

        private static List<string> ParseIds(string value)
        {
            var ids = new List<string>();
            foreach (string raw in value.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string id = raw.Trim('[', ']', '(', ')', '"', '\'', '.', ':').ToUpperInvariant();
                if (id.Length < 2 || id[0] != 'T') continue;
                bool digits = true;
                for (int i = 1; i < id.Length; i++)
                    if (id[i] < '0' || id[i] > '9') { digits = false; break; }
                if (digits && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
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

        // ---- Player direction and planned threads ----------------------------------------------

        /// <summary><paramref name="save"/> false while the player is still typing: the next prompt sees it, the disk does not yet.</summary>
        public static void SetDirection(string text, bool save)
        {
            CurrentState.Direction = Clean(text, MaxDirectionChars);
            if (save) SaveState();
        }

        /// <summary>Returns null when the text is empty or the list is full.</summary>
        public static DmPlannedThread AddThread(string text, int notBeforeTurns, int byTurns)
        {
            string clean = Clean(text, MaxThreadChars);
            if (clean.Length == 0 || CurrentState.PlannedThreads.Count >= MaxPendingThreads) return null;

            var thread = new DmPlannedThread
            {
                Id = "T" + CurrentState.NextThreadNumber++,
                Text = clean,
                AddedTurn = CurrentState.TurnCounter,
                NotBeforeTurns = ClampTurns(notBeforeTurns),
                ByTurns = ClampTurns(byTurns)
            };
            CurrentState.PlannedThreads.Add(thread);
            ThreadsChanged();
            return thread;
        }

        /// <summary>Edits a pending thread. Its id and the turn its windows count from stay as they were.</summary>
        public static bool UpdateThread(string id, string text, int notBeforeTurns, int byTurns)
        {
            var thread = FindPending(id);
            string clean = Clean(text, MaxThreadChars);
            if (thread == null || clean.Length == 0) return false;

            thread.Text = clean;
            thread.NotBeforeTurns = ClampTurns(notBeforeTurns);
            thread.ByTurns = ClampTurns(byTurns);
            ThreadsChanged();
            return true;
        }

        /// <summary>Removes a thread outright, pending or introduced.</summary>
        public static void DeleteThread(string id)
        {
            int removed = CurrentState.PlannedThreads.RemoveAll(t => t.Id == id)
                          + CurrentState.IntroducedThreads.RemoveAll(t => t.Id == id);
            if (removed > 0) ThreadsChanged();
        }

        /// <summary>The player marks a pending thread as introduced.</summary>
        public static void RetireThread(string id)
        {
            var thread = FindPending(id);
            if (thread == null) return;
            MoveToIntroduced(thread, byPlayer: true);
            ThreadsChanged();
        }

        /// <summary>Puts an introduced thread back in the pending list (the AI claimed it too early).</summary>
        public static void RestoreThread(string id)
        {
            var thread = CurrentState.IntroducedThreads.FirstOrDefault(t => t.Id == id);
            if (thread == null) return;
            CurrentState.IntroducedThreads.Remove(thread);
            thread.IntroducedTurn = -1;
            thread.RetiredByPlayer = false;
            CurrentState.PlannedThreads.Add(thread);
            ThreadsChanged();
        }

        /// <summary>
        /// Retires the threads the AI reported on its "introduced:" line. Only a thread it was
        /// shown this turn counts: an id it invented, one still held back, or one already retired
        /// is ignored. Returns how many were retired. Caller saves.
        /// </summary>
        internal static int RetireReported(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return 0;

            var offered = DmThreadLogic.SelectForPrompt(CurrentState, MaxThreadsPerPrompt);
            int retired = 0;
            foreach (string id in ids)
            {
                var thread = offered.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                if (thread == null || !CurrentState.PlannedThreads.Contains(thread))
                {
                    Debug.Log($"[GenContext] DM Notes: AI reported '{id}' as introduced, but no such thread was offered this turn - ignored.");
                    continue;
                }
                MoveToIntroduced(thread, byPlayer: false);
                retired++;
            }
            return retired;
        }

        private static void MoveToIntroduced(DmPlannedThread thread, bool byPlayer)
        {
            CurrentState.PlannedThreads.Remove(thread);
            thread.IntroducedTurn = CurrentState.TurnCounter;
            thread.RetiredByPlayer = byPlayer;
            CurrentState.IntroducedThreads.Add(thread);
            while (CurrentState.IntroducedThreads.Count > MaxIntroducedKept)
                CurrentState.IntroducedThreads.RemoveAt(0);
        }

        private static DmPlannedThread FindPending(string id)
            => CurrentState.PlannedThreads.FirstOrDefault(t => t.Id == id);

        private static void ThreadsChanged()
        {
            _uiDirty = true;
            SaveState();
        }

        private static int ClampTurns(int turns) => Math.Max(0, Math.Min(MaxTurnWindow, turns));

        /// <summary>Player text goes into the prompt on one line.</summary>
        private static string Clean(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string clean = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return clean.Length <= max ? clean : clean.Substring(0, max).TrimEnd();
        }

        /// <summary>
        /// GameplayManager.TurnHappenedEvent handler. numTurns is how many player turns just ended
        /// (0 for an NPC or system turn). While DM Notes is off the clock stands still, so thread
        /// windows do not run out behind the player's back.
        /// </summary>
        public static void OnTurnHappened(int numTurns, long secs)
        {
            try
            {
                if (numTurns <= 0 || !IsActive) return;
                CurrentState.TurnCounter += numTurns;
                _uiDirty = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.OnTurnHappened error: {ex.Message}");
            }
        }

        // ---- Per-save lifecycle ----------------------------------------------------------------

        public static void ResetState()
        {
            CurrentState = new DmNotesState();
            _stateDir = CurrentSaveDir();
            _uiDirty = true;
            Debug.Log("[GenContext] DM Notes state reset for new game.");
        }

        /// <summary>
        /// Called when a game scene starts, before the save is read. Drops notes that belong to a
        /// different save folder than the one the game now points at. A multiplayer client neither
        /// reads a save nor starts a new game, so without this it would play on with the last
        /// single-player save's notes, direction and planned threads.
        /// </summary>
        public static void DetachIfSaveChanged()
        {
            if (_stateDir == CurrentSaveDir()) return;
            CurrentState = new DmNotesState();
            _stateDir = null;
            _uiDirty = true;
        }

        public static void TrimHistory(int maxEntries = 30)
        {
            if (CurrentState.History.Count <= maxEntries) return;
            CurrentState.History.RemoveRange(0, CurrentState.History.Count - maxEntries);
            SaveState();
            Debug.Log($"[GenContext] DM Notes history trimmed to {maxEntries} entries.");
        }

        public static bool ConsumeUiDirty()
        {
            bool v = _uiDirty;
            _uiDirty = false;
            return v;
        }

        private static string CurrentSaveDir()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
            return Path.Combine(SS.I.saveTopLvlDir ?? "", SS.I.saveSubDirAsArg);
        }

        public static void SaveState()
        {
            try
            {
                string dir = CurrentSaveDir();
                if (dir == null || dir != _stateDir) return;
                File.WriteAllText(Path.Combine(dir, FileName), JsonConvert.SerializeObject(CurrentState, Formatting.Indented));
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
                CurrentState = new DmNotesState();
                _stateDir = null;
                _uiDirty = true;
                if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return;

                string dir = Path.Combine(SS.I.saveTopLvlDir ?? "", saveSubDir);
                _stateDir = dir;
                string path = Path.Combine(dir, FileName);
                if (File.Exists(path))
                    CurrentState = Normalize(JsonConvert.DeserializeObject<DmNotesState>(File.ReadAllText(path)));

                Debug.Log($"[GenContext] DM Notes state loaded from {saveSubDir}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] DmNotesManager.LoadState error: {ex.Message}");
                CurrentState = new DmNotesState();
            }
        }

        /// <summary>A hand-edited or older file can carry nulls where the code expects lists.</summary>
        private static DmNotesState Normalize(DmNotesState state)
        {
            if (state == null) return new DmNotesState();

            if (string.IsNullOrEmpty(state.PlayerState)) state.PlayerState = "Unknown";
            if (string.IsNullOrEmpty(state.PacingDecision)) state.PacingDecision = "Medium";
            if (state.EngagementAnalysis == null) state.EngagementAnalysis = "";
            if (state.Direction == null) state.Direction = "";
            if (state.PlotThreads == null) state.PlotThreads = new List<string>();
            if (state.PreferenceNotes == null) state.PreferenceNotes = new List<string>();
            if (state.History == null) state.History = new List<DmNotesEntry>();
            if (state.PlannedThreads == null) state.PlannedThreads = new List<DmPlannedThread>();
            if (state.IntroducedThreads == null) state.IntroducedThreads = new List<DmPlannedThread>();

            state.PlotThreads.RemoveAll(string.IsNullOrEmpty);
            state.PreferenceNotes.RemoveAll(string.IsNullOrEmpty);
            state.History.RemoveAll(e => e == null);
            state.PlannedThreads.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Text));
            state.IntroducedThreads.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Text));
            if (state.TurnCounter < 0) state.TurnCounter = 0;

            int highest = 0;
            foreach (var t in state.PlannedThreads.Concat(state.IntroducedThreads))
            {
                int n = DmThreadLogic.IdNumber(t.Id);
                if (n != int.MaxValue && n > highest) highest = n;
            }
            if (state.NextThreadNumber <= highest) state.NextThreadNumber = highest + 1;
            return state;
        }
    }
}
