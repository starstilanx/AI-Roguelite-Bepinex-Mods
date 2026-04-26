using System.Linq;
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
            if (!ContextManager.GetGlobalSetting("DMNotes")) return "";

            var state = DmNotesManager.CurrentState;
            var sb = new StringBuilder();

            sb.AppendLine("[DM DIRECTOR]");

            bool hasState = state.PlayerState != "Unknown"
                            || state.PlotThreads.Count > 0
                            || state.PreferenceNotes.Count > 0;

            if (hasState)
            {
                sb.AppendLine("[CURRENT DM STATE — apply this, then rewrite it]");
                if (state.PlayerState != "Unknown")
                    sb.AppendLine($"Engagement: {state.PlayerState} | Pacing: {state.PacingDecision} (Fast=brief, Medium=standard, Slow=rich detail)");
                if (!string.IsNullOrEmpty(state.EngagementAnalysis))
                    sb.AppendLine($"Analysis: {state.EngagementAnalysis}");
                if (state.PlotThreads.Count > 0)
                    sb.AppendLine("Plot threads (weave these into your response): " + string.Join("; ", state.PlotThreads));
                if (state.PreferenceNotes.Count > 0)
                    sb.AppendLine("Player preferences (tailor your writing to these): " + string.Join("; ", state.PreferenceNotes));
            }

            sb.AppendLine("Output a hidden <DM_NOTES> block at the START of your response:");
            sb.AppendLine("<DM_NOTES>");
            sb.AppendLine("player_state: [Engaged/Neutral/Impatient]");
            sb.AppendLine("pacing: [Fast/Medium/Slow]");
            sb.AppendLine("engagement: [one sentence]");
            sb.AppendLine("plot_threads: [FULL updated list — drop resolved, merge similar, add new; max 8; semicolons; or 'none']");
            sb.AppendLine("preferences: [FULL updated list — consolidate similar; max 6; semicolons; or 'none']");
            sb.AppendLine("</DM_NOTES>");

            return sb.ToString();
        }
    }
}
