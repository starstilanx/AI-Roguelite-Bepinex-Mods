using System;
using System.Collections.Generic;
using AIROG_Core;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AIROG_ScenePace
{
    /// <summary>
    /// Stops the GM racing past the moment.
    ///
    /// Vanilla scopes a successful roll to the player's entire stated clause. Ask to "cook a meal
    /// she would like" and roll well, and the specialized prompt says outright "does the following
    /// action, resulting in a success: ${try_str}" — so the GM resolves the whole clause, narrating
    /// preparation, cooking, plating, serving, her tasting it and her liking it, in one turn. Every
    /// choice, reaction and further roll in between is skipped. The unified pipeline has the same
    /// hole from the other end: `story` is defined as narrating "the attempted action and its
    /// outcome", and "its outcome" is unbounded. Neither pipeline sets a scene boundary anywhere.
    ///
    /// This mod injects two rules — a roll settles the means and never the ends; the story stops at
    /// the player's next real decision point — at three places:
    ///
    ///   1. GetActionSuffix postfix. Vanilla substitutes this into ${PLAYER_ACTION_INST_SUFFIX},
    ///      which appears in all 23 specialized outcome prompts *inside* the bracketed instruction,
    ///      and appends it to `player_input` in BuildUnifiedUserPrompt. One hook, both pipelines,
    ///      sitting right beside the action being resolved. Composes with (does not replace) the
    ///      suffix the player may have set in Options.
    ///   2. + 3. GetStoryOrUnifiedPreambleStrInJarrayForm prefix, amending the two unified preamble
    ///      substrings before the builder reads them: json_substr_unified_story_desc_input.txt (the
    ///      `story` field description) and json_substr_unified_roll_guidance.txt (roll turns only).
    ///      Both are consumed only on the turns we care about, so no gating is needed here; and it
    ///      covers multiplayer, where vanilla skips the action suffix entirely.
    ///
    /// The older specialized preamble is deliberately left alone: its only always-present hook is
    /// global, and would impose scene discipline on travel and system narration where fast-forward
    /// is the desirable behavior. The action suffix already covers that pipeline where it matters.
    /// The per-save narrativeRules are also left alone — the player edits those in the Journal, and
    /// a mod writing there would both fight those edits and only take effect on new games.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class ScenePacePlugin : BaseModPlugin
    {
        public const string PLUGIN_GUID = "com.airog.scenepace";
        public const string PLUGIN_NAME = "Scene Pace";
        public const string PLUGIN_VERSION = "1.0.0";

        private const string STORY_DESC_KEY = "json_substr_unified_story_desc_input.txt";
        private const string ROLL_GUIDANCE_KEY = "json_substr_unified_roll_guidance.txt";

        public static ScenePacePlugin Instance { get; private set; }

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<Strength> RuleStrength;
        public static ConfigEntry<bool> UsePromptFiles;
        public static ConfigEntry<bool> InjectActionSuffix;
        public static ConfigEntry<bool> InjectIntoPreamble;
        public static ConfigEntry<bool> LogInjections;

        protected override void Awake()
        {
            base.Awake();
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false, every hook passes straight through to vanilla behavior.");
            RuleStrength = Config.Bind("General", "Strength", Strength.Firm,
                "How hard the scene-scope rules are worded.\n" +
                "Off = inject nothing.\n" +
                "Gentle = 'prefer to' / 'where it fits'; leaves the call to the model. Try this first if Firm " +
                "makes turns feel clipped.\n" +
                "Firm = direct rules with a named stop condition. Recommended.\n" +
                "Strict = adds explicit prohibitions and a 'when unsure, stop earlier' tiebreak. Use if the GM " +
                "still runs ahead on Firm.\n" +
                "Changing this rewrites any prompt file you have NOT edited by hand.");

            UsePromptFiles = Config.Bind("Prompts", "UsePromptFiles", true,
                "Read the injected text from the editable .txt files in BepInEx/config/AIROG_ScenePace/ " +
                "(recommended - multi-line, re-read on save, no restart). When false, the built-in text for the " +
                "Strength above is used and the files are ignored.");
            InjectActionSuffix = Config.Bind("Prompts", "InjectActionSuffix", true,
                "Append the scene-scope note to your action text itself. This is the only injection that reaches " +
                "the older non-unified prompt pipeline, and the strongest one in both. Turn off if you would " +
                "rather write your own note in Options > action instruction suffix.");
            InjectIntoPreamble = Config.Bind("Prompts", "InjectIntoPreamble", true,
                "Add the scene-scope and roll-scope rules to the GM's system prompt in unified mode. This is what " +
                "covers multiplayer, where the game does not apply an action suffix at all.");

            LogInjections = Config.Bind("Debug", "LogInjections", false,
                "Log each injection to the BepInEx console. Verbose - one or more lines per turn.");

            SafeRun("seed prompt files", () =>
            {
                PromptFiles.Seed(RuleKind.StoryScope, RuleStrength.Value);
                PromptFiles.Seed(RuleKind.RollScope, RuleStrength.Value);
                PromptFiles.Seed(RuleKind.ActionSuffix, RuleStrength.Value);
            });

            SafePatch(typeof(GameplayManager), "GetActionSuffix",
                postfix: new HarmonyMethod(typeof(ScenePacePlugin), nameof(GetActionSuffix_Postfix)));
            SafePatch(typeof(UnifiedPromptBuilder), "GetStoryOrUnifiedPreambleStrInJarrayForm",
                prefix: new HarmonyMethod(typeof(ScenePacePlugin), nameof(Preamble_Prefix)));

            Logger.LogInfo($"{PLUGIN_NAME} {PLUGIN_VERSION} loaded (Strength={RuleStrength.Value}).");
        }

        // BaseUnityPlugin.Logger is protected, so sibling types in this assembly log through here.
        internal static void LogInfo(string msg) => Instance?.Logger.LogInfo(msg);

        internal static void LogWarn(string msg) => Instance?.Logger.LogWarning(msg);

        private static bool Active =>
            Enabled != null && Enabled.Value &&
            RuleStrength != null && RuleStrength.Value != Strength.Off;

        private static string TextFor(RuleKind kind) =>
            (PromptFiles.Read(kind, RuleStrength.Value) ?? "").Trim();

        /// <summary>
        /// Appends the scene-scope note to the instruction suffix that vanilla splices in next to
        /// the player's action, in both pipelines. __result is whatever the player set in Options
        /// (usually empty, and null when the pref was never written), so we compose rather than
        /// replace. ${actor_name} is resolved here because vanilla already did its own substitution
        /// on the player's text before this postfix runs.
        /// </summary>
        public static void GetActionSuffix_Postfix(GameplayManager __instance, ref string __result)
        {
            if (!Active || InjectActionSuffix == null || !InjectActionSuffix.Value) return;
            try
            {
                string note = TextFor(RuleKind.ActionSuffix);
                if (note.Length == 0) return;

                // Substitute before the duplicate check, so the comparison is against the same
                // form we would be appending.
                if (note.Contains("${actor_name}"))
                {
                    note = note.Replace("${actor_name}", ActorName(__instance) ?? "the player");
                }

                // Guards the case where the player pasted this text into Options themselves.
                string existing = __result ?? "";
                if (existing.Contains(note)) return;

                bool needsSpace = existing.Length > 0 && !char.IsWhiteSpace(existing[existing.Length - 1]);
                __result = existing + (needsSpace ? " " : "") + note;

                if (LogInjections != null && LogInjections.Value) LogInfo($"Action suffix injected ({note.Length} chars).");
            }
            catch (Exception ex)
            {
                LogWarn($"GetActionSuffix postfix failed: {ex}");
            }
        }

        /// <summary>
        /// GetThePlayerStrOrActorNameForPrompts is an extension on MmCtxGetter rather than a member
        /// of GameplayManager, so it is called defensively here — a signature change in a future
        /// game build should degrade to "the player", not throw on every turn.
        /// </summary>
        private static string ActorName(GameplayManager manager)
        {
            try
            {
                return manager == null ? null : MmHelper.GetThePlayerStrOrActorNameForPrompts(manager);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Amends the two unified preamble substrings in SS.I.storyPreamblesV2Dict just before the
        /// builder reads them. Runs on every build rather than once at load, because a prompt reload
        /// (MainMenu.ApplyModsToGameInOrder) clears and repopulates the dict; Append tracks the
        /// vanilla text per key so repeat calls replace the injection instead of stacking copies.
        /// Both keys are only consumed on the turns this mod cares about — the story field
        /// description is skipped for generation_instruction turns, and the roll guidance is only
        /// pulled in when the resolution mode is NORMAL, i.e. an actual roll — so this needs no
        /// further gating of its own.
        /// </summary>
        public static void Preamble_Prefix(GameplayManager manager)
        {
            if (Enabled == null || RuleStrength == null) return;
            try
            {
                if (manager == null || !manager.IsUnifiedFlow()) return;

                Dictionary<string, string> dict = SS.I?.storyPreamblesV2Dict;
                if (dict == null) return;

                // Passing "" when switched off restores the vanilla text, so toggling Enabled,
                // Strength or InjectIntoPreamble takes effect on the next turn without a restart.
                bool on = Active && InjectIntoPreamble != null && InjectIntoPreamble.Value;
                Append(dict, STORY_DESC_KEY, on ? TextFor(RuleKind.StoryScope) : "");
                Append(dict, ROLL_GUIDANCE_KEY, on ? TextFor(RuleKind.RollScope) : "");
            }
            catch (Exception ex)
            {
                LogWarn($"Preamble prefix failed: {ex}");
            }
        }

        // The vanilla text of each amended key, so an edit to a prompt file replaces our previous
        // injection instead of stacking a second copy on top of it.
        private static readonly Dictionary<string, string> vanillaText = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> lastWritten = new Dictionary<string, string>();

        private static void Append(Dictionary<string, string> dict, string key, string addition)
        {
            if (!dict.TryGetValue(key, out string current) || string.IsNullOrEmpty(current))
            {
                LogWarn($"Preamble key '{key}' missing from storyPreamblesV2Dict - skipping that injection.");
                return;
            }

            // If the dict no longer holds what we last wrote, the game repopulated it
            // (MainMenu.ApplyModsToGameInOrder clears and reloads every prompt) — so whatever is
            // there now is the new vanilla baseline.
            if (!lastWritten.TryGetValue(key, out string ours) || ours != current)
            {
                vanillaText[key] = current;
            }

            string baseText = vanillaText[key];
            string composed = addition.Length == 0 ? baseText : baseText.TrimEnd() + "\n" + addition;
            if (composed == current) return;

            dict[key] = composed;
            lastWritten[key] = composed;
            if (LogInjections != null && LogInjections.Value) LogInfo($"Injected into '{key}' ({addition.Length} chars).");
        }
    }
}
