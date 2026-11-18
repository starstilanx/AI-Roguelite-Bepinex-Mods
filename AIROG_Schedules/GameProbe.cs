using System;
using System.Collections.Generic;
using AIROG_Schedules.Logic;

namespace AIROG_Schedules
{
    internal enum WeekSource
    {
        None,
        /// <summary>The game's own calendar names the days: the mod follows it exactly.</summary>
        GameCalendar,
        /// <summary>The calendar has a clock but no day names: weekday = (day + per-save offset) mod length.</summary>
        Derived
    }

    /// <summary>Everything the mod reads from the running game for one moment.</summary>
    internal sealed class NowSnapshot
    {
        public ClockInfo Clock;                 // null when the save has no in-game clock
        public WeekSource WeekSource = WeekSource.None;
        public string NoClockReason;

        /// <summary>Current sub-location first, then enclosing locations, region, world.</summary>
        public List<string> PlaceChain = new List<string>();
        public string TopLocationName;
        public List<string> NearbyPlaces = new List<string>();
        public List<string> PresentNames = new List<string>();
    }

    /// <summary>
    /// The only file that touches game objects for reading. Every read is guarded: this runs
    /// inside the prompt builder and on a HUD button, in saves of any age and at any point of
    /// loading, and a schedule is never worth a broken turn.
    /// </summary>
    internal static class GameProbe
    {
        public static GameplayManager Manager()
        {
            try
            {
                GameplayManager manager = SS.I != null ? SS.I.hackyManager : null;
                return manager == null ? null : manager; // Unity-null (destroyed on scene change) counts as none
            }
            catch { return null; }
        }

        public static NowSnapshot Capture()
        {
            var snap = new NowSnapshot();
            GameplayManager manager = Manager();
            if (manager == null)
            {
                snap.NoClockReason = "No game is running.";
                return snap;
            }
            ReadClock(manager, snap);
            ReadPlace(manager, snap);
            ReadPresent(manager, snap);
            return snap;
        }

        // ---- clock -----------------------------------------------------------------------------

        private static CalendarConfig Calendar(GameplayManager manager, out UniverseInfo universe)
        {
            universe = null;
            // GameplayManager.GetCalConfig() dereferences GetCurrentUniverse() without a null check,
            // and that can be null (or throw) while a save is still loading.
            try { universe = manager.GetCurrentUniverse(); } catch { }
            return universe != null ? universe.calendarConfig : null;
        }

        private static void ReadClock(GameplayManager manager, NowSnapshot snap)
        {
            try
            {
                UniverseInfo universe;
                CalendarConfig cal = Calendar(manager, out universe);
                if (cal == null)
                {
                    // Same test as GameplayManager.UsesDatetime(): without a calendar the game does
                    // not advance inGameElapsedSecs at all.
                    snap.NoClockReason = "This save has no in-game calendar, so it has no time of day. Schedules are kept but cannot apply.";
                    return;
                }

                IList<string> names;
                int weekLength, offset;
                if (cal.day_names != null && cal.day_names.Count > 0)
                {
                    snap.WeekSource = WeekSource.GameCalendar;
                    names = cal.day_names;
                    weekLength = cal.day_names.Count;
                    offset = cal.anchor_day_offset;
                }
                else
                {
                    snap.WeekSource = WeekSource.Derived;
                    ScheduleFile data = ScheduleStore.Data;
                    weekLength = data.weekLength > 0 ? data.weekLength : SchedulesPlugin.DefaultWeekLength();
                    names = SchedulesPlugin.ConfiguredWeekdayNames();
                    offset = data.weekdayOffset;
                }

                snap.Clock = ClockMath.FromElapsedSeconds(universe.inGameElapsedSecs, cal.minutes_per_hour, cal.hours_per_day,
                    weekLength, offset, names);
            }
            catch (Exception ex)
            {
                snap.Clock = null;
                snap.WeekSource = WeekSource.None;
                snap.NoClockReason = "The in-game clock could not be read (" + ex.Message + ").";
            }
        }

        /// <summary>
        /// Makes today the given weekday. With a game calendar this writes the calendar's own
        /// day-of-week offset (CalendarConfig.anchor_day_offset, the field the game's right-click
        /// date editor edits), so the HUD clock, the "Current datetime" line in the prompt and
        /// this mod can never name different days. Without day names it is the mod's own offset.
        /// </summary>
        public static string SetToday(int weekdayIndex)
        {
            try
            {
                GameplayManager manager = Manager();
                NowSnapshot snap = new NowSnapshot();
                if (manager == null) return "No game is running.";
                ReadClock(manager, snap);
                if (snap.Clock == null) return snap.NoClockReason;

                ClockInfo clock = snap.Clock;
                int target = ClockMath.WeekdayOf(weekdayIndex, 0, clock.WeekLength);
                int offset = ClockMath.OffsetForToday(clock.DayIndex, target, clock.WeekLength);
                string name = clock.WeekdayNames[target];

                if (snap.WeekSource == WeekSource.GameCalendar)
                {
                    if (MpSession.IsClient) return "In co-op the weekday belongs to the host's calendar.";
                    UniverseInfo universe;
                    CalendarConfig cal = Calendar(manager, out universe);
                    if (cal == null) return snap.NoClockReason ?? "No calendar.";
                    cal.anchor_day_offset = offset;
                    try { if (manager.dateTimeDisp != null) manager.dateTimeDisp.UpdateDisp(); } catch { }
                    try
                    {
                        if (manager.journalModal != null && manager.journalModal.calEventsView != null)
                            manager.journalModal.calEventsView.RedrawEvents();
                    }
                    catch { }
                    return "Today is now " + name + " (the game calendar was shifted to match).";
                }

                ScheduleStore.SetWeekdayOffset(offset);
                return "Today is now " + name + ".";
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("SetToday failed: " + ex);
                return "Could not change the weekday: " + ex.Message;
            }
        }

        // ---- place -----------------------------------------------------------------------------

        private static string NameOf(GameEntity entity)
        {
            try { return entity != null ? (entity.GetPrettyName() ?? "").Trim() : ""; }
            catch { return ""; }
        }

        private static void ReadPlace(GameplayManager manager, NowSnapshot snap)
        {
            try
            {
                Place place = manager.currentPlace;
                if (place == null) return;

                // Slot 0 is always the current place, even if it has no name yet, so that a match
                // depth of 0 always means "exactly here".
                snap.PlaceChain.Add(NameOf(place));
                Place top = place;
                for (int guard = 0; guard < 64; guard++)
                {
                    Place parent = null;
                    try { parent = top.ParentPl(); } catch { }
                    if (parent == null) break;
                    top = parent;
                    string name = NameOf(top);
                    if (name.Length > 0) snap.PlaceChain.Add(name);
                }
                snap.TopLocationName = NameOf(top);

                Region region = null;
                try { region = top.GetRegion(); } catch { }
                if (region != null)
                {
                    string regionName = NameOf(region);
                    if (regionName.Length > 0) snap.PlaceChain.Add(regionName);
                    string worldName = NameOf(region.parentVw);
                    if (worldName.Length > 0) snap.PlaceChain.Add(worldName);
                }

                // Every other room of the same top-level location, for "elsewhere in the building".
                try
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string inChain in snap.PlaceChain) seen.Add(PlaceMatcher.Normalize(inChain));
                    foreach (Place other in top.GetSelfAndDescendants())
                    {
                        if (snap.NearbyPlaces.Count >= 400) break;
                        string name = NameOf(other);
                        if (name.Length > 0 && seen.Add(PlaceMatcher.Normalize(name))) snap.NearbyPlaces.Add(name);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Could not read the current place: " + ex.Message);
            }
        }

        // ---- who is present --------------------------------------------------------------------

        private static void ReadPresent(GameplayManager manager, NowSnapshot snap)
        {
            try
            {
                if (manager.scenarioState == null) return;
                // Followers plus the living NPCs and enemies of the current place: the same list
                // the game uses for "nearby characters" in its own prompts.
                List<GameCharacter> present = manager.GetNpcsEnemiesAndMaybeFollowers();
                if (present == null) return;
                foreach (GameCharacter ch in present)
                {
                    string name = NameOf(ch);
                    if (name.Length > 0 && !snap.PresentNames.Contains(name)) snap.PresentNames.Add(name);
                }
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Could not list the characters present: " + ex.Message);
            }
        }

        /// <summary>The player's current place name, for pre-filling a new entry.</summary>
        public static string CurrentPlaceName()
        {
            GameplayManager manager = Manager();
            try { return manager != null ? NameOf(manager.currentPlace) : ""; }
            catch { return ""; }
        }
    }
}
