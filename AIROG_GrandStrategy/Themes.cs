using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AIROG_GrandStrategy
{
    // The lexicon every player-facing string is voiced through, so the dominion layer
    // reads correctly in any setting (medieval, post-apocalyptic, sci-fi, modern...).
    // Serialized inside grand_strategy_data.json so GenContext's provider sees the same
    // terms without duplicating the preset tables.
    [Serializable]
    public class ThemeLexicon
    {
        public string Key           = "GENERIC";
        public string RulerTitle    = "leader";        // "the {RulerTitle} of {DominionName}"
        public string DomainNoun    = "domain";        // "across the {DomainNoun}"
        public string BannersNoun   = "banners";       // what gets raised over claimed land
        public string CurrencyWord  = "coin";          // prose ("30 coin for the treasury")
        public string CurrencyShort = "c";             // compact UI costs ("ANNEX 25c")
        public string WeaponsNoun   = "weapons";       // "{WeaponsNoun} stay lowered"
        public string SoldiersNoun  = "soldiers";
        public string CourtNoun     = "council";       // what advisors join
        public string PetitionNoun  = "petition";      // what subjects bring before you
        public string VassalNoun    = "tributary power";
        public string Icon          = "★";             // map-marker / panel glyph

        public Dictionary<string, string> RoleTitles  = new Dictionary<string, string>();  // MARSHAL → display title
        public Dictionary<string, string> WonderNames = new Dictionary<string, string>();  // CITADEL/MINT/TEMPLE → display name
        public List<string> AdvisorNames = new List<string>();

        public string RoleTitle(string role)
        {
            string t;
            return RoleTitles != null && RoleTitles.TryGetValue(role, out t) ? t : role;
        }

        public string WonderName(string key)
        {
            string n;
            return WonderNames != null && WonderNames.TryGetValue(key, out n) ? n : key;
        }
    }

    public static class Themes
    {
        public static readonly string[] Keys = { "MEDIEVAL", "POSTAPOC", "SCIFI", "MODERN", "GENERIC" };

        public static ThemeLexicon Build(string key)
        {
            switch ((key ?? "").ToUpperInvariant())
            {
                case "MEDIEVAL": return new ThemeLexicon
                {
                    Key = "MEDIEVAL", RulerTitle = "sovereign", DomainNoun = "realm",
                    BannersNoun = "banners", CurrencyWord = "gold", CurrencyShort = "g",
                    WeaponsNoun = "swords", SoldiersNoun = "troops", CourtNoun = "court",
                    PetitionNoun = "petition", VassalNoun = "vassal realm", Icon = "♛",
                    RoleTitles = new Dictionary<string, string> {
                        { "MARSHAL", "Marshal" }, { "STEWARD", "Steward" },
                        { "SPYMASTER", "Spymaster" }, { "CHANCELLOR", "Chancellor" } },
                    WonderNames = new Dictionary<string, string> {
                        { "CITADEL", "Grand Citadel" }, { "MINT", "Royal Mint" }, { "TEMPLE", "High Temple" } },
                    AdvisorNames = new List<string> {
                        "Aldric", "Brennus", "Cassia", "Dorvan", "Elowen", "Faelan",
                        "Guinevra", "Halric", "Ithera", "Jorund", "Kestrel", "Lysandra" },
                };
                case "POSTAPOC": return new ThemeLexicon
                {
                    Key = "POSTAPOC", RulerTitle = "commander", DomainNoun = "territory",
                    BannersNoun = "colors", CurrencyWord = "scrip", CurrencyShort = "s",
                    WeaponsNoun = "guns", SoldiersNoun = "fighters", CourtNoun = "inner circle",
                    PetitionNoun = "appeal", VassalNoun = "tributary faction", Icon = "★",
                    RoleTitles = new Dictionary<string, string> {
                        { "MARSHAL", "War Captain" }, { "STEWARD", "Quartermaster" },
                        { "SPYMASTER", "Informant Chief" }, { "CHANCELLOR", "Negotiator" } },
                    WonderNames = new Dictionary<string, string> {
                        { "CITADEL", "Fortified Compound" }, { "MINT", "Trade Hub" }, { "TEMPLE", "Sanctuary" } },
                    AdvisorNames = new List<string> {
                        "Rook", "Cinder", "Flint", "Mara", "Havoc", "Sable",
                        "Grit", "Yuri", "Vesper", "Boyd", "Ash", "Nadia" },
                };
                case "SCIFI": return new ThemeLexicon
                {
                    Key = "SCIFI", RulerTitle = "administrator", DomainNoun = "sector",
                    BannersNoun = "insignia", CurrencyWord = "credits", CurrencyShort = "cr",
                    WeaponsNoun = "weapons", SoldiersNoun = "troopers", CourtNoun = "command staff",
                    PetitionNoun = "petition", VassalNoun = "client state", Icon = "★",
                    RoleTitles = new Dictionary<string, string> {
                        { "MARSHAL", "Force Commander" }, { "STEWARD", "Logistics Chief" },
                        { "SPYMASTER", "Intelligence Director" }, { "CHANCELLOR", "Diplomatic Officer" } },
                    WonderNames = new Dictionary<string, string> {
                        { "CITADEL", "Defense Grid" }, { "MINT", "Central Exchange" }, { "TEMPLE", "Unity Spire" } },
                    AdvisorNames = new List<string> {
                        "Vex", "Orin", "Zara", "Kaidan", "Nyx", "Talos",
                        "Rhea", "Cassian", "Dax", "Lyra", "Io", "Sol" },
                };
                case "MODERN": return new ThemeLexicon
                {
                    Key = "MODERN", RulerTitle = "leader", DomainNoun = "territory",
                    BannersNoun = "flags", CurrencyWord = "funds", CurrencyShort = "$",
                    WeaponsNoun = "guns", SoldiersNoun = "soldiers", CourtNoun = "cabinet",
                    PetitionNoun = "appeal", VassalNoun = "client faction", Icon = "★",
                    RoleTitles = new Dictionary<string, string> {
                        { "MARSHAL", "General" }, { "STEWARD", "Treasurer" },
                        { "SPYMASTER", "Intelligence Chief" }, { "CHANCELLOR", "Chief of Staff" } },
                    WonderNames = new Dictionary<string, string> {
                        { "CITADEL", "Fortified Headquarters" }, { "MINT", "Central Bank" }, { "TEMPLE", "Grand Forum" } },
                    AdvisorNames = new List<string> {
                        "Marcus", "Elena", "Reyes", "Sofia", "Dmitri", "Hana",
                        "Cole", "Priya", "Viktor", "Amara", "Jonas", "Petra" },
                };
                default: return new ThemeLexicon
                {
                    Key = "GENERIC", RulerTitle = "leader", DomainNoun = "domain",
                    BannersNoun = "banners", CurrencyWord = "coin", CurrencyShort = "c",
                    WeaponsNoun = "weapons", SoldiersNoun = "soldiers", CourtNoun = "council",
                    PetitionNoun = "petition", VassalNoun = "tributary power", Icon = "★",
                    RoleTitles = new Dictionary<string, string> {
                        { "MARSHAL", "War Chief" }, { "STEWARD", "Steward" },
                        { "SPYMASTER", "Spymaster" }, { "CHANCELLOR", "Envoy" } },
                    WonderNames = new Dictionary<string, string> {
                        { "CITADEL", "Great Bastion" }, { "MINT", "Grand Exchange" }, { "TEMPLE", "Great Sanctum" } },
                    AdvisorNames = new List<string> {
                        "Arden", "Bracha", "Corin", "Della", "Emrys", "Farrow",
                        "Gale", "Hollis", "Imara", "Joss", "Kae", "Lior" },
                };
            }
        }

        // ─── Genre auto-detection ─────────────────────────────────────────────────
        // Scores keyword hits in the universe name/description + world backstory.
        // Ties or zero hits fall through to GENERIC, which reads fine anywhere.

        private static readonly Dictionary<string, string[]> Keywords = new Dictionary<string, string[]>
        {
            { "POSTAPOC", new[] { "post-apoc", "postapoc", "apocaly", "wasteland", "nuclear", "radiat",
                "irradiat", "mutant", "fallout", "stalker", "chernobyl", "anomal", "scaveng",
                "raider", "bunker", "vault", "the zone", "wastes", "collapse of civilization" } },
            { "SCIFI", new[] { "sci-fi", "scifi", "space", "galax", "starship", "spaceship", "planet",
                "colony ship", "cyber", "android", "alien", "orbital", "federation", "futurist",
                "mech", "neon", "megacorp", "terraform", "warp", "hyperspace", "cyborg" } },
            { "MODERN", new[] { "modern", "modern-day", "present day", "contemporary", "21st century",
                "20th century", "police", "cartel", "mafia", "detective", "corporate", "office",
                "high school", "urban", "gang war", "cold war", "world war" } },
            { "MEDIEVAL", new[] { "medieval", "kingdom", "castle", "knight", "dragon", "wizard",
                "sorcer", "magic", "feudal", "throne", "fantasy", "elf", "elves", "dwarf",
                "dwarves", "sword", "quest", "dungeon", "bard", "king ", "queen " } },
        };

        public static string DetectKey(GameplayManager manager)
        {
            string blob = "";
            try
            {
                var uni = manager?.GetCurrentUniverse();
                if (uni != null)
                    blob += (uni.name ?? "") + " " + (uni.GetPotentiallyNullDescription() ?? "") + " ";
                var vw = manager?.GetCurrentVoronoiWorld();
                if (vw != null)
                    blob += (vw.name ?? "") + " " + (vw.worldBackstory ?? "");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GrandStrategy] Theme detection could not read world text: {e.Message}");
            }
            if (string.IsNullOrWhiteSpace(blob)) return "GENERIC";
            blob = blob.ToLowerInvariant();

            string bestKey = "GENERIC";
            int bestScore = 0;
            foreach (var kvp in Keywords)
            {
                int score = kvp.Value.Count(k => blob.Contains(k));
                if (score > bestScore) { bestScore = score; bestKey = kvp.Key; }
            }
            return bestKey;
        }

        // Applies a theme to the state. "AUTO" re-runs detection. The world's own
        // AI-generated currency name (if any) overrides the preset's currency words.
        public static string Apply(DominionState s, string key, GameplayManager manager)
        {
            if ((key ?? "").ToUpperInvariant() == "AUTO") key = DetectKey(manager);
            var lex = Build(key);

            try
            {
                string nativeCurrency = manager?.GetCurrentOrDefaultCurrency()?.nme?.Trim();
                if (!string.IsNullOrEmpty(nativeCurrency) && nativeCurrency.Length <= 20)
                {
                    lex.CurrencyWord  = nativeCurrency.ToLowerInvariant();
                    lex.CurrencyShort = nativeCurrency.Substring(0, 1).ToLowerInvariant();
                }
            }
            catch { }

            s.Lex = lex;
            return lex.Key;
        }

        // Legacy saves (and mid-save installs) have no lexicon yet — detect once, lazily,
        // at the first point a manager is in hand.
        public static void EnsureTheme(DominionState s, GameplayManager manager)
        {
            if (s == null || !s.Founded) return;
            if (s.Lex != null && !string.IsNullOrEmpty(s.Lex.Key)) return;
            string picked = Apply(s, "AUTO", manager);
            Debug.Log($"[GrandStrategy] Auto-detected dominion theme: {picked}");
            GrandStrategyData.SaveToCurrentDir();
        }
    }
}
