using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AIROG_Mythic
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.airog.npcexpansion", BepInDependency.DependencyFlags.SoftDependency)]
    public class MythicPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.mythic";
        public const string PLUGIN_NAME = "AIROG Mythic Director";
        public const string PLUGIN_VERSION = "1.1.0";

        public static MythicPlugin Instance { get; private set; }

        public static ConfigEntry<int> CfgStartingCF;
        public static ConfigEntry<string> CfgChaosVariation;
        public static ConfigEntry<bool> CfgInjectRegisterDirective;
        public static ConfigEntry<bool> CfgEnableRandomEvents;
        public static ConfigEntry<int> CfgEventCooldownTurns;
        public static ConfigEntry<int> CfgMaxEventsPerScene;
        public static ConfigEntry<bool> CfgEnableSceneTest;
        public static ConfigEntry<bool> CfgSceneTestOnlyNewPlaces;

        private void Awake()
        {
            Instance = this;

            CfgStartingCF = Config.Bind("General", "StartingChaosFactor", 5,
                new ConfigDescription("Chaos Factor for new games (1 = ordered world, 9 = spiraling).",
                    new AcceptableValueRange<int>(1, 9)));
            CfgChaosVariation = Config.Bind("General", "ChaosVariation", "Standard",
                "How strongly the Chaos Factor sways the oracle and the director. " +
                "Standard = full effect; Low = CF clamped to 4-6; None = CF tracked but ignored (always treated as 5).");
            CfgInjectRegisterDirective = Config.Bind("General", "InjectNarrativeRegister", true,
                "Always inject a short directive telling the AI how volatile the world currently feels (never states numbers).");

            CfgEnableRandomEvents = Config.Bind("Events", "EnableRandomEvents", true,
                "Each turn, doubles on d100 whose digit is within the Chaos Factor fire a director event " +
                "woven into the next AI generations.");
            CfgEventCooldownTurns = Config.Bind("Events", "EventCooldownTurns", 8,
                "Minimum turns between director events.");
            CfgMaxEventsPerScene = Config.Bind("Events", "MaxEventsPerScene", 2,
                "Maximum director events per scene (one stay at a top-level place).");

            CfgEnableSceneTest = Config.Bind("SceneTest", "EnableSceneTest", false,
                "Test arrivals at top-level places against the Chaos Factor: d10 <= CF means the scene is " +
                "Altered (odd) or Interrupted (even). Off by default — travel is frequent in AI Roguelite.");
            CfgSceneTestOnlyNewPlaces = Config.Bind("SceneTest", "SceneTestOnlyNewPlaces", true,
                "Only test arrivals at places the player has never visited before.");

            Logger.LogInfo($"[Mythic] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            Harmony.CreateAndPatchAll(typeof(RandomEventEngine.Patch_TurnHappened));
            Harmony.CreateAndPatchAll(typeof(SceneTest.Patch_ApplyLocationChange));
            Harmony.CreateAndPatchAll(typeof(ChaosEngine.Patch_SetAsCorpse));
            Harmony.CreateAndPatchAll(typeof(Patch_SaveLoad));
            Harmony.CreateAndPatchAll(typeof(ConsoleCommands));
            // Last and guarded: RandomEvents only exists from the 11/18/2026 game build on, and a
            // failure here must not take the patches above or the provider registration with it.
            try { Harmony.CreateAndPatchAll(typeof(RandomEventEngine.Patch_NativeRandomEvent)); }
            catch (Exception ex) { Logger.LogWarning($"[Mythic] Native random-event hook unavailable. ({ex.GetType().Name})"); }

            // Register with GenContext if present (soft dependency — inert without it)
            try
            {
                AIROG_GenContext.ContextManager.RegisterProvider(new MythicProvider());
                Logger.LogInfo("[Mythic] MythicProvider registered with GenContext.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Mythic] GenContext not available — prompt injection disabled. ({ex.Message})");
            }
        }

        // ── Per-save master switch (GenContext "Mods" checklist) ─────────────────

        private static bool _noModToggles;

        /// <summary>False when this save has the Mythic Director switched off. GenContext is a
        /// soft dependency: when it (or its ModToggles) is absent the mod is always on.</summary>
        public static bool SwitchedOn()
        {
            if (_noModToggles) return true;
            try { return QuerySwitch(); }
            catch { _noModToggles = true; return true; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool QuerySwitch()
        {
            return AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.Mythic);
        }

        // ── Save / load lifecycle ────────────────────────────────────────────────

        public static class Patch_SaveLoad
        {
            [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
            [HarmonyPostfix]
            // No injected args: WriteSaveFile's first parameter is MmCtxGetter since the 07/28 build,
            // so declaring it as GameplayManager was an unchecked type mismatch.
            public static void Postfix_WriteSaveFile()
            {
                // LoadGame can autosave (every-turn autosave) before our LoadGame postfix has read
                // this save's file. Memory still holds the previous save's state then - don't write it.
                if (MythicData.Loading) return;
                MythicData.SaveToCurrentDir();
            }

            [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
            [HarmonyPrefix]
            public static void Prefix_LoadGame()
            {
                MythicData.Loading = true;
            }

            [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
            [HarmonyPostfix]
            public static void Postfix_LoadGame(GameplayManager __instance)
            {
                try
                {
                    if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                    {
                        MythicData.Load(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
                        ChaosEngine.OnAfterLoad();
                    }
                }
                finally
                {
                    MythicData.Loading = false;
                    RandomEventEngine.NativeEventThisTurn = false; // the turn it belonged to is gone
                }
            }

            [HarmonyPatch(typeof(MainMenu), "NewGame")]
            [HarmonyPostfix]
            public static void Postfix_NewGame(MainMenu __instance)
            {
                MythicData.Reset();
                ChaosEngine.OnAfterLoad();
            }
        }
    }
}
