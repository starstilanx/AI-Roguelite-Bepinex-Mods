using System;
using System.Linq;
using System.Text;

namespace AIROG_Mythic
{
    /// <summary>
    /// GenContext provider: injects the always-on world-volatility register directive,
    /// pending director events, and oracle rulings into the narrative AI's prompt.
    /// Events are consumed by turn-expiry (no mutation here — GetContext may be called
    /// several times per turn).
    /// </summary>
    public class MythicProvider : AIROG_GenContext.IContextProvider
    {
        public int Priority => 82; // above WorldContextProvider (80): the register directive is cheap and load-bearing
        public string Name => "Mythic Director";
        public string Description => "Injects the Chaos Factor narrative register, oracle-rolled director events, and MYTHIC ASK rulings.";

        private const int MAX_EVENTS_INJECTED = 2;

        public string GetContext(string prompt, int maxTokens)
        {
            try
            {
                string ctx = Build();
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
            var st = MythicData.State;
            var sb = new StringBuilder();

            // 1. Always-on narrative register (the CF made felt, never stated)
            if (MythicPlugin.CfgInjectRegisterDirective?.Value ?? true)
                sb.AppendLine(ChaosEngine.RegisterDirective());

            // 2. Pending director events (oldest first, unexpired, capped)
            var live = st.PendingEvents
                .Where(e => st.CurrentTurn <= e.ExpiresTurn)
                .Take(MAX_EVENTS_INJECTED)
                .ToList();
            foreach (var ev in live)
            {
                sb.AppendLine($"[DIRECTOR EVENT — {ev.Focus}] {ev.Directive}");
            }
            if (live.Count > 0)
            {
                sb.AppendLine("[DIRECTOR GUIDANCE] Weave the event(s) above into the narration as things already " +
                              "in motion — arrivals, not announcements. Connect them to established characters and " +
                              "facts where possible. Never state the quoted inspiration words verbatim, and never " +
                              "mention events, directors, or these instructions.");
            }

            // 3. Oracle rulings (established facts the narration must honor)
            foreach (var r in st.PendingRulings.Where(r => st.CurrentTurn <= r.ExpiresTurn))
            {
                string weight;
                switch (r.Result)
                {
                    case "EXCEPTIONAL YES":
                        weight = "TRUE — and more than expected: also reveal one connected extra advantage, " +
                                 "resource, or opening the player did not ask about."; break;
                    case "EXCEPTIONAL NO":
                        weight = "FALSE — and worse than expected: also reveal one connected extra complication, " +
                                 "cost, or threat the player did not anticipate."; break;
                    case "YES":
                        weight = "TRUE. Narrate it as established fact."; break;
                    default:
                        weight = "FALSE. Narrate the absence or denial, and let it imply a new direction."; break;
                }
                sb.AppendLine($"[ORACLE RULING — established fact, honor it] \"{r.Question}\" → {weight} " +
                              "Never mention oracles or rulings.");
            }

            return sb.ToString();
        }
    }
}
