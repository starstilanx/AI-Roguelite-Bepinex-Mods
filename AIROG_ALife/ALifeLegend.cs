using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.0 "The Wake": the world reacts to the player the way an ecosystem reacts to
    /// a predator. This is deliberately NOT a nemesis system (NPCExpansion owns personal
    /// grudges) — no squad ever hunts the player. Instead:
    ///  - squads accumulate FEAR (from witnessed deaths, spread onward as rumor) and
    ///    AWE (from peaceful contact, defections, gifts of mercy);
    ///  - frightened squads refuse to face the player: they flee before they're seen,
    ///    or materialize wary — parleying instead of attacking;
    ///  - places where the player kills accrue DREAD, and squads (caravans especially)
    ///    reroute around those killing grounds, physically reshaping travel patterns;
    ///  - the player's LEGEND is the global tally that seasons all of it.
    /// </summary>
    public static class ALifeLegend
    {
        public const int FEAR_FLEE = 60;   // squads at/above this refuse to share a place with the player
        public const int FEAR_WARY = 30;   // hostile squads at/above this parley instead of attacking
        public const int DREAD_AVOID = 4;  // places at/above this get routed around
        public const int LEGEND_MAX = 60;  // ceiling: a reputation that can't be outlived isn't one

        // ── Live kill tracking ──────────────────────────────────────────────────

        [HarmonyPatch(typeof(GameCharacter), "SetAsCorpse")]
        public static class Patch_SetAsCorpse
        {
            [HarmonyPostfix]
            public static void Postfix(GameCharacter __instance)
            {
                try
                {
                    if (ALifeEmbodiment.ApplyingOfflineCasualties) return;
                    OnRealDeath(__instance);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ALife] Kill tracking failed: " + ex.Message);
                }
            }
        }

        /// <summary>A real GameCharacter died by the game's own hand. If it was an
        /// embodied squad member, the squad — and the world — take note.</summary>
        public static void OnRealDeath(GameCharacter ch)
        {
            if (ch == null || string.IsNullOrEmpty(ch.uuid)) return;
            VirtualSquad squad = ALifeData.State.Squads
                .FirstOrDefault(s => s.IsEmbodied && s.MemberUuids.Contains(ch.uuid));
            if (squad == null) return;

            squad.MemberUuids.Remove(ch.uuid);
            squad.Size = Math.Max(0, squad.Size - 1);

            GameplayManager manager = SS.I?.hackyManager;
            string playerTop = manager?.currentPlace?.GetTopLvlPlace()?.uuid;
            bool nearPlayer = playerTop != null && squad.CurrentPlaceUuid == playerTop;
            bool wasLeader = ch.uuid == squad.Leader?.Uuid;

            if (nearPlayer)
            {
                // Witnessed death in the player's presence: fear, dread, legend.
                string playerName = ALifeEmbodiment.SafePlayerName(manager);
                squad.DeathsThisVisit++;
                squad.FearOfPlayer = Math.Min(100, squad.FearOfPlayer + (wasLeader ? 35 : 15));
                ALifeData.State.PlayerLegend = Math.Min(LEGEND_MAX, ALifeData.State.PlayerLegend + 1);
                AddDread(squad.CurrentPlaceUuid, squad.CurrentPlaceName, 2);
                SpreadRumorOfPlayer(squad.CurrentPlaceUuid, wasLeader ? 12 : 8, squad.Id);
                squad.AddChronicle($"{ch.GetPrettyName()} fell in an encounter with {playerName} at {squad.CurrentPlaceName}.");
            }
            else
            {
                squad.AddChronicle($"{ch.GetPrettyName()} was slain at {squad.CurrentPlaceName}.");
            }

            if (wasLeader)
            {
                squad.Leader.Uuid = null;
                if (squad.Size > 0)
                    ALifeLifecycle.Succession(squad, null, leaderDead: true,
                        nearPlayer ? "slain before the whole band" : "slain at " + squad.CurrentPlaceName);
            }

            if (squad.Size <= 0)
                ALifeLifecycle.WipeSquad(squad, nearPlayer
                    ? "destroyed in an encounter with " + ALifeEmbodiment.SafePlayerName(manager)
                    : "wiped out at " + squad.CurrentPlaceName);
        }

        // ── Dread zones ─────────────────────────────────────────────────────────

        public static void AddDread(string placeUuid, string placeName, int amount)
        {
            var st = ALifeData.State;
            st.DreadMap.TryGetValue(placeUuid, out int cur);
            st.DreadMap[placeUuid] = Math.Min(20, cur + amount);
            st.DreadNames[placeUuid] = placeName;
        }

        public static int DreadAt(string placeUuid)
        {
            return placeUuid != null && ALifeData.State.DreadMap.TryGetValue(placeUuid, out int d) ? d : 0;
        }

        /// <summary>Should this squad route around that place? Fearful squads and caravans avoid killing grounds.</summary>
        public static bool AvoidsPlace(VirtualSquad squad, string placeUuid)
        {
            int dread = DreadAt(placeUuid);
            if (dread < DREAD_AVOID) return false;
            if (squad.Archetype == SquadArchetype.CARAVAN || squad.Archetype == SquadArchetype.PILGRIMS)
                return true;                              // civilians always avoid
            return squad.FearOfPlayer >= FEAR_WARY;       // fighters only if they've heard the stories
        }

        // ── Rumor of the player ─────────────────────────────────────────────────

        /// <summary>
        /// Word of violence travels one hop: nearby squads gain secondhand fear. Squads
        /// sharing the death site witnessed it directly and get the full bump, regardless of
        /// whether they'd already met the player — direct witnessing isn't rumor, and a band
        /// that already knows the player can still be freshly reminded why. excludeSquadId
        /// keeps this from double-dipping the squad whose own member just died in front of
        /// the player: that squad already took its (larger) direct hit in OnRealDeath.
        /// </summary>
        public static void SpreadRumorOfPlayer(string placeUuid, int fearAmount, string excludeSquadId = null)
        {
            var neighborUuids = new HashSet<string>(ALifeGraph.Neighbors(placeUuid).Select(p => p.uuid));
            foreach (var s in ALifeData.State.Squads)
            {
                if (s.Id == excludeSquadId) continue;
                if (s.CurrentPlaceUuid == placeUuid)
                    s.FearOfPlayer = Math.Min(100, s.FearOfPlayer + fearAmount);
                else if (neighborUuids.Contains(s.CurrentPlaceUuid))
                    s.FearOfPlayer = Math.Min(100, s.FearOfPlayer + fearAmount / 2);
            }
        }

        // ── Decay ───────────────────────────────────────────────────────────────

        private static int _decayCarry;

        /// <summary>Clears the fractional-turn decay accumulator. Without this, starting a
        /// second New Game (or loading a save) in the same process inherited whatever partial
        /// turn count was left over from a previous playthrough's decay cycle.</summary>
        public static void ResetDecayCarry()
        {
            _decayCarry = 0;
        }

        public static void DecayTick(int numTurns)
        {
            var st = ALifeData.State;
            _decayCarry += numTurns;
            while (_decayCarry >= 4)
            {
                _decayCarry -= 4;
                foreach (string key in st.DreadMap.Keys.ToList())
                {
                    st.DreadMap[key]--;
                    if (st.DreadMap[key] <= 0) { st.DreadMap.Remove(key); st.DreadNames.Remove(key); }
                }
                foreach (var s in st.Squads)
                {
                    if (s.FearOfPlayer > 0) s.FearOfPlayer--;
                    if (s.AweOfPlayer > 0 && s.AweOfPlayer > 60) s.AweOfPlayer--; // high awe cools; earned respect keeps
                }
                // Legend fades on the same 4-turn cadence as fear and dread. The old rule
                // only fired when the turn counter happened to land on a multiple of 12,
                // so clearing a couple of bands early left the player "feared across the
                // region" for the rest of the run — pinning every scene at high tension.
                if (st.PlayerLegend > 0) st.PlayerLegend--;
            }
        }

        public static string LegendTier()
        {
            int l = ALifeData.State.PlayerLegend;
            if (l >= 40) return "a walking legend — spoken of around every fire";
            if (l >= 20) return "feared across the region";
            if (l >= 8) return "known and talked about";
            return null;
        }
    }
}
