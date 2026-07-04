using System.Text;
using AIROG_GenContext;

namespace AIROG_Reverie
{
    public class ReverieProvider : IContextProvider
    {
        public string Name        => "Reverie";
        public int    Priority    => 96;  // above DmNotes(95)/Settlement(90)/Chronicle(88) — dream framing must dominate the prompt
        public string Description => "Dream layer: injects dream-sequence directives while the player sleeps, and prophetic omens / hauntings while awake.";

        public string GetContext(string prompt, int maxTokens)
        {
            var state = ReverieManager.State;
            if (state == null || maxTokens <= 0) return "";

            var sb = new StringBuilder();

            if (state.Phase == DreamPhase.Dreaming && state.CurrentDream != null)
                BuildDreamDirective(sb, state);
            else
            {
                if (state.PendingWake != WakeOutcome.None)
                    BuildWakeDirective(sb, state);
                BuildAwakeContext(sb, state);
            }

            string result = sb.ToString().TrimEnd();
            if (result.Length == 0) return "";

            // The dream directive is all-or-nothing: a truncated rule block is worse than none,
            // but in practice Priority 96 means we run near the top of the budget.
            int maxChars = maxTokens >= int.MaxValue / 4 ? int.MaxValue : maxTokens * 4;
            if (result.Length > maxChars && state.Phase != DreamPhase.Dreaming)
                result = result.Substring(0, maxChars);
            return result;
        }

        private static void BuildDreamDirective(StringBuilder sb, ReverieState state)
        {
            var dream = state.CurrentDream;
            int turnNumber = ReverieManager.DREAM_LENGTH - dream.DreamTurnsRemaining + 1;
            bool firstTurn = dream.Events.Count == 0 && dream.DreamTurnsRemaining == ReverieManager.DREAM_LENGTH;
            bool climax = dream.DreamTurnsRemaining <= 1;

            sb.AppendLine($"[DREAM SEQUENCE — \"{dream.Theme}\" — dream-turn {turnNumber} of {ReverieManager.DREAM_LENGTH} | Lucidity {dream.Lucidity}/{ReverieManager.MAX_LUCIDITY} | Confrontation {dream.Progress}/100]");
            sb.AppendLine("The player character is ASLEEP AND DREAMING. Everything happening right now takes place inside the dream, not the waking world.");
            sb.AppendLine($"Dreamscape: {dream.Premise}");
            sb.AppendLine($"The dream's heart: to master this dream, the dreamer must {dream.Core}.");
            if (firstTurn)
                sb.AppendLine("This is the moment of falling asleep: transition the narration from the player's rest into the dream — let the waking world dissolve into the dreamscape.");
            sb.AppendLine("Narrate with dream-logic: surreal transitions, symbols, familiar people and places misremembered or merged. The dream may echo the woven memories, distorted.");
            sb.AppendLine("DREAM RULES:");
            sb.AppendLine("- The sleeping body cannot be harmed: do NOT apply damage, item loss, or status effects to the player. Peril in the dream erodes LUCIDITY instead.");
            sb.AppendLine("- Everyone and everything here is a figment. Figments may know things the dreamer has forgotten.");
            sb.AppendLine("- Bold, clever, or self-aware action raises lucidity and progress; panic, denial, or being overwhelmed lowers lucidity.");
            if (climax)
                sb.AppendLine("- THIS IS THE FINAL DREAM-TURN. Bring the dream to its climax and force the confrontation with the dream's heart NOW.");
            sb.AppendLine("[DREAM INSTRUCTION: At the very end of your response, append a hidden block in exactly this format:]");
            sb.AppendLine("<DREAM_STATE>");
            sb.AppendLine("lucidity_delta: [-1 if the dreamer lost their grip this turn, 1 if they gained it, else 0]");
            sb.AppendLine("progress: [0-100, how close the dreamer now is to confronting the dream's heart]");
            sb.AppendLine("event: [one short line describing what just happened in the dream]");
            sb.AppendLine("</DREAM_STATE>");
            sb.AppendLine("[If — and only if — the dreamer fully confronts the dream's heart this turn (progress 100), or this is the final dream-turn and they have essentially prevailed, ALSO add a line before </DREAM_STATE>:");
            sb.AppendLine("omen: [a single specific prophecy about the WAKING world that this dream revealed — a concrete person, place, danger, or opportunity]");
            sb.AppendLine("This block will be stripped before the player sees the response.]");
            sb.AppendLine();
        }

        private static void BuildWakeDirective(StringBuilder sb, ReverieState state)
        {
            sb.AppendLine("[WAKING FROM THE DREAM]");
            sb.AppendLine(state.PendingWakeSummary ?? "The dream has ended.");
            sb.AppendLine("Narrate the player waking where they fell asleep. Only the night has passed; their body is unharmed. Let a residue of the dream color their first waking moments, then return fully to the waking world.");
            sb.AppendLine();
        }

        private static void BuildAwakeContext(StringBuilder sb, ReverieState state)
        {
            var omens = ReverieManager.LiveOmens();
            foreach (var o in omens)
            {
                sb.AppendLine($"[PROPHETIC OMEN — dreamed on turn {o.CreatedTurn}]");
                sb.AppendLine($"\"{o.Text}\"");
                sb.AppendLine("The player has genuinely foreseen this. Treat it as true prophecy: gradually and plausibly steer events toward making it come to pass. When it does, make the moment land — the player will recognize it from the dream.");
            }

            var haunting = ReverieManager.LiveHaunting();
            if (haunting != null)
            {
                sb.AppendLine("[HAUNTED]");
                sb.AppendLine(haunting.Text);
                sb.AppendLine("Let it manifest in subtle, unsettling details at the edges of scenes — glimpses, sounds, wrongness — growing bolder over time. It cannot directly harm the player yet. Do not explain what it is.");
            }

            if (omens.Count > 0 || haunting != null) sb.AppendLine();
        }
    }
}
