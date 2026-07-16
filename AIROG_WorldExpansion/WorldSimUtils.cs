using System;

namespace AIROG_WorldExpansion
{
    /// <summary>Small helpers shared by the world-simulation pieces split out of WorldSimulation.cs.</summary>
    internal static class WorldSimUtils
    {
        public static readonly Random Rng = new Random();

        public static bool ContainsAny(string text, params string[] keywords)
        {
            foreach (var k in keywords)
                if (text.Contains(k)) return true;
            return false;
        }

        // Whole-word matching for event text ("war" must not match "warm"/"reward").
        // Faction-name tagging uses ContainsAny instead, on purpose ("dark" should match "Darkmoor").
        public static bool ContainsAnyWord(string text, params string[] keywords)
        {
            foreach (var k in keywords)
                if (System.Text.RegularExpressions.Regex.IsMatch(text,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(k)}\b"))
                    return true;
            return false;
        }
    }
}
