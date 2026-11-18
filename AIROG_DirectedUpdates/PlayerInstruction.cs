using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.InputSystem;

namespace AIROG_DirectedUpdates
{
    /// <summary>
    /// Player-issued instructions: "Instruct update..." in the entity right-click menu asks for
    /// an instruction and runs the game's own state-change step for that entity, exactly as if
    /// the GM had listed it in update_entities with that instruction. The step itself is not
    /// re-implemented: a regular "Details updated" icon (DecisionType.LEARNED_NAME) is raised
    /// under a new log line with the instruction already on it, and its callback is invoked.
    /// If the AI call fails the icon stays clickable, which is the retry.
    /// </summary>
    public static class PlayerInstruction
    {
        public enum QuickModifierKey
        {
            None,
            Ctrl,
            Alt,
            Shift
        }

        private const string MENU_LABEL = "Instruct update...";

        private static readonly HashSet<string> inFlight = new HashSet<string>();

        /// <summary>The component the state-change callback can work on, or null.</summary>
        private static InGameCpn TargetOf(GameEntity entity)
        {
            InGameCpn cpn = entity?.AsCpnGe() as InGameCpn;
            if (cpn == null || cpn.parentInGameEnt == null || string.IsNullOrEmpty(cpn.uuid)) return null;
            return SS.I.uuidToGameEntityMap.ContainsKey(cpn.uuid) ? cpn : null;
        }

        private static string WhyUnavailable(GameplayManager manager, InGameCpn target)
        {
            if (MpSession.IsClient) return "Only the host can instruct an update.";
            if (manager.expectingInteracteeFromItemOrAbil) return "Pick or cancel the target first.";
            if (Utils.PlayerInteractionsDisabled()) return "The game is busy. Try again in a moment.";
            if (!SS.I.uuidToGameEntityMap.ContainsKey(target.uuid)) return "That entity no longer exists.";
            if (inFlight.Contains(target.parentInGameEnt.uuid)) return "An update of " + target.GetPrettyNameForUi() + " is already running.";
            return null;
        }

        /// <summary>
        /// MmHelper.GetCommonEditMenuRows builds the top of every entity context menu (in-game
        /// and in the new-game screens); the row goes right under "Edit details".
        /// </summary>
        public static void GetCommonEditMenuRows_Postfix(MmCtxGetter mm, GameEntity entity, List<DropdownMenuItem> __result)
        {
            try
            {
                if (__result == null || !DirectedUpdatesPlugin.IsOn(DirectedUpdatesPlugin.PlayerInstructionEnabled)) return;
                GameplayManager manager = mm as GameplayManager;
                if (manager == null || MpSession.IsClient || !manager.WorldEditsAllowed()) return;
                InGameCpn target = TargetOf(entity);
                if (target == null) return;

                DropdownMenuItem row = new DropdownMenuItem(MENU_LABEL, () =>
                {
                    OpenPrompt(manager, target);
                    return Task.CompletedTask;
                });
                string editLabel = LS.I.GetLocStr("edit_details");
                int at = __result.FindIndex(r => r != null && r.menuItemText == editLabel);
                __result.Insert(at >= 0 ? at + 1 : __result.Count, row);
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"GetCommonEditMenuRows postfix failed: {ex}");
            }
        }

        /// <summary>Optional shortcut: modifier + right-click opens the prompt instead of the menu.</summary>
        public static bool ShowEditDetailsOrRedoImageMenu_Prefix(GameplayManager __instance, GameEntity entity)
        {
            try
            {
                if (!DirectedUpdatesPlugin.IsOn(DirectedUpdatesPlugin.PlayerInstructionEnabled) || !QuickModifierHeld()) return true;
                InGameCpn target = TargetOf(entity);
                if (target == null || WhyUnavailable(__instance, target) != null) return true;
                OpenPrompt(__instance, target);
                return false;
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"ShowEditDetailsOrRedoImageMenu prefix failed: {ex}");
                return true;
            }
        }

        private static bool QuickModifierHeld()
        {
            QuickModifierKey mod = DirectedUpdatesPlugin.PlayerInstructionQuickModifier.Value;
            Keyboard kb = Keyboard.current;
            if (mod == QuickModifierKey.None || kb == null) return false;
            switch (mod)
            {
                case QuickModifierKey.Ctrl: return kb.ctrlKey.isPressed;
                case QuickModifierKey.Alt: return kb.altKey.isPressed;
                case QuickModifierKey.Shift: return kb.shiftKey.isPressed;
                default: return false;
            }
        }

        private static void OpenPrompt(GameplayManager manager, InGameCpn target)
        {
            try
            {
                string why = WhyUnavailable(manager, target);
                if (why != null)
                {
                    Toast.I.ShowToast(why);
                    return;
                }

                string typed = null;
                NTextPromptModal.I.PresentSelf(
                    new List<NTextPromptModal.PromptArg>
                    {
                        new NTextPromptModal.TextPromptArg(
                            "Instruction for the update step (applied like a GM instruction, with the most recent story turn as context):",
                            "", "e.g. add a sprained ankle status effect", true, s => typed = s)
                    },
                    target.GetPrettyNameForUi(),
                    () =>
                    {
                        string instruction = typed?.Trim();
                        if (string.IsNullOrEmpty(instruction)) return;
                        Run(manager, target, instruction);
                    },
                    requireNonEmptyInputForConfirm: true);
            }
            catch (Exception ex)
            {
                DirectedUpdatesPlugin.LogWarn($"Could not open the instruction prompt: {ex}");
            }
        }

        private static async void Run(GameplayManager manager, InGameCpn target, string instruction)
        {
            InGameEntity owner = target.parentInGameEnt;
            string name = target.GetPrettyNameForUi();
            if (!inFlight.Add(owner.uuid))
            {
                Toast.I.ShowToast("An update of " + name + " is already running.");
                return;
            }
            AiDecisionIcon icon = null;
            bool sent = false;
            try
            {
                string before = Snapshot(owner);
                // The loading block is taken here, before the prompt modal closes, so no turn can
                // be started in the frames between the modal closing and the AI call starting.
                await Utils.DoTaskWithLoadingScreen(async delegate
                {
                    // Same context a GM update of the latest turn gets: GetLastStoryTurnStrNoNewlines
                    // is what the game puts in result.storyEvents, BuildContextChatTurnUuids what it
                    // passes to CreateForStateChange (null in the specialized pipeline, which has no
                    // chat turns; the icon then falls back to the story turn itself).
                    StoryTurn lastStory = manager.GetLastStoryTurn();
                    ChatTurnV2 lastChat = manager.scenarioState?.chatHistory?.turns?.LastOrDefault();
                    AiDecisionIcon.SerializableCbParams cbParams = AiDecisionIcon.SerializableCbParams.CreateForStateChange(
                        target, manager.GetLastStoryTurnStrNoNewlines(), lastStory, null,
                        manager.BuildContextChatTurnUuids(lastStory, lastChat));
                    DirectedUpdatesPlugin.Stash(cbParams, DirectedUpdatesPlugin.STASH_PLAYER, instruction);

                    GameLogViewObj line = await manager.gameLogView.LogText(
                        GameLogView.Decision("Instruction for " + name + ": " + instruction));
                    if (line == null) return;
                    string tooltip = target.IsMainPlayer()
                        ? LS.I.GetLocStr("player-details-updated-iconmsg")
                        : LS.I.GetLocStr("details-updated-iconmsg", ("entity_name", target.GetPrettyName()));
                    icon = line.AddAiDecisionIcon(AiDecisionIcon.DecisionType.LEARNED_NAME, tooltip, cbParams);
                    if (icon == null) return;
                    sent = true;
                    await icon.GetCb()(true);
                });

                if (!sent)
                {
                    Toast.I.ShowToast("The instruction for " + name + " was not sent (the log was being rebuilt).");
                }
                else if (icon == null || !icon.wasClicked)
                {
                    Toast.I.ShowToast("The instruction for " + name + " could not be applied.");
                }
                else if (Snapshot(owner) == before)
                {
                    await manager.gameLogView.LogText(GameLogView.Decision(name + ": the update step made no changes." +
                        (owner.lockNameAndDesc ? " Its name and description are locked." : "")));
                }
            }
            catch (Exception ex)
            {
                // The game has already shown its own error dialog for a failed AI call.
                DirectedUpdatesPlugin.LogWarn($"Player instruction for '{name}' failed: {ex}");
                bool canRetry = icon != null && !icon.wasClicked;
                Toast.I?.ShowToast("The instruction for " + name + " was not applied." +
                    (canRetry ? " Click the name-tag icon under it in the log to retry." : ""));
            }
            finally
            {
                inFlight.Remove(owner.uuid);
            }
        }

        // Only used to tell "applied" from "the updater changed nothing"; the game itself logs
        // nothing in the second case.
        private static string Snapshot(InGameEntity owner)
        {
            InGameCpn cpn = owner.GetActiveCpn();
            return string.Join("\u0001",
                owner.GetPrettyName(),
                cpn?.GetType().Name,
                cpn?.GetPotentiallyNullDescription(),
                cpn?.GetStatusFxV2ForPrompts(),
                (cpn as GameItem)?.quantity.ToString(),
                (cpn as GameCharacter)?.GetRelationTo());
        }
    }
}
