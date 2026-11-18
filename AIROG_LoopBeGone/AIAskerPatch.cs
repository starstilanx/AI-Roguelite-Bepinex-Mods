using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_LoopBeGone
{
    [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
    public static class AIAskerPatch
    {
        private static bool _antiLoopActive = false;
        private static int _antiLoopTurnsRemaining = 0;

        // Which game the loop was seen in (identity of its GameplayManager; every load builds a
        // new one). An int rather than the reference, so a finished game is not kept alive by it.
        private static int _antiLoopGameId = 0;

        private const string AntiLoopInstruction =
            "\n\n[NARRATIVE DIRECTIVE: The previous response was repetitive. " +
            "Generate a FRESH, UNIQUE response that moves the story forward in a new direction. " +
            "Do NOT reuse phrases, sentences, or ideas from recent responses.]";

        // Hidden blocks other mods have the GM append to its story (<DM_NOTES>...</DM_NOTES> and
        // the like) - the same shape the game's Utils.FindModTagBlocks looks for. They are not
        // story text, and whether they have been stripped yet depends on patch order.
        private static readonly Regex ModTagBlock = new Regex(@"<([A-Z][A-Z0-9_]{2,})>[\s\S]*?</\1>");

        private static int CurrentGameId()
        {
            object manager = SS.I?.hackyManager;
            return manager == null ? 0 : RuntimeHelpers.GetHashCode(manager);
        }

        [HarmonyPrefix]
        public static void Prefix(ref string prompt, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (!_antiLoopActive || _antiLoopTurnsRemaining <= 0) return;
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;

            // A loop seen in one save says nothing about the save loaded after it.
            if (CurrentGameId() != _antiLoopGameId)
            {
                _antiLoopActive = false;
                _antiLoopTurnsRemaining = 0;
                return;
            }

            prompt += AntiLoopInstruction;
            _antiLoopTurnsRemaining--;
            if (_antiLoopTurnsRemaining <= 0)
                _antiLoopActive = false;

            Debug.Log($"[LoopBeGone] Anti-loop instruction injected ({_antiLoopTurnsRemaining} turns remaining).");
        }

        [HarmonyPostfix]
        public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            // Only monitor actual story generation — not utility calls
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED)
                return;

            __result = ProcessResult(__result, chatGptPromptType);
        }

        private static async Task<string> ProcessResult(Task<string> task, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            string originalText = await task;
            try
            {
                return Inspect(originalText, chatGptPromptType);
            }
            catch (Exception ex)
            {
                // Detection is advisory. Left unhandled, an exception here would fault the very
                // task the game is awaiting, and the player's turn would fail with it.
                Debug.LogWarning($"[LoopBeGone] Loop check skipped for this response: {ex.Message}");
                return originalText;
            }
        }

        private static string Inspect(string originalText, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (string.IsNullOrEmpty(originalText)) return originalText;

            var manager = SS.I?.hackyManager;
            if (manager == null) return originalText;

            // scenarioState.storyChain is the live chain for both pipelines (the unified one adds
            // every story turn to it as well as to chatHistory). PcGameEntity.storyChain, which
            // this used to read, is a legacy field the game only fills in while migrating an old
            // save - on any other save it is null, and no response was ever checked.
            var storyChain = manager.scenarioState?.storyChain;
            if (storyChain?.storyTurns == null) return originalText;

            string storyText = StoryTextOf(originalText, chatGptPromptType);
            if (string.IsNullOrWhiteSpace(storyText)) return originalText;

            List<string> history = storyChain.storyTurns
                .Skip(Math.Max(0, storyChain.storyTurns.Count - 10))
                .Where(t => t != null && !(string.IsNullOrEmpty(t.tryStr) && string.IsNullOrEmpty(t.resultingStoryStr)))
                .Select(t => t.getCombinedStrNoNewlines())
                .ToList();

            var detection = LoopDetector.DetectLoop(storyText, history);

            if (detection.IsLoop && detection.Severity >= LoopBeGonePlugin.SeverityThreshold.Value)
            {
                Debug.LogWarning($"[LoopBeGone] Loop detected! Severity: {detection.Severity:P0}. Reason: {detection.Reason}");

                _antiLoopActive = true;
                _antiLoopTurnsRemaining = 2;
                _antiLoopGameId = CurrentGameId();

                if (LoopBeGonePlugin.DebugMode.Value)
                    return originalText + " <LOOP_DETECTED>";
            }

            return originalText;
        }

        /// <summary>
        /// The narrative part of a response, or null when it has none to check.
        /// </summary>
        private static string StoryTextOf(string response, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            string story = response;
            if (chatGptPromptType == AIAsker.ChatGptPromptType.UNIFIED)
            {
                // The unified pipeline answers with a JSON envelope: resolution, story, api, meta.
                // Compared whole against story history, the bookkeeping fields drown the story
                // out - a word-for-word repeat of the previous turn scored as no loop at all.
                try
                {
                    story = (string)JObject.Parse(Utils.GetAiJsonSubstr(response))["story"];
                }
                catch (Exception)
                {
                    return null; // malformed envelope: the game rejects or retries it by itself
                }
            }
            return story == null ? null : ModTagBlock.Replace(story, "");
        }
    }
}
