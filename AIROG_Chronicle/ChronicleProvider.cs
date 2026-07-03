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

        // Without a cap, this section gains a line every CHAPTER_LENGTH turns forever. On a long save
        // that eventually eats most of GenContext's shared token budget and pushes the truncation cut
        // (below) into the current-chapter beats / beat-recording instruction instead of old recaps.
        private const int MAX_CLOSED_CHAPTERS_SHOWN = 10;

        public string GetContext(string prompt, int maxTokens)
        {
            var state = ChronicleManager.State;
            if (state == null || maxTokens <= 0) return "";

            bool hasClosedChapters = state.ClosedChapters != null && state.ClosedChapters.Count > 0;
            bool hasCurrentBeats   = state.CurrentChapter?.Beats != null && state.CurrentChapter.Beats.Count > 0;

            // Must-have part: current-chapter beats + the beat-recording instruction. Built first so
            // it's protected from truncation below — losing the instruction silently stops the AI from
            // producing <CHRONICLE_BEAT> blocks, which stops beat recording entirely.
            var bodySb = new StringBuilder();

            if (hasCurrentBeats)
            {
                var cur = state.CurrentChapter;
                bodySb.AppendLine($"[CHRONICLE — Current Chapter (Turn {cur.StartTurn}+)]");
                var recentBeats = cur.Beats.Skip(Math.Max(0, cur.Beats.Count - 10)).ToList();
                foreach (var b in recentBeats)
                    bodySb.AppendLine($"T{b.Turn}: {b.Summary}{(b.IsMilestone ? " ★" : "")}");
                bodySb.AppendLine();
            }

            bodySb.AppendLine("[CHRONICLE INSTRUCTION: At the end of your response, append a hidden block in exactly this format:]");
            bodySb.AppendLine("<CHRONICLE_BEAT>");
            bodySb.AppendLine("event_type: narrative");
            bodySb.AppendLine("summary: [One sentence describing the key event that just happened]");
            bodySb.AppendLine("is_milestone: false");
            bodySb.AppendLine("</CHRONICLE_BEAT>");
            bodySb.AppendLine("[Replace event_type with: combat, arrival, death, levelup, or quest if more fitting. Set is_milestone: true for major plot moments. This block will be stripped before the player sees the response.]");

            // Soft part: closed-chapter recaps, capped to the most recent N so this can't grow forever.
            var headSb = new StringBuilder();
            if (hasClosedChapters)
            {
                headSb.AppendLine("[CHRONICLE — Story So Far]");
                int skip = Math.Max(0, state.ClosedChapters.Count - MAX_CLOSED_CHAPTERS_SHOWN);
                if (skip > 0)
                    headSb.AppendLine($"(+ {skip} earlier chapter{(skip == 1 ? "" : "s")} omitted for length)");
                foreach (var ch in state.ClosedChapters.Skip(skip))
                {
                    string title = string.IsNullOrEmpty(ch.Title)
                        ? $"Chapter {ch.Number}"
                        : $"Ch.{ch.Number} \"{ch.Title}\"";
                    string recap = string.IsNullOrEmpty(ch.Recap) ? "(no summary yet)" : ch.Recap;
                    headSb.AppendLine($"{title} (T{ch.StartTurn}–{ch.EndTurn}): {recap}");
                }
                headSb.AppendLine();
            }

            string body = bodySb.ToString().TrimEnd();
            int maxChars = maxTokens >= int.MaxValue / 4 ? int.MaxValue : maxTokens * 4;

            // Only the chapter-recap head degrades under a tight budget; the body is always kept whole.
            int headBudget = Math.Max(0, maxChars - body.Length);
            string head = headSb.ToString();
            if (head.Length > headBudget)
                head = headBudget > 0 ? head.Substring(0, headBudget) + "...\n\n" : "";

            return (head + body).TrimEnd();
        }
    }
}
