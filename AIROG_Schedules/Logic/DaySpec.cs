using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIROG_Schedules.Logic
{
    /// <summary>
    /// Turns an entry's "days" list into a per-weekday on/off mask for the current week.
    ///
    /// Accepted per item: a weekday name (case-insensitive), an unambiguous prefix of one of at
    /// least three letters ("Mon"), a 1-based day number ("1" = first day of the week), or one of
    /// the every-day words below.
    /// </summary>
    public static class DaySpec
    {
        public const string EveryDay = "every day";

        private static readonly string[] EveryDayWords = { "every day", "everyday", "daily", "all", "all days", "any", "*" };

        public static bool IsEveryDayWord(string s)
        {
            string t = (s ?? "").Trim();
            foreach (string w in EveryDayWords)
                if (string.Equals(t, w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsEveryDay(IList<string> days)
        {
            if (days == null) return false;
            foreach (string d in days) if (IsEveryDayWord(d)) return true;
            return false;
        }

        /// <summary>
        /// Mask indexed by weekday. Items that match no weekday are returned in
        /// <paramref name="unknown"/> so the UI can point at them.
        /// </summary>
        public static bool[] Resolve(IList<string> days, IList<string> weekNames, out List<string> unknown)
        {
            int n = weekNames != null ? weekNames.Count : 0;
            var mask = new bool[n];
            unknown = new List<string>();
            if (days == null) return mask;

            foreach (string raw in days)
            {
                string d = (raw ?? "").Trim();
                if (d.Length == 0) continue;
                if (IsEveryDayWord(d))
                {
                    for (int i = 0; i < n; i++) mask[i] = true;
                    continue;
                }
                int idx = IndexOfDay(d, weekNames);
                if (idx >= 0) mask[idx] = true;
                else unknown.Add(d);
            }
            return mask;
        }

        public static bool AnyDay(bool[] mask)
        {
            if (mask == null) return false;
            foreach (bool b in mask) if (b) return true;
            return false;
        }

        public static bool AllDays(bool[] mask)
        {
            if (mask == null || mask.Length == 0) return false;
            foreach (bool b in mask) if (!b) return false;
            return true;
        }

        /// <summary>Weekday index for a name, prefix or 1-based number; -1 when it matches nothing.</summary>
        public static int IndexOfDay(string text, IList<string> weekNames)
        {
            if (weekNames == null) return -1;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return -1;

            for (int i = 0; i < weekNames.Count; i++)
                if (string.Equals((weekNames[i] ?? "").Trim(), t, StringComparison.OrdinalIgnoreCase)) return i;

            if (t.Length >= 3)
            {
                int hit = -1;
                for (int i = 0; i < weekNames.Count; i++)
                {
                    string name = (weekNames[i] ?? "").Trim();
                    if (name.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hit >= 0) { hit = -2; break; } // ambiguous
                        hit = i;
                    }
                }
                if (hit >= 0) return hit;
            }

            int number;
            if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                && number >= 1 && number <= weekNames.Count)
                return number - 1;

            return -1;
        }

        /// <summary>The list to store for a mask: the every-day token when all days are on.</summary>
        public static List<string> FromMask(bool[] mask, IList<string> weekNames)
        {
            var list = new List<string>();
            if (mask == null || weekNames == null) return list;
            if (AllDays(mask))
            {
                list.Add(EveryDay);
                return list;
            }
            for (int i = 0; i < mask.Length && i < weekNames.Count; i++)
                if (mask[i]) list.Add(weekNames[i]);
            return list;
        }

        /// <summary>Splits "Mon, Wed; Fri" into items.</summary>
        public static List<string> ParseList(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string part in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>Short text for the list UI: "every day", "Mon, Wed", "no days".</summary>
        public static string Describe(IList<string> days, IList<string> weekNames)
        {
            List<string> unknown;
            bool[] mask = Resolve(days, weekNames, out unknown);
            if (AllDays(mask)) return "every day";

            string[] shortNames = ShortNames(weekNames);
            var parts = new List<string>();
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) parts.Add(shortNames[i]);
            foreach (string u in unknown) parts.Add(u + "?");
            return parts.Count == 0 ? "no days" : string.Join(", ", parts.ToArray());
        }

        /// <summary>Three-letter forms when they are unique within the week, otherwise the full names.</summary>
        public static string[] ShortNames(IList<string> weekNames)
        {
            int n = weekNames != null ? weekNames.Count : 0;
            var full = new string[n];
            var shortened = new string[n];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unique = true;
            for (int i = 0; i < n; i++)
            {
                full[i] = (weekNames[i] ?? "").Trim();
                shortened[i] = full[i].Length > 3 ? full[i].Substring(0, 3) : full[i];
                if (!seen.Add(shortened[i])) unique = false;
            }
            return unique ? shortened : full;
        }
    }
}
