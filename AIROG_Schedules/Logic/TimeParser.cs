using System.Globalization;
using System.Text.RegularExpressions;

namespace AIROG_Schedules.Logic
{
    /// <summary>
    /// Forgiving time-of-day parsing: "8", "8:30", "08.30", "8h30", "0830", "8pm", "8:30 p.m.",
    /// "20:15", "noon", "midnight". The result is a minute of the day.
    ///
    /// Worlds can have their own day length (the game's CalendarConfig carries hours_per_day and
    /// minutes_per_hour), so both are parameters and range checks use them.
    /// </summary>
    public static class TimeParser
    {
        private static readonly Regex Suffix = new Regex(@"^(.*?)(?:\s*([ap])\.?(?:\s*m\.?)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex HourOnly = new Regex(@"^\d{1,2}$", RegexOptions.CultureInvariant);
        private static readonly Regex Compact = new Regex(@"^(\d{1,2})(\d{2})$", RegexOptions.CultureInvariant);
        private static readonly Regex Separated = new Regex(@"^(\d{1,3})(?:\s*[:.h]\s*|\s+)(\d{1,3})?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public const string Examples = "8, 8:30, 0830, 8pm or 20:15";

        public static bool TryParse(string text, int hoursPerDay, int minutesPerHour, out int minuteOfDay, out string error)
        {
            minuteOfDay = 0;
            error = null;
            if (hoursPerDay <= 0) hoursPerDay = 24;
            if (minutesPerHour <= 0) minutesPerHour = 60;

            string s = (text ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0)
            {
                error = "The time is empty. Try " + Examples + ".";
                return false;
            }

            if (s == "midnight") return true;
            if (s == "noon" || s == "midday")
            {
                minuteOfDay = (hoursPerDay / 2) * minutesPerHour;
                return true;
            }

            Match sm = Suffix.Match(s);
            string core = sm.Success ? sm.Groups[1].Value.Trim() : s;
            string meridiem = sm.Success ? sm.Groups[2].Value : "";

            int hour, minute = 0;
            Match m;
            if (HourOnly.IsMatch(core))
            {
                hour = ParseInt(core);
            }
            else if ((m = Compact.Match(core)).Success)
            {
                hour = ParseInt(m.Groups[1].Value);
                minute = ParseInt(m.Groups[2].Value);
            }
            else if ((m = Separated.Match(core)).Success)
            {
                hour = ParseInt(m.Groups[1].Value);
                minute = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? ParseInt(m.Groups[2].Value) : 0;
            }
            else
            {
                error = "\"" + text.Trim() + "\" is not a time of day. Try " + Examples + ".";
                return false;
            }

            if (minute >= minutesPerHour)
            {
                error = "\"" + text.Trim() + "\": minutes must be below " + minutesPerHour.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            if (meridiem.Length > 0)
            {
                // am/pm splits the day in two halves, which only means something for an even day.
                if (hoursPerDay % 2 != 0)
                {
                    error = "\"" + text.Trim() + "\": am/pm does not work with a " + hoursPerDay.ToString(CultureInfo.InvariantCulture)
                            + "-hour day. Use hour:minute instead.";
                    return false;
                }
                int half = hoursPerDay / 2;
                if (hour < 1 || hour > half)
                {
                    error = "\"" + text.Trim() + "\": with am/pm the hour must be 1 to " + half.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
                bool pm = meridiem == "p";
                if (hour == half) hour = pm ? half : 0;      // 12am = 00:00, 12pm = 12:00
                else if (pm) hour += half;
            }
            else if (hour == hoursPerDay && minute == 0)
            {
                hour = 0;                                     // "24:00" is midnight
            }

            if (hour >= hoursPerDay)
            {
                error = "\"" + text.Trim() + "\": the hour must be below " + hoursPerDay.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            minuteOfDay = hour * minutesPerHour + minute;
            return true;
        }

        /// <summary>"08:30" style, the canonical form stored in the save file and shown to the AI.</summary>
        public static string Format(int minuteOfDay, int minutesPerHour)
        {
            if (minutesPerHour <= 0) minutesPerHour = 60;
            if (minuteOfDay < 0) minuteOfDay = 0;
            int h = minuteOfDay / minutesPerHour;
            int m = minuteOfDay % minutesPerHour;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string digits)
        {
            int value;
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }
}
