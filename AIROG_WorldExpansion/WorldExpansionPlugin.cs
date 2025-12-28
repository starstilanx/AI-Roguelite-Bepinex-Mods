using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.IO;
using System;

namespace AIROG_WorldExpansion
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class WorldExpansionPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.worldexpansion";
        public const string PLUGIN_NAME = "World Expansion";
        public const string PLUGIN_VERSION = "1.0.0";

        public static WorldExpansionPlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            Harmony.CreateAndPatchAll(typeof(WorldExpansionPlugin));
            
            // Register other patches
            Harmony.CreateAndPatchAll(typeof(WorldSimulation));
            Harmony.CreateAndPatchAll(typeof(WorldEventsUI));
            Harmony.CreateAndPatchAll(typeof(WorldLoreExpansion));
            Harmony.CreateAndPatchAll(typeof(WorldPromptInjection));
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        [HarmonyPostfix]
        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                WorldData.Save(saveDir);
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
        [HarmonyPostfix]
        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                WorldData.Load(saveDir);
                WorldEventsUI.MarkDirty();
            }
        }
        
        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        [HarmonyPrefix]
        public static bool Prefix_ProcessConsoleCommand(string txt, GameplayManager __instance)
        {
            string cmd = txt.ToUpperInvariant();
            if (cmd == "WORLD_SIM_TEST")
            {
                WorldSimulation.RunMinorTick(__instance);
                return false;
            }
            if (cmd == "WORLD_MAJOR_TEST")
            {
                WorldSimulation.RunMajorTick(__instance);
                return false;
            }
            if (cmd == "WORLD_ECON_TEST")
            {
                WorldSimulation.RunEconomyTick(__instance);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(MainMenu), "NewGame")]
        [HarmonyPostfix]
        public static void Postfix_NewGame(MainMenu __instance)
        {
            // Reset world data on new game
            WorldData.Reset();
        }

        [HarmonyPatch(typeof(Utils), "GetItemGoldValForBuying")]
        [HarmonyPostfix]
        public static void Postfix_GetItemGoldValForBuying(GameItem item, ref long __result)
        {
            if (WorldData.CurrentState != null && WorldData.CurrentState.Market != null)
            {
                // Apply global multiplier
                float mult = WorldData.CurrentState.Market.PriceMultiplier;
                // Apply type modifier if checked (simplified for now)
                
                __result = (long)(__result * mult);
            }
        }

        [HarmonyPatch(typeof(Utils), "GetItemGoldValForSelling")]
        [HarmonyPostfix]
        public static void Postfix_GetItemGoldValForSelling(GameItem item, ref long __result)
        {
            if (WorldData.CurrentState != null && WorldData.CurrentState.Market != null)
            {
                float mult = WorldData.CurrentState.Market.SellMultiplier;
                __result = (long)(__result * mult);
            }
        }
    }
}
