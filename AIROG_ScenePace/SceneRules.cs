namespace AIROG_ScenePace
{
    /// <summary>How hard the scene-scope rules are worded.</summary>
    public enum Strength
    {
        /// <summary>Injects nothing. Same as disabling the mod, but keeps the config/files around.</summary>
        Off = 0,

        /// <summary>"Prefer to" / "where it fits". Leaves the call to the model.</summary>
        Gentle = 1,

        /// <summary>Direct rules with a named stop condition. The default.</summary>
        Firm = 2,

        /// <summary>Firm plus explicit prohibitions and a "when unsure, stop earlier" tiebreak.</summary>
        Strict = 3
    }

    /// <summary>Which piece of prompt text is being asked for.</summary>
    public enum RuleKind
    {
        /// <summary>Appended to the unified `story` field description. Bounds where the scene ends.</summary>
        StoryScope,

        /// <summary>Appended to the unified roll guidance. Bounds what a successful roll actually covers.</summary>
        RollScope,

        /// <summary>Appended to the per-action instruction suffix. Reaches both prompt pipelines.</summary>
        ActionSuffix
    }

    /// <summary>
    /// The built-in prompt text, one string per (kind, strength) pair.
    ///
    /// The problem being solved: vanilla scopes a successful roll to the player's whole stated
    /// clause, so "cook a meal she would like" resolves as *she liked it*. The specialized prompts
    /// say it outright ("does the following action, resulting in a success: ${try_str}"), and the
    /// unified prompt defines `story` as narrating "the attempted action and its outcome", with
    /// "its outcome" left unbounded. Nothing anywhere sets a scene boundary, so the GM is free to
    /// run preparation -> execution -> another character's verdict as one turn, skipping every
    /// choice the player would have made along the way.
    ///
    /// So the rules below draw two lines: a roll settles the *means*, never the *ends*; and the
    /// story stops at the first point the player could act again — normally the instant another
    /// character would have to make up their mind.
    ///
    /// StoryScope and RollScope are injected into the system preamble and may use ${player_or_entity},
    /// which the game substitutes ("player" / "acting entity" / "acting party members") after our
    /// text is spliced in. ActionSuffix is injected next to the action itself and may use
    /// ${actor_name}, which this mod substitutes. Text is deliberately ASCII-only.
    /// </summary>
    internal static class SceneRules
    {
        public static string Get(RuleKind kind, Strength strength)
        {
            switch (kind)
            {
                case RuleKind.StoryScope: return StoryScope(strength);
                case RuleKind.RollScope: return RollScope(strength);
                case RuleKind.ActionSuffix: return ActionSuffix(strength);
                default: return "";
            }
        }

        /// <summary>Every built-in string, used to tell "player never edited this file" from "player customised it".</summary>
        public static string[] AllVariants(RuleKind kind) => new[]
        {
            Get(kind, Strength.Gentle),
            Get(kind, Strength.Firm),
            Get(kind, Strength.Strict)
        };

        // ---------------------------------------------------------------- story scope

        private const string STORY_SCOPE_FIRM =
            "SCENE SCOPE: \"its outcome\" means the immediate, observable result of the attempt itself, not the " +
            "fulfilment of whatever the ${player_or_entity} hoped to achieve by it. End the story at the first " +
            "point where the ${player_or_entity} could meaningfully react or choose again. That point is usually " +
            "one of:\n" +
            "- another character has to make up their mind (accept, refuse, believe, agree, approve, be won over, " +
            "hand something over): show their first visible reaction, then stop short of their verdict;\n" +
            "- the attempt lands and there is now something new to respond to;\n" +
            "- continuing would call for a further attempt or roll.\n" +
            "Do not carry the scene past that point, and do not compress elapsed time (\"hours later\", \"by the " +
            "time\", \"eventually\", \"finally\") unless the action explicitly asked for a time skip. Ending early " +
            "is correct; the next turn continues from there.";

        private static string StoryScope(Strength s)
        {
            switch (s)
            {
                case Strength.Gentle:
                    return "SCENE SCOPE: Prefer to end the story at the first point where the ${player_or_entity} " +
                           "could meaningfully react or choose again, which is often the moment another character " +
                           "is about to make up their mind about something. Where it fits, leave that decision, and " +
                           "anything that follows from it, for the next turn rather than resolving it here.";

                case Strength.Firm:
                    return STORY_SCOPE_FIRM;

                case Strength.Strict:
                    return STORY_SCOPE_FIRM + "\n" +
                           "This is a hard limit, not a stylistic preference. Never narrate another character's " +
                           "decision, judgement or change of mind in the same turn as the ${player_or_entity} " +
                           "action that prompted it. Never narrate the ${player_or_entity} carrying out follow-up " +
                           "steps the action did not name. When unsure where the scene ends, end it earlier.";

                default:
                    return "";
            }
        }

        // ----------------------------------------------------------------- roll scope

        private const string ROLL_SCOPE_FIRM =
            "WHAT THE ROLL COVERS: The roll resolves the attempt, not the ambition. When the action names a goal, " +
            "a purpose, or another character's reaction as the thing it is for (\"cook a meal she would like\", " +
            "\"convince the guard to let us pass\", \"pick the lock so we can slip in\"), the outcome applies only " +
            "to the doing: how well the cooking, the arguing, the lockpicking went. Whether the goal is actually " +
            "met, and how any other character decides, is not settled by this roll and must not be written as " +
            "settled. critical_success means the attempt could not have been performed better; it does not mean " +
            "the ${player_or_entity} got what they wanted.";

        private static string RollScope(Strength s)
        {
            switch (s)
            {
                case Strength.Gentle:
                    return "WHAT THE ROLL COVERS: The roll is best read as resolving the attempt rather than the " +
                           "ambition behind it. When an action names a goal or another character's reaction as its " +
                           "purpose, prefer to let the outcome describe how well the doing itself went, and leave " +
                           "whether the goal was actually met to play out over the turns that follow.";

                case Strength.Firm:
                    return ROLL_SCOPE_FIRM;

                case Strength.Strict:
                    return ROLL_SCOPE_FIRM + "\n" +
                           "Treat any goal clause strictly as a statement of intent. A success entitles the " +
                           "${player_or_entity} to a well-executed attempt and a favourable position going into " +
                           "whatever comes next, and to nothing more. If the goal depends on another character, " +
                           "that character's response is a separate matter, resolved on a later turn on its own terms.";

                default:
                    return "";
            }
        }

        // -------------------------------------------------------------- action suffix
        // No double quotes here: this text is spliced next to the raw action string in both
        // pipelines, and the specialized one puts it inside a bracketed instruction.

        private static string ActionSuffix(Strength s)
        {
            switch (s)
            {
                case Strength.Gentle:
                    return "(Scene scope: where it fits, cover only this attempt and its immediate result, and " +
                           "prefer to stop at the first point the player could react or choose again.)";

                case Strength.Firm:
                    return "(Scene scope: narrate only this attempt and its immediate result. If the action names " +
                           "a goal or another character's reaction as its purpose, resolve only how well the " +
                           "attempt itself went, not whether the goal is met or how anyone else decides. Stop at " +
                           "the first point where the player could meaningfully react or choose again, usually the " +
                           "moment another character would have to make up their mind. Do not skip ahead or " +
                           "compress time past that point.)";

                case Strength.Strict:
                    return "(Scene scope, hard limit: narrate only this attempt and its immediate result, then " +
                           "stop. Resolve only how well the attempt itself went, never whether the stated goal is " +
                           "met. Never decide another character's verdict, judgement or change of mind in this " +
                           "turn; show their first visible reaction and stop there. Never add follow-up steps the " +
                           "action did not name. When unsure where to end, end earlier.)";

                default:
                    return "";
            }
        }
    }
}
