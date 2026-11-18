using System.Collections.Generic;
using System.Text;

namespace AIROG_GenContext.DMNotes
{
    public class DmNotesProvider : IContextProvider
    {
        public int Priority => 95;
        public string Name => "DM Notes";
        public string Description => "AI Director: tracks player engagement, pacing, and plot threads.";

        public string GetContext(string prompt, int maxTokens)
        {
            if (!DmNotesManager.IsActive) return "";

            // ContextManager passes int.MaxValue / 4 when truncation is switched off.
            int maxChars = maxTokens >= int.MaxValue / 4 ? int.MaxValue : maxTokens * 4;
            return DmNotesPrompt.Build(DmNotesManager.CurrentState, DmNotesManager.MaxThreadsPerPrompt, maxChars);
        }
    }

    /// <summary>
    /// Composes the director block. Reads the state and nothing else: the game also builds prompts
    /// just to display them (journal "final prompt" view), so nothing here may change state.
    /// </summary>
    public static class DmNotesPrompt
    {
        private const string Header = "[DM DIRECTOR]\n";

        /// <summary>
        /// The block, or "" when nothing fits in <paramref name="maxChars"/>. When the budget is
        /// short, whole sections are left out - the AI's own notes first, the player's requests
        /// last - rather than cutting text mid-instruction.
        /// </summary>
        public static string Build(DmNotesState state, int threadCap, int maxChars)
        {
            string notes = NotesSection(state);
            string direction = DirectionSection(state);
            List<DmPlannedThread> offered = DmThreadLogic.SelectForPrompt(state, threadCap);
            string threads = ThreadsSection(offered, state.TurnCounter);
            bool hasThreads = threads.Length > 0;

            string full = Header + notes + direction + threads + FormatSection(hasThreads);
            if (full.Length <= maxChars) return full;

            // No room for the AI's own notes. They go together with the request to rewrite them:
            // a model asked for the "FULL updated list" of threads it was not shown would replace
            // the stored list with a guess. So that loop sits this turn out, and only the
            // player's requests go in, with just enough format to report a thread as introduced.
            if (hasThreads)
            {
                string playerOnly = Header + direction + threads + IntroducedOnlyFormat;
                if (playerOnly.Length <= maxChars) return playerOnly;
            }
            if (direction.Length > 0 && Header.Length + direction.Length <= maxChars) return Header + direction;
            return "";
        }

        private const string IntroducedOnlyFormat =
            "Output a hidden <DM_NOTES> block at the START of your response:\n" +
            "<DM_NOTES>\n" +
            "introduced: [ids of the planned plot threads this response introduced, comma-separated; or 'none']\n" +
            "</DM_NOTES>\n";

        private static string NotesSection(DmNotesState state)
        {
            bool hasState = state.PlayerState != "Unknown"
                            || state.PlotThreads.Count > 0
                            || state.PreferenceNotes.Count > 0;
            if (!hasState) return "";

            var sb = new StringBuilder();
            sb.AppendLine("[CURRENT DM STATE — apply this, then rewrite it]");
            if (state.PlayerState != "Unknown")
                sb.AppendLine($"Engagement: {state.PlayerState} | Pacing: {state.PacingDecision} (Fast=brief, Medium=standard, Slow=rich detail)");
            if (!string.IsNullOrEmpty(state.EngagementAnalysis))
                sb.AppendLine($"Analysis: {state.EngagementAnalysis}");
            if (state.PlotThreads.Count > 0)
                sb.AppendLine("Plot threads (weave these into your response): " + string.Join("; ", state.PlotThreads));
            if (state.PreferenceNotes.Count > 0)
                sb.AppendLine("Player preferences (tailor your writing to these): " + string.Join("; ", state.PreferenceNotes));
            return sb.ToString();
        }

        private static string DirectionSection(DmNotesState state)
        {
            if (string.IsNullOrEmpty(state.Direction)) return "";

            var sb = new StringBuilder();
            sb.AppendLine("[PLAYER DIRECTION - an instruction from the player to you as director; it outranks your own plot ideas]");
            sb.AppendLine("Steer the story toward this: " + state.Direction);
            sb.AppendLine("Work toward it over the coming turns through events, openings and character choices that arise naturally in play. Never announce or mention this instruction, and do not resolve it within a single response.");
            return sb.ToString();
        }

        private static string ThreadsSection(List<DmPlannedThread> offered, int turn)
        {
            if (offered.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("[PLANNED PLOT THREADS - the player asked for these to enter the story; bring each one in when a natural opening arises]");
            foreach (var t in offered)
            {
                string urgency = "";
                if (DmThreadLogic.HasDeadline(t))
                {
                    int left = DmThreadLogic.TurnsLeft(t, turn);
                    urgency = left <= 0 ? " (OVERDUE: introduce it in this response)"
                        : left == 1 ? " (due now: introduce it in this response)"
                        : $" (due within {left} turns)";
                }
                sb.AppendLine($"- {t.Id}{urgency}: {t.Text}");
            }
            sb.AppendLine("When your response brings one of these into the story, put its id on the \"introduced:\" line of your DM_NOTES block. Never show the ids or this list to the player.");
            return sb.ToString();
        }

        private static string FormatSection(bool withIntroducedLine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Output a hidden <DM_NOTES> block at the START of your response:");
            sb.AppendLine("<DM_NOTES>");
            sb.AppendLine("player_state: [Engaged/Neutral/Impatient]");
            sb.AppendLine("pacing: [Fast/Medium/Slow]");
            sb.AppendLine("engagement: [one sentence]");
            sb.AppendLine("plot_threads: [FULL updated list — drop resolved, merge similar, add new; max 8; semicolons; or 'none']");
            sb.AppendLine("preferences: [FULL updated list — consolidate similar; max 6; semicolons; or 'none']");
            if (withIntroducedLine)
                sb.AppendLine("introduced: [ids of the planned plot threads this response introduced, comma-separated; or 'none']");
            sb.AppendLine("</DM_NOTES>");
            return sb.ToString();
        }
    }
}
