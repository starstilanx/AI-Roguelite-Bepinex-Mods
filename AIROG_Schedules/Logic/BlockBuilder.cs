using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIROG_Schedules.Logic
{
    public sealed class BlockOptions
    {
        public int SoonWindowMinutes = 30;
        public int MaxHereNow = 4;
        /// <summary>Applies to "starting soon" and to "just ended" separately.</summary>
        public int MaxSoonOrEnded = 3;
        public int MaxExpectedHere = 5;
        public int MaxElsewhere = 4;
        public int MaxNearby = 3;
        public int MaxDetailChars = 160;
        /// <summary>The mod's own ceiling for the whole block, on top of the budget it is handed.</summary>
        public int MaxBlockChars = 1600;
    }

    public sealed class BlockInput
    {
        public ClockInfo Clock;

        /// <summary>Player's current sub-location first, then each enclosing place, region, world.</summary>
        public IList<string> PlaceChain;

        /// <summary>Name of the top-level location the player is inside (for the "elsewhere in ..." header).</summary>
        public string TopLocationName;

        /// <summary>Other places inside the same top-level location (not in the chain).</summary>
        public IList<string> NearbyPlaces;

        /// <summary>Characters in the current scene.</summary>
        public IList<string> PresentNames;

        /// <summary>The text the provider was handed (the game's prompt); scanned for character names.</summary>
        public string PromptText;

        public IList<ScheduleEntry> Entries;
        public BlockOptions Options = new BlockOptions();

        /// <summary>Token budget from GenContext (about four characters per token).</summary>
        public int MaxTokens = int.MaxValue;
    }

    public sealed class BlockResult
    {
        /// <summary>The text to inject. Empty when no entry is relevant or nothing fits the budget.</summary>
        public string Text = "";
        public int CandidateLines;
        public int EmittedLines;
        public int DroppedForCap;
        public int DroppedForBudget;
    }

    /// <summary>Builds the prompt block from the schedule, the clock and where the player is.</summary>
    public static class BlockBuilder
    {
        public const string Framing =
            "Usual timetable and routines of this setting. People normally keep to them, so treat them as the default " +
            "for what is going on and who is around; break one only when the story gives a reason. " +
            "Never recite the schedule to the player.";

        // Display order of the sections.
        private const int SecHereNow = 0, SecSoon = 1, SecEnded = 2, SecExpected = 3, SecElsewhere = 4, SecNearby = 5;
        private const int SectionCount = 6;

        // Which sections keep their lines when the budget runs short (most important first).
        private static readonly int[] BudgetOrder = { SecHereNow, SecExpected, SecElsewhere, SecSoon, SecEnded, SecNearby };

        private sealed class Line
        {
            public string Text;
            public long SortKey;
            public int Order; // original position, keeps the sort stable
        }

        private sealed class ElsewhereInfo
        {
            public string Name;
            public bool Present;
            public readonly List<string> Places = new List<string>();
            public readonly List<string> Reasons = new List<string>();
            public int Order;
        }

        public static string Build(BlockInput input)
        {
            return BuildDetailed(input).Text;
        }

        public static BlockResult BuildDetailed(BlockInput input)
        {
            var result = new BlockResult();
            if (input == null || input.Clock == null || input.Entries == null || input.MaxTokens <= 0) return result;

            BlockOptions opt = input.Options ?? new BlockOptions();
            ClockInfo clock = input.Clock;
            IList<string> chain = input.PlaceChain ?? new List<string>();

            var sections = new List<Line>[SectionCount];
            for (int i = 0; i < SectionCount; i++) sections[i] = new List<Line>();

            var hereExact = new HashSet<string>(StringComparer.Ordinal);   // normalized names placed at the current place
            var elsewhere = new Dictionary<string, ElsewhereInfo>(StringComparer.Ordinal);
            var nearbyRoutines = new List<KeyValuePair<ScheduleEntry, EntryState>>();
            int order = 0;

            foreach (ScheduleEntry entry in input.Entries)
            {
                order++;
                if (entry == null || !entry.enabled) continue;
                EntryState state = ScheduleEngine.Evaluate(entry, clock, opt.SoonWindowMinutes);
                if (!state.Runnable || state.Status == EntryStatus.Inactive) continue;

                string title = Clip(Clean(entry.title), 80);
                if (title.Length == 0) title = "(untitled)";
                List<string> npcs = CleanNames(entry.npcs);
                bool routine = ScheduleKinds.IsRoutine(entry.kind) && npcs.Count > 0; // a routine needs somebody
                bool anywhere = PlaceMatcher.IsAnywhere(entry.place);
                int depth = PlaceMatcher.MatchDepth(entry.place, chain);
                bool here = depth >= 0;
                string placeText = Clip(Clean(entry.place), 60);
                string until = state.AllDay ? "all day" : "until " + TimeParser.Format(state.EndMinute, clock.MinutesPerHour);

                if (state.Status == EntryStatus.Active)
                {
                    if (here)
                    {
                        string scope = !anywhere && depth > 0 && depth < chain.Count ? "across " + Clip(Clean(chain[depth]), 60) + ", " : "";
                        string details = Clip(Clean(entry.details), opt.MaxDetailChars);
                        long sortKey = (anywhere ? 1000L : depth) * 100000L + state.StartMinute;
                        if (routine)
                        {
                            string text = "- " + JoinNames(npcs) + ": " + title + " (" + scope + until + ")";
                            if (details.Length > 0) text += ". " + EnsurePeriod(details);
                            sections[SecExpected].Add(new Line { Text = text, SortKey = sortKey, Order = order });
                        }
                        else
                        {
                            string text = "- " + title + " (" + scope + until + ")";
                            if (details.Length > 0) text += ": " + EnsurePeriod(details);
                            if (npcs.Count > 0) text += (details.Length > 0 ? " " : ". ") + "With: " + JoinNames(npcs) + ".";
                            sections[SecHereNow].Add(new Line { Text = text, SortKey = sortKey, Order = order });
                        }
                        if (depth == 0)
                            foreach (string n in npcs) hereExact.Add(NameMatcher.Normalize(n));
                    }
                    else
                    {
                        foreach (string n in npcs)
                        {
                            string key = NameMatcher.Normalize(n);
                            ElsewhereInfo info;
                            if (!elsewhere.TryGetValue(key, out info))
                            {
                                info = new ElsewhereInfo { Name = n, Order = order };
                                elsewhere[key] = info;
                            }
                            if (!PlaceMatcher.ContainsName(info.Places, placeText)) info.Places.Add(placeText);
                            info.Reasons.Add(title + ", " + until);
                        }

                        if (PlaceMatcher.ContainsName(input.NearbyPlaces, entry.place))
                        {
                            if (routine) nearbyRoutines.Add(new KeyValuePair<ScheduleEntry, EntryState>(entry, state));
                            else
                                sections[SecNearby].Add(new Line
                                {
                                    Text = "- " + title + " (in " + placeText + ", " + until + ")",
                                    SortKey = state.StartMinute,
                                    Order = order
                                });
                        }
                    }
                }
                else if (here)
                {
                    string who = routine ? JoinNames(npcs) + ": " : "";
                    string with = !routine && npcs.Count > 0 ? "; with " + JoinNames(npcs) : "";
                    if (state.Status == EntryStatus.StartingSoon)
                    {
                        string when = "starts " + TimeParser.Format(state.StartMinute, clock.MinutesPerHour)
                                      + ", in " + state.MinutesUntilStart.ToString(CultureInfo.InvariantCulture) + " min";
                        sections[SecSoon].Add(new Line
                        {
                            Text = "- " + who + title + " (" + when + with + ")",
                            SortKey = state.MinutesUntilStart,
                            Order = order
                        });
                    }
                    else
                    {
                        string ago = state.MinutesSinceEnd <= 0
                            ? "just now"
                            : state.MinutesSinceEnd.ToString(CultureInfo.InvariantCulture) + " min ago";
                        string when = "ended " + TimeParser.Format(state.EndMinute, clock.MinutesPerHour) + ", " + ago;
                        sections[SecEnded].Add(new Line
                        {
                            Text = "- " + who + title + " (" + when + with + ")",
                            SortKey = state.MinutesSinceEnd,
                            Order = order
                        });
                    }
                }
            }

            // "Normally elsewhere now": only characters who are in the scene or named in the prompt,
            // and whom no running entry puts at the player's exact place.
            var listedElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ElsewhereInfo> kv in elsewhere)
            {
                if (hereExact.Contains(kv.Key)) continue;
                ElsewhereInfo info = kv.Value;
                info.Present = IsPresent(info.Name, input.PresentNames);
                if (!info.Present && !NameMatcher.MentionedIn(info.Name, input.PromptText)) continue;

                string text = "- " + info.Name + ": usually at " + string.Join(" or ", info.Places.ToArray())
                              + " now (" + string.Join("; ", info.Reasons.ToArray()) + ")";
                sections[SecElsewhere].Add(new Line { Text = text, SortKey = info.Present ? 0 : 1, Order = info.Order });
                listedElsewhere.Add(kv.Key);
            }

            // Routines running in another room of the same location: say who is where, unless the
            // line above already did.
            foreach (KeyValuePair<ScheduleEntry, EntryState> pair in nearbyRoutines)
            {
                var names = new List<string>();
                foreach (string n in CleanNames(pair.Key.npcs))
                {
                    string key = NameMatcher.Normalize(n);
                    if (!listedElsewhere.Contains(key) && !hereExact.Contains(key)) names.Add(n);
                }
                if (names.Count == 0) continue;
                EntryState st = pair.Value;
                string until = st.AllDay ? "all day" : "until " + TimeParser.Format(st.EndMinute, clock.MinutesPerHour);
                sections[SecNearby].Add(new Line
                {
                    Text = "- " + JoinNames(names) + ": " + Clip(Clean(pair.Key.title), 80)
                           + " (in " + Clip(Clean(pair.Key.place), 60) + ", " + until + ")",
                    SortKey = st.StartMinute,
                    Order = int.MaxValue / 2
                });
            }

            // Caps.
            int[] caps = { opt.MaxHereNow, opt.MaxSoonOrEnded, opt.MaxSoonOrEnded, opt.MaxExpectedHere, opt.MaxElsewhere, opt.MaxNearby };
            for (int s = 0; s < SectionCount; s++)
            {
                List<Line> lines = sections[s];
                result.CandidateLines += lines.Count;
                lines.Sort((a, b) => a.SortKey != b.SortKey ? a.SortKey.CompareTo(b.SortKey) : a.Order.CompareTo(b.Order));
                int cap = Math.Max(0, caps[s]);
                if (lines.Count > cap)
                {
                    result.DroppedForCap += lines.Count - cap;
                    lines.RemoveRange(cap, lines.Count - cap);
                }
            }

            string[] headers =
            {
                "Happening here now:",
                "Starting soon here:",
                "Just ended here:",
                "Expected here now:",
                "Normally elsewhere now (if one of them is here anyway, that is unusual, or they are about to leave):",
                string.IsNullOrEmpty(input.TopLocationName)
                    ? "Nearby now:"
                    : "Elsewhere in " + Clip(Clean(input.TopLocationName), 60) + " now:"
            };

            string where = chain.Count > 0 ? Clip(Clean(chain[0]), 60) : "";
            if (where.Length > 0 && !string.IsNullOrEmpty(input.TopLocationName) && !PlaceMatcher.Same(chain[0], input.TopLocationName))
                where += ", in " + Clip(Clean(input.TopLocationName), 60);
            string head = "[SCHEDULES | now: " + clock.WeekdayName + " " + clock.TimeText
                          + (where.Length > 0 ? " | player is at: " + where : "") + "]\n" + Framing + "\n";

            // Budget: the header and framing are all-or-nothing, then whole lines are added in
            // order of importance. A line is never cut in the middle.
            const string Lead = "\n\n";
            long maxChars = Math.Min(Math.Max(0, opt.MaxBlockChars), (long)input.MaxTokens * 4L);
            long used = Lead.Length + head.Length;
            var keep = new List<Line>[SectionCount];
            for (int i = 0; i < SectionCount; i++) keep[i] = new List<Line>();

            foreach (int s in BudgetOrder)
            {
                foreach (Line line in sections[s])
                {
                    long cost = line.Text.Length + 1 + (keep[s].Count == 0 ? headers[s].Length + 1 : 0);
                    if (used + cost > maxChars)
                    {
                        result.DroppedForBudget++;
                        continue;
                    }
                    keep[s].Add(line);
                    used += cost;
                    result.EmittedLines++;
                }
            }

            if (result.EmittedLines == 0) return result; // nothing relevant, or no room: inject nothing

            var sb = new StringBuilder((int)Math.Min(used, 8192));
            sb.Append(Lead).Append(head);
            for (int s = 0; s < SectionCount; s++)
            {
                if (keep[s].Count == 0) continue;
                sb.Append(headers[s]).Append('\n');
                foreach (Line line in keep[s]) sb.Append(line.Text).Append('\n');
            }
            result.Text = sb.ToString();
            return result;
        }

        private static bool IsPresent(string name, IList<string> presentNames)
        {
            if (presentNames == null) return false;
            foreach (string p in presentNames)
                if (NameMatcher.SameCharacter(name, p)) return true;
            return false;
        }

        private static List<string> CleanNames(IList<string> names)
        {
            var list = new List<string>();
            if (names == null) return list;
            foreach (string raw in names)
            {
                string n = Clip(Clean(raw), 40);
                if (n.Length == 0) continue;
                bool seen = false;
                foreach (string have in list)
                    if (NameMatcher.Normalize(have) == NameMatcher.Normalize(n)) { seen = true; break; }
                if (!seen) list.Add(n);
            }
            return list;
        }

        private static string JoinNames(List<string> names)
        {
            const int Max = 6;
            if (names.Count <= Max) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, Max).ToArray())
                   + " and " + (names.Count - Max).ToString(CultureInfo.InvariantCulture) + " more";
        }

        /// <summary>One line, single spaces: player text must not be able to break the block's layout.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Cuts at a word boundary where there is one in the back half, and marks the cut with "...".</summary>
        public static string Clip(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxChars <= 0) return "";
            if (text.Length <= maxChars) return text;
            if (maxChars <= 3) return text.Substring(0, maxChars);
            int cut = maxChars - 3;
            int space = text.LastIndexOf(' ', cut);
            if (space >= cut / 2) cut = space;
            return text.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.') + "...";
        }

        private static string EnsurePeriod(string text)
        {
            if (text.Length == 0) return text;
            char last = text[text.Length - 1];
            return last == '.' || last == '!' || last == '?' ? text : text + ".";
        }
    }
}
