using System;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Configuration for the Skill Web "mechanical layer" that rides on top of the game's
    /// native perk tree. The native tree owns structure / learning / activation / narrative;
    /// this mod only attaches attribute bonuses to learned perks.
    /// </summary>
    [Serializable]
    public class SkillWebConfig
    {
        /// <summary>Whether learned-perk attribute bonuses are applied to the player at all.</summary>
        public bool AllowStatBonuses = true;

        /// <summary>
        /// If true, perk attribute bonuses are derived by an AI call that reads the perk's
        /// name + description. If false (or the call fails), a deterministic keyword heuristic is used.
        /// AI derivation runs once per perk, asynchronously, and refines the heuristic result.
        /// </summary>
        public bool UseAIStatDerivation = true;

        /// <summary>
        /// Total attribute budget the heuristic distributes across a learned perk's matched
        /// attributes. Higher = stronger passives.
        /// </summary>
        public float HeuristicBudget = 6f;

        /// <summary>
        /// Bonus multiplier applied when a learned perk is also one of the player's active perks.
        /// Active perks already drive the narrative; this rewards activating them mechanically too.
        /// </summary>
        public float ActiveBonusMultiplier = 1.5f;

        /// <summary>Safety cap on the total bonus contributed to any single attribute.</summary>
        public float MaxBonusPerAttribute = 30f;
    }
}
