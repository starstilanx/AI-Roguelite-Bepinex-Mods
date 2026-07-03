using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.IO;
using System;
using System.Linq;

namespace AIROG_WorldExpansion
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class WorldExpansionPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.worldexpansion";
        public const string PLUGIN_NAME = "World Expansion";
        public const string PLUGIN_VERSION = "1.3.0";

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
            Harmony.CreateAndPatchAll(typeof(PlayerWorldActor));
            Harmony.CreateAndPatchAll(typeof(StrategicMapUI));
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
            if (cmd == "WORLD_STATUS")
            {
                var st = WorldData.CurrentState;
                string wars = st.ActiveWars.Count > 0
                    ? string.Join("\n", st.ActiveWars.Values.Select(w => $"  {w.ActorName} vs {w.TargetName} ({w.CasusBelli})"))
                    : "  none";
                string bounties = st.PlayerBounties.Count > 0
                    ? string.Join(", ", st.PlayerBounties.Select(u => st.Factions.TryGetValue(u, out var f) && !string.IsNullOrEmpty(f.Name) ? f.Name : u))
                    : "none";
                __instance.MessageModal().ShowModal(
                    $"Turn {st.CurrentTurn} — {st.CurrentSeason}\n" +
                    $"Economy: {st.Market.GlobalCondition} (×{st.Market.PriceMultiplier:0.##} buy, ×{st.Market.SellMultiplier:0.##} sell)\n" +
                    $"Active wars:\n{wars}\n" +
                    $"Bounties on you: {bounties}\n" +
                    $"Next major event: turn {st.NextMajorEventTurn}",
                    false, true);
                return false;
            }
            if (cmd == "WORLD_MAP")
            {
                // Open the game map with the political lens forced on
                if (__instance.mapModal != null)
                {
                    StrategicMapUI.LensRequested = true;
                    __instance.mapModal.ShowMapModal();
                }
                return false;
            }
            if (cmd == "WORLD_BOUNTY_TEST")
            {
                var fac = __instance.GetCurrentFactions()?.FirstOrDefault(f => f.GetPrettyName() != "Player");
                if (fac != null)
                {
                    WorldData.CurrentState.PlayerBounties.Add(fac.uuid);
                    WorldData.CurrentState.PlayerGrievances[fac.uuid] = 3;
                    WorldData.LogEvent($"{fac.GetPrettyName()} has placed a bounty on the player's head!", "PLAYER");
                    WorldData.QueuePlayerEvent($"{fac.GetPrettyName()} has placed a bounty on your head. Their agents are watching for you.", "FACTION_BOUNTY");
                    WorldEventsUI.MarkDirty();
                    WorldData.SaveToCurrentDir();
                }
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
