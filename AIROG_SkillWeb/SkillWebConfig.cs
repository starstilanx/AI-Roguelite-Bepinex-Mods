using System;

namespace AIROG_SkillWeb
{
    [Serializable]
    public class SkillWebConfig
    {
        /// <summary>Skill points granted per character level-up.</summary>
        public int PointsPerLevel = 1;

        /// <summary>Points spent to unlock a new node from the tree.</summary>
        public int NodeCost = 1;

        /// <summary>Base cost to upgrade a node to the next tier (multiplied by current tier).</summary>
        public int UpgradeCost = 2;

        /// <summary>Whether unlocked-node stat bonuses are applied to the player.</summary>
        public bool AllowStatBonuses = true;

        /// <summary>Minimum locked frontier nodes to auto-generate when a node is unlocked.</summary>
        public int FrontierNodesMin = 1;

        /// <summary>Maximum locked frontier nodes to auto-generate when a node is unlocked.</summary>
        public int FrontierNodesMax = 4;

        /// <summary>If false, frontier generation is disabled (manual-only expansion).</summary>
        public bool AutoGenerateFrontier = true;
    }
}
