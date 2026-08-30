using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.0 embodiment: once a squad has materialized, its members are REAL
    /// GameCharacters forever — the squad record tracks their uuids, moves the real
    /// entities across the world offline, and applies offline battle casualties as
    /// real corpses (which the game decays and culls natively). This replaces the
    /// v1.0 one-way handoff: kill three of five raiders and flee, and that squad
    /// limps on with two members — and remembers.
    /// </summary>
    public static class ALifeEmbodiment
    {
        /// <summary>Set while the sim itself kills members, so the Legend kill-tracking
        /// SetAsCorpse postfix doesn't attribute offline battle deaths to the player.</summary>
        public static bool ApplyingOfflineCasualties;

        // ── Lookup ───────────────────────────────────────────────────────────────

        public static GameCharacter ResolveMember(string uuid)
        {
            if (string.IsNullOrEmpty(uuid) || SS.I?.uuidToGameEntityMap == null) return null;
            SS.I.uuidToGameEntityMap.TryGetValue(uuid, out GameEntity ge);
            return ge as GameCharacter;
        }

        public static List<GameCharacter> LivingMembers(VirtualSquad squad)
        {
            var living = new List<GameCharacter>();
            if (squad?.MemberUuids == null) return living;
            foreach (string uuid in squad.MemberUuids)
            {
                var ch = ResolveMember(uuid);
                if (ch != null && ch.corpseState == GameCharacter.CorpseState.NONE && !ch.IsFollower())
                    living.Add(ch);
            }
            return living;
        }

        /// <summary>Drop uuids that no longer resolve (culled corpses, pruned places, stale saves).
        /// Silent — no fear/legend attribution, that's only for witnessed deaths.</summary>
        public static void Sanitize(VirtualSquad squad)
        {
            if (!squad.IsEmbodied || squad.MemberUuids.Count == 0) return;
            squad.MemberUuids.RemoveAll(uuid =>
            {
                var ch = ResolveMember(uuid);
                return ch == null || ch.corpseState != GameCharacter.CorpseState.NONE;
            });
            if (squad.Leader != null && squad.Leader.Uuid != null && !squad.MemberUuids.Contains(squad.Leader.Uuid))
                squad.Leader.Uuid = null;
        }

        // ── Offline movement ─────────────────────────────────────────────────────

        /// <summary>
        /// Move a squad's real members to a new place using the same core steps as the
        /// native SetAsChildOfPl (remove from old place lists → reparent → add to new),
        /// but WITHOUT its four per-call UI refreshes — embodied moves only ever happen
        /// between places the player can't see, so no UI needs to change.
        /// </summary>
        public static void MoveEmbodied(VirtualSquad squad, Place dest)
        {
            if (!squad.IsEmbodied || dest == null) return;
            foreach (string uuid in squad.MemberUuids.ToList())
            {
                try
                {
                    var ch = ResolveMember(uuid);
                    if (ch == null || ch.corpseState != GameCharacter.CorpseState.NONE || ch.IsFollower())
                        continue;
                    InGameEntity ent = ch.ParentInGameEnt();
                    if (ent == null) continue;
                    ent.ParentPl()?.RemoveInGameEnt(ent);
                    ent.SetParentEnt(dest);
                    dest.AddInGameEnt(ent);
                    if (dest.IsGrdStyle())
                        ent.PopulateGrdInfo(dest, null, Vector2Int.one);
                    ch.parentPlace = dest;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ALife] Embodied move failed for member {uuid} of {squad.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Move a squad's real members off ground the player can SEE: the full native
        /// relocation, UI refreshes included. MoveEmbodied is the silent counterpart for
        /// places the player isn't standing in — use this one inside the online bubble.
        /// </summary>
        public static void RelocateVisibly(VirtualSquad squad, Place dest)
        {
            if (!squad.IsEmbodied || dest == null) return;
            foreach (var ch in LivingMembers(squad))
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
                    Debug.LogWarning("[ALife] Visible relocation failed for " + ch.GetPrettyName() + ": " + ex.Message);
                }
            }
        }

        // ── Offline casualties ───────────────────────────────────────────────────

        /// <summary>
        /// Kill up to <paramref name="count"/> real members offline (battle losses).
        /// Non-leaders die first; the leader only dies if <paramref name="leaderDies"/>.
        /// Real corpses are left where the squad stands — the player can find them.
        /// Returns how many real entities were actually killed.
        /// </summary>
        public static int ApplyOfflineCasualties(VirtualSquad squad, int count, bool leaderDies)
        {
            if (!squad.IsEmbodied || count <= 0) return 0;
            int killed = 0;
            ApplyingOfflineCasualties = true;
            try
            {
                var living = LivingMembers(squad);
                var ordered = living
                    .OrderBy(ch => ch.uuid == squad.Leader?.Uuid ? 1 : 0) // leader last
                    .ToList();
                foreach (var ch in ordered)
                {
                    if (killed >= count) break;
                    bool isLeader = ch.uuid == squad.Leader?.Uuid;
                    if (isLeader && !leaderDies) continue;
                    try
                    {
                        ch.SetAsCorpse();
                        squad.MemberUuids.Remove(ch.uuid);
                        if (isLeader) squad.Leader.Uuid = null;
                        killed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ALife] Offline casualty failed for {ch.GetPrettyName()}: {ex.Message}");
                    }
                }
            }
            finally
            {
                ApplyingOfflineCasualties = false;
            }
            return killed;
        }

        // ── Departure reconciliation ─────────────────────────────────────────────

        /// <summary>
        /// The player just left a top-level place. Settle accounts with every embodied
        /// squad that shared it: members recruited away become defections (and earn the
        /// squad's awe), a bloodless visit builds respect, an emptied squad is wiped,
        /// and survivors unfreeze and resume their goals.
        /// (Deaths themselves are tracked live by ALifeLegend's SetAsCorpse postfix.)
        /// </summary>
        public static void ReconcileDeparture(GameplayManager manager, string prevTopUuid)
        {
            if (string.IsNullOrEmpty(prevTopUuid)) return;
            string playerName = SafePlayerName(manager);

            foreach (var squad in ALifeData.State.Squads.ToList())
            {
                if (squad.CurrentPlaceUuid != prevTopUuid || !squad.MetPlayer) continue;

                // Defections: members the player recruited as followers leave the squad.
                if (squad.IsEmbodied)
                {
                    foreach (string uuid in squad.MemberUuids.ToList())
                    {
                        var ch = ResolveMember(uuid);
                        if (ch != null && ch.IsFollower())
                        {
                            squad.MemberUuids.Remove(uuid);
                            squad.Size = Math.Max(0, squad.Size - 1);
                            squad.AweOfPlayer = Math.Min(100, squad.AweOfPlayer + 20);
                            if (uuid == squad.Leader?.Uuid)
                            {
                                squad.Leader.Uuid = null;
                                ALifeLifecycle.Succession(squad, null, leaderDead: false,
                                    $"won over, leaving to walk beside {playerName}");
                            }
                            squad.AddChronicle($"{ch.GetPrettyName()} left the band to follow {playerName}.");
                            ALifeData.LogEvent(prevTopUuid, squad.CurrentPlaceName, "LIFECYCLE",
                                $"{ch.GetPrettyName()} abandoned {squad.Name} to follow {playerName}.");
                        }
                    }
                    Sanitize(squad);
                }

                if (squad.Size <= 0)
                {
                    ALifeLifecycle.WipeSquad(squad, $"destroyed in an encounter with {playerName}");
                    continue;
                }

                // A visit with no bloodshed earns quiet respect.
                if (squad.DeathsThisVisit == 0)
                {
                    squad.AweOfPlayer = Math.Min(100, squad.AweOfPlayer + 10);
                    squad.FearOfPlayer = Math.Max(0, squad.FearOfPlayer - 5);
                    squad.AddChronicle($"Shared ground with {playerName} in peace.");
                }
                squad.DeathsThisVisit = 0;

                // Unfreeze: pick the squad's life back up.
                ALifeSimulation.AssignGoal(manager, squad);
            }
        }

        public static string SafePlayerName(GameplayManager manager)
        {
            try
            {
                string n = manager?.playerCharacter?.name;
                return string.IsNullOrEmpty(n) ? "the stranger" : n;
            }
            catch { return "the stranger"; }
        }
    }
}
