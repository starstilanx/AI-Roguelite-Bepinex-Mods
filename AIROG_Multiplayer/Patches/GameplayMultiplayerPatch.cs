using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using AIROG_Multiplayer.Inventory;
using AIROG_Multiplayer.Network;
using UnityEngine;

namespace AIROG_Multiplayer.Patches
{
    /// <summary>
    /// Hooks into GameplayManager and related classes to:
    /// 1. Inject remote player characters into BuildPromptString (co-op AI context)
    /// 2. Gate the host's turn until all clients submit actions (v2.0)
    /// 3. Broadcast post-turn save data with world state (v2.0)
    /// 4. Relay AI-generated story images to all clients (v2.0)
    /// 5. Detect and broadcast location changes
    /// 6. Intercept client action submission and redirect to host
    /// 7. Block client-side save file writes
    ///
    /// NOTE: Patches that have BOTH a Prefix AND a Postfix targeting the same method
    /// MUST live in their own class decorated with a class-level [HarmonyPatch].
    /// Putting two method-level [HarmonyPatch] annotations for the same target in a
    /// non-patch class causes HarmonyX PatchAll() to skip one or both silently.
    /// Solo patches (one patch per target) can stay here safely.
    /// </summary>
    public static class GameplayMultiplayerPatch
    {
        // Pending client actions to inject into the next prompt (key: playerId, value: "CharName: action")
        private static readonly Dictionary<string, string> _pendingClientActions
            = new Dictionary<string, string>();
        private static readonly object _actionLock = new object();

        public static void AddPendingAction(string playerId, string characterName, string actionText)
        {
            lock (_actionLock)
            {
                _pendingClientActions[playerId] = $"{characterName}: {actionText}";
            }
        }

        public static void ClearPendingActions()
        {
            lock (_actionLock) { _pendingClientActions.Clear(); }
        }

        public static bool HasPendingActions()
        {
            lock (_actionLock) { return _pendingClientActions.Count > 0; }
        }

        /// <summary>
        /// Atomically snapshots and clears pending actions.
        /// Used by ConvoSubmissionPatch.Prefix to inject actions into the text field
        /// before ProcessConvoMessage() reads it synchronously.
        /// </summary>
        public static List<string> GetAndClearPendingActions()
        {
            lock (_actionLock)
            {
                var list = _pendingClientActions.Values.ToList();
                _pendingClientActions.Clear();
                return list;
            }
        }

        // -----------------------------------------------------------------------
        // Patch 1: Inject co-op partner context into BuildPromptString
        // Solo postfix — no conflict.
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "BuildPromptString",
            new Type[] {
                typeof(bool), typeof(bool), typeof(InteractionInfo),
                typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                typeof(List<Faction>), typeof(List<string>), typeof(bool)
            })]
        [HarmonyPostfix]
        public static void Postfix_BuildPromptString(GameplayManager __instance, ref string __result)
        {
            // Unconditional diagnostic to confirm this patch fires
            UnityEngine.Debug.Log($"[MP-DIAG] Postfix_BuildPromptString: IsHost={MultiplayerPlugin.IsHost} IsMultiplayer={MultiplayerPlugin.IsMultiplayer}");

            if (!MultiplayerPlugin.IsMultiplayer) return;

            try
            {
                if (!MultiplayerPlugin.IsHost || MultiplayerPlugin.Server == null) return;

                var clients = MultiplayerPlugin.Server.GetClients();
                if (clients.Count == 0) return;

                var sb = new StringBuilder();
                sb.AppendLine("\n\n[CO-OP PARTY MEMBERS]");
                sb.AppendLine("This is a co-op session. The following characters are present and adventuring alongside the player:");

                foreach (var client in clients)
                {
                    var info = client.CharacterInfo;
                    if (info == null) continue;

                    sb.AppendLine($"\n- {info.CharacterName}" +
                        $"{(string.IsNullOrEmpty(info.CharacterClass) ? "" : $", {info.CharacterClass}")}" +
                        $" (HP: {info.Health}/{info.MaxHealth})" +
                        $"{(string.IsNullOrEmpty(info.CurrentLocation) ? "" : $" — at: {info.CurrentLocation}")}");

                    if (!string.IsNullOrEmpty(info.CharacterBackground))
                        sb.AppendLine($"  Background: {info.CharacterBackground}");
                    if (!string.IsNullOrEmpty(info.Personality))
                        sb.AppendLine($"  Personality/Goals: {info.Personality}");
                    if (!string.IsNullOrEmpty(info.CharacterAppearance))
                        sb.AppendLine($"  Appearance: {info.CharacterAppearance}");
                }

                // NOTE: Client actions are injected via the text field in ConvoSubmissionPatch.Prefix,
                // not here — because BuildPromptString is not always called for open-ended actions
                // (depends on whether the prompt template contains ${FULL_PROMPT_STR}).

                __result += sb.ToString();
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] BuildPromptString patch error: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Patch 2b: Relay AI StoryTurn text to clients
        // Solo postfix — no conflict.
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameLogView), "LogText",
            new System.Type[] { typeof(StoryTurn), typeof(IllustratedStoryTurn) })]
        [HarmonyPostfix]
        public static void Postfix_LogText_StoryTurn(StoryTurn st)
        {
            // Unconditional diagnostic — confirms this fires (only in unified flow)
            UnityEngine.Debug.Log($"[MP-DIAG] Postfix_LogText_StoryTurn: IsHost={MultiplayerPlugin.IsHost} st={(st == null ? "null" : "set")}");

            if (!MultiplayerPlugin.IsHost) return;
            if (st == null) return;

            try
            {
                string text = st.getCombinedStr()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                MultiplayerPlugin.Instance?.Log.LogInfo($"[Host] Postfix_LogText_StoryTurn: broadcasting {text.Length} chars");
                MultiplayerPlugin.Server?.BroadcastStoryTurn(new StoryEntry
                {
                    Text = text,
                    AuthorName = "Narrator",
                    IsPlayerAction = false,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                // Schedule a save + broadcast after a short delay so AddLastIlluStoryTurn
                // (called inside the async LogText body) has time to run before we snapshot.
                if (MultiplayerPlugin.Instance != null)
                {
                    MultiplayerPlugin.Instance.StartCoroutine(
                        MultiplayerPlugin.SaveAndBroadcastAfterDelay(0.3f));
                    MultiplayerPlugin.Instance.Log.LogInfo("[Host] SaveAndBroadcastAfterDelay coroutine started.");
                }
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] LogText StoryTurn relay error: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Patch 4: Detect location changes and broadcast to clients
        // Solo postfix — no conflict.
        // -----------------------------------------------------------------------
        private static string _lastBroadcastLocation = null;

        [HarmonyPatch(typeof(GameplayManager), "UpdateLocationBar")]
        [HarmonyPostfix]
        public static void Postfix_UpdateLocationBar(GameplayManager __instance)
        {
            if (!MultiplayerPlugin.IsHost) return;

            try
            {
                string newLocation = __instance.currentPlace?.name;
                if (string.IsNullOrEmpty(newLocation)) return;
                if (newLocation == _lastBroadcastLocation) return;

                _lastBroadcastLocation = newLocation;
                string description = __instance.currentPlace?.description ?? "";
                MultiplayerPlugin.BroadcastLocation(newLocation, description);
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] UpdateLocationBar patch error: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Patch 5 (v2.0): Relay AI-generated story image to all clients
        // Solo postfix — no conflict.
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(MainImg), "UpdateMainImageWithXfade")]
        [HarmonyPostfix]
        public static void Postfix_UpdateMainImageWithXfade(MainImg __instance, IllustratedStoryTurn illustratedStoryTurn)
        {
            if (!MultiplayerPlugin.IsHost) return;
            if (illustratedStoryTurn == null) return;
            if (MultiplayerPlugin.Server == null) return;

            Task.Run(() =>
            {
                try
                {
                    string pathNoExt = illustratedStoryTurn.GetImgPathNoExt(GameEntity.ImgType.REGULAR);
                    string fullPathNoExt = System.IO.Path.Combine(SS.I.saveTopLvlDir, pathNoExt);

                    string pngPath = fullPathNoExt + ".png";
                    bool isPng = File.Exists(pngPath);
                    if (!isPng)
                    {
                        pngPath = fullPathNoExt + ".jpg";
                        if (!File.Exists(pngPath)) return;
                    }

                    byte[] imgBytes = File.ReadAllBytes(pngPath);
                    string base64 = Convert.ToBase64String(imgBytes);
                    string turnText = illustratedStoryTurn.turnTxt ?? "";
                    string fileName = System.IO.Path.GetFileName(pngPath);

                    MultiplayerPlugin.Server?.BroadcastStoryImage(base64, turnText, fileName);
                    MultiplayerPlugin.Instance?.Log.LogInfo(
                        $"[Host] Broadcast story image ({imgBytes.Length / 1024}KB) '{fileName}' for turn: {TruncateStr(turnText, 40)}");
                }
                catch (Exception ex)
                {
                    MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] Image relay error: {ex.Message}");
                }
            });
        }

        // -----------------------------------------------------------------------
        // Patch 6: Block image generation on the client.
        // Solo prefix — no conflict.
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "StartContinuousBackgroundTasks")]
        [HarmonyPrefix]
        public static bool Prefix_StartContinuousBackgroundTasks()
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Blocked StartContinuousBackgroundTasks.");
            return false;
        }

        // -----------------------------------------------------------------------
        // Patch 7: Block WomboClient image generation on the client.
        // Solo prefixes — no conflict (different overloads = different targets).
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(WomboClient), "GenerateImage",
            new System.Type[] { typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool Prefix_WomboGenerateImage_String()
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Blocked WomboClient.GenerateImage(string,string).");
            return false;
        }

        [HarmonyPatch(typeof(WomboClient), "GenerateImage",
            new System.Type[] { typeof(GameEntity), typeof(GameEntity.ImgGenInfo), typeof(string), typeof(string) })]
        [HarmonyPrefix]
        public static bool Prefix_WomboGenerateImage_Entity()
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Blocked WomboClient.GenerateImage(entity).");
            return false;
        }

        // -----------------------------------------------------------------------
        // Patch 8: Block AIAsker image generation on the client (covers NanoBanana).
        // Solo prefixes — no conflict (different method names = different targets).
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(AIAsker), "getGeneratedImage")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_AIAsker_getGeneratedImage(ref System.Threading.Tasks.Task __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Blocked AIAsker.getGeneratedImage.");
            __result = System.Threading.Tasks.Task.CompletedTask;
            return false;
        }

        [HarmonyPatch(typeof(AIAsker), "getGeneratedSprite")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_AIAsker_getGeneratedSprite(ref System.Threading.Tasks.Task __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Blocked AIAsker.getGeneratedSprite.");
            __result = System.Threading.Tasks.Task.CompletedTask;
            return false;
        }

        private static string TruncateStr(string s, int max) =>
            s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    // ==========================================================================
    // Patch 3: DoConvoTextFieldSubmission — Prefix + Postfix
    //
    // IMPORTANT: Both prefix and postfix for the same target MUST be in a class
    // decorated with a class-level [HarmonyPatch]. Putting them as individual
    // method-level patches in a non-patch class causes HarmonyX to silently drop
    // one or both patches during PatchAll().
    // ==========================================================================
    [HarmonyPatch(typeof(GameplayManager), "DoConvoTextFieldSubmission")]
    public static class ConvoSubmissionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GameplayManager __instance)
        {
            // Unconditional diagnostic — bypasses Instance?.Log null-check issues
            UnityEngine.Debug.Log($"[MP-DIAG] ConvoSubmissionPatch.Prefix: IsClientMode={MultiplayerPlugin.IsClientMode} IsHost={MultiplayerPlugin.IsHost} Instance={(MultiplayerPlugin.Instance != null ? "live" : "NULL")}");

            // --- Client mode: intercept and send to host ---
            if (MultiplayerPlugin.IsClientMode)
            {
                MultiplayerPlugin.Instance?.Log.LogInfo("[Client] DoConvoTextFieldSubmission intercepted.");
                if (MultiplayerPlugin.Client == null || !MultiplayerPlugin.Client.IsConnected)
                    return false; // Not connected — just swallow the input

                string text = __instance.npcConvoTextInput?.text?.Trim() ?? "";
                if (__instance.npcConvoTextInput != null)
                    __instance.npcConvoTextInput.text = "";

                if (!string.IsNullOrEmpty(text))
                {
                    MultiplayerPlugin.Client.SendAction(text);
                    MultiplayerPlugin.Client.SendTurnReady();
                    CoopStatusOverlay.Instance?.ShowActionQueued(text);
                }
                return false; // Block local AI turn
            }

            // --- Host mode: check turn gate ---
            if (!MultiplayerPlugin.IsHost) return true;

            if (MultiplayerPlugin.ShouldBlockTurn(out string reason))
            {
                __instance.toast?.ShowToast($"⏳ {reason}");
                return false;
            }

            // Relay host player's action to all clients as a story turn entry
            try
            {
                string playerAction = __instance.npcConvoTextInput?.text;
                if (!string.IsNullOrWhiteSpace(playerAction))
                {
                    string hostCharName = __instance.playerCharacter?.pcGameEntity?.name ?? "Host";
                    MultiplayerPlugin.Server?.BroadcastStoryTurn(new StoryEntry
                    {
                        Text = $"{hostCharName}: {playerAction}",
                        AuthorName = hostCharName,
                        IsPlayerAction = true,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] DoConvoTextFieldSubmission prefix error: {ex.Message}");
            }

            // Append party member context + pending client actions to the text field.
            // ProcessConvoMessage() reads npcConvoTextInput.text synchronously into a local
            // variable — before any async work — so modifying it here (Prefix fires
            // synchronously) is the reliable injection point.
            // We always inject character descriptions here (not only via BuildPromptString)
            // because BuildPromptString is not called for every open-ended action type.
            try
            {
                var pendingActions = GameplayMultiplayerPatch.GetAndClearPendingActions();
                var clients = MultiplayerPlugin.Server?.GetClients();
                bool hasClients = clients != null && clients.Count > 0;
                bool hasActions = pendingActions.Count > 0;

                if ((hasActions || hasClients) && __instance.npcConvoTextInput != null)
                {
                    UnityEngine.Debug.Log($"[MP-DIAG] ConvoSubmissionPatch.Prefix: injecting {pendingActions.Count} action(s) + {(clients?.Count ?? 0)} party member description(s)");
                    var originalText = __instance.npcConvoTextInput.text ?? "";
                    var sb = new StringBuilder();
                    if (originalText.Length > 0) sb.AppendLine(originalText);

                    // Always inject party member role cards so the AI knows who is who,
                    // even if BuildPromptString was skipped for this prompt type.
                    if (hasClients)
                    {
                        sb.AppendLine("[CO-OP PARTY MEMBERS]");
                        sb.AppendLine("This is a co-op session. The following characters are adventuring alongside the player:");
                        foreach (var c in clients)
                        {
                            var info = c.CharacterInfo;
                            if (info == null) continue;
                            sb.Append($"- {info.CharacterName}");
                            if (!string.IsNullOrEmpty(info.CharacterClass))
                                sb.Append($", {info.CharacterClass}");
                            sb.AppendLine($" (HP: {info.Health}/{info.MaxHealth})");
                            if (!string.IsNullOrEmpty(info.CharacterBackground))
                                sb.AppendLine($"  Background: {info.CharacterBackground}");
                            if (!string.IsNullOrEmpty(info.Personality))
                                sb.AppendLine($"  Personality/Goals: {info.Personality}");
                            if (!string.IsNullOrEmpty(info.CharacterAppearance))
                                sb.AppendLine($"  Appearance: {info.CharacterAppearance}");
                        }
                    }

                    if (hasActions)
                    {
                        sb.AppendLine("[CO-OP PARTY MEMBER ACTIONS]");
                        sb.AppendLine("The following party members have declared their actions for this turn:");
                        foreach (var a in pendingActions)
                            sb.AppendLine($"- {a}");
                        sb.AppendLine("Please incorporate their actions naturally into your narrative response.");
                    }

                    __instance.npcConvoTextInput.text = sb.ToString();
                }
                else
                {
                    UnityEngine.Debug.Log("[MP-DIAG] ConvoSubmissionPatch.Prefix: no party members or pending actions");
                }
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] DoConvoTextFieldSubmission action-inject error: {ex.Message}");
            }

            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(GameplayManager __instance)
        {
            if (!MultiplayerPlugin.IsHost) return;

            // NOTE: Do NOT call ClearPendingActions() here — clearing now happens inside
            // Postfix_BuildPromptString, atomically after reading the actions, because
            // this Postfix fires at the async void's first await (before BuildPromptString runs).
            MultiplayerPlugin.OnTurnCompleted(__instance);

            try
            {
                var hostInfo = MultiplayerPlugin.GetLocalCharacterInfo(__instance);
                var clients = MultiplayerPlugin.Server?.GetClients() ?? new List<ConnectedClient>();

                var members = new List<RemoteCharacterInfo> { hostInfo };
                foreach (var c in clients)
                    if (c.CharacterInfo != null) members.Add(c.CharacterInfo);

                MultiplayerPlugin.Server?.BroadcastPartyUpdate(new PartyUpdatePayload
                {
                    Members = members.ToArray()
                });
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] Post-turn party update error: {ex.Message}");
            }
        }
    }

    // ==========================================================================
    // Patch 6a+6b: SaveIO.WriteSaveFile — Prefix (client-block) + Postfix (host-broadcast)
    //
    // IMPORTANT: Same rationale as ConvoSubmissionPatch above — must be class-level.
    // ==========================================================================
    [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
    public static class WriteSaveFilePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            // Unconditional diagnostic
            UnityEngine.Debug.Log($"[MP-DIAG] WriteSaveFilePatch.Prefix: IsClientMode={MultiplayerPlugin.IsClientMode} IsHost={MultiplayerPlugin.IsHost} Instance={(MultiplayerPlugin.Instance != null ? "live" : "NULL")}");

            if (MultiplayerPlugin.IsClientMode)
            {
                MultiplayerPlugin.Instance?.Log.LogInfo("[Client] Suppressed SaveIO.WriteSaveFile (client mode).");
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            UnityEngine.Debug.Log($"[MP-DIAG] WriteSaveFilePatch.Postfix: IsHost={MultiplayerPlugin.IsHost} IsClientMode={MultiplayerPlugin.IsClientMode}");
            if (!MultiplayerPlugin.IsHost) return;
            try
            {
                var manager = SS.I?.hackyManager;
                MultiplayerPlugin.Instance?.Log.LogInfo($"[Multiplayer] WriteSaveFilePatch.Postfix: manager={(manager == null ? "null" : "set")}");
                if (manager != null)
                    MultiplayerPlugin.BroadcastSaveData(manager);
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] WriteSaveFilePatch.Postfix error: {ex.Message}");
            }

            // Sync host inventory from the game, persist, and broadcast to clients
            try
            {
                if (!MultiplayerPlugin.IsHost) return;
                var manager = SS.I?.hackyManager;
                if (manager == null) return;
                MPInventoryManager.SyncHostFromGame(manager);
                MPInventoryManager.Save();
                MultiplayerPlugin.BroadcastInventory();
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Instance?.Log.LogError($"[Multiplayer] WriteSaveFilePatch inventory sync error: {ex.Message}");
            }
        }
    }
}
