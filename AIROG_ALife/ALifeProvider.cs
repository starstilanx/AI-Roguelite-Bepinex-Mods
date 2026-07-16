using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIROG_ALife
{
    /// <summary>
    /// GenContext provider: surfaces offline A-Life activity around the player's current
    /// location so the narrative AI can weave in aftermath (corpses, tracks, smoke on the
    /// horizon), portray squads and their named leaders consistently, and play out the
    /// world's regard for the player (fear, awe, dread zones). Reads the live in-process
    /// state — no JSON staleness.
    /// </summary>
    public class ALifeProvider : AIROG_GenContext.IContextProvider
    {
        public int Priority => 78; // just below WorldContextProvider (80)
        public string Name => "A-Life";
        public string Description => "Injects offline squad activity (battles, feuds, migrations) and the world's regard for the player.";

        private const int EVENT_WINDOW_TURNS = 25;
        private const int MAX_EVENT_LINES = 4;

        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                string ctx = Build();
                // Respect the shared GenContext budget: ContextManager trusts providers
                // to honor maxTokens (~4 chars/token) rather than truncating for them.
                int charBudget = maxTokens < int.MaxValue / 8 ? maxTokens * 4 : int.MaxValue;
                if (ctx.Length > charBudget)
                    ctx = charBudget < 80 ? "" : ctx.Substring(0, charBudget);
                return ctx;
            }
            catch
            {
                return "";
            }
        }

        private string Build()
        {
            GameplayManager manager = SS.I?.hackyManager;
            Place top = manager?.currentPlace?.GetTopLvlPlace();
            if (top == null) return "";

            var sb = new StringBuilder();

            // Aftermath: things that happened HERE while the player was away
            var events = ALifeData.EventsAt(top.uuid, EVENT_WINDOW_TURNS, MAX_EVENT_LINES);
            if (events.Count > 0)
            {
                sb.AppendLine($"[LOCAL ACTIVITY — recent happenings at {top.GetPrettyName()}]");
                foreach (var e in events)
                {
                    int ago = Math.Max(0, ALifeData.State.CurrentTurn - e.Turn);
                    sb.AppendLine($"- ({ago} turn{(ago == 1 ? "" : "s")} ago) {e.Description}");
                }
            }

            // Squads physically present — with their leaders and their regard for the player
            var here = ALifeData.State.Squads.Where(s => s.CurrentPlaceUuid == top.uuid).ToList();
            foreach (var s in here.Take(2))
            {
                string leader = s.Leader != null ? $", led by {s.Leader.FullName}" : "";
                sb.AppendLine($"- In this area: {s.Name} ({s.Size} strong{leader}, {s.Activity}).{RegardNote(s)}");
            }

            // Squads one hop out — rumors/foreshadowing
            var neighborUuids = new HashSet<string>(ALifeGraph.Neighbors(top.uuid).Select(p => p.uuid));
            var nearby = ALifeData.State.Squads
                .Where(s => neighborUuids.Contains(s.CurrentPlaceUuid))
                .Take(2).ToList();
            foreach (var s in nearby)
                sb.AppendLine($"- Rumored nearby at {s.CurrentPlaceName}: {s.Name} ({s.Activity}).");

            // Blood feuds touching any squad the player can see or hear of
            var visible = here.Concat(nearby).ToList();
            var feudLines = new List<string>();
            foreach (var s in visible)
                foreach (var f in s.Feuds.Where(f => f.Heat >= 30))
                    feudLines.Add($"- Blood feud: {ALifeSimulation.Cap(s.Name)} against {f.EnemySquadName}, over {f.Reason}.");
            foreach (var line in feudLines.Distinct().Take(2))
                sb.AppendLine(line);

            // Dread: this ground remembers what the player did here
            int dread = ALifeLegend.DreadAt(top.uuid);
            if (dread >= ALifeLegend.DREAD_AVOID)
                sb.AppendLine($"- This place has become a killing ground — travelers and caravans now avoid it out of dread.");

            // The player's legend
            string tier = ALifeLegend.LegendTier();
            if (tier != null)
                sb.AppendLine($"[REPUTATION] Among the wandering bands, the player is {tier}.");

            if (sb.Length == 0) return "";

            sb.AppendLine("[A-LIFE GUIDANCE] Weave the above into the narration as ambient, physical world activity " +
                          "(remains, tracks, distant sounds, witnesses) — the world moves on its own. Treat named " +
                          "band leaders as real, persistent characters with the histories given. Play each band's " +
                          "regard for the player honestly: fearful bands hold back, parley, or flee — they do not " +
                          "fight to the death; respectful bands show deference. Do not have characters recite this " +
                          "list verbatim.");
            return sb.ToString();
        }

        private static string RegardNote(VirtualSquad s)
        {
            if (s.FearOfPlayer >= ALifeLegend.FEAR_WARY)
                return " They know of the player and are AFRAID — they will parley or withdraw rather than attack.";
            if (s.AweOfPlayer >= 40)
                return " They know of the player and hold them in high regard.";
            if (s.MetPlayer)
                return " They have met the player before.";
            return "";
        }
    }
}
