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

            // Aftermath: things that HAPPENED here while the player was away. ENCOUNTER
            // notices are excluded — they describe who is standing here right now, which
            // the "in this area" lines below already report. Filed as aftermath they read
            // to the AI as a fresh threat every single turn, which is what turned quiet
            // scenes into standoffs.
            var events = ALifeData.EventsAt(top.uuid, EVENT_WINDOW_TURNS, MAX_EVENT_LINES, excludeType: "ENCOUNTER");
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
            foreach (var s in here.Take(ALifeKnowledge.VISIBLE_HERE_CAP))
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

            // War front: if this ground's owner is at war, report how the field is going
            string hereFacUuid = top.faction?.uuid;
            if (hereFacUuid != null)
            {
                var war = ALifeWorldBridge.GetActiveWars()
                    .FirstOrDefault(w => w.ActorUuid == hereFacUuid || w.TargetUuid == hereFacUuid);
                if (war != null)
                {
                    string front = ALifeWar.FrontLine(war);
                    sb.AppendLine($"- This is wartime territory ({war.ActorName} vs {war.TargetName})" +
                                  (front != null ? $": {front}." : ": the front is unbroken."));
                }
            }

            // Dread: this ground remembers what the player did here
            int dread = ALifeLegend.DreadAt(top.uuid);
            if (dread >= ALifeLegend.DREAD_AVOID)
                sb.AppendLine($"- This place has become a killing ground — travelers and caravans now avoid it out of dread.");

            // The player's legend — only where there is somebody who would have heard it.
            // Injected unconditionally (as it was before v2.3) it reached every scene, and
            // the AI duly had shopkeepers and tavern patrons cowering at a stranger buying
            // bread. It is a reputation among fighting men, not an aura.
            string tier = ALifeLegend.LegendTier();
            bool audience = here.Count > 0 || nearby.Count > 0 || dread >= ALifeLegend.DREAD_AVOID;
            if (tier != null && audience)
                sb.AppendLine($"[REPUTATION] Among fighting bands and people who travel the roads, the player is {tier}. " +
                              "This does NOT extend to ordinary townsfolk, traders, innkeepers or staff going about " +
                              "their day: they have not heard the stories and treat the player as an unremarkable stranger.");

            if (sb.Length > 0)
                sb.AppendLine("[A-LIFE GUIDANCE] Weave the above into the narration as ambient, physical world activity " +
                              "(remains, tracks, distant sounds, witnesses) — the world moves on its own. Treat named " +
                              "band leaders as real, persistent characters with the histories given. Play each band's " +
                              "regard for the player honestly: fearful bands hold back, parley, or flee — they do not " +
                              "fight to the death; respectful bands show deference. That regard belongs to the bands " +
                              "named above and to nobody else. Do not have characters recite this list verbatim.");

            sb.Append(AmbientLifeDirective());
            return sb.ToString();
        }

        /// <summary>
        /// Always-on counterweight to a player-centric prompt. Everything else a mod injects
        /// describes how the world regards the PLAYER, so the AI has nothing to write ordinary
        /// life from and aims every reaction at them. This gives it something else to do.
        /// </summary>
        private static string AmbientLifeDirective()
        {
            if (!(ALifePlugin.CfgAmbientLife?.Value ?? true)) return "";
            return "[AMBIENT LIFE] The people in a scene have their own business and their own standing with each " +
                   "other. Show some of it: talk that does not involve the player, old grudges, glances between " +
                   "rivals, work being done, haggling, someone walking out mid-argument. Most characters have no " +
                   "reason to react to the player at all — only those with an actual reason (a weapon drawn, a name " +
                   "they know, a stake in what the player is doing) should take notice. A stranger eating a meal is " +
                   "not the center of the room.\n";
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
