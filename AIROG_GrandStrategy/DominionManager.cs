using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;
using HarmonyLib;
using UnityEngine;

namespace AIROG_GrandStrategy
{
    // Founding + the strategic tick. The tick rides WorldExpansion's minor tick (~every
    // 5 turns) so the dominion's economy moves at the same cadence as rival factions.
    public static class DominionManager
    {
        private static readonly System.Random rng = new System.Random();

        private const int CP_REGEN        = 2;
        private const int BASE_INCOME     = 5;
        private const int MINE_INCOME     = 6;
        private const int MARKET_INCOME   = 4;
        private const int FARM_POP_GROWTH = 20;
        private const int REBELLION_UNREST = 100;

        [HarmonyPatch(typeof(WorldSimulation), nameof(WorldSimulation.RunMinorTick))]
        [HarmonyPostfix]
        public static void Postfix_RunMinorTick(GameplayManager manager)
        {
            try { StrategicTick(manager); }
            catch (Exception e) { Debug.LogError($"[GrandStrategy] Strategic tick failed: {e}"); }
        }

        // ─── Founding ─────────────────────────────────────────────────────────────

        public static string FoundDominion(GameplayManager manager, string name)
        {
            var s = GrandStrategyData.State;
            if (s.Founded) return $"You already rule {s.DominionName}.";
            if (string.IsNullOrWhiteSpace(name)) return "Usage: GS_FOUND <dominion name>";

            // Try to find a native "Player" faction object. In some scenes GetCurrentFactions()
            // excludes it (WorldExpansion itself filters it out), so we fall back gracefully.
            var allFactions = manager.GetCurrentFactions() ?? new List<Faction>();
            var playerFaction = allFactions.FirstOrDefault(f => f != null && f.GetPrettyName() == "Player");

            // Derive a stable UUID: prefer the native faction's UUID, otherwise use a
            // deterministic constant so the dominion's data survives save/load.
            const string FALLBACK_PLAYER_UUID = "player-dominion-uuid-static-0001";
            string playerUuid = playerFaction?.uuid ?? FALLBACK_PLAYER_UUID;



            Place capital = null;
            try { capital = manager.currentPlace != null ? manager.currentPlace.GetTopLvlPlace() : null; }
            catch { }
            if (capital == null) return "You must be somewhere in the world to found a dominion.";
            if (capital.faction != null && capital.faction.uuid != playerUuid)
                return $"This territory already belongs to {capital.faction.GetPrettyName()} — found your dominion on unclaimed ground, or take it by other means.";

            // Detect the world's genre up front so every string from here on is voiced correctly
            string themeKey = Themes.Apply(s, "AUTO", manager);
            Debug.Log($"[GrandStrategy] Dominion theme: {themeKey}");

            s.Founded          = true;
            s.DominionName     = name.Trim();
            s.FactionUuid      = playerUuid;
            s.CapitalPlaceUuid = capital.uuid;
            s.CapitalName      = capital.GetPrettyName();
            s.FoundedTurn      = GrandStrategyData.WorldExpansionTurn();
            s.Treasury         = 50;
            s.ArmyStrength     = 10;
            s.CommandPoints    = 2;

            // Register the dominion into WorldExpansion's sim as a real faction so wars,
            // diplomacy tiers, the political map, and prompt injection all see it.
            var fac = WorldData.GetFactionData(playerUuid);
            fac.Name       = s.DominionName;
            fac.Tag        = "Player Dominion";
            fac.Resources  = 100;
            fac.Population = 800;
            fac.PopState   = "Normal";
            fac.Seeded     = true;

            OrderSystem.ClaimPlace(manager, capital, isCapital: true);

            GrandStrategyData.LogDeed($"{s.DominionName} was founded, with {s.CapitalName} as its capital.");
            WorldData.LogEvent($"A new power has risen: {s.DominionName}, seated at {s.CapitalName}.", "DOMINION");
            WorldData.QueuePlayerEvent(
                $"You have founded the dominion of {s.DominionName}, with {s.CapitalName} as its capital. The world's factions take note of a new power.",
                "DOMINION_FOUNDED");
            GrandStrategyData.TryRecordChronicleBeat($"{s.DominionName} was founded, with {s.CapitalName} as its capital.");
            WorldEventsUI.MarkDirty();
            GrandStrategyData.SaveToCurrentDir();
            WorldData.SaveToCurrentDir();
            return $"The dominion of {s.DominionName} is founded! Capital: {s.CapitalName}. Use GS_ORDERS to see what you can decree.";
        }

        // ─── Strategic tick ───────────────────────────────────────────────────────

        public static void StrategicTick(GameplayManager manager)
        {
            var s = GrandStrategyData.State;
            if (!s.Founded) return;
            Themes.EnsureTheme(s, manager); // legacy saves: detect a theme on first tick

            s.CommandPoints = Math.Min(s.MaxCommandPoints, s.CommandPoints + CP_REGEN);

            var fac = WorldData.GetFactionData(s.FactionUuid);
            float marketMult = WorldData.CurrentState.Market != null ? WorldData.CurrentState.Market.SellMultiplier : 1f;

            // Holding yields + unrest drift
            int income = BASE_INCOME;
            bool hasTemple  = s.Wonders.Contains("TEMPLE");
            bool hasCitadel = s.Wonders.Contains("CITADEL");
            foreach (var kvp in s.Holdings)
            {
                var h = kvp.Value;
                income += 2;
                if (h.Improvements.Contains("MINE"))   income += MINE_INCOME;
                if (h.Improvements.Contains("MARKET")) income += Mathf.RoundToInt(MARKET_INCOME * Mathf.Max(0.5f, marketMult));
                if (h.Improvements.Contains("FARM"))
                {
                    fac.Population += FARM_POP_GROWTH;
                    h.Unrest = Math.Max(0, h.Unrest - 2);
                }
                if (h.Improvements.Contains("GARRISON"))
                    h.Unrest = Math.Max(0, h.Unrest - 1);
                if (hasTemple)                 h.Unrest = Math.Max(0, h.Unrest - 3);
                if (hasCitadel && h.IsCapital) h.Unrest = Math.Max(0, h.Unrest - 2);

                // War-weariness: every active war stirs unrest in every holding
                // War-weariness: capped to +4/tick so multi-war spirals don't rebel in a single cycle
                h.Unrest += Math.Min(4, CountActiveWars(s) * 2);

                // Tax policy: the leadership's greed (or mercy) is felt everywhere, every tick
                if (s.TaxPolicy == "HIGH")     h.Unrest += 3;
                else if (s.TaxPolicy == "LOW") h.Unrest = Math.Max(0, h.Unrest - 2);

                // Overextension: holdings far from the capital are harder to administer,
                // naturally discouraging sprawl without an explicit distance cap
                if (!h.IsCapital)
                {
                    int overextension = OverextensionUnrest(s, kvp.Key);
                    if (overextension > 0) h.Unrest += overextension;
                }
            }
            if (s.TaxPolicy == "HIGH")     income = income * 3 / 2;
            else if (s.TaxPolicy == "LOW") income = income / 2;
            if (s.Wonders.Contains("MINT")) income = income * 3 / 2;
            s.Treasury += income;
            fac.Resources = Math.Max(0, s.Treasury / 2); // mirror treasury so bankruptcy is visible to rival factions

            TickWonder(s);
            TickVassals(s);
            TickDiplomacy(manager, s);
            CourtSystem.Tick(s);
            CheckRetaliation(manager, s);
            CheckEspionage(manager, s);
            CheckRebellion(manager, s, fac);
            CheckVictory(manager, s, fac);

            GrandStrategyData.SaveToCurrentDir();
        }

        // Distance-based unrest: 1 unrest per ~300 world-units from the capital, capped at 3/tick
        private static int OverextensionUnrest(DominionState s, string holdingUuid)
        {
            try
            {
                if (SS.I?.uuidToGameEntityMap == null) return 0;
                if (!SS.I.uuidToGameEntityMap.TryGetValue(s.CapitalPlaceUuid, out var capEnt) || !(capEnt is Place capPlace)) return 0;
                if (!SS.I.uuidToGameEntityMap.TryGetValue(holdingUuid, out var hEnt) || !(hEnt is Place hPlace)) return 0;
                float dist = Vector2.Distance(capPlace.worldCoords, hPlace.worldCoords);
                return Math.Min(3, Mathf.FloorToInt(dist / 300f));
            }
            catch { return 0; }
        }

        // Fabricated claims go stale over time; standing trade pacts pay the treasury each tick
        private static void TickDiplomacy(GameplayManager manager, DominionState s)
        {
            int turn = GrandStrategyData.WorldExpansionTurn();
            foreach (var uuid in s.CasusBelli.ToList())
            {
                if (!s.CasusBelliExpiry.TryGetValue(uuid, out int exp) || turn < exp) continue;
                s.CasusBelli.Remove(uuid);
                s.CasusBelliExpiry.Remove(uuid);
                string name = WorldData.CurrentState.Factions.TryGetValue(uuid, out var fd) ? fd.Name : "a rival power";
                GrandStrategyData.LogDeed($"{s.DominionName}'s claims against {name} have grown stale and are abandoned.");
            }

            int tradeIncome = 0;
            foreach (var f in manager.GetCurrentFactions() ?? new List<Faction>())
            {
                if (f == null || f.uuid == s.FactionUuid || f.GetPrettyName() == "Player") continue;
                if (WorldData.CurrentState.EliminatedFactions.Contains(f.uuid)) continue;
                string key = WorldData.GetRelationshipKey(s.FactionUuid, f.uuid);
                if (WorldData.GetTier(key) >= DiplomaticTier.TradePact) tradeIncome += 5;
            }
            if (tradeIncome > 0) s.Treasury += tradeIncome;
        }

        // Rivals nursing a real grievance occasionally strike back — the dominion's own
        // SABOTAGE/INCITE orders, mirrored, so hostility isn't purely one-directional
        private static void CheckEspionage(GameplayManager manager, DominionState s)
        {
            var rivals = (manager.GetCurrentFactions() ?? new List<Faction>())
                .Where(f => f != null && f.uuid != s.FactionUuid && f.GetPrettyName() != "Player"
                            && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid))
                .ToList();

            foreach (var f in rivals)
            {
                string key = WorldData.GetRelationshipKey(s.FactionUuid, f.uuid);
                if (WorldData.GetGrievance(key) < 3) continue; // needs real animosity to bother
                if (rng.NextDouble() > 0.15) continue;         // 15%/tick per sufficiently aggrieved rival

                string fName = f.GetPrettyName();
                if (rng.NextDouble() < 0.5)
                {
                    int loss = Math.Min(s.Treasury, rng.Next(10, 26));
                    s.Treasury -= loss;
                    string cur = GrandStrategyData.L.CurrencyWord;
                    WorldData.LogEvent($"Agents of {fName} sabotaged the treasury of {s.DominionName}, making off with {loss} {cur}.", "DOMINION");
                    WorldData.QueuePlayerEvent($"{fName}'s saboteurs struck at your treasury — {loss} {cur} lost.", "DOMINION_SABOTAGED");
                }
                else
                {
                    var holding = s.Holdings.Values.OrderBy(_ => rng.Next()).FirstOrDefault();
                    if (holding == null) continue;
                    holding.Unrest += 8;
                    WorldData.LogEvent($"Agitators sent by {fName} stirred unrest in {holding.Name}.", "DOMINION");
                    WorldData.QueuePlayerEvent($"{fName} has stirred discontent in {holding.Name}.", "DOMINION_INCITED");
                }
                WorldEventsUI.MarkDirty();
            }
        }

        private static void TickWonder(DominionState s)
        {
            if (string.IsNullOrEmpty(s.WonderInProgress)) return;
            s.WonderTicksLeft--;
            if (s.WonderTicksLeft > 0) return;

            var def = OrderSystem.WonderDefs.FirstOrDefault(w => w.Key == s.WonderInProgress);
            string name = OrderSystem.WonderDisplayName(s.WonderInProgress);
            s.Wonders.Add(s.WonderInProgress);
            s.WonderInProgress = "";
            s.WonderTicksLeft  = 0;

            GrandStrategyData.LogDeed($"The {name} was completed in {s.CapitalName} — a wonder of the age.");
            WorldData.LogEvent($"The {name} now stands at {s.CapitalName}, seat of {s.DominionName}. Travelers speak of it in distant lands.", "DOMINION");
            WorldData.QueuePlayerEvent(
                $"The {name} is complete! {s.CapitalName} is transformed{(def != null ? $" ({def.Effect})" : "")}.",
                "DOMINION_WONDER");
            GrandStrategyData.TryRecordChronicleBeat($"The {name} was completed in {s.CapitalName}, capital of {s.DominionName}.");
            WorldEventsUI.MarkDirty();
        }

        private static void TickVassals(DominionState s)
        {
            if (s.VassalFactionUuids.Count == 0) return;
            int tributeTotal = 0;

            foreach (var uuid in s.VassalFactionUuids.ToList())
            {
                string name = s.VassalNames.TryGetValue(uuid, out var n) ? n : $"a {GrandStrategyData.L.VassalNoun}";

                if (WorldData.CurrentState.EliminatedFactions.Contains(uuid))
                {
                    s.VassalFactionUuids.Remove(uuid);
                    s.VassalNames.Remove(uuid);
                    WorldData.LogEvent($"{name}, {GrandStrategyData.L.VassalNoun} of {s.DominionName}, has ceased to exist. Its tribute ends with it.", "DOMINION");
                    continue;
                }

                // The sim can sour relations on its own; a vassal driven to Cold War or worse breaks free
                string key = WorldData.GetRelationshipKey(s.FactionUuid, uuid);
                if ((int)WorldData.GetTier(key) <= (int)DiplomaticTier.ColdWar)
                {
                    s.VassalFactionUuids.Remove(uuid);
                    s.VassalNames.Remove(uuid);
                    GrandStrategyData.LogDeed($"{name} renounced its obligations and broke free of {s.DominionName}.");
                    WorldData.QueuePlayerEvent(
                        $"{name} has renounced its allegiance to {s.DominionName}! Their envoys cut all ties.",
                        "DOMINION_VASSAL_REVOLT");
                    WorldEventsUI.MarkDirty();
                    continue;
                }

                var vfac = WorldData.GetFactionData(uuid);
                int tribute = Math.Max(2, Math.Min(10, vfac.Resources / 10));
                vfac.Resources = Math.Max(0, vfac.Resources - tribute);
                tributeTotal += tribute;
            }

            if (tributeTotal > 0) s.Treasury += tributeTotal;
        }

        private static int CountActiveWars(DominionState s)
        {
            return WorldData.CurrentState.ActiveWars.Values
                .Count(w => w.ActorUuid == s.FactionUuid || w.TargetUuid == s.FactionUuid);
        }

        // Enemies at war with the dominion strike back — wars are two-sided even in Phase 0.
        private static void CheckRetaliation(GameplayManager manager, DominionState s)
        {
            foreach (var war in WorldData.CurrentState.ActiveWars.Values.ToList())
            {
                bool involvesUs = war.ActorUuid == s.FactionUuid || war.TargetUuid == s.FactionUuid;
                if (!involvesUs || rng.NextDouble() > 0.30) continue;

                string enemyUuid = war.ActorUuid == s.FactionUuid ? war.TargetUuid : war.ActorUuid;
                string enemyName = war.ActorUuid == s.FactionUuid ? war.TargetName : war.ActorName;
                var enemy = WorldData.GetFactionData(enemyUuid);

                // Pick a target holding; watchtowers halve the raid's success chance
                var targetEntry = s.Holdings.OrderBy(_ => rng.Next()).FirstOrDefault();
                if (targetEntry.Value == null) continue;
                var holding = targetEntry.Value;

                int attack  = enemy.Resources / 3 + enemy.Population / 100 + rng.Next(1, 21);
                int defense = s.ArmyStrength + (holding.Improvements.Contains("GARRISON") ? 15 : 0)
                              + (holding.IsCapital && s.Wonders.Contains("CITADEL") ? 30 : 0) + rng.Next(1, 21);
                if (holding.Improvements.Contains("WATCHTOWER") && rng.NextDouble() < 0.5)
                {
                    WorldData.LogEvent($"Watchtowers of {holding.Name} spotted {enemyName}'s raiders — the attack was turned back before it began.", "DOMINION");
                    continue;
                }

                if (attack > defense)
                {
                    // A rout, not just a win, risks the holding itself — never the capital.
                    // War-only, and only for non-capital holdings, so the dominion has skin
                    // in every war it starts or is drawn into, not just gold to lose.
                    if (!holding.IsCapital && attack > defense * 1.5f && rng.NextDouble() < 0.25)
                    {
                        s.Holdings.Remove(targetEntry.Key);
                        var facLoss = WorldData.GetFactionData(s.FactionUuid);
                        facLoss.ClaimedPlaceUuids.Remove(targetEntry.Key);
                        enemy.ClaimedPlaceUuids.Add(targetEntry.Key);
                        try
                        {
                            if (SS.I != null && SS.I.uuidToGameEntityMap != null
                                && SS.I.uuidToGameEntityMap.TryGetValue(targetEntry.Key, out var e) && e is Place place)
                            {
                                var enemyFaction = (manager.GetCurrentFactions() ?? new List<Faction>())
                                    .FirstOrDefault(f => f != null && f.uuid == enemyUuid);
                                if (enemyFaction != null) place.faction = enemyFaction;
                            }
                        }
                        catch { }

                        GrandStrategyData.LogDeed($"{holding.Name} fell to {enemyName}'s forces — {s.DominionName}'s {GrandStrategyData.L.BannersNoun} are torn down.");
                        WorldData.LogEvent($"{enemyName} has seized {holding.Name} from {s.DominionName}!", "DOMINION");
                        WorldData.QueuePlayerEvent(
                            $"{enemyName}'s forces overran {holding.Name} — the holding is lost.", "DOMINION_HOLDING_LOST");
                    }
                    else
                    {
                        int plunder = Math.Min(s.Treasury, rng.Next(10, 26));
                        s.Treasury -= plunder;
                        holding.Unrest += 10;
                        string cur2 = GrandStrategyData.L.CurrencyWord;
                        WorldData.LogEvent($"{enemyName} raided {holding.Name}, plundering {plunder} {cur2} from {s.DominionName}!", "DOMINION");
                        WorldData.QueuePlayerEvent(
                            $"{enemyName} has raided {holding.Name} — {plunder} {cur2} plundered and the people are shaken.",
                            "DOMINION_RAIDED");
                    }
                }
                else
                {
                    s.ArmyStrength = Math.Max(0, s.ArmyStrength - rng.Next(1, 4));
                    WorldData.LogEvent($"{enemyName} probed the defenses of {holding.Name} but was repelled.", "DOMINION");
                }
                WorldEventsUI.MarkDirty();
            }
        }

        private static void CheckRebellion(GameplayManager manager, DominionState s, FactionExtData fac)
        {
            foreach (var kvp in s.Holdings.ToList())
            {
                var h = kvp.Value;
                if (h.Unrest < REBELLION_UNREST) continue;
                if (h.IsCapital) { h.Unrest = REBELLION_UNREST - 10; continue; } // the capital seethes but never secedes

                s.Holdings.Remove(kvp.Key);
                fac.ClaimedPlaceUuids.Remove(kvp.Key);
                try
                {
                    if (SS.I != null && SS.I.uuidToGameEntityMap != null
                        && SS.I.uuidToGameEntityMap.TryGetValue(kvp.Key, out var e) && e is Place place)
                        place.faction = null;
                }
                catch { }

                GrandStrategyData.LogDeed($"{h.Name} rose in rebellion and broke away from {s.DominionName}.");
                WorldData.LogEvent($"{h.Name} has risen in open rebellion against {s.DominionName} and declared independence!", "DOMINION");
                WorldData.QueuePlayerEvent(
                    $"Rebellion! {h.Name} has thrown off the rule of {s.DominionName}. Your officials flee the area.",
                    "DOMINION_REBELLION");
                WorldEventsUI.MarkDirty();
            }
        }

        private static void CheckVictory(GameplayManager manager, DominionState s, FactionExtData fac)
        {
            if (!string.IsNullOrEmpty(s.ActiveVictory)) return;

            string won = null;
            string desc = null;

            // DOMINATION — own half the known world
            try
            {
                var topPlaces = manager.GetAllTopLvlPlaces();
                if (topPlaces != null && topPlaces.Count >= 5
                    && s.Holdings.Count >= 5 && s.Holdings.Count * 2 >= topPlaces.Count)
                {
                    won  = "DOMINATION";
                    desc = $"{s.DominionName} now rules half the known world. Its dominion is beyond dispute.";
                }
            }
            catch { }

            // HEGEMONY — every surviving faction allied or vassalized
            if (won == null)
            {
                var others = (manager.GetCurrentFactions() ?? new List<Faction>())
                    .Where(f => f != null && f.uuid != s.FactionUuid && f.GetPrettyName() != "Player"
                                && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid))
                    .ToList();
                if (others.Count >= 2 && others.All(f =>
                        s.VassalFactionUuids.Contains(f.uuid)
                        || WorldData.GetTier(WorldData.GetRelationshipKey(s.FactionUuid, f.uuid)) == DiplomaticTier.Alliance))
                {
                    won  = "HEGEMONY";
                    desc = $"Every power in the world stands allied to or beneath {s.DominionName}. A hegemony in all but name.";
                }
            }

            // GOLDEN AGE — wealth and contentment
            // GOLDEN_AGE — allow up to 10 unrest per holding; exact-zero is impossible mid-war
            if (won == null && s.Treasury >= 500 && s.Holdings.Count >= 5
                && s.Holdings.Values.All(h => h.Unrest <= 10))
            {
                won  = "GOLDEN_AGE";
                desc = $"{s.DominionName} has entered a golden age — coffers overflowing, its people content across every holding.";
            }

            // WONDER_AGE — every great work raised in the capital
            if (won == null && s.Wonders.Count >= OrderSystem.WonderDefs.Count)
            {
                string wonderList = string.Join(", ",
                    OrderSystem.WonderDefs.Select(w => "the " + OrderSystem.WonderDisplayName(w.Key)));
                won  = "WONDER_AGE";
                desc = $"{wonderList} — every great work now stands at {s.CapitalName}. {s.DominionName} will be remembered for ages.";
            }

            if (won == null) return;

            s.ActiveVictory = won;
            GrandStrategyData.LogDeed(desc);
            WorldData.LogEvent(desc, "DOMINION");
            WorldData.QueuePlayerEvent(desc + " Your renown spreads far beyond your borders.", "DOMINION_VICTORY");
            GrandStrategyData.TryRecordChronicleBeat(desc);
            WorldEventsUI.MarkDirty();
        }
    }
}
