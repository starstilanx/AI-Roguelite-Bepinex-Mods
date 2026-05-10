using BepInEx;
using HarmonyLib;
using UnityEngine;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_Insight
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class InsightPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.insight";
        public const string PLUGIN_NAME = "Insight Mechanic";
        public const string PLUGIN_VERSION = "1.1.0";

        private void Awake()
        {
            var harmony = new Harmony(PLUGIN_GUID);

            // Save insights when the game saves
            var writeSaveMethod = AccessTools.Method(typeof(SaveIO), "WriteSaveFile");
            if (writeSaveMethod != null)
                harmony.Patch(writeSaveMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_WriteSaveFile)));
            else
                Logger.LogWarning("[Insight] Could not find SaveIO.WriteSaveFile");

            // Load insights synchronously after save data is read
            var readSaveMethod = AccessTools.Method(typeof(SaveIO), "ReadSaveFile");
            if (readSaveMethod != null)
                harmony.Patch(readSaveMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_ReadSaveFile)));
            else
                Logger.LogWarning("[Insight] Could not find SaveIO.ReadSaveFile");

            // Reset on new game
            var newGameMethod = AccessTools.Method(typeof(GameplayManager), "doNewGame");
            if (newGameMethod != null)
                harmony.Patch(newGameMethod, prefix: new HarmonyMethod(typeof(InsightPlugin), nameof(Prefix_DoNewGame)));
            else
                Logger.LogWarning("[Insight] Could not find GameplayManager.doNewGame");

            // Track NPC conversations — fires each time the player takes a turn
            var processMethod = AccessTools.Method(typeof(GameplayManager), "ProcessInteractionInfoNoTryStr");
            if (processMethod != null)
                harmony.Patch(processMethod, prefix: new HarmonyMethod(typeof(InsightPlugin), nameof(Prefix_ProcessInteraction)));
            else
                Logger.LogWarning("[Insight] Could not find GameplayManager.ProcessInteractionInfoNoTryStr");

            // Intercept AI responses to extract <NPC_INSIGHT> blocks
            var generateMethod = AccessTools.Method(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle));
            if (generateMethod != null)
                harmony.Patch(generateMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_GenerateTxt)));
            else
                Logger.LogWarning("[Insight] Could not find AIAsker.GenerateTxtNoTryStrStyle");

            Logger.LogInfo("InsightPlugin loaded.");
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                InsightData.Instance.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        public static void Postfix_ReadSaveFile(string saveSubDir)
        {
            if (!string.IsNullOrEmpty(saveSubDir) && SS.I != null)
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, saveSubDir);
                InsightData.Instance.Load(saveDir);
                Debug.Log($"[Insight] Loaded insight data from {saveDir}");
            }
        }

        public static void Prefix_DoNewGame()
        {
            InsightData.ResetInstance();
            Debug.Log("[Insight] Insight data reset for new game.");
        }

        // ── Conversation Tracking ─────────────────────────────────────────────

        public static void Prefix_ProcessInteraction(GameplayManager __instance, InteractionInfo interactionInfo)
        {
            try
            {
                // Only count turns directed at an NPC
                var npc = __instance?.npcActionsHandler?.currentNpc;
                if (npc == null) return;

                var data = InsightData.Instance;
                if (!data.ConversationCounts.ContainsKey(npc.uuid))
                    data.ConversationCounts[npc.uuid] = 0;

                data.ConversationCounts[npc.uuid]++;
                int count = data.ConversationCounts[npc.uuid];
                Debug.Log($"[Insight] Conversation #{count} with {npc.GetPrettyName()} (threshold: {InsightData.InsightThreshold})");

                // Persist the updated count so InsightProvider can read it via the JSON cache
                if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                    data.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Insight] Prefix_ProcessInteraction error: {ex.Message}");
            }
        }

        // ── AI Response Interception ──────────────────────────────────────────

        public static void Postfix_GenerateTxt(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;

            __result = ExtractInsightAsync(__result);
        }

        private static async Task<string> ExtractInsightAsync(Task<string> original)
        {
            string text = await original;
            try
            {
                const string openTag = "<NPC_INSIGHT>";
                const string closeTag = "</NPC_INSIGHT>";
                int start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
                int end   = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);

                if (start >= 0 && end > start)
                {
                    string insightText = text.Substring(start + openTag.Length, end - start - openTag.Length).Trim();

                    // Strip block from response so the player doesn't see it
                    string after = text.Substring(end + closeTag.Length).TrimStart('\n', '\r', ' ');
                    text = text.Substring(0, start) + after;

                    // Attribute the insight to the NPC currently being conversed with
                    var manager = UnityEngine.Object.FindObjectOfType<GameplayManager>();
                    var npc = manager?.npcActionsHandler?.currentNpc;
                    if (npc != null && !string.IsNullOrEmpty(insightText))
                    {
                        InsightData.Instance.NpcInsights[npc.uuid] = insightText;
                        Debug.Log($"[Insight] ✦ Gained insight on {npc.GetPrettyName()}: {insightText}");

                        if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                            InsightData.Instance.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
                    }
                }
                else if (start >= 0)
                {
                    // Bare/unclosed tag — strip it to avoid leaking into the narrative
                    int lineEnd = text.IndexOf('\n', start);
                    text = text.Substring(0, start) + (lineEnd > start ? text.Substring(lineEnd + 1) : "");
                    text = text.TrimStart('\n', '\r', ' ');
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Insight] ExtractInsightAsync error: {ex.Message}");
            }
            return text;
        }
    }
}
