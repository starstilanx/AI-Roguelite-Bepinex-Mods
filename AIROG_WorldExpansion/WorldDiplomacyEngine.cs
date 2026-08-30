using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_WorldExpansion
{
    /// <summary>
    /// Faction AI: minor-tick actions (war/trade/rumor), territorial expansion/conquest,
    /// faction elimination, population drift, and diplomatic tier drift.
    /// </summary>
    internal static class WorldDiplomacyEngine
    {
        private const int GRIEVANCE_WAR_THRESHOLD = 3;  // raids before war declared
        private const int    EXPANSION_COST    = 25;
        private const int    EXPANSION_MIN_RES = 50;
        private const double EXPANSION_CHANCE  = 0.35;

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

                // Base income + population modifier
                int popBonus = 0;
                if      (data.PopState == "Thriving")  popBonus =  10;
                else if (data.PopState == "Struggling") popBonus = -5;
                else if (data.PopState == "Razed")      popBonus = -10;
                data.Resources = Math.Max(0, data.Resources + 5 + popBonus);
            }

            // Update faction populations and log state transitions
            UpdateFactionPopulations(manager);

            // Faction courts: generate missing leaders/lieutenants, bind figures to real NPCs
            FactionCourtSystem.EnsureCourts(manager);
            FactionCourtSystem.TryBindFigures(manager);

            // Hostile/friendly factions react to the player's native reputation standing
            PlayerWorldActor.StandingTick(manager);

            // Pick an active non-player faction to act
            var activeFactions = factions
                .Where(f => f.GetPrettyName() != "Player" && !eliminated.Contains(f.uuid))
                .ToList();
            if (activeFactions.Count == 0) return;

            // Peaceful territorial growth toward unclaimed land
            TryExpandTerritories(manager, activeFactions);

            var acting    = activeFactions[WorldSimUtils.Rng.Next(activeFactions.Count)];
            var actingData = WorldData.GetFactionData(acting.uuid);

            if (actingData.Resources > 30)
            {
                var others = activeFactions.Where(f => f.uuid != acting.uuid).ToList();
                if (others.Count == 0) return;
                var target     = others[WorldSimUtils.Rng.Next(others.Count)];
                PerformFactionAction(acting, target, actingData, manager);
            }
        }

        private static void PerformFactionAction(Faction acting, Faction target, FactionExtData actingData, GameplayManager manager)
        {
            UpdateFactionTag(acting);
            UpdateFactionTag(target);

            string actingTag = actingData.Tag;
            var    targetData = WorldData.GetFactionData(target.uuid);
            string targetTag  = targetData.Tag;
            string relKey     = WorldData.GetRelationshipKey(acting.uuid, target.uuid);

            // Establish initial diplomatic relation if none exists
            if (!WorldData.CurrentState.DiplomaticRelations.ContainsKey(relKey))
            {
                DiplomaticTier initialTier = DiplomaticTier.Neutral;
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    initialTier = DiplomaticTier.Hostile;
                else if (actingTag == targetTag && actingTag != "Neutral")
                    initialTier = DiplomaticTier.NonAggression;
                else if (WorldSimUtils.Rng.NextDouble() > 0.8)
                    initialTier = WorldSimUtils.Rng.NextDouble() > 0.5 ? DiplomaticTier.NonAggression : DiplomaticTier.Hostile;
                WorldData.SetTier(relKey, initialTier, "first contact", WorldData.CurrentState.CurrentTurn,
                    acting.GetPrettyName(), target.GetPrettyName());
            }

            DiplomaticTier tier = WorldData.GetTier(relKey);
            bool atWar          = WorldData.CurrentState.ActiveWars.ContainsKey(relKey);

            // Action weights: [War/Raid, Trade, Rumor]
            double[] weights;
            if (atWar || tier <= DiplomaticTier.Hostile)
                weights = new double[] { 85, 0, 15 };
            else if (tier >= DiplomaticTier.TradePact)
                weights = new double[] { 0, 80, 20 };
            else if (tier == DiplomaticTier.NonAggression)
                weights = new double[] { 10, 50, 40 };
            else // Neutral or ColdWar
            {
                weights = new double[] { 30, 30, 40 };
                if ((actingTag == "Holy" && targetTag == "Demon") || (actingTag == "Demon" && targetTag == "Holy"))
                    weights[0] += 50;
                if (actingTag == "Trade" || targetTag == "Trade")
                    weights[1] += 40;
            }

            int    actionType = PickWeighted(weights);
            string eventDesc  = "";
            string eventType  = "INFO";

            if (actionType == 0) // WAR / RAID
            {
                int cost = 30;
                if (actingData.Resources >= cost)
                {
                    actingData.Resources -= cost;
                    bool success = (WorldSimUtils.Rng.NextDouble() + actingData.Resources * 0.01)
                                 > (WorldSimUtils.Rng.NextDouble() + targetData.Resources * 0.01);
                    if (success)
                    {
                        int stolen = WorldSimUtils.Rng.Next(10, 30);
                        targetData.Resources = Math.Max(0, targetData.Resources - stolen);
                        actingData.Resources += stolen;
                        eventDesc = $"{acting.GetPrettyName()} raided {target.GetPrettyName()} and plundered {stolen} resources!";
                        eventType = "WAR";

                        // Population damage from raid
                        int popLoss = WorldSimUtils.Rng.Next(20, 80);
                        targetData.Population = Math.Max(10, targetData.Population - popLoss);

                        // Accumulate grievance → escalate to formal war
                        WorldData.AddGrievance(relKey);
                        if (WorldData.ShiftTier(relKey, -1, "raid escalation",
                            acting.GetPrettyName(), target.GetPrettyName()))
                        {
                            WorldData.LogEvent(
                                $"{acting.GetPrettyName()} and {target.GetPrettyName()} relations deteriorated to {WorldData.GetTierLabel(WorldData.GetTier(relKey))}.",
                                "DIPLOMACY");
                        }

                        int grievance = WorldData.GetGrievance(relKey);
                        if (grievance >= GRIEVANCE_WAR_THRESHOLD && !atWar)
                        {
                            string casusBelli = PickCasusBelli(actingTag, targetTag, relKey);
                            WorldData.DeclareWar(acting.uuid, acting.GetPrettyName(),
                                                 target.uuid, target.GetPrettyName(), casusBelli);
                            PlayerWorldActor.OnWarDeclared(acting, target);
                        }

                        // A raid that draws blood can reach the defender's court
                        FactionCourtSystem.RollRaidCasualty(targetData, acting.GetPrettyName());

                        // Territory conquest if at war
                        if (atWar && targetData.ClaimedPlaceUuids.Count > 0)
                            TryCaptureTerritory(actingData, targetData, acting, acting.GetPrettyName(), target.GetPrettyName());

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

                    // Population growth from trade prosperity
                    actingData.Population += WorldSimUtils.Rng.Next(5, 20);
                    targetData.Population += WorldSimUtils.Rng.Next(5, 20);

                    // Trade improves relations
                    if (WorldData.ShiftTier(relKey, 1, "trade goodwill",
                        acting.GetPrettyName(), target.GetPrettyName()))
                    {
                        WorldData.LogEvent(
                            $"{acting.GetPrettyName()} and {target.GetPrettyName()} relations improved to {WorldData.GetTierLabel(WorldData.GetTier(relKey))}.",
                            "DIPLOMACY");
                    }

                    // Reduce lingering grievances between friendly trading partners
                    if (tier >= DiplomaticTier.TradePact && WorldData.CurrentState.GrievanceCounts.ContainsKey(relKey))
                        WorldData.CurrentState.GrievanceCounts[relKey] = Math.Max(0, WorldData.CurrentState.GrievanceCounts[relKey] - 1);
                }
            }
            else // RUMOR / DIPLOMACY
            {
                bool isHostile = atWar || tier <= DiplomaticTier.Hostile;
                string[] flavors = isHostile
                    ? new[] { "denounced", "mocked", "threatened", "spied on", "accused" }
                    : tier >= DiplomaticTier.TradePact
                        ? new[] { "praised", "sent gifts to", "held a feast for", "supported", "defended" }
                        : new[] { "sent diplomats to", "observed", "has concerns about", "is ignoring", "proposed talks with" };

                string flavor = flavors[WorldSimUtils.Rng.Next(flavors.Length)];
                // Courts give rumors a face: half the time a known leader speaks for the faction
                string actorPart  = acting.GetPrettyName();
                string targetPart = target.GetPrettyName();
                string actorLeader = WorldData.GetLeaderTag(acting.uuid);
                if (actorLeader != null && WorldSimUtils.Rng.NextDouble() < 0.5)
                    actorPart = $"{actorLeader} of {actorPart}";
                string targetLeader = WorldData.GetLeaderTag(target.uuid);
                if (targetLeader != null && WorldSimUtils.Rng.NextDouble() < 0.3)
                    targetPart = $"{targetLeader} and {targetPart}";
                eventDesc = $"{actorPart} {flavor} {targetPart}.";
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

        // ─── Territorial Expansion ────────────────────────────────────────────────
        // Each minor tick, healthy factions may annex the nearest unclaimed top-level
        // place to their territory, so borders organically grow toward each other
        // (and war fronts on the political map eventually meet).
        private static void TryExpandTerritories(GameplayManager manager, List<Faction> activeFactions)
        {
            List<Place> topPlaces;
            try { topPlaces = manager.GetCurrentVoronoiWorld()?.GetAllTopLvlPlaces() ?? new List<Place>(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Could not enumerate places for expansion: {e.Message}");
                return;
            }
            if (topPlaces.Count == 0) return;

            var placeByUuid = new Dictionary<string, Place>();
            foreach (var p in topPlaces)
                if (p != null && !placeByUuid.ContainsKey(p.uuid)) placeByUuid[p.uuid] = p;

            var owned = new HashSet<string>();
            foreach (var p in topPlaces)
                if (p?.faction != null) owned.Add(p.uuid);
            foreach (var kvp in WorldData.CurrentState.Factions)
                if (!WorldData.CurrentState.EliminatedFactions.Contains(kvp.Key))
                    foreach (var u in kvp.Value.ClaimedPlaceUuids) owned.Add(u);

            // Fair-share cap keeps one faction from eating the whole map
            int cap = Math.Max(3, topPlaces.Count / Math.Max(2, activeFactions.Count));

            foreach (var faction in activeFactions)
            {
                var data = WorldData.GetFactionData(faction.uuid);
                if (data.ClaimedPlaceUuids.Count == 0) continue; // landless factions get land via seeding, not expansion
                if (data.ClaimedPlaceUuids.Count >= cap) continue;
                if (data.Resources < EXPANSION_MIN_RES) continue;
                if (data.PopState == "Struggling" || data.PopState == "Razed") continue;
                if (WorldSimUtils.Rng.NextDouble() > EXPANSION_CHANCE) continue;

                Place best = null;
                float bestDist = float.MaxValue;
                foreach (var ownUuid in data.ClaimedPlaceUuids)
                {
                    if (!placeByUuid.TryGetValue(ownUuid, out var ownPl)) continue;
                    foreach (var cand in topPlaces)
                    {
                        if (cand == null || owned.Contains(cand.uuid)) continue;
                        float d = (cand.worldCoords - ownPl.worldCoords).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; best = cand; }
                    }
                }
                if (best == null) continue; // no unclaimed land left

                data.Resources -= EXPANSION_COST;
                data.ClaimedPlaceUuids.Add(best.uuid);
                owned.Add(best.uuid);
                best.faction = faction; // native ownership, visible in-game like conquest
                WorldData.LogEvent(
                    $"{faction.GetPrettyName()} has annexed {best.GetPrettyName()}, expanding its territory.",
                    "TERRITORY");

                // Only alert the AI when the map changes under the player's feet
                if (manager.currentPlace?.GetTopLvlPlace()?.uuid == best.uuid)
                    WorldData.QueuePlayerEvent(
                        $"{faction.GetPrettyName()} has annexed {best.GetPrettyName()} — the region you are in is now their territory.",
                        "TERRITORY_CLAIMED");

                WorldEventsUI.MarkDirty();
            }
        }

        // ─── Territory Conquest ───────────────────────────────────────────────────
        private static void TryCaptureTerritory(FactionExtData winner, FactionExtData loser,
            Faction winnerFaction, string winnerName, string loserName)
        {
            if (WorldSimUtils.Rng.NextDouble() > 0.33) return;
            if (loser.ClaimedPlaceUuids.Count == 0) return;
            string territoryUuid = loser.ClaimedPlaceUuids[WorldSimUtils.Rng.Next(loser.ClaimedPlaceUuids.Count)];
            loser.ClaimedPlaceUuids.Remove(territoryUuid);
            winner.ClaimedPlaceUuids.Add(territoryUuid);

            // Flip native ownership so the conquest is visible in-game (place icon, prompts)
            string placeName = TransferNativePlaceOwnership(territoryUuid, winnerFaction);
            WorldData.LogEvent(placeName != null
                ? $"{winnerName} has seized {placeName} from {loserName}!"
                : $"{winnerName} has seized a territory from {loserName}!", "WAR");
        }

        // Sets Place.faction on the real place, returning its name (null if the uuid can't be resolved)
        private static string TransferNativePlaceOwnership(string placeUuid, Faction newOwner)
        {
            try
            {
                if (SS.I?.uuidToGameEntityMap != null
                    && SS.I.uuidToGameEntityMap.TryGetValue(placeUuid, out var ent)
                    && ent is Place place)
                {
                    place.faction = newOwner;
                    return place.GetPrettyName();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Could not transfer place ownership: {e.Message}");
            }
            return null;
        }

        // ─── Faction Elimination ──────────────────────────────────────────────────
        private static void EliminateFaction(Faction loser, Faction victor,
            FactionExtData loserData, FactionExtData victorData, GameplayManager manager)
        {
            WorldData.CurrentState.EliminatedFactions.Add(loser.uuid);

            // Transfer territories to victor (including native place ownership)
            foreach (var territory in loserData.ClaimedPlaceUuids)
            {
                victorData.ClaimedPlaceUuids.Add(territory);
                TransferNativePlaceOwnership(territory, victor);
            }
            loserData.ClaimedPlaceUuids.Clear();

            // Population devastated by conquest
            loserData.Population = Math.Max(10, loserData.Population / 5);
            loserData.PopState   = "Razed";

            FactionCourtSystem.OnFactionEliminated(loserData, victor.GetPrettyName());

            string desc = $"[FALL OF {loser.GetPrettyName().ToUpper()}] {loser.GetPrettyName()} has been utterly defeated and absorbed by {victor.GetPrettyName()}!";
            WorldData.LogEvent(desc, "MAJOR");
            WorldData.CurrentState.MajorEventHistory.Add(desc);
            WorldData.QueuePlayerEvent($"{loser.GetPrettyName()} has fallen and been absorbed by {victor.GetPrettyName()}.", "FACTION_FALL");

            // End ALL active wars involving the loser, not just the one with the victor —
            // a defeated faction can't credibly still be fighting a third party, and
            // nothing else clears those wars (CheckActiveWarPeace only catches them once
            // its own exhaustion/duration conditions happen to line up, up to several
            // turns later), leaving ghost wars in the AI prompt, World News, and the
            // political map's war-front rendering in the meantime.
            var warsToClear = WorldData.CurrentState.ActiveWars
                .Where(kv => kv.Value.ActorUuid == loser.uuid || kv.Value.TargetUuid == loser.uuid)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in warsToClear)
                WorldData.CurrentState.ActiveWars.Remove(k);

            PlayerWorldActor.OnFactionFallen(loser, victor);

            WorldLoreExpansion.RecordHistoricalEvent(manager, desc, "History",
                new List<string> { loser.GetPrettyName(), victor.GetPrettyName(), "fallen", "conquered" });
            WorldEventsUI.MarkDirty();
            Debug.Log($"[WorldExpansion] Faction eliminated: {loser.GetPrettyName()}");
        }

        // ─── Population Update ────────────────────────────────────────────────────
        private static void UpdateFactionPopulations(GameplayManager manager)
        {
            var factions  = manager.GetCurrentFactions();
            if (factions == null) return;
            var eliminated = WorldData.CurrentState.EliminatedFactions;

            foreach (var faction in factions)
            {
                if (faction.GetPrettyName() == "Player") continue;
                if (eliminated.Contains(faction.uuid)) continue;

                var data    = WorldData.GetFactionData(faction.uuid);
                bool isAtWar = WorldData.CurrentState.ActiveWars.Values.Any(
                    w => w.ActorUuid == faction.uuid || w.TargetUuid == faction.uuid);

                int delta;
                if      (isAtWar)             delta = -WorldSimUtils.Rng.Next(10, 30);
                else if (data.Tag == "Trade") delta =  WorldSimUtils.Rng.Next(5, 15);
                else                          delta =  WorldSimUtils.Rng.Next(0, 8);

                data.Population = Math.Max(10, data.Population + delta);

                // Update pop state and log transitions
                string oldState = data.PopState;
                if      (data.Population > 2000) data.PopState = "Thriving";
                else if (data.Population > 500)  data.PopState = "Normal";
                else if (data.Population > 100)  data.PopState = "Struggling";
                else                             data.PopState = "Razed";

                if (data.PopState != oldState && !string.IsNullOrEmpty(data.Name))
                {
                    WorldData.LogEvent($"{data.Name}'s territories are now {data.PopState} (pop: {data.Population}).", "POPULATION");
                    WorldEventsUI.MarkDirty();
                }
            }
        }

        // ─── Diplomatic Drift ─────────────────────────────────────────────────────
        public static void ShiftDiplomacyOverTime()
        {
            int turn = WorldData.CurrentState.CurrentTurn;
            foreach (var kvp in WorldData.CurrentState.DiplomaticRelations)
            {
                // Active wars are managed by the war system, skip them
                if (WorldData.CurrentState.ActiveWars.ContainsKey(kvp.Key)) continue;
                var rel = kvp.Value;
                // Only drift if enough time has passed since last change
                if (turn - rel.TierChangedTurn < 30) continue;
                int target = (int)DiplomaticTier.Neutral;
                if (rel.Tier == target) continue;
                rel.Tier            += (rel.Tier < target) ? 1 : -1;
                rel.TierChangedTurn  = turn;
                rel.TierChangeReason = "natural drift";
            }
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
            return options[WorldSimUtils.Rng.Next(options.Length)];
        }

        private static void UpdateFactionTag(Faction f)
        {
            var data = WorldData.GetFactionData(f.uuid);
            if (data.Tag != "Neutral") return;

            string name = f.GetPrettyName().ToLower();

            if (WorldSimUtils.ContainsAny(name, "holy", "church", "clergy", "divine", "temple", "cult", "faith", "order of"))
                data.Tag = "Holy";
            else if (WorldSimUtils.ContainsAny(name, "demon", "hell", "abyss", "dark", "shadow", "evil", "chaos", "fiend"))
                data.Tag = "Demon";
            else if (WorldSimUtils.ContainsAny(name, "trade", "merchant", "guild", "commerce", "bank", "cartel", "company", "exchange"))
                data.Tag = "Trade";
            else if (WorldSimUtils.ContainsAny(name, "empire", "kingdom", "realm", "state", "republic", "alliance", "federation", "dominion"))
                data.Tag = "Empire";
            else if (WorldSimUtils.ContainsAny(name, "nature", "wild", "tribe", "clan", "druid", "forest", "beast", "horde"))
                data.Tag = "Nature";
            else if (WorldSimUtils.ContainsAny(name, "arcane", "mage", "wizard", "academy", "magic", "circle", "enclave"))
                data.Tag = "Arcane";
            else if (WorldSimUtils.ContainsAny(name, "undead", "necromancer", "death", "lich", "grave", "crypt"))
                data.Tag = "Undead";
        }

        private static int PickWeighted(double[] weights)
        {
            double total = 0;
            foreach (var w in weights) total += w;
            double r = WorldSimUtils.Rng.NextDouble() * total;
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
