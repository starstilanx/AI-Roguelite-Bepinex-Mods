using BepInEx;
using HarmonyLib;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AIROG_Insight
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class InsightPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.insight";
        public const string PLUGIN_NAME = "Insight Mechanic";
        public const string PLUGIN_VERSION = "1.2.1";

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

            // Track NPC conversations — fires each time a turn is resolved.
            // (ProcessInteractionInfoNoTryStr still exists in the game but nothing calls it any
            // more: every turn, single- or multi-actor, goes ProcessTurn -> ProcessTurnNoTryStr.)
            var processMethod = AccessTools.Method(typeof(GameplayManager), "ProcessTurnNoTryStr");
            if (processMethod != null)
                harmony.Patch(processMethod, prefix: new HarmonyMethod(typeof(InsightPlugin), nameof(Prefix_ProcessTurn)));
            else
                Logger.LogWarning("[Insight] Could not find GameplayManager.ProcessTurnNoTryStr");

            // Intercept AI responses to extract <NPC_INSIGHT> / <PLACE_INSIGHT> blocks
            var generateMethod = AccessTools.Method(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle));
            if (generateMethod != null)
                harmony.Patch(generateMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_GenerateTxt)));
            else
                Logger.LogWarning("[Insight] Could not find AIAsker.GenerateTxtNoTryStrStyle");

            // Story turns at a place with a panorama are sent through this method instead
            var generateWithImgMethod = AccessTools.Method(typeof(AIAsker), nameof(AIAsker.GenerateTxtFromTxtAndImg));
            if (generateWithImgMethod != null)
                harmony.Patch(generateWithImgMethod, postfix: new HarmonyMethod(typeof(InsightPlugin), nameof(Postfix_GenerateTxt)));
            else
                Logger.LogWarning("[Insight] Could not find AIAsker.GenerateTxtFromTxtAndImg");

            Logger.LogInfo("InsightPlugin loaded.");
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        private static string CurrentSaveDir()
        {
            if (SS.I == null || string.IsNullOrEmpty(SS.I.saveSubDirAsArg)) return null;
            return Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
        }

        private static void SaveNow()
        {
            string dir = CurrentSaveDir();
            if (dir == null || dir != InsightData.OwnerDir) return;
            // Clients never write saves (the game's own WriteSaveFile returns early for them too)
            if (MpSession.IsClient) return;
            InsightData.Instance.Save(dir);
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
            InsightData.ResetInstance(CurrentSaveDir());
            Debug.Log("[Insight] Insight data reset for new game.");
        }

        // ── Conversation / Place Tracking ─────────────────────────────────────

        public static void Prefix_ProcessTurn(GameplayManager __instance, List<TurnAction> batch)
        {
            try
            {
                if (!ModSwitch.Enabled) return;

                var data = InsightData.Instance;
                bool playerTurn = batch != null && batch.Count > 0 && batch[0]?.info != null && !batch[0].info.IsNonPlayerTurn();

                // Who this turn is aimed at. The provider and the response extractor both go by this.
                var npc = playerTurn ? TargetedNpc(__instance, batch) : null;
                bool dirty = data.ActiveNpcUuid != npc?.uuid;
                data.ActiveNpcUuid = npc?.uuid;

                // Count turns directed at an NPC
                if (npc != null)
                {
                    if (!data.ConversationCounts.ContainsKey(npc.uuid))
                        data.ConversationCounts[npc.uuid] = 0;
                    data.ConversationCounts[npc.uuid]++;
                    dirty = true;
                    Debug.Log($"[Insight] Conversation #{data.ConversationCounts[npc.uuid]} with {npc.GetPrettyName()} (threshold: {InsightData.InsightThreshold})");
                }

                // Count interactions taken at the current place (drives location insights)
                var place = playerTurn ? __instance?.currentPlace : null;
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
                Debug.LogError($"[Insight] Prefix_ProcessTurn error: {ex.Message}");
            }
        }

        /// <summary>
        /// The NPC a turn's action is aimed at (talking through the convo dropdown, an NPC menu
        /// action, giving an item...). The game's npcActionsHandler.currentNpc cannot be used for
        /// this: nothing in the game assigns it any more, so it is always null.
        /// </summary>
        private static GameCharacter TargetedNpc(GameplayManager manager, List<TurnAction> batch)
        {
            GameCharacter player = manager?.playerCharacter?.GetPcGameCharacter();
            foreach (var action in batch)
            {
                if (action?.info?.interacteeInfo?.interacteeEntity is GameCharacter ch
                    && ch != player && !ch.IsPlayable() && !ch.IsEnemyType())
                    return ch;
            }
            return null;
        }

        private static GameCharacter ActiveNpc()
        {
            string uuid = InsightData.Instance.ActiveNpcUuid;
            return string.IsNullOrEmpty(uuid) ? null : Utils.FindEntityByUuid<GameCharacter>(uuid);
        }

        // ── AI Response Interception ──────────────────────────────────────────

        public static void Postfix_GenerateTxt(ref Task<string> __result, AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (__result == null) return;
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;

            // The data object is captured now, when the request starts: the reply can arrive after
            // the player has moved to another save.
            __result = ExtractInsightAsync(__result, InsightData.Instance);
        }

        private static async Task<string> ExtractInsightAsync(Task<string> original, InsightData dataAtRequest)
        {
            string text = await original;
            if (text == null) return null;
            try
            {
                // Switched off, or a reply for a save that is no longer loaded: the block is still
                // removed from the story text, but nothing is recorded or announced.
                bool enabled = ModSwitch.Enabled && ReferenceEquals(InsightData.Instance, dataAtRequest);
                bool dirty = false;
                text = ExtractBlock(text, "NPC_INSIGHT", insightText =>
                {
                    if (!enabled) return;
                    var npc = ActiveNpc();
                    if (npc == null || string.IsNullOrEmpty(insightText)) return;

                    InsightData.Instance.AddNpcInsight(npc.uuid, insightText);
                    dirty = true;
                    Debug.Log($"[Insight] ✦ Gained insight on {npc.GetPrettyName()}: {insightText}");
                    NotifyPlayer($"✦ Insight gained — {npc.GetPrettyName()}: {insightText}");
                    TryRecordChronicleBeat($"Gained insight into {npc.GetPrettyName()}: {insightText}");
                });

                text = ExtractBlock(text, "PLACE_INSIGHT", insightText =>
                {
                    if (!enabled) return;
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
            int closeLen = closeTag.Length;

            // "<\/TAG>" is the same closing tag with JSON's optional escaped slash
            string escapedClose = "<\\/" + tag + ">";
            int endEscaped = text.IndexOf(escapedClose, StringComparison.OrdinalIgnoreCase);
            if (endEscaped > start && (end <= start || endEscaped < end))
            {
                end = endEscaped;
                closeLen = escapedClose.Length;
            }

            if (end > start)
            {
                string content = text.Substring(start + openTag.Length, end - start - openTag.Length).Trim();
                // In UNIFIED mode newlines inside JSON strings arrive as literal \n
                content = content.Replace("\\n", " ").Replace("\\r", "").Trim();
                onFound(content);

                string after = text.Substring(end + closeLen).TrimStart('\n', '\r', ' ');
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
