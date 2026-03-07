using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Periodically synthesizes GameCharacter.storyTurnHistoryV2 into LongTermMemories
    /// via a short AI call. Runs every SYNTHESIS_INTERVAL global turns per NPC.
    /// </summary>
    public static class NPCMemorySynthesis
    {
        private const int SYNTHESIS_INTERVAL = 10;
        private const int MAX_MEMORIES = 10;

        public static async Task SynthesizeForNpc(GameCharacter npc, NPCData data, int globalTurn)
        {
            try
            {
                if (globalTurn - data.MemorySynthesisTurn < SYNTHESIS_INTERVAL) return;
                if (npc.storyTurnHistoryV2 == null || npc.storyTurnHistoryV2.Count == 0) return;
                if (string.IsNullOrEmpty(data.Personality)) return; // Only for lore'd NPCs

                // Build compact story snippet from recent turns
                var history = npc.storyTurnHistoryV2;
                var recentTurns = history
                    .Skip(Math.Max(0, history.Count - 4))
                    .Where(t => !string.IsNullOrEmpty(t.resultingStoryStr))
                    .ToList();

                if (recentTurns.Count == 0) return;

                string storyText = string.Join("\n", recentTurns
                    .Select(t => "- " + t.resultingStoryStr.Substring(0, Math.Min(100, t.resultingStoryStr.Length))));

                string prompt = $"NPC '{npc.GetPrettyName()}' witnessed these events:\n{storyText}\n\n" +
                                $"Write 1-2 memory entries this NPC would retain (each under 20 words). " +
                                $"One memory per line. No preamble, no bullets.";

                string result = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(result)) return;

                var newMemories = result.Split('\n')
                    .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
                    .Where(l => l.Length > 5 && l.Length < 120)
                    .Take(2)
                    .ToList();

                if (data.LongTermMemories == null) data.LongTermMemories = new List<string>();

                foreach (var mem in newMemories)
                    data.LongTermMemories.Add(mem);

                while (data.LongTermMemories.Count > MAX_MEMORIES)
                    data.LongTermMemories.RemoveAt(0);

                data.MemorySynthesisTurn = globalTurn;
                NPCData.Save(npc.uuid, data);
                Debug.Log($"[MemorySynthesis] {npc.GetPrettyName()} gained {newMemories.Count} new memories.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MemorySynthesis] Failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }
    }
}
