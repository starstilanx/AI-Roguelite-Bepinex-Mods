using System.Collections.Generic;

namespace AIROG_Schedules.Logic
{
    /// <summary>Turns what the player typed into the edit form into an entry, or says what is wrong.</summary>
    public static class EntryForm
    {
        /// <summary>
        /// Returns null and a message for the player when a value cannot be used. Days come either
        /// from <paramref name="dayMask"/> (one tick box per weekday) or, when
        /// <paramref name="daysText"/> is not null, from a comma separated list.
        /// </summary>
        public static ScheduleEntry Build(string title, string details, string place, string start, string end,
            string npcs, string daysText, bool routine, bool enabled, bool[] dayMask, ClockInfo clock, out string error)
        {
            error = null;
            title = (title ?? "").Trim();
            if (title.Length == 0)
            {
                error = "Give the entry a short title.";
                return null;
            }

            int startMinute, endMinute;
            string timeError;
            if (!TimeParser.TryParse(start, clock.HoursPerDay, clock.MinutesPerHour, out startMinute, out timeError))
            {
                error = "Start time: " + timeError;
                return null;
            }
            if (!TimeParser.TryParse(end, clock.HoursPerDay, clock.MinutesPerHour, out endMinute, out timeError))
            {
                error = "End time: " + timeError;
                return null;
            }

            List<string> names = NameMatcher.ParseList(npcs);
            if (routine && names.Count == 0)
            {
                error = "A routine says where characters are: name at least one character, or pick Event.";
                return null;
            }

            List<string> days;
            if (daysText != null)
            {
                List<string> unknown;
                bool[] mask = DaySpec.Resolve(DaySpec.ParseList(daysText), clock.WeekdayNames, out unknown);
                if (unknown.Count > 0)
                {
                    error = "Unknown day \"" + unknown[0] + "\". Use this world's day names, or: every day.";
                    return null;
                }
                days = DaySpec.FromMask(mask, clock.WeekdayNames);
            }
            else
            {
                days = DaySpec.FromMask(dayMask, clock.WeekdayNames);
            }
            if (days.Count == 0)
            {
                error = "Tick at least one day. To park an entry, untick Enabled instead.";
                return null;
            }

            var entry = new ScheduleEntry
            {
                title = title,
                details = details ?? "",
                place = place ?? "",
                days = days,
                // Stored in one canonical form, whatever the player typed ("8pm" becomes "20:00").
                start = TimeParser.Format(startMinute, clock.MinutesPerHour),
                end = TimeParser.Format(endMinute, clock.MinutesPerHour),
                npcs = names,
                kind = routine ? ScheduleKinds.Routine : ScheduleKinds.Event,
                enabled = enabled
            };
            entry.Sanitize();
            return entry;
        }
    }
}
