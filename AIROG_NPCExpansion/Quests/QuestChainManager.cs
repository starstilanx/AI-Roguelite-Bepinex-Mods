using System;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Handles multi-step quest chains. After a quest completes there is a 40% chance
    /// the NPC offers a follow-up quest that escalates the original narrative.
    /// Chains can span up to 3 steps (0, 1, 2). The final step gives a boosted reward
    /// and records an arc milestone for the relationship.
    /// </summary>
    public static class QuestChainManager
    {
        private const float CHAIN_CHANCE = 0.40f;
        private const int MAX_CHAIN_STEP = 2; // steps 0→1→2 = 3-quest chain

        private static readonly System.Random _rng = new System.Random();

        public static async Task TryOfferChainQuest(QuestData completedQuest, GameplayManager manager)
        {
            try
            {
                if (completedQuest.ChainStep >= MAX_CHAIN_STEP) return;
                if (_rng.NextDouble() > CHAIN_CHANCE) return;

                // Resolve the quest giver
                GameEntity giverEnt = null;
                SS.I?.uuidToGameEntityMap?.TryGetValue(completedQuest.GiverId, out giverEnt);
                if (!(giverEnt is GameCharacter giver)) return;
                if (giver.corpseState != GameCharacter.CorpseState.NONE) return;

                var data = NPCData.Load(giver.uuid);
                if (data == null) return;

                int nextStep = completedQuest.ChainStep + 1;
                bool isFinal = nextStep >= MAX_CHAIN_STEP;
                string chainId = string.IsNullOrEmpty(completedQuest.ChainId)
                    ? completedQuest.Id
                    : completedQuest.ChainId;
                int maxGold = isFinal ? 800 : 400;
                string finaleHint = isFinal
                    ? "This is the climactic final quest of this chain. Make it grand and consequential."
                    : "";

                string prompt = $"NPC '{giver.GetPrettyName()}' is offering a follow-up quest.\n" +
                                $"Previous quest completed: \"{completedQuest.ObjectiveText}\"\n" +
                                $"NPC personality: {data.Personality}\n" +
                                $"{finaleHint}\n\n" +
                                $"Generate a follow-up that escalates from the previous one. Respond ONLY:\n" +
                                $"OBJECTIVE: [one-sentence quest, under 30 words]\n" +
                                $"CONDITION: [what must happen, under 20 words]\n" +
                                $"REWARD: [narrative reward, under 15 words]\n" +
                                $"GOLD: [integer 0-{maxGold}]";

                string result = await GameCompat.GenerateTxt(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE);

                if (string.IsNullOrEmpty(result)) return;

                var quest = new QuestData
                {
                    Id = Guid.NewGuid().ToString(),
                    GiverId = giver.uuid,
                    GiverName = giver.GetPrettyName(),
                    TurnGiven = ScenarioUpdater.GlobalTurn,
                    ChainId = chainId,
                    ChainStep = nextStep,
                    IsChainFinal = isFinal,
                    RewardAffinity = 15 + (nextStep * 10),
                };

                foreach (var line in result.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("OBJECTIVE:"))      quest.ObjectiveText = t.Substring(10).Trim();
                    else if (t.StartsWith("CONDITION:")) quest.CompletionCondition = t.Substring(10).Trim();
                    else if (t.StartsWith("REWARD:"))    quest.RewardText = t.Substring(7).Trim();
                    else if (t.StartsWith("GOLD:") && int.TryParse(t.Substring(5).Trim(), out int g))
                        quest.RewardGold = Mathf.Clamp(g, 0, maxGold);
                }

                if (string.IsNullOrEmpty(quest.ObjectiveText)) return;

                QuestManager.AllQuests.Add(quest);
                QuestManager.SaveQuests();

                string label = isFinal ? "[CHAIN FINALE]" : $"[CHAIN Pt.{nextStep + 1}]";
                _ = manager.gameLogView.LogTextCompat(
                    $"<color=#ffd700>{label} {giver.GetPrettyName()}: {quest.ObjectiveText}</color>");

                if (isFinal)
                    _ = manager.gameLogView.LogTextCompat(
                        $"<color=#ffd700>Complete this final quest to earn {giver.GetPrettyName()}'s eternal gratitude!</color>");

                Debug.Log($"[QuestChain] Step {nextStep} generated for chain {chainId}: {quest.ObjectiveText}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuestChain] TryOfferChainQuest failed: {ex.Message}");
            }
        }
    }
}
