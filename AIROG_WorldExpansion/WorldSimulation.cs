using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    public class WorldSimulation
    {
        private const int SEASON_LENGTH       = 20;  // turns per season
        private const int GRIEVANCE_WAR_THRESHOLD = 3;  // raids before war declared
        private const int WAR_EXHAUSTION_RESOURCES = 15; // resources below this = peace
        private const int WAR_PROLONGED_TURNS = 50;  // turns before peace roll starts
        private const float WAR_PEACE_CHANCE  = 0.25f;

        private static readonly System.Random rng = new System.Random();

        private static readonly string[] FallbackMajorEvents =
        {
            "The Great Plague has swept across the lands, decimating populations and halting trade.",
            "An Age of Discovery has begun! Explorers are finding new lands and ancient artifacts.",
            "A Global War has broken out as old alliances crumble and new powers rise.",
            "The Stars Have Aligned, bringing a surge of magical energy to the world.",
            "A Great Depression has hit the global economy, making gold scarce and desperation high.",
            "The Rise of a Dark Lord has been prophesied, causing fear to spread through every kingdom.",
            "A Holy Crusade has been declared, uniting many factions under a single banner.",
            "A devastating famine grips the realm — crops wither and rivers run dry.",
            "An ancient evil stirs beneath the mountains, and tremors shake the land.",
            "A legendary hero has risen, rallying the common folk against oppressive powers.",
            "Rival mages have shattered the Accord of Spells, unleashing wild magic across the land.",
            "A celestial event heralds change — scholars argue whether it is an omen of doom or rebirth.",
            "The sea routes have been blockaded by a powerful pirate armada, crippling overseas trade.",
            "A great fire has razed a major trade city, sending shockwaves through the economy.",
            "A new religion spreads like wildfire, destabilizing old power structures overnight.",
        };

        // ─── Main Tick ────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameplayManager), "InvokeTurnHappened")]
        [HarmonyPostfix]
        public static void OnTurnHappened(GameplayManager __instance, int numTurns, long secs)
        {
            if (__instance == null) return;

            WorldData.CurrentState.CurrentTurn += numTurns;
            int turn = WorldData.CurrentState.CurrentTurn;

            // Lazy territory initialization
            if (!WorldData.CurrentState.TerritoriesInitialized)
                InitializeFactionTerritories(__instance);

            // Season advancement
            WorldData.CurrentState.SeasonTurnCounter += numTurns;
            while (WorldData.CurrentState.SeasonTurnCounter >= SEASON_LENGTH)
            {
                WorldData.CurrentState.SeasonTurnCounter -= SEASON_LENGTH;
                AdvanceSeason(__instance);
            }

            // Peace checks for active wars
            CheckActiveWarPeace();

            // Minor Tick (faction actions) — every 5 turns
            if (turn % 5 == 0)
                RunMinorTick(__instance);

            // Economy Tick — every 25 turns
            if (turn % 25 == 0)
                RunEconomyTick(__instance);

            // Major Event — when scheduled
            if (turn >= WorldData.CurrentState.NextMajorEventTurn)
                RunMajorTick(__instance);
        }

        // ─── Season ───────────────────────────────────────────────────────────────
        private static void AdvanceSeason(GameplayManager manager)
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

        private static void ApplySeasonBias(string season)
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

        // ─── Territory Initialization ─────────────────────────────────────────────
        private static void InitializeFactionTerritories(GameplayManager manager)
        {
            var factions = manager.GetCurrentFactions();
            if (factions == null) return;
            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                var data = WorldData.GetFactionData(faction.uuid);
                if (data.ClaimedPlaceUuids.Count == 0)
                {
                    int count = rng.Next(1, 4);
                    for (int i = 0; i < count; i++)
                        data.ClaimedPlaceUuids.Add($"territory_{faction.uuid}_{i}");
                }
            }
            WorldData.CurrentState.TerritoriesInitialized = true;
        }

        // ─── Active War Peace Checks ──────────────────────────────────────────────
        private static void CheckActiveWarPeace()
        {
            var toEnd = new List<KeyValuePair<string, string>>(); // key → reason
            int turn = WorldData.CurrentState.CurrentTurn;

            foreach (var kvp in WorldData.CurrentState.ActiveWars)
            {
                var war = kvp.Value;
                int duration = turn - war.StartTurn;

                // Exhaustion peace: one side is bankrupt
                bool actorExhausted  = WorldData.CurrentState.Factions.TryGetValue(war.ActorUuid,  out var a) && a.Resources < WAR_EXHAUSTION_RESOURCES;
                bool targetExhausted = WorldData.CurrentState.Factions.TryGetValue(war.TargetUuid, out var t) && t.Resources < WAR_EXHAUSTION_RESOURCES;

                if (actorExhausted || targetExhausted)
                {
                    toEnd.Add(new KeyValuePair<string, string>(kvp.Key, "both sides are exhausted"));
                    continue;
                }

                // Prolonged war: random peace roll after threshold
                if (duration >= WAR_PROLONGED_TURNS && rng.NextDouble() < WAR_PEACE_CHANCE)
                    toEnd.Add(new KeyValuePair<string, string>(kvp.Key, "the prolonged conflict has ground to a stalemate"));
            }

            foreach (var pair in toEnd)
            {
                WorldData.EndWar(pair.Key, pair.Value);
                WorldEventsUI.MarkDirty();
            }
        }

        // ─── Major Tick (world events) ────────────────────────────────────────────
        public static void RunMajorTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running MAJOR Tick at Turn {WorldData.CurrentState.CurrentTurn}");

            // Reschedule first to prevent double-fire if AI takes a while
            WorldData.CurrentState.NextMajorEventTurn = WorldData.CurrentState.CurrentTurn + rng.Next(10, 101);

            GenerateMajorEventAsync(manager);
        }

        private static async void GenerateMajorEventAsync(GameplayManager manager)
        {
            try
            {
                string desc = await TryGenerateAIMajorEvent(manager);

                WorldData.LogEvent(desc, "MAJOR");
                WorldData.CurrentState.MajorEventHistory.Add(desc);

                // Apply economy feedback based on event content
                ApplyEconomyFeedback(desc, manager);

                // Record in lorebook
                WorldLoreExpansion.RecordHistoricalEvent(manager, desc, "History",
                    new List<string> { "Major", "Global", WorldData.CurrentState.CurrentSeason });

                WorldEventsUI.MarkDirty();
                Debug.Log($"[WorldExpansion] Major Event logged: {desc}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldExpansion] Major event generation error: {ex}");
            }
        }

        private static async Task<string> TryGenerateAIMajorEvent(GameplayManager manager)
        {
            if (manager == null) return PickFallbackEvent();

            try
            {
                var state = WorldData.CurrentState;
                string worldName  = manager.GetCurrentVoronoiWorld()?.GetPrettyName() ?? "the realm";
                string univName   = manager.GetCurrentUniverse()?.GetPrettyName() ?? "the world";
                string season     = state.CurrentSeason;
                string economy    = state.Market.GlobalCondition;

                string warsSummary = state.ActiveWars.Count > 0
                    ? string.Join(", ", state.ActiveWars.Values.Select(w => $"{w.ActorName} vs {w.TargetName}"))
                    : "none";

                var topFactions = state.Factions.Values
                    .Where(f => !string.IsNullOrEmpty(f.Name) && !state.EliminatedFactions.Contains(
                        state.Factions.FirstOrDefault(kv => kv.Value == f).Key))
                    .OrderByDescending(f => f.Resources)
                    .Take(3)
                    .Select(f => $"{f.Name} [{f.Tag}]");
                string factionsSummary = topFactions.Any() ? string.Join(", ", topFactions) : "unknown factions";

                string prompt =
                    $"You are the narrator of a persistent fantasy world called \"{univName}\" ({worldName}). " +
                    $"Generate a single dramatic major world event (2-3 sentences) that is happening right now. " +
                    $"Season: {season}. Economy: {economy}. Active wars: {warsSummary}. " +
                    $"Major factions: {factionsSummary}. " +
                    $"Make it feel organic and specific to this world's context. " +
                    $"Return only the event description text, nothing else.";

                string raw = await AIAsker.GenerateTxtNoTryStrStyle(
                    AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER,
                    prompt,
                    AIAsker.ChatGptPostprocessingType.NONE,
                    forceOfficialChatgpt: false,
                    forceNsfwFriendlyIfAvail: false,
                    null,
                    background: true,
                    forceEventCheckModel: true);

                if (!string.IsNullOrWhiteSpace(raw))
                    return TrimToTwoSentences(raw.Trim());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldExpansion] AI major event failed, using fallback: {ex.Message}");
            }

            return PickFallbackEvent();
        }

        private static string PickFallbackEvent()
        {
            // Season-flavoured fallback selection
            string season = WorldData.CurrentState.CurrentSeason;
            var pool = FallbackMajorEvents.ToList();
            if (season == "Winter")
                pool.Add("A brutal winter has frozen the trade roads, leaving towns isolated and starving.");
            else if (season == "Spring")
                pool.Add("With the spring thaw, ancient ruins have re-emerged from the melting snows, drawing adventurers from afar.");
            else if (season == "Summer")
                pool.Add("A scorching summer drought has withered harvests across the land, igniting unrest.");
            else if (season == "Autumn")
                pool.Add("The harvest festivals have been interrupted by a mysterious blight spreading through the crop fields.");

            return pool[rng.Next(pool.Count)];
        }

        private static string TrimToTwoSentences(string text)
        {
            var terminators = new char[] { '.', '!', '?' };
            int end = -1, count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (Array.IndexOf(terminators, text[i]) >= 0)
                {
                    count++;
                    end = i;
                    if (count >= 3) break;
                }
            }
            return end >= 0 ? text.Substring(0, end + 1).Trim() : text;
        }

        // ─── Economy Feedback ─────────────────────────────────────────────────────
        private static void ApplyEconomyFeedback(string eventText, GameplayManager manager)
        {
            if (string.IsNullOrEmpty(eventText)) return;
            string lower = eventText.ToLower();

            string newCondition   = null;
            float  newBuy         = WorldData.CurrentState.Market.PriceMultiplier;
            float  newSell        = WorldData.CurrentState.Market.SellMultiplier;
            string feedbackEvent  = null;

            if (ContainsAny(lower, "plague", "pestilence", "disease", "sickness", "dying", "death", "blight", "famine", "drought"))
            {
                newCondition  = "Depression";
                newBuy        = 0.6f;
                newSell       = 0.4f;
                feedbackEvent = "Disease and death have devastated trade routes. Markets have collapsed.";
            }
            else if (ContainsAny(lower, "war", "battle", "invasion", "siege", "crusade", "raid", "conflict", "blockade"))
            {
                newCondition  = "Shortage";
                newBuy        = 1.4f;
                newSell       = 1.2f;
                feedbackEvent = "Wartime demands and disrupted supply lines have driven prices up sharply.";
            }
            else if (ContainsAny(lower, "discovery", "prosperity", "golden age", "abundance", "harvest", "trade route", "opens"))
            {
                newCondition  = "Surplus";
                newBuy        = 0.7f;
                newSell       = 0.6f;
                feedbackEvent = "New prosperity has brought goods flooding into the markets across the realm.";
            }
            else if (ContainsAny(lower, "dark lord", "evil rises", "shadow", "dread", "omen", "prophes"))
            {
                newCondition  = "Shortage";
                newBuy        = 1.3f;
                newSell       = 1.1f;
                feedbackEvent = "Fear of the rising darkness has caused widespread hoarding and market disruption.";
            }
            else if (ContainsAny(lower, "inflation", "coin", "treasury", "taxe", "wealth flows"))
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

        private static bool ContainsAny(string text, params string[] keywords)
        {
            foreach (var k in keywords)
                if (text.Contains(k)) return true;
            return false;
        }

        // ─── Economy Tick ─────────────────────────────────────────────────────────
        public static void RunEconomyTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running Economy Tick at Turn {WorldData.CurrentState.CurrentTurn}");

            if (rng.NextDouble() >= 0.20) return; // 20% chance to change state

            var market = WorldData.CurrentState.Market;
            market.PreviousCondition = market.GlobalCondition;

            string desc  = "";
            int state = rng.Next(5);
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
            ApplySeasonBias(WorldData.CurrentState.CurrentSeason);

            WorldData.LogEvent(desc, "ECONOMY");
            WorldEventsUI.MarkDirty();
        }

        // ─── Minor Tick (faction simulation) ─────────────────────────────────────
        public static void RunMinorTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running Minor Tick at Turn {WorldData.CurrentState.CurrentTurn}");

            var factions = manager.GetCurrentFactions();
            if (factions == null || factions.Count == 0) return;

            var eliminated = WorldData.CurrentState.EliminatedFactions;

            // Income processing (skip eliminated factions)
            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                if (eliminated.Contains(faction.uuid)) continue;

                var data = WorldData.GetFactionData(faction.uuid);

                // Cache faction name if not stored yet
                if (string.IsNullOrEmpty(data.Name))
                    data.Name = faction.GetPrettyName();

                data.Resources += 5;
            }

            // Pick an active non-player faction to act
            var activeFactions = factions
                .Where(f => f.GetPrettyName() != "Player" && !eliminated.Contains(f.uuid))
                .ToList();
            if (activeFactions.Count == 0) return;

            var acting    = activeFactions[rng.Next(activeFactions.Count)];
            var actingData = WorldData.GetFactionData(acting.uuid);

            if (actingData.Resources > 30)
            {
                var others = activeFactions.Where(f => f.uuid != acting.uuid).ToList();
                if (others.Count == 0) return;
                var target     = others[rng.Next(others.Count)];
                PerformFactionAction(acting, target, actingData, manager);
            }
        }

        private static void PerformFactionAction(Faction acting, Faction target, FactionExtData actingData, GameplayManager manager)
        {
            UpdateFactionTag(acting);
            UpdateFactionTag(target);

            string actingTag = actingData.Tag;
            string targetTag = WorldData.GetFactionData(target.uuid).Tag;
            string relKey    = WorldData.GetRelationshipKey(acting.uuid, target.uuid);

            // Establish initial relationship if unknown
            if (!WorldData.CurrentState.FactionRelationships.ContainsKey(relKey))
            {
                string rel = "NEUTRAL";
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    rel = "ENEMIES";
                else if (actingTag == targetTag && actingTag != "Neutral")
                    rel = "ALLIES";
                else if (rng.NextDouble() > 0.8)
                    rel = rng.NextDouble() > 0.5 ? "ALLIES" : "ENEMIES";
                WorldData.CurrentState.FactionRelationships[relKey] = rel;
            }

            string relation = WorldData.CurrentState.FactionRelationships[relKey];
            bool atWar      = WorldData.CurrentState.ActiveWars.ContainsKey(relKey);

            // Action weights: [War/Raid, Trade, Rumor]
            double[] weights;
            if (atWar || relation == "ENEMIES")
                weights = new double[] { 85, 0, 15 };
            else if (relation == "ALLIES")
                weights = new double[] { 0, 80, 20 };
            else
            {
                weights = new double[] { 30, 30, 40 };
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    weights[0] += 50;
                if (actingTag == "Trade" || targetTag == "Trade")
                    weights[1] += 40;
            }

            int actionType = PickWeighted(weights);

            var targetData = WorldData.GetFactionData(target.uuid);
            string eventDesc = "";
            string eventType = "INFO";

            if (actionType == 0) // WAR / RAID
            {
                int cost = 30;
                if (actingData.Resources >= cost)
                {
                    actingData.Resources -= cost;
                    bool success = (rng.NextDouble() + actingData.Resources * 0.01)
                                 > (rng.NextDouble() + targetData.Resources * 0.01);
                    if (success)
                    {
                        int stolen = rng.Next(10, 30);
                        targetData.Resources = Math.Max(0, targetData.Resources - stolen);
                        actingData.Resources += stolen;
                        eventDesc = $"{acting.GetPrettyName()} raided {target.GetPrettyName()} and plundered {stolen} resources!";
                        eventType = "WAR";

                        // Accumulate grievance → escalate to formal war
                        WorldData.AddGrievance(relKey);
                        int grievance = WorldData.GetGrievance(relKey);
                        if (grievance >= GRIEVANCE_WAR_THRESHOLD && !atWar)
                        {
                            string casusBelli = PickCasusBelli(actingTag, targetTag, relKey);
                            WorldData.DeclareWar(acting.uuid, acting.GetPrettyName(),
                                                 target.uuid, target.GetPrettyName(), casusBelli);
                        }

                        // Territory conquest if at war
                        if (atWar && targetData.ClaimedPlaceUuids.Count > 0)
                            TryCaptureTerritory(actingData, targetData, acting.GetPrettyName(), target.GetPrettyName());

                        // Faction elimination check
                        if (targetData.Resources <= 0)
                            EliminateFaction(target, acting, targetData, actingData, manager);
                    }
                    else
                    {
                        eventDesc = $"{acting.GetPrettyName()} tried to raid {target.GetPrettyName()} but was repelled.";
                        eventType = "WAR";
                    }
                }
            }
            else if (actionType == 1) // TRADE
            {
                if (actingData.Resources >= 10 && targetData.Resources >= 10)
                {
                    actingData.Resources += 15;
                    targetData.Resources += 15;
                    eventDesc = $"{acting.GetPrettyName()} and {target.GetPrettyName()} have deepened their economic ties through trade.";
                    eventType = "TRADE";

                    // Allies trading resets some grievances
                    if (WorldData.CurrentState.GrievanceCounts.ContainsKey(relKey))
                        WorldData.CurrentState.GrievanceCounts[relKey] = Math.Max(0, WorldData.CurrentState.GrievanceCounts[relKey] - 1);
                }
            }
            else // RUMOR / DIPLOMACY
            {
                string[] flavors = relation == "ENEMIES"
                    ? new[] { "denounced", "mocked", "threatened", "spied on", "accused" }
                    : relation == "ALLIES"
                        ? new[] { "praised", "sent gifts to", "held a feast for", "supported", "defended" }
                        : new[] { "sent diplomats to", "observed", "has concerns about", "is ignoring", "proposed talks with" };

                string flavor = flavors[rng.Next(flavors.Length)];
                eventDesc = $"{acting.GetPrettyName()} {flavor} {target.GetPrettyName()}.";
                eventType = "RUMOR";
            }

            if (!string.IsNullOrEmpty(eventDesc))
            {
                WorldData.LogEvent(eventDesc, eventType);
                if (eventType == "WAR" || eventType == "TRADE")
                    WorldLoreExpansion.RecordHistoricalEvent(manager, eventDesc, "History",
                        new List<string> { acting.GetPrettyName(), target.GetPrettyName(), eventType.ToLower() });
                WorldEventsUI.MarkDirty();
            }
        }

        // ─── Territory Conquest ───────────────────────────────────────────────────
        private static void TryCaptureTerritory(FactionExtData winner, FactionExtData loser,
            string winnerName, string loserName)
        {
            if (rng.NextDouble() > 0.33) return;
            string territory = loser.ClaimedPlaceUuids[rng.Next(loser.ClaimedPlaceUuids.Count)];
            loser.ClaimedPlaceUuids.Remove(territory);
            winner.ClaimedPlaceUuids.Add(territory);
            WorldData.LogEvent($"{winnerName} has seized a territory from {loserName}!", "WAR");
        }

        // ─── Faction Elimination ──────────────────────────────────────────────────
        private static void EliminateFaction(Faction loser, Faction victor,
            FactionExtData loserData, FactionExtData victorData, GameplayManager manager)
        {
            WorldData.CurrentState.EliminatedFactions.Add(loser.uuid);

            // Transfer territories to victor
            foreach (var territory in loserData.ClaimedPlaceUuids)
                victorData.ClaimedPlaceUuids.Add(territory);
            loserData.ClaimedPlaceUuids.Clear();

            string desc = $"[FALL OF {loser.GetPrettyName().ToUpper()}] {loser.GetPrettyName()} has been utterly defeated and absorbed by {victor.GetPrettyName()}!";
            WorldData.LogEvent(desc, "MAJOR");
            WorldData.CurrentState.MajorEventHistory.Add(desc);

            // End any active war between them
            string key = WorldData.GetRelationshipKey(loser.uuid, victor.uuid);
            if (WorldData.CurrentState.ActiveWars.ContainsKey(key))
                WorldData.CurrentState.ActiveWars.Remove(key);

            WorldLoreExpansion.RecordHistoricalEvent(manager, desc, "History",
                new List<string> { loser.GetPrettyName(), victor.GetPrettyName(), "fallen", "conquered" });
            WorldEventsUI.MarkDirty();
            Debug.Log($"[WorldExpansion] Faction eliminated: {loser.GetPrettyName()}");
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────
        private static string PickCasusBelli(string actingTag, string targetTag, string relKey)
        {
            if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                return "ideological";
            if (actingTag == "Empire" || targetTag == "Empire")
                return "territorial";
            if (WorldData.GetGrievance(relKey) >= 5)
                return "revenge";
            string[] options = { "territorial", "expansion", "revenge", "ideological", "resources" };
            return options[rng.Next(options.Length)];
        }

        private static void UpdateFactionTag(Faction f)
        {
            var data = WorldData.GetFactionData(f.uuid);
            if (data.Tag != "Neutral") return;

            string name = f.GetPrettyName().ToLower();

            if (ContainsAny(name, "holy", "church", "clergy", "divine", "temple", "cult", "faith", "order of"))
                data.Tag = "Holy";
            else if (ContainsAny(name, "demon", "hell", "abyss", "dark", "shadow", "evil", "chaos", "fiend"))
                data.Tag = "Demon";
            else if (ContainsAny(name, "trade", "merchant", "guild", "commerce", "bank", "cartel", "company", "exchange"))
                data.Tag = "Trade";
            else if (ContainsAny(name, "empire", "kingdom", "realm", "state", "republic", "alliance", "federation", "dominion"))
                data.Tag = "Empire";
            else if (ContainsAny(name, "nature", "wild", "tribe", "clan", "druid", "forest", "beast", "horde"))
                data.Tag = "Nature";
            else if (ContainsAny(name, "arcane", "mage", "wizard", "academy", "magic", "circle", "enclave"))
                data.Tag = "Arcane";
            else if (ContainsAny(name, "undead", "necromancer", "death", "lich", "grave", "crypt"))
                data.Tag = "Undead";
        }

        private static int PickWeighted(double[] weights)
        {
            double total = 0;
            foreach (var w in weights) total += w;
            double r = rng.NextDouble() * total;
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i];
                if (r <= sum) return i;
            }
            return weights.Length - 1;
        }
    }
}
