using AIROG_Core;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AIROG_Wildcards
{
    /// <summary>
    /// Wildcards for player-authored prompt text, in the syntax of the A1111 sd-dynamic-prompts
    /// extension: __name__ is replaced by a random line of wildcards/name.txt and {a|b|c} by one
    /// of its options, in code, before the AI sees the text. A language model asked to "pick a
    /// hairstyle" returns the same three; a dice roll does not.
    ///
    /// Expansion happens at prompt-build time, fresh for every AI request, and always on a copy.
    /// The player's stored rules keep their syntax, so the first roll never becomes permanent.
    /// Four seams, each carrying only text the player typed (see Patches.cs for why each is safe):
    ///   1. MmHelper.GetMaybeEntityGenRulesInst postfix - the Entity generation rules box. This
    ///      one call feeds ${maybe_entity_generation_rules} in every entity, item, location,
    ///      region, faction and ability prompt.
    ///   2. AIAsker.Spawn...FromPrompt prefixes - the description boxes of the spawn menus.
    ///   3. EntImgSettings.GetFormatted prefix + postfix - the scenario's Image prompt format and
    ///      the per-image-type formats in Options. The description the game drops into ${prompt}
    ///      is never expanded.
    ///   4. UnifiedPromptBuilder.GetStoryOrUnifiedPreambleStrInJarrayForm postfix - the Narrative
    ///      rules box and the instruction text of the Custom narrative flavor.
    /// Deliberately not hooked: world/player background, extra lore and the lorebook (canon that
    /// must not change between requests), entity descriptions and optional image descriptions
    /// (the AI writes those too), player actions, story history and AI responses.
    ///
    /// The engine itself (Engine\) has no Unity or game dependencies and is covered by the
    /// console harness in Tests\, which is a separate project and is not shipped.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class WildcardsPlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.wildcards";
        public const string PLUGIN_NAME = "Wildcards";
        public const string PLUGIN_VERSION = "1.0.0";

        public static WildcardsPlugin Instance { get; private set; }

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> RepeatSuppression;
        public static ConfigEntry<int> MaxDepth;
        public static ConfigEntry<bool> LogExpansions;
        public static ConfigEntry<bool> AreaEntityRules;
        public static ConfigEntry<bool> AreaSpawnInstructions;
        public static ConfigEntry<bool> AreaImagePromptFormat;
        public static ConfigEntry<bool> AreaNarrativeRules;
        public static ConfigEntry<string> WildcardFolder;

        protected override void Awake()
        {
            base.Awake();
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false, every hook passes text straight through and the AI sees your " +
                "__wildcards__ and {a|b} choices as raw text. The console commands keep working.");
            RepeatSuppression = Config.Bind("General", "RepeatSuppression", true,
                "A wildcard file hands out each of its lines once, in a new random order every time round, " +
                "before any line comes up again, and avoids its last few picks. Stops a short list landing on " +
                "the same option several times running. Not applied to {a|b} choices or to files that use " +
                "weights. When false, every pick is an independent random draw.");
            MaxDepth = Config.Bind("General", "MaxDepth", 10,
                new ConfigDescription(
                    "How deep wildcards and choices may nest (a wildcard line that contains another wildcard, " +
                    "and so on). Past this the remaining text is left as written, so a file that refers to " +
                    "itself cannot loop.",
                    new AcceptableValueRange<int>(1, 50)));
            LogExpansions = Config.Bind("General", "LogExpansions", false,
                "Write every expansion to BepInEx/LogOutput.log: which box it came from, what each wildcard " +
                "and choice became, and the text before and after (long text is cut at 600 characters). " +
                "Use it to see what was rolled. Verbose - the entity rules are expanded for every generation request.");

            AreaEntityRules = Config.Bind("Areas", "EntityGenerationRules", true,
                "Expand wildcard syntax in the Entity generation rules box. Rolled again for every request " +
                "that generates a character, enemy, item, object, location, region, faction or ability.");
            AreaSpawnInstructions = Config.Bind("Areas", "SpawnInstructions", true,
                "Expand wildcard syntax in the description you type when spawning a character, enemy, object, " +
                "item, piece of equipment, location or region (spawn menus, map, region editor, and the " +
                "starting item generators on the new game screen).");
            AreaImagePromptFormat = Config.Bind("Areas", "ImagePromptFormat", true,
                "Expand wildcard syntax in the scenario's Image prompt format box and in the per-image-type " +
                "prompt formats in Options (the ones containing ${prompt}). Rolled again for every picture.");
            AreaNarrativeRules = Config.Bind("Areas", "NarrativeRules", true,
                "Expand wildcard syntax in the Narrative rules box and in the instruction box of the Custom " +
                "narrative flavor. Rolled again every turn.");

            WildcardFolder = Config.Bind("Files", "WildcardFolder", "",
                "Folder to read wildcard .txt files from. Leave empty for " +
                "BepInEx/config/AIROG_Wildcards/wildcards/. Set a full path to read an existing " +
                "sd-dynamic-prompts wildcards folder in place instead of copying it.");

            SafeRun("seed wildcard files", WildcardFiles.Seed);
            if (!SafeRun("start wildcard engine", WildcardRuntime.Init))
            {
                Logger.LogError($"{PLUGIN_NAME} could not start; no text will be expanded.");
                return;
            }

            // One SafeRun per hook: a game type that a future build renames or removes fails only
            // its own block (the typeof is resolved when that block is first run), and the other
            // areas keep working.
            SafeRun("hook entity generation rules", PatchEntityRules);
            SafeRun("hook spawn instructions", PatchSpawnInstructions);
            SafeRun("hook image prompt format", PatchImagePromptFormat);
            SafeRun("hook narrative rules", PatchNarrativeRules);
            SafeRun("hook console commands", PatchConsole);

            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loaded. Wildcard folder: {WildcardRuntime.WildcardFolder}");
        }

        // BaseUnityPlugin.Logger is protected, so sibling types in this assembly log through here.
        internal static void LogInfo(string msg) => Instance?.Logger.LogInfo(msg);

        internal static void LogWarn(string msg) => Instance?.Logger.LogWarning(msg);

        private void PatchEntityRules()
        {
            SafePatch(typeof(MmHelper), "GetMaybeEntityGenRulesInst",
                postfix: new HarmonyMethod(typeof(EntityRulesPatch), nameof(EntityRulesPatch.EntityGenRules_Postfix)));
        }

        private void PatchSpawnInstructions()
        {
            SafePatch(typeof(AIAsker), "SpawnEntityFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
            SafePatch(typeof(AIAsker), "SpawnItemFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
            SafePatch(typeof(AIAsker), "SpawnTopLvlLocationFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
            SafePatch(typeof(AIAsker), "SpawnDetachedLocationFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
            SafePatch(typeof(AIAsker), "SpawnRegionWithLocationsFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
            SafePatch(typeof(AIAsker), "SpawnRegionWoLocationsFromPrompt",
                prefix: new HarmonyMethod(typeof(SpawnInstructionPatch), nameof(SpawnInstructionPatch.SpawnInstruction_Prefix)));
        }

        private void PatchImagePromptFormat()
        {
            SafePatch(typeof(SettingsPojo.EntImgSettings), "GetFormatted",
                prefix: new HarmonyMethod(typeof(ImageFormatPatch), nameof(ImageFormatPatch.ImageFormatScenario_Prefix)),
                argTypes: new[] { typeof(string), typeof(string) });
            SafePatch(typeof(SettingsPojo.EntImgSettings), "GetFormatted",
                postfix: new HarmonyMethod(typeof(ImageFormatPatch), nameof(ImageFormatPatch.ImageFormatOptions_Postfix)) { priority = Priority.First },
                argTypes: new[] { typeof(string) });
        }

        private void PatchNarrativeRules()
        {
            SafePatch(typeof(UnifiedPromptBuilder), "GetStoryOrUnifiedPreambleStrInJarrayForm",
                postfix: new HarmonyMethod(typeof(NarrativeRulesPatch), nameof(NarrativeRulesPatch.Preamble_Postfix)));
        }

        private void PatchConsole()
        {
            SafePatch(typeof(GameplayManager), "ProcessConsoleCommand",
                prefix: new HarmonyMethod(typeof(ConsoleCommands), nameof(ConsoleCommands.Prefix)));
        }
    }
}
