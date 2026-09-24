using System;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Configuration for the Skill Web v4.0 "Constellation" Overhaul.
    /// Controls the procedural generation behavior, attribute constraints, and point economies.
    /// </summary>
    [Serializable]
    public class SkillWebConfig
    {
        /// <summary>Whether attribute bonuses from the web are applied to the player.</summary>
        public bool AllowStatBonuses = true;

        /// <summary>If true, AI is used to refine/generate names, attributes, and lore; otherwise, only offline heuristics are used.</summary>
        public bool UseAIGeneration = true;

        /// <summary>Compatibility bridge for legacy config files mapping to UseAIGeneration.</summary>
        public bool UseAIStatDerivation
        {
            get => UseAIGeneration;
            set => UseAIGeneration = value;
        }

        /// <summary>Scales all Resonance points earned.</summary>
        public float ResonanceMultiplier = 1.0f;

        /// <summary>Safety cap on the total bonus contributed to any single attribute.</summary>
        public float MaxBonusPerAttribute = 50.0f;

        /// <summary>Bonus multiplier applied to adjacent nodes when an Anchor Star's native perk is active.</summary>
        public float ActiveBonusMultiplier = 1.5f;

        /// <summary>Maximum number of simultaneously active Keystones.</summary>
        public int MaxActiveKeystones = 3;

        /// <summary>
        /// Sectors in a new character's web (3-8). Raised automatically when the character has
        /// more native perk trees than this. Fixed once the web exists.
        /// </summary>
        public int SectorsAtStart = 6;

        /// <summary>Rings charted (revealed) in every sector when a web is created; the rest reveal as you reach them.</summary>
        public int SeedRings = 3;

        /// <summary>
        /// Size of newly generated webs: "compact" (5 rings, ~400 nodes), "standard" (7 rings,
        /// ~800 nodes) or "vast" (9 rings, ~1,200 nodes at 8 sectors). Existing webs keep their size.
        /// </summary>
        public string WebSize = "standard";

        /// <summary>Enable spending Resonance to level up Basic and Notable nodes (up to Tier 3).</summary>
        public bool MasteryTiersEnabled = true;

        /// <summary>Enable soft integration hooks with Reverie, Chronicle, Insight, NPCExpansion, and Settlement.</summary>
        public bool CrossModHooks = true;

        /// <summary>Show uncharted outer rings as faint dots on the map.</summary>
        public bool FrontierPreview = true;

        /// <summary>Attribute budget distributed across a learned native perk's Anchor Star by the offline heuristic.</summary>
        public float HeuristicBudget = 6.0f;

        /// <summary>Show hand-made medallion icons (from Assets/SkillSpriteSheet.png) on web nodes that have no bespoke AI icon yet.</summary>
        public bool UseSpriteSheetIcons = true;

        /// <summary>Mint usable active abilities from unlocked Keystone (and optionally Confluence) nodes, castable from the ability bar.</summary>
        public bool GrantUsableAbilities = true;

        /// <summary>Also grant usable abilities from unlocked Confluence nodes, in addition to Keystones.</summary>
        public bool AbilitiesFromConfluences = true;
    }
}
