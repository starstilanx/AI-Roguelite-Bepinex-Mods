using System.Collections.Generic;
using System.Globalization;

namespace AIROG_Schedules.Logic
{
    /// <summary>The in-game "now" as the mod sees it: absolute day, minute of that day, weekday.</summary>
    public sealed class ClockInfo
    {
        /// <summary>Absolute in-game day number, 0-based (elapsed whole days since the calendar's epoch).</summary>
        public long DayIndex;

        public int MinuteOfDay;
        public int MinutesPerHour = 60;
        public int HoursPerDay = 24;

        public int WeekLength = 7;

        /// <summary>Weekday index of day 0. Weekday of day d = (d + WeekOffset) mod WeekLength.</summary>
        public int WeekOffset;

        /// <summary>One name per weekday index; always WeekLength long.</summary>
        public string[] WeekdayNames = ClockMath.BuildNames(null, 7);

        public int MinutesPerDay { get { return MinutesPerHour * HoursPerDay; } }
        public long NowAbsMinutes { get { return DayIndex * MinutesPerDay + MinuteOfDay; } }
        public int WeekdayIndex { get { return ClockMath.WeekdayOf(DayIndex, WeekOffset, WeekLength); } }

        public string WeekdayName
        {
            get
            {
                int i = WeekdayIndex;
                return WeekdayNames != null && i < WeekdayNames.Length ? WeekdayNames[i] : "Day " + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        public string TimeText { get { return TimeParser.Format(MinuteOfDay, MinutesPerHour); } }
    }

    public static class ClockMath
    {
        // A sanity bound only. The game's new-game screen stops at 100 day names, but the in-game
        // calendar editor does not, and the weekday must keep agreeing with the game's own modulo.
        public const int MaxWeekLength = 1000;

        /// <summary>
        /// Splits elapsed in-game seconds into day and minute-of-day. Same arithmetic as the game's
        /// TimeHandler.GetDateFromElapsed (seconds/60, then minutes_per_hour, then hours_per_day),
        /// without its month loop, so the result always agrees with the clock on the HUD.
        /// </summary>
        public static ClockInfo FromElapsedSeconds(long elapsedSeconds, int minutesPerHour, int hoursPerDay,
            int weekLength, int weekOffset, IList<string> weekdayNames)
        {
            if (elapsedSeconds < 0) elapsedSeconds = 0;
            if (minutesPerHour <= 0) minutesPerHour = 60;
            if (hoursPerDay <= 0) hoursPerDay = 24;
            weekLength = ClampWeekLength(weekLength);

            long totalMinutes = elapsedSeconds / 60;
            long totalHours = totalMinutes / minutesPerHour;
            long day = totalHours / hoursPerDay;
            int minute = (int)(totalMinutes % minutesPerHour);
            int hour = (int)(totalHours % hoursPerDay);

            return new ClockInfo
            {
                DayIndex = day,
                MinuteOfDay = hour * minutesPerHour + minute,
                MinutesPerHour = minutesPerHour,
                HoursPerDay = hoursPerDay,
                WeekLength = weekLength,
                WeekOffset = weekOffset,
                WeekdayNames = BuildNames(weekdayNames, weekLength)
            };
        }

        public static int ClampWeekLength(int weekLength)
        {
            if (weekLength < 1) return 1;
            return weekLength > MaxWeekLength ? MaxWeekLength : weekLength;
        }

        /// <summary>Weekday index (0-based) of an absolute day. Safe for negative days and offsets.</summary>
        public static int WeekdayOf(long dayIndex, int weekOffset, int weekLength)
        {
            weekLength = ClampWeekLength(weekLength);
            long r = (dayIndex + weekOffset) % weekLength;
            if (r < 0) r += weekLength;
            return (int)r;
        }

        /// <summary>The offset that makes <paramref name="dayIndex"/> fall on <paramref name="desiredWeekday"/>.</summary>
        public static int OffsetForToday(long dayIndex, int desiredWeekday, int weekLength)
        {
            weekLength = ClampWeekLength(weekLength);
            long r = (desiredWeekday - dayIndex) % weekLength;
            if (r < 0) r += weekLength;
            return (int)r;
        }

        /// <summary>
        /// Exactly <paramref name="weekLength"/> names. Missing or blank names become "Day N", so a
        /// week longer than the configured name list (or a calendar with a blank day name) still
        /// has something to show and to match against.
        /// </summary>
        public static string[] BuildNames(IList<string> source, int weekLength)
        {
            weekLength = ClampWeekLength(weekLength);
            var names = new string[weekLength];
            for (int i = 0; i < weekLength; i++)
            {
                string n = source != null && i < source.Count ? (source[i] ?? "").Trim() : "";
                names[i] = n.Length > 0 ? n : "Day " + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            return names;
        }
    }
}
