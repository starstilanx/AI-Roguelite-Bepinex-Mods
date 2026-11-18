using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIROG_WorldExpansion
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class WorldExpansionPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.worldexpansion";
        public const string PLUGIN_NAME = "World Expansion";
        public const string PLUGIN_VERSION = "1.5.0";

        public static WorldExpansionPlugin Instance { get; private set; }

        // Set from the mod-switch callbacks, which run inside whichever call raised them; the
        // UI is re-synced from Update() instead, on the main thread and outside that call.
        private static volatile bool _switchUiDirty;

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
            Harmony.CreateAndPatchAll(typeof(FactionCourtSystem));

            ModSwitch.Hook(() => _switchUiDirty = true);
        }

        private void Update()
        {
            if (!_switchUiDirty) return;
            _switchUiDirty = false;
            try
            {
                var manager = SS.I?.hackyManager;
                if (manager == null) return;
                WorldEventsUI.ApplyModSwitch(manager.journalModal);
                StrategicMapUI.ApplyModSwitch(manager.mapModal);

                // If Lore is the journal's selected tab, swap it between the category view and the
                // native list now (any other time the game redraws it when the tab is clicked)
                var lore = manager.journalModal != null ? manager.journalModal.lorebookView : null;
                if (lore != null && lore.gameObject.activeSelf && manager.scenarioState != null) lore.Redraw();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Could not apply the mod switch to the UI: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        [HarmonyPostfix]
        // No injected args: WriteSaveFile's first parameter is MmCtxGetter since the 07/28 build,
        // so declaring it as GameplayManager was an unchecked type mismatch.
        public static void Postfix_WriteSaveFile()
        {
            try
            {
                if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                {
                    string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                    WorldData.Save(saveDir);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WorldExpansion] Save hook failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "LoadGame")]
        [HarmonyPostfix]
        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            try
            {
                if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                {
                    string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                    WorldData.Load(saveDir);
                    WorldEventsUI.MarkDirty();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WorldExpansion] Load hook failed: {e.Message}");
            }
        }

        // The game deletes every image in the save folder that no live entity owns when it saves
        // on exit ("auto-clean saves", on by default). Lore images belong to no entity, so they
        // were wiped each time the player returned to the main menu.
        [HarmonyPatch(typeof(SaveIO), "CleanOrphanAssetFiles", new Type[] { typeof(string), typeof(HashSet<string>) })]
        [HarmonyPrefix]
        public static void Prefix_CleanOrphanAssetFiles(HashSet<string> keepUuids)
        {
            try
            {
                if (keepUuids == null || WorldData.CurrentState?.LoreExtras == null) return;
                foreach (var extra in WorldData.CurrentState.LoreExtras.Values)
                    if (extra != null && !string.IsNullOrEmpty(extra.ImageUuid)) keepUuids.Add(extra.ImageUuid);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldExpansion] Could not protect lore images from save cleaning: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        [HarmonyPrefix]
        public static bool Prefix_ProcessConsoleCommand(string txt, GameplayManager __instance)
        {
            string cmd = (txt ?? "").Trim().ToUpperInvariant();
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
                MessageModal.I.ShowModal(
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
            if (cmd == "WORLD_COURT")
            {
                MessageModal.I.ShowModal(FactionCourtSystem.BuildCourtReport(), false, true);
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
            if (WorldData.CurrentState != null && WorldData.CurrentState.Market != null && ModSwitch.Enabled)
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
            if (WorldData.CurrentState != null && WorldData.CurrentState.Market != null && ModSwitch.Enabled)
            {
                float mult = WorldData.CurrentState.Market.SellMultiplier;
                __result = (long)(__result * mult);
            }
        }
    }
}
