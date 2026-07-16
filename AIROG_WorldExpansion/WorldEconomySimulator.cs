using UnityEngine;

namespace AIROG_WorldExpansion
{
    /// <summary>Periodic economy tick and event-driven market feedback.</summary>
    internal static class WorldEconomySimulator
    {
        public static void RunEconomyTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running Economy Tick at Turn {WorldData.CurrentState.CurrentTurn}");

            if (WorldSimUtils.Rng.NextDouble() >= 0.20) return; // 20% chance to change state

            var market = WorldData.CurrentState.Market;
            market.PreviousCondition = market.GlobalCondition;

            string desc  = "";
            int state = WorldSimUtils.Rng.Next(5);
            switch (state)
            {
                case 0:
                    market.GlobalCondition = "Normal";
                    market.PriceMultiplier = 1.0f;
                    market.SellMultiplier  = 1.0f;
                    desc = "The global markets have stabilized.";
                    break;
                case 1:
                    market.GlobalCondition = "Shortage";
                    market.PriceMultiplier = 1.4f;
                    market.SellMultiplier  = 1.2f;
                    desc = "Resources are scarce! Prices for goods have skyrocketed.";
                    break;
                case 2:
                    market.GlobalCondition = "Surplus";
                    market.PriceMultiplier = 0.7f;
                    market.SellMultiplier  = 0.6f;
                    desc = "The markets are flooded with goods. Prices have dropped.";
                    break;
                case 3:
                    market.GlobalCondition = "Inflation";
                    market.PriceMultiplier = 1.25f;
                    market.SellMultiplier  = 1.2f;
                    desc = "Inflation is rising. Currency is flowing freely but worth less.";
                    break;
                case 4:
                    market.GlobalCondition = "Depression";
                    market.PriceMultiplier = 0.6f;
                    market.SellMultiplier  = 0.4f;
                    desc = "Economic depression has hit. Trade has ground to a halt.";
                    break;
            }

            // Season can push back against the roll
            WorldTickCoordinator.ApplySeasonBias(WorldData.CurrentState.CurrentSeason);

            WorldData.LogEvent(desc, "ECONOMY");
            WorldEventsUI.MarkDirty();
        }

        // ─── Economy Feedback ─────────────────────────────────────────────────────
        public static void ApplyEconomyFeedback(string eventText, GameplayManager manager)
        {
            if (string.IsNullOrEmpty(eventText)) return;
            string lower = eventText.ToLower();

            string newCondition   = null;
            float  newBuy         = WorldData.CurrentState.Market.PriceMultiplier;
            float  newSell        = WorldData.CurrentState.Market.SellMultiplier;
            string feedbackEvent  = null;

            if (WorldSimUtils.ContainsAnyWord(lower, "plague", "pestilence", "disease", "sickness", "dying", "death", "blight", "famine", "drought"))
            {
                newCondition  = "Depression";
                newBuy        = 0.6f;
                newSell       = 0.4f;
                feedbackEvent = "Disease and death have devastated trade routes. Markets have collapsed.";
            }
            else if (WorldSimUtils.ContainsAnyWord(lower, "war", "wars", "battle", "invasion", "siege", "crusade", "raid", "conflict", "blockade"))
            {
                newCondition  = "Shortage";
                newBuy        = 1.4f;
                newSell       = 1.2f;
                feedbackEvent = "Wartime demands and disrupted supply lines have driven prices up sharply.";
            }
            else if (WorldSimUtils.ContainsAnyWord(lower, "discovery", "prosperity", "golden age", "abundance", "harvest", "trade route", "opens"))
            {
                newCondition  = "Surplus";
                newBuy        = 0.7f;
                newSell       = 0.6f;
                feedbackEvent = "New prosperity has brought goods flooding into the markets across the realm.";
            }
            else if (WorldSimUtils.ContainsAnyWord(lower, "dark lord", "evil rises", "shadow", "dread", "omen", "prophecy", "prophesied", "prophesy"))
            {
                newCondition  = "Shortage";
                newBuy        = 1.3f;
                newSell       = 1.1f;
                feedbackEvent = "Fear of the rising darkness has caused widespread hoarding and market disruption.";
            }
            else if (WorldSimUtils.ContainsAnyWord(lower, "inflation", "coin", "treasury", "tax", "taxes", "wealth flows"))
            {
                newCondition  = "Inflation";
                newBuy        = 1.25f;
                newSell       = 1.2f;
                feedbackEvent = "A surge of coin in circulation has driven inflation across the realm.";
            }

            if (newCondition != null)
            {
                var market              = WorldData.CurrentState.Market;
                market.PreviousCondition = market.GlobalCondition;
                market.GlobalCondition  = newCondition;
                market.PriceMultiplier  = newBuy;
                market.SellMultiplier   = newSell;
                WorldData.LogEvent(feedbackEvent, "ECONOMY");
            }
        }
    }
}
