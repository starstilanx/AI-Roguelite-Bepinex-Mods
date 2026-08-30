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
        public const string PLUGIN_VERSION = "1.2.0";

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

            // Intercept AI responses to extract <NPC_INSIGHT> / <PLACE_INSIGHT> blocks
            var generateMethod = AccessTools.Method(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle));
            if (generateMethod != null)
                harmony.Patch(generateMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_GenerateTxt)));
            else
                Logger.LogWarning("[Insight] Could not find AIAsker.GenerateTxtNoTryStrStyle");

            Logger.LogInfo("InsightPlugin loaded.");
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        private static void SaveNow()
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                InsightData.Instance.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
        }

        // WriteSaveFile's first arg is MmCtxGetter (both GameplayManager and MainMenu implement it)
        // as of the 07/28 build; take no injected params so Harmony emits no castclass.
        public static void Postfix_WriteSaveFile()
        {
            SaveNow();
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

        // ── Conversation / Place Tracking ─────────────────────────────────────

        public static void Prefix_ProcessInteraction(GameplayManager __instance, InteractionInfo interactionInfo)
        {
            try
            {
                var data = InsightData.Instance;
                bool dirty = false;

                // Count turns directed at an NPC
                var npc = __instance?.npcActionsHandler?.currentNpc;
                if (npc != null)
                {
                    if (!data.ConversationCounts.ContainsKey(npc.uuid))
                        data.ConversationCounts[npc.uuid] = 0;
                    data.ConversationCounts[npc.uuid]++;
                    dirty = true;
                    Debug.Log($"[Insight] Conversation #{data.ConversationCounts[npc.uuid]} with {npc.GetPrettyName()} (threshold: {InsightData.InsightThreshold})");
                }

                // Count interactions taken at the current place (drives location insights)
                var place = __instance?.currentPlace;
                if (place != null)
                {
                    if (!data.PlaceInteractionCounts.ContainsKey(place.uuid))
                        data.PlaceInteractionCounts[place.uuid] = 0;
                    data.PlaceInteractionCounts[place.uuid]++;
                    dirty = true;
                }

                // Persist so InsightProvider (GenContext) sees fresh counts via the JSON cache
                if (dirty)
                    SaveNow();
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
                bool dirty = false;
                text = ExtractBlock(text, "NPC_INSIGHT", insightText =>
                {
                    var npc = SS.I?.hackyManager?.npcActionsHandler?.currentNpc;
                    if (npc == null || string.IsNullOrEmpty(insightText)) return;

                    InsightData.Instance.AddNpcInsight(npc.uuid, insightText);
                    dirty = true;
                    Debug.Log($"[Insight] ✦ Gained insight on {npc.GetPrettyName()}: {insightText}");
                    NotifyPlayer($"✦ Insight gained — {npc.GetPrettyName()}: {insightText}");
                    TryRecordChronicleBeat($"Gained insight into {npc.GetPrettyName()}: {insightText}");
                });

                text = ExtractBlock(text, "PLACE_INSIGHT", insightText =>
                {
                    var place = SS.I?.hackyManager?.currentPlace;
                    if (place == null || string.IsNullOrEmpty(insightText)) return;
                    if (InsightData.Instance.PlaceInsights.ContainsKey(place.uuid)) return;

                    InsightData.Instance.PlaceInsights[place.uuid] = insightText;
                    dirty = true;
                    Debug.Log($"[Insight] ✦ Gained insight on location {place.GetPrettyName()}: {insightText}");
                    NotifyPlayer($"✦ Location insight — {place.GetPrettyName()}: {insightText}");
                });

                if (dirty)
                    SaveNow();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Insight] ExtractInsightAsync error: {ex.Message}");
            }
            return text;
        }

        /// <summary>
        /// Finds a &lt;TAG&gt;...&lt;/TAG&gt; block, passes its trimmed content to <paramref name="onFound"/>,
        /// and returns the text with the block stripped. Bare/unclosed tags are stripped to end of line.
        /// </summary>
        private static string ExtractBlock(string text, string tag, Action<string> onFound)
        {
            string openTag = "<" + tag + ">";
            string closeTag = "</" + tag + ">";
            int start = text.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return text;
            int end = text.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase);

            if (end > start)
            {
                string content = text.Substring(start + openTag.Length, end - start - openTag.Length).Trim();
                // In UNIFIED mode newlines inside JSON strings arrive as literal \n
                content = content.Replace("\\n", " ").Replace("\\r", "").Trim();
                onFound(content);

                string after = text.Substring(end + closeTag.Length).TrimStart('\n', '\r', ' ');
                return text.Substring(0, start) + after;
            }

            // Bare/unclosed tag — strip it to avoid leaking into the narrative
            int lineEnd = text.IndexOf('\n', start);
            text = text.Substring(0, start) + (lineEnd > start ? text.Substring(lineEnd + 1) : "");
            return text.TrimStart('\n', '\r', ' ');
        }

        // ── Player feedback ───────────────────────────────────────────────────

        private static void NotifyPlayer(string message)
        {
            try
            {
                var logView = SS.I?.hackyManager?.gameLogView;
                if (logView != null)
                    _ = logView.LogText("<color=#B08FFF>" + message + "</color>");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Insight] NotifyPlayer failed: {ex.Message}");
            }
        }

        // ── Chronicle integration (soft dependency via reflection) ────────────

        private static void TryRecordChronicleBeat(string summary)
        {
            try
            {
                var mgrType = Type.GetType("AIROG_Chronicle.ChronicleManager, AIROG_Chronicle");
                var beatType = Type.GetType("AIROG_Chronicle.ChronicleBeat, AIROG_Chronicle");
                if (mgrType == null || beatType == null) return;

                int turn = 0;
                var state = mgrType.GetProperty("State")?.GetValue(null);
                if (state != null)
                    turn = (int)(state.GetType().GetProperty("GlobalTurn")?.GetValue(state) ?? 0);

                var beat = Activator.CreateInstance(beatType);
                beatType.GetProperty("Turn")?.SetValue(beat, turn);
                beatType.GetProperty("Summary")?.SetValue(beat, summary);
                beatType.GetProperty("IsMilestone")?.SetValue(beat, true);
                mgrType.GetMethod("RecordBeat")?.Invoke(null, new[] { beat });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Insight] Chronicle beat integration failed: {ex.Message}");
            }
        }
    }
}
