using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// v2.0 squad lifecycle: squads are careers, not spawns. They gain XP and level up,
    /// recruit on home ground, merge when mauled, split when swollen, and desert their
    /// faction when morale breaks. Leaders earn epithets, die, and are succeeded —
    /// and blood feuds between squads carry across all of it.
    /// </summary>
    public static class ALifeLifecycle
    {
        private static readonly System.Random Rng = new System.Random();

        public const int MAX_SQUAD_SIZE = 8;
        public const int SPLIT_SIZE = 9;       // merge overflow can reach this → split
        public const int MERGE_SIZE = 3;       // both squads at/below this may merge
        public const int MAX_LEVEL = 15;

        // ── Veterancy ───────────────────────────────────────────────────────────

        public static void AwardBattleXP(VirtualSquad squad, bool won, int enemiesKilled)
        {
            squad.XP += won ? 8 + enemiesKilled * 2 : 3;
            if (squad.Leader != null)
            {
                squad.Leader.Kills += enemiesKilled;
                if (won) squad.Leader.Victories++; else squad.Leader.Defeats++;
            }
            while (squad.XP >= 25 * Math.Max(1, squad.AvgLevel) && squad.AvgLevel < MAX_LEVEL)
            {
                squad.XP -= 25 * Math.Max(1, squad.AvgLevel);
                squad.AvgLevel++;
                squad.AddChronicle("The band grew hardened by battle (now level " + squad.AvgLevel + ").");
            }
            CheckEpithet(squad);
        }

        public static void CheckEpithet(VirtualSquad squad)
        {
            if (squad.Leader == null) return;
            string earned = ALifeNames.EpithetForKills(squad.Archetype, squad.Leader.Kills);
            if (earned != null && earned != squad.Leader.Epithet
                && squad.Leader.Epithet != "the Avenger" && squad.Leader.Epithet != "Who Walked Away")
            {
                string old = squad.Leader.FullName;
                squad.Leader.Epithet = earned;
                squad.AddChronicle($"{old} earned a new name: {squad.Leader.FullName}.");
                ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "LEGEND",
                    $"The {squad.Leader.Role} of {squad.Name} is now called {squad.Leader.FullName}.");
            }
        }

        // ── Succession ──────────────────────────────────────────────────────────

        /// <summary>The leader is gone (dead or departed). A new one rises. If a killer
        /// squad is named and the old leader died, the feud burns hotter and the heir
        /// takes the Avenger's name.</summary>
        public static void Succession(VirtualSquad squad, VirtualSquad killerSquad, bool leaderDead, string howLost)
        {
            if (squad.Size <= 0) return;
            SquadLeader old = squad.Leader;
            SquadLeader heir = ALifeNames.MakeLeader(squad);
            if (leaderDead && (killerSquad != null || squad.DeathsThisVisit > 0))
                heir.Epithet = "the Avenger";
            squad.Leader = heir;
            squad.Morale = Math.Max(10, squad.Morale - 15);

            if (killerSquad != null && leaderDead)
                AddFeud(squad, killerSquad, 40, $"the killing of {old?.FullName ?? "their leader"}");

            string oldName = old?.FullName ?? "Their leader";
            squad.AddChronicle($"{oldName} was {howLost}; {heir.FullName} now leads.");
            ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "LIFECYCLE",
                $"{oldName}, {old?.Role ?? "leader"} of {squad.Name}, was {howLost} — {heir.FullName} now leads" +
                (heir.Epithet == "the Avenger" ? ", sworn to vengeance." : "."));
        }

        // ── Feuds ───────────────────────────────────────────────────────────────

        public static void AddFeud(VirtualSquad squad, VirtualSquad enemy, int heat, string reason)
        {
            if (!ALifePlugin.CfgEnableFeuds.Value || squad == null || enemy == null || squad.Id == enemy.Id) return;
            var existing = squad.Feuds.FirstOrDefault(f => f.EnemySquadId == enemy.Id);
            if (existing != null)
            {
                existing.Heat = Math.Min(100, existing.Heat + heat);
                return;
            }
            if (squad.Feuds.Count >= 2) return; // a band can only hate so much
            squad.Feuds.Add(new FeudRecord
            {
                EnemySquadId = enemy.Id,
                EnemySquadName = enemy.Name,
                Heat = Math.Min(100, heat),
                Reason = reason,
                StartedTurn = ALifeData.State.CurrentTurn
            });
            if (heat >= 40)
                ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "FEUD",
                    $"{ALifeSimulation.Cap(squad.Name)} swore a blood feud against {enemy.Name} over {reason}.");
        }

        public static FeudRecord FeudBetween(VirtualSquad a, VirtualSquad b)
        {
            return a.Feuds.FirstOrDefault(f => f.EnemySquadId == b.Id)
                ?? b.Feuds.FirstOrDefault(f => f.EnemySquadId == a.Id);
        }

        /// <summary>Feud upkeep: heat cools with time; hot feuds send squads hunting.</summary>
        public static void FeudTick(GameplayManager manager, int numTurns, string playerTopUuid)
        {
            if (!ALifePlugin.CfgEnableFeuds.Value) return;
            foreach (var squad in ALifeData.State.Squads)
            {
                foreach (var feud in squad.Feuds.ToList())
                {
                    feud.Heat -= numTurns;
                    var enemy = ALifeData.SquadById(feud.EnemySquadId);
                    if (enemy == null)
                    {
                        squad.Feuds.Remove(feud);
                        continue;
                    }
                    if (feud.Heat <= 0)
                    {
                        squad.Feuds.Remove(feud);
                        squad.AddChronicle($"The feud with {feud.EnemySquadName} cooled and was let lie.");
                        continue;
                    }
                    // Hot feud + fighting shape → go hunting.
                    if (feud.Heat >= 50 && squad.Morale >= 50 && squad.GoalType != SquadGoal.HUNT
                        && squad.GoalType != SquadGoal.FLEE
                        && squad.CurrentPlaceUuid != playerTopUuid
                        && Rng.NextDouble() < 0.30)
                    {
                        squad.GoalType = SquadGoal.HUNT;
                        squad.TargetSquadId = enemy.Id;
                        squad.TargetPlaceUuid = enemy.CurrentPlaceUuid;
                        squad.TargetPlaceName = enemy.CurrentPlaceName;
                        squad.Activity = ALifeNames.ActivityLine(squad);
                        ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "FEUD",
                            $"{ALifeSimulation.Cap(squad.Name)} set out hunting {enemy.Name}, sworn enemies since {feud.Reason}.");
                    }
                }
            }
        }

        // ── Death of a squad ────────────────────────────────────────────────────

        public static void WipeSquad(VirtualSquad squad, string how)
        {
            if (!ALifeData.State.Squads.Contains(squad)) return; // idempotent
            ALifeData.State.Squads.Remove(squad);
            ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "WIPE",
                $"{ALifeSimulation.Cap(squad.Name)} is no more — {how}.");
            // Settle every feud pointed at the fallen band.
            foreach (var other in ALifeData.State.Squads)
            {
                var feud = other.Feuds.FirstOrDefault(f => f.EnemySquadId == squad.Id);
                if (feud != null)
                {
                    other.Feuds.Remove(feud);
                    other.AddChronicle($"The blood feud with {squad.Name} ended with their destruction.");
                    ALifeData.LogEvent(other.CurrentPlaceUuid, other.CurrentPlaceName, "FEUD",
                        $"With the destruction of {squad.Name}, the blood feud against them is settled.");
                }
            }
        }

        // ── Lifecycle tick (recruit / merge / split / defect) ───────────────────

        public static void Tick(GameplayManager manager, int numTurns, string playerTopUuid)
        {
            if (!ALifePlugin.CfgEnableLifecycle.Value) return;
            var state = ALifeData.State;

            foreach (var squad in state.Squads.ToList())
            {
                if (squad.CurrentPlaceUuid == playerTopUuid) continue; // frozen in the bubble

                // Recruiting: home ground or owned territory refills the ranks.
                if (squad.Size < 6 && state.CurrentTurn - squad.LastRecruitTurn >= 6
                    && Rng.NextDouble() < 0.30 && OnFriendlyGround(squad))
                {
                    squad.Size++;
                    squad.LastRecruitTurn = state.CurrentTurn;
                    squad.Morale = Math.Min(100, squad.Morale + 5);
                    // note: recruits are virtual until next materialization spawns them
                }

                // Defection: broken faction squads go renegade.
                if (squad.FactionUuid != null && squad.Morale < 25 && Rng.NextDouble() < 0.15)
                {
                    string oldName = squad.Name;
                    squad.Archetype = SquadArchetype.RAIDERS;
                    squad.FactionUuid = null;
                    string oldFaction = squad.FactionName;
                    squad.FactionName = null;
                    squad.Name = "renegades of the " + (oldFaction ?? "old order");
                    squad.Morale = 60; // nothing left to lose is its own morale
                    squad.AddChronicle($"Deserted {oldFaction} and turned renegade.");
                    ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "LIFECYCLE",
                        $"{ALifeSimulation.Cap(oldName)} broke faith with {oldFaction} and turned renegade.");
                    ALifeWorldBridge.PushWorldEvent(
                        $"{ALifeSimulation.Cap(oldName)} deserted {oldFaction} and now roams as renegades.", alertPlayer: false);
                    ALifeSimulation.AssignGoal(manager, squad);
                }

                // Splitting: a swollen band spawns a splinter under a new leader.
                if (squad.Size >= SPLIT_SIZE)
                {
                    int splinterSize = Math.Max(2, (int)(squad.Size * 0.4));
                    squad.Size -= splinterSize;
                    var splinter = new VirtualSquad
                    {
                        Id = "sq_" + state.NextSquadNum++,
                        Archetype = squad.Archetype,
                        FactionUuid = squad.FactionUuid,
                        FactionName = squad.FactionName,
                        Size = splinterSize,
                        AvgLevel = squad.AvgLevel,
                        CurrentPlaceUuid = squad.CurrentPlaceUuid,
                        CurrentPlaceName = squad.CurrentPlaceName,
                        HomePlaceUuid = squad.HomePlaceUuid,
                        SpawnedTurn = state.CurrentTurn,
                        TurnsPerHop = squad.TurnsPerHop,
                        FearOfPlayer = squad.FearOfPlayer,
                        AweOfPlayer = squad.AweOfPlayer
                    };
                    splinter.Name = ALifeNames.SquadName(splinter.Archetype, splinter.FactionName,
                        splinter.CurrentPlaceName ?? "the wilds");
                    splinter.Leader = ALifeNames.MakeLeader(splinter);
                    splinter.AddChronicle($"Split away from {squad.Name} under {splinter.Leader.FullName}.");
                    squad.AddChronicle($"{splinter.Leader.FullName} led {splinterSize} away to form {splinter.Name}.");
                    state.Squads.Add(splinter);
                    ALifeSimulation.AssignGoal(manager, splinter);
                    ALifeData.LogEvent(squad.CurrentPlaceUuid, squad.CurrentPlaceName, "LIFECYCLE",
                        $"{ALifeSimulation.Cap(squad.Name)} grew too large — {splinter.Leader.FullName} led {splinterSize} away as {splinter.Name}.");
                }
            }

            // Merging: two mauled squads of the same allegiance in the same place join up.
            var byPlace = state.Squads
                .Where(s => s.CurrentPlaceUuid != playerTopUuid && s.Size <= MERGE_SIZE && s.FactionUuid != null)
                .GroupBy(s => s.CurrentPlaceUuid);
            foreach (var grp in byPlace.ToList())
            {
                var squads = grp.ToList();
                for (int i = 0; i < squads.Count; i++)
                    for (int j = i + 1; j < squads.Count; j++)
                    {
                        var a = squads[i]; var b = squads[j];
                        if (a.FactionUuid != b.FactionUuid || a.Archetype != b.Archetype) continue;
                        if (!state.Squads.Contains(a) || !state.Squads.Contains(b)) continue;
                        var keep = (a.Leader?.Kills ?? 0) >= (b.Leader?.Kills ?? 0) ? a : b;
                        var fold = keep == a ? b : a;
                        keep.Size = Math.Min(MAX_SQUAD_SIZE, keep.Size + fold.Size);
                        keep.Morale = Math.Min(100, keep.Morale + 15);
                        keep.MemberUuids.AddRange(fold.MemberUuids.Where(u => !keep.MemberUuids.Contains(u)));
                        keep.IsEmbodied = keep.IsEmbodied || fold.IsEmbodied;
                        foreach (var feud in fold.Feuds)
                            if (keep.Feuds.Count < 2 && keep.Feuds.All(f => f.EnemySquadId != feud.EnemySquadId))
                                keep.Feuds.Add(feud);
                        keep.AddChronicle($"The remnants of {fold.Name} under {fold.Leader?.FullName ?? "their leader"} folded into the band.");
                        state.Squads.Remove(fold);
                        ALifeData.LogEvent(keep.CurrentPlaceUuid, keep.CurrentPlaceName, "LIFECYCLE",
                            $"The battered remnants of {fold.Name} joined {keep.Name} under {keep.Leader?.FullName ?? "one banner"}.");
                    }
            }
        }

        private static bool OnFriendlyGround(VirtualSquad squad)
        {
            if (squad.CurrentPlaceUuid == squad.HomePlaceUuid) return true;
            if (squad.FactionUuid == null) return false;
            Place here = ALifeGraph.PlaceByUuid(squad.CurrentPlaceUuid);
            if (here?.faction != null && here.faction.uuid == squad.FactionUuid) return true;
            return ALifeWorldBridge.GetClaimedPlaces(squad.FactionUuid).Contains(squad.CurrentPlaceUuid);
        }
    }
}
