using System;
using System.Collections.Generic;

// Everything under Logic\ is plain C#: no Unity, no game types, no Newtonsoft attributes.
// The Tests project compiles these files by link and runs them in a console.
namespace AIROG_Schedules.Logic
{
    public static class ScheduleKinds
    {
        /// <summary>Something happening at a place: class in session, market open.</summary>
        public const string Event = "event";

        /// <summary>Where the attached characters normally are: works at the forge 08:00-17:00.</summary>
        public const string Routine = "routine";

        public static string Normalize(string kind)
        {
            return string.Equals((kind ?? "").Trim(), Routine, StringComparison.OrdinalIgnoreCase) ? Routine : Event;
        }

        public static bool IsRoutine(string kind)
        {
            return Normalize(kind) == Routine;
        }
    }

    /// <summary>
    /// One schedule line. Field names are the JSON keys, kept lowercase and human-editable on
    /// purpose: players share these files and edit them by hand.
    /// </summary>
    public class ScheduleEntry
    {
        public string title = "";
        public string details = "";

        /// <summary>Name of a sub-location, location, region or world. Empty = anywhere.</summary>
        public string place = "";

        /// <summary>
        /// Weekday names this entry runs on, or the single token "every day". An empty list means
        /// the entry never runs. Names, not indexes, so a hand-edited or shared file stays readable;
        /// see <see cref="DaySpec"/> for what is accepted.
        /// </summary>
        public List<string> days = new List<string>();

        /// <summary>Time of day as text ("08:30"). Parsed forgivingly, see <see cref="TimeParser"/>.</summary>
        public string start = "00:00";

        /// <summary>End earlier than start = runs past midnight. End equal to start = a full day.</summary>
        public string end = "00:00";

        public List<string> npcs = new List<string>();
        public string kind = ScheduleKinds.Event;
        public bool enabled = true;

        public ScheduleEntry Clone()
        {
            return new ScheduleEntry
            {
                title = title,
                details = details,
                place = place,
                days = new List<string>(days ?? new List<string>()),
                start = start,
                end = end,
                npcs = new List<string>(npcs ?? new List<string>()),
                kind = kind,
                enabled = enabled
            };
        }

        /// <summary>Repairs what a hand-edited or partial JSON file can leave null.</summary>
        public void Sanitize()
        {
            title = (title ?? "").Trim();
            details = (details ?? "").Trim();
            place = (place ?? "").Trim();
            start = (start ?? "").Trim();
            end = (end ?? "").Trim();
            kind = ScheduleKinds.Normalize(kind);
            days = CleanList(days);
            npcs = CleanList(npcs);
        }

        private static List<string> CleanList(List<string> source)
        {
            var result = new List<string>();
            if (source == null) return result;
            foreach (string raw in source)
            {
                string s = (raw ?? "").Trim();
                if (s.Length == 0) continue;
                bool seen = false;
                foreach (string have in result)
                    if (string.Equals(have, s, StringComparison.OrdinalIgnoreCase)) { seen = true; break; }
                if (!seen) result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// True when two entries say the same thing (used to skip exact duplicates on import).
        /// Case, surrounding whitespace and list order are ignored; the on/off switch is not part
        /// of the comparison, so re-importing a file does not bring back a copy of an entry the
        /// player merely switched off.
        /// </summary>
        public static bool SameContent(ScheduleEntry a, ScheduleEntry b)
        {
            if (a == null || b == null) return a == b;
            return Eq(a.title, b.title)
                && Eq(a.details, b.details)
                && Eq(a.place, b.place)
                && Eq(a.start, b.start)
                && Eq(a.end, b.end)
                && ScheduleKinds.Normalize(a.kind) == ScheduleKinds.Normalize(b.kind)
                && SameSet(a.days, b.days)
                && SameSet(a.npcs, b.npcs);
        }

        private static bool Eq(string x, string y)
        {
            return string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameSet(List<string> x, List<string> y)
        {
            var sx = ToSet(x);
            var sy = ToSet(y);
            return sx.SetEquals(sy);
        }

        private static HashSet<string> ToSet(List<string> list)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (list == null) return set;
            foreach (string s in list)
            {
                string t = (s ?? "").Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }
    }

    /// <summary>schedules.json in the save folder (and the entries part of schedules_export.json).</summary>
    public class ScheduleFile
    {
        public int version = 1;

        /// <summary>
        /// Only used when the game's calendar has no day names of its own: weekday index of
        /// in-game day 0. See <see cref="ClockMath.WeekdayOf"/>.
        /// </summary>
        public int weekdayOffset;

        /// <summary>Only used in that same case. 0 = use the configured default week length.</summary>
        public int weekLength;

        public List<ScheduleEntry> entries = new List<ScheduleEntry>();

        public void Sanitize()
        {
            if (entries == null) entries = new List<ScheduleEntry>();
            entries.RemoveAll(e => e == null);
            foreach (ScheduleEntry e in entries) e.Sanitize();
            if (weekLength < 0) weekLength = 0;
        }

        /// <summary>Adds every incoming entry that is not already present. Returns (added, skipped).</summary>
        public int MergeFrom(IEnumerable<ScheduleEntry> incoming, out int skipped)
        {
            int added = 0;
            skipped = 0;
            if (incoming == null) return 0;
            foreach (ScheduleEntry raw in incoming)
            {
                if (raw == null) continue;
                ScheduleEntry candidate = raw.Clone();
                candidate.Sanitize();
                bool duplicate = false;
                foreach (ScheduleEntry have in entries)
                    if (ScheduleEntry.SameContent(have, candidate)) { duplicate = true; break; }
                if (duplicate) { skipped++; continue; }
                entries.Add(candidate);
                added++;
            }
            return added;
        }
    }
}
