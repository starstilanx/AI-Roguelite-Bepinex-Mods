using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Plain-text prompt files under BepInEx/config/AIROG_DirectedUpdates/, so the prompt
    /// strings this mod injects can be edited in a normal text editor (multi-line, no escaping)
    /// instead of on one cramped line of the .cfg. Files are re-read when their timestamp
    /// changes, so edits apply on the next AI call without restarting the game. Lines starting
    /// with '#' are comments and are stripped before the text reaches the model.
    /// </summary>
    internal static class PromptFiles
    {
        public const string DOC_ADDENDUM_FILE = "doc_addendum.txt";
        public const string INJECTION_TEMPLATE_FILE = "injection_template.txt";
        public const string PLAYER_INJECTION_TEMPLATE_FILE = "player_injection_template.txt";
        public const string DESTINATION_DOC_ADDENDUM_FILE = "destination_doc_addendum.txt";
        public const string DESTINATION_INJECTION_TEMPLATE_FILE = "destination_injection_template.txt";
        private const string README_FILE = "README.txt";
        private const string FOLDER_NAME = "AIROG_DirectedUpdates";

        private class Cached
        {
            public DateTime stampUtc;
            public string text;
        }

        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        private static bool readmeChecked;

        public static string FolderPath => Path.Combine(Paths.ConfigPath, FOLDER_NAME);

        private static string PathFor(string fileName) => Path.Combine(FolderPath, fileName);

        /// <summary>
        /// Creates the folder, the README and any missing prompt file. A prompt file is only
        /// ever written when it doesn't exist, so player edits are never clobbered.
        /// </summary>
        public static void EnsureSeeded(string fileName, string seedText)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                RefreshReadme();
                WriteIfMissing(fileName, seedText ?? "");
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"Could not seed prompt file '{fileName}': {ex.Message}");
            }
        }

        private static string ToDiskText(string text) =>
            text.Replace("\\n", "\n").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

        private static void WriteIfMissing(string fileName, string text)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) return;
            File.WriteAllText(path, ToDiskText(text), new UTF8Encoding(false));
            DirectedUpdatesPlugin.LogInfo($"Wrote default prompt file: {path}");
        }

        // Unlike the prompt files the README is not something the player edits, and one written
        // by an older version would never mention the files and features added since.
        private static void RefreshReadme()
        {
            if (readmeChecked) return;
            readmeChecked = true;
            string path = PathFor(README_FILE);
            string wanted = ToDiskText(README_TEXT);
            if (File.Exists(path) && File.ReadAllText(path) == wanted) return;
            File.WriteAllText(path, wanted, new UTF8Encoding(false));
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
            "AIROG_DirectedUpdates - what it does and its editable prompt files\n" +
            "==================================================================\n" +
            "\n" +
            "1. GM instructions for entity updates (unified story pipeline)\n" +
            "   The story AI (the GM) can attach an \"instruction\" to an update_entities entry.\n" +
            "   It is handed to the follow-up step that rewrites that entity's name,\n" +
            "   description and status effects, and shown on the \"Details updated\" icon.\n" +
            "\n" +
            "2. Your own instruction, any time (both story pipelines)\n" +
            "   Right-click an entity (NPC, enemy, follower, object, item, location or your own\n" +
            "   character), pick \"Instruct update...\" from the menu and type what should\n" +
            "   change, for example:\n" +
            "     add a sprained ankle status effect\n" +
            "     remove the poisoned status\n" +
            "     she now wears the blue cloak instead of the travel coat\n" +
            "     this is now an item the player carries\n" +
            "   The game then runs its normal \"details updated\" step for that entity with the\n" +
            "   most recent story turn as context and your text as the instruction. No story\n" +
            "   turn is spent. A line in the log shows the request; if the AI call fails, click\n" +
            "   the name-tag icon under that line to retry. Leave the box empty or cancel to do\n" +
            "   nothing. In co-op only the host can use this.\n" +
            "   Config section [PlayerInstruction]:\n" +
            "     Enabled        turn the menu entry on or off\n" +
            "     QuickModifier  None, Ctrl, Alt or Shift. When set, holding that key while\n" +
            "                    right-clicking an entity opens the instruction box directly\n" +
            "                    and skips the menu.\n" +
            "\n" +
            "3. GM destinations for departing characters (unified story pipeline)\n" +
            "   The GM can attach a \"destination\" to an npcs_leave entry: a brief name or\n" +
            "   description of where the player would next meet that character. It is handed\n" +
            "   to the step that picks the character's new location on the map, and shown on\n" +
            "   the \"left\" icon. Characters that leave in the same turn with the same\n" +
            "   destination text are placed in the same location.\n" +
            "   Config section [Destinations]: Enabled, ShareSameDestination.\n" +
            "\n" +
            "Prompt files\n" +
            "------------\n" +
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
            "  Appended to the story text given to the second-pass \"update this entity\" model\n" +
            "  when the GM wrote an instruction.\n" +
            "  Placeholders:\n" +
            "    {entity}       the entity being updated\n" +
            "    {instruction}  the instruction the GM wrote for it\n" +
            "  Tune this if the updater follows the instruction too literally, or throws away\n" +
            "  existing description details it should have kept.\n" +
            "\n" +
            "player_injection_template.txt\n" +
            "  The same, for an instruction you typed yourself (feature 2). Same placeholders.\n" +
            "  It tells the updater that this is a direct instruction from the player, to be\n" +
            "  applied like a GM instruction even when the story text does not mention it.\n" +
            "\n" +
            "destination_doc_addendum.txt\n" +
            "  Appended to the GM-facing documentation of the \"npcs_leave\" action. Teaches the\n" +
            "  GM when and how to name a destination. Tune this if destinations are given too\n" +
            "  often, too rarely, or as full paths.\n" +
            "\n" +
            "destination_injection_template.txt\n" +
            "  Added to the prompts of the step that picks where a departed character went.\n" +
            "  Placeholders:\n" +
            "    {entity}       the character who left\n" +
            "    {destination}  the destination text the GM wrote\n" +
            "\n" +
            "To restore a default, delete the file - it is rewritten on the next game start.\n" +
            "To ignore this folder entirely, set UsePromptFiles = false in\n" +
            "BepInEx/config/com.airog.directedupdates.cfg.\n" +
            "This README is rewritten by the mod whenever its text changes; do not keep notes\n" +
            "in it.\n";
    }
}
