using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace AIROG_ALife
{
    /// <summary>
    /// The offline A-Life tick. Every game turn: seed the world toward its target squad
    /// population, run the lifecycle (recruit/merge/split/defect) and feud upkeep, move
    /// squads one graph-hop at a time toward their goals (moving their REAL members if
    /// embodied), resolve battles between hostile squads sharing a place, occasionally
    /// migrate a real named NPC, and persist. The player's current top-level place is
    /// the "online bubble": squads there are frozen (the real game owns it).
    /// </summary>
    public static class ALifeSimulation
    {
        private static readonly System.Random Rng = new System.Random();

        [HarmonyPatch(typeof(GameplayManager), "InvokeTurnHappened")]
        public static class Patch_TurnHappened
        {
            [HarmonyPostfix]
            public static void Postfix(GameplayManager __instance, int numTurns, long secs)
            {
                try
                {
                    OnTurn(__instance, numTurns);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[ALife] Turn tick failed: " + ex);
                }
            }
        }

        public static void OnTurn(GameplayManager manager, int numTurns)
        {
            if (!ALifePlugin.CfgEnableSim.Value) return;
            if (manager == null || manager.currentPlace == null) return;

            var state = ALifeData.State;
            state.CurrentTurn += numTurns;

            ALifeGraph.EnsureBuilt(manager);
            List<Place> topPlaces = ALifeGraph.GetTopPlaces(manager);
            if (topPlaces.Count < 2) return;

            string playerTopUuid = manager.currentPlace.GetTopLvlPlace()?.uuid;
            if (state.LastPlayerTopUuid == null) state.LastPlayerTopUuid = playerTopUuid;

            foreach (var s in state.Squads) ALifeEmbodiment.Sanitize(s);
            PruneInvalidSquads(topPlaces);
            ALifeLegend.DecayTick(numTurns);
            SeedSquads(manager, topPlaces, playerTopUuid);
            ALifeLifecycle.Tick(manager, numTurns, playerTopUuid);
            ALifeLifecycle.FeudTick(manager, numTurns, playerTopUuid);
            MoveSquads(manager, playerTopUuid, numTurns);
            ResolveEncounters(manager, playerTopUuid);
            MaybeMigrateNamedNpc(manager, topPlaces, playerTopUuid);

            ALifeData.SaveToCurrentDir();
        }

        // ── Population ──────────────────────────────────────────────────────────

        private static void PruneInvalidSquads(List<Place> topPlaces)
        {
            var valid = new HashSet<string>(topPlaces.Select(p => p.uuid));
            ALifeData.State.Squads.RemoveAll(s => !valid.Contains(s.CurrentPlaceUuid));
        }

        private static int TargetPopulation(int placeCount, int warCount)
        {
            int configured = ALifePlugin.CfgMaxSquads.Value;
            int auto = Mathf.Clamp(placeCount / 3, 3, 10) + warCount;
            return configured > 0 ? Math.Min(configured, auto + warCount) : auto;
        }

        private static void SeedSquads(GameplayManager manager, List<Place> topPlaces, string playerTopUuid)
        {
            var state = ALifeData.State;
            var wars = ALifeWorldBridge.GetActiveWars();
            if (state.Squads.Count >= TargetPopulation(topPlaces.Count, wars.Count)) return;
            if (Rng.NextDouble() > 0.6) return; // stagger spawns: at most ~0.6/turn

            List<Faction> factions = manager.GetCurrentFactions() ?? new List<Faction>();
            factions = factions.Where(f => f != null && f.GetPrettyName() != "Player").ToList();

            // Pick an archetype. Wars bias toward warbands; no factions → wild packs only.
            string archetype;
            Faction faction = null;
            double roll = Rng.NextDouble();
            if (factions.Count == 0)
                archetype = SquadArchetype.HUNTERS;
            else if (wars.Count > 0 && roll < 0.35)
            {
                archetype = SquadArchetype.WARBAND;
                var warFacUuids = wars.SelectMany(w => new[] { w.ActorUuid, w.TargetUuid }).Distinct().ToList();
                faction = factions.FirstOrDefault(f => warFacUuids.Contains(f.uuid))
                          ?? factions[Rng.Next(factions.Count)];
            }
            else if (roll < 0.30) archetype = SquadArchetype.HUNTERS;
            else if (roll < 0.45) archetype = SquadArchetype.RAIDERS;
            else if (roll < 0.60) archetype = SquadArchetype.CARAVAN;
            else if (roll < 0.70) archetype = SquadArchetype.PILGRIMS;
            else archetype = SquadArchetype.PATROL;

            bool factionBound = archetype == SquadArchetype.PATROL || archetype == SquadArchetype.WARBAND
                             || archetype == SquadArchetype.CARAVAN;
            if (factionBound && faction == null)
            {
                if (factions.Count == 0) { archetype = SquadArchetype.HUNTERS; factionBound = false; }
                else faction = factions[Rng.Next(factions.Count)];
            }

            // Spawn location: faction squads start on owned ground (native ownership first,
            // then WorldExpansion claims); wild packs prefer dangerous/unowned places.
            Place home = PickSpawnPlace(topPlaces, playerTopUuid, faction, archetype);
            if (home == null) return;

            var squad = new VirtualSquad
            {
                Id = "sq_" + state.NextSquadNum++,
                Archetype = archetype,
                FactionUuid = faction?.uuid,
                FactionName = faction?.GetPrettyName(),
                Size = Rng.Next(2, 6),
                AvgLevel = Math.Max(1, home.GetAreaLvlAfterScaling() + Rng.Next(-1, 2)),
                CurrentPlaceUuid = home.uuid,
                CurrentPlaceName = home.GetPrettyName(),
                HomePlaceUuid = home.uuid,
                SpawnedTurn = state.CurrentTurn,
                TurnsPerHop = archetype == SquadArchetype.HUNTERS ? 4
                            : archetype == SquadArchetype.CARAVAN ? 2 : 3
            };
            squad.Name = ALifeNames.SquadName(archetype, squad.FactionName, home.GetPrettyName());
            squad.Leader = ALifeNames.MakeLeader(squad);
            squad.AddChronicle($"Formed at {home.GetPrettyName()} under {squad.Leader.FullName}.");
            AssignGoal(manager, squad);
            state.Squads.Add(squad);
            ALifeData.LogEvent(home.uuid, home.GetPrettyName(), "SPAWN",
                $"{Cap(squad.Name)} ({squad.Size} strong, led by {squad.Leader.FullName}) set out from {home.GetPrettyName()}.");
        }

        private static Place PickSpawnPlace(List<Place> topPlaces, string playerTopUuid, Faction faction, string archetype)
        {
            var candidates = topPlaces.Where(p => p.uuid != playerTopUuid).ToList();
            if (candidates.Count == 0) return null;

            if (faction != null)
            {
                var owned = candidates.Where(p => p.faction == faction).ToList();
                if (owned.Count == 0)
                {
                    var claimed = new HashSet<string>(ALifeWorldBridge.GetClaimedPlaces(faction.uuid));
                    owned = candidates.Where(p => claimed.Contains(p.uuid)).ToList();
                }
                if (owned.Count > 0) return owned[Rng.Next(owned.Count)];
            }
            if (archetype == SquadArchetype.HUNTERS)
            {
                var wild = candidates.Where(p => p.faction == null).ToList();
                if (wild.Count > 0)
                    return wild.OrderByDescending(p => p.GetAreaLvlAfterScaling() + Rng.Next(0, 4)).First();
            }
            return candidates[Rng.Next(candidates.Count)];
        }

        // ── Goals & movement ────────────────────────────────────────────────────

        public static void AssignGoal(GameplayManager manager, VirtualSquad s)
        {
            s.TargetSquadId = null;
            Place cur = ALifeGraph.PlaceByUuid(s.CurrentPlaceUuid);
            if (cur == null) return;
            List<Place> topPlaces = ALifeGraph.GetTopPlaces(manager);

            switch (s.Archetype)
            {
                case SquadArchetype.PATROL:
                {
                    var own = OwnedPlaces(topPlaces, s.FactionUuid);
                    Place dest = PreferCalm(s, own.Where(p => p.uuid != s.CurrentPlaceUuid))
                                 ?? SafeNeighbor(s);
                    SetTravel(s, SquadGoal.TRAVEL_TO, dest);
                    break;
                }
                case SquadArchetype.WARBAND:
                {
                    var enemies = ALifeWorldBridge.WarEnemiesOf(s.FactionUuid);
                    var enemyGround = topPlaces.Where(p =>
                        (p.faction != null && enemies.Contains(p.faction.uuid)) ||
                        enemies.Any(e => ALifeWorldBridge.GetClaimedPlaces(e).Contains(p.uuid))).ToList();
                    if (enemyGround.Count > 0)
                        SetTravel(s, SquadGoal.RAID, enemyGround[Rng.Next(enemyGround.Count)]);
                    else
                        SetTravel(s, SquadGoal.TRAVEL_TO, SafeNeighbor(s));
                    break;
                }
                case SquadArchetype.RAIDERS:
                {
                    var marks = topPlaces.Where(p => p.uuid != s.CurrentPlaceUuid && p.faction != null).ToList();
                    if (marks.Count > 0 && Rng.NextDouble() < 0.5)
                        SetTravel(s, SquadGoal.RAID, marks[Rng.Next(marks.Count)]);
                    else
                        SetTravel(s, SquadGoal.WANDER, SafeNeighbor(s));
                    break;
                }
                case SquadArchetype.CARAVAN:
                {
                    // Shuttle: pick/flip route endpoints.
                    if (s.TradeHomeUuid == null) s.TradeHomeUuid = s.CurrentPlaceUuid;
                    var stops = topPlaces.Where(p => p.uuid != s.CurrentPlaceUuid && p.faction != null).ToList();
                    Place dest = s.CurrentPlaceUuid == s.TradeHomeUuid && stops.Count > 0
                        ? (PreferCalm(s, stops) ?? stops[Rng.Next(stops.Count)])
                        : ALifeGraph.PlaceByUuid(s.TradeHomeUuid);
                    SetTravel(s, SquadGoal.TRADE, dest ?? SafeNeighbor(s));
                    break;
                }
                case SquadArchetype.HUNTERS:
                {
                    var n = ALifeGraph.Neighbors(s.CurrentPlaceUuid);
                    Place dest = n.OrderByDescending(p => p.GetAreaLvlAfterScaling() + Rng.Next(0, 5)).FirstOrDefault();
                    SetTravel(s, SquadGoal.WANDER, dest);
                    break;
                }
                default:
                    SetTravel(s, SquadGoal.WANDER, SafeNeighbor(s));
                    break;
            }
            s.Activity = ALifeNames.ActivityLine(s);
        }

        /// <summary>Random neighbor, preferring ones the squad isn't afraid to enter (dread zones).</summary>
        private static Place SafeNeighbor(VirtualSquad s)
        {
            var neighbors = ALifeGraph.Neighbors(s.CurrentPlaceUuid);
            var calm = neighbors.Where(p => !ALifeLegend.AvoidsPlace(s, p.uuid)).ToList();
            var pool = calm.Count > 0 ? calm : neighbors;
            return pool.Count > 0 ? pool[Rng.Next(pool.Count)] : null;
        }

        /// <summary>First choice among candidates that the squad doesn't dread; null if none.</summary>
        private static Place PreferCalm(VirtualSquad s, IEnumerable<Place> candidates)
        {
            var list = candidates.ToList();
            var calm = list.Where(p => !ALifeLegend.AvoidsPlace(s, p.uuid)).ToList();
            var pool = calm.Count > 0 ? calm : list;
            return pool.Count > 0 ? pool[Rng.Next(pool.Count)] : null;
        }

        private static List<Place> OwnedPlaces(List<Place> topPlaces, string factionUuid)
        {
            if (string.IsNullOrEmpty(factionUuid)) return new List<Place>();
            var claimed = new HashSet<string>(ALifeWorldBridge.GetClaimedPlaces(factionUuid));
            return topPlaces.Where(p => (p.faction != null && p.faction.uuid == factionUuid) || claimed.Contains(p.uuid)).ToList();
        }

        private static void SetTravel(VirtualSquad s, string goal, Place dest)
        {
            s.GoalType = goal;
            s.TargetPlaceUuid = dest?.uuid;
            s.TargetPlaceName = dest?.GetPrettyName();
        }

        private static void MoveSquads(GameplayManager manager, string playerTopUuid, int numTurns)
        {
            foreach (VirtualSquad s in ALifeData.State.Squads.ToList())
            {
                if (s.CurrentPlaceUuid == playerTopUuid) continue; // online bubble: frozen

                // HUNT tracks a moving target: retarget to the enemy's live position.
                if (s.GoalType == SquadGoal.HUNT)
                {
                    var quarry = ALifeData.SquadById(s.TargetSquadId);
                    if (quarry == null) { AssignGoal(manager, s); }
                    else
                    {
                        s.TargetPlaceUuid = quarry.CurrentPlaceUuid;
                        s.TargetPlaceName = quarry.CurrentPlaceName;
                    }
                }

                s.HopProgress += numTurns;
                if (s.HopProgress < s.TurnsPerHop) continue;
                s.HopProgress = 0;

                if (s.TargetPlaceUuid == null || s.TargetPlaceUuid == s.CurrentPlaceUuid)
                {
                    OnArrived(manager, s);
                    continue;
                }

                Place next = ALifeGraph.NextHopToward(s.CurrentPlaceUuid, s.TargetPlaceUuid);
                if (next == null) { AssignGoal(manager, s); continue; }

                // Never walk into the player's bubble mid-goal, and balk at killing grounds.
                if (next.uuid == playerTopUuid && s.FearOfPlayer >= ALifeLegend.FEAR_FLEE)
                    { AssignGoal(manager, s); continue; }
                if (ALifeLegend.AvoidsPlace(s, next.uuid) && next.uuid != s.TargetPlaceUuid)
                {
                    s.Activity = "waiting, unwilling to cross " + next.GetPrettyName();
                    continue; // dread decays; they'll move eventually or re-plan on arrival timeout
                }

                MoveSquadTo(s, next);
                if (next.uuid == s.TargetPlaceUuid)
                    OnArrived(manager, s);
            }
        }

        /// <summary>Move the squad record — and, if embodied, its real members — one hop.</summary>
        public static void MoveSquadTo(VirtualSquad s, Place next)
        {
            s.CurrentPlaceUuid = next.uuid;
            s.CurrentPlaceName = next.GetPrettyName();
            if (s.IsEmbodied)
                ALifeEmbodiment.MoveEmbodied(s, next);
        }

        private static void OnArrived(GameplayManager manager, VirtualSquad s)
        {
            Place here = ALifeGraph.PlaceByUuid(s.CurrentPlaceUuid);
            if (here == null) { AssignGoal(manager, s); return; }

            if (s.GoalType == SquadGoal.RAID && here.uuid == s.TargetPlaceUuid)
            {
                // No defenders resolved by the encounter step? Then the raid lands.
                string victim = here.faction != null ? here.faction.GetPrettyName() : "the locals";
                ALifeData.LogEvent(here.uuid, here.GetPrettyName(), "RAID",
                    $"{Cap(s.Name)} raided the outskirts of {here.GetPrettyName()}, striking at {victim} before withdrawing.");
                if (s.Archetype == SquadArchetype.WARBAND)
                    ALifeWorldBridge.PushWorldEvent(
                        $"{Cap(s.Name)} raided {here.GetPrettyName()}.", alertPlayer: false);
                s.LastEventTurn = ALifeData.State.CurrentTurn;
                s.Morale = Math.Min(100, s.Morale + 10);
                s.XP += 5;
                if (s.Leader != null) s.Leader.Kills += 1; // raids draw blood
                ALifeLifecycle.CheckEpithet(s);
            }
            AssignGoal(manager, s);
        }

        // ── Encounters & battles ────────────────────────────────────────────────

        private static void ResolveEncounters(GameplayManager manager, string playerTopUuid)
        {
            var byPlace = ALifeData.State.Squads
                .Where(s => s.CurrentPlaceUuid != playerTopUuid)
                .GroupBy(s => s.CurrentPlaceUuid);

            foreach (var grp in byPlace.ToList())
            {
                var squads = grp.ToList();
                if (squads.Count < 2) continue;
                for (int i = 0; i < squads.Count; i++)
                    for (int j = i + 1; j < squads.Count; j++)
                    {
                        if (!AreHostile(squads[i], squads[j])) continue;
                        ResolveBattle(squads[i], squads[j]);
                        // one battle per place per turn keeps the log readable
                        goto nextPlace;
                    }
                nextPlace: ;
            }
        }

        public static bool AreHostile(VirtualSquad a, VirtualSquad b)
        {
            // Blood feuds override every other allegiance.
            var feud = ALifeLifecycle.FeudBetween(a, b);
            if (feud != null && feud.Heat >= 30) return true;

            bool aWild = a.Archetype == SquadArchetype.HUNTERS;
            bool bWild = b.Archetype == SquadArchetype.HUNTERS;
            if (aWild && bWild) return false;           // packs avoid each other
            if (aWild || bWild) return true;            // wild packs attack everyone else
            if (a.Archetype == SquadArchetype.PILGRIMS || b.Archetype == SquadArchetype.PILGRIMS)
                return a.Archetype == SquadArchetype.RAIDERS || b.Archetype == SquadArchetype.RAIDERS;
            // Raiders prey on caravans of any other allegiance
            if ((a.Archetype == SquadArchetype.RAIDERS && b.Archetype == SquadArchetype.CARAVAN) ||
                (b.Archetype == SquadArchetype.RAIDERS && a.Archetype == SquadArchetype.CARAVAN))
                return a.FactionUuid != b.FactionUuid;
            // Faction squads fight along war lines
            return ALifeWorldBridge.AreAtWar(a.FactionUuid, b.FactionUuid);
        }

        public static void ResolveBattle(VirtualSquad a, VirtualSquad b)
        {
            var state = ALifeData.State;
            string placeName = a.CurrentPlaceName;
            string placeUuid = a.CurrentPlaceUuid;

            var feud = ALifeLifecycle.FeudBetween(a, b);
            double fury = feud != null && feud.Heat >= 30 ? 1.15 : 1.0; // feuding squads fight to the knife

            double powerA = a.Strength * (0.5 + a.Morale / 200.0) * (0.7 + Rng.NextDouble() * 0.6) * fury;
            double powerB = b.Strength * (0.5 + b.Morale / 200.0) * (0.7 + Rng.NextDouble() * 0.6) * fury;
            VirtualSquad winner = powerA >= powerB ? a : b;
            VirtualSquad loser  = powerA >= powerB ? b : a;

            int loserLosses  = Math.Max(1, (int)Math.Round(loser.Size * (0.4 + Rng.NextDouble() * 0.4)));
            int winnerLosses = (int)Math.Round(winner.Size * (0.1 + Rng.NextDouble() * 0.25));
            loserLosses = Math.Min(loserLosses, loser.Size);
            winnerLosses = Math.Min(winnerLosses, winner.Size);
            loser.Size -= loserLosses;
            winner.Size -= winnerLosses;
            winner.LastEventTurn = loser.LastEventTurn = state.CurrentTurn;

            bool loserWiped  = loser.Size <= 0;
            bool winnerWiped = winner.Size <= 0;
            bool loserLeaderDies = loserWiped || Rng.NextDouble() < 0.12;
            bool winnerLeaderDies = winnerWiped || (winnerLosses > 0 && Rng.NextDouble() < 0.05);

            // Embodied squads bleed real corpses onto the field.
            ALifeEmbodiment.ApplyOfflineCasualties(loser, loserLosses, loserLeaderDies);
            ALifeEmbodiment.ApplyOfflineCasualties(winner, winnerLosses, winnerLeaderDies);

            ALifeLifecycle.AwardBattleXP(winner, won: true, enemiesKilled: loserLosses);
            ALifeLifecycle.AwardBattleXP(loser, won: false, enemiesKilled: winnerLosses);

            string feudNote = feud != null ? " The blood feud between them ran red." : "";
            string desc = $"{Cap(winner.Name)} under {winner.Leader?.FullName ?? "their leader"} clashed with {loser.Name} at {placeName} — " +
                          $"{loserLosses + winnerLosses} dead" +
                          (loserWiped ? $"; {loser.Name} was wiped out." : $"; {loser.Name} broke and fled.") + feudNote;
            ALifeData.LogEvent(placeUuid, placeName, loserWiped ? "WIPE" : "BATTLE", desc);
            winner.AddChronicle($"Broke {loser.Name} at {placeName}.");
            if (!loserWiped) loser.AddChronicle($"Beaten by {winner.Name} at {placeName}.");

            // Faction-on-faction wipes are world news
            if (loserWiped && loser.FactionName != null && winner.FactionName != null)
                ALifeWorldBridge.PushWorldEvent(desc, alertPlayer: false);

            if (loserWiped)
            {
                ALifeLifecycle.WipeSquad(loser, $"destroyed by {winner.Name} at {placeName}");
            }
            else
            {
                if (loserLeaderDies)
                    ALifeLifecycle.Succession(loser, winner, leaderDead: true,
                        $"slain in battle with {winner.Name}");
                else
                    ALifeLifecycle.AddFeud(loser, winner, 30, $"the battle at {placeName}");

                if (loser.Size == 1 && loser.Leader != null && string.IsNullOrEmpty(loser.Leader.Epithet))
                {
                    loser.Leader.Epithet = "Who Walked Away";
                    loser.AddChronicle($"{loser.Leader.Name} alone survived — now called {loser.Leader.FullName}.");
                }

                loser.Morale = Math.Max(10, loser.Morale - 30);
                Place fleeTo = ALifeGraph.RandomNeighbor(loser.CurrentPlaceUuid);
                if (fleeTo != null)
                    MoveSquadTo(loser, fleeTo);
                loser.GoalType = SquadGoal.FLEE;
                loser.TargetPlaceUuid = null;
                loser.TargetSquadId = null;
                loser.Activity = ALifeNames.ActivityLine(loser);
            }

            if (winnerWiped)
            {
                ALifeLifecycle.WipeSquad(winner, $"bled dry even in victory over {loser.Name} at {placeName}");
            }
            else
            {
                if (winnerLeaderDies)
                    ALifeLifecycle.Succession(winner, loser, leaderDead: true,
                        $"slain even in victory over {loser.Name}");
                winner.Morale = Math.Min(100, winner.Morale + 15);
                // Victors cool their side of the feud a little; vengeance satisfied.
                var winnerFeud = winner.Feuds.FirstOrDefault(f => f.EnemySquadId == loser.Id);
                if (winnerFeud != null) winnerFeud.Heat = Math.Max(0, winnerFeud.Heat - 40);
            }
        }

        // ── Named NPC migration ─────────────────────────────────────────────────

        private static void MaybeMigrateNamedNpc(GameplayManager manager, List<Place> topPlaces, string playerTopUuid)
        {
            if (!ALifePlugin.CfgNpcMigration.Value) return;
            if (Rng.NextDouble() >= ALifePlugin.CfgMigrationChance.Value) return;

            var squadMemberUuids = new HashSet<string>(
                ALifeData.State.Squads.SelectMany(s => s.MemberUuids));

            var followers = manager.scenarioState?.followers ?? new List<GameCharacter>();
            var candidates = new List<GameCharacter>();
            foreach (var kv in SS.I.uuidToGameEntityMap.ToList())
            {
                var ch = kv.Value as GameCharacter;
                if (ch == null) continue;
                if (ch.characterType != GameCharacter.CharacterType.NPC) continue;
                if (ch.corpseState != GameCharacter.CorpseState.NONE) continue;
                if (ch.isMerchant) continue;                       // don't move shops out from under the player
                if (followers.Contains(ch)) continue;
                if (squadMemberUuids.Contains(ch.uuid)) continue;  // squad members move with their squad
                if (ch.parentPlace == null) continue;
                Place top = ch.parentPlace.GetTopLvlPlace();
                if (top == null || top.uuid == playerTopUuid) continue; // never move NPCs the player can see
                candidates.Add(ch);
            }
            if (candidates.Count == 0) return;

            GameCharacter npc = candidates[Rng.Next(candidates.Count)];
            Place fromTop = npc.parentPlace.GetTopLvlPlace();
            Place dest = ALifeGraph.RandomNeighbor(fromTop.uuid);
            if (dest == null || dest.uuid == playerTopUuid) return;

            try
            {
                InGameEntity ent = npc.ParentInGameEnt();
                if (ent == null) return;
                ent.SetAsChildOfPl(dest);                  // native relocation (same path the game's own
                if (dest.IsGrdStyle())                     //  "character from previous place" decision uses)
                    ent.PopulateGrdInfo(dest, null, Vector2Int.one);
                npc.parentPlace = dest;

                ALifeData.LogEvent(dest.uuid, dest.GetPrettyName(), "MIGRATION",
                    $"{npc.GetPrettyName()} traveled from {fromTop.GetPrettyName()} to {dest.GetPrettyName()}.");
                ALifeData.LogEvent(fromTop.uuid, fromTop.GetPrettyName(), "MIGRATION",
                    $"{npc.GetPrettyName()} left, heading for {dest.GetPrettyName()}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ALife] NPC migration failed for " + npc.GetPrettyName() + ": " + ex.Message);
            }
        }

        internal static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }
    }
}
