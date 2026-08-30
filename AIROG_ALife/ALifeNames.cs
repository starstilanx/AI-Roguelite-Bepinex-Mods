using System;
using System.Collections.Generic;

namespace AIROG_ALife
{
    /// <summary>
    /// Genre-neutral squad naming and canned member descriptions. No AI calls — the
    /// narrative AI embellishes these via the GenContext provider; these just need to
    /// be evocative enough to seed it. (GrandStrategy lesson: keep the lexicon universal
    /// so it works in fantasy, sci-fi, and modern scenarios alike.)
    /// v2.0 adds leader names (syllable-built so they fit any setting) and earned epithets.
    /// </summary>
    public static class ALifeNames
    {
        private static readonly Random Rng = new Random();

        private static readonly string[] HunterPackAdjectives =
            { "Ashen", "Red", "Pale", "Silent", "Hollow", "Rabid", "Grey", "Black", "Withered", "Feral" };
        private static readonly string[] HunterPackNouns =
            { "Fangs", "Shadows", "Howlers", "Gnashers", "Stalkers", "Claws", "Hides", "Prowlers" };

        private static readonly string[] RaiderBandNouns =
            { "marauders", "reavers", "outcasts", "cutthroats", "scavengers", "renegades" };

        private static readonly string[] PilgrimNouns =
            { "wanderers", "pilgrims", "drifters", "refugees", "nomads" };

        private static string Pick(string[] arr) => arr[Rng.Next(arr.Length)];

        public static string SquadName(string archetype, string factionName, string homePlaceName)
        {
            switch (archetype)
            {
                case SquadArchetype.PATROL:
                    return factionName + " patrol";
                case SquadArchetype.WARBAND:
                    return factionName + " warband";
                case SquadArchetype.CARAVAN:
                    return string.IsNullOrEmpty(factionName)
                        ? "trade caravan out of " + homePlaceName
                        : factionName + " caravan";
                case SquadArchetype.RAIDERS:
                    return "the " + ALifeSimulation.Cap(homePlaceName) + " " + Pick(RaiderBandNouns);
                case SquadArchetype.HUNTERS:
                    return "the " + Pick(HunterPackAdjectives) + " " + Pick(HunterPackNouns);
                case SquadArchetype.PILGRIMS:
                    return "a band of " + Pick(PilgrimNouns);
                default:
                    return "an unknown band";
            }
        }

        // ── v2.0 leaders ─────────────────────────────────────────────────────────

        private static readonly string[] NameOnsets =
            { "Var", "Kel", "Dra", "Mor", "Tal", "Ser", "Bran", "Gor", "Ash", "Rul",
              "Nev", "Jor", "Kas", "Ver", "Ola", "Dun", "Mir", "Sza", "Tho", "Yev" };
        private static readonly string[] NameEnds =
            { "ek", "an", "ra", "is", "od", "un", "eth", "ar", "ik", "os", "a", "en", "ur", "im", "yx" };

        private static readonly string[] BeastNameA =
            { "Scar", "Grey", "Blood", "Broke", "White", "Lame", "Iron", "Dusk", "One-Eye", "Long" };
        private static readonly string[] BeastNameB =
            { "jaw", "hide", "fang", "claw", "pelt", "maw", "shank", "tail" };

        /// <summary>Kill-count epithet milestones. Beast leaders (HUNTERS) get their own ladder.</summary>
        private static readonly (int kills, string manEpithet, string beastEpithet)[] KillEpithets =
        {
            (25, "the Reaper",   "the Terror"),
            (12, "Bloodhand",    "Man-Eater"),
            (5,  "the Red",      "the Scarred"),
        };

        public static string PersonName() => Pick(NameOnsets) + Pick(NameEnds);
        public static string BeastName() => Pick(BeastNameA) + Pick(BeastNameB);

        public static string LeaderRole(string archetype)
        {
            switch (archetype)
            {
                case SquadArchetype.PATROL:  return "sergeant";
                case SquadArchetype.WARBAND: return "war leader";
                case SquadArchetype.CARAVAN: return "caravan master";
                case SquadArchetype.RAIDERS: return "ringleader";
                case SquadArchetype.HUNTERS: return "alpha";
                default:                     return "elder";
            }
        }

        public static SquadLeader MakeLeader(VirtualSquad squad)
        {
            bool beast = squad.Archetype == SquadArchetype.HUNTERS;
            return new SquadLeader
            {
                Name = beast ? BeastName() : PersonName(),
                Role = LeaderRole(squad.Archetype)
            };
        }

        /// <summary>Highest kill-milestone epithet earned, or null if none newer than current.</summary>
        public static string EpithetForKills(string archetype, int kills)
        {
            bool beast = archetype == SquadArchetype.HUNTERS;
            foreach (var (needed, man, bst) in KillEpithets)
                if (kills >= needed)
                    return beast ? bst : man;
            return null;
        }

        /// <summary>Member roles used when a squad materializes into real GameCharacters.</summary>
        public static string[] MemberRoles(string archetype)
        {
            switch (archetype)
            {
                case SquadArchetype.PATROL:  return new[] { "sergeant", "guard", "scout" };
                case SquadArchetype.WARBAND: return new[] { "war leader", "veteran", "skirmisher" };
                case SquadArchetype.CARAVAN: return new[] { "caravan master", "guard", "porter" };
                case SquadArchetype.RAIDERS: return new[] { "ringleader", "brute", "lookout" };
                case SquadArchetype.HUNTERS: return new[] { "alpha", "hunter", "stray" };
                default:                     return new[] { "elder", "traveler", "straggler" };
            }
        }

        public static string MemberName(VirtualSquad squad, string role)
        {
            switch (squad.Archetype)
            {
                case SquadArchetype.HUNTERS:
                    // "alpha of the Ashen Fangs"
                    return role + " of " + squad.Name;
                case SquadArchetype.PILGRIMS:
                    return "wandering " + role;
                default:
                    // "Redwater patrol sergeant"
                    return squad.Name + " " + role;
            }
        }

        public static string MemberDesc(VirtualSquad squad, string role)
        {
            switch (squad.Archetype)
            {
                case SquadArchetype.PATROL:
                    return $"A {role} of the {squad.FactionName}, part of a patrol sweeping the area. Watchful and disciplined, wearing their faction's colors.";
                case SquadArchetype.WARBAND:
                    return $"A hardened {role} marching with a {squad.FactionName} war party. Armed for battle and hostile to their faction's enemies.";
                case SquadArchetype.CARAVAN:
                    return $"A {role} traveling with {squad.Name}, moving goods along the roads. More interested in profit than trouble.";
                case SquadArchetype.RAIDERS:
                    return $"A dangerous {role} of {squad.Name}, living off ambush and plunder. Eyes anyone who passes as a potential mark.";
                case SquadArchetype.HUNTERS:
                    return $"A predatory creature of {squad.Name}, a wild pack that has been roaming and hunting through the region.";
                default:
                    return $"A weary {role} traveling with {squad.Name}, drifting from place to place.";
            }
        }

        /// <summary>Description for the leader when materialized — carries the dossier into the game.</summary>
        public static string LeaderDesc(VirtualSquad squad)
        {
            var l = squad.Leader;
            string record = l.Kills > 0
                ? $" {l.Kills} foes have fallen to this band under their leadership."
                : "";
            string veteran = l.Victories >= 3 ? " A veteran of many battles." : "";
            if (squad.Archetype == SquadArchetype.HUNTERS)
                return $"{l.FullName}, the alpha of {squad.Name} — the largest and most cunning of the pack.{record}";
            return $"{l.FullName}, {l.Role} of {squad.Name}.{veteran}{record} " +
                   MemberDesc(squad, l.Role);
        }

        public static string ActivityLine(VirtualSquad s)
        {
            switch (s.GoalType)
            {
                case SquadGoal.RAID:   return "moving to raid " + (s.TargetPlaceName ?? "a nearby settlement");
                case SquadGoal.FLEE:   return "fleeing after a defeat";
                case SquadGoal.TRADE:  return "running a trade route";
                case SquadGoal.HUNT:   return "hunting a sworn enemy";
                case SquadGoal.GARRISON: return "dug in and holding this ground";
                case SquadGoal.TRAVEL_TO: return "traveling toward " + (s.TargetPlaceName ?? "parts unknown");
                default: return s.Archetype == SquadArchetype.HUNTERS ? "prowling for prey" : "roaming the area";
            }
        }
    }
}
