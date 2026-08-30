using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Plain-text prompt files under BepInEx/config/AIROG_DirectedUpdates/, so the two prompt
    /// strings this mod injects can be edited in a normal text editor (multi-line, no escaping)
    /// instead of on one cramped line of the .cfg. Files are re-read when their timestamp
    /// changes, so edits apply on the next AI call without restarting the game. Lines starting
    /// with '#' are comments and are stripped before the text reaches the model.
    /// </summary>
    internal static class PromptFiles
    {
        public const string DOC_ADDENDUM_FILE = "doc_addendum.txt";
        public const string INJECTION_TEMPLATE_FILE = "injection_template.txt";
        private const string README_FILE = "README.txt";
        private const string FOLDER_NAME = "AIROG_DirectedUpdates";

        private class Cached
        {
            public DateTime stampUtc;
            public string text;
        }

        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();

        public static string FolderPath => Path.Combine(Paths.ConfigPath, FOLDER_NAME);

        private static string PathFor(string fileName) => Path.Combine(FolderPath, fileName);

        /// <summary>
        /// Creates the folder, the README and any missing prompt file. A file is only ever
        /// written when it doesn't exist, so player edits are never clobbered.
        /// </summary>
        public static void EnsureSeeded(string fileName, string seedText)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                WriteIfMissing(README_FILE, README_TEXT);
                WriteIfMissing(fileName, seedText ?? "");
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"Could not seed prompt file '{fileName}': {ex.Message}");
            }
        }

        private static void WriteIfMissing(string fileName, string text)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) return;
            File.WriteAllText(path, text.Replace("\\n", "\n").Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            DirectedUpdatesPlugin.LogInfo($"Wrote default prompt file: {path}");
        }

        /// <summary>
        /// Returns the file's contents (comments stripped), or <paramref name="fallback"/> when
        /// prompt files are disabled or the file can't be read.
        /// </summary>
        public static string Read(string fileName, string fallback)
        {
            if (DirectedUpdatesPlugin.UsePromptFiles == null || !DirectedUpdatesPlugin.UsePromptFiles.Value) return fallback;
            try
            {
                string path = PathFor(fileName);
                if (!File.Exists(path)) return fallback;

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (cache.TryGetValue(fileName, out Cached hit) && hit.stampUtc == stamp) return hit.text;

                string text = StripComments(File.ReadAllText(path));
                cache[fileName] = new Cached { stampUtc = stamp, text = text };
                DirectedUpdatesPlugin.LogInfo($"Loaded prompt file '{fileName}' ({text.Length} chars).");
                return text;
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"Could not read prompt file '{fileName}', using config value: {ex.Message}");
                return fallback;
            }
        }

        private static string StripComments(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                if (line.TrimStart().StartsWith("#")) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString().Trim();
        }

        private const string README_TEXT =
            "AIROG_DirectedUpdates - editable prompt files\n" +
            "=============================================\n" +
            "\n" +
            "Each .txt file here is one piece of prompt text this mod injects. Edit it in any\n" +
            "text editor and save - the change applies on the next AI call, no game restart.\n" +
            "\n" +
            "Lines starting with '#' are comments and are stripped before the text is sent to\n" +
            "the AI. Blank lines are kept.\n" +
            "\n" +
            "doc_addendum.txt\n" +
            "  Appended to the GM-facing documentation of the \"update_entities\" action. This is\n" +
            "  what teaches the first-pass model HOW to write instructions. Tune this if the\n" +
            "  instructions you get are too wordy/narrative, or too rare.\n" +
            "\n" +
            "injection_template.txt\n" +
            "  Appended to the story text given to the second-pass \"update this entity\" model.\n" +
            "  Placeholders:\n" +
            "    {entity}       the entity being updated\n" +
            "    {instruction}  the instruction the GM wrote for it\n" +
            "  Tune this if the updater follows the instruction too literally, or throws away\n" +
            "  existing description details it should have kept.\n" +
            "\n" +
            "To restore a default, delete the file - it is rewritten on the next game start.\n" +
            "To ignore this folder entirely, set UsePromptFiles = false in\n" +
            "BepInEx/config/com.airog.directedupdates.cfg.\n";
    }
}

