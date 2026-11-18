using System;
using HarmonyLib;
using UnityEngine;

namespace AIROG_GenContext.Patches
{
    /// <summary>
    /// GameplayManager.AfterLoadOrNewGame is the game's "the save is in" step: it runs on the main
    /// thread at the end of a load, of a new game, and of a multiplayer join, after the save data
    /// has been applied. That makes it the earliest safe moment to tell mods that a different
    /// save's switches are in force (see <see cref="ModToggles.Reloaded"/>).
    /// </summary>
    [HarmonyPatch(typeof(GameplayManager), "AfterLoadOrNewGame")]
    public static class Patch_AfterLoadOrNewGame
    {
        // A prefix, and void: the target is async. This must not wait for it and cannot skip it.
        [HarmonyPrefix]
        public static void Prefix()
        {
            try
            {
                ModToggles.RaiseReloadedIfPending();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] Mod switch reload notification failed: {ex}");
            }
        }
    }
}
