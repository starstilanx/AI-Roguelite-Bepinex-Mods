using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_WorldExpansion;
using UnityEngine;

namespace AIROG_GrandStrategy
{
    // Strategic order engine: validates costs, resolves effects immediately, logs deeds.
    // All world mutations go through WorldExpansion's WorldData API so the sim, map lens,
    // and WorldContextProvider stay consistent.
    public static class OrderSystem
    {
        private static readonly System.Random rng = new System.Random();

        public static readonly string[] Improvements = { "FARM", "MINE", "MARKET", "GARRISON", "WATCHTOWER", "SHRINE", "BARRACKS" };
        public const int MAX_IMPROVEMENTS_PER_HOLDING = 4;
        public static readonly string[] AdvisorRoles = { "MARSHAL", "STEWARD", "SPYMASTER", "CHANCELLOR" };

        public class OrderDef
        {
            public string Type;
            public int Cp;
            public int Gold;
            public string Usage;
        }

        public static readonly List<OrderDef> Defs = new List<OrderDef>
        {
            new OrderDef { Type = "ANNEX",     Cp = 1, Gold = 25, Usage = "ANNEX — claim nearest unowned region" },
            new OrderDef { Type = "DEVELOP",   Cp = 1, Gold = 30, Usage = "DEVELOP <FARM|MINE|MARKET|GARRISON|WATCHTOWER> — improve capital" },
            new OrderDef { Type = "LEVY",      Cp = 1, Gold = 0,  Usage = "LEVY — 100 population → 10 army strength (+unrest)" },
            new OrderDef { Type = "TRADE",     Cp = 1, Gold = 0,  Usage = "TRADE — treasury windfall scaled by market" },
            new OrderDef { Type = "ENVOY",     Cp = 1, Gold = 20, Usage = "ENVOY <faction> — improve diplomatic tier" },
            new OrderDef { Type = "FABRICATE", Cp = 2, Gold = 40, Usage = "FABRICATE <faction> — gain casus belli" },
            new OrderDef { Type = "WAR",       Cp = 2, Gold = 0,  Usage = "WAR <faction> — declare war (needs casus belli or Hostile)" },
            new OrderDef { Type = "CAMPAIGN",  Cp = 2, Gold = 0,  Usage = "CAMPAIGN <faction> — battle a war enemy for territory" },
            new OrderDef { Type = "INCITE",    Cp = 1, Gold = 30, Usage = "INCITE <faction> — sow grievance between them and a rival" },
            new OrderDef { Type = "SABOTAGE",  Cp = 2, Gold = 30, Usage = "SABOTAGE <faction> — drain their resources (risk of discovery)" },
            new OrderDef { Type = "PILLAGE",   Cp = 2, Gold = 0,  Usage = "PILLAGE <faction> — raid a war enemy for gold instead of land" },
            new OrderDef { Type = "PEACE",     Cp = 1, Gold = 25, Usage = "PEACE <faction> — sue for peace, paying reparations" },
            new OrderDef { Type = "VASSAL",    Cp = 2, Gold = 0,  Usage = "VASSAL <faction> — demand submission of a weakened war enemy" },
            new OrderDef { Type = "FESTIVAL",  Cp = 1, Gold = 25, Usage = "FESTIVAL — grand celebrations, −15 unrest in every holding" },
            new OrderDef { Type = "PROJECT",   Cp = 1, Gold = 0,  Usage = "PROJECT <CITADEL|MINT|TEMPLE> — begin a great work in the capital" },
            new OrderDef { Type = "SCOUT",     Cp = 1, Gold = 15, Usage = "SCOUT <faction> — spy mission reveals a rival's strength and resources" },
            new OrderDef { Type = "DISBAND",   Cp = 1, Gold = 0,  Usage = "DISBAND — demobilise 10 army → +50 population (reduces war-weariness pressure)" },
            new OrderDef { Type = "PACT",       Cp = 1, Gold = 15, Usage = "PACT <faction> — swear non-aggression (relations must be Cold War or better)" },
            new OrderDef { Type = "TRADE_DEAL", Cp = 1, Gold = 20, Usage = "TRADE_DEAL <faction> — sign a trade pact for standing income each tick (needs a non-aggression pact first)" },
            new OrderDef { Type = "COUNCIL",    Cp = 1, Gold = 40, Usage = "COUNCIL <MARSHAL|STEWARD|SPYMASTER|CHANCELLOR> — recruit an advisor who biases which petitions reach you" },
        };

        // Great works: one under construction at a time, capital-only, passive effects once built.
        public class WonderDef
        {
            public string Key;      // player-facing order argument
            public string Name;
            public int Gold;
            public int Ticks;
            public string Effect;   // shown in listings
        }

        public static readonly List<WonderDef> WonderDefs = new List<WonderDef>
        {
            // Name is a fallback only — display goes through the theme lexicon (WonderDisplayName)
            new WonderDef { Key = "CITADEL", Name = "Great Bastion",  Gold = 80,  Ticks = 3, Effect = "capital defense +30, capital unrest −2/tick" },
            new WonderDef { Key = "MINT",    Name = "Grand Exchange", Gold = 100, Ticks = 3, Effect = "+50% dominion income" },
            new WonderDef { Key = "TEMPLE",  Name = "Great Sanctum",  Gold = 90,  Ticks = 3, Effect = "−3 unrest in every holding per tick" },
        };

        // Setting-appropriate wonder name (keys stay stable for save compatibility)
        public static string WonderDisplayName(string key)
        {
            var L = GrandStrategyData.L;
            string themed = L.WonderName(key);
            if (themed != key) return themed;
            var def = WonderDefs.FirstOrDefault(w => w.Key == key);
            return def != null ? def.Name : key;
        }

        // Returns a player-facing result string (also logged); null-safe on all native lookups.
        // placeUuid: optional map-click target for ANNEX/CAMPAIGN (DominionUI's pick mode);
        // null falls back to the automatic nearest/adjacent selection.
        public static string Issue(GameplayManager manager, string type, string arg, string placeUuid = null)
        {
            var s = GrandStrategyData.State;
            if (!s.Founded) return "You have not founded a dominion yet. Use GS_FOUND <name>.";

            var def = Defs.FirstOrDefault(d => d.Type == type);
            if (def == null) return $"Unknown order '{type}'. Use GS_ORDERS to list orders.";
            if (s.CommandPoints < def.Cp) return $"Not enough command points ({s.CommandPoints}/{def.Cp} needed).";
            if (s.Treasury < def.Gold) return $"Not enough treasury ({s.Treasury}/{def.Gold} needed).";

            string result;
            switch (type)
            {
                case "ANNEX":     result = ResolveAnnex(manager, placeUuid); break;
                case "DEVELOP":   result = ResolveDevelop(arg); break;
                case "LEVY":      result = ResolveLevy(); break;
                case "TRADE":     result = ResolveTrade(); break;
                case "ENVOY":     result = ResolveEnvoy(manager, arg); break;
                case "FABRICATE": result = ResolveFabricate(manager, arg); break;
                case "WAR":       result = ResolveWar(manager, arg); break;
                case "CAMPAIGN":  result = ResolveCampaign(manager, arg, placeUuid); break;
                case "INCITE":    result = ResolveIncite(manager, arg); break;
                case "SABOTAGE":  result = ResolveSabotage(manager, arg); break;
                case "PILLAGE":   result = ResolvePillage(manager, arg); break;
                case "PEACE":     result = ResolvePeace(manager, arg); break;
                case "VASSAL":    result = ResolveVassal(manager, arg); break;
                case "FESTIVAL":  result = ResolveFestival(); break;
                case "PROJECT":   result = ResolveProject(arg); break;
                case "SCOUT":     result = ResolveScout(manager, arg); break;
                case "DISBAND":   result = ResolveDisband(); break;
                case "PACT":       result = ResolvePact(manager, arg); break;
                case "TRADE_DEAL": result = ResolveTradeDeal(manager, arg); break;
                case "COUNCIL":    result = ResolveCouncil(arg); break;
                default:          result = null; break;
            }

            if (result == null) return "Order failed — invalid or missing target.";
            if (result.StartsWith("!")) return result.Substring(1); // validation failure: no cost spent

            s.CommandPoints -= def.Cp;
            s.Treasury      -= def.Gold;
            GrandStrategyData.LogDeed(result);
            WorldData.LogEvent(result, "DOMINION");
            WorldEventsUI.MarkDirty();
            GrandStrategyData.SaveToCurrentDir();
            WorldData.SaveToCurrentDir();
            return result;
        }

        // ─── Resolution ───────────────────────────────────────────────────────────

        private static string ResolveAnnex(GameplayManager manager, string preferredPlaceUuid = null)
        {
            var s = GrandStrategyData.State;
            List<Place> topPlaces;
            try { topPlaces = manager.GetAllTopLvlPlaces() ?? new List<Place>(); }
            catch { return "!Could not survey the region."; }

            var owned = new HashSet<string>();
            foreach (var p in topPlaces)
                if (p != null && p.faction != null) owned.Add(p.uuid);
            foreach (var kvp in WorldData.CurrentState.Factions)
                foreach (var u in kvp.Value.ClaimedPlaceUuids) owned.Add(u);

            var placeByUuid = topPlaces.Where(p => p != null)
                .GroupBy(p => p.uuid).ToDictionary(g => g.Key, g => g.First());

            Place best = null;
            if (!string.IsNullOrEmpty(preferredPlaceUuid))
            {
                // A map-click target from DominionUI's pick mode overrides the nearest-place heuristic
                if (!placeByUuid.TryGetValue(preferredPlaceUuid, out best) || best == null)
                    return "!That place could not be found on the map.";
                if (owned.Contains(preferredPlaceUuid))
                    return $"!{best.GetPrettyName()} is already claimed.";
            }
            else
            {
                float bestDist = float.MaxValue;
                foreach (var ownUuid in s.Holdings.Keys)
                {
                    Place ownPl;
                    if (!placeByUuid.TryGetValue(ownUuid, out ownPl)) continue;
                    foreach (var cand in topPlaces)
                    {
                        if (cand == null || owned.Contains(cand.uuid)) continue;
                        float d = (cand.worldCoords - ownPl.worldCoords).sqrMagnitude;
                        if (d < bestDist) { bestDist = d; best = cand; }
                    }
                }
                if (best == null) return "!No unclaimed land borders your dominion.";
            }

            ClaimPlace(manager, best);
            return $"The {GrandStrategyData.L.BannersNoun} of {s.DominionName} are now raised over {best.GetPrettyName()} — annexed without bloodshed.";
        }

        private static string ResolveDevelop(string arg)
        {
            var s = GrandStrategyData.State;
            // Syntax: DEVELOP <IMP> [holdingName]
            // Split on first space to get improvement; remainder is an optional holding name filter.
            string[] parts = (arg ?? "").Trim().Split(new[] { ' ' }, 2);
            string imp       = parts[0].ToUpperInvariant();
            string holdingHint = parts.Length > 1 ? parts[1].Trim() : "";

            if (!Improvements.Contains(imp))
                return "!Choose an improvement: " + string.Join(", ", Improvements);

            HoldingData holding;
            if (string.IsNullOrEmpty(holdingHint))
            {
                // Default: capital
                holding = GrandStrategyData.GetHolding(s.CapitalPlaceUuid);
                if (holding == null) return "!Your capital is not among your holdings.";
            }
            else
            {
                // Match the nearest holding by name (case-insensitive partial)
                string needle = holdingHint.ToUpperInvariant();
                holding = s.Holdings.Values
                    .FirstOrDefault(h => h.Name.ToUpperInvariant().Contains(needle));
                if (holding == null)
                    return $"!No holding name matches '{holdingHint}'. Holdings: " +
                           string.Join(", ", s.Holdings.Values.Select(h => h.Name));
            }

            if (holding.Improvements.Contains(imp)) return $"!{holding.Name} already has a {imp.ToLower()}."
                ;
            if (holding.Improvements.Count >= MAX_IMPROVEMENTS_PER_HOLDING)
                return $"!{holding.Name} has no room for more improvements (max {MAX_IMPROVEMENTS_PER_HOLDING})."
                ;

            holding.Improvements.Add(imp);
            return $"{s.DominionName} has raised a {imp.ToLower()} in {holding.Name}.";
        }

        private static string ResolveLevy()
        {
            var s = GrandStrategyData.State;
            var fac = WorldData.GetFactionData(s.FactionUuid);
            if (fac.Population < 200) return "!Your population is too small to levy troops (need 200+)."
                ;

            fac.Population -= 100;
            s.ArmyStrength += 10;
            // Conscription is felt across all holdings, not just the capital
            foreach (var h in s.Holdings.Values)
                h.Unrest += (h.IsCapital ? 5 : 2);
            return $"{s.DominionName} has raised fresh {GrandStrategyData.L.SoldiersNoun} — army strength is now {s.ArmyStrength}."
                ;
        }

        private static string ResolveTrade()
        {
            var s = GrandStrategyData.State;
            float mult = WorldData.CurrentState.Market != null ? WorldData.CurrentState.Market.SellMultiplier : 1f;
            int gain = Mathf.RoundToInt(rng.Next(15, 41) * Mathf.Max(0.5f, mult));
            s.Treasury += gain;
            return $"A trading venture by {s.DominionName} pays off — {gain} {GrandStrategyData.L.CurrencyWord} into the treasury.";
        }

        private static string ResolveEnvoy(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return "!You cannot send envoys to a faction you are at war with.";

            string tName = target.GetPrettyName();
            if (WorldData.ShiftTier(key, 1, "dominion envoy", s.DominionName, tName))
            {
                var tier = WorldData.GetTier(key);
                if ((int)tier >= (int)DiplomaticTier.Alliance) s.CasusBelli.Remove(target.uuid);
                return $"Envoys from {s.DominionName} were received by {tName} — relations improved to {WorldData.GetTierLabel(tier)}.";
            }
            return $"Envoys from {s.DominionName} visited {tName}, but relations are already as warm as they can be.";
        }

        private static string ResolveFabricate(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";
            if (s.CasusBelli.Contains(target.uuid)) return $"!You already hold a claim against {target.GetPrettyName()}.";

            s.CasusBelli.Add(target.uuid);
            s.CasusBelliExpiry[target.uuid] = GrandStrategyData.WorldExpansionTurn() + 40; // ~8 strategic ticks before the claim goes stale
            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            WorldData.ShiftTier(key, -1, "fabricated claims", s.DominionName, target.GetPrettyName());
            return $"Agents of {s.DominionName} have manufactured a claim against {target.GetPrettyName()} — a pretext for war, should you want one.";
        }

        private static string ResolveWar(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return $"!You are already at war with {target.GetPrettyName()}.";

            bool hasClaim = s.CasusBelli.Contains(target.uuid);
            bool hostile  = (int)WorldData.GetTier(key) <= (int)DiplomaticTier.Hostile;
            if (!hasClaim && !hostile)
                return "!No justification for war — fabricate a claim first, or let relations sour to Hostile.";

            string casus = hasClaim ? "pressing fabricated claims" : "long-standing hostility";
            WorldData.DeclareWar(s.FactionUuid, s.DominionName, target.uuid, target.GetPrettyName(), casus);
            s.CasusBelli.Remove(target.uuid);
            return $"{s.DominionName} has declared war on {target.GetPrettyName()} — {casus}!";
        }

        private static string ResolveCampaign(GameplayManager manager, string arg, string preferredPlaceUuid = null)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (!WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return $"!You are not at war with {target.GetPrettyName()} — declare war first.";
            if (s.ArmyStrength < 10) return "!Your army is too weak to campaign (levy troops first).";

            string tName = target.GetPrettyName();
            var enemy = WorldData.GetFactionData(target.uuid);
            int attack  = s.ArmyStrength + rng.Next(1, 21);
            int defense = enemy.Resources / 3 + enemy.Population / 100 + rng.Next(1, 21);

            if (attack >= defense)
            {
                enemy.Resources = Math.Max(0, enemy.Resources - 15);
                s.ArmyStrength  = Math.Max(5, s.ArmyStrength - rng.Next(2, 6));

                // Only seize places adjacent to the dominion's own holdings to prevent
                // teleport-captures across the map (mirrors ANNEX's proximity logic).
                List<string> adjacentEnemyPlaces;
                try
                {
                    var topPlaces = manager.GetAllTopLvlPlaces() ?? new System.Collections.Generic.List<Place>();
                    var placeByUuid = topPlaces.Where(p => p != null)
                        .GroupBy(p => p.uuid).ToDictionary(g => g.Key, g => g.First());

                    const float ADJACENT_SQR = 250000f; // ~500 world-units squared
                    var ourPositions = s.Holdings.Keys
                        .Where(u => placeByUuid.ContainsKey(u))
                        .Select(u => placeByUuid[u].worldCoords)
                        .ToList();

                    adjacentEnemyPlaces = enemy.ClaimedPlaceUuids
                        .Where(u => placeByUuid.ContainsKey(u) && ourPositions
                            .Any(pos => (placeByUuid[u].worldCoords - pos).sqrMagnitude <= ADJACENT_SQR))
                        .ToList();

                    // If nothing qualifies as adjacent, fall back to any enemy place so the
                    // battle isn't entirely fruitless (e.g. enemy capital is far but isolated).
                    if (adjacentEnemyPlaces.Count == 0)
                        adjacentEnemyPlaces = enemy.ClaimedPlaceUuids.ToList();
                }
                catch
                {
                    adjacentEnemyPlaces = enemy.ClaimedPlaceUuids.ToList();
                }

                // A map-click target from DominionUI's pick mode overrides the random/adjacent pick,
                // as long as the enemy still actually holds it
                List<string> candidatePlaces =
                    !string.IsNullOrEmpty(preferredPlaceUuid) && enemy.ClaimedPlaceUuids.Contains(preferredPlaceUuid)
                        ? new List<string> { preferredPlaceUuid }
                        : adjacentEnemyPlaces;

                if (candidatePlaces.Count > 0)
                {
                    string seized = candidatePlaces[rng.Next(candidatePlaces.Count)];
                    enemy.ClaimedPlaceUuids.Remove(seized);
                    string placeName = ClaimPlaceByUuid(manager, seized) ?? "a territory";
                    WorldData.QueuePlayerEvent(
                        $"Your forces have seized {placeName} from {tName}!", "DOMINION_CONQUEST");
                    return $"Victory! The forces of {s.DominionName} crushed {tName}'s defenders and seized {placeName}.";
                }
                return $"Victory! {s.DominionName} routed {tName}'s forces in the field and plundered their supplies.";
            }

            s.ArmyStrength = Math.Max(0, s.ArmyStrength - rng.Next(4, 9));
            return $"Defeat — {tName} repelled the forces of {s.DominionName}. Your {GrandStrategyData.L.SoldiersNoun} limp home (strength {s.ArmyStrength}).";
        }

        private static string ResolveIncite(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            var rival = (manager.GetCurrentFactions() ?? new List<Faction>())
                .Where(f => f != null && f.uuid != target.uuid && f.uuid != s.FactionUuid
                            && f.GetPrettyName() != "Player"
                            && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid))
                .OrderBy(_ => rng.Next()).FirstOrDefault();
            if (rival == null) return $"!{target.GetPrettyName()} has no rivals left to turn against them.";

            string key = WorldData.GetRelationshipKey(target.uuid, rival.uuid);
            WorldData.AddGrievance(key, 2);
            WorldData.ShiftTier(key, -1, "agents provocateurs", target.GetPrettyName(), rival.GetPrettyName());
            return $"Agents of {s.DominionName} have sown discord between {target.GetPrettyName()} and {rival.GetPrettyName()}.";
        }

        private static string ResolveSabotage(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            var enemy = WorldData.GetFactionData(target.uuid);
            enemy.Resources = Math.Max(0, enemy.Resources - 20);
            string tName = target.GetPrettyName();

            // Discovery feeds the native rep pipeline — PlayerWorldActor's DeltaRep postfix
            // will count the grievance and can escalate to a bounty on its own.
            if (rng.NextDouble() < 0.35)
            {
                try { target.DeltaRep(-10f); } catch { }
                return $"Saboteurs of {s.DominionName} crippled {tName}'s stores — but were identified. {tName} knows who sent them.";
            }
            return $"Unmarked saboteurs wrecked {tName}'s stockpiles and supply lines. None can prove who sent them.";
        }

        private static string ResolvePillage(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (!WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return $"!You are not at war with {target.GetPrettyName()} — pillaging in peacetime is banditry.";
            if (s.ArmyStrength < 10) return "!Your army is too weak to pillage (levy troops first).";

            string tName = target.GetPrettyName();
            var enemy = WorldData.GetFactionData(target.uuid);
            int attack  = s.ArmyStrength + rng.Next(1, 21);
            int defense = enemy.Resources / 3 + enemy.Population / 100 + rng.Next(1, 21);

            if (attack >= defense)
            {
                int loot = rng.Next(20, 41);
                s.Treasury += loot;
                enemy.Resources = Math.Max(0, enemy.Resources - 15);
                s.ArmyStrength  = Math.Max(5, s.ArmyStrength - rng.Next(1, 4));
                WorldData.AddGrievance(key, 1);
                return $"Raiders of {s.DominionName} stripped {tName}'s outlying holdings, carrying off {loot} {GrandStrategyData.L.CurrencyWord} in plunder.";
            }

            s.ArmyStrength = Math.Max(0, s.ArmyStrength - rng.Next(3, 7));
            return $"{tName}'s defenders caught your raiders laden with loot — the plunder was lost and your forces mauled (strength {s.ArmyStrength}).";
        }

        private static string ResolvePeace(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (!WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return $"!You are not at war with {target.GetPrettyName()}.";

            string tName = target.GetPrettyName();
            var enemy = WorldData.GetFactionData(target.uuid);

            // Dominant victor extracts reparations rather than paying them.
            if (s.ArmyStrength > enemy.Resources / 3 + enemy.Population / 100)
            {
                int reparations = rng.Next(10, 26);
                s.Treasury    += reparations;
                enemy.Resources = Math.Max(0, enemy.Resources - reparations);
                WorldData.EndWar(key, $"{tName} sued for peace and paid reparations to {s.DominionName}");
                return $"{tName} accepted terms and paid {reparations} {GrandStrategyData.L.CurrencyWord} in reparations. The war is over."
                    ;
            }

            // Even match or losing position: we pay reparations
            enemy.Resources += 10;
            WorldData.EndWar(key, $"{s.DominionName} sued for peace and paid reparations");
            return $"{s.DominionName} has sued for peace with {tName}, paying reparations to end the war. Your people breathe easier."
                ;
        }

        private static string ResolveVassal(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";
            if (s.VassalFactionUuids.Contains(target.uuid))
                return $"!{target.GetPrettyName()} already answers to {s.DominionName}.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (!WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return $"!Submission is dictated by force — you must be at war with {target.GetPrettyName()}.";

            string tName = target.GetPrettyName();
            var enemy = WorldData.GetFactionData(target.uuid);
            if (enemy.Resources > 40 && enemy.Population > 300)
                return $"!{tName} still has fight left. Grind them down first (resources ≤ 40 or population ≤ 300).";
            if (s.ArmyStrength < 15)
                return "!Your army is too weak to enforce vassalage (strength 15+ required).";

            var L = GrandStrategyData.L;
            WorldData.EndWar(key, $"{tName} submitted to {s.DominionName}");
            WorldData.SetTier(key, DiplomaticTier.Alliance, "submission sworn",
                WorldData.CurrentState.CurrentTurn, s.DominionName, tName);
            s.VassalFactionUuids.Add(target.uuid);
            s.VassalNames[target.uuid] = tName;
            s.CasusBelli.Remove(target.uuid);
            WorldData.QueuePlayerEvent(
                $"{tName} has submitted! They are now a {L.VassalNoun} of {s.DominionName}, bound to pay tribute.",
                "DOMINION_VASSAL");
            GrandStrategyData.TryRecordChronicleBeat($"{tName} submitted to {s.DominionName}, becoming a {L.VassalNoun}.");
            return $"{tName} yields to {s.DominionName} — a {L.VassalNoun}, bound to tribute and loyalty.";
        }

        private static string ResolveFestival()
        {
            var s = GrandStrategyData.State;
            if (s.Holdings.Values.All(h => h.Unrest == 0))
                return "!Your people are already content — save the treasury for darker days.";

            foreach (var h in s.Holdings.Values)
                h.Unrest = Math.Max(0, h.Unrest - 15);
            return $"Celebrations sweep across {s.DominionName} — for a night, at least, the people's grievances are set aside.";
        }

        private static string ResolveProject(string arg)
        {
            var s = GrandStrategyData.State;
            string key = (arg ?? "").Trim().ToUpperInvariant();
            var wonder = WonderDefs.FirstOrDefault(w => w.Key == key);
            if (wonder == null)
                return "!Choose a great work: " + string.Join(" | ",
                    WonderDefs.Select(w => $"{w.Key} ({WonderDisplayName(w.Key)}, {w.Gold}{GrandStrategyData.L.CurrencyShort} — {w.Effect})"));

            if (s.Wonders.Contains(wonder.Key)) return $"!The {WonderDisplayName(wonder.Key)} already stands in {s.CapitalName}.";
            if (!string.IsNullOrEmpty(s.WonderInProgress))
                return $"!Your work crews are already raising the {WonderDisplayName(s.WonderInProgress)} ({s.WonderTicksLeft} tick(s) remain).";
            if (s.Treasury < wonder.Gold) return $"!Not enough treasury ({s.Treasury}/{wonder.Gold} needed for the {WonderDisplayName(wonder.Key)}).";

            s.Treasury -= wonder.Gold; // wonders have per-project costs, deducted here rather than via def.Gold
            s.WonderInProgress = wonder.Key;
            s.WonderTicksLeft  = wonder.Ticks;
            return $"Ground is broken in {s.CapitalName} — the {WonderDisplayName(wonder.Key)} rises ({wonder.Ticks} ticks to completion).";
        }

        private static string ResolveScout(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string tName = target.GetPrettyName();
            var tFac = WorldData.GetFactionData(target.uuid);

            // Intelligence quality varies: excellent scouting gives exact numbers, poor gives ranges
            bool preciseIntel = rng.NextDouble() < 0.55;
            string armyEst, resEst, popEst;
            if (preciseIntel)
            {
                armyEst = $"{tFac.Resources / 3 + tFac.Population / 100} (est.)"; // approx combat power
                resEst  = tFac.Resources.ToString();
                popEst  = tFac.Population.ToString();
            }
            else
            {
                // Deliberately noisy — scouts are human
                int noiseR = rng.Next(-15, 16);
                int noiseP = rng.Next(-50, 51);
                resEst  = $"~{Math.Max(0, tFac.Resources + noiseR)}";
                popEst  = $"~{Math.Max(0, tFac.Population + noiseP)}";
                armyEst = $"~{Math.Max(0, (tFac.Resources + noiseR) / 3 + (tFac.Population + noiseP) / 100)} (est.)";
            }

            string tier = WorldData.GetTierLabel(WorldData.GetTier(
                WorldData.GetRelationshipKey(s.FactionUuid, target.uuid)));
            bool atWar  = WorldData.CurrentState.ActiveWars.ContainsKey(
                WorldData.GetRelationshipKey(s.FactionUuid, target.uuid));

            string report = $"Scouts return from {tName}: Resources {resEst} | Population {popEst} | " +
                            $"Combat strength {armyEst} | Relations: {tier}{(atWar ? " (at war)" : "")}";

            WorldData.LogEvent($"{s.DominionName}'s agents brought back intelligence on {tName}.", "DOMINION");
            return report;
        }

        private static string ResolveDisband()
        {
            var s = GrandStrategyData.State;
            if (s.ArmyStrength < 10) return "!Your army is already too small to disband further (need 10+).";

            var fac = WorldData.GetFactionData(s.FactionUuid);
            s.ArmyStrength -= 10;
            fac.Population += 50; // veterans return to civilian life

            // Demobilisation eases unrest slightly (soldiers stop eating the realm's food)
            foreach (var h in s.Holdings.Values)
                h.Unrest = Math.Max(0, h.Unrest - 1);

            return $"{s.DominionName} has demobilised a company of veterans — army strength {s.ArmyStrength}, population +50.";
        }

        private static string ResolvePact(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return "!You cannot swear non-aggression with a faction you are at war with.";
            var tier = WorldData.GetTier(key);
            if (tier >= DiplomaticTier.NonAggression)
                return $"!{target.GetPrettyName()} already honors a non-aggression pact with {s.DominionName}.";
            if (tier < DiplomaticTier.ColdWar)
                return $"!Relations with {target.GetPrettyName()} are too poisoned for a pact — improve them first (ENVOY).";

            string tName = target.GetPrettyName();
            WorldData.SetTier(key, DiplomaticTier.NonAggression, "non-aggression pact sworn",
                WorldData.CurrentState.CurrentTurn, s.DominionName, tName);
            return $"{s.DominionName} and {tName} swear a pact of non-aggression — {GrandStrategyData.L.WeaponsNoun} stay lowered between them.";
        }

        private static string ResolveTradeDeal(GameplayManager manager, string arg)
        {
            var s = GrandStrategyData.State;
            Faction target = FindFaction(manager, arg);
            if (target == null) return "!No faction matches that name.";

            string key = WorldData.GetRelationshipKey(s.FactionUuid, target.uuid);
            if (WorldData.CurrentState.ActiveWars.ContainsKey(key))
                return "!You cannot open trade with a faction you are at war with.";
            var tier = WorldData.GetTier(key);
            if (tier >= DiplomaticTier.TradePact)
                return $"!{s.DominionName} already trades freely with {target.GetPrettyName()}.";
            if (tier < DiplomaticTier.NonAggression)
                return $"!{target.GetPrettyName()} won't open trade routes without a non-aggression pact first (PACT).";

            string tName = target.GetPrettyName();
            WorldData.SetTier(key, DiplomaticTier.TradePact, "trade pact signed",
                WorldData.CurrentState.CurrentTurn, s.DominionName, tName);
            return $"Trade now flows freely between {s.DominionName} and {tName} — a standing pact, {GrandStrategyData.L.CurrencyWord} every tick.";
        }

        private static string ResolveCouncil(string arg)
        {
            var s = GrandStrategyData.State;
            var L = GrandStrategyData.L;
            string role = (arg ?? "").Trim().ToUpperInvariant();
            if (!AdvisorRoles.Contains(role))
                return "!Choose an advisor role: " + string.Join(", ", AdvisorRoles);
            if (s.Advisors.Any(a => a.Role == role))
                return $"!{s.DominionName} already retains a {L.RoleTitle(role).ToLower()}.";

            var pool = (L.AdvisorNames != null && L.AdvisorNames.Count > 0)
                ? L.AdvisorNames : Themes.Build("GENERIC").AdvisorNames;
            string name = pool[rng.Next(pool.Count)];
            s.Advisors.Add(new Advisor { Role = role, Name = name, Personality = RolePersonality(role), Loyalty = 50 });
            return $"{name} joins {s.DominionName}'s {L.CourtNoun} as {L.RoleTitle(role)} — their counsel will shape which {L.PetitionNoun}s reach you.";
        }

        private static string RolePersonality(string role)
        {
            switch (role)
            {
                case "MARSHAL":    return "Blunt and battle-hardened; presses for strength over subtlety.";
                case "STEWARD":    return "Frugal and pragmatic; watches the treasury like a hawk.";
                case "SPYMASTER":  return "Guarded and watchful; trusts secrets more than soldiers.";
                case "CHANCELLOR": return "Smooth-tongued and image-conscious; minds your reputation and public standing.";
                default:           return "";
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        public static Faction FindFaction(GameplayManager manager, string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            var factions = manager.GetCurrentFactions();
            if (factions == null) return null;
            string needle = arg.Trim().ToUpperInvariant();
            return factions.FirstOrDefault(f =>
                f != null
                && f.uuid != GrandStrategyData.State.FactionUuid
                && f.GetPrettyName() != "Player"
                && !WorldData.CurrentState.EliminatedFactions.Contains(f.uuid)
                && f.GetPrettyName().ToUpperInvariant().Contains(needle));
        }

        // Adds a real Place to the dominion: WorldData claim list + native flip + holding record.
        public static void ClaimPlace(GameplayManager manager, Place place, bool isCapital = false)
        {
            var s = GrandStrategyData.State;
            var fac = WorldData.GetFactionData(s.FactionUuid);
            if (!fac.ClaimedPlaceUuids.Contains(place.uuid))
                fac.ClaimedPlaceUuids.Add(place.uuid);

            try
            {
                var playerFaction = (manager.GetCurrentFactions() ?? new List<Faction>())
                    .FirstOrDefault(f => f != null && f.uuid == s.FactionUuid);
                if (playerFaction != null) place.faction = playerFaction;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GrandStrategy] Could not flip native ownership: {e.Message}");
            }

            if (!s.Holdings.ContainsKey(place.uuid))
                s.Holdings[place.uuid] = new HoldingData { Name = place.GetPrettyName(), IsCapital = isCapital };
        }

        private static string ClaimPlaceByUuid(GameplayManager manager, string placeUuid)
        {
            try
            {
                if (SS.I != null && SS.I.uuidToGameEntityMap != null
                    && SS.I.uuidToGameEntityMap.TryGetValue(placeUuid, out var e) && e is Place place)
                {
                    ClaimPlace(manager, place);
                    return place.GetPrettyName();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GrandStrategy] Could not claim seized place: {ex.Message}");
            }
            return null;
        }
    }
}
