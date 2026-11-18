using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AIROG_GenContext;
using AIROG_Schedules.Logic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AIROG_Schedules
{
    /// <summary>
    /// Schedules: recurring, weekday-aware timetables for places ("the debate club meets in the
    /// debate club room, Monday and Wednesday 15:30-17:00") and routines for characters ("works at
    /// the forge 08:00-17:00"), fed to the storyteller as context.
    ///
    /// GenContext is a hard dependency: the whole mod is a context provider and does nothing
    /// without it.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext")]
    public class SchedulesPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.airog.schedules";
        public const string PLUGIN_NAME    = "Schedules";
        public const string PLUGIN_VERSION = "1.0.0";

        /// <summary>Per-save switch key = provider name = label in GenContext's Mods checklist. Never rename.</summary>
        public const string SWITCH_KEY = "Schedules";

        private static ManualLogSource _log;
        private static readonly HashSet<string> _warnedOnce = new HashSet<string>();

        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<string> CfgWeekdayNames;
        public static ConfigEntry<int> CfgDefaultWeekLength;
        public static ConfigEntry<int> CfgSoonWindowMinutes;
        public static ConfigEntry<int> CfgMaxHappeningNow;
        public static ConfigEntry<int> CfgMaxSoonOrEnded;
        public static ConfigEntry<int> CfgMaxExpectedHere;
        public static ConfigEntry<int> CfgMaxElsewhere;
        public static ConfigEntry<int> CfgMaxNearby;
        public static ConfigEntry<int> CfgMaxDetailChars;
        public static ConfigEntry<int> CfgMaxBlockChars;

        private void Awake()
        {
            _log = Logger;

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "Master switch for every save. Off = nothing is added to prompts and the HUD button is hidden. " +
                "To switch the mod off for one save only, use the Mods checklist in game instead.");

            CfgWeekdayNames = Config.Bind("Calendar", "WeekdayNames", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday",
                "Comma separated weekday names. ONLY used in saves whose in-game calendar has no day names of its own; " +
                "when the game calendar names its days, those names and that week length are used.");
            CfgDefaultWeekLength = Config.Bind("Calendar", "DefaultWeekLength", 7,
                new ConfigDescription("Days per week, for the same case as WeekdayNames (a calendar without day names). " +
                                      "Each save can override it in the Schedules panel. Days beyond the name list are called 'Day N'.",
                    new AcceptableValueRange<int>(1, 100)));

            CfgSoonWindowMinutes = Config.Bind("Injection", "SoonWindowMinutes", 30,
                new ConfigDescription("In-game minutes before a start and after an end during which an entry is mentioned as 'starting soon' or 'just ended'. 0 turns both off.",
                    new AcceptableValueRange<int>(0, 600)));
            CfgMaxHappeningNow = Config.Bind("Injection", "MaxHappeningNow", 4,
                new ConfigDescription("Most 'happening here now' lines per prompt.", new AcceptableValueRange<int>(0, 20)));
            CfgMaxSoonOrEnded = Config.Bind("Injection", "MaxSoonOrEnded", 3,
                new ConfigDescription("Most 'starting soon' lines, and separately most 'just ended' lines.", new AcceptableValueRange<int>(0, 20)));
            CfgMaxExpectedHere = Config.Bind("Injection", "MaxExpectedHere", 5,
                new ConfigDescription("Most 'expected here now' lines (character routines at this place).", new AcceptableValueRange<int>(0, 20)));
            CfgMaxElsewhere = Config.Bind("Injection", "MaxNormallyElsewhere", 4,
                new ConfigDescription("Most 'normally elsewhere now' lines (characters present or named whose schedule puts them somewhere else).", new AcceptableValueRange<int>(0, 20)));
            CfgMaxNearby = Config.Bind("Injection", "MaxElsewhereInLocation", 3,
                new ConfigDescription("Most lines about things running in other rooms of the location the player is in. 0 turns the section off.", new AcceptableValueRange<int>(0, 20)));
            CfgMaxDetailChars = Config.Bind("Injection", "MaxDetailChars", 160,
                new ConfigDescription("Longest 'details' text sent per entry; longer text is cut at a word and ends in '...'.", new AcceptableValueRange<int>(0, 600)));
            CfgMaxBlockChars = Config.Bind("Injection", "MaxBlockChars", 1600,
                new ConfigDescription("Ceiling for the whole block in characters (about 4 per token). The block also never exceeds the budget GenContext hands it.",
                    new AcceptableValueRange<int>(300, 6000)));

            Logger.LogInfo($"{PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            // One call per patch class, each on its own: a target that moves in a future game
            // build then costs that one feature, not the provider or the console commands.
            // (Spelled out rather than looped so tools\patch-audit can see every registration.)
            try { Harmony.CreateAndPatchAll(typeof(Patch_ReadSaveFile)); }
            catch (Exception ex) { Logger.LogError("Load hook failed to apply: " + ex.Message); }
            try { Harmony.CreateAndPatchAll(typeof(Patch_WriteSaveFile)); }
            catch (Exception ex) { Logger.LogError("Save hook failed to apply: " + ex.Message); }
            try { Harmony.CreateAndPatchAll(typeof(Patch_DoNewGame)); }
            catch (Exception ex) { Logger.LogError("New-game hook failed to apply: " + ex.Message); }
            try { Harmony.CreateAndPatchAll(typeof(SchedulesUI.Patch_MainLayouts)); }
            catch (Exception ex) { Logger.LogError("HUD button hook failed to apply: " + ex.Message); }
            try { Harmony.CreateAndPatchAll(typeof(SchedulesUI.Patch_EscKey)); }
            catch (Exception ex) { Logger.LogError("Esc hook failed to apply (the panel still closes with its X button): " + ex.Message); }
            try { Harmony.CreateAndPatchAll(typeof(Patch_ConsoleCommands)); }
            catch (Exception ex) { Logger.LogError("Console command hook failed to apply: " + ex.Message); }

            ContextManager.RegisterProvider(new SchedulesProvider());

            // UI only (see ModToggles.Reloaded: it can fire in the middle of a load).
            ModToggles.Changed += (key, enabled) => { if (key == SWITCH_KEY) SchedulesUI.ApplySwitch(); };
            ModToggles.Reloaded += SchedulesUI.ApplySwitch;
            CfgEnabled.SettingChanged += (sender, args) => SchedulesUI.ApplySwitch();

            SeedReadme();
        }

        // ---- switches ---------------------------------------------------------------------------

        /// <summary>The config flag and this save's switch in the Mods checklist.</summary>
        public static bool IsActive()
        {
            if (CfgEnabled != null && !CfgEnabled.Value) return false;
            return ModToggles.IsEnabled(SWITCH_KEY);
        }

        // ---- config helpers ---------------------------------------------------------------------

        internal static List<string> ConfiguredWeekdayNames()
        {
            return DaySpec.ParseList(CfgWeekdayNames != null ? CfgWeekdayNames.Value : "");
        }

        internal static int DefaultWeekLength()
        {
            int n = CfgDefaultWeekLength != null ? CfgDefaultWeekLength.Value : 7;
            return n < 1 ? 7 : n;
        }

        internal static int SoonWindow()
        {
            return CfgSoonWindowMinutes != null ? Math.Max(0, CfgSoonWindowMinutes.Value) : 30;
        }

        internal static BlockOptions BlockOptionsFromConfig()
        {
            return new BlockOptions
            {
                SoonWindowMinutes = SoonWindow(),
                MaxHereNow = Math.Max(0, CfgMaxHappeningNow.Value),
                MaxSoonOrEnded = Math.Max(0, CfgMaxSoonOrEnded.Value),
                MaxExpectedHere = Math.Max(0, CfgMaxExpectedHere.Value),
                MaxElsewhere = Math.Max(0, CfgMaxElsewhere.Value),
                MaxNearby = Math.Max(0, CfgMaxNearby.Value),
                MaxDetailChars = Math.Max(0, CfgMaxDetailChars.Value),
                MaxBlockChars = Math.Max(0, CfgMaxBlockChars.Value)
            };
        }

        // ---- logging ----------------------------------------------------------------------------

        internal static void LogInfo(string message) { if (_log != null) _log.LogInfo(message); }
        internal static void LogWarning(string message) { if (_log != null) _log.LogWarning(message); }

        /// <summary>For failures that would otherwise repeat on every prompt.</summary>
        internal static void LogWarningOnce(string key, string message)
        {
            lock (_warnedOnce) if (!_warnedOnce.Add(key)) return;
            LogWarning(message);
        }

        // ---- README -----------------------------------------------------------------------------

        private void SeedReadme()
        {
            try
            {
                string text;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIROG_Schedules.README.txt"))
                {
                    if (stream == null) return;
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
                }

                string dir = Path.Combine(Paths.ConfigPath, "AIROG_Schedules");
                string path = Path.Combine(dir, "README.txt");
                // Written on first run, and again when a newer version brings a newer README.
                if (File.Exists(path) && File.ReadAllText(path) == text) return;
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, text);
                Logger.LogInfo("README written to " + path);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not write the README: " + ex.Message);
            }
        }

        // ---- save / load / new game -------------------------------------------------------------
        // Each body is wrapped: these run inside the game's own load and save, and a schedule file
        // problem must never surface there.

        [HarmonyPatch(typeof(SaveIO), nameof(SaveIO.ReadSaveFile))]
        public static class Patch_ReadSaveFile
        {
            [HarmonyPostfix]
            public static void Postfix(string saveSubDir)
            {
                try { ScheduleStore.OnSaveFileRead(saveSubDir); }
                catch (Exception ex) { LogWarning("Load hook error: " + ex); }
            }
        }

        [HarmonyPatch(typeof(SaveIO), nameof(SaveIO.WriteSaveFile))]
        public static class Patch_WriteSaveFile
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                try { ScheduleStore.Save(); }
                catch (Exception ex) { LogWarning("Save hook error: " + ex); }
            }
        }

        [HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.doNewGame))]
        public static class Patch_DoNewGame
        {
            // A void prefix: it can never skip the async original.
            [HarmonyPrefix]
            public static void Prefix()
            {
                try { ScheduleStore.OnNewGame(); }
                catch (Exception ex) { LogWarning("New-game hook error: " + ex); }
            }
        }

        // ---- console commands -------------------------------------------------------------------
        // All mods share this one prefix chain: return true for anything that is not ours, never throw.

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        public static class Patch_ConsoleCommands
        {
            [HarmonyPrefix]
            public static bool Prefix(string txt)
            {
                string cmd;
                try { cmd = (txt ?? "").Trim().ToUpperInvariant(); }
                catch { return true; }

                bool now = cmd == "SCHEDULE_NOW" || cmd == "SCHEDULES_NOW";
                bool list = cmd == "SCHEDULE_LIST" || cmd == "SCHEDULES_LIST";
                if (!now && !list) return true;

                try
                {
                    string report = now ? Reports.Now() : Reports.List();
                    LogInfo(cmd + "\n" + report);
                    if (MessageModal.I != null) MessageModal.I.ShowModal(report, true, false);
                }
                catch (Exception ex)
                {
                    LogWarning(cmd + " failed: " + ex);
                }
                return false;
            }
        }
    }

    /// <summary>Text for the console commands (also written to the BepInEx log).</summary>
    internal static class Reports
    {
        public static string Now()
        {
            var sb = new StringBuilder();
            NowSnapshot snap = GameProbe.Capture();
            bool active = SchedulesPlugin.IsActive();
            sb.Append("Schedules ").Append(SchedulesPlugin.PLUGIN_VERSION).Append(" - ")
              .Append(active ? "ON for this save" : "OFF for this save (nothing is injected)").Append('\n');

            if (snap.Clock == null)
            {
                sb.Append("Clock: none. ").Append(snap.NoClockReason).Append('\n');
            }
            else
            {
                ClockInfo c = snap.Clock;
                sb.Append("Now: ").Append(c.WeekdayName).Append(' ').Append(c.TimeText)
                  .Append("  (in-game day ").Append(c.DayIndex + 1).Append(", weekday ").Append(c.WeekdayIndex + 1)
                  .Append(" of ").Append(c.WeekLength).Append(", ")
                  .Append(snap.WeekSource == WeekSource.GameCalendar ? "from the game calendar" : "derived: (day + offset " + c.WeekOffset + ") mod week length")
                  .Append(")\n");
                if (c.HoursPerDay != 24 || c.MinutesPerHour != 60)
                    sb.Append("Day length: ").Append(c.HoursPerDay).Append(" hours of ").Append(c.MinutesPerHour).Append(" minutes\n");
            }

            sb.Append("Place chain: ").Append(snap.PlaceChain.Count > 0 ? string.Join(" < ", snap.PlaceChain.ToArray()) : "(none)").Append('\n');
            sb.Append("Present: ").Append(snap.PresentNames.Count > 0 ? string.Join(", ", snap.PresentNames.ToArray()) : "(nobody)").Append('\n');
            sb.Append("Entries: ").Append(ScheduleStore.Data.entries.Count).Append('\n');

            BlockResult block = SchedulesProvider.BuildBlock(snap, "", ContextManager.MAX_TOTAL_TOKENS);
            sb.Append(active ? "\n--- block injected right now" : "\n--- block that would be injected right now if the mod were on");
            if (block.Text.Length > 0)
                sb.Append(" (").Append(block.Text.Length).Append(" characters, about ").Append(block.Text.Length / ContextManager.CHAR_LIMIT_PER_TOKEN).Append(" tokens)");
            sb.Append(" ---\n");
            sb.Append(block.Text.Length > 0 ? block.Text.Trim('\n') : "(nothing: no entry is relevant here and now)").Append('\n');
            if (block.DroppedForCap + block.DroppedForBudget > 0)
                sb.Append("(left out: ").Append(block.DroppedForCap).Append(" over the per-section caps, ")
                  .Append(block.DroppedForBudget).Append(" over the size limit)\n");
            sb.Append("\nA real prompt can add 'normally elsewhere' lines for characters the story text names, and can be shorter when other mods have used up the shared budget.");
            return sb.ToString();
        }

        public static string List()
        {
            var sb = new StringBuilder();
            NowSnapshot snap = GameProbe.Capture();
            List<ScheduleEntry> entries = ScheduleStore.Data.entries;
            bool hasClock = snap.Clock != null;
            ClockInfo clock = snap.Clock ?? SchedulesUI.FallbackClock();
            sb.Append(entries.Count).Append(entries.Count == 1 ? " schedule entry" : " schedule entries");
            if (hasClock) sb.Append("  (now: ").Append(clock.WeekdayName).Append(' ').Append(clock.TimeText).Append(')');
            else sb.Append("  (no in-game clock: ").Append(snap.NoClockReason).Append(')');
            sb.Append('\n');

            for (int i = 0; i < entries.Count; i++)
            {
                ScheduleEntry e = entries[i];
                EntryState st = ScheduleEngine.Evaluate(e, clock, SchedulesPlugin.SoonWindow());
                sb.Append(i + 1).Append(". [").Append(e.enabled ? "on" : "off").Append("] ")
                  .Append(string.IsNullOrEmpty(e.title) ? "(untitled)" : e.title)
                  .Append(" | ").Append(ScheduleKinds.Normalize(e.kind))
                  .Append(" | ").Append(PlaceMatcher.IsAnywhere(e.place) ? "anywhere" : e.place)
                  .Append(" | ").Append(DaySpec.Describe(e.days, clock.WeekdayNames))
                  .Append(" | ").Append(e.start).Append('-').Append(e.end);
                if (e.npcs.Count > 0) sb.Append(" | ").Append(string.Join(", ", e.npcs.ToArray()));
                if (hasClock && !st.Runnable) sb.Append(" | CANNOT RUN: ").Append(st.Problem);
                else if (hasClock && e.enabled && st.Status != EntryStatus.Inactive) sb.Append(" | ").Append(st.Status);
                sb.Append('\n');
                if (!string.IsNullOrEmpty(e.details)) sb.Append("     ").Append(e.details).Append('\n');
            }
            if (entries.Count == 0) sb.Append("Open the Schedules panel (HUD button 'Sched') and press Add entry.\n");
            return sb.ToString();
        }
    }
}
