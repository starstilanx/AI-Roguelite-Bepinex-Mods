using System;
using System.Collections.Generic;
using System.IO;
using AIROG_Schedules.Logic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIROG_Schedules
{
    /// <summary>
    /// The current save's schedules (schedules.json in the save folder).
    ///
    /// The data in memory always belongs to exactly one save folder, <see cref="_stateDir"/>, and
    /// is only ever written back to that folder. Three things bind it:
    ///   - the game reading the active save (SaveIO.ReadSaveFile postfix),
    ///   - a new game starting (GameplayManager.doNewGame prefix),
    ///   - any read while the game points at a different folder than the one in memory
    ///     (<see cref="EnsureCurrent"/>): co-op clients never run either hook above.
    /// </summary>
    internal static class ScheduleStore
    {
        public const string FILE_NAME = "schedules.json";
        public const string EXPORT_FILE_NAME = "schedules_export.json";

        private static ScheduleFile _data = new ScheduleFile();
        private static string _stateDir;   // save folder _data belongs to (normalized); null = none yet
        private static string _savedJson;  // what is on disk for _stateDir, to skip no-op writes

        /// <summary>Goes up whenever the data changes; the panel redraws when it sees a new value.</summary>
        public static int Revision { get; private set; }

        /// <summary>Set when schedules.json existed but could not be read; shown in the panel.</summary>
        public static string LoadProblem { get; private set; }

        public static ScheduleFile Data
        {
            get
            {
                EnsureCurrent();
                return _data;
            }
        }

        // ---- binding to a save -----------------------------------------------------------------

        /// <summary>The active save folder, or null on the main menu.</summary>
        public static string CurrentSaveDir()
        {
            try
            {
                if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
                return DirFor(SS.I.saveSubDirAsArg);
            }
            catch { return null; }
        }

        private static string DirFor(string saveSubDir)
        {
            if (SS.I == null || string.IsNullOrEmpty(saveSubDir)) return null;
            // saveSubDirAsArg is a bare folder name for loaded saves and a full path for new games
            // (MainMenu builds it with Path.Combine(saveTopLvlDir, guid)); Combine handles both.
            string dir = Path.Combine(SS.I.saveTopLvlDir ?? "", saveSubDir);
            try { dir = Path.GetFullPath(dir); } catch { }
            return dir.TrimEnd('\\', '/');
        }

        private static bool SameDir(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>SaveIO.ReadSaveFile postfix.</summary>
        public static void OnSaveFileRead(string saveSubDir)
        {
            string dir = DirFor(saveSubDir);
            string current = CurrentSaveDir();
            // ReadSaveFile also runs when the main menu rebuilds a missing lite file for some other
            // slot. That is a peek, not a load: only the active save may replace what is in memory.
            if (dir == null || !SameDir(dir, current)) return;
            LoadFromDir(dir);
        }

        /// <summary>
        /// GameplayManager.doNewGame prefix. The new save folder already exists when a preset was
        /// used (MainMenu copies the preset's staging files into it), so a schedules.json that
        /// shipped with a scenario preset is picked up here; otherwise the new game starts empty.
        /// </summary>
        public static void OnNewGame()
        {
            LoadFromDir(CurrentSaveDir());
        }

        /// <summary>Reloads when the game is pointing at another save than the one in memory.</summary>
        public static void EnsureCurrent()
        {
            string dir = CurrentSaveDir();
            if (dir == null || SameDir(dir, _stateDir)) return;
            LoadFromDir(dir);
        }

        private static void LoadFromDir(string dir)
        {
            _data = new ScheduleFile();
            _stateDir = dir;
            _savedJson = null;
            LoadProblem = null;
            Revision++;
            if (dir == null) return;

            string path = Path.Combine(dir, FILE_NAME);
            try
            {
                if (File.Exists(path))
                {
                    var read = JsonConvert.DeserializeObject<ScheduleFile>(File.ReadAllText(path));
                    if (read != null)
                    {
                        read.Sanitize();
                        _data = read;
                    }
                    SchedulesPlugin.LogInfo($"Loaded {_data.entries.Count} schedule entr{(_data.entries.Count == 1 ? "y" : "ies")} from {dir}.");
                }
                _savedJson = Serialize(_data);
            }
            catch (Exception ex)
            {
                // A hand-edited file with a typo must not be silently replaced by an empty one on
                // the next save: keep a copy, start empty, and say so.
                _data = new ScheduleFile();
                LoadProblem = FILE_NAME + " could not be read (" + ex.Message + ")";
                try
                {
                    File.Copy(path, path + ".unreadable", true);
                    LoadProblem += ". A copy was kept as " + FILE_NAME + ".unreadable.";
                }
                catch { }
                SchedulesPlugin.LogWarning(LoadProblem);
            }
        }

        // ---- writing ---------------------------------------------------------------------------

        private static string Serialize(ScheduleFile file)
        {
            return JsonConvert.SerializeObject(file, Formatting.Indented);
        }

        /// <summary>
        /// Writes schedules.json into the save the data belongs to. Does nothing when the game is
        /// pointing at any other folder, and nothing when there is nothing new to write.
        /// </summary>
        public static bool Save()
        {
            try
            {
                string dir = CurrentSaveDir();
                if (dir == null || !SameDir(dir, _stateDir)) return false;
                if (!Directory.Exists(dir)) return false; // the game's first save creates it and calls us again

                string path = Path.Combine(dir, FILE_NAME);
                bool exists = File.Exists(path);
                bool blank = _data.entries.Count == 0 && _data.weekdayOffset == 0 && _data.weekLength == 0;
                if (blank && !exists) return true;        // never used in this save: leave no file behind
                if (blank && LoadProblem != null) return true; // do not replace a file we could not read with an empty one

                string json = Serialize(_data);
                if (exists && json == _savedJson) return true;
                File.WriteAllText(path, json);
                _savedJson = json;
                return true;
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Could not write " + FILE_NAME + ": " + ex.Message);
                return false;
            }
        }

        private static void Changed()
        {
            Revision++;
            Save();
        }

        // ---- edits (all go through here so every change is saved and redrawn) -------------------

        public static void Add(ScheduleEntry entry)
        {
            if (entry == null) return;
            entry.Sanitize();
            Data.entries.Add(entry);
            Changed();
        }

        public static void Replace(int index, ScheduleEntry entry)
        {
            var entries = Data.entries;
            if (entry == null || index < 0 || index >= entries.Count) return;
            entry.Sanitize();
            entries[index] = entry;
            Changed();
        }

        public static void RemoveAt(int index)
        {
            var entries = Data.entries;
            if (index < 0 || index >= entries.Count) return;
            entries.RemoveAt(index);
            Changed();
        }

        public static void Duplicate(int index)
        {
            var entries = Data.entries;
            if (index < 0 || index >= entries.Count) return;
            ScheduleEntry copy = entries[index].Clone();
            copy.title = string.IsNullOrEmpty(copy.title) ? "Copy" : copy.title + " (copy)";
            entries.Insert(index + 1, copy);
            Changed();
        }

        public static void SetEnabled(int index, bool enabled)
        {
            var entries = Data.entries;
            if (index < 0 || index >= entries.Count || entries[index].enabled == enabled) return;
            entries[index].enabled = enabled;
            Changed();
        }

        public static void SetWeekdayOffset(int offset)
        {
            if (Data.weekdayOffset == offset) return;
            _data.weekdayOffset = offset;
            Changed();
        }

        public static void SetWeekLength(int weekLength)
        {
            weekLength = ClockMath.ClampWeekLength(weekLength);
            if (Data.weekLength == weekLength) return;
            _data.weekLength = weekLength;
            Changed();
        }

        // ---- sharing ---------------------------------------------------------------------------

        private class ExportFile
        {
            public int version = 1;
            public string madeWith = "AIROG Schedules " + SchedulesPlugin.PLUGIN_VERSION;
            public string note = "Copy this file into another save folder and press Import in the Schedules panel. " +
                                 "days: weekday names of that world, or \"every day\". start/end: time of day.";
            public List<ScheduleEntry> entries = new List<ScheduleEntry>();
        }

        /// <summary>Writes schedules_export.json next to the save. Returns the message for the player.</summary>
        public static string Export()
        {
            try
            {
                string dir = CurrentSaveDir();
                if (dir == null) return "No save is loaded.";
                EnsureCurrent();
                if (!Directory.Exists(dir)) return "This game has not been saved yet. Save once, then export.";
                var file = new ExportFile();
                foreach (ScheduleEntry e in _data.entries) file.entries.Add(e.Clone());
                string path = Path.Combine(dir, EXPORT_FILE_NAME);
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
                SchedulesPlugin.LogInfo("Exported " + Count(file.entries.Count) + " to " + path);
                return "Exported " + Count(file.entries.Count) + " to " + EXPORT_FILE_NAME + " in this save's folder.";
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Export failed: " + ex);
                return "Export failed: " + ex.Message;
            }
        }

        /// <summary>Merges schedules_export.json from the save folder. Returns the message for the player.</summary>
        public static string Import()
        {
            try
            {
                string dir = CurrentSaveDir();
                if (dir == null) return "No save is loaded.";
                EnsureCurrent();
                string path = Path.Combine(dir, EXPORT_FILE_NAME);
                if (!File.Exists(path)) return "Nothing to import: put " + EXPORT_FILE_NAME + " into this save's folder (the Folder button opens it).";

                List<ScheduleEntry> incoming = ReadEntries(File.ReadAllText(path));
                if (incoming == null) return EXPORT_FILE_NAME + " has no \"entries\" list.";

                int skipped;
                int added = _data.MergeFrom(incoming, out skipped);
                if (added > 0) Changed();
                return "Imported " + Count(added) + (skipped > 0 ? ", skipped " + skipped + " already present" : "") + ".";
            }
            catch (Exception ex)
            {
                SchedulesPlugin.LogWarning("Import failed: " + ex);
                return "Import failed: " + EXPORT_FILE_NAME + " is not valid (" + ex.Message + ")";
            }
        }

        /// <summary>Accepts the export wrapper, a schedules.json, or a bare list of entries.</summary>
        private static List<ScheduleEntry> ReadEntries(string json)
        {
            JToken root = JToken.Parse(json);
            JToken list = root.Type == JTokenType.Array ? root : (root.Type == JTokenType.Object ? root["entries"] : null);
            if (list == null || list.Type != JTokenType.Array) return null;
            return list.ToObject<List<ScheduleEntry>>() ?? new List<ScheduleEntry>();
        }

        private static string Count(int n)
        {
            return n + (n == 1 ? " entry" : " entries");
        }
    }
}
