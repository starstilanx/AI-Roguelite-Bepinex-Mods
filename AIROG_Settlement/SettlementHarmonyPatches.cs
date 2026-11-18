using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AIROG_Settlement
{
    // Small, independent Harmony patches — save/load hooks and the map-location label/highlight
    // tweak. Split out of SettlementPlugin.cs (the previous single-file bundle); the bigger UI
    // construction patches live in SettlementMainUIPatch.cs and SettlementMapButtonsPatch.cs.
    //
    // Every body is wrapped: these hook game start, save, load and map drawing, and an exception
    // escaping a Harmony patch is thrown into the game method that was patched.

    [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
    public static class Patch_SaveIO_WriteSaveFile
    {
        public static void Postfix()
        {
            try
            {
                if (SettlementPlugin.Instance == null) return;
                // Production moved to TurnHappenedEvent — this hook only persists state.
                SettlementPlugin.Instance.SaveSettlementData();
                SettlementPlugin.Instance.ScheduleUiUpdate();
            }
            catch (Exception e) { SettlementPlugin.Log?.LogError($"Settlement save hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameplayManager), "Start")]
    public static class Patch_GameplayManager_Start
    {
        public static void Postfix()
        {
            try
            {
                // -=/+= keeps the subscription single across scene reloads
                GameplayManager.TurnHappenedEvent -= SettlementPlugin.OnTurnHappened;
                GameplayManager.TurnHappenedEvent += SettlementPlugin.OnTurnHappened;

                // Runs before the game reads the save file (LOAD) or builds the world (NEW):
                // both sit behind an await in StartGame that always yields first.
                SettlementPlugin.Instance?.ResetForNewScene();
            }
            catch (Exception e) { SettlementPlugin.Log?.LogError($"Settlement start hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
    public static class Patch_SaveIO_ReadSaveFile
    {
        public static void Postfix(string saveSubDir)
        {
            try
            {
                if (SettlementPlugin.Instance == null) return;
                SettlementPlugin.Instance.LoadSettlementData(saveSubDir);
            }
            catch (Exception e) { SettlementPlugin.Log?.LogError($"Settlement load hook failed: {e.Message}"); }
        }
    }

    // The game deletes every image in the save folder that no live entity owns when it saves on
    // exit ("auto-clean saves", on by default). The settlement picture belongs to no entity.
    [HarmonyPatch(typeof(SaveIO), "CleanOrphanAssetFiles", new Type[] { typeof(string), typeof(HashSet<string>) })]
    public static class Patch_SaveIO_CleanOrphanAssetFiles
    {
        public static void Prefix(HashSet<string> keepUuids)
        {
            try
            {
                string imageUuid = SettlementPlugin.Instance?.CurrentSettlement?.ImageUuid;
                if (keepUuids != null && !string.IsNullOrEmpty(imageUuid)) keepUuids.Add(imageUuid);
            }
            catch (Exception e) { SettlementPlugin.Log?.LogWarning($"Could not protect the settlement image from save cleaning: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(MapLocation), "UpdateGraphicalInfo")]
    public static class Patch_MapLocation_UpdateGraphicalInfo
    {
        public static void Postfix(MapLocation __instance)
        {
            try
            {
                Place p = __instance.GetPlace();
                if (p != null && SettlementPlugin.Instance != null && SettlementPlugin.Instance.IsSettlement(p)
                    && ModSwitch.Enabled && __instance.entityTitle != null)
                {
                    if (!__instance.entityTitle.text.Contains("[Settlement]"))
                        __instance.entityTitle.text = "[Settlement] " + __instance.entityTitle.text;

                    if (__instance.highlightedImg != null &&
                        __instance.highlightedImg.color == MapLocation.HIGHLIGHTED_COLOR)
                    {
                        __instance.highlightedImg.color = new Color(0.2f, 0.8f, 0.2f, 1.0f);
                    }
                }
            }
            catch (Exception e) { SettlementPlugin.Log?.LogWarning($"Settlement map label failed: {e.Message}"); }
        }
    }
}
