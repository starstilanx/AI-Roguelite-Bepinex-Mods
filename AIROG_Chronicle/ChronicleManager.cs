using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Chronicle
{
    public static class ChronicleManager
    {
        // Number of turns before a chapter auto-closes
        private const int CHAPTER_LENGTH = 15;

        public static ChronicleState State { get; private set; } = new ChronicleState();

        // Set true during internal AI calls so the interceptor skips them
        public static bool IsInternalCall { get; private set; }

        private static bool _uiDirty;

        // ---- Lifecycle ----

        public static void Init()
        {
            State = new ChronicleState();
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Debug.Log("[Chronicle] ChronicleManager initialized.");
        }

        private static void OnTurnHappened(int numTurns, long secs)
        {
            if (numTurns > 0 && State != null)
                State.GlobalTurn += numTurns;
        }

        public static void Reset()
        {
            State = new ChronicleState();
            _uiDirty = true;
            // Re-subscribe in case the game nulled TurnHappenedEvent on exit to main menu
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Debug.Log("[Chronicle] State reset for new game.");
        }

        // ---- Beat Processing ----

        public static void ProcessBeatBlock(string block)
        {
            try
            {
                var beat = new ChronicleBeat { Turn = State?.GlobalTurn ?? 0 };

                foreach (var rawLine in block.Split('\n'))
                {
                    string line = rawLine.Trim();
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;

                    string key = line.Substring(0, colon).Trim().ToLowerInvariant().Replace(" ", "_");
                    string value = line.Substring(colon + 1).Trim();

                    switch (key)
                    {
                        case "event_type": beat.Type = ParseBeatType(value); break;
                        case "summary":    beat.Summary = value; break;
                        case "is_milestone": beat.IsMilestone = value.ToLowerInvariant() == "true"; break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(beat.Summary))
                    RecordBeat(beat);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] ProcessBeatBlock error: {ex.Message}");
            }
        }

        private static BeatType ParseBeatType(string value)
        {
            switch (value.ToLowerInvariant().Trim())
            {
                case "combat":   return BeatType.Combat;
                case "arrival":  return BeatType.Arrival;
                case "death":    return BeatType.Death;
                case "levelup":  return BeatType.LevelUp;
                case "quest":    return BeatType.Quest;
                default:         return BeatType.Narrative;
            }
        }

        public static void RecordBeat(ChronicleBeat beat)
        {
            if (State == null) State = new ChronicleState();
            if (State.CurrentChapter == null)
                State.CurrentChapter = new Chapter { Number = 1, StartTurn = 1, EndTurn = -1 };

            // Skip exact repeats of the most recent beat (the AI sometimes re-emits the same summary)
            var beats = State.CurrentChapter.Beats;
            if (beats.Count > 0 &&
                string.Equals(beats[beats.Count - 1].Summary, beat.Summary, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[Chronicle] Duplicate beat skipped: {beat.Summary}");
                return;
            }

            State.CurrentChapter.Beats.Add(beat);
            _uiDirty = true;

            Debug.Log($"[Chronicle] Beat recorded: T{beat.Turn} [{beat.Type}] {beat.Summary}");

            // Close chapter after CHAPTER_LENGTH turns (guard: not already closing, not in an internal call)
            int turnsInChapter = State.GlobalTurn - State.CurrentChapter.StartTurn;
            if (turnsInChapter >= CHAPTER_LENGTH && State.CurrentChapter.EndTurn == -1 && !IsInternalCall)
            {
                var chapterToClose = State.CurrentChapter;
                chapterToClose.EndTurn = State.GlobalTurn;

                // Add to closed list immediately so saves don't lose the chapter
                State.ClosedChapters.Add(chapterToClose);

                // Open a new chapter right away so incoming beats go to the right place
                State.CurrentChapter = new Chapter
                {
                    Number = chapterToClose.Number + 1,
                    StartTurn = State.GlobalTurn + 1,
                    EndTurn = -1
                };

                // Persist the close immediately — the AI title/recap fills in later via its own Save()
                Save();

                // Fire-and-forget: AI generates title + recap in background
                _ = GenerateTitleAndRecapAsync(chapterToClose);
            }
        }

        // ---- AI Chapter Close ----

        private static async Task GenerateTitleAndRecapAsync(Chapter chapter)
        {
            try
            {
                string beatList = string.Join("\n",
                    chapter.Beats.Select(b => $"- {b.Summary}"));
                if (string.IsNullOrWhiteSpace(beatList))
                    beatList = "- (no recorded beats)";

                string prompt =
                    "[Fantasy RPG story beats from a single chapter]\n" + beatList + "\n\n" +
                    "Write exactly two lines:\n" +
                    "Title: [a dramatic chapter title, max 6 words]\n" +
                    "Recap: [a 1-2 sentence summary of what happened]";

                string resp = await InternalAICall(prompt);

                ParseChapterResponse(resp, chapter.Number, out string title, out string recap);
                chapter.Title = title;
                chapter.Recap = recap;

                _uiDirty = true;
                Save();
                Debug.Log($"[Chronicle] Chapter {chapter.Number} titled: \"{title}\"");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] GenerateTitleAndRecapAsync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires an internal AI call with IsInternalCall raised only around the synchronous kickoff.
        /// The interceptor's Harmony postfix runs at kickoff time, so this is sufficient to make it
        /// skip this call — while leaving concurrent player-turn responses intercepted normally.
        /// (Holding the flag across the await made concurrent turns leak raw beat blocks on screen.)
        /// </summary>
        private static Task<string> InternalAICall(string prompt)
        {
            IsInternalCall = true;
            try
            {
                return AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.STORY_COMPLETER, prompt,
                    forceConcise: true);
            }
            finally
            {
                IsInternalCall = false;
            }
        }

        // ---- Session Recap ----

        private static bool _recapRunning;

        /// <summary>
        /// Generates (or returns the cached) "Previously on…" session recap covering the recent
        /// chapters and the current chapter's beats. Returns null if a generation is already in
        /// flight or there is nothing to recap.
        /// </summary>
        public static async Task<string> GetOrGenerateSessionRecapAsync()
        {
            if (State == null) return null;

            // Cached and still current for this turn — reuse it
            if (!string.IsNullOrEmpty(State.LastSessionRecap) && State.LastSessionRecapTurn == State.GlobalTurn)
                return State.LastSessionRecap;

            if (_recapRunning) return null;

            var lines = new System.Collections.Generic.List<string>();
            if (State.ClosedChapters != null)
                foreach (var ch in State.ClosedChapters.Skip(Math.Max(0, State.ClosedChapters.Count - 3)))
                    if (!string.IsNullOrEmpty(ch.Recap))
                        lines.Add($"Chapter {ch.Number}{(string.IsNullOrEmpty(ch.Title) ? "" : $" \"{ch.Title}\"")}: {ch.Recap}");
            if (State.CurrentChapter?.Beats != null)
                foreach (var b in State.CurrentChapter.Beats.Skip(Math.Max(0, State.CurrentChapter.Beats.Count - 12)))
                    lines.Add($"- {b.Summary}");

            if (lines.Count == 0) return null;

            _recapRunning = true;
            try
            {
                string prompt =
                    "[Recent events of a fantasy RPG saga]\n" + string.Join("\n", lines) + "\n\n" +
                    "Write a dramatic \"Previously on...\" style recap of these events in 2-4 sentences, " +
                    "second person (\"you\"), no preamble or headings.";

                string resp = await InternalAICall(prompt);
                if (string.IsNullOrWhiteSpace(resp)) return null;

                State.LastSessionRecap = resp.Trim();
                State.LastSessionRecapTurn = State.GlobalTurn;
                Save();
                return State.LastSessionRecap;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Session recap generation error: {ex.Message}");
                return null;
            }
            finally
            {
                _recapRunning = false;
            }
        }

        private static void ParseChapterResponse(string resp, int chapterNumber, out string title, out string recap)
        {
            title = $"Chapter {chapterNumber}";
            recap = resp?.Trim() ?? "";

            if (string.IsNullOrEmpty(resp)) return;

            string pendingRecap = null;
            foreach (var rawLine in resp.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                    title = line.Substring(6).Trim();
                else if (line.StartsWith("Recap:", StringComparison.OrdinalIgnoreCase))
                    pendingRecap = line.Substring(6).Trim();
            }
            if (pendingRecap != null)
                recap = pendingRecap;
        }

        // ---- Save / Load ----

        public static void Save()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "chronicle.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(State, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Save error: {ex.Message}");
            }
        }

        public static void Load(string saveSubDir)
        {
            try
            {
                State = new ChronicleState();
                _uiDirty = true;
                if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, saveSubDir, "chronicle.json");
                if (File.Exists(path))
                {
                    State = JsonConvert.DeserializeObject<ChronicleState>(File.ReadAllText(path))
                            ?? new ChronicleState();
                    Debug.Log($"[Chronicle] State loaded from {saveSubDir} " +
                              $"({State.ClosedChapters.Count} closed chapters, turn {State.GlobalTurn})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Load error: {ex.Message}");
                State = new ChronicleState();
            }
            finally
            {
                // Re-subscribe in case the game nulled TurnHappenedEvent on exit to main menu
                GameplayManager.TurnHappenedEvent -= OnTurnHappened;
                GameplayManager.TurnHappenedEvent += OnTurnHappened;
            }
        }

        // ---- UI Dirty Flag ----

        public static void SetUiDirty() => _uiDirty = true;

        public static bool ConsumeUiDirty()
        {
            bool v = _uiDirty;
            _uiDirty = false;
            return v;
        }
    }
}
