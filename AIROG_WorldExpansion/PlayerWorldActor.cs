using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    /// <summary>
    /// Bridges the game's NATIVE player↔faction reputation (Faction.playerFactionRep,
    /// changed by the AI via DeltaRep) into the world simulation, making the player a
    /// world actor:
    ///  - repeated rep losses with a faction accumulate grievances → bounty on the player
    ///  - hostile factions harass the player; admiring factions honor them (minor tick)
    ///  - wars and faction falls ripple back into native rep and player-facing alerts
    /// All player-facing consequences flow through PendingWorldEvents so GenContext
    /// surfaces them to the AI narrator.
    /// </summary>
    public static class PlayerWorldActor
    {
        private const int   PLAYER_GRIEVANCE_BOUNTY_THRESHOLD = 3;
        private const float BIG_REP_DELTA = 10f; // native minor changes are ±10, major ±20

        private static bool _selfInflicted; // guard: don't react to our own DeltaRep calls

        // ─── Native rep bridge ────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Faction), "DeltaRep")]
        [HarmonyPostfix]
        public static void Postfix_DeltaRep(Faction __instance, float delta)
        {
            if (_selfInflicted || __instance == null) return;
            try
            {
                ReactToRepChange(__instance, delta);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Player rep reaction failed: {e.Message}");
            }
        }

        private static void ReactToRepChange(Faction faction, float delta)
        {
            var state = WorldData.CurrentState;
            string uuid = faction.uuid;
            string name = faction.GetPrettyName();

            if (delta <= -BIG_REP_DELTA)
            {
                if (!state.PlayerGrievances.ContainsKey(uuid))
                    state.PlayerGrievances[uuid] = 0;
                state.PlayerGrievances[uuid]++;

                if (state.PlayerGrievances[uuid] >= PLAYER_GRIEVANCE_BOUNTY_THRESHOLD
                    && faction.GetStanding() <= Faction.FactionStanding.SCORNED
                    && state.PlayerBounties.Add(uuid))
                {
                    WorldData.LogEvent($"{name} has placed a bounty on the player's head!", "PLAYER");
                    WorldData.QueuePlayerEvent(
                        $"{name} has placed a bounty on your head. Their agents are watching for you.",
                        "FACTION_BOUNTY");
                    WorldEventsUI.MarkDirty();
                    WorldData.SaveToCurrentDir();
                }
            }
            else if (delta >= BIG_REP_DELTA)
            {
                // Goodwill erodes recorded grievances, and lifts bounties once trust returns
                if (state.PlayerGrievances.TryGetValue(uuid, out int g) && g > 0)
                    state.PlayerGrievances[uuid] = g - 1;

                if (state.PlayerBounties.Contains(uuid)
                    && faction.GetStanding() >= Faction.FactionStanding.NONE)
                {
                    state.PlayerBounties.Remove(uuid);
                    state.PlayerGrievances[uuid] = 0;
                    WorldData.LogEvent($"{name} has rescinded its bounty on the player.", "PLAYER");
                    WorldData.QueuePlayerEvent($"{name} has called off its bounty on you.", "BOUNTY_LIFTED");
                    WorldEventsUI.MarkDirty();
                    WorldData.SaveToCurrentDir();
                }
            }
        }

        // ─── Standing tick (called from RunMinorTick) ─────────────────────────────
        public static void StandingTick(GameplayManager manager)
        {
            try
            {
                StandingTickInner(manager);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Standing tick failed: {e.Message}");
            }
        }

        private static void StandingTickInner(GameplayManager manager)
        {
            var factions = manager?.GetCurrentFactions();
            if (factions == null) return;
            var state = WorldData.CurrentState;

            // Don't pile on: skip if a player-targeted alert is already live
            bool playerEventActive = state.PendingWorldEvents.Any(e =>
                (e.Type == "FACTION_BOUNTY" || e.Type == "FACTION_HOSTILITY" || e.Type == "FACTION_HONOR")
                && e.TurnAdded + e.TtlTurns >= state.CurrentTurn);
            if (playerEventActive) return;

            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                if (state.EliminatedFactions.Contains(faction.uuid)) continue;

                var standing = faction.GetStanding();
                string name  = faction.GetPrettyName();

                if (standing <= Faction.FactionStanding.SCORNED && WorldSimUtils.Rng.NextDouble() < 0.15)
                {
                    bool bounty = state.PlayerBounties.Contains(faction.uuid);
                    string[] hostileActs = bounty
                        ? new[]
                        {
                            $"Bounty hunters hired by {name} are asking about the player in nearby settlements.",
                            $"{name}'s agents have been spotted shadowing the player's trail.",
                        }
                        : new[]
                        {
                            $"{name} has publicly denounced the player.",
                            $"{name} is spreading ugly rumors about the player.",
                            $"Agents of {name} are keeping hostile watch on the player's movements.",
                        };
                    string act = hostileActs[WorldSimUtils.Rng.Next(hostileActs.Length)];
                    WorldData.LogEvent(act, "PLAYER");
                    WorldData.QueuePlayerEvent(act, "FACTION_HOSTILITY");
                    WorldEventsUI.MarkDirty();
                    return; // at most one player-targeted event per tick
                }

                if (standing >= Faction.FactionStanding.ADMIRED && WorldSimUtils.Rng.NextDouble() < 0.08)
                {
                    string[] friendlyActs =
                    {
                        $"{name} publicly praised the player's deeds; word of it spreads through taverns.",
                        $"{name} has sent an envoy bearing a gift and warm regards for the player.",
                        $"Songs commissioned by {name} about the player's exploits are being sung in the streets.",
                    };
                    string act = friendlyActs[WorldSimUtils.Rng.Next(friendlyActs.Length)];
                    WorldData.LogEvent(act, "PLAYER");
                    WorldData.QueuePlayerEvent(act, "FACTION_HONOR");
                    WorldEventsUI.MarkDirty();
                    return;
                }
            }
        }

        // ─── War ripples ──────────────────────────────────────────────────────────
        /// A war declaration strains the aggressor's relationship with a player who
        /// befriended the target; if the player is close to both sides, they're torn.
        public static void OnWarDeclared(Faction aggressor, Faction target)
        {
            try
            {
                bool targetIsFriend    = target.GetStanding()    >= Faction.FactionStanding.TRUSTED;
                bool aggressorIsFriend = aggressor.GetStanding() >= Faction.FactionStanding.TRUSTED;

                if (targetIsFriend && aggressorIsFriend)
                {
                    WorldData.QueuePlayerEvent(
                        $"Two factions that hold you in high regard — {aggressor.GetPrettyName()} and {target.GetPrettyName()} — are now at war. Both may seek your support.",
                        "TORN_ALLEGIANCE");
                }
                else if (targetIsFriend)
                {
                    SafeDeltaRep(aggressor, -10f);
                    string msg = $"{aggressor.GetPrettyName()} views the player with suspicion because of their ties to {target.GetPrettyName()}.";
                    WorldData.LogEvent(msg, "PLAYER");
                    WorldData.QueuePlayerEvent(msg, "WAR_SUSPICION");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] War rep ripple failed: {e.Message}");
            }
        }

        /// The fall of a faction the player cared about (or despised) is personal news.
        public static void OnFactionFallen(Faction fallen, Faction victor)
        {
            try
            {
                // A fallen faction can't credibly still be hunting the player — drop any
                // bounty unconditionally, even if their standing drifted above Scorned
                // (e.g. via small native rep gains) before they were eliminated. Otherwise
                // it lingers forever: nothing else clears a bounty except a future DeltaRep
                // on a faction that no longer takes any actions.
                WorldData.CurrentState.PlayerBounties.Remove(fallen.uuid);

                if (fallen.GetStanding() >= Faction.FactionStanding.TRUSTED)
                {
                    SafeDeltaRep(victor, -15f);
                    WorldData.QueuePlayerEvent(
                        $"{fallen.GetPrettyName()}, a faction that held you in high regard, has fallen to {victor.GetPrettyName()}. Its scattered survivors may look to you.",
                        "ALLY_FALLEN");
                }
                else if (fallen.GetStanding() <= Faction.FactionStanding.SCORNED)
                {
                    WorldData.QueuePlayerEvent(
                        $"{fallen.GetPrettyName()}, a faction hostile to you, has been destroyed by {victor.GetPrettyName()}. One enemy fewer.",
                        "ENEMY_FALLEN");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Faction fall rep ripple failed: {e.Message}");
            }
        }

        // Calls native DeltaRep without re-triggering our own postfix
        private static void SafeDeltaRep(Faction f, float delta)
        {
            _selfInflicted = true;
            try { f.DeltaRep(delta); }
            finally { _selfInflicted = false; }
        }
    }
}
