using System;
using System.Collections.Generic;

namespace AIROG_Settlement
{
    /// <summary>
    /// Defines a research node: a one-time purchase (Knowledge + a secondary resource) that
    /// unlocks a passive effect. Effects are read at point of use elsewhere by checking
    /// SettlementState.Researched.Contains(id) — this class only describes cost/eligibility.
    /// </summary>
    public class ResearchDefinition
    {
        public string ID;
        public string Name;
        public string Description;
        public Dictionary<string, int> Cost;

        /// <summary>Optional building-gate; null means always available once affordable.</summary>
        public Func<SettlementState, bool> Condition;

        public bool IsAvailable(SettlementState state) => Condition == null || Condition(state);

        public bool CanAfford(SettlementState state) => BuildingDefinition.CanAffordCost(state, Cost);
    }

    public static class ResearchCatalog
    {
        public static readonly ResearchDefinition[] All =
        {
            new ResearchDefinition
            {
                ID = "masonry", Name = "Masonry",
                Description = "Reinforced quarrying technique. Quarry produces +2 stone per turn.",
                Cost = new Dictionary<string, int> { { "Knowledge", 15 }, { "Gold", 20 } },
                Condition = s => s.HasBuilding("quarry")
            },
            new ResearchDefinition
            {
                ID = "irrigation", Name = "Irrigation",
                Description = "Channeled water for the fields. Farm produces +3 gold per turn.",
                Cost = new Dictionary<string, int> { { "Knowledge", 15 }, { "Gold", 20 } },
                Condition = s => s.HasBuilding("farm")
            },
            new ResearchDefinition
            {
                ID = "fortifications", Name = "Fortifications",
                Description = "Walls and watchposts. Raiders grow rarer, and the militia fights better.",
                Cost = new Dictionary<string, int> { { "Knowledge", 20 }, { "Stone", 30 } },
                Condition = s => s.HasBuilding("barracks")
            },
            new ResearchDefinition
            {
                ID = "guild_charter", Name = "Guild Charter",
                Description = "A chartered merchants' guild. All gold production +10%.",
                Cost = new Dictionary<string, int> { { "Knowledge", 25 }, { "Gold", 40 } },
                Condition = s => s.HasBuilding("market")
            },
            new ResearchDefinition
            {
                ID = "trade_agreements", Name = "Trade Agreements",
                Description = "Standing terms with traveling merchants. Import costs -10%, export income +10%.",
                Cost = new Dictionary<string, int> { { "Knowledge", 20 }, { "Gold", 15 } }
            },
            new ResearchDefinition
            {
                ID = "civic_planning", Name = "Civic Planning",
                Description = "Proper streets and lots. Population cap +2.",
                Cost = new Dictionary<string, int> { { "Knowledge", 25 }, { "Gold", 30 }, { "Wood", 20 } }
            },
        };

        public static ResearchDefinition Get(string id) => Array.Find(All, r => r.ID == id);
    }
}
