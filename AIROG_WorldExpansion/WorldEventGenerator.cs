using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    /// <summary>AI-narrated (with fallback) major world events, fired by WorldSimulation.RunMajorTick.</summary>
    internal static class WorldEventGenerator
    {
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

        public static void RunMajorTick(GameplayManager manager)
        {
            Debug.Log($"[WorldExpansion] Running MAJOR Tick at Turn {WorldData.CurrentState.CurrentTurn}");

            // Reschedule first to prevent double-fire if AI takes a while
            WorldData.CurrentState.NextMajorEventTurn = WorldData.CurrentState.CurrentTurn + WorldSimUtils.Rng.Next(10, 101);

            GenerateMajorEventAsync(manager);
        }

        private static async void GenerateMajorEventAsync(GameplayManager manager)
        {
            try
            {
                string desc = await TryGenerateAIMajorEvent(manager);

                WorldData.LogEvent(desc, "MAJOR");
                WorldData.CurrentState.MajorEventHistory.Add(desc);
                WorldData.QueuePlayerEvent(desc, "MAJOR_EVENT");

                // Apply economy feedback based on event content
                WorldEconomySimulator.ApplyEconomyFeedback(desc, manager);

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

                var topFactions = state.Factions
                    .Where(kv => !string.IsNullOrEmpty(kv.Value.Name) && !state.EliminatedFactions.Contains(kv.Key))
                    .OrderByDescending(kv => kv.Value.Resources)
                    .Take(3)
                    .Select(kv => $"{kv.Value.Name} [{kv.Value.Tag}]");
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
                    return TrimToThreeSentences(raw.Trim());
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

            return pool[WorldSimUtils.Rng.Next(pool.Count)];
        }

        // Caps output at 3 sentences, matching the prompt's "2-3 sentences" ask.
        private static string TrimToThreeSentences(string text)
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
    }
}
