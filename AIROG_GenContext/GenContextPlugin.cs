using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_GenContext
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class GenContextPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.gencontext";
        public const string PLUGIN_NAME = "GenContext";
        public const string PLUGIN_VERSION = "1.1.0";

        public static GenContextPlugin Instance { get; private set; }
        public static ConfigEntry<bool> EnableVideoGenFix;
        public static ConfigEntry<int> MaxPlannedThreadsPerPrompt;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            // Initialize Manager
            ContextManager.Init();

            MaxPlannedThreadsPerPrompt = Config.Bind("DM Notes", "MaxPlannedThreadsPerPrompt", 3,
                new ConfigDescription(
                    "How many of your pending Planned Threads (DM Director panel) the AI is shown per turn: " +
                    "overdue ones first, then the oldest. The rest wait in the queue.",
                    new AcceptableValueRange<int>(1, 8)));
            DMNotes.DmNotesManager.MaxThreadsPerPrompt = MaxPlannedThreadsPerPrompt.Value;
            MaxPlannedThreadsPerPrompt.SettingChanged += (sender, args) =>
                DMNotes.DmNotesManager.MaxThreadsPerPrompt = MaxPlannedThreadsPerPrompt.Value;

            // Apply Patches
            var harmony = new Harmony(PLUGIN_GUID);
            Harmony.CreateAndPatchAll(typeof(GenContextPlugin));

            // Patch BuildPromptString with version-aware fallback:
            // Latest has 14 params (includes actor + mpActions),
            // current has 12 params (isForUnifiedReqDeprecated + isForStoryPipeline),
            // alpha had 11 (isForUnifiedReq only), stable had 10.
            var turnActionType = AccessTools.TypeByName("TurnAction");
            var turnActionListType = turnActionType != null ? typeof(List<>).MakeGenericType(turnActionType) : null;

            var buildPromptMethod =
                (turnActionListType != null ? AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                    typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                    typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                    typeof(List<Faction>), typeof(List<string>), typeof(bool), typeof(bool),
                    typeof(GameCharacter), turnActionListType // latest (14 params)
                }) : null) ??
                AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                    typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                    typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                    typeof(List<Faction>), typeof(List<string>), typeof(bool), typeof(bool) // current (12 params)
                }) ??
                AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                    typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                    typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                    typeof(List<Faction>), typeof(List<string>), typeof(bool) // alpha (11 params)
                }) ??
                AccessTools.Method(typeof(GameplayManager), "BuildPromptString", new Type[] {
                    typeof(string).MakeByRefType(), typeof(bool), typeof(bool), typeof(InteractionInfo),
                    typeof(GameCharacter), typeof(Place), typeof(bool), typeof(VoronoiWorld),
                    typeof(List<Faction>), typeof(List<string>) // stable (10 params)
                });
            if (buildPromptMethod != null)
                harmony.Patch(buildPromptMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(ContextManager), nameof(ContextManager.Postfix_BuildPromptString))));
            else
                Logger.LogError("[GenContext] Failed to find BuildPromptString — plugin context injection disabled.");
            Harmony.CreateAndPatchAll(typeof(GenContextUI.Patch_MainLayouts_InitCommonAnchs));
            Harmony.CreateAndPatchAll(typeof(Integration.SettlementIntegration.Patch_MainLayouts_InitCommonAnchs_Integration));
            Harmony.CreateAndPatchAll(typeof(DMNotes.DmNotesResponseInterceptor.Patch_GenerateTxtNoTryStrStyle));
            Harmony.CreateAndPatchAll(typeof(DMNotes.DmNotesResponseInterceptor.Patch_ReadSaveFile));
            Harmony.CreateAndPatchAll(typeof(DMNotes.DmNotesResponseInterceptor.Patch_WriteSaveFile));
            Harmony.CreateAndPatchAll(typeof(DMNotes.DmNotesResponseInterceptor.Patch_DoNewGame));
            Harmony.CreateAndPatchAll(typeof(DMNotes.DmNotesPanel.Patch_MainLayouts));

            // Truncated-JSON repair + the "RR Compat Mode" toggle. This class carried its
            // [HarmonyPatch] attribute from the day it was written but was never passed to
            // PatchAll (PatchAll(Type) does not descend into other types), so neither feature
            // had ever been live. Isolated in a try so a future rename of Utils.GetAiJsonSubstr
            // cannot take the rest of GenContext down with it.
            try { Harmony.CreateAndPatchAll(typeof(Patches.Patch_GetAiJsonSubstr)); }
            catch (Exception ex) { Logger.LogError($"[GenContext] JSON repair / RR Compat patch failed to apply: {ex.Message}"); }
            // Tells the patch above which string is the story response. Without it, it does nothing.
            TryPatch(typeof(Patches.Patch_GenerateUnifiedResponse), "Story response marker (JSON repair / RR Compat)");

            TryPatch(typeof(DMNotes.DmNotesResponseInterceptor.Patch_GenerateTxtFromTxtAndImg), "DM Notes extraction for panorama turns");
            TryPatch(typeof(Patches.Patch_AfterLoadOrNewGame), "Mod switch reload notification");

            // Fix Video Generation Race Condition
            EnableVideoGenFix = Config.Bind("General", "EnableVideoGenFix", true, "Fixes a crash during video generation by waiting for files to appear.");
            Patches.VideoGenFix.Patch(new Harmony(PLUGIN_GUID), EnableVideoGenFix.Value);
        }

        /// <summary>Applies one patch class on its own, so a target the game renamed cannot take the rest of Awake with it.</summary>
        private void TryPatch(Type patchClass, string what)
        {
            try { Harmony.CreateAndPatchAll(patchClass); }
            catch (Exception ex) { Logger.LogError($"[GenContext] {what} patch failed to apply: {ex.Message}"); }
        }
    }
}
