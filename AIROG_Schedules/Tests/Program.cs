using System;
using System.Collections.Generic;
using System.Linq;
using AIROG_Schedules.Logic;

namespace AIROG_Schedules.Tests
{
    /// <summary>
    /// Console tests for the pure schedule logic. Exit code = number of failed checks.
    ///   dotnet run --project AIROG_Schedules\Tests\AIROG_Schedules.Tests.csproj
    ///   dotnet run --project AIROG_Schedules\Tests\AIROG_Schedules.Tests.csproj -- --sample   (also prints sample blocks)
    /// </summary>
    internal static class Program
    {
        private static int _passed, _failed;
        private static string _group = "";

        private static readonly string[] Week = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        private const int Mon = 0, Tue = 1, Wed = 2, Thu = 3, Fri = 4, Sat = 5, Sun = 6;

        private static int Main(string[] args)
        {
            bool verbose = args.Contains("-v");
            Run("TimeParser", TimeParserTests);
            Run("ClockMath", ClockMathTests);
            Run("DaySpec", DaySpecTests);
            Run("PlaceMatcher", PlaceMatcherTests);
            Run("NameMatcher", NameMatcherTests);
            Run("ScheduleEngine", EngineTests);
            Run("EntryForm", EntryFormTests);
            Run("ScheduleModel", ModelTests);
            Run("BlockBuilder (school)", SchoolBlockTests);
            Run("BlockBuilder (routines, caps, budget)", BlockEdgeTests);

            if (verbose || args.Contains("--sample"))
            {
                Console.WriteLine();
                Console.WriteLine("---- sample block: Monday 12:10, Cafeteria, Ms. Tanaka present ----");
                Console.WriteLine(School("Cafeteria", Mon, "12:10", new[] { "Ms. Tanaka", "Kenji" }, "").Trim('\n'));
                Console.WriteLine();
                Console.WriteLine("---- sample block: Wednesday 16:00, Debate Club Room ----");
                Console.WriteLine(School("Debate Club Room", Wed, "16:00", new[] { "Aiko" }, "").Trim('\n'));
                Console.WriteLine();
                Console.WriteLine("---- sample block: Monday 23:30, Corridor ----");
                Console.WriteLine(School("Corridor", Mon, "23:30", new string[0], "").Trim('\n'));
            }

            Console.WriteLine();
            Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed, " + (_passed + _failed) + " checks.");
            return _failed;
        }

        // ---- harness ---------------------------------------------------------------------------

        private static void Run(string group, Action tests)
        {
            _group = group;
            int beforePass = _passed, beforeFail = _failed;
            try { tests(); }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL [" + group + "] threw " + ex);
            }
            Console.WriteLine((_failed == beforeFail ? "ok   " : "FAIL ") + group + ": " + (_passed - beforePass) + " passed, " + (_failed - beforeFail) + " failed");
        }

        private static void Check(string name, bool condition)
        {
            if (condition) { _passed++; return; }
            _failed++;
            Console.WriteLine("  FAIL [" + _group + "] " + name);
        }

        private static void Eq<T>(string name, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) { _passed++; return; }
            _failed++;
            Console.WriteLine("  FAIL [" + _group + "] " + name + ": expected <" + expected + "> but was <" + actual + ">");
        }

        private static void Contains(string name, string text, string part)
        {
            if (text != null && text.Contains(part)) { _passed++; return; }
            _failed++;
            Console.WriteLine("  FAIL [" + _group + "] " + name + ": missing <" + part + "> in:\n" + text);
        }

        private static void Lacks(string name, string text, string part)
        {
            if (text == null || !text.Contains(part)) { _passed++; return; }
            _failed++;
            Console.WriteLine("  FAIL [" + _group + "] " + name + ": should not contain <" + part + "> in:\n" + text);
        }

        // ---- builders --------------------------------------------------------------------------

        private static int T(string time)
        {
            int minute; string error;
            if (!TimeParser.TryParse(time, 24, 60, out minute, out error)) throw new ArgumentException(error);
            return minute;
        }

        /// <summary>Standard world: 24h x 60min, Monday-first week, day 0 is a Monday.</summary>
        private static ClockInfo Clock(long day, string time, int offset = 0)
        {
            return ClockMath.FromElapsedSeconds((day * 1440L + T(time)) * 60L, 60, 24, 7, offset, Week);
        }

        private static ScheduleEntry Entry(string title, string place, string days, string start, string end,
            string kind = ScheduleKinds.Event, string npcs = "", string details = "")
        {
            return new ScheduleEntry
            {
                title = title,
                place = place,
                days = DaySpec.ParseList(days),
                start = start,
                end = end,
                kind = kind,
                npcs = NameMatcher.ParseList(npcs),
                details = details
            };
        }

        private static EntryState Eval(ScheduleEntry e, long day, string time, int window = 30)
        {
            return ScheduleEngine.Evaluate(e, Clock(day, time), window);
        }

        // ---- TimeParser ------------------------------------------------------------------------

        private static void TimeParserTests()
        {
            var good = new Dictionary<string, int>
            {
                { "8", 480 }, { "8:30", 510 }, { "08:30", 510 }, { "0830", 510 }, { "830", 510 }, { "8pm", 1200 },
                { "20:15", 1215 }, { "2015", 1215 }, { "8.30", 510 }, { "8h30", 510 }, { "8h", 480 }, { "8 30", 510 },
                { "8:30 PM", 1230 }, { "8:30pm", 1230 }, { "8 p.m.", 1200 }, { "8 a.m.", 480 }, { "8a", 480 }, { "8p", 1200 },
                { "12am", 0 }, { "12pm", 720 }, { "12:30am", 30 }, { "12:30pm", 750 }, { "noon", 720 }, { "Midnight", 0 },
                { "24:00", 0 }, { "24", 0 }, { "0", 0 }, { "00:00", 0 }, { "23:59", 1439 }, { "  7:05  ", 425 }, { "7:5", 425 },
                { "11PM", 1380 }, { "1 am", 60 }, { "0000", 0 }, { "2359", 1439 }
            };
            foreach (var kv in good)
            {
                int minute; string error;
                bool ok = TimeParser.TryParse(kv.Key, 24, 60, out minute, out error);
                Check("parses \"" + kv.Key + "\"" + (ok ? "" : " (" + error + ")"), ok);
                Eq("value of \"" + kv.Key + "\"", kv.Value, minute);
            }

            string[] bad = { "", "   ", null, "abc", "25:00", "24:01", "8:60", "25:99", "2599", "-5", "13pm", "0pm", "8:30:15", "12345",
                             "pm", "8am30", "8::30", "eight", "8,30", "24pm", "99" };
            foreach (string s in bad)
            {
                int minute; string error;
                bool ok = TimeParser.TryParse(s, 24, 60, out minute, out error);
                Check("rejects \"" + s + "\"", !ok);
                Check("message for \"" + s + "\"", !ok && !string.IsNullOrEmpty(error));
            }

            {
                int minute; string error;
                TimeParser.TryParse("25:99", 24, 60, out minute, out error);
                Contains("error quotes the input", error, "25:99");
            }

            // A world with 20 hours of 50 minutes.
            {
                int minute; string error;
                Check("custom day 19:49", TimeParser.TryParse("19:49", 20, 50, out minute, out error));
                Eq("custom day 19:49 value", 19 * 50 + 49, minute);
                Check("custom day 20:00 is midnight", TimeParser.TryParse("20:00", 20, 50, out minute, out error) && minute == 0);
                Check("custom day rejects minute 50", !TimeParser.TryParse("19:50", 20, 50, out minute, out error));
                Check("custom day rejects hour 21", !TimeParser.TryParse("21:00", 20, 50, out minute, out error));
                Check("custom day 3pm = hour 13", TimeParser.TryParse("3pm", 20, 50, out minute, out error) && minute == 13 * 50);
                Check("custom day 10pm = midday", TimeParser.TryParse("10pm", 20, 50, out minute, out error) && minute == 10 * 50);
                Check("custom day rejects 11pm", !TimeParser.TryParse("11pm", 20, 50, out minute, out error));
                Check("odd day rejects am/pm", !TimeParser.TryParse("3pm", 25, 60, out minute, out error));
                Check("odd day accepts 24:30", TimeParser.TryParse("24:30", 25, 60, out minute, out error) && minute == 24 * 60 + 30);
            }

            Eq("format 510", "08:30", TimeParser.Format(510, 60));
            Eq("format 0", "00:00", TimeParser.Format(0, 60));
            Eq("format 1439", "23:59", TimeParser.Format(1439, 60));
            bool roundTrip = true;
            for (int m = 0; m < 1440; m++)
            {
                int back; string error;
                if (!TimeParser.TryParse(TimeParser.Format(m, 60), 24, 60, out back, out error) || back != m) { roundTrip = false; break; }
            }
            Check("format/parse round trip for every minute", roundTrip);
        }

        // ---- ClockMath -------------------------------------------------------------------------

        private static void ClockMathTests()
        {
            ClockInfo c = ClockMath.FromElapsedSeconds(0, 60, 24, 7, 0, Week);
            Eq("epoch day", 0L, c.DayIndex);
            Eq("epoch minute", 0, c.MinuteOfDay);
            Eq("epoch weekday", "Monday", c.WeekdayName);

            c = ClockMath.FromElapsedSeconds((3 * 1440L + 12 * 60 + 10) * 60L + 59, 60, 24, 7, 0, Week);
            Eq("day 3", 3L, c.DayIndex);
            Eq("12:10", 730, c.MinuteOfDay);
            Eq("time text", "12:10", c.TimeText);
            Eq("day 3 = Thursday", "Thursday", c.WeekdayName);
            Eq("abs minutes", 3 * 1440L + 730, c.NowAbsMinutes);

            Eq("offset 2 makes day 0 a Wednesday", "Wednesday", ClockMath.FromElapsedSeconds(0, 60, 24, 7, 2, Week).WeekdayName);
            Eq("offset wraps", "Monday", ClockMath.FromElapsedSeconds(0, 60, 24, 7, 14, Week).WeekdayName);

            // The game's own formula: (anchor_day_offset + days) % day_names.Count
            bool agrees = true;
            var rng = new Random(12345);
            for (int i = 0; i < 2000; i++)
            {
                long secs = (long)(rng.NextDouble() * 4e10);
                int mph = 30 + rng.Next(90), hpd = 10 + rng.Next(30), n = 1 + rng.Next(12), anchor = rng.Next(20);
                long totalMin = secs / 60, totalHours = totalMin / mph, days = totalHours / hpd;
                int gameWeekday = (int)((anchor + days) % n);
                ClockInfo k = ClockMath.FromElapsedSeconds(secs, mph, hpd, n, anchor, null);
                if (k.WeekdayIndex != gameWeekday || k.DayIndex != days
                    || k.MinuteOfDay != (int)(totalHours % hpd) * mph + (int)(totalMin % mph)) { agrees = false; break; }
            }
            Check("matches the game's day / hour / minute / weekday arithmetic", agrees);

            Eq("5-day week, day 7", 2, ClockMath.WeekdayOf(7, 0, 5));
            Eq("10-day week, day 23, offset 4", 7, ClockMath.WeekdayOf(23, 4, 10));
            Eq("negative offset", 6, ClockMath.WeekdayOf(0, -1, 7));
            Eq("negative day", 6, ClockMath.WeekdayOf(-1, 0, 7));
            Eq("week length 0 does not divide by zero", 0, ClockMath.WeekdayOf(5, 3, 0));

            bool offsets = true;
            for (int len = 1; len <= 12 && offsets; len++)
                for (long day = 0; day < 40 && offsets; day++)
                    for (int want = 0; want < len; want++)
                        if (ClockMath.WeekdayOf(day, ClockMath.OffsetForToday(day, want, len), len) != want) { offsets = false; break; }
            Check("OffsetForToday makes today the wanted weekday (all lengths 1-12)", offsets);
            Check("OffsetForToday stays inside the week", ClockMath.OffsetForToday(1234, 3, 7) >= 0 && ClockMath.OffsetForToday(1234, 3, 7) < 7);

            c = ClockMath.FromElapsedSeconds((2L * 20 * 50 + 7 * 50 + 13) * 60L, 50, 20, 6, 0, null);
            Eq("custom day length: day", 2L, c.DayIndex);
            Eq("custom day length: minute of day", 7 * 50 + 13, c.MinuteOfDay);
            Eq("custom day length: minutes per day", 1000, c.MinutesPerDay);
            Eq("custom day length: time text", "07:13", c.TimeText);

            string[] names = ClockMath.BuildNames(new[] { "Sol", " ", "Luna" }, 5);
            Eq("names length", 5, names.Length);
            Eq("blank name becomes Day 2", "Day 2", names[1]);
            Eq("kept name", "Luna", names[2]);
            Eq("missing name becomes Day 5", "Day 5", names[4]);

            c = ClockMath.FromElapsedSeconds(-50, 0, 0, 0, 0, null);
            Check("bad inputs fall back to a sane clock", c.MinutesPerDay == 1440 && c.WeekLength == 1 && c.DayIndex == 0);
        }

        // ---- DaySpec ---------------------------------------------------------------------------

        private static void DaySpecTests()
        {
            List<string> unknown;
            bool[] m = DaySpec.Resolve(new[] { "Monday", " wednesday " }, Week, out unknown);
            Check("two named days", m[Mon] && m[Wed] && m.Count(b => b) == 2 && unknown.Count == 0);

            foreach (string word in new[] { "every day", "Everyday", "DAILY", "*", "all" })
                Check("every-day word \"" + word + "\"", DaySpec.AllDays(DaySpec.Resolve(new[] { word }, Week, out unknown)));

            m = DaySpec.Resolve(new[] { "Mon", "wed", "Thu" }, Week, out unknown);
            Check("three-letter prefixes", m[Mon] && m[Wed] && m[Thu] && unknown.Count == 0);
            m = DaySpec.Resolve(new[] { "T", "Tu", "S" }, Week, out unknown);
            Check("short or ambiguous prefixes are unknown", !DaySpec.AnyDay(m) && unknown.Count == 3);
            m = DaySpec.Resolve(new[] { "Tue" }, Week, out unknown);
            Check("Tue is unambiguous", m[Tue] && unknown.Count == 0);

            m = DaySpec.Resolve(new[] { "1", "7" }, Week, out unknown);
            Check("day numbers are 1-based", m[Mon] && m[Sun] && m.Count(b => b) == 2);
            m = DaySpec.Resolve(new[] { "0", "8" }, Week, out unknown);
            Check("out-of-range numbers are unknown", !DaySpec.AnyDay(m) && unknown.Count == 2);

            m = DaySpec.Resolve(new[] { "Montag" }, Week, out unknown);
            Check("foreign name is reported", !DaySpec.AnyDay(m) && unknown.Count == 1 && unknown[0] == "Montag");

            Check("empty list = no day", !DaySpec.AnyDay(DaySpec.Resolve(new string[0], Week, out unknown)));
            Check("null list = no day", !DaySpec.AnyDay(DaySpec.Resolve(null, Week, out unknown)));

            var all = new bool[7];
            for (int i = 0; i < 7; i++) all[i] = true;
            List<string> stored = DaySpec.FromMask(all, Week);
            Check("all days are stored as the every-day token", stored.Count == 1 && stored[0] == DaySpec.EveryDay);
            stored = DaySpec.FromMask(new[] { true, false, true, false, false, false, false }, Week);
            Check("some days are stored by name", stored.SequenceEqual(new[] { "Monday", "Wednesday" }));
            Check("no days stores nothing", DaySpec.FromMask(new bool[7], Week).Count == 0);

            Eq("describe every day", "every day", DaySpec.Describe(new[] { "every day" }, Week));
            Eq("describe all seven by name", "every day", DaySpec.Describe(Week, Week));
            Eq("describe two", "Mon, Wed", DaySpec.Describe(new[] { "Wednesday", "monday" }, Week));
            Eq("describe none", "no days", DaySpec.Describe(new string[0], Week));
            Eq("describe unknown", "Mon, Montag?", DaySpec.Describe(new[] { "Monday", "Montag" }, Week));

            string[] clash = DaySpec.ShortNames(new[] { "Sunday", "Sunset", "Moonday" });
            Eq("clashing short names fall back to full names", "Sunset", clash[1]);

            Check("list parsing", DaySpec.ParseList("Mon, Wed; Fri\n").SequenceEqual(new[] { "Mon", "Wed", "Fri" }));

            // A five-day week with its own names.
            string[] five = { "Ash", "Birch", "Cedar", "Dusk", "Elm" };
            m = DaySpec.Resolve(new[] { "cedar", "5" }, five, out unknown);
            Check("custom week names", m[2] && m[4] && m.Length == 5);
            m = DaySpec.Resolve(new[] { "Monday" }, five, out unknown);
            Check("earth day in a custom week is unknown", !DaySpec.AnyDay(m) && unknown.Count == 1);
        }

        // ---- matchers --------------------------------------------------------------------------

        private static void PlaceMatcherTests()
        {
            var chain = new List<string> { "Debate Club Room", "Seika Academy", "Hoshino Ward", "Japan" };
            Eq("current sub-location", 0, PlaceMatcher.MatchDepth("Debate Club Room", chain));
            Eq("parent location", 1, PlaceMatcher.MatchDepth("Seika Academy", chain));
            Eq("region", 2, PlaceMatcher.MatchDepth("Hoshino Ward", chain));
            Eq("world", 3, PlaceMatcher.MatchDepth("Japan", chain));
            Eq("somewhere else", -1, PlaceMatcher.MatchDepth("Gym", chain));

            Eq("case and whitespace", 0, PlaceMatcher.MatchDepth("  debate   CLUB room\t", chain));
            Eq("ancestor, case and whitespace", 1, PlaceMatcher.MatchDepth("seika  academy", chain));
            Eq("leading 'the' on the entry", 0, PlaceMatcher.MatchDepth("The Debate Club Room", chain));
            Eq("leading 'the' on the game name", 0, PlaceMatcher.MatchDepth("forge", new[] { "The Forge" }));
            Eq("partial name does not match", -1, PlaceMatcher.MatchDepth("Debate Club", chain));
            Eq("word inside a name does not match", -1, PlaceMatcher.MatchDepth("Academy", chain));

            Eq("blank = anywhere", 0, PlaceMatcher.MatchDepth("", chain));
            Eq("spaces = anywhere", 0, PlaceMatcher.MatchDepth("   ", chain));
            Eq("null = anywhere", 0, PlaceMatcher.MatchDepth(null, chain));
            Eq("'anywhere' = anywhere", 0, PlaceMatcher.MatchDepth("Anywhere", chain));
            Eq("anywhere with no chain", 0, PlaceMatcher.MatchDepth("", new List<string>()));
            Eq("named place with no chain", -1, PlaceMatcher.MatchDepth("Gym", new List<string>()));
            Eq("named place with null chain", -1, PlaceMatcher.MatchDepth("Gym", null));

            var unnamed = new List<string> { "", "Seika Academy" };
            Eq("unnamed current place never matches a name", -1, PlaceMatcher.MatchDepth("Gym", unnamed));
            Eq("its parent still matches", 1, PlaceMatcher.MatchDepth("Seika Academy", unnamed));

            Check("Same ignores case/space", PlaceMatcher.Same("Staff Room", " staff   room "));
            Check("Same is false for blanks", !PlaceMatcher.Same("", ""));
            Check("ContainsName", PlaceMatcher.ContainsName(new[] { "Gym", "Staff Room" }, "the staff room"));
        }

        private static void NameMatcherTests()
        {
            Check("case and whitespace", NameMatcher.SameCharacter("Ms. Tanaka", " ms.   tanaka "));
            Check("surname matches full name", NameMatcher.SameCharacter("Tanaka", "Ms. Tanaka"));
            Check("full name matches surname", NameMatcher.SameCharacter("Aiko Tanaka", "Tanaka"));
            Check("part of a word does not match", !NameMatcher.SameCharacter("Ann", "Joanna"));
            Check("Ren is not Renata", !NameMatcher.SameCharacter("Ren", "Renata"));
            Check("two-letter part does not match", !NameMatcher.SameCharacter("Al", "Al Bundy"));
            Check("two-letter names match when equal", NameMatcher.SameCharacter("Al", "al"));
            Check("blank never matches", !NameMatcher.SameCharacter("", "Tanaka") && !NameMatcher.SameCharacter(null, null));

            Check("mentioned, different case", NameMatcher.MentionedIn("Aiko", "You see aiko waving."));
            Check("not mentioned inside another word", !NameMatcher.MentionedIn("Ren", "The children play."));
            Check("mentioned with punctuation", NameMatcher.MentionedIn("Ms. Tanaka", "Ask Ms. Tanaka, please."));
            Check("mentioned at the very end", NameMatcher.MentionedIn("Ren", "I look for Ren"));
            Check("not mentioned in empty text", !NameMatcher.MentionedIn("Ren", "") && !NameMatcher.MentionedIn("Ren", null));
            Check("second occurrence counts", NameMatcher.MentionedIn("Ren", "Brendan waves. Ren nods."));

            Check("list parsing drops blanks and repeats",
                NameMatcher.ParseList("Aiko, Ren; aiko ,, Ms. Tanaka").SequenceEqual(new[] { "Aiko", "Ren", "Ms. Tanaka" }));
        }

        // ---- engine ----------------------------------------------------------------------------

        private static void EngineTests()
        {
            ScheduleEntry cls = Entry("Class", "", "Monday", "09:00", "10:00");

            EntryState s = Eval(cls, Mon, "08:59");
            Check("1 min before: starting soon", s.Status == EntryStatus.StartingSoon && s.MinutesUntilStart == 1);
            Check("1 min before, window 0: inactive", Eval(cls, Mon, "08:59", 0).Status == EntryStatus.Inactive);
            s = Eval(cls, Mon, "08:30");
            Check("exactly the window before: starting soon", s.Status == EntryStatus.StartingSoon && s.MinutesUntilStart == 30);
            Check("31 min before: inactive", Eval(cls, Mon, "08:29").Status == EntryStatus.Inactive);
            s = Eval(cls, Mon, "09:00");
            Check("at start: active, 60 left", s.Status == EntryStatus.Active && s.MinutesRemaining == 60);
            s = Eval(cls, Mon, "09:59");
            Check("last minute: active, 1 left", s.Status == EntryStatus.Active && s.MinutesRemaining == 1);
            s = Eval(cls, Mon, "10:00");
            Check("at end: just ended (the end is exclusive)", s.Status == EntryStatus.JustEnded && s.MinutesSinceEnd == 0);
            s = Eval(cls, Mon, "10:30");
            Check("30 min after: just ended", s.Status == EntryStatus.JustEnded && s.MinutesSinceEnd == 30);
            Check("31 min after: inactive", Eval(cls, Mon, "10:31").Status == EntryStatus.Inactive);
            Check("same time, other day: inactive", Eval(cls, Tue, "09:30").Status == EntryStatus.Inactive);
            Check("same weekday next week: active", Eval(cls, Mon + 7, "09:30").Status == EntryStatus.Active);
            Check("start/end/duration reported", s.StartMinute == 540 && s.EndMinute == 600 && s.DurationMinutes == 60 && !s.AllDay);

            // Past midnight: the day flag belongs to the day the window STARTS on.
            ScheduleEntry late = Entry("Friday night", "", "Friday", "23:00", "02:00");
            Check("Fri 23:00 active", Eval(late, Fri, "23:00").Status == EntryStatus.Active);
            s = Eval(late, Sat, "00:30");
            Check("Sat 00:30 active (started Friday)", s.Status == EntryStatus.Active && s.MinutesRemaining == 90);
            Check("Sat 01:59 active", Eval(late, Sat, "01:59").Status == EntryStatus.Active);
            Check("Sat 02:00 just ended", Eval(late, Sat, "02:00").Status == EntryStatus.JustEnded);
            Check("Fri 00:30 NOT active (Thursday is not flagged)", Eval(late, Fri, "00:30").Status == EntryStatus.Inactive);
            Check("Sat 23:30 not active (Saturday is not flagged)", Eval(late, Sat, "23:30").Status == EntryStatus.Inactive);
            Check("duration across midnight", Eval(late, Fri, "23:00").DurationMinutes == 180);

            ScheduleEntry lateTwo = Entry("Two nights", "", "Thursday, Friday", "23:00", "02:00");
            Check("Fri 00:30 active when Thursday is flagged too", Eval(lateTwo, Fri, "00:30").Status == EntryStatus.Active);

            ScheduleEntry sunNight = Entry("Sunday night", "", "Sunday", "23:00", "02:00");
            Check("rolls over the week boundary (Sun -> Mon)", Eval(sunNight, 7, "00:30").Status == EntryStatus.Active);
            Check("rolls over on the very first day (day -1 was a Sunday)", Eval(sunNight, 0, "00:30").Status == EntryStatus.Active);

            ScheduleEntry early = Entry("Small hours", "", "Saturday", "00:10", "01:00");
            s = Eval(early, Fri, "23:50");
            Check("starting soon across midnight uses tomorrow's flag", s.Status == EntryStatus.StartingSoon && s.MinutesUntilStart == 20);
            ScheduleEntry evening = Entry("Evening", "", "Friday", "22:00", "23:50");
            s = Eval(evening, Sat, "00:10");
            Check("just ended across midnight", s.Status == EntryStatus.JustEnded && s.MinutesSinceEnd == 20);

            // start == end: a full day from that time.
            ScheduleEntry full = Entry("Festival", "", "Monday", "08:00", "08:00");
            s = Eval(full, Mon, "08:00");
            Check("start == end is a full day", s.Status == EntryStatus.Active && s.AllDay && s.DurationMinutes == 1440);
            s = Eval(full, Tue, "07:59");
            Check("still on a minute before the same time next day", s.Status == EntryStatus.Active && s.MinutesRemaining == 1);
            Check("ends at the same time next day", Eval(full, Tue, "08:00").Status == EntryStatus.JustEnded);
            Check("not on before it starts", Eval(full, Mon, "07:59").Status == EntryStatus.StartingSoon);
            ScheduleEntry always = Entry("Always", "", "every day", "00:00", "00:00");
            bool alwaysOn = true;
            for (int d = 0; d < 9; d++)
                foreach (string t in new[] { "00:00", "11:11", "23:59" })
                    if (Eval(always, d, t).Status != EntryStatus.Active) alwaysOn = false;
            Check("every day 00:00-00:00 is always on", alwaysOn);

            // Entries that cannot run.
            ScheduleEntry noDays = Entry("Nothing", "", "", "09:00", "10:00");
            s = Eval(noDays, Mon, "09:30");
            Check("no days selected: never runs", !s.Runnable && s.Status == EntryStatus.Inactive && s.Problem == "no days selected");
            bool never = true;
            for (int d = 0; d < 7; d++) if (Eval(noDays, d, "09:30").Status != EntryStatus.Inactive) never = false;
            Check("no days selected: inactive on every weekday", never);

            s = Eval(Entry("Foreign", "", "Montag", "09:00", "10:00"), Mon, "09:30");
            Check("only unknown days: cannot run, says which", !s.Runnable && s.Problem.Contains("Montag"));
            s = Eval(Entry("Mixed", "", "Monday, Montag", "09:00", "10:00"), Mon, "09:30");
            Check("known + unknown day: runs, unknown is reported", s.Runnable && s.Status == EntryStatus.Active && s.UnknownDays.Contains("Montag"));

            s = Eval(Entry("Bad start", "", "Monday", "25:99", "10:00"), Mon, "09:30");
            Check("bad start time: cannot run", !s.Runnable && s.Problem.StartsWith("start time"));
            s = Eval(Entry("Bad end", "", "Monday", "09:00", "soonish"), Mon, "09:30");
            Check("bad end time: cannot run", !s.Runnable && s.Problem.StartsWith("end time"));

            ScheduleEntry off = Entry("Off", "", "Monday", "09:00", "10:00");
            off.enabled = false;
            Check("the engine ignores the on/off switch (callers filter)", Eval(off, Mon, "09:30").Status == EntryStatus.Active);
            Check("null entry is harmless", !ScheduleEngine.Evaluate(null, Clock(0, "09:00"), 30).Runnable);

            // Forgiving times in stored entries.
            Check("stored '9am'-'5pm' works", Eval(Entry("Shift", "", "Monday", "9am", "5pm"), Mon, "16:59").Status == EntryStatus.Active);

            // Weeks that are not seven days long.
            string[] five = { "Ash", "Birch", "Cedar", "Dusk", "Elm" };
            ScheduleEntry cedar = Entry("Cedar market", "", "Cedar", "09:00", "10:00");
            ClockInfo c5 = ClockMath.FromElapsedSeconds((7 * 1440L + 570) * 60L, 60, 24, 5, 0, five);
            Check("5-day week: day 7 is Cedar", c5.WeekdayName == "Cedar" && ScheduleEngine.Evaluate(cedar, c5, 30).Status == EntryStatus.Active);
            c5 = ClockMath.FromElapsedSeconds((8 * 1440L + 570) * 60L, 60, 24, 5, 0, five);
            Check("5-day week: day 8 is not", ScheduleEngine.Evaluate(cedar, c5, 30).Status == EntryStatus.Inactive);
            c5 = ClockMath.FromElapsedSeconds((8 * 1440L + 570) * 60L, 60, 24, 5, 4, five);
            Check("5-day week with offset 4: day 8 is Cedar", c5.WeekdayName == "Cedar" && ScheduleEngine.Evaluate(cedar, c5, 30).Status == EntryStatus.Active);

            // Derived weekday with a per-save offset: day 0 is a Thursday.
            ScheduleEntry thu = Entry("Thursday thing", "", "Thursday", "09:00", "10:00");
            Check("offset 3: day 0 is Thursday", ScheduleEngine.Evaluate(thu, Clock(0, "09:30", 3), 30).Status == EntryStatus.Active);
            Check("offset 3: day 1 is not", ScheduleEngine.Evaluate(thu, Clock(1, "09:30", 3), 30).Status == EntryStatus.Inactive);

            // A 20-hour day of 50-minute hours: 18:00-02:00 crosses that world's midnight.
            ScheduleEntry alien = Entry("Night shift", "", "every day", "18:00", "02:00");
            Func<long, int, int, ClockInfo> alienClock = (day, h, m) =>
                ClockMath.FromElapsedSeconds((day * 1000L + h * 50 + m) * 60L, 50, 20, 7, 0, Week);
            s = ScheduleEngine.Evaluate(alien, alienClock(3, 19, 25), 30);
            Check("custom day: active in the evening", s.Status == EntryStatus.Active && s.DurationMinutes == 200);
            Check("custom day: active after that world's midnight", ScheduleEngine.Evaluate(alien, alienClock(4, 1, 49), 30).Status == EntryStatus.Active);
            ScheduleEntry alienMon = Entry("Monday shift", "", "Monday", "18:00", "02:00");
            Check("custom day: ended at 02:00", ScheduleEngine.Evaluate(alienMon, alienClock(1, 2, 0), 30).Status == EntryStatus.JustEnded);

            Check("a huge 'soon' window is clamped, not an error", Eval(cls, Tue, "09:30", 50000).Status == EntryStatus.StartingSoon
                                                                    || Eval(cls, Tue, "09:30", 50000).Status == EntryStatus.JustEnded);
            Check("negative window behaves like 0", Eval(cls, Mon, "08:59", -5).Status == EntryStatus.Inactive);
        }

        // ---- form ------------------------------------------------------------------------------

        private static void EntryFormTests()
        {
            ClockInfo clock = Clock(0, "09:00");
            var monWed = new[] { true, false, true, false, false, false, false };
            string error;

            ScheduleEntry e = EntryForm.Build(" Debate club ", "Members argue.", " Debate Club Room ", "3:30pm", "1700", "Aiko, Ren, aiko",
                null, false, true, monWed, clock, out error);
            Check("valid form builds", e != null && error == null);
            Eq("title trimmed", "Debate club", e.title);
            Eq("place trimmed", "Debate Club Room", e.place);
            Eq("start canonical", "15:30", e.start);
            Eq("end canonical", "17:00", e.end);
            Check("days by name", e.days.SequenceEqual(new[] { "Monday", "Wednesday" }));
            Check("characters de-duplicated", e.npcs.SequenceEqual(new[] { "Aiko", "Ren" }));
            Eq("kind", ScheduleKinds.Event, e.kind);

            Check("empty title rejected", EntryForm.Build("  ", "", "", "8", "9", "", null, false, true, monWed, clock, out error) == null
                                          && error.Contains("title"));
            Check("bad start rejected, says which field", EntryForm.Build("X", "", "", "25:99", "9", "", null, false, true, monWed, clock, out error) == null
                                                         && error.StartsWith("Start time") && error.Contains("25:99"));
            Check("bad end rejected, says which field", EntryForm.Build("X", "", "", "8", "later", "", null, false, true, monWed, clock, out error) == null
                                                       && error.StartsWith("End time") && error.Contains("later"));
            Check("routine without a character rejected", EntryForm.Build("X", "", "", "8", "9", " , ", null, true, true, monWed, clock, out error) == null
                                                          && error.Contains("character"));
            Check("no day ticked rejected", EntryForm.Build("X", "", "", "8", "9", "", null, false, true, new bool[7], clock, out error) == null
                                            && error.Contains("day"));

            var all = Enumerable.Repeat(true, 7).ToArray();
            e = EntryForm.Build("X", null, null, "8", "8", "Sato", null, true, false, all, clock, out error);
            Check("all days become the every-day token", e != null && e.days.Count == 1 && e.days[0] == DaySpec.EveryDay);
            Check("routine kind, disabled, null texts tolerated", e.kind == ScheduleKinds.Routine && !e.enabled && e.details == "" && e.place == "");

            e = EntryForm.Build("X", "", "", "8", "9", "", "mon, FRIDAY", false, true, null, clock, out error);
            Check("days from text", e != null && e.days.SequenceEqual(new[] { "Monday", "Friday" }));
            Check("unknown day in text rejected", EntryForm.Build("X", "", "", "8", "9", "", "Monday, Blursday", false, true, null, clock, out error) == null
                                                  && error.Contains("Blursday"));
            Check("empty day text rejected", EntryForm.Build("X", "", "", "8", "9", "", "", false, true, null, clock, out error) == null);
        }

        // ---- model -----------------------------------------------------------------------------

        private static void ModelTests()
        {
            ScheduleEntry a = Entry("Debate club", "Debate Club Room", "Monday, Wednesday", "15:30", "17:00", ScheduleKinds.Event, "Aiko, Ren", "Members argue.");
            ScheduleEntry b = Entry(" debate CLUB ", "debate club room", "Wednesday, monday", "15:30", "17:00", "EVENT", "Ren, aiko", "members argue.");
            b.enabled = false;
            Check("same content ignores case, order and the on/off switch", ScheduleEntry.SameContent(a, b));
            ScheduleEntry c = a.Clone();
            c.end = "18:00";
            Check("a different time is different content", !ScheduleEntry.SameContent(a, c));
            c = a.Clone();
            c.npcs.Add("Kenji");
            Check("clone has its own lists", a.npcs.Count == 2 && !ScheduleEntry.SameContent(a, c));

            var file = new ScheduleFile();
            file.entries.Add(a);
            int skipped;
            int added = file.MergeFrom(new[] { b, c, null, Entry("Lunch", "", "every day", "12:00", "13:00") }, out skipped);
            Check("merge adds the new ones and skips the duplicate", added == 2 && skipped == 1 && file.entries.Count == 3);
            added = file.MergeFrom(file.entries.Select(x => x.Clone()).ToList(), out skipped);
            Check("re-importing the same file adds nothing", added == 0 && skipped == 3 && file.entries.Count == 3);

            var broken = new ScheduleEntry { title = null, details = null, place = null, days = null, npcs = null, start = null, end = null, kind = "Routine " };
            broken.Sanitize();
            Check("sanitize repairs nulls", broken.title == "" && broken.days != null && broken.npcs != null && broken.start == "" && broken.kind == ScheduleKinds.Routine);
            Check("a sanitized broken entry cannot run but does not throw", !ScheduleEngine.Evaluate(broken, Clock(0, "09:00"), 30).Runnable);

            var f2 = new ScheduleFile { entries = null, weekLength = -3 };
            f2.Sanitize();
            Check("file sanitize", f2.entries != null && f2.weekLength == 0);
            Check("kind normalizing", ScheduleKinds.Normalize(" ROUTINE ") == ScheduleKinds.Routine && ScheduleKinds.Normalize("whatever") == ScheduleKinds.Event
                                      && ScheduleKinds.Normalize(null) == ScheduleKinds.Event);
        }

        // ---- block: the school example from the README ------------------------------------------

        private static readonly string[] SchoolRooms = { "Classroom 2-B", "Cafeteria", "Staff Room", "Debate Club Room", "Corridor", "Gym" };

        private static List<ScheduleEntry> SchoolEntries()
        {
            const string weekdays = "Monday, Tuesday, Wednesday, Thursday, Friday";
            return new List<ScheduleEntry>
            {
                Entry("Morning classes", "Seika Academy", weekdays, "08:30", "12:00", ScheduleKinds.Event, "",
                    "Lessons are in session. Corridors are empty, students found outside class are asked for a hall pass."),
                Entry("Lunch break", "Seika Academy", weekdays, "12:00", "13:00", ScheduleKinds.Event, "",
                    "Students crowd the cafeteria and the courtyard."),
                Entry("Afternoon classes", "Seika Academy", weekdays, "13:00", "15:30"),
                Entry("Debate club", "Debate Club Room", "Monday, Wednesday", "15:30", "17:00", ScheduleKinds.Event, "Aiko, Ren",
                    "Members argue a set motion. Visitors may watch from the back."),
                Entry("Teaches class 2-B", "Classroom 2-B", weekdays, "08:30", "12:00", ScheduleKinds.Routine, "Ms. Tanaka"),
                Entry("Lunch in the staff room", "Staff Room", weekdays, "12:00", "13:00", ScheduleKinds.Routine, "Ms. Tanaka"),
                Entry("Night watch", "Seika Academy", "every day", "22:00", "06:00", ScheduleKinds.Routine, "Mr. Sato",
                    "Walks the corridors with a torch and locks what he finds open.")
            };
        }

        private static BlockInput SchoolInput(string room, long day, string time, string[] present, string prompt)
        {
            return new BlockInput
            {
                Clock = Clock(day, time),
                PlaceChain = new List<string> { room, "Seika Academy", "Hoshino Ward", "Japan" },
                TopLocationName = "Seika Academy",
                NearbyPlaces = SchoolRooms.Where(r => r != room).ToList(),
                PresentNames = present,
                PromptText = prompt,
                Entries = SchoolEntries(),
                Options = new BlockOptions(),
                MaxTokens = 2048
            };
        }

        private static string School(string room, long day, string time, string[] present, string prompt)
        {
            return BlockBuilder.Build(SchoolInput(room, day, time, present, prompt));
        }

        private static void SchoolBlockTests()
        {
            // Monday 12:10, Cafeteria, Ms. Tanaka standing there.
            string b = School("Cafeteria", Mon, "12:10", new[] { "Ms. Tanaka", "Kenji" }, "");
            Check("starts with a blank line and the header", b.StartsWith("\n\n[SCHEDULES | now: Monday 12:10 | player is at: Cafeteria, in Seika Academy]\n"));
            Check("ends with a newline", b.EndsWith("\n") && !b.EndsWith("\n\n"));
            Contains("framing: default, not a law", b, "break one only when the story gives a reason");
            Contains("framing: do not recite", b, "Never recite the schedule to the player.");
            Contains("lunch is happening here (ancestor match)", b,
                "Happening here now:\n- Lunch break (across Seika Academy, until 13:00): Students crowd the cafeteria and the courtyard.\n");
            Contains("morning classes just ended", b, "Just ended here:\n- Morning classes (ended 12:00, 10 min ago)\n");
            Contains("Tanaka is normally elsewhere", b, "- Ms. Tanaka: usually at Staff Room now (Lunch in the staff room, until 13:00)\n");
            Contains("elsewhere header says her presence is notable", b, "Normally elsewhere now (if one of them is here anyway, that is unusual");
            Lacks("debate club is not on", b, "Debate club");
            Lacks("afternoon classes are not soon yet (50 min away)", b, "Afternoon classes");
            Lacks("Tanaka is not repeated in the other-rooms section", b, "Elsewhere in Seika Academy now:");
            Lacks("no empty sections", b, "Expected here now:");
            Lacks("no empty sections (soon)", b, "Starting soon here:");
            Lacks("her finished morning routine in another room is not 'ended here'", b, "Teaches class 2-B");

            // Same moment, nobody notable around.
            b = School("Cafeteria", Mon, "12:10", new[] { "Kenji" }, "The cafeteria is loud.");
            Lacks("not present, not named: no 'normally elsewhere'", b, "Normally elsewhere now");
            Contains("but the other-rooms section says where she is", b,
                "Elsewhere in Seika Academy now:\n- Ms. Tanaka: Lunch in the staff room (in Staff Room, until 13:00)\n");

            // Named in the story text only.
            b = School("Cafeteria", Mon, "12:10", new string[0], "I look around for Ms. Tanaka.");
            Contains("named in the prompt: normally elsewhere", b, "- Ms. Tanaka: usually at Staff Room now");
            b = School("Cafeteria", Mon, "12:10", new[] { "Tanaka" }, "");
            Contains("present under a shorter name still matches", b, "- Ms. Tanaka: usually at Staff Room now");

            // 12:40: afternoon classes start in 20 minutes.
            b = School("Cafeteria", Mon, "12:40", new string[0], "");
            Contains("starting soon", b, "Starting soon here:\n- Afternoon classes (starts 13:00, in 20 min)\n");
            Lacks("morning classes ended more than 30 min ago", b, "Morning classes");

            // Wednesday 16:00 in the club room.
            b = School("Debate Club Room", Wed, "16:00", new[] { "Aiko" }, "");
            Contains("club in session, exact place, details and members", b,
                "Happening here now:\n- Debate club (until 17:00): Members argue a set motion. Visitors may watch from the back. With: Aiko, Ren.\n");
            Lacks("exact match has no 'across'", b, "across Debate Club Room");
            Contains("afternoon classes just ended (30 min ago)", b, "- Afternoon classes (ended 15:30, 30 min ago)");
            Lacks("Aiko is where she should be", b, "Normally elsewhere now");
            Contains("header for the club room", b, "player is at: Debate Club Room, in Seika Academy]");

            // Wednesday 16:00 in the cafeteria with Aiko.
            b = School("Cafeteria", Wed, "16:00", new[] { "Aiko" }, "");
            Contains("Aiko should be at the club", b, "- Aiko: usually at Debate Club Room now (Debate club, until 17:00)\n");
            Contains("the club is running in another room", b, "Elsewhere in Seika Academy now:\n- Debate club (in Debate Club Room, until 17:00)\n");
            Lacks("Ren is neither present nor named", b, "- Ren:");

            // Tuesday: no club.
            b = School("Debate Club Room", Tue, "16:00", new[] { "Aiko" }, "");
            Lacks("no club on Tuesday", b, "Debate club");
            Contains("only the ended classes remain", b, "Just ended here:\n- Afternoon classes (ended 15:30, 30 min ago)\n");
            b = School("Debate Club Room", Tue, "16:01", new[] { "Aiko" }, "");
            Eq("nothing relevant: nothing injected at all", "", b);

            // Monday 15:10: club starts in 20 min in this very room.
            b = School("Debate Club Room", Mon, "15:10", new string[0], "");
            Contains("club starting soon, with its members", b, "- Debate club (starts 15:30, in 20 min; with Aiko, Ren)\n");

            // Night watch across midnight.
            b = School("Corridor", Mon, "23:30", new string[0], "");
            Contains("night watch expected, across the academy", b,
                "Expected here now:\n- Mr. Sato: Night watch (across Seika Academy, until 06:00). Walks the corridors with a torch and locks what he finds open.\n");
            b = School("Corridor", Mon, "01:00", new string[0], "");
            Contains("01:00 Monday: the watch that began on Sunday is still on", b, "- Mr. Sato: Night watch (across Seika Academy, until 06:00)");
            b = School("Corridor", Mon, "06:10", new string[0], "");
            Contains("06:10: the watch just ended", b, "Just ended here:\n- Mr. Sato: Night watch (ended 06:00, 10 min ago)\n");

            // 08:10 in her classroom: her routine starts soon, at this exact place.
            b = School("Classroom 2-B", Mon, "08:10", new string[0], "");
            Contains("routine starting soon here", b, "- Ms. Tanaka: Teaches class 2-B (starts 08:30, in 20 min)\n");
            Contains("classes starting soon too", b, "- Morning classes (starts 08:30, in 20 min)\n");
            b = School("Classroom 2-B", Mon, "09:00", new[] { "Ms. Tanaka" }, "");
            Contains("expected here at the exact place (no 'across')", b, "Expected here now:\n- Ms. Tanaka: Teaches class 2-B (until 12:00)\n");
            Lacks("and therefore not 'elsewhere'", b, "Normally elsewhere now");

            // Weekend.
            Eq("Saturday 10:00: nothing", "", School("Cafeteria", Sat, "10:00", new[] { "Ms. Tanaka" }, "Ms. Tanaka waves."));

            // Away from the school.
            var home = SchoolInput("Bedroom", Mon, "12:10", new string[0], "");
            home.PlaceChain = new List<string> { "Bedroom", "Home", "Hoshino Ward", "Japan" };
            home.TopLocationName = "Home";
            home.NearbyPlaces = new List<string> { "Kitchen" };
            Eq("at home, nobody named: nothing", "", BlockBuilder.Build(home));
            home.PromptText = "I text Ms. Tanaka.";
            string hb = BlockBuilder.Build(home);
            Contains("at home, teacher named: where she is", hb, "- Ms. Tanaka: usually at Staff Room now");
            Lacks("at home: no school events", hb, "Lunch break");

            // Disabled entries are skipped; an empty list injects nothing.
            var offInput = SchoolInput("Cafeteria", Mon, "12:10", new[] { "Ms. Tanaka" }, "");
            foreach (ScheduleEntry e in offInput.Entries) e.enabled = false;
            Eq("all entries off: nothing", "", BlockBuilder.Build(offInput));
            offInput.Entries = new List<ScheduleEntry>();
            Eq("no entries: nothing", "", BlockBuilder.Build(offInput));
            Eq("null input: nothing", "", BlockBuilder.Build(null));
        }

        // ---- block: overlapping routines, caps, truncation, budget --------------------------------

        private static BlockInput Town(string place, string time, string[] present, string prompt, List<ScheduleEntry> entries)
        {
            return new BlockInput
            {
                Clock = Clock(Mon, time),
                PlaceChain = new List<string> { place, "Old Town", "Vale" },
                TopLocationName = place,
                NearbyPlaces = new List<string>(),
                PresentNames = present,
                PromptText = prompt,
                Entries = entries,
                Options = new BlockOptions(),
                MaxTokens = 2048
            };
        }

        private static void BlockEdgeTests()
        {
            // One character in two overlapping routines.
            var bram = new List<ScheduleEntry>
            {
                Entry("Works the forge", "Forge", "every day", "08:00", "17:00", ScheduleKinds.Routine, "Bram"),
                Entry("Market run", "Market", "every day", "12:00", "13:00", ScheduleKinds.Routine, "Bram")
            };
            string b = BlockBuilder.Build(Town("Tavern", "12:30", new[] { "Bram" }, "", bram));
            Contains("both elsewhere: one line naming both places", b,
                "- Bram: usually at Forge or Market now (Works the forge, until 17:00; Market run, until 13:00)\n");
            Eq("both elsewhere: listed once", 1, CountOf(b, "- Bram:"));

            b = BlockBuilder.Build(Town("Forge", "12:30", new[] { "Bram" }, "", bram));
            Contains("one of them is here: expected here", b, "Expected here now:\n- Bram: Works the forge (until 17:00)\n");
            Lacks("one of them is here: not 'elsewhere' (here wins)", b, "Normally elsewhere now");
            Lacks("one of them is here: the other place is not mentioned", b, "Market");

            var wide = new List<ScheduleEntry>
            {
                Entry("In town for the day", "Old Town", "every day", "07:00", "20:00", ScheduleKinds.Routine, "Bram"),
                Entry("Works the forge", "Forge", "every day", "08:00", "17:00", ScheduleKinds.Routine, "Bram")
            };
            b = BlockBuilder.Build(Town("Market Square", "12:30", new string[0], "Where is Bram?", wide));
            Contains("region-wide routine: expected in the area", b, "- Bram: In town for the day (across Old Town, until 20:00)\n");
            Contains("plus the specific usual spot", b, "- Bram: usually at Forge now (Works the forge, until 17:00)\n");

            // An event with attached characters also says where they are.
            var guild = new List<ScheduleEntry> { Entry("Guild meeting", "Guild Hall", "every day", "12:00", "14:00", ScheduleKinds.Event, "Mira, Tobin") };
            b = BlockBuilder.Build(Town("Tavern", "12:30", new[] { "Mira" }, "", guild));
            Contains("event member met elsewhere", b, "- Mira: usually at Guild Hall now (Guild meeting, until 14:00)\n");
            Lacks("the other member is not listed", b, "Tobin");

            // A routine nobody is attached to behaves like an event.
            var orphan = new List<ScheduleEntry> { Entry("Open hours", "Tavern", "every day", "10:00", "23:00", ScheduleKinds.Routine) };
            Contains("routine without characters is shown as something happening", BlockBuilder.Build(Town("Tavern", "12:30", null, null, orphan)),
                "Happening here now:\n- Open hours (until 23:00)\n");

            // "Anywhere" entries, sorted after place-specific ones; all-day wording.
            var mixed = new List<ScheduleEntry>
            {
                Entry("Curfew", "", "every day", "22:00", "05:00", ScheduleKinds.Event, "", "Nobody is allowed on the streets."),
                Entry("Last orders", "Tavern", "every day", "22:30", "23:30"),
                Entry("Harvest week", "Vale", "every day", "00:00", "00:00")
            };
            b = BlockBuilder.Build(Town("Tavern", "23:00", null, "", mixed));
            Contains("anywhere entry applies here", b, "- Curfew (until 05:00): Nobody is allowed on the streets.\n");
            Contains("world-wide all-day entry", b, "- Harvest week (across Vale, all day)\n");
            Check("exact place first, then wider places, then anywhere",
                b.IndexOf("Last orders", StringComparison.Ordinal) < b.IndexOf("Harvest week", StringComparison.Ordinal)
                && b.IndexOf("Harvest week", StringComparison.Ordinal) < b.IndexOf("Curfew", StringComparison.Ordinal));

            // Per-section cap.
            var many = new List<ScheduleEntry>();
            for (int i = 0; i < 10; i++) many.Add(Entry("Stall " + i, "Market", "every day", "08:0" + i, "18:00"));
            BlockInput capped = Town("Market", "12:00", null, "", many);
            BlockResult r = BlockBuilder.BuildDetailed(capped);
            Eq("cap: lines emitted", 4, r.EmittedLines);
            Eq("cap: lines dropped", 6, r.DroppedForCap);
            Contains("cap keeps the earliest", r.Text, "Stall 0");
            Lacks("cap drops the rest", r.Text, "Stall 4");
            capped.Options.MaxHereNow = 0;
            Eq("cap 0 turns the section off (and then nothing is left)", "", BlockBuilder.Build(capped));

            // Details truncation and layout safety.
            string longDetails = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 20));
            var wordy = new List<ScheduleEntry> { Entry("Wordy\nevent", "Tavern", "every day", "10:00", "23:00", ScheduleKinds.Event, "", "Line one.\n\nLine two.\t" + longDetails) };
            b = BlockBuilder.Build(Town("Tavern", "12:30", null, "", wordy));
            string line = b.Split('\n').First(l => l.StartsWith("- Wordy"));
            Check("player newlines cannot break a line", line.StartsWith("- Wordy event (until 23:00): Line one. Line two. The quick"));
            Check("details cut to the limit with dots", line.EndsWith("...") && line.Length <= "- Wordy event (until 23:00): ".Length + 160);
            Check("cut at a word, not inside one", !line.EndsWith(" ...") && line[line.Length - 4] != ' ');

            // Budget: small.
            var busy = new List<ScheduleEntry>();
            for (int i = 0; i < 4; i++)
                busy.Add(Entry("Event number " + i, "Tavern", "every day", "10:0" + i, "23:00", ScheduleKinds.Event, "", "Some sentence that makes this line a fair bit longer than the others."));
            busy.Add(Entry("Shift", "Tavern", "every day", "10:00", "23:00", ScheduleKinds.Routine, "Olga"));
            BlockInput tight = Town("Tavern", "12:30", null, "", busy);
            BlockResult full = BlockBuilder.BuildDetailed(tight);
            Eq("unlimited: all five lines", 5, full.EmittedLines);
            tight.MaxTokens = 125; // 500 characters
            BlockResult small = BlockBuilder.BuildDetailed(tight);
            Check("small budget: fits (" + small.Text.Length + " <= 500)", small.Text.Length > 0 && small.Text.Length <= 500);
            Check("small budget: something was dropped and counted", small.DroppedForBudget > 0 && small.EmittedLines + small.DroppedForBudget == 5);
            Contains("small budget: header kept whole", small.Text, "[SCHEDULES | now: Monday 12:30 | player is at: Tavern]\n" + BlockBuilder.Framing + "\n");
            string[] fullLines = full.Text.Split('\n');
            Check("small budget: every line is a whole line of the full block", small.Text.Split('\n').All(l => fullLines.Contains(l)));
            Check("small budget: ends cleanly", small.Text.EndsWith("\n"));
            Contains("small budget: the most important section survives", small.Text, "Happening here now:\n- Event number 0");

            // Budget: too small for the header plus one line -> nothing, never a torn block.
            tight.MaxTokens = 60;
            Eq("tiny budget: nothing", "", BlockBuilder.Build(tight));
            tight.MaxTokens = 1;
            Eq("budget of 1 token: nothing", "", BlockBuilder.Build(tight));
            tight.MaxTokens = 0;
            Eq("budget of 0: nothing", "", BlockBuilder.Build(tight));

            // The mod's own ceiling applies even when GenContext truncation is off (huge budget).
            tight.MaxTokens = int.MaxValue / 4;
            tight.Options.MaxBlockChars = 500;
            r = BlockBuilder.BuildDetailed(tight);
            Check("own ceiling respected with an unlimited budget", r.Text.Length > 0 && r.Text.Length <= 500 && r.DroppedForBudget > 0);
            tight.Options.MaxBlockChars = 1600;
            Check("huge budget does not overflow", BlockBuilder.Build(tight).Length == full.Text.Length);

            // Header uses the world's own day names and day length.
            string[] five = { "Ash", "Birch", "Cedar", "Dusk", "Elm" };
            BlockInput odd = Town("Tavern", "12:30", null, "", new List<ScheduleEntry> { Entry("Cedar market", "Tavern", "Cedar", "18:00", "02:00") });
            odd.Clock = ClockMath.FromElapsedSeconds((7 * 1000L + 19 * 50 + 5) * 60L, 50, 20, 5, 0, five);
            b = BlockBuilder.Build(odd);
            Contains("custom calendar header", b, "[SCHEDULES | now: Cedar 19:05 | player is at: Tavern]");
            Contains("custom calendar window", b, "- Cedar market (until 02:00)\n");
        }

        private static int CountOf(string text, string part)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
            return count;
        }
    }
}
