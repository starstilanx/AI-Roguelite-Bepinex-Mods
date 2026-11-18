using System.Collections.Generic;

namespace AIROG_Schedules.Logic
{
    public enum EntryStatus
    {
        /// <summary>Not running, and neither about to start nor just over.</summary>
        Inactive,
        Active,
        StartingSoon,
        JustEnded
    }

    /// <summary>What one entry is doing at one moment.</summary>
    public sealed class EntryState
    {
        public EntryStatus Status = EntryStatus.Inactive;

        /// <summary>False when the entry can never run as written. <see cref="Problem"/> says why.</summary>
        public bool Runnable;
        public string Problem;

        /// <summary>Day names in the entry that the current week does not have.</summary>
        public List<string> UnknownDays = new List<string>();

        public int StartMinute;
        public int EndMinute;
        public int DurationMinutes;
        public bool AllDay;

        /// <summary>Active: minutes until this occurrence ends.</summary>
        public int MinutesRemaining;
        /// <summary>StartingSoon: minutes until it starts.</summary>
        public int MinutesUntilStart;
        /// <summary>JustEnded: minutes since it ended.</summary>
        public int MinutesSinceEnd;
    }

    /// <summary>
    /// Decides whether an entry's window contains "now".
    ///
    /// Rules (all tested in Tests\Program.cs):
    ///  - The window is start &lt;= now &lt; end. At exactly the end time it has just ended.
    ///  - End earlier than start: the window runs past midnight.
    ///  - End equal to start: a full day, from that time to the same time on the next day.
    ///  - A day flag belongs to the day the window STARTS on. "Friday 23:00-02:00" is on at 00:30
    ///    on Saturday because it began on Friday, and is NOT on at 00:30 on Friday unless Thursday
    ///    is flagged too. This is how people read "Friday night".
    ///  - No days selected: never runs.
    ///  - The on/off switch is not looked at here; callers skip disabled entries.
    /// </summary>
    public static class ScheduleEngine
    {
        public static EntryState Evaluate(ScheduleEntry entry, ClockInfo clock, int soonWindowMinutes)
        {
            var state = new EntryState();
            if (entry == null || clock == null)
            {
                state.Problem = "missing entry";
                return state;
            }

            int minutesPerDay = clock.MinutesPerDay;
            string error;
            int start, end;
            if (!TimeParser.TryParse(entry.start, clock.HoursPerDay, clock.MinutesPerHour, out start, out error))
            {
                state.Problem = "start time: " + error;
                return state;
            }
            if (!TimeParser.TryParse(entry.end, clock.HoursPerDay, clock.MinutesPerHour, out end, out error))
            {
                state.Problem = "end time: " + error;
                return state;
            }

            int duration = end - start;
            if (duration <= 0) duration += minutesPerDay;   // past midnight, or start == end = full day
            state.StartMinute = start;
            state.EndMinute = end;
            state.DurationMinutes = duration;
            state.AllDay = duration >= minutesPerDay;

            bool[] mask = DaySpec.Resolve(entry.days, clock.WeekdayNames, out state.UnknownDays);
            if (!DaySpec.AnyDay(mask))
            {
                state.Problem = state.UnknownDays.Count > 0
                    ? "no day of this world's week matches: " + string.Join(", ", state.UnknownDays.ToArray())
                    : "no days selected";
                return state;
            }
            state.Runnable = true;

            int window = soonWindowMinutes;
            if (window < 0) window = 0;
            if (window > minutesPerDay - 1) window = minutesPerDay - 1;

            long now = clock.NowAbsMinutes;
            int bestSoon = int.MaxValue, bestEnded = int.MaxValue;

            // An occurrence that matters now started at most two days back (a full-day window that
            // began the day before yesterday can have ended minutes ago) or starts tomorrow.
            for (long day = clock.DayIndex - 2; day <= clock.DayIndex + 1; day++)
            {
                if (!mask[ClockMath.WeekdayOf(day, clock.WeekOffset, clock.WeekLength)]) continue;
                long occStart = day * minutesPerDay + start;
                long occEnd = occStart + duration;

                if (now >= occStart && now < occEnd)
                {
                    state.Status = EntryStatus.Active;
                    state.MinutesRemaining = (int)(occEnd - now);
                    return state;
                }
                if (now < occStart && occStart - now <= window)
                {
                    int until = (int)(occStart - now);
                    if (until < bestSoon) bestSoon = until;
                }
                else if (now >= occEnd && now - occEnd <= window)
                {
                    int since = (int)(now - occEnd);
                    if (since < bestEnded) bestEnded = since;
                }
            }

            if (bestSoon != int.MaxValue)
            {
                state.Status = EntryStatus.StartingSoon;
                state.MinutesUntilStart = bestSoon;
            }
            else if (bestEnded != int.MaxValue)
            {
                state.Status = EntryStatus.JustEnded;
                state.MinutesSinceEnd = bestEnded;
            }
            return state;
        }
    }
}
