using System;
using System.Collections.Generic;
using AIROG_Schedules.Logic;

namespace AIROG_Schedules
{
    /// <summary>
    /// Add / edit one entry through the game's own multi-field modal (NTextPromptModal).
    ///
    /// How that modal works (GenericModal.PresentSelfLastStep): Confirm runs Validate() first and
    /// only on true calls every field's callback, then the post-process action, then closes. So
    /// the checking happens in the validate hook, reading the fields while the modal is still
    /// open: a rejected value leaves the modal up with everything the player typed. Enter goes
    /// through the same Confirm button (GameplayManager.EnterKey), Esc through Cancel.
    /// </summary>
    internal static class SchedulesEditor
    {
        /// <summary>Above this many weekdays the day tick boxes become one text field.</summary>
        private const int MaxDayToggles = 10;

        private static readonly List<string> KindLabels = new List<string>
        {
            "Event: something happening at the place",
            "Routine: where the listed characters normally are"
        };

        /// <summary>A new entry for "Add": here, from the current hour, for one hour, every day.</summary>
        public static ScheduleEntry NewEntry(NowSnapshot snap, ClockInfo clock)
        {
            int start = 8 * clock.MinutesPerHour;
            if (snap != null && snap.Clock != null) start = (clock.MinuteOfDay / clock.MinutesPerHour) * clock.MinutesPerHour;
            int end = (start + clock.MinutesPerHour) % clock.MinutesPerDay;
            return new ScheduleEntry
            {
                place = GameProbe.CurrentPlaceName(),
                days = new List<string> { DaySpec.EveryDay },
                start = TimeParser.Format(start, clock.MinutesPerHour),
                end = TimeParser.Format(end, clock.MinutesPerHour),
                kind = ScheduleKinds.Event,
                enabled = true
            };
        }

        /// <summary>
        /// Shows the modal. <paramref name="commit"/> receives the finished entry on Confirm;
        /// <paramref name="closed"/> runs after Confirm or Cancel. Returns false when the modal
        /// could not be shown (nothing else has happened then).
        /// </summary>
        public static bool Open(ScheduleEntry source, bool isNew, ClockInfo clock, Action<ScheduleEntry> commit, Action closed)
        {
            NTextPromptModal modal = NTextPromptModal.I;
            if (modal == null || source == null || clock == null) return false;

            string[] weekNames = clock.WeekdayNames;
            bool dayToggles = weekNames.Length <= MaxDayToggles;
            List<string> unknownDays;
            bool[] mask = DaySpec.Resolve(source.days, weekNames, out unknownDays);

            // The callbacks only run after validation passed; they just collect.
            string title = null, details = null, place = null, start = null, end = null, npcs = null, daysText = null;
            int kind = ScheduleKinds.IsRoutine(source.kind) ? 1 : 0;
            bool enabled = source.enabled;
            bool[] picked = (bool[])mask.Clone();

            var args = new List<NTextPromptModal.PromptArg>
            {
                new NTextPromptModal.TextPromptArg("Title (short: Debate club, Lunch break, Night shift at the forge)",
                    source.title, "Debate club", false, s => title = s),
                new NTextPromptModal.TextPromptArg("Details the storyteller should know while this is on (optional, a sentence or two)",
                    source.details, "Members argue a set motion; visitors may watch from the back.", true, s => details = s),
                new NTextPromptModal.TextPromptArg("Place: name of a sub-location, location or region (blank = anywhere)",
                    source.place, "Debate Club Room", false, s => place = s),
                new NTextPromptModal.TextPromptArg("Start time (" + TimeParser.Examples + ")",
                    source.start, "15:30", false, s => start = s),
                new NTextPromptModal.TextPromptArg("End time (earlier than the start = runs past midnight; same as the start = all day)",
                    source.end, "17:00", false, s => end = s),
                new NTextPromptModal.TextPromptArg("Characters, comma separated (optional for an event, needed for a routine)",
                    string.Join(", ", source.npcs.ToArray()), "Aiko, Ren", false, s => npcs = s)
            };
            if (!dayToggles)
                args.Add(new NTextPromptModal.TextPromptArg("Days, comma separated (or: every day). This world's days: " + string.Join(", ", weekNames),
                    DaySpec.IsEveryDay(source.days) ? DaySpec.EveryDay : string.Join(", ", source.days.ToArray()),
                    DaySpec.EveryDay, false, s => daysText = s));

            args.Add(new NTextPromptModal.DropdownPromptArg("Kind", KindLabels, kind, i => kind = i));
            args.Add(new NTextPromptModal.TogglePromptArg("Enabled", enabled, b => enabled = b));
            if (dayToggles)
                for (int i = 0; i < weekNames.Length; i++)
                {
                    int day = i;
                    args.Add(new NTextPromptModal.TogglePromptArg("Runs on " + weekNames[i], mask[i], b => picked[day] = b));
                }

            ScheduleEntry checkedEntry = null;

            Func<bool> validate = () =>
            {
                try
                {
                    // Row indexes = the order the rows were added above: text rows 0-5 (6 = days as
                    // text), one dropdown, toggle 0 = Enabled, toggles 1.. = the weekdays.
                    var days = new bool[weekNames.Length];
                    if (dayToggles)
                        for (int i = 0; i < weekNames.Length; i++) days[i] = modal.HackyGetToggleIsOn(1 + i);

                    string error;
                    ScheduleEntry built = EntryForm.Build(
                        modal.HackyGetTxt(0), modal.HackyGetTxt(1), modal.HackyGetTxt(2), modal.HackyGetTxt(3),
                        modal.HackyGetTxt(4), modal.HackyGetTxt(5), dayToggles ? null : (modal.HackyGetTxt(6) ?? ""),
                        modal.HackyGetDropdownVal(0) == 1, modal.HackyGetToggleIsOn(0), days, clock, out error);
                    if (built == null)
                    {
                        ShowError(modal, error);
                        return false;
                    }
                    checkedEntry = built;
                    return true;
                }
                catch (Exception ex)
                {
                    SchedulesPlugin.LogWarning("Schedule editor validation failed: " + ex);
                    ShowError(modal, "Could not read the form (" + ex.Message + ").");
                    return false;
                }
            };

            Action confirmed = () =>
            {
                try
                {
                    // Built from what the callbacks delivered; falls back to the validated copy if
                    // a callback did not fire.
                    ScheduleEntry built = null;
                    if (title != null)
                    {
                        string error;
                        built = EntryForm.Build(title, details, place, start, end, npcs, dayToggles ? null : (daysText ?? ""),
                            kind == 1, enabled, picked, clock, out error);
                    }
                    built = built ?? checkedEntry;
                    if (built != null) commit(built);
                }
                catch (Exception ex)
                {
                    SchedulesPlugin.LogWarning("Could not store the schedule entry: " + ex);
                }
                finally
                {
                    if (closed != null) closed();
                }
            };

            Action cancelled = () => { if (closed != null) closed(); };

            modal.PresentSelf(args, isNew ? "New schedule entry" : "Edit schedule entry", confirmed, cancelled, validate);
            return true;
        }

        /// <summary>
        /// The message goes into the modal's own title line as well as a toast: the title is part
        /// of the modal, so it is visible whatever is drawn above or below a toast.
        /// </summary>
        private static void ShowError(NTextPromptModal modal, string message)
        {
            try
            {
                if (modal.mainTitleTxt != null) modal.mainTitleTxt.SetText("<color=#ff9090>" + message + "</color>");
            }
            catch { }
            try
            {
                if (Toast.I != null) Toast.I.ShowToast(message);
            }
            catch { }
        }
    }
}
