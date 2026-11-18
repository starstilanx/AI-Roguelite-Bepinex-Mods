using System;
using System.Collections.Generic;
using System.Text;

namespace AIROG_Schedules.Logic
{
    /// <summary>
    /// "Is the player at or inside place X", by name. The chain handed in starts at the player's
    /// current sub-location and walks outward: parent locations, the top-level location, the
    /// region and the world.
    /// </summary>
    public static class PlaceMatcher
    {
        /// <summary>
        /// Comparison form of a place name: trimmed, inner whitespace collapsed, lower case, and a
        /// leading "the " dropped ("The Forge" and "forge" are the same place to a player).
        /// </summary>
        public static string Normalize(string name)
        {
            string s = NameMatcher.Normalize(name);
            if (s.StartsWith("the ", StringComparison.Ordinal) && s.Length > 4) s = s.Substring(4);
            return s;
        }

        /// <summary>An entry with no place (or "anywhere" / "*") applies wherever the player is.</summary>
        public static bool IsAnywhere(string place)
        {
            string s = Normalize(place);
            return s.Length == 0 || s == "*" || s == "anywhere" || s == "everywhere";
        }

        public static bool Same(string a, string b)
        {
            string na = Normalize(a);
            return na.Length > 0 && na == Normalize(b);
        }

        /// <summary>
        /// -1 = the player is not there. 0 = it is the player's current place (or the entry applies
        /// anywhere). n &gt; 0 = it is the n-th enclosing place in the chain (parent, ..., region, world).
        /// </summary>
        public static int MatchDepth(string entryPlace, IList<string> chain)
        {
            if (IsAnywhere(entryPlace)) return 0;
            if (chain == null) return -1;
            string want = Normalize(entryPlace);
            for (int i = 0; i < chain.Count; i++)
                if (Normalize(chain[i]) == want) return i;
            return -1;
        }

        public static bool ContainsName(IList<string> names, string place)
        {
            if (names == null) return false;
            string want = Normalize(place);
            if (want.Length == 0) return false;
            foreach (string n in names)
                if (Normalize(n) == want) return true;
            return false;
        }
    }

    public static class NameMatcher
    {
        /// <summary>Trimmed, inner whitespace runs collapsed to one space, lower case.</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool pendingSpace = false;
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// True when two names mean the same character: equal after normalizing, or one is a
        /// whole-word part of the other ("Tanaka" and "Ms. Tanaka"). The shorter side must be at
        /// least three characters so initials do not match everybody.
        /// </summary>
        public static bool SameCharacter(string a, string b)
        {
            string na = Normalize(a), nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            if (na == nb) return true;
            string shorter = na.Length <= nb.Length ? na : nb;
            string longer = na.Length <= nb.Length ? nb : na;
            return shorter.Length >= 3 && ContainsWholeWord(longer, shorter);
        }

        /// <summary>True when <paramref name="name"/> appears in <paramref name="text"/> as whole words.</summary>
        public static bool MentionedIn(string name, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string needle = (name ?? "").Trim();
            if (needle.Length < 2) return false;
            return ContainsWholeWord(text, needle);
        }

        private static bool ContainsWholeWord(string haystack, string needle)
        {
            int from = 0;
            while (from <= haystack.Length - needle.Length)
            {
                int at = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return false;
                int end = at + needle.Length;
                bool leftOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
                bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                if (leftOk && rightOk) return true;
                from = at + 1;
            }
            return false;
        }

        /// <summary>Splits "Aiko, Ren; Ms. Tanaka" into names, dropping blanks and repeats.</summary>
        public static List<string> ParseList(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string part in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                bool seen = false;
                foreach (string have in list)
                    if (Normalize(have) == Normalize(t)) { seen = true; break; }
                if (!seen) list.Add(t);
            }
            return list;
        }
    }
}
