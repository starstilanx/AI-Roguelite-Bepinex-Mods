using System;
using System.Runtime.CompilerServices;
using AIROG_Wildcards.Engine;

namespace AIROG_Wildcards
{
    /// <summary>
    /// The Harmony patch bodies. Every one of them expands a single piece of player-authored text
    /// on its way into a prompt, on a copy: nothing here writes to a settings field, and the
    /// template engine is never run over an AI response, story history or the game's own prompt
    /// text - only over the player's fragment, which is then put back in place of itself.
    ///
    /// Each patch method is a thin shell. Anything that touches a game member lives in a separate
    /// non-inlined helper called inside the shell's try block, so a member renamed by a future
    /// game build fails inside that try (JIT errors surface at the call) instead of throwing out
    /// of the game method being patched. One class per hook, so a game type that disappears from
    /// one signature cannot stop the other hooks from being found.
    /// </summary>
    internal static class EntityRulesPatch
    {
        /// <summary>
        /// Postfix on MmHelper.GetMaybeEntityGenRulesInst, the one place the rules text is turned
        /// into the "Entity generation guidelines" block that every entity prompt splices in via
        /// ${maybe_entity_generation_rules}. It is called once per prompt build, so each request
        /// gets its own roll.
        /// </summary>
        public static void EntityGenRules_Postfix(MmCtxGetter mm, ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || !WildcardRuntime.AreaOn(WildcardsPlugin.AreaEntityRules)) return;
            try
            {
                __result = ExpandEntityRules(mm, __result);
            }
            catch (Exception ex)
            {
                WildcardRuntime.WarnOnce("patch:entity-rules", "Entity generation rules hook failed; rules sent unexpanded. " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ExpandEntityRules(MmCtxGetter mm, string block)
        {
            string rules = mm?.GetEntityGenerationRules();
            if (string.IsNullOrEmpty(rules) || !WildcardEngine.MayContainSyntax(rules)) return block;

            // Only the span that is the player's rules text is replaced; the game's header and
            // trailing newlines around it are kept as they are.
            int at = block.IndexOf(rules, StringComparison.Ordinal);
            if (at < 0) return block;
            string expanded = WildcardRuntime.Expand(rules, "entity generation rules");
            if (ReferenceEquals(expanded, rules)) return block;
            return block.Substring(0, at) + expanded + block.Substring(at + rules.Length);
        }
    }

    internal static class SpawnInstructionPatch
    {
        /// <summary>
        /// Prefix shared by the six AIAsker.Spawn...FromPrompt methods. Their playerInst argument
        /// is only ever the text the player typed into a spawn box (EntitySpawner, MapModal,
        /// RegionEditor, NgEquipSlot, NgStartingItems, or a co-op client's forwarded MpSpawnArgs);
        /// no AI output is routed through it. Rewriting the argument changes this call only.
        /// The originals are async, but this prefix never skips them, so the Task is untouched.
        /// </summary>
        public static void SpawnInstruction_Prefix(ref string playerInst)
        {
            if (string.IsNullOrEmpty(playerInst) || !WildcardRuntime.AreaOn(WildcardsPlugin.AreaSpawnInstructions)) return;
            try
            {
                playerInst = WildcardRuntime.Expand(playerInst, "spawn instruction");
            }
            catch (Exception ex)
            {
                WildcardRuntime.WarnOnce("patch:spawn", "Spawn instruction hook failed; text sent unexpanded. " + ex);
            }
        }
    }

    internal static class ImageFormatPatch
    {
        /// <summary>
        /// Prefix on EntImgSettings.GetFormatted(pr, worldImagePromptFmt): expands the scenario's
        /// image prompt format. The argument is a copy of the reference held by ScenarioState, so
        /// the stored format is not changed. pr (the entity's description) is left alone.
        /// </summary>
        public static void ImageFormatScenario_Prefix(ref string worldImagePromptFmt)
        {
            if (string.IsNullOrEmpty(worldImagePromptFmt) || !WildcardRuntime.AreaOn(WildcardsPlugin.AreaImagePromptFormat)) return;
            try
            {
                worldImagePromptFmt = WildcardRuntime.Expand(worldImagePromptFmt, "image prompt format (scenario)");
            }
            catch (Exception ex)
            {
                WildcardRuntime.WarnOnce("patch:image-scenario", "Scenario image prompt format hook failed. " + ex);
            }
        }

        /// <summary>
        /// Postfix on EntImgSettings.GetFormatted(pr): expands the per-image-type format from
        /// Options. That format is a field of the shared settings object, so it is never written
        /// to; the result is rebuilt from an expanded copy of the string instead. Registered with
        /// Priority.First (in WildcardsPlugin, not by attribute: the patch audit reads a Harmony
        /// attribute as an annotated patch class nobody registers), so __result is still exactly
        /// what the game returned and any other mod's postfix runs afterwards, once, on our result.
        /// </summary>
        public static void ImageFormatOptions_Postfix(SettingsPojo.EntImgSettings __instance, string pr, ref string __result)
        {
            if (__instance == null || !WildcardRuntime.AreaOn(WildcardsPlugin.AreaImagePromptFormat)) return;
            try
            {
                ExpandOptionsImageFormat(__instance, pr, ref __result);
            }
            catch (Exception ex)
            {
                WildcardRuntime.WarnOnce("patch:image-options", "Image prompt format hook failed. " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ExpandOptionsImageFormat(SettingsPojo.EntImgSettings settings, string pr, ref string result)
        {
            string fmt = settings.fmt;
            if (string.IsNullOrEmpty(fmt) || !WildcardEngine.MayContainSyntax(fmt)) return;
            string expanded = WildcardRuntime.Expand(fmt, "image prompt format (options)");
            if (ReferenceEquals(expanded, fmt)) return;

            // Calling GetFormatted again on a copy of the settings would run every other mod's
            // patches on it a second time, so the format rule is mirrored here instead - and only
            // trusted when it reproduces what the game itself just returned for the unexpanded
            // format. If a game update changes the rule, this stops expanding rather than
            // quietly formatting image prompts the old way.
            if (!string.Equals(Format(fmt, pr), result, StringComparison.Ordinal))
            {
                WildcardRuntime.WarnOnce("image-options-mismatch", "The game no longer builds image prompts the way this mod expects "
                    + "(EntImgSettings.GetFormatted changed, or another mod replaced its result); wildcards in the Options image prompt formats are not being expanded.");
                return;
            }
            result = Format(expanded, pr);
        }

        /// <summary>The rule of EntImgSettings.GetFormatted(string): empty = no format, no ${prompt} = prefix.</summary>
        private static string Format(string fmt, string pr)
        {
            if (string.IsNullOrEmpty(fmt)) return pr;
            if (!fmt.Contains("${prompt}")) return Utils.ConcatWithMaybeSpace(fmt, pr);
            return fmt.Replace("${prompt}", pr);
        }
    }

    internal static class NarrativeRulesPatch
    {
        /// <summary>
        /// Postfix on UnifiedPromptBuilder.GetStoryOrUnifiedPreambleStrInJarrayForm. The method
        /// reads playerRules.narrativeRules inline and returns the whole system preamble, so this
        /// is the one hook that sees more than the player's own text. It does not parse or expand
        /// the preamble: it looks for the exact JSON-escaped copy of the player's rules that the
        /// game just inserted and swaps that span for the expanded rules. See SwapEscapedFragment.
        /// </summary>
        public static void Preamble_Postfix(GameplayManager manager, ref string __result)
        {
            if (manager == null || string.IsNullOrEmpty(__result) || !WildcardRuntime.AreaOn(WildcardsPlugin.AreaNarrativeRules)) return;
            try
            {
                __result = ExpandNarrative(manager, __result);
            }
            catch (Exception ex)
            {
                WildcardRuntime.WarnOnce("patch:narrative", "Narrative rules hook failed; rules sent unexpanded. " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ExpandNarrative(GameplayManager manager, string preamble)
        {
            ScenarioState state = manager.scenarioState;
            if (state == null) return preamble;

            // Same field choice and same ${perspective_inst} substitution as the game makes
            // before it inserts the rules.
            bool unified = manager.IsUnifiedFlow();
            string rules = unified ? state.playerRules?.narrativeRules : state.playerRules?.narrativeRulesSpecialized;
            if (!string.IsNullOrEmpty(rules) && WildcardEngine.MayContainSyntax(rules))
            {
                string perspective = AIAsker.GetNarrativePerspectiveInst(manager.IsMpGame());
                preamble = SwapEscapedFragment(preamble, rules, "narrative rules",
                    text => text.Replace("${perspective_inst}", perspective), unified);
            }

            string custom = manager.GetScenSettings()?.narrativeFlavorCustomStr;
            if (!string.IsNullOrEmpty(custom) && WildcardEngine.MayContainSyntax(custom))
            {
                preamble = SwapEscapedFragment(preamble, custom, "custom narrative style", null, false);
            }
            return preamble;
        }

        /// <summary>
        /// Replaces the one span of <paramref name="preamble"/> that is byte-for-byte the
        /// JSON-escaped form of <paramref name="fragment"/> with the JSON-escaped expansion of it.
        /// Leaves the preamble alone when that span is absent (this preamble does not use the
        /// fragment, or the game truncated it) or occurs more than once (could not tell which one
        /// the game inserted). Every character outside the span is carried over untouched.
        /// </summary>
        private static string SwapEscapedFragment(string preamble, string fragment, string area, Func<string, string> asInserted, bool checkSystemPromptLimit)
        {
            string before = Utils.JsonEscape(asInserted != null ? asInserted(fragment) : fragment);
            if (string.IsNullOrEmpty(before)) return preamble;
            int at = preamble.IndexOf(before, StringComparison.Ordinal);
            if (at < 0) return preamble;
            if (preamble.IndexOf(before, at + 1, StringComparison.Ordinal) >= 0) return preamble;

            string expanded = WildcardRuntime.Expand(fragment, area);
            if (ReferenceEquals(expanded, fragment)) return preamble;
            string after = asInserted != null ? asInserted(expanded) : expanded;

            // The game truncates over-long rules for its own servers before inserting them. Rules
            // that fitted must not be pushed past that limit by what the wildcards rolled.
            if (checkSystemPromptLimit && Utils.IsOfficialServers(SS.I.GetActualTextGen(SS.HackyTxtGenType.OTHER)))
            {
                int limit = FirebaseClient.I.GetSystemPromptCharLimit(SS.HackyTxtGenType.OTHER, AIAsker.ChatGptPromptType.STORY_COMPLETER);
                if (after.Length > limit)
                {
                    WildcardRuntime.WarnOnce("limit:" + area, "The expanded " + area + " would exceed the server's " + limit
                        + " character limit for rules, so this request used them unexpanded. Shorten the rules or the wildcard lines.");
                    return preamble;
                }
            }
            return preamble.Substring(0, at) + Utils.JsonEscape(after) + preamble.Substring(at + before.Length);
        }
    }
}
