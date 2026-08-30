using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// STALKER's offline→online switch, v2.0: two-way. When the player arrives at a
    /// top-level place where a virtual squad is present, the squad materializes into
    /// real GameCharacters — leader first, carrying their dossier. The squad record
    /// SURVIVES: when the player leaves, ALifeEmbodiment reconciles the visit
    /// (deaths, defections, peace) and the squad resumes its offline life with the
    /// same real entities. Squads that fear the player enough refuse the meeting
    /// entirely and flee before they're seen.
    /// </summary>
    public static class ALifeMaterializer
    {
        private static readonly System.Random Rng = new System.Random();

        /// <summary>
        /// Minimum turns between two presence announcements for the same band on the same
        /// ground. Matched to ALifeProvider's event window so a repeat can never land in
        /// the same prompt as the first. ApplyLocationChange fires on every arrival —
        /// including sub-location moves inside one settlement — so without this a band
        /// the player walked past once re-announces itself for the rest of the run.
        /// </summary>
        private const int ANNOUNCE_COOLDOWN_TURNS = 25;

        /// <summary>
        /// Set for the duration of GameplayManager.LoadGame. Its own arrival call fires
        /// synchronously, before our own postfix on LoadGame gets to load the save's real
        /// state — so OnPlayerArrived must not run (or save) against whatever was in memory
        /// beforehand. ALifePlugin replays the arrival once the real data is loaded.
        /// </summary>
        public static bool SuppressArrival;

        [HarmonyPatch(typeof(GameplayManager), "ApplyLocationChange")]
        public static class Patch_ApplyLocationChange
        {
            [HarmonyPostfix]
            public static void Postfix(GameplayManager __instance, Place newPl)
            {
                try
                {
                    OnPlayerArrived(__instance, newPl);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[ALife] Materialization failed: " + ex);
                }
            }
        }

        public static void OnPlayerArrived(GameplayManager manager, Place newPl)
        {
            if (SuppressArrival) return;
            if (!ALifePlugin.CfgEnableSim.Value) return;
            if (manager == null || newPl == null) return;

            Place top = newPl.GetTopLvlPlace();
            if (top == null) return;

            // Crossing a top-level boundary: settle accounts at the place we left.
            string prevTop = ALifeData.State.LastPlayerTopUuid;
            if (prevTop != top.uuid)
            {
                if (prevTop != null)
                    ALifeEmbodiment.ReconcileDeparture(manager, prevTop);
                ALifeData.State.LastPlayerTopUuid = top.uuid;
                // A genuinely new visit: reset the bloodshed tally for every band on this
                // ground. (This used to live in Materialize, which re-runs on every
                // sub-location move — so killing two raiders and stepping indoors wiped
                // the tally and earned the band's respect for a "bloodless" visit.)
                foreach (var s in ALifeData.State.Squads)
                    if (s.CurrentPlaceUuid == top.uuid)
                        s.DeathsThisVisit = 0;
                ALifeData.SaveToCurrentDir();
            }

            ALifeKnowledge.DiscoverSite(top.uuid); // walking the ground reveals what happened on it

            if (!ALifePlugin.CfgMaterialize.Value) return;

            var squadsHere = ALifeData.State.Squads.Where(s => s.CurrentPlaceUuid == top.uuid).ToList();
            if (squadsHere.Count == 0) return;

            // The Wake: squads that fear the player refuse the meeting and bolt.
            foreach (var scared in squadsHere.ToList())
            {
                // Only drop it from squadsHere if it actually got somewhere — a squad with
                // no reachable neighbor (isolated top-level place) stays put and stays
                // eligible for materialization instead of silently vanishing from every
                // future visit.
                if (scared.FearOfPlayer >= ALifeLegend.FEAR_FLEE && FleeFromPlayer(manager, scared, top))
                    squadsHere.Remove(scared);
            }
            if (squadsHere.Count == 0) return;

            // One squad per arrival, hostiles first — the rest stay virtual "in the area"
            // (frozen by the online bubble) and are surfaced narratively by the provider.
            VirtualSquad squad = squadsHere.FirstOrDefault(s => IsHostileToPlayer(s))
                                 ?? squadsHere[0];
            Materialize(manager, squad, newPl);
        }

        public static bool IsHostileToPlayer(VirtualSquad s)
        {
            switch (s.Archetype)
            {
                case SquadArchetype.HUNTERS: return true;
                // deterministic per-squad so repeated checks agree, and stable across
                // process restarts — string.GetHashCode() is randomized per-process in
                // .NET, which would flip a persisted squad's hostility on the next launch
                case SquadArchetype.RAIDERS: return (StableHash(s.Id ?? "") % 10) < 6;
                case SquadArchetype.WARBAND:
                case SquadArchetype.PATROL:
                    return ALifeWorldBridge.PlayerHasBountyFrom(s.FactionUuid);
                default: return false;
            }
        }

        /// <summary>Process-stable string hash (unlike string.GetHashCode(), which is
        /// randomized per-process in .NET Framework 4.5+). Always non-negative, so callers
        /// never need Math.Abs (which overflows on int.MinValue).</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in s) hash = hash * 31 + c;
                return hash & 0x7FFFFFFF;
            }
        }

        /// <summary>Hostile on paper, but too afraid to start the fight: parleys instead.</summary>
        public static bool IsWaryOfPlayer(VirtualSquad s)
        {
            return IsHostileToPlayer(s)
                && s.FearOfPlayer >= ALifeLegend.FEAR_WARY
                && s.FearOfPlayer < ALifeLegend.FEAR_FLEE
                && s.Archetype != SquadArchetype.HUNTERS; // beasts don't parley
        }

        /// <summary>Returns false (and leaves the squad in place) if there's nowhere to flee to.</summary>
        private static bool FleeFromPlayer(GameplayManager manager, VirtualSquad squad, Place fromTop)
        {
            Place dest = ALifeGraph.Neighbors(fromTop.uuid)
                .Where(p => p.uuid != fromTop.uuid)
                .OrderBy(_ => Rng.Next()).FirstOrDefault();
            if (dest == null) return false;

            string playerName = ALifeEmbodiment.SafePlayerName(manager);
            ALifeEmbodiment.RelocateVisibly(squad, dest); // player-visible ground: full native move
            squad.CurrentPlaceUuid = dest.uuid;
            squad.CurrentPlaceName = dest.GetPrettyName();
            squad.GoalType = SquadGoal.FLEE;
            squad.TargetPlaceUuid = null;
            squad.Activity = "fleeing from " + playerName;
            squad.AddChronicle($"Fled {fromTop.GetPrettyName()} rather than face {playerName}.");
            ALifeKnowledge.Learn(squad, met: false); // you know who fled, and where to
            ALifeData.LogEvent(fromTop.uuid, fromTop.GetPrettyName(), "LEGEND",
                $"{ALifeSimulation.Cap(squad.Name)} broke camp and fled toward {dest.GetPrettyName()} rather than face {playerName}.");
            try
            {
                manager.gameLogView.LogText(GameLogView.AiDecision(
                    $"Signs of a hasty departure: {squad.Name} fled this place at word of your coming."));
            }
            catch { /* log line is cosmetic */ }
            return true;
        }

        public static void Materialize(GameplayManager manager, VirtualSquad squad, Place at)
        {
            // An embodied band whose real members have all vanished WITHOUT dying — deleted
            // by the player, culled with a pruned place, lost to a stale save — is not a band
            // any more. Retire the record instead of resurrecting bodies the player got rid
            // of, or announcing a standoff with nobody in it. (Deaths go through
            // ALifeLegend.OnRealDeath, which decrements Size and wipes at zero.)
            if (squad.IsEmbodied && ALifeEmbodiment.LivingMembers(squad).Count == 0)
            {
                ALifeLifecycle.WipeSquad(squad, "scattered, and never seen again");
                ALifeData.SaveToCurrentDir();
                return;
            }

            bool hostile = IsHostileToPlayer(squad);
            bool wary = IsWaryOfPlayer(squad);
            int cap = Math.Min(squad.Size, hostile && !wary ? 4 : 3);
            string[] roles = ALifeNames.MemberRoles(squad.Archetype);
            Faction faction = squad.FactionUuid == null
                ? null
                : (manager.GetCurrentFactions() ?? new List<Faction>()).FirstOrDefault(f => f.uuid == squad.FactionUuid);

            // Wary squads spawn as NPCs even when hostile on paper: they hold back and
            // parley — the AI decides how the standoff goes (regard directive injected).
            var charType = hostile && !wary
                ? GameCharacter.CharacterType.NORMAL_MOB
                : GameCharacter.CharacterType.NPC;

            int alreadyHere = squad.IsEmbodied ? ALifeEmbodiment.LivingMembers(squad).Count : 0;
            int toSpawn = Math.Max(0, cap - alreadyHere);
            var spawnedNames = new List<string>();

            // The leader manifests first, by name, carrying the dossier.
            if (toSpawn > 0 && squad.Leader != null && squad.Leader.Uuid == null)
            {
                var lch = SpawnMember(manager, squad, at, squad.Leader.FullName,
                    ALifeNames.LeaderDesc(squad), charType, faction);
                if (lch != null)
                {
                    squad.Leader.Uuid = lch.uuid;
                    spawnedNames.Add(squad.Leader.FullName);
                    toSpawn--;
                }
            }
            for (int i = 0; i < toSpawn; i++)
            {
                string role = roles[Math.Min(i + 1, roles.Length - 1)];
                var ch = SpawnMember(manager, squad, at, ALifeNames.MemberName(squad, role),
                    ALifeNames.MemberDesc(squad, role), charType, faction);
                if (ch != null) spawnedNames.Add(ch.GetPrettyName());
            }

            // Grid places need an entity/grid sync so the newcomers get tiles.
            if (spawnedNames.Count > 0 && at.IsGrdStyle() && manager.currentPlace == at)
            {
                try
                {
                    _ = manager.gridManager.MaybeSyncEntitiesWithGrid(
                        $"{ALifeSimulation.Cap(squad.Name)} arrived in the area.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ALife] Grid sync after materialization failed: " + ex.Message);
                }
            }

            // Announce the band ONCE per meeting, and only when there is something real
            // to see. Re-entering the ground they're already standing on is not news, and
            // announcing a band that failed to spawn (or whose members the player has
            // since removed) puts a threat in the prompt that isn't in the room.
            Place topAt = at.GetTopLvlPlace();
            bool anyoneReal = spawnedNames.Count > 0 || alreadyHere > 0;
            bool newGround = !squad.MetPlayer || squad.LastAnnouncedPlaceUuid != topAt?.uuid;
            bool cooledDown = ALifeData.State.CurrentTurn - squad.LastAnnouncedTurn >= ANNOUNCE_COOLDOWN_TURNS;

            if (topAt != null && anyoneReal && (newGround || cooledDown))
            {
                string leaderNote = squad.Leader != null ? $", led by {squad.Leader.FullName}" : "";
                string encounterDesc =
                    wary ? $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}) is here — hostile, but they know your name and hold back, wary."
                    : hostile ? $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}, {squad.Activity}) is here — and hostile."
                    : $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}, {squad.Activity}) is passing through.";
                ALifeData.LogEvent(topAt.uuid, topAt.GetPrettyName(), "ENCOUNTER", encounterDesc);
                squad.LastAnnouncedTurn = ALifeData.State.CurrentTurn;
                squad.LastAnnouncedPlaceUuid = topAt.uuid;

                try
                {
                    manager.gameLogView.LogText(GameLogView.AiDecision(encounterDesc));
                }
                catch { /* log line is cosmetic */ }
            }

            // A squad that spawned nobody (every SpawnMember attempt threw — e.g. a
            // transient faction/entity lookup failure) is left virtual instead of marked
            // embodied: flipping IsEmbodied with an empty MemberUuids list would trip the
            // "no living members left" guard at the top of this method on the very next
            // visit and wipe a squad that never actually got its chance to appear.
            if (anyoneReal)
            {
                squad.MetPlayer = true;
                ALifeKnowledge.Learn(squad, met: true);

                if (ALifePlugin.CfgPersistentSquads.Value)
                {
                    // v2.0: the squad record lives on; reconciliation on departure.
                    squad.IsEmbodied = true;
                }
                else
                {
                    // v1.0 fallback: one-way handoff, the game owns the characters from here.
                    ALifeData.State.Squads.Remove(squad);
                }
            }
            else
            {
                Debug.LogWarning($"[ALife] Materialize for '{squad.Name}' spawned nobody — leaving it virtual for a later retry.");
            }
            ALifeData.SaveToCurrentDir();
            Debug.Log($"[ALife] Materialized {squad.Id} '{squad.Name}' (+{spawnedNames.Count} chars, {alreadyHere} already real) at {at.GetPrettyName()} (hostile={hostile}, wary={wary}).");
        }

        private static GameCharacter SpawnMember(GameplayManager manager, VirtualSquad squad, Place at,
            string name, string desc, GameCharacter.CharacterType charType, Faction faction)
        {
            try
            {
                // Sync ctor with an explicit desc = no AI call. powerLvl -1 → level scales
                // to the place, same as native spawns.
                var ch = new GameCharacter(at, name, desc, manager, charType, isMerchant: false,
                                           faction: faction, powerLvl: -1);
                InGameEntity ent = ch.ParentInGameEnt();
                if (ent != null)
                {
                    // Re-run the native placement path: idempotent for the place lists, and it
                    // triggers the map redraw / convo dropdown / spawn-button UI refreshes.
                    ent.SetAsChildOfPl(at);
                    if (at.IsGrdStyle())
                        ent.PopulateGrdInfo(at, null, Vector2Int.one);
                }
                squad.MemberUuids.Add(ch.uuid);
                return ch;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ALife] Spawn failed for '{name}': {ex.Message}");
                return null;
            }
        }
    }
}
