using System;
using System.IO;
using System.Text;
using BepInEx;

namespace AIROG_Wildcards
{
    /// <summary>
    /// Where the wildcard files live (BepInEx/config/AIROG_Wildcards/wildcards/, or the folder
    /// named in the config) and the one-time seeding of the README and two example files.
    /// </summary>
    internal static class WildcardFiles
    {
        private const string FOLDER_NAME = "AIROG_Wildcards";
        private const string WILDCARDS_SUBFOLDER = "wildcards";
        private const string README_FILE = "README.txt";

        public static string BaseFolder => Path.Combine(Paths.ConfigPath, FOLDER_NAME);

        public static string DefaultWildcardFolder => Path.Combine(BaseFolder, WILDCARDS_SUBFOLDER);

        /// <summary>
        /// The folder wildcards are read from: the config override when set (absolute, or
        /// relative to BepInEx/config/AIROG_Wildcards/), otherwise the default folder.
        /// </summary>
        public static string ResolveWildcardFolder(string configured)
        {
            string custom = (configured ?? "").Trim().Trim('"');
            if (custom.Length == 0) return DefaultWildcardFolder;
            try
            {
                return Path.IsPathRooted(custom) ? Path.GetFullPath(custom) : Path.GetFullPath(Path.Combine(BaseFolder, custom));
            }
            catch (Exception)
            {
                return DefaultWildcardFolder;
            }
        }

        /// <summary>
        /// Writes the README when it is missing. The example wildcards are only written the first
        /// time the wildcards folder is created, so deleting them is permanent and a player's own
        /// folder dropped in before first launch is left exactly as it is.
        /// </summary>
        public static void Seed()
        {
            bool firstRun = !Directory.Exists(DefaultWildcardFolder);
            Directory.CreateDirectory(DefaultWildcardFolder);
            WriteIfMissing(Path.Combine(BaseFolder, README_FILE), README_TEXT);
            if (!firstRun) return;

            string examples = Path.Combine(DefaultWildcardFolder, "examples");
            Directory.CreateDirectory(examples);
            WriteIfMissing(Path.Combine(examples, "hair_style.txt"), EXAMPLE_HAIR_STYLE);
            WriteIfMissing(Path.Combine(examples, "bangs.txt"), EXAMPLE_BANGS);
        }

        private static void WriteIfMissing(string path, string text)
        {
            if (File.Exists(path)) return;
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            WildcardsPlugin.LogInfo("Wrote " + path);
        }

        private const string EXAMPLE_HAIR_STYLE =
            "# Example wildcard file for AIROG_Wildcards. Use it as __examples/hair_style__\n" +
            "# One option per line. Blank lines are ignored. Lines starting with # are comments.\n" +
            "# A line may contain {choices} and other __wildcards__ of its own.\n" +
            "\n" +
            "long straight hair\n" +
            "shoulder-length wavy hair\n" +
            "a short bob\n" +
            "a high ponytail\n" +
            "twin braids\n" +
            "a messy bun\n" +
            "a {neat|loose} side braid\n" +
            "short cropped hair\n";

        private const string EXAMPLE_BANGS =
            "# Example wildcard file for AIROG_Wildcards. Use it as __examples/bangs__\n" +
            "\n" +
            "blunt bangs\n" +
            "side-swept bangs\n" +
            "curtain bangs\n" +
            "wispy bangs\n" +
            "no bangs\n";

        private const string README_TEXT =
            "AIROG_Wildcards - random picks made by the mod, not by the AI\n" +
            "==============================================================\n" +
            "\n" +
            "Language models are bad at \"pick one at random\": ask for a hairstyle and you get the\n" +
            "same three every time. This mod makes the pick itself, from your own lists, before the\n" +
            "AI ever sees the text. It uses the wildcard syntax of the A1111 \"Dynamic Prompts\"\n" +
            "extension (sd-dynamic-prompts) and reads the same wildcard .txt files.\n" +
            "\n" +
            "Nothing you type is ever rewritten. Your rules keep their __wildcards__ and {a|b}\n" +
            "choices exactly as you wrote them; the mod rolls a fresh copy each time the game builds\n" +
            "a request for the AI.\n" +
            "\n" +
            "\n" +
            "1. WHERE YOUR WILDCARD FILES GO\n" +
            "-------------------------------\n" +
            "  BepInEx/config/AIROG_Wildcards/wildcards/\n" +
            "\n" +
            "Copy the contents of your existing sd-dynamic-prompts \"wildcards\" folder in there,\n" +
            "subfolders and all. hair.txt is used as __hair__, hair/bangs.txt as __hair/bangs__.\n" +
            "Files are read when first used and read again whenever you save them: no restart\n" +
            "needed. (To use a folder somewhere else, set WildcardFolder in\n" +
            "BepInEx/config/com.airog.wildcards.cfg.)\n" +
            "\n" +
            "File format: one option per line. Blank lines are ignored. A line starting with # is a\n" +
            "comment. A line may itself contain wildcards and {a|b} choices. \"3::long hair\" makes\n" +
            "that line three times as likely as an unweighted one.\n" +
            "\n" +
            "Two small example files were created under wildcards/examples/ the first time the mod\n" +
            "ran. Delete them whenever you like; they are not created again.\n" +
            "\n" +
            "\n" +
            "2. SYNTAX\n" +
            "---------\n" +
            "  __name__              one random line from name.txt\n" +
            "  __folder/name__       one random line from folder/name.txt\n" +
            "  __hair/*__            one random line from any file directly inside hair/\n" +
            "  __hair/**__           ... from any file inside hair/, at any depth\n" +
            "  __colours*__          ... from any file whose name starts with \"colours\"\n" +
            "  {red|green|blue}      one of the options\n" +
            "  {3::red|1::blue}      weighted: red is three times as likely (decimals work: 0.5::)\n" +
            "  {red|blue|}           an empty option is allowed (here: one time in three, nothing)\n" +
            "  {2$$a|b|c|d}          two different options, joined with \", \"\n" +
            "  {1-3$$a|b|c|d}        between one and three different options\n" +
            "  {-2$$a|b|c}           up to two (same as 1-2)\n" +
            "  {2-$$a|b|c}           two or more (same as 2-3 here)\n" +
            "  {2$$ and $$a|b|c}     two options joined with \" and \" instead of \", \"\n" +
            "  {2$$__hair__}         two different lines from hair.txt\n" +
            "  \\{  \\}  \\|            a literal { } or | (the backslash is removed)\n" +
            "  \\_\\_name\\_\\_          a literal __name__\n" +
            "\n" +
            "Everything nests: choices inside choices, wildcards inside choices, and wildcard lines\n" +
            "that contain more wildcards and choices. Nesting stops ten levels deep (MaxDepth); past\n" +
            "that the text is left as written, so a file that refers to itself cannot hang the game.\n" +
            "\n" +
            "Spaces and line breaks around each option are ignored, so a long list can be written\n" +
            "one option per line. Write \"{long|short} hair\", not \"{long |short }hair\".\n" +
            "\n" +
            "Wildcard names may use letters, digits, spaces and _ - . ' + & and / for subfolders.\n" +
            "Upper and lower case do not matter.\n" +
            "\n" +
            "What is deliberately left alone:\n" +
            "  - Braces without a | or $$ in them. \"{placeholder}\" and JSON such as\n" +
            "    {\"name\": \"x\", \"tags\": [\"a\", \"b\"]} pass through untouched. So does a JSON object\n" +
            "    whose text happens to contain a |, as long as it starts with a \"key\": pair.\n" +
            "  - ${...}. That is the game's own placeholder syntax (${prompt}), so a { written\n" +
            "    directly after a $ is never read as a choice.\n" +
            "  - Unbalanced braces, and a __name__ that has no file. An unknown wildcard stays in\n" +
            "    the text exactly as typed and is reported once in BepInEx/LogOutput.log.\n" +
            "\n" +
            "Repeat suppression (on by default): a wildcard file hands out every one of its lines\n" +
            "once, in a new random order each time round, before any line comes up again, and it\n" +
            "avoids its last few picks. A short list therefore works through all its options instead\n" +
            "of landing on the same one three times running. It applies to wildcard files only, not\n" +
            "to {a|b} choices, and not to files that use weights (it would cancel the weights).\n" +
            "A {a|b|c} choice is an independent roll every time, so short runs of the same option\n" +
            "are normal there; move the options into a wildcard file if you want them dealt evenly.\n" +
            "\n" +
            "\n" +
            "3. WHERE IT WORKS IN THE GAME\n" +
            "-----------------------------\n" +
            "  a) Entity generation rules\n" +
            "     New game: the Rulesets page. In game: Journal, Story tab, \"Entity Generation\n" +
            "     Rules\". Rolled again for every request that creates or describes something:\n" +
            "     characters, enemies, items, objects, locations, regions, factions, abilities, loot.\n" +
            "\n" +
            "  b) Spawn description boxes\n" +
            "     The \"describe it\" box you get when you spawn a character, enemy, object or\n" +
            "     sub-location, an item or a piece of equipment, a location or region on the map, or\n" +
            "     a region in the region editor, plus the starting item and equipment generators on\n" +
            "     the new game screen. Rolled once, when you confirm the box.\n" +
            "\n" +
            "  c) Image prompt format\n" +
            "     The scenario's \"Image prompt format\" box (Rulesets page, or Journal, Story tab)\n" +
            "     and the prompt format box of each image type in the image generation options: the\n" +
            "     ones that contain ${prompt}. Rolled again for every picture. ${prompt} itself, the\n" +
            "     description the game inserts, is never touched.\n" +
            "\n" +
            "  d) Narrative rules\n" +
            "     The \"Narrative Rules\" box (Rulesets page, or Journal, Story tab) and the\n" +
            "     instruction box that appears in the scenario settings when the narrative flavor is\n" +
            "     set to Custom. Rolled again every turn.\n" +
            "\n" +
            "Each of the four can be switched off on its own in BepInEx/config/com.airog.wildcards.cfg.\n" +
            "\n" +
            "It does NOT apply to: the world background, the player background, extra lore, lorebook\n" +
            "entries, names and descriptions you edit by hand, an entity's optional image\n" +
            "description, negative prompts, anything you type as an action or in chat, the story so\n" +
            "far, or anything the AI writes. Those are either facts that should not change from one\n" +
            "request to the next, or text the mod has no business rewriting.\n" +
            "\n" +
            "In multiplayer the host builds every request, so the host's wildcard files are the ones\n" +
            "that count.\n" +
            "\n" +
            "\n" +
            "4. A WORKED EXAMPLE\n" +
            "-------------------\n" +
            "Put this in Entity generation rules:\n" +
            "\n" +
            "  New human characters have __examples/hair_style__ with __examples/bangs__ and\n" +
            "  {green|blue|brown|grey|hazel} eyes. Ignore this line for creatures, items and places.\n" +
            "\n" +
            "What the AI actually receives for one request:\n" +
            "\n" +
            "  New human characters have twin braids with curtain bangs and brown eyes. Ignore this\n" +
            "  line for creatures, items and places.\n" +
            "\n" +
            "and for the next one:\n" +
            "\n" +
            "  New human characters have a short bob with no bangs and grey eyes. Ignore this line\n" +
            "  for creatures, items and places.\n" +
            "\n" +
            "The description the AI writes is stored by the game as usual, and the character's\n" +
            "portrait is generated from that description, so the rolled hairstyle shows up there too.\n" +
            "\n" +
            "Letting the AI choose WHICH list applies: give it every case and let it use the one\n" +
            "that fits. The mod rolls all of them; the AI ignores the lines that do not apply.\n" +
            "\n" +
            "  If the new character is a woman, her hair is __hair/female__.\n" +
            "  If the new character is a man, his hair is __hair/male__.\n" +
            "  If it is a creature, its hide is {scaled|furred|feathered|chitinous}.\n" +
            "\n" +
            "Things to know:\n" +
            "  - The rules are rolled for every generation request, including ones that have nothing\n" +
            "    to do with characters. That is harmless, but it means \"one roll\" is \"one request\",\n" +
            "    not \"one character\".\n" +
            "  - Vary the art style of pictures with the image prompt format, for example:\n" +
            "      ${prompt}, {oil painting|watercolour|ink sketch|35mm photograph}\n" +
            "\n" +
            "\n" +
            "5. CHECKING YOUR FILES\n" +
            "----------------------\n" +
            "Open the in-game console and type:\n" +
            "\n" +
            "  WILDCARD_TEST a {tall|short} woman with __examples/hair_style__\n" +
            "      shows three sample results in a message box (and in BepInEx/LogOutput.log), and\n" +
            "      names any wildcard it could not find.\n" +
            "  WILDCARD_RELOAD\n" +
            "      forgets all cached files and the repeat-suppression history.\n" +
            "\n" +
            "To watch real rolls as you play, set LogExpansions = true in the .cfg: every expansion\n" +
            "is written to BepInEx/LogOutput.log with what each wildcard and choice became.\n" +
            "\n" +
            "\n" +
            "6. SETTINGS (BepInEx/config/com.airog.wildcards.cfg)\n" +
            "----------------------------------------------------\n" +
            "  [General] Enabled            master switch (default true)\n" +
            "  [General] RepeatSuppression  see section 2 (default true)\n" +
            "  [General] MaxDepth           nesting limit (default 10)\n" +
            "  [General] LogExpansions      log every roll (default false)\n" +
            "  [Areas]   EntityGenerationRules, SpawnInstructions, ImagePromptFormat, NarrativeRules\n" +
            "                               where the syntax is active (all default true)\n" +
            "  [Files]   WildcardFolder     another folder to read wildcards from (default: empty)\n" +
            "\n" +
            "\n" +
            "7. NOT SUPPORTED (compared with the A1111 extension)\n" +
            "----------------------------------------------------\n" +
            "  - Variables: ${name=value} and ${name}. (If a wildcard file contains ${...}, a space\n" +
            "    is put after the $, because the game rejects any request that still contains \"${\".)\n" +
            "  - Comments inside the text itself (#, // and /* */). Rules often use # for headings,\n" +
            "    so it is not treated as a comment. Comments work in wildcard files only, at the\n" +
            "    start of a line.\n" +
            "  - YAML and JSON wildcard files. Plain .txt only.\n" +
            "  - Combinatorial and cyclical samplers ({@a|b}, {~a|b}), wildcard parameters\n" +
            "    (__name(var=x)__), prompt editing, attention syntax and the \"magic prompt\" options.\n" +
            "  - Jinja2 templates.\n" +
            "One difference in your favour: \"3::long hair\" works as a weight inside a .txt wildcard\n" +
            "file. In the A1111 extension that needs a YAML file.\n" +
            "\n" +
            "Three cautions:\n" +
            "  - If you switch this mod off or remove it, also take the wildcard syntax out of your\n" +
            "    rules. The AI would otherwise be shown the raw __names__ and {a|b} text.\n" +
            "  - When the game's AI rewrites your rules for you (asking the narrator to change the\n" +
            "    rules in unified mode), it is shown your rules as written and may drop or change the\n" +
            "    wildcard syntax. The undo button next to the rules in the Journal brings them back.\n" +
            "  - Inside a pick whose first option is itself a choice, put a space after the $$:\n" +
            "    {2$$ {a|b}|c}. It works without the space too, but your text as written would then\n" +
            "    contain \"${\", and the game refuses any request that still contains \"${\" - which\n" +
            "    matters wherever your rules are used unexpanded (the mod switched off, or the AI\n" +
            "    rewriting your rules).\n" +
            "\n" +
            "To get this file back after deleting it, start the game once.\n";
    }
}
