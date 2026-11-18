using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Chronicle
{
    public static class ChronicleManager
    {
        // Number of turns before a chapter auto-closes
        private const int CHAPTER_LENGTH = 15;

        // After a background AI call fails, wait this many turns before starting another
        private const int RETRY_BACKOFF_TURNS = 5;
        // Attempts per chapter / act per session; a backend that keeps failing is not hammered forever
        private const int MAX_ATTEMPTS = 3;
        // Story text kept for the fallback beat (the oldest turns are dropped past this)
        private const int STORY_BUFFER_MAX_CHARS = 6000;
        // Story turns kept for tag recall
        private const int RECENT_STORY_TURNS = 2;

        public static ChronicleState State { get; private set; } = new ChronicleState();

        // Set true during internal AI calls so the interceptor skips them
        public static bool IsInternalCall { get; private set; }

        private static bool _uiDirty;

        // The save folder State was loaded from / created for. Save() refuses to write anywhere
        // else: between "the game points at another save" and "our load hook ran" the in-memory
        // state still belongs to the previous save.
        private static string _stateDir;

        // ---- Per-session bookkeeping (never saved; cleared on load and on new game) ----

        private static readonly List<string> _unsummarized = new List<string>(); // story turns since the last story beat
        private static int _turnsSinceBeat;
        private static readonly List<string> _recentStory = new List<string>();
        private static readonly HashSet<Chapter> _chaptersInFlight = new HashSet<Chapter>();
        private static readonly Dictionary<Chapter, int> _recapAttempts = new Dictionary<Chapter, int>();
        private static readonly Dictionary<ChronicleAct, int> _actAttempts = new Dictionary<ChronicleAct, int>();
        private static ChronicleAct _actInFlight;
        private static ChronicleState _fallbackFor;   // the state a fallback-beat call is running for
        private static int _retryNotBeforeTurn;
        private static bool _derivedReady;

        // ---- Lifecycle ----

        public static void Init()
        {
            State = new ChronicleState();
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Debug.Log("[Chronicle] ChronicleManager initialized.");
        }

        private static void OnTurnHappened(int numTurns, long secs)
        {
            // Runs inside the game's turn pipeline: an exception here would break the turn.
            try
            {
                if (numTurns <= 0 || State == null || !ModSwitch.Enabled) return;
                State.GlobalTurn += numTurns;
                PumpBackgroundWork();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Turn handler error: {ex.Message}");
            }
        }

        public static void Reset()
        {
            State = new ChronicleState();
            _stateDir = SS.I != null ? SS.I.saveSubDirAsArg : null;
            ResetSession();
            _uiDirty = true;
            // Re-subscribe in case the game nulled TurnHappenedEvent on exit to main menu
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Debug.Log("[Chronicle] State reset for new game.");
        }

        private static void ResetSession()
        {
            _unsummarized.Clear();
            _turnsSinceBeat = 0;
            _recentStory.Clear();
            _recapAttempts.Clear();
            _actAttempts.Clear();
            _retryNotBeforeTurn = 0;
            _derivedReady = false;
            // Calls still running for the previous save finish on their own and drop their result;
            // they must not hold up this save's background work in the meantime.
            _chaptersInFlight.Clear();
            _actInFlight = null;
            _fallbackFor = null;
        }

        public static string PlayerName()
        {
            // playerCharacter is a MonoBehaviour, so playerCharacter.name is the scene object's name.
            try { return SS.I?.hackyManager?.playerCharacter?.GetPlayerNameWFallback(); }
            catch { return null; }
        }

        /// <summary>
        /// Brings data derived from the chapters up to date: tags for chapters saved before tags
        /// existed, and the grouping of aged-out chapters into acts. Not done in Load() because
        /// the player's name (which must not become a tag) is unknown until the game has loaded.
        /// </summary>
        public static void EnsureDerived()
        {
            if (_derivedReady || State == null) return;
            _derivedReady = true;
            try
            {
                var exclude = ChronicleText.BuildBlacklist(null, PlayerName());
                foreach (var ch in State.ClosedChapters)
                    if (ch.Tags == null) ch.Tags = HeuristicTagsFor(ch, exclude);
                var s = ChroniclePlugin.Settings();
                if (ChronicleComposer.FormActs(State, s.RecentChapters, s.ChaptersPerAct) > 0)
                    _uiDirty = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] EnsureDerived error: {ex.Message}");
            }
        }

        private static List<string> HeuristicTagsFor(Chapter ch, HashSet<string> exclude)
        {
            var texts = new List<string> { ch.Recap };
            if (ch.Beats != null) texts.AddRange(ch.Beats.Select(b => b?.Summary));
            return ChronicleText.HeuristicTags(texts, exclude);
        }

        // ---- Beat Processing ----

        /// <summary>Records the beat described by a hidden block. Returns false if the block held no usable summary.</summary>
        public static bool ProcessBeatBlock(string block, bool requireCompleteSummary = false, int? turn = null)
        {
            try
            {
                if (!ChronicleText.TryParseBeat(block, out string type, out string summary, out bool milestone, out bool complete))
                    return false;
                if (requireCompleteSummary && !complete) return false;

                return RecordBeat(new ChronicleBeat
                {
                    Turn = turn ?? State?.GlobalTurn ?? 0,
                    Type = ChronicleText.ParseBeatType(type),
                    Summary = summary,
                    IsMilestone = milestone
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] ProcessBeatBlock error: {ex.Message}");
                return false;
            }
        }

        public static bool RecordBeat(ChronicleBeat beat)
        {
            // Entry point for other mods too (Insight calls it by reflection): switched off means inert.
            if (beat == null || string.IsNullOrWhiteSpace(beat.Summary) || !ModSwitch.Enabled) return false;

            if (State == null) State = new ChronicleState();
            if (State.CurrentChapter?.Beats == null || State.ClosedChapters == null) State.EnsureCollections();

            // A beat can arrive late: the fallback call answers a turn or two after the passage it
            // summarises, and a second beat can land on the very turn a chapter closed. File it in
            // the chapter its turn belongs to, in turn order.
            Chapter lastClosed = State.ClosedChapters.Count > 0 ? State.ClosedChapters[State.ClosedChapters.Count - 1] : null;
            bool intoClosed = lastClosed != null && beat.Turn < State.CurrentChapter.StartTurn && beat.Turn <= lastClosed.EndTurn;
            var beats = intoClosed ? lastClosed.Beats : State.CurrentChapter.Beats;
            int at = beats.Count;
            while (at > 0 && beats[at - 1].Turn > beat.Turn) at--;

            // Skip exact repeats of the neighbouring beat (the AI sometimes re-emits the same summary)
            if (at > 0 && string.Equals(beats[at - 1].Summary, beat.Summary, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[Chronicle] Duplicate beat skipped: {beat.Summary}");
                return false;
            }

            beats.Insert(at, beat);
            _uiDirty = true;

            Debug.Log($"[Chronicle] Beat recorded: T{beat.Turn} [{beat.Type}] {beat.Summary}");
            if (intoClosed) return true;

            // Close chapter after CHAPTER_LENGTH turns (guard: not already closing, not in an internal call)
            int turnsInChapter = State.GlobalTurn - State.CurrentChapter.StartTurn;
            if (turnsInChapter >= CHAPTER_LENGTH && State.CurrentChapter.EndTurn == -1 && !IsInternalCall)
            {
                var chapterToClose = State.CurrentChapter;
                chapterToClose.EndTurn = State.GlobalTurn;

                // Usable tags straight away; the AI call below replaces them with better ones.
                chapterToClose.Tags = HeuristicTagsFor(chapterToClose, ChronicleText.BuildBlacklist(null, PlayerName()));

                // Add to closed list immediately so saves don't lose the chapter
                State.ClosedChapters.Add(chapterToClose);

                // Open a new chapter right away so incoming beats go to the right place
                State.CurrentChapter = new Chapter
                {
                    Number = chapterToClose.Number + 1,
                    StartTurn = State.GlobalTurn + 1,
                    EndTurn = -1
                };

                var s = ChroniclePlugin.Settings();
                ChronicleComposer.FormActs(State, s.RecentChapters, s.ChaptersPerAct);

                // Persist the close immediately — the AI title/recap fills in later via its own Save()
                Save();

                // Fire-and-forget: AI generates title + recap + tags in background
                _ = GenerateTitleAndRecapAsync(chapterToClose);
            }
            return true;
        }

        // ---- Fallback beats ----

        /// <summary>The newest story turns Chronicle has seen, for tag recall.</summary>
        public static string RecentStoryText => string.Join("\n", _recentStory);

        /// <summary>
        /// Called once per story response. <paramref name="beatRecorded"/>: the response carried a
        /// usable hidden beat. When enough turns go by without one, Chronicle writes the beat itself.
        /// </summary>
        public static void OnStoryTurn(string storyText, bool beatRecorded)
        {
            if (string.IsNullOrWhiteSpace(storyText)) return; // no story this turn (blocked action, out-of-character reply)

            _recentStory.Add(storyText);
            while (_recentStory.Count > RECENT_STORY_TURNS) _recentStory.RemoveAt(0);

            if (beatRecorded)
            {
                _unsummarized.Clear();
                _turnsSinceBeat = 0;
                return;
            }

            _unsummarized.Add(storyText.Trim());
            while (_unsummarized.Count > 1 && _unsummarized.Sum(t => t.Length) > STORY_BUFFER_MAX_CHARS)
                _unsummarized.RemoveAt(0);
            _turnsSinceBeat++;

            var s = ChroniclePlugin.Settings();
            if (!s.FallbackBeats || _fallbackFor != null) return;
            if (_turnsSinceBeat < Math.Max(1, s.FallbackBeatTurns)) return;
            _ = GenerateFallbackBeatAsync();
        }

        private static async Task GenerateFallbackBeatAsync()
        {
            var state = State;
            int covered = _unsummarized.Count;
            int turn = state.GlobalTurn;
            _fallbackFor = state;
            try
            {
                string passage = string.Join("\n\n", _unsummarized);
                if (passage.Length > STORY_BUFFER_MAX_CHARS) passage = passage.Substring(passage.Length - STORY_BUFFER_MAX_CHARS);

                string prompt =
                    "[Latest passage of an ongoing RPG story]\n" + passage + "\n\n" +
                    "Record the single most important thing that happened in this passage. " +
                    "Reply with exactly these three lines and nothing else (keep the labels in English):\n" +
                    "event_type: [one of: narrative, combat, arrival, death, quest]\n" +
                    "summary: [one sentence, naming who was involved and where]\n" +
                    "is_milestone: [true only for a major plot moment, otherwise false]";

                string resp = await InternalAICall(prompt, background: true);

                if (!ReferenceEquals(State, state)) return; // another save was loaded while waiting

                // Models that ignore the format usually still answer with the one sentence asked for.
                bool recorded = ProcessBeatBlock(resp, turn: turn)
                             || (!ChronicleText.HasBeatKeys(resp) && ProcessBeatBlock("summary: " + FirstLine(resp), turn: turn));
                if (!recorded && string.IsNullOrWhiteSpace(resp)) throw new Exception("empty response");

                _unsummarized.RemoveRange(0, Math.Min(covered, _unsummarized.Count));
                _turnsSinceBeat = _unsummarized.Count;
            }
            catch (Exception ex)
            {
                // Keep the text and try again after another quiet stretch, with the longer passage.
                if (ReferenceEquals(State, state)) _turnsSinceBeat = 0;
                Debug.LogWarning($"[Chronicle] Fallback beat failed, will retry later: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_fallbackFor, state)) _fallbackFor = null;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            foreach (string line in text.Split('\n'))
                if (line.Trim().Length > 0) return ChronicleText.Clamp(line, 300);
            return "";
        }

        // ---- Background upkeep (one AI job at a time, started from the turn tick) ----

        private static void PumpBackgroundWork()
        {
            EnsureDerived();
            if (State.GlobalTurn < _retryNotBeforeTurn) return;
            if (_chaptersInFlight.Count > 0 || _actInFlight != null) return;

            var s = ChroniclePlugin.Settings();

            // A closed chapter whose title/recap call failed, or was lost to a reload mid-call.
            // Only the chapters still feeding the prompt directly or about to feed an act.
            var closed = State.ClosedChapters;
            int oldest = Math.Max(0, closed.Count - Math.Max(1, s.RecentChapters + s.ChaptersPerAct));
            for (int i = closed.Count - 1; i >= oldest; i--)
            {
                var ch = closed[i];
                if (!string.IsNullOrWhiteSpace(ch.Recap) || ch.Beats.Count == 0) continue;
                _recapAttempts.TryGetValue(ch, out int tries);
                if (tries >= MAX_ATTEMPTS) continue;
                _recapAttempts[ch] = tries + 1;
                _ = GenerateTitleAndRecapAsync(ch);
                return;
            }

            // Summaries for the acts that go into the prompt, newest first.
            if (s.ChaptersPerAct <= 0 || s.MaxActsInPrompt <= 0) return;
            foreach (var act in State.Acts.OrderByDescending(a => a.Number).Take(s.MaxActsInPrompt))
            {
                if (!string.IsNullOrWhiteSpace(act.Summary)) continue;
                _actAttempts.TryGetValue(act, out int tries);
                if (tries >= MAX_ATTEMPTS) continue;
                _actAttempts[act] = tries + 1;
                _ = GenerateActSummaryAsync(act);
                return;
            }
        }

        public static bool IsActBusy(ChronicleAct act) => ReferenceEquals(_actInFlight, act);

        /// <summary>Panel button: summarise an act that is older than the ones kept up to date automatically.</summary>
        public static void RequestActSummary(ChronicleAct act)
        {
            if (act == null || _actInFlight != null || !ModSwitch.Enabled) return;
            _ = GenerateActSummaryAsync(act);
        }

        private static async Task GenerateActSummaryAsync(ChronicleAct act)
        {
            var state = State;
            _actInFlight = act;
            _uiDirty = true;
            try
            {
                var lines = ChronicleComposer.ChaptersOf(act, state.ClosedChapters)
                    .Select(c => ChronicleComposer.ChapterLine(c, 600))
                    .Where(l => l != null).ToList();
                if (lines.Count == 0) throw new Exception("its chapters have no recaps or beats");

                string prompt =
                    "[Chapter recaps from one act of an ongoing RPG saga, in order]\n" + string.Join("\n", lines) + "\n\n" +
                    "Condense these chapters into one act summary of 2 sentences (at most 50 words), in the past tense, " +
                    "keeping the names of the key people and places. Write only the summary, with no title or preamble.";

                string summary = ChronicleText.CleanSummaryReply(await InternalAICall(prompt, background: true), 700);
                if (summary.Length == 0) throw new Exception("empty response");

                act.Summary = summary;
                if (!ReferenceEquals(State, state)) return;
                Save();
                Debug.Log($"[Chronicle] Act {act.Number} summarised.");
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(State, state)) _retryNotBeforeTurn = state.GlobalTurn + RETRY_BACKOFF_TURNS;
                Debug.LogWarning($"[Chronicle] Act {act.Number} summary failed, will retry later: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_actInFlight, act)) _actInFlight = null;
                _uiDirty = true;
            }
        }

        // ---- AI Chapter Close ----

        private static async Task GenerateTitleAndRecapAsync(Chapter chapter)
        {
            if (!_chaptersInFlight.Add(chapter)) return;
            var state = State;
            try
            {
                string beatList = string.Join("\n",
                    chapter.Beats.Select(b => $"- {b.Summary}"));
                if (string.IsNullOrWhiteSpace(beatList))
                    beatList = "- (no recorded beats)";

                string prompt =
                    "[Fantasy RPG story beats from a single chapter]\n" + beatList + "\n\n" +
                    "Write exactly three lines (keep the labels Title, Recap and Tags in English):\n" +
                    "Title: [a dramatic chapter title, max 6 words]\n" +
                    "Recap: [a 1-2 sentence summary of what happened]\n" +
                    "Tags: [3 to 6 comma-separated names of the people, places or things this chapter is about, exactly as written in the story; never the protagonist]";

                string resp = await InternalAICall(prompt, background: true);

                ChronicleText.ParseChapterResponse(resp, chapter.Number, out string title, out string recap, out var tags);
                if (recap.Length == 0) throw new Exception("no recap in the response");

                chapter.Title = title;
                chapter.Recap = recap;
                var exclude = ChronicleText.BuildBlacklist(null, PlayerName());
                tags.RemoveAll(exclude.Contains);
                if (tags.Count > 0) chapter.Tags = tags;

                if (!ReferenceEquals(State, state)) return; // another save was loaded while waiting
                _uiDirty = true;
                Save();
                Debug.Log($"[Chronicle] Chapter {chapter.Number} titled: \"{title}\"");
            }
            catch (Exception ex)
            {
                // The chapter keeps an empty recap, which is what makes the turn tick try it again.
                if (ReferenceEquals(State, state)) _retryNotBeforeTurn = state.GlobalTurn + RETRY_BACKOFF_TURNS;
                Debug.LogWarning($"[Chronicle] Chapter {chapter.Number} title/recap failed, will retry later: {ex.Message}");
            }
            finally
            {
                _chaptersInFlight.Remove(chapter);
            }
        }

        /// <summary>
        /// Fires an internal AI call with IsInternalCall raised only around the synchronous kickoff.
        /// The interceptor's Harmony postfix runs at kickoff time, so this is sufficient to make it
        /// skip this call — while leaving concurrent player-turn responses intercepted normally.
        /// (Holding the flag across the await made concurrent turns leak raw beat blocks on screen.)
        ///
        /// Sent as GENERAL_QUESTION_ANSWERER, not STORY_COMPLETER: a STORY_COMPLETER call gets the
        /// story system prompt (in unified mode the JSON one) and is taken for a story turn by
        /// every mod that watches story output (loop detection, quest completion, ...).
        /// Background calls queue behind the player's own turn, and are not tied to the turn's
        /// cancel token.
        /// </summary>
        private static Task<string> InternalAICall(string prompt, bool background)
        {
            IsInternalCall = true;
            try
            {
                return AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER, prompt,
                    background: background, forceEventCheckModel: true, forceConcise: true,
                    ct: background ? CancellationToken.None : (CancellationToken?)null);
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
            var state = State;
            if (state == null) return null;

            // Cached and still current for this turn — reuse it
            if (!string.IsNullOrEmpty(state.LastSessionRecap) && state.LastSessionRecapTurn == state.GlobalTurn)
                return state.LastSessionRecap;

            if (_recapRunning) return null;

            var lines = new List<string>();
            if (state.ClosedChapters != null)
                foreach (var ch in state.ClosedChapters.Skip(Math.Max(0, state.ClosedChapters.Count - 3)))
                    if (!string.IsNullOrEmpty(ch.Recap))
                        lines.Add($"Chapter {ch.Number}{(string.IsNullOrEmpty(ch.Title) ? "" : $" \"{ch.Title}\"")}: {ch.Recap}");
            if (state.CurrentChapter?.Beats != null)
                foreach (var b in state.CurrentChapter.Beats.Skip(Math.Max(0, state.CurrentChapter.Beats.Count - 12)))
                    lines.Add($"- {b.Summary}");

            if (lines.Count == 0) return null;

            _recapRunning = true;
            try
            {
                string prompt =
                    "[Recent events of a fantasy RPG saga]\n" + string.Join("\n", lines) + "\n\n" +
                    "Write a dramatic \"Previously on...\" style recap of these events in 2-4 sentences, " +
                    "second person (\"you\"), no preamble or headings.";

                string resp = await InternalAICall(prompt, background: false);
                if (string.IsNullOrWhiteSpace(resp)) return null;

                state.LastSessionRecap = resp.Trim();
                state.LastSessionRecapTurn = state.GlobalTurn;
                if (ReferenceEquals(State, state)) Save();
                return state.LastSessionRecap;
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

        // ---- Save / Load ----

        public static void Save()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                if (SS.I.saveSubDirAsArg != _stateDir) return;
                // Clients never write saves (the game's own WriteSaveFile returns early for them too)
                if (MpSession.IsClient) return;
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
            string path = null;
            try
            {
                State = new ChronicleState();
                _stateDir = saveSubDir;
                ResetSession();
                _uiDirty = true;
                if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return;
                path = Path.Combine(SS.I.saveTopLvlDir, saveSubDir, "chronicle.json");
                if (File.Exists(path))
                {
                    State = JsonConvert.DeserializeObject<ChronicleState>(File.ReadAllText(path))
                            ?? new ChronicleState();
                    State.EnsureCollections();
                    Debug.Log($"[Chronicle] State loaded from {saveSubDir} " +
                              $"({State.ClosedChapters.Count} closed chapters, turn {State.GlobalTurn})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Chronicle] Load error: {ex.Message}");
                State = new ChronicleState();
                // The next save would overwrite the unreadable file (often a hand edit with a typo)
                // with an empty chronicle: keep a copy the player can repair.
                try { if (path != null && File.Exists(path)) File.Copy(path, path + ".unreadable", true); }
                catch { }
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
