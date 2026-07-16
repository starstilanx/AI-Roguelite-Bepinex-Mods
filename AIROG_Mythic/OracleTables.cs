using System;
using System.Collections.Generic;

namespace AIROG_Mythic
{
    /// <summary>
    /// The dice tables: the 1e-style Fate Chart (11 odds × 9 CF), exceptional-result
    /// math, the event focus table, and original Action/Descriptor inspiration-word
    /// lists (Mythic-spirited but our own words — no Word Mill table text).
    /// </summary>
    public static class OracleTables
    {
        // ── Odds levels ─────────────────────────────────────────────────────────

        public static readonly string[] OddsNames =
        {
            "Impossible", "No Way", "Very Unlikely", "Unlikely", "50/50",
            "Somewhat Likely", "Likely", "Very Likely", "Near Sure Thing",
            "A Sure Thing", "Has To Be"
        };

        /// <summary>Parse a console odds token → odds index (0–10), or −1.</summary>
        public static int ParseOdds(string token)
        {
            switch ((token ?? "").ToUpperInvariant().Replace("_", "").Replace("-", ""))
            {
                case "IMPOSSIBLE": return 0;
                case "NOWAY": return 1;
                case "VERYUNLIKELY": case "VU": return 2;
                case "UNLIKELY": return 3;
                case "5050": case "FIFTY": case "EVEN": return 4;
                case "SOMEWHAT": case "SOMEWHATLIKELY": case "SL": return 5;
                case "LIKELY": return 6;
                case "VERYLIKELY": case "VL": return 7;
                case "NEARSURE": case "NEARSURETHING": case "NS": return 8;
                case "SURE": case "SURETHING": return 9;
                case "HASTO": case "HASTOBE": return 10;
                default: return -1;
            }
        }

        /// <summary>Fate Chart YES thresholds: [odds 0–10, CF 1–9 → index 0–8]. d100 ≤ threshold = YES.</summary>
        private static readonly int[,] FateChart =
        {
            {  1,  1,  1,  2,  4,  7, 10, 13, 16 }, // Impossible
            {  1,  2,  4,  7, 10, 13, 16, 20, 25 }, // No Way
            {  3,  5,  9, 14, 20, 26, 32, 40, 50 }, // Very Unlikely
            {  5,  9, 15, 22, 30, 38, 46, 55, 65 }, // Unlikely
            { 10, 15, 22, 32, 50, 68, 78, 85, 90 }, // 50/50
            { 15, 22, 32, 45, 60, 72, 80, 87, 93 }, // Somewhat Likely
            { 20, 30, 45, 60, 70, 80, 87, 93, 97 }, // Likely
            { 35, 50, 62, 72, 80, 87, 93, 97, 99 }, // Very Likely
            { 55, 68, 78, 85, 90, 94, 97, 99,100 }, // Near Sure Thing
            { 78, 85, 90, 94, 97, 99,100,100,100 }, // A Sure Thing
            { 95, 97, 99,100,100,100,100,100,100 }, // Has To Be
        };

        public static int Threshold(int oddsIdx, int cf)
        {
            oddsIdx = Math.Max(0, Math.Min(10, oddsIdx));
            cf = Math.Max(1, Math.Min(9, cf));
            return FateChart[oddsIdx, cf - 1];
        }

        /// <summary>Exceptional YES band: roll ≤ this (0 = impossible to get).</summary>
        public static int ExceptionalYesMax(int threshold)
        {
            return threshold <= 0 ? 0 : Math.Max(1, threshold / 5);
        }

        /// <summary>Exceptional NO band: roll ≥ this (101 = impossible). Top 20% of the NO range.</summary>
        public static int ExceptionalNoMin(int threshold)
        {
            if (threshold >= 100) return 101;
            int noRange = 100 - threshold;                       // rolls threshold+1 .. 100
            return threshold + 1 + (int)Math.Floor(noRange * 0.8);
        }

        public static string ResolveFate(int oddsIdx, int cf, int roll, out bool yes, out bool exceptional)
        {
            int t = Threshold(oddsIdx, cf);
            yes = roll <= t;
            exceptional = yes ? roll <= ExceptionalYesMax(t) : roll >= ExceptionalNoMin(t);
            return (exceptional ? "EXCEPTIONAL " : "") + (yes ? "YES" : "NO");
        }

        /// <summary>Random-event trigger (published 1e rule): d100 doubles whose digit ≤ CF.</summary>
        public static bool IsEventTrigger(int d100Roll, int cf)
        {
            return d100Roll % 11 == 0 && d100Roll >= 11 && d100Roll <= 99 && d100Roll / 11 <= cf;
        }

        // ── Event focus (d100) ──────────────────────────────────────────────────

        public const string F_REMOTE = "REMOTE EVENT";
        public const string F_NPC_ACTION = "NPC ACTION";
        public const string F_NEW_NPC = "NEW ARRIVAL";
        public const string F_THREAD_TOWARD = "GOAL ADVANCES";
        public const string F_THREAD_AWAY = "GOAL SETBACK";
        public const string F_THREAD_CLOSE = "GOAL RESOLVES";
        public const string F_PC_NEG = "PC NEGATIVE";
        public const string F_PC_POS = "PC POSITIVE";
        public const string F_AMBIGUOUS = "AMBIGUOUS EVENT";
        public const string F_NPC_NEG = "NPC NEGATIVE";
        public const string F_NPC_POS = "NPC POSITIVE";

        /// <summary>Roll → (focus, significant).</summary>
        public static string ResolveFocus(int d100, out bool significant)
        {
            significant = false;
            if (d100 <= 7) return F_REMOTE;
            if (d100 <= 15) return F_NPC_ACTION;
            if (d100 <= 20) return F_NEW_NPC;
            if (d100 <= 30) return F_THREAD_TOWARD;
            if (d100 <= 35) return F_THREAD_AWAY;
            if (d100 <= 45) return F_THREAD_CLOSE;
            if (d100 <= 50) return F_PC_NEG;
            if (d100 <= 55) return F_PC_POS;
            if (d100 <= 67) return F_AMBIGUOUS;
            if (d100 <= 75) return F_NPC_NEG;
            if (d100 <= 83) return F_NPC_POS;
            if (d100 <= 92) { significant = true; return F_THREAD_TOWARD; }
            significant = true; return F_NEW_NPC;
        }

        // ── Inspiration words (original lists, 100 each) ───────────────────────

        public static readonly string[] ActionWords =
        {
            "Abandon", "Ambush", "Awaken", "Bargain", "Beckon", "Besiege", "Bless", "Block",
            "Break", "Burn", "Bury", "Celebrate", "Challenge", "Chase", "Cheat", "Claim",
            "Cleanse", "Collapse", "Command", "Conceal", "Confess", "Confront", "Conspire",
            "Corrupt", "Crave", "Crown", "Curse", "Defend", "Demand", "Depart", "Desert",
            "Destroy", "Discover", "Disguise", "Divide", "Doubt", "Dream", "Drift", "Embrace",
            "Endure", "Escape", "Escort", "Expose", "Falter", "Feast", "Flee", "Follow",
            "Forge", "Forgive", "Gather", "Grieve", "Guard", "Haunt", "Heal", "Hide",
            "Hoard", "Honor", "Hunt", "Ignite", "Imprison", "Inherit", "Invade", "Judge",
            "Kneel", "Liberate", "Lure", "Mend", "Mock", "Mourn", "Offer", "Oppose",
            "Overthrow", "Plead", "Plot", "Poison", "Pray", "Preserve", "Promise", "Protect",
            "Provoke", "Punish", "Pursue", "Rally", "Ransom", "Rebuild", "Reclaim", "Refuse",
            "Renounce", "Rescue", "Return", "Reveal", "Sabotage", "Scatter", "Seal", "Seize",
            "Shelter", "Silence", "Smuggle", "Summon", "Surrender", "Warn"
        };

        public static readonly string[] DescriptorWords =
        {
            "Ashes", "Authority", "Balance", "Barriers", "Beasts", "Beginnings", "Blood",
            "Bones", "Borders", "Chains", "Champions", "Children", "Commerce", "Corruption",
            "Courage", "Crossroads", "Darkness", "Debts", "Decay", "Deception", "Devotion",
            "Disease", "Doubt", "Dreams", "Dust", "Echoes", "Elders", "Endings", "Enemies",
            "Exile", "Faith", "Fame", "Family", "Famine", "Fear", "Fire", "Fortune",
            "Freedom", "Ghosts", "Gifts", "Graves", "Greed", "Grief", "Guardians", "Healing",
            "Heirs", "History", "Home", "Honor", "Hope", "Hunger", "Hunters", "Illusions",
            "Innocence", "Journeys", "Justice", "Keys", "Kin", "Knowledge", "Law", "Legends",
            "Loyalty", "Madness", "Masks", "Memory", "Mercy", "Messengers", "Monuments",
            "Mysteries", "Oaths", "Obsession", "Omens", "Outsiders", "Passages", "Patience",
            "Power", "Pride", "Prisoners", "Prophecy", "Rebellion", "Refuge", "Relics",
            "Rivals", "Roads", "Ruins", "Rumors", "Sacrifice", "Sanctuary", "Scars",
            "Secrets", "Shadows", "Silence", "Smoke", "Songs", "Sorrow", "Spies",
            "Storms", "Strangers", "Temptation", "Territory", "Thieves", "Thresholds",
            "Thrones", "Tides", "Traditions", "Treasure", "Trust", "Truth", "Vengeance",
            "Vows", "Walls", "War", "Wealth", "Weapons", "Whispers", "Wilderness", "Wounds"
        };

        /// <summary>Roll a meaning pair, e.g. "Pursue + Debts".</summary>
        public static string RollMeaning()
        {
            string a = ActionWords[UnityEngine.Random.Range(0, ActionWords.Length)];
            string d = DescriptorWords[UnityEngine.Random.Range(0, DescriptorWords.Length)];
            return a + " + " + d;
        }
    }
}
