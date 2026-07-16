using HarmonyLib;

namespace AIROG_WorldExpansion
{
    /// <summary>
    /// Turn-tick entry point for the world simulation. Kept as a thin façade: GrandStrategy's
    /// DominionManager Harmony-patches WorldSimulation.RunMinorTick directly by reflection
    /// (typeof(WorldSimulation), nameof(RunMinorTick)), so this class and its public method
    /// names/signatures must not move. The actual logic lives in WorldTickCoordinator,
    /// WorldEventGenerator, WorldEconomySimulator, and WorldDiplomacyEngine.
    /// </summary>
    public class WorldSimulation
    {
        private const int SEASON_LENGTH        = 20;  // turns per season
        private const int MINOR_TICK_TURNS     = 5;
        private const int DIPLOMACY_TICK_TURNS = 10;
        private const int ECONOMY_TICK_TURNS   = 25;

        // ─── Main Tick ────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameplayManager), "InvokeTurnHappened")]
        [HarmonyPostfix]
        public static void OnTurnHappened(GameplayManager __instance, int numTurns, long secs)
        {
            if (__instance == null) return;

            var state = WorldData.CurrentState;
            state.CurrentTurn += numTurns;
            int turn = state.CurrentTurn;

            // Lazy per-faction territory + population seeding (covers factions generated mid-game)
            WorldTickCoordinator.SeedNewFactions(__instance);

            // Season advancement
            state.SeasonTurnCounter += numTurns;
            while (state.SeasonTurnCounter >= SEASON_LENGTH)
            {
                state.SeasonTurnCounter -= SEASON_LENGTH;
                WorldTickCoordinator.AdvanceSeason(__instance);
            }

            // Peace checks for active wars
            WorldTickCoordinator.CheckActiveWarPeace();

            // Accumulator-based ticks: a multi-turn skip (rest) can't jump over a boundary
            state.TurnsSinceDiplomacyTick += numTurns;
            if (state.TurnsSinceDiplomacyTick >= DIPLOMACY_TICK_TURNS)
            {
                state.TurnsSinceDiplomacyTick = 0;
                WorldDiplomacyEngine.ShiftDiplomacyOverTime();
            }

            state.TurnsSinceMinorTick += numTurns;
            if (state.TurnsSinceMinorTick >= MINOR_TICK_TURNS)
            {
                state.TurnsSinceMinorTick = 0;
                RunMinorTick(__instance);
            }

            state.TurnsSinceEconomyTick += numTurns;
            if (state.TurnsSinceEconomyTick >= ECONOMY_TICK_TURNS)
            {
                state.TurnsSinceEconomyTick = 0;
                RunEconomyTick(__instance);
            }

            // Major Event — when scheduled
            if (turn >= state.NextMajorEventTurn)
                RunMajorTick(__instance);

            // Persist every turn: GenContext's provider reads the JSON from disk, so without
            // this, mid-session events (wars, alerts) wouldn't reach the AI until a game save
            WorldData.SaveToCurrentDir();
        }

        public static void RunMajorTick(GameplayManager manager) => WorldEventGenerator.RunMajorTick(manager);

        public static void RunEconomyTick(GameplayManager manager) => WorldEconomySimulator.RunEconomyTick(manager);

        public static void RunMinorTick(GameplayManager manager) => WorldDiplomacyEngine.RunMinorTick(manager);
    }
}
