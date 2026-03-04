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
            sb.AppendLine("At the very START of your response, output a <DM_NOTES> block (it will be hidden from the player).");
            sb.AppendLine("Format it exactly like this:");
            sb.AppendLine("<DM_NOTES>");
            sb.AppendLine("player_state: [Engaged/Neutral/Impatient — based on the length and tone of the player's last input]");
            sb.AppendLine("pacing: [Fast/Medium/Slow — how much narrative detail to use this turn]");
            sb.AppendLine("engagement: [One sentence summarising what the player seems most interested in]");
            sb.AppendLine("plot_threads: [Any new hooks or consequences to track; use semicolons to separate multiple items; or write 'none']");
            sb.AppendLine("preferences: [New observations about player preferences; semicolon-separated; or 'none']");
            sb.AppendLine("</DM_NOTES>");
            sb.AppendLine("After </DM_NOTES>, write your normal story response.");

            bool hasState = state.PlayerState != "Unknown"
                            || state.PlotThreads.Count > 0
                            || state.PreferenceNotes.Count > 0;

            if (hasState)
            {
                sb.AppendLine("\n[CURRENT DM STATE]");
                if (state.PlayerState != "Unknown")
                    sb.AppendLine($"Player engagement: {state.PlayerState}. Preferred pacing: {state.PacingDecision}.");
                if (!string.IsNullOrEmpty(state.EngagementAnalysis))
                    sb.AppendLine($"Last analysis: {state.EngagementAnalysis}");
                if (state.PlotThreads.Count > 0)
                    sb.AppendLine("Active plot threads: " + string.Join("; ", state.PlotThreads.Take(5)));
                if (state.PreferenceNotes.Count > 0)
                    sb.AppendLine("Player preferences: " + string.Join("; ", state.PreferenceNotes.Take(4)));
            }

            return sb.ToString();
        }
    }
}
