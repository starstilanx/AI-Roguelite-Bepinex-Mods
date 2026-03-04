using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    public static class WorldPromptInjection
    {
        [HarmonyPatch(typeof(AIAsker), "GenerateTxtNoTryStrStyle")]
        [HarmonyPrefix]
        public static void Prefix_GenerateTxtNoTryStrStyle(ref string prompt, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            // DISABLED: Logic moved to AIROG_GenContext to optimize token usage.
            /*
            // Only inject for story completion or general questions to avoid bloating specialized prompts
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER && 
                chatGptPromptType != AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER)
            {
                return;
            }

            if (WorldData.CurrentState == null || WorldData.CurrentState.Events == null || WorldData.CurrentState.Events.Count == 0)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("\n\n[Current World State & Recent History]");
            
            // 1. Economic Condition
            var market = WorldData.CurrentState.Market;
            sb.AppendLine($"- Global Economy: {market.GlobalCondition} (Price Multiplier: {market.PriceMultiplier:P0}, Sell Multiplier: {market.SellMultiplier:P0})");

            // 2. Recent Major Events
            if (WorldData.CurrentState.MajorEventHistory != null && WorldData.CurrentState.MajorEventHistory.Count > 0)
            {
                sb.AppendLine("- Recent Major Events:");
                var majorEvents = WorldData.CurrentState.MajorEventHistory.Skip(System.Math.Max(0, WorldData.CurrentState.MajorEventHistory.Count - 3));
                foreach (var e in majorEvents)
                {
                    sb.AppendLine($"  * {e}");
                }
            }

            // 3. Recent Minor Events (Last 5)
            sb.AppendLine("- Recent Occurrences:");
            var lastEvents = WorldData.CurrentState.Events
                .Where(e => e.Type != "MAJOR" && e.Type != "ECONOMY")
                .OrderByDescending(e => e.Turn)
                .Take(5)
                .Reverse();

            foreach (var evt in lastEvents)
            {
                sb.AppendLine($"  * {evt.Description}");
            }

            sb.AppendLine("[End of World Context]\n");

            // Prepend or Append? 
            // Prepending is usually better for "Instruction" style, but Appending might be safer for "Memory" style.
            // Let's Append as it's additional context.
            prompt += sb.ToString();

            // Debug.Log("[WorldExpansion] Injected world context into AI prompt.");
            */
        }
    }
}
