using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AIROG_Multiplayer.Util
{
    /// <summary>
    /// Parses and rolls dice expressions in NdM+K format (e.g., 2d6+3, 1d20, 4d8-2).
    /// </summary>
    public static class DiceRoller
    {
        private static readonly Random _rng = new Random();
        private static readonly Regex _diceRegex = new Regex(
            @"^(\d+)d(\d+)([+-]\d+)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public struct DiceResult
        {
            public string Expression;
            public int NumDice;
            public int NumSides;
            public int Modifier;
            public int[] Rolls;
            public int Total;
        }

        /// <summary>
        /// Tries to parse a dice expression like "2d6+3".
        /// Returns true if valid, false otherwise.
        /// </summary>
        public static bool TryParse(string input, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            var m = _diceRegex.Match(input.Trim());
            if (!m.Success) return false;

            int n = int.Parse(m.Groups[1].Value);
            int sides = int.Parse(m.Groups[2].Value);
            if (n < 1 || n > 100 || sides < 1 || sides > 1000) return false;

            int mod = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            normalized = $"{n}d{sides}" + (mod > 0 ? $"+{mod}" : mod < 0 ? mod.ToString() : "");
            return true;
        }

        /// <summary>
        /// Rolls the given dice expression and returns the result.
        /// </summary>
        public static DiceResult Roll(string expression)
        {
            var m = _diceRegex.Match(expression.Trim());
            if (!m.Success)
                throw new ArgumentException($"Invalid dice expression: {expression}");

            int numDice = int.Parse(m.Groups[1].Value);
            int numSides = int.Parse(m.Groups[2].Value);
            int modifier = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;

            numDice = Math.Min(numDice, 100);
            numSides = Math.Min(numSides, 1000);

            var rolls = new int[numDice];
            int sum = 0;
            for (int i = 0; i < numDice; i++)
            {
                rolls[i] = _rng.Next(1, numSides + 1);
                sum += rolls[i];
            }

            string norm = $"{numDice}d{numSides}" + (modifier > 0 ? $"+{modifier}" : modifier < 0 ? modifier.ToString() : "");

            return new DiceResult
            {
                Expression = norm,
                NumDice = numDice,
                NumSides = numSides,
                Modifier = modifier,
                Rolls = rolls,
                Total = sum + modifier
            };
        }

        /// <summary>
        /// Formats a DiceResult into a display string like "[4, 2] + 3 = 9".
        /// </summary>
        public static string FormatResult(DiceResult result)
        {
            string rolls = "[" + string.Join(", ", result.Rolls) + "]";
            if (result.Modifier != 0)
            {
                string modStr = result.Modifier > 0 ? $" + {result.Modifier}" : $" - {Math.Abs(result.Modifier)}";
                return $"{rolls}{modStr} = {result.Total}";
            }
            return $"{rolls} = {result.Total}";
        }
    }
}
