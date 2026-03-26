using System;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Multiplayer.Patches
{
    /// <summary>
    /// Blocks game-state-modifying operations on the multiplayer client to prevent desync with the host.
    ///
    /// Clients run a full copy of the host's game scene (loaded from the synced save) but must
    /// never trigger independent AI actions, navigate, or modify authoritative game state locally.
    /// All meaningful actions go through DoConvoTextFieldSubmission which is already intercepted,
    /// or through the dedicated multiplayer action packets.
    ///
    /// Intercepted operations:
    ///   - AttemptVisitPlace   — Navigation buttons / exit button (host controls location)
    ///   - ProcessInteractionInfo / ProcessInteractionInfoNoTryStr
    ///                         — All AI-driven game actions: combat, item use, encounters, etc.
    ///   - QuickActions        — Generates an AI suggestion list locally (expensive + desyncing)
    ///   - Forage              — Early block before loading screen appears
    ///
    /// NOTE: AttemptVisitPlace is the *player-navigated* path. GameplayManager.LoadGame() restores
    /// location state via VisitPlace directly, so blocking AttemptVisitPlace does not interfere
    /// with save loading on the client.
    /// </summary>
    public static class ClientInterceptPatch
    {
        private const string NAV_HINT   = "⛔ Navigation is controlled by the host.";
        private const string ACT_HINT   = "⛔ Use the text box to submit actions to the host.";
        private const string ATTACK_HINT = "⚔ Type your attack action in the text box.";
        private const string FLEE_HINT   = "🏃 Type your flee action in the text box.";
        private const string FORAGE_HINT = "⛔ Type a forage action in the text box.";
        private const string QA_HINT     = "⛔ Type your action in the text box and submit.";

        // -----------------------------------------------------------------------
        // Helper: show a client-block toast on the game's Toast component and
        // also push to the CoopStatusOverlay so it's visible even when the game
        // toast is off-screen or obscured.
        // -----------------------------------------------------------------------
        private static void ShowBlockedToast(GameplayManager manager, string hint)
        {
            try { manager?.toast?.ShowToast(hint, "ff7722"); } catch { }
            try { CoopStatusOverlay.Instance?.ShowNotification(hint, 3f); } catch { }
        }

        // -----------------------------------------------------------------------
        // 1. Navigation — AttemptVisitPlace
        //    Called by: ExitThisPlace, map-modal clicks, prev/next/up buttons
        //    Return type: async Task
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "AttemptVisitPlace",
            new Type[] { typeof(Place), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_AttemptVisitPlace(GameplayManager __instance, ref Task __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            ShowBlockedToast(__instance, NAV_HINT);
            __result = Task.CompletedTask;
            return false;
        }

        // -----------------------------------------------------------------------
        // 2a. All AI-driven game actions — ProcessInteractionInfo
        //     Called by: combat buttons, item use in combat, NPC interactions, etc.
        //     Return type: async Task<GameEventResult>
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "ProcessInteractionInfo",
            new Type[] { typeof(InteractionInfo), typeof(bool) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_ProcessInteractionInfo(GameplayManager __instance,
            InteractionInfo interactionInfo, ref Task<GameEventResult> __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            ShowBlockedToast(__instance, GetHintForInteraction(interactionInfo));
            __result = Task.FromResult(new GameEventResult());
            return false;
        }

        // -----------------------------------------------------------------------
        // 2b. NoTryStr variant — ProcessInteractionInfoNoTryStr
        //     Called when IsNoTryStrStyle() is true (some game modes / scenarios).
        //     Return type: async Task<GameEventResult>
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "ProcessInteractionInfoNoTryStr",
            new Type[] { typeof(InteractionInfo), typeof(bool) })]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_ProcessInteractionInfoNoTryStr(GameplayManager __instance,
            InteractionInfo interactionInfo, ref Task<GameEventResult> __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            ShowBlockedToast(__instance, GetHintForInteraction(interactionInfo));
            __result = Task.FromResult(new GameEventResult());
            return false;
        }

        // -----------------------------------------------------------------------
        // 3. Forage — early block before loading screen appears
        //    Forage() also calls ProcessInteractionInfo internally, but blocking
        //    here gives better UX: no loading-screen flash.
        //    Return type: async Task
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "Forage")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_Forage(GameplayManager __instance, ref Task __result)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            ShowBlockedToast(__instance, FORAGE_HINT);
            __result = Task.CompletedTask;
            return false;
        }

        // -----------------------------------------------------------------------
        // 4. QuickActions — blocks local AI generation of suggestion list
        //    Return type: async void (no __result needed)
        // -----------------------------------------------------------------------
        [HarmonyPatch(typeof(GameplayManager), "QuickActions")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.VeryHigh)]
        public static bool Prefix_QuickActions(GameplayManager __instance)
        {
            if (!MultiplayerPlugin.IsClientMode) return true;
            ShowBlockedToast(__instance, QA_HINT);
            return false;
        }

        // -----------------------------------------------------------------------
        // Hint selector based on interaction type
        // -----------------------------------------------------------------------
        private static string GetHintForInteraction(InteractionInfo info)
        {
            try
            {
                switch (info?.interacterInfo?.interacterType)
                {
                    case InteracterInfo.InteracterType.ATTACK_WITH_ITEM:
                        return ATTACK_HINT;
                    case InteracterInfo.InteracterType.RUN_AWAY:
                        return FLEE_HINT;
                    default:
                        return ACT_HINT;
                }
            }
            catch
            {
                return ACT_HINT;
            }
        }
    }
}
