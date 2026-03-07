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
        public const string PLUGIN_VERSION = "1.0.0";

        public static GenContextPlugin Instance { get; private set; }
        public static ConfigEntry<bool> EnableVideoGenFix;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            // Initialize Manager
            ContextManager.Init();

            // Apply Patches
            var harmony = new Harmony(PLUGIN_GUID);
            Harmony.CreateAndPatchAll(typeof(GenContextPlugin));

            // Patch BuildPromptString with version-aware fallback:
            // Alpha has 11 params (includes isForUnifiedReq), stable has 10.
            var buildPromptMethod =
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

            // Fix Video Generation Race Condition
            EnableVideoGenFix = Config.Bind("General", "EnableVideoGenFix", true, "Fixes a crash during video generation by waiting for files to appear.");
            Patches.VideoGenFix.Patch(new Harmony(PLUGIN_GUID), EnableVideoGenFix.Value);
        }
    }
}
