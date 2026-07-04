using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_Reverie
{
    public static class ReverieManager
    {
        // ---- Tuning ----
        public const float DREAM_CHANCE = 0.35f;         // chance a qualifying rest becomes a dream
        public const int MIN_TURNS_BETWEEN_DREAMS = 12;  // cooldown
        public const int DREAM_LENGTH = 5;               // max dream-turns (DREAM_STATE blocks)
        public const int REAL_TURN_TIMEOUT = 8;          // failsafe forced wake (real turns)
        public const int START_LUCIDITY = 3;
        public const int MAX_LUCIDITY = 5;
        public const int TRIUMPH_THRESHOLD = 60;         // progress needed when turns run out
        public const int OMEN_TTL = 40;                  // turns an omen stays live
        public const int MAX_LIVE_OMENS = 2;
        public const int HAUNTING_TTL = 25;              // turns a haunting stays live

        public static ReverieState State { get; private set; } = new ReverieState();

        /// <summary>Set by REVERIE_TEST-adjacent flows to bypass roll + cooldown on the next rest.</summary>
        public static bool ForceNextDream;

        private static readonly System.Random Rng = new System.Random();

        // Strong intent: these verbs are unambiguous sleep
        private static readonly Regex StrongRestRegex = new Regex(
            @"\b(sleep|sleeps|sleeping|nap|naps|napping|doze|dozes|dozing|slumber|slumbers" +
            @"|make camp|makes camp|making camp|set up camp|sets up camp|break camp for the night" +
            @"|bed down|beds down|go to bed|goes to bed|going to bed|turn in for|turns in for)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Weak intent: "rest"/"lie down" only with rest-like context ("the rest of the room" must not match)
        private static readonly Regex WeakRestRegex = new Regex(
            @"\b(take a rest|get some rest|rest (for|at|until|through|here|a while|the night|my|our|until morning)" +
            @"|lie down (and|to|for)|lies down (and|to|for)|lay down (and|to|for))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---- Lifecycle ----

        public static void Init()
        {
            State = new ReverieState();
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
            Debug.Log("[Reverie] ReverieManager initialized.");
        }

        public static void Reset()
        {
            State = new ReverieState();
            ForceNextDream = false;
            Resubscribe();
            Debug.Log("[Reverie] State reset for new game.");
        }

        private static void Resubscribe()
        {
            // Re-subscribe in case the game nulled TurnHappenedEvent on exit to main menu
            GameplayManager.TurnHappenedEvent -= OnTurnHappened;
            GameplayManager.TurnHappenedEvent += OnTurnHappened;
        }

        // ---- Turn tick ----

        private static void OnTurnHappened(int numTurns, long secs)
        {
            if (numTurns <= 0 || State == null) return;
            State.GlobalTurn += numTurns;

            // Expire omens / haunting
            State.Omens?.RemoveAll(o => State.GlobalTurn >= o.ExpiresTurn);
            if (State.ActiveHaunting != null && State.GlobalTurn >= State.ActiveHaunting.ExpiresTurn)
            {
                Debug.Log("[Reverie] The haunting has faded.");
                State.ActiveHaunting = null;
            }

            // Clear the one-shot [WAKING] directive once a full turn has passed with it live
            if (State.PendingWake != WakeOutcome.None && State.GlobalTurn > _pendingWakeSetTurn)
            {
                State.PendingWake = WakeOutcome.None;
                State.PendingWakeSummary = null;
            }

            // Failsafe: if DREAM_STATE blocks stop arriving, don't trap the player in the dream
            if (State.Phase == DreamPhase.Dreaming && State.CurrentDream != null &&
                State.GlobalTurn - State.CurrentDream.StartedTurn >= REAL_TURN_TIMEOUT)
            {
                Debug.LogWarning("[Reverie] Dream real-turn timeout hit — forcing wake.");
                Wake(ResolveByProgress(State.CurrentDream), null);
            }
        }

        private static int _pendingWakeSetTurn = -1;

        // ---- Dream entry ----

        /// <summary>Called from the DoConvoTextFieldSubmission prefix with the raw player action text.</summary>
        public static void OnPlayerAction(string text, GameplayManager manager)
        {
            if (State == null || State.Phase != DreamPhase.Awake) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (manager?.enemyActionsHandler?.currentEnemy != null) return; // no dozing off mid-combat

            bool restIntent = StrongRestRegex.IsMatch(text) || WeakRestRegex.IsMatch(text);
            if (!restIntent) return;

            if (!ForceNextDream)
            {
                if (State.GlobalTurn - State.LastDreamTurn < MIN_TURNS_BETWEEN_DREAMS) return;
                if (Rng.NextDouble() > DREAM_CHANCE) return;
            }
            ForceNextDream = false;

            BeginDream();
        }

        public static void BeginDream()
        {
            if (State.Phase == DreamPhase.Dreaming) return;

            var dream = DreamWeaver.Weave(State);
            try
            {
                var pc = SS.I?.hackyManager?.playerCharacter?.GetPcGameCharacter();
                if (pc != null) dream.HpSnapshot = pc.GetHealth();
            }
            catch (Exception ex) { Debug.LogWarning($"[Reverie] Could not snapshot HP: {ex.Message}"); }

            State.CurrentDream = dream;
            State.Phase = DreamPhase.Dreaming;
            State.LastDreamTurn = State.GlobalTurn;
            State.TotalDreams++;
            Save();

            LogToGame($"— Sleep takes you, and the dream called \"{dream.Theme}\" begins... —");
            Debug.Log($"[Reverie] Dream begun: {dream.Theme} (HP snapshot {dream.HpSnapshot}).");
        }

        // ---- Dream-state processing (from the AI's hidden block) ----

        public static void ProcessDreamStateBlock(string block)
        {
            if (State?.Phase != DreamPhase.Dreaming || State.CurrentDream == null) return;
            var dream = State.CurrentDream;

            int lucidityDelta = 0, progress = -1;
            string evt = null, omen = null;

            foreach (var rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant().Replace(" ", "_");
                string value = line.Substring(colon + 1).Trim();

                switch (key)
                {
                    case "lucidity_delta":
                        int.TryParse(value.TrimStart('+'), out lucidityDelta);
                        lucidityDelta = Mathf.Clamp(lucidityDelta, -1, 1);
                        break;
                    case "progress":
                        // Tolerate "85/100" or "85%"
                        var m = Regex.Match(value, @"\d+");
                        if (m.Success) int.TryParse(m.Value, out progress);
                        break;
                    case "event":
                        evt = value;
                        break;
                    case "omen":
                        omen = value;
                        break;
                }
            }

            dream.Lucidity = Mathf.Clamp(dream.Lucidity + lucidityDelta, 0, MAX_LUCIDITY);
            if (progress >= 0) dream.Progress = Mathf.Clamp(Math.Max(dream.Progress, progress), 0, 100);
            if (!string.IsNullOrWhiteSpace(evt)) dream.Events.Add(evt);
            dream.DreamTurnsRemaining--;

            Debug.Log($"[Reverie] Dream-state: lucidity {dream.Lucidity}/{MAX_LUCIDITY}, " +
                      $"progress {dream.Progress}/100, {dream.DreamTurnsRemaining} dream-turns left. {evt}");

            if (dream.Lucidity <= 0)
                Wake(WakeOutcome.Nightmare, null);
            else if (dream.Progress >= 100)
                Wake(WakeOutcome.Triumph, omen);
            else if (dream.DreamTurnsRemaining <= 0)
                Wake(ResolveByProgress(dream), omen);
        }

        private static WakeOutcome ResolveByProgress(DreamRecord dream)
            => dream.Progress >= TRIUMPH_THRESHOLD ? WakeOutcome.Triumph : WakeOutcome.Neutral;

        // ---- Waking ----

        public static void Wake(WakeOutcome outcome, string omenText)
        {
            var dream = State.CurrentDream;
            if (State.Phase != DreamPhase.Dreaming || dream == null) return;

            // The body was never in danger: restore the falling-asleep snapshot
            try
            {
                var pc = SS.I?.hackyManager?.playerCharacter?.GetPcGameCharacter();
                if (pc != null && dream.HpSnapshot > 0 && pc.GetHealth() < dream.HpSnapshot)
                    pc.SetHealth(dream.HpSnapshot);
            }
            catch (Exception ex) { Debug.LogWarning($"[Reverie] Could not restore HP: {ex.Message}"); }

            string logLine;
            switch (outcome)
            {
                case WakeOutcome.Triumph:
                    State.TotalTriumphs++;
                    if (string.IsNullOrWhiteSpace(omenText))
                        omenText = $"What the dream of \"{dream.Theme}\" promised will find its shape in the waking world.";
                    // Newest omen wins a slot; drop the oldest over the cap
                    State.Omens.Add(new Omen
                    {
                        Text = omenText.Trim(),
                        CreatedTurn = State.GlobalTurn,
                        ExpiresTurn = State.GlobalTurn + OMEN_TTL
                    });
                    while (State.Omens.Count > MAX_LIVE_OMENS) State.Omens.RemoveAt(0);
                    // A triumph banishes any haunting
                    if (State.ActiveHaunting != null)
                    {
                        State.ActiveHaunting = null;
                        LogToGame("— The thing that haunted you loses its grip and is gone. —");
                    }
                    State.PendingWakeSummary =
                        $"The dreamer mastered the dream of \"{dream.Theme}\" and wakes carrying a prophetic omen: \"{omenText.Trim()}\"";
                    logLine = $"— You wake with the dream's shape still sharp in your mind. An omen stays with you: \"{omenText.Trim()}\" —";
                    break;

                case WakeOutcome.Nightmare:
                    State.TotalNightmares++;
                    string hauntText = dream.Events.Count > 0
                        ? $"From the nightmare of \"{dream.Theme}\": {dream.Events.Last().TrimEnd('.')}. It followed the dreamer out."
                        : DreamWeaver.DefaultHauntingText(dream);
                    State.ActiveHaunting = new Haunting
                    {
                        Text = hauntText,
                        CreatedTurn = State.GlobalTurn,
                        ExpiresTurn = State.GlobalTurn + HAUNTING_TTL
                    };
                    State.PendingWakeSummary =
                        $"The dreamer was overwhelmed by the nightmare of \"{dream.Theme}\" — lucidity shattered — and something followed them out of the dream.";
                    logLine = "— You claw awake from the nightmare, heart pounding. Something followed you out. —";
                    break;

                default: // Neutral
                    State.PendingWakeSummary =
                        $"The dream of \"{dream.Theme}\" faded before its heart could be reached; the dreamer wakes unharmed but unanswered.";
                    logLine = "— The dream fades before its heart could be reached. You wake unharmed, but unanswered. —";
                    break;
            }

            State.PendingWake = outcome;
            _pendingWakeSetTurn = State.GlobalTurn;
            State.Phase = DreamPhase.Awake;
            State.LastDreamTurn = State.GlobalTurn;
            State.CurrentDream = null;
            Save();

            LogToGame(logLine);
            Debug.Log($"[Reverie] Woke: {outcome}. Omens live: {State.Omens.Count}, haunted: {State.ActiveHaunting != null}.");
        }

        // ---- Save / Load (Chronicle pattern) ----

        public static void Save()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg, "reverie.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(State, Formatting.Indented));
            }
            catch (Exception ex) { Debug.LogError($"[Reverie] Save error: {ex.Message}"); }
        }

        public static void Load(string saveSubDir)
        {
            try
            {
                State = new ReverieState();
                if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return;
                string path = Path.Combine(SS.I.saveTopLvlDir, saveSubDir, "reverie.json");
                if (File.Exists(path))
                {
                    State = JsonConvert.DeserializeObject<ReverieState>(File.ReadAllText(path))
                            ?? new ReverieState();
                    State.EnsureCollections();
                    Debug.Log($"[Reverie] State loaded from {saveSubDir} " +
                              $"(turn {State.GlobalTurn}, phase {State.Phase}, {State.Omens.Count} omens).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Reverie] Load error: {ex.Message}");
                State = new ReverieState();
            }
            finally
            {
                Resubscribe();
            }
        }

        // ---- Helpers ----

        private static void LogToGame(string line)
        {
            try
            {
                var logView = SS.I?.hackyManager?.gameLogView;
                if (logView != null) _ = logView.LogText($"\n{line}\n");
            }
            catch (Exception ex) { Debug.LogWarning($"[Reverie] LogToGame failed: {ex.Message}"); }
        }

        /// <summary>Live omens, expiry re-checked at read time (provider calls this).</summary>
        public static System.Collections.Generic.List<Omen> LiveOmens()
        {
            var list = new System.Collections.Generic.List<Omen>();
            if (State?.Omens == null) return list;
            foreach (var o in State.Omens)
                if (State.GlobalTurn < o.ExpiresTurn) list.Add(o);
            return list;
        }

        public static Haunting LiveHaunting()
        {
            var h = State?.ActiveHaunting;
            return (h != null && State.GlobalTurn < h.ExpiresTurn) ? h : null;
        }
    }
}
