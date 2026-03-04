using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;


namespace AIROG_LoopBeGone
{
    [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
    public static class AIAskerPatch
    {
        // We use Postfix on the Task returning method. 
        // In Harmony, patching an async method requires patching the method that returns the Task.
        [HarmonyPostfix]
        public static void Postfix(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            // We only care about story generation for now, as that's where loops are most jarring
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER && 
                chatGptPromptType != AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER)
            {
                return;
            }

            __result = ProcessResult(__result);
        }

        private static async Task<string> ProcessResult(Task<string> task)
        {
            string originalText = await task;
            if (string.IsNullOrEmpty(originalText)) return originalText;

            // Get history from the current story chain
            var manager = SS.I?.hackyManager;
            if (manager == null) return originalText;

            var storyChain = manager.playerCharacter?.pcGameEntity?.storyChain;
            if (storyChain == null) return originalText;

            // Get last few turns for comparison.
            // Note: StoryChain.GetLastNStoryTurnsAsStrsNoNewlines was removed from the game.
            // We replicate its behavior by reading storyTurns directly and calling getCombinedStrNoNewlines().
            List<string> history = storyChain.storyTurns
                .Skip(Math.Max(0, storyChain.storyTurns.Count - 10))
                .Select(t => t.getCombinedStrNoNewlines())
                .ToList();

            var detection = LoopDetector.DetectLoop(originalText, history);

            if (detection.IsLoop)
            {
                Debug.LogWarning($"[LoopBeGone] Potential loop detected! Severity: {detection.Severity:P0}. Reason: {detection.Reason}");
                Debug.LogWarning($"[LoopBeGone] Detected Text: {originalText}");

                // If severity is very high, we might want to flag it in the UI or eventually retry.
                // For now, we just log it and maybe add a small indicator to the text for debugging.
                if (LoopBeGonePlugin.DebugMode.Value)
                {
                    return originalText + " <LOOP_DETECTED>";
                }
            }

            return originalText;
        }
    }
}
