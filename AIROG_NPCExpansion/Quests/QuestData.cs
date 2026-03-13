using System;

namespace AIROG_NPCExpansion
{
    public enum QuestStatus { Active, Completed, Failed }

    [Serializable]
    public class QuestData
    {
        public string Id;                    // GUID
        public string GiverId;              // NPC UUID
        public string GiverName;
        public string ObjectiveText;        // AI-generated quest objective
        public string CompletionCondition;  // AI-generated completion hint
        public string RewardText;           // Narrative reward description
        public int RewardGold;
        public int RewardAffinity = 15;
        public QuestStatus Status = QuestStatus.Active;
        public int TurnGiven;
        public int TurnDeadline = -1;       // -1 = no deadline
        public string CompletionNotes = "";

        // Quest Chain
        public string ChainId = "";         // Non-empty if part of a chain; first quest's Id used as ChainId
        public int ChainStep = 0;           // 0 = standalone / first in chain
        public bool IsChainFinal = false;   // True on the last quest in the chain
    }
}
