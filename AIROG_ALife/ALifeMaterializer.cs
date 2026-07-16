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
                ALifeData.SaveToCurrentDir();
            }

            if (!ALifePlugin.CfgMaterialize.Value) return;

            var squadsHere = ALifeData.State.Squads.Where(s => s.CurrentPlaceUuid == top.uuid).ToList();
            if (squadsHere.Count == 0) return;

            // The Wake: squads that fear the player refuse the meeting and bolt.
            foreach (var scared in squadsHere.ToList())
            {
                if (scared.FearOfPlayer >= ALifeLegend.FEAR_FLEE)
                {
                    FleeFromPlayer(manager, scared, top);
                    squadsHere.Remove(scared);
                }
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
                // deterministic per-squad so repeated checks agree
                case SquadArchetype.RAIDERS: return (Math.Abs((s.Id ?? "").GetHashCode()) % 10) < 6;
                case SquadArchetype.WARBAND:
                case SquadArchetype.PATROL:
                    return ALifeWorldBridge.PlayerHasBountyFrom(s.FactionUuid);
                default: return false;
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

        private static void FleeFromPlayer(GameplayManager manager, VirtualSquad squad, Place fromTop)
        {
            Place dest = ALifeGraph.Neighbors(fromTop.uuid)
                .Where(p => p.uuid != fromTop.uuid)
                .OrderBy(_ => Rng.Next()).FirstOrDefault();
            if (dest == null) return;

            string playerName = ALifeEmbodiment.SafePlayerName(manager);
            if (squad.IsEmbodied)
            {
                // Player-visible place: use the FULL native relocation (UI refreshes included).
                foreach (var ch in ALifeEmbodiment.LivingMembers(squad))
                {
                    try
                    {
                        InGameEntity ent = ch.ParentInGameEnt();
                        if (ent == null) continue;
                        ent.SetAsChildOfPl(dest);
                        if (dest.IsGrdStyle())
                            ent.PopulateGrdInfo(dest, null, Vector2Int.one);
                        ch.parentPlace = dest;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[ALife] Flee move failed for " + ch.GetPrettyName() + ": " + ex.Message);
                    }
                }
            }
            squad.CurrentPlaceUuid = dest.uuid;
            squad.CurrentPlaceName = dest.GetPrettyName();
            squad.GoalType = SquadGoal.FLEE;
            squad.TargetPlaceUuid = null;
            squad.Activity = "fleeing from " + playerName;
            squad.AddChronicle($"Fled {fromTop.GetPrettyName()} rather than face {playerName}.");
            ALifeData.LogEvent(fromTop.uuid, fromTop.GetPrettyName(), "LEGEND",
                $"{ALifeSimulation.Cap(squad.Name)} broke camp and fled toward {dest.GetPrettyName()} rather than face {playerName}.");
            try
            {
                manager.gameLogView.LogText(GameLogView.AiDecision(
                    $"Signs of a hasty departure: {squad.Name} fled this place at word of your coming."));
            }
            catch { /* log line is cosmetic */ }
        }

        public static void Materialize(GameplayManager manager, VirtualSquad squad, Place at)
        {
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

            string leaderNote = squad.Leader != null ? $", led by {squad.Leader.FullName}" : "";
            string encounterDesc =
                wary ? $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}) is here — hostile, but they know your name and hold back, wary."
                : hostile ? $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}, {squad.Activity}) is here — and hostile."
                : $"{ALifeSimulation.Cap(squad.Name)} ({squad.Size} strong{leaderNote}, {squad.Activity}) is passing through.";
            ALifeData.LogEvent(at.GetTopLvlPlace().uuid, at.GetTopLvlPlace().GetPrettyName(), "ENCOUNTER", encounterDesc);

            try
            {
                manager.gameLogView.LogText(GameLogView.AiDecision(encounterDesc));
            }
            catch { /* log line is cosmetic */ }

            squad.MetPlayer = true;
            squad.DeathsThisVisit = 0;

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
