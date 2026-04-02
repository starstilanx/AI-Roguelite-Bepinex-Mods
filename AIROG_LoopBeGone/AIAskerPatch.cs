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
        private static bool _antiLoopActive = false;
        private static int _antiLoopTurnsRemaining = 0;

        private const string AntiLoopInstruction =
            "\n\n[NARRATIVE DIRECTIVE: The previous response was repetitive. " +
            "Generate a FRESH, UNIQUE response that moves the story forward in a new direction. " +
            "Do NOT reuse phrases, sentences, or ideas from recent responses.]";

        [HarmonyPrefix]
        public static void Prefix(ref string prompt, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (!_antiLoopActive || _antiLoopTurnsRemaining <= 0) return;
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;

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

            __result = ProcessResult(__result);
        }

        private static async Task<string> ProcessResult(Task<string> task)
        {
            string originalText = await task;
            if (string.IsNullOrEmpty(originalText)) return originalText;

            var manager = SS.I?.hackyManager;
            if (manager == null) return originalText;

            var storyChain = manager.playerCharacter?.pcGameEntity?.storyChain;
            if (storyChain == null) return originalText;

            List<string> history = storyChain.storyTurns
                .Skip(Math.Max(0, storyChain.storyTurns.Count - 10))
                .Select(t => t.getCombinedStrNoNewlines())
                .ToList();

            var detection = LoopDetector.DetectLoop(originalText, history);

            if (detection.IsLoop && detection.Severity >= LoopBeGonePlugin.SeverityThreshold.Value)
            {
                Debug.LogWarning($"[LoopBeGone] Loop detected! Severity: {detection.Severity:P0}. Reason: {detection.Reason}");

                _antiLoopActive = true;
                _antiLoopTurnsRemaining = 2;

                if (LoopBeGonePlugin.DebugMode.Value)
                    return originalText + " <LOOP_DETECTED>";
            }

            return originalText;
        }
    }
}
