using System;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Generates periodic AI-generated ambient dialogue ("barks") for nearby NPCs
    /// who have lore data and are not actively being conversed with.
    /// </summary>
    public static class NPCBarkSystem
    {
        private const int BARK_COOLDOWN = 8;     // Min global turns between barks for one NPC
        private const float BARK_CHANCE = 0.40f; // 40% chance per eligible NPC per bark tick
        private static readonly System.Random _rng = new System.Random();

        public static async Task TryBark(GameCharacter npc, NPCData data, int globalTurn, GameplayManager manager)
        {
            try
            {
                if (globalTurn - data.LastBarkTurn < BARK_COOLDOWN) return;
                if (!NPCData.HasProfile(npc, data)) return;
                if (_rng.NextDouble() > BARK_CHANCE) return;

                // Don't bark if this NPC is currently selected for conversation
                if (manager.npcActionsHandler?.currentNpc?.uuid == npc.uuid) return;

                string context = BuildContext(npc, data);
                string prompt = $"{context}\n\n" +
                                $"Write one brief ambient remark this character mutters or says aloud (under 25 words). " +
                                $"Match their personality and current state exactly. " +
                                $"Output ONLY the spoken words. No quotes. No action beats.";

                string bark = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(bark)) return;

                // Normalise before the length check: strip quotes/whitespace and collapse
                // any multi-line output into the first line.
                bark = bark.Trim().Trim('"').Split('\n')[0].Trim();
                if (bark.Length == 0 || bark.Length > 200) return;
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#e0c080>[{npc.GetPrettyName()}]</color> \"{bark}\"");

                data.LastBarkTurn = globalTurn;
                NPCData.Save(npc.uuid, data);
                Debug.Log($"[BarkSystem] {npc.GetPrettyName()}: \"{bark}\"");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BarkSystem] Failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }

        private static string BuildContext(GameCharacter npc, NPCData data)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"NPC: {npc.GetPrettyName()}");
            sb.AppendLine($"Personality: {NPCData.GetPersonality(npc, data)}");
            string background = NPCData.GetBackground(npc, data);
            if (!string.IsNullOrEmpty(background))
                sb.AppendLine($"Current Situation: {background}");
            if (!string.IsNullOrEmpty(data.CurrentGoal))
                sb.AppendLine($"Goal: {data.CurrentGoal}");
            sb.AppendLine($"Relationship with player: {data.RelationshipStatus} (Affinity {data.Affinity}/100)");
            if (data.IsNemesis)
                sb.AppendLine("Note: This NPC killed the player previously and is arrogant about it.");
            if (data.ReputationTags != null && data.ReputationTags.Count > 0)
                sb.AppendLine($"Known for: {string.Join(", ", data.ReputationTags)}");
            return sb.ToString().TrimEnd();
        }
    }
}
