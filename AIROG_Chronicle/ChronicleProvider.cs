using System;
using System.Linq;
using System.Text;
using AIROG_GenContext;

namespace AIROG_Chronicle
{
    public class ChronicleProvider : IContextProvider
    {
        public string Name        => "Chronicle";
        public int    Priority    => 88;  // above WorldContext(80)/SkillWeb(85), below Settlement(90)/DmNotes(95)/NPC(200)
        public string Description => "Injects a compact narrative timeline so the AI remembers story arcs from earlier in the session, keeping context lean.";

        public string GetContext(string prompt, int maxTokens)
        {
            var state = ChronicleManager.State;
            if (state == null || maxTokens <= 0) return "";

            var sb = new StringBuilder();

            bool hasClosedChapters = state.ClosedChapters != null && state.ClosedChapters.Count > 0;
            bool hasCurrentBeats   = state.CurrentChapter?.Beats != null && state.CurrentChapter.Beats.Count > 0;

            // Closed chapters — one compact line each (title + recap)
            if (hasClosedChapters)
            {
                sb.AppendLine("[CHRONICLE — Story So Far]");
                foreach (var ch in state.ClosedChapters)
                {
                    string title = string.IsNullOrEmpty(ch.Title)
                        ? $"Chapter {ch.Number}"
                        : $"Ch.{ch.Number} \"{ch.Title}\"";
                    string recap = string.IsNullOrEmpty(ch.Recap) ? "(no summary yet)" : ch.Recap;
                    sb.AppendLine($"{title} (T{ch.StartTurn}–{ch.EndTurn}): {recap}");
                }
                sb.AppendLine();
            }

            // Current chapter — individual beats (most recent 10)
            if (hasCurrentBeats)
            {
                var cur = state.CurrentChapter;
                sb.AppendLine($"[CHRONICLE — Current Chapter (Turn {cur.StartTurn}+)]");
                var recentBeats = cur.Beats.Skip(Math.Max(0, cur.Beats.Count - 10)).ToList();
                foreach (var b in recentBeats)
                    sb.AppendLine($"T{b.Turn}: {b.Summary}{(b.IsMilestone ? " ★" : "")}");
                sb.AppendLine();
            }

            // Always inject the beat instruction — without this the AI never produces the block,
            // which means beats are never recorded (bootstrap deadlock on new games).
            sb.AppendLine("[CHRONICLE INSTRUCTION: At the end of your response, append a hidden block in exactly this format:]");
            sb.AppendLine("<CHRONICLE_BEAT>");
            sb.AppendLine("event_type: narrative");
            sb.AppendLine("summary: [One sentence describing the key event that just happened]");
            sb.AppendLine("is_milestone: false");
            sb.AppendLine("</CHRONICLE_BEAT>");
            sb.AppendLine("[Replace event_type with: combat, arrival, death, levelup, or quest if more fitting. Set is_milestone: true for major plot moments. This block will be stripped before the player sees the response.]");

            string result = sb.ToString().TrimEnd();
            int maxChars = maxTokens >= int.MaxValue / 4 ? result.Length : maxTokens * 4;
            if (maxChars <= 0) return "";
            return result.Length > maxChars ? result.Substring(0, maxChars) + "..." : result;
        }
    }
}
