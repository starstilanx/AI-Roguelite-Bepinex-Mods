using System;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Generates short reputation tags for NPCs based on their observed actions.
    /// Tags are AI-generated (1-3 words) and accumulate up to MAX_TAGS.
    /// </summary>
    public static class NPCReputationSystem
    {
        private const int MAX_TAGS = 5;

        /// <summary>
        /// Attempts to add a new reputation tag based on an action the NPC just performed.
        /// Fire-and-forget safe.
        /// </summary>
        public static async Task AddReputationFromAction(GameCharacter npc, NPCData data, string actionDesc)
        {
            try
            {
                if (data.ReputationTags == null) data.ReputationTags = new System.Collections.Generic.List<string>();
                if (data.ReputationTags.Count >= MAX_TAGS) return;
                if (string.IsNullOrEmpty(data.Personality)) return;

                string existing = data.ReputationTags.Count > 0
                    ? $"Existing reputation: {string.Join(", ", data.ReputationTags)}. "
                    : "";

                string prompt = $"NPC '{npc.GetPrettyName()}': {data.Personality}\n{existing}" +
                                $"They just: {actionDesc}\n\n" +
                                $"Give ONE short reputation tag (2-4 words, lowercase) this action earns them. " +
                                $"Output ONLY the tag. Examples: 'battle-hardened', 'generous merchant', 'quick to flee'.";

                string tag = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(tag)) return;

                tag = tag.Trim().ToLower().Trim('"').Split('\n')[0].Trim();
                if (tag.Length < 3 || tag.Length > 30) return;
                if (data.ReputationTags.Contains(tag)) return;

                data.ReputationTags.Add(tag);
                NPCData.Save(npc.uuid, data);
                Debug.Log($"[NPCReputation] {npc.GetPrettyName()} earned tag: '{tag}'");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCReputation] Failed for {npc?.GetPrettyName()}: {ex.Message}");
            }
        }
    }
}
