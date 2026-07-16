using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    /// <summary>
    /// Season progression, per-faction territory/population seeding, and active-war peace
    /// checks — the per-turn bookkeeping WorldSimulation.OnTurnHappened delegates to.
    /// </summary>
    internal static class WorldTickCoordinator
    {
        private const int WAR_EXHAUSTION_RESOURCES = 15; // resources below this = peace
        private const int WAR_PROLONGED_TURNS = 50;  // turns before peace roll starts
        private const float WAR_PEACE_CHANCE  = 0.25f;
        private const int WAR_MIN_TURNS       = 5;   // wars can't end before this (prevents same-turn peace)

        // ─── Season ───────────────────────────────────────────────────────────────
        public static void AdvanceSeason(GameplayManager manager)
        {
            string[] seasons = { "Spring", "Summer", "Autumn", "Winter" };
            int idx = Array.IndexOf(seasons, WorldData.CurrentState.CurrentSeason);
            WorldData.CurrentState.CurrentSeason = seasons[(idx + 1) % 4];
            string season = WorldData.CurrentState.CurrentSeason;

            WorldData.LogEvent($"The season turns to {season}.", "SEASON");
            ApplySeasonBias(season);
            WorldEventsUI.MarkDirty();
            Debug.Log($"[WorldExpansion] Season changed to {season}");
        }

        public static void ApplySeasonBias(string season)
        {
            var market = WorldData.CurrentState.Market;
            switch (season)
            {
                case "Winter":
                    // Cold and scarce — nudge toward shortage if not already depressed
                    if (market.GlobalCondition == "Normal" || market.GlobalCondition == "Surplus")
                    {
                        market.PriceMultiplier = Mathf.Min(market.PriceMultiplier * 1.1f, 1.5f);
                        market.SellMultiplier  = Mathf.Min(market.SellMultiplier  * 1.05f, 1.3f);
                    }
                    break;
                case "Autumn":
                    // Harvest season — nudge toward surplus
                    if (market.GlobalCondition == "Shortage" || market.GlobalCondition == "Normal")
                    {
                        market.PriceMultiplier = Mathf.Max(market.PriceMultiplier * 0.95f, 0.65f);
                        market.SellMultiplier  = Mathf.Max(market.SellMultiplier  * 0.95f, 0.55f);
                    }
                    break;
                case "Spring":
                case "Summer":
                    // Normalize gently toward 1.0
                    market.PriceMultiplier = Mathf.Lerp(market.PriceMultiplier, 1.0f, 0.2f);
                    market.SellMultiplier  = Mathf.Lerp(market.SellMultiplier,  1.0f, 0.2f);
                    break;
            }
        }

        // ─── Territory & Population Seeding ───────────────────────────────────────
        // Claims REAL top-level Place UUIDs: first any places the game already marks as
        // owned by the faction (Place.faction), then unowned places as a top-up.
        public static void SeedNewFactions(GameplayManager manager)
        {
            var factions = manager.GetCurrentFactions();
            if (factions == null) return;

            List<Place> topPlaces = null;
            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                var data = WorldData.GetFactionData(faction.uuid);
                if (data.Seeded) continue;

                if (string.IsNullOrEmpty(data.Name))
                    data.Name = faction.GetPrettyName();

                if (topPlaces == null)
                {
                    try { topPlaces = manager.GetCurrentVoronoiWorld()?.GetAllTopLvlPlaces() ?? new List<Place>(); }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[WorldExpansion] Could not enumerate places for seeding: {e.Message}");
                        topPlaces = new List<Place>();
                    }
                }

                // 1. Adopt places the game already assigns to this faction
                foreach (var pl in topPlaces)
                {
                    if (pl?.faction != null && pl.faction.uuid == faction.uuid
                        && !data.ClaimedPlaceUuids.Contains(pl.uuid))
                        data.ClaimedPlaceUuids.Add(pl.uuid);
                }

                // 2. Landless factions claim 1-3 unowned places (mod-claim only; native
                //    ownership is set later on conquest, where it's a dramatic event)
                if (data.ClaimedPlaceUuids.Count == 0 && topPlaces.Count > 0)
                {
                    var claimedByAnyone = new HashSet<string>(
                        WorldData.CurrentState.Factions.Values.SelectMany(f => f.ClaimedPlaceUuids));
                    var unowned = topPlaces
                        .Where(p => p != null && p.faction == null && !claimedByAnyone.Contains(p.uuid))
                        .ToList();
                    // First pick is random; the rest cluster around it so the faction
                    // starts with a contiguous homeland instead of scattered blotches
                    int want = Math.Min(WorldSimUtils.Rng.Next(2, 5), unowned.Count);
                    var picked = new List<Place>();
                    for (int i = 0; i < want; i++)
                    {
                        Place pick;
                        if (picked.Count == 0)
                            pick = unowned[WorldSimUtils.Rng.Next(unowned.Count)];
                        else
                            pick = unowned.OrderBy(c => picked.Min(h => (c.worldCoords - h.worldCoords).sqrMagnitude)).First();
                        unowned.Remove(pick);
                        picked.Add(pick);
                        data.ClaimedPlaceUuids.Add(pick.uuid);
                    }
                }

                // Seed population
                if (data.Population == 500) // default value = not yet seeded
                {
                    data.Population = WorldSimUtils.Rng.Next(300, 1500);
                    data.PopState   = "Normal";
                }

                data.Seeded = true;
            }
        }

        // ─── Active War Peace Checks ──────────────────────────────────────────────
        public static void CheckActiveWarPeace()
        {
            var toEnd = new List<KeyValuePair<string, string>>(); // key → reason
            int turn = WorldData.CurrentState.CurrentTurn;

            foreach (var kvp in WorldData.CurrentState.ActiveWars)
            {
                var war = kvp.Value;
                int duration = turn - war.StartTurn;

                // Wars need time to breathe — no peace on the turn they're declared
                if (duration < WAR_MIN_TURNS) continue;

                // Exhaustion peace: one (or both) sides are bankrupt
                bool actorExhausted  = WorldData.CurrentState.Factions.TryGetValue(war.ActorUuid,  out var a) && a.Resources < WAR_EXHAUSTION_RESOURCES;
                bool targetExhausted = WorldData.CurrentState.Factions.TryGetValue(war.TargetUuid, out var t) && t.Resources < WAR_EXHAUSTION_RESOURCES;

                if (actorExhausted || targetExhausted)
                {
                    string reason = actorExhausted && targetExhausted
                        ? "both sides are exhausted"
                        : $"{(actorExhausted ? war.ActorName : war.TargetName)} can no longer sustain the fight";
                    toEnd.Add(new KeyValuePair<string, string>(kvp.Key, reason));
                    continue;
                }

                // Prolonged war: random peace roll after threshold
                if (duration >= WAR_PROLONGED_TURNS && WorldSimUtils.Rng.NextDouble() < WAR_PEACE_CHANCE)
                    toEnd.Add(new KeyValuePair<string, string>(kvp.Key, "the prolonged conflict has ground to a stalemate"));
            }

            foreach (var pair in toEnd)
            {
                WorldData.EndWar(pair.Key, pair.Value);
                WorldEventsUI.MarkDirty();
            }
        }
    }
}
