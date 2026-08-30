using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace AIROG_ScenePace
{
    /// <summary>
    /// Plain-text prompt files under BepInEx/config/AIROG_ScenePace/, so the three strings this mod
    /// injects can be edited in a normal text editor instead of on one cramped line of the .cfg.
    /// Files are re-read when their timestamp changes, so edits apply on the next AI call without
    /// restarting. Lines starting with '#' are comments and are stripped before the text is sent.
    ///
    /// Reseeding: on startup a file is rewritten from the current Strength tier only if it is
    /// missing or still matches one of the built-in tier texts verbatim. Once it has been edited it
    /// is never touched again, so changing Strength stops moving a file you have customised.
    /// </summary>
    internal static class PromptFiles
    {
        public const string STORY_SCOPE_FILE = "story_scope.txt";
        public const string ROLL_SCOPE_FILE = "roll_scope.txt";
        public const string ACTION_SUFFIX_FILE = "action_suffix.txt";
        private const string README_FILE = "README.txt";
        private const string FOLDER_NAME = "AIROG_ScenePace";

        private class Cached
        {
            public DateTime stampUtc;
            public string text;
        }

        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();

        public static string FolderPath => Path.Combine(Paths.ConfigPath, FOLDER_NAME);

        private static string PathFor(string fileName) => Path.Combine(FolderPath, fileName);

        public static string FileFor(RuleKind kind)
        {
            switch (kind)
            {
                case RuleKind.StoryScope: return STORY_SCOPE_FILE;
                case RuleKind.RollScope: return ROLL_SCOPE_FILE;
                case RuleKind.ActionSuffix: return ACTION_SUFFIX_FILE;
                default: return null;
            }
        }

        /// <summary>
        /// Creates the folder and the README, then writes the current tier's text for
        /// <paramref name="kind"/> unless the existing file has been customised.
        /// </summary>
        public static void Seed(RuleKind kind, Strength strength)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                WriteIfMissing(README_FILE, README_TEXT);

                string fileName = FileFor(kind);
                string path = PathFor(fileName);
                string desired = SceneRules.Get(kind, strength) ?? "";

                if (File.Exists(path))
                {
                    string current = StripComments(File.ReadAllText(path));
                    if (!IsPristine(kind, current))
                    {
                        ScenePacePlugin.LogInfo($"'{fileName}' has been customised; leaving it alone.");
                        return;
                    }
                    if (current.Trim() == desired.Trim()) return;
                }

                Write(path, HEADER_FOR[kind] + desired);
                cache.Remove(fileName);
                ScenePacePlugin.LogInfo($"Wrote {fileName} for Strength={strength}.");
            }
            catch (Exception ex)
            {
                ScenePacePlugin.LogWarn($"Could not seed prompt file for {kind}: {ex.Message}");
            }
        }

        /// <summary>True when the text is empty or still matches a built-in tier verbatim.</summary>
        private static bool IsPristine(RuleKind kind, string text)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return true;
            foreach (string variant in SceneRules.AllVariants(kind))
            {
                if (trimmed == (variant ?? "").Trim()) return true;
            }
            return false;
        }

        private static void WriteIfMissing(string fileName, string text)
        {
            string path = PathFor(fileName);
            if (File.Exists(path)) return;
            Write(path, text);
        }

        private static void Write(string path, string text)
        {
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        }

        /// <summary>
        /// Returns the file's contents (comments stripped), or the built-in text for
        /// <paramref name="strength"/> when prompt files are disabled or the file can't be read.
        /// </summary>
        public static string Read(RuleKind kind, Strength strength)
        {
            string fallback = SceneRules.Get(kind, strength) ?? "";
            if (ScenePacePlugin.UsePromptFiles == null || !ScenePacePlugin.UsePromptFiles.Value) return fallback;

            string fileName = FileFor(kind);
            if (fileName == null) return fallback;

            try
            {
                string path = PathFor(fileName);
                if (!File.Exists(path)) return fallback;

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (cache.TryGetValue(fileName, out Cached hit) && hit.stampUtc == stamp) return hit.text;

                string text = StripComments(File.ReadAllText(path));
                cache[fileName] = new Cached { stampUtc = stamp, text = text };
                ScenePacePlugin.LogInfo($"Loaded prompt file '{fileName}' ({text.Length} chars).");
                return text;
            }
            catch (Exception ex)
            {
                ScenePacePlugin.LogWarn($"Could not read prompt file '{fileName}', using built-in text: {ex.Message}");
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

        private static readonly Dictionary<RuleKind, string> HEADER_FOR = new Dictionary<RuleKind, string>
        {
            [RuleKind.StoryScope] =
                "# story_scope.txt - appended to the GM's description of the \"story\" field (unified mode).\n" +
                "# Bounds WHERE THE SCENE ENDS. ${player_or_entity} is substituted by the game.\n" +
                "# Delete this file to restore the default for your Strength setting.\n\n",

            [RuleKind.RollScope] =
                "# roll_scope.txt - appended to the GM's roll guidance (unified mode, roll turns only).\n" +
                "# Bounds WHAT A SUCCESSFUL ROLL ACTUALLY COVERS. ${player_or_entity} is substituted by the game.\n" +
                "# Delete this file to restore the default for your Strength setting.\n\n",

            [RuleKind.ActionSuffix] =
                "# action_suffix.txt - appended to your action text on every player-driven turn.\n" +
                "# Reaches BOTH prompt pipelines, and sits right next to the action, so this is the\n" +
                "# highest-leverage of the three. ${actor_name} is substituted by this mod.\n" +
                "# Avoid double quotes here. Delete this file to restore the default for your Strength setting.\n\n",
        };

        private const string README_TEXT =
            "AIROG_ScenePace - editable prompt files\n" +
            "=======================================\n" +
            "\n" +
            "The problem this mod addresses: when you roll a success on an action that names a goal\n" +
            "(\"cook a meal she would like\"), the GM tends to treat the whole clause as succeeding and\n" +
            "narrates straight through preparation, execution, serving, her tasting it and her liking\n" +
            "it - skipping every choice, reaction and roll you would have made along the way.\n" +
            "\n" +
            "Two rules fix that, and each .txt file here is one piece of the prompt text that carries\n" +
            "them. Edit any file in a text editor and save - the change applies on the next AI call,\n" +
            "with no game restart.\n" +
            "\n" +
            "  1. A roll resolves the ATTEMPT, not the AMBITION. Success means the cooking went well,\n" +
            "     not that she liked it. Her verdict is a separate matter.\n" +
            "  2. The story ENDS at your next real decision point - normally the instant another\n" +
            "     character would have to make up their mind.\n" +
            "\n" +
            "story_scope.txt\n" +
            "  Appended to the GM's description of the \"story\" response field. Carries rule 2.\n" +
            "  Unified mode only. Placeholder: ${player_or_entity} (substituted by the game).\n" +
            "\n" +
            "roll_scope.txt\n" +
            "  Appended to the GM's roll guidance, on roll turns. Carries rule 1.\n" +
            "  Unified mode only. Placeholder: ${player_or_entity} (substituted by the game).\n" +
            "\n" +
            "action_suffix.txt\n" +
            "  Appended to your action text itself, so it lands right beside the action the GM is\n" +
            "  resolving. Carries a compressed form of both rules, and is the only one of the three\n" +
            "  that reaches the older non-unified prompt pipeline. Placeholder: ${actor_name}.\n" +
            "  Avoid double quotes in this one.\n" +
            "\n" +
            "Lines starting with '#' are comments and are stripped before the text is sent to the AI.\n" +
            "\n" +
            "Strength (in BepInEx/config/com.airog.scenepace.cfg) chooses how hard the built-in\n" +
            "wording is: Gentle / Firm / Strict. Changing it rewrites any file you have NOT edited.\n" +
            "Once you edit a file, Strength stops overwriting it - delete the file to opt back in.\n" +
            "\n" +
            "To ignore this folder entirely, set UsePromptFiles = false in the same .cfg.\n";
    }
}
