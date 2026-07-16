using HarmonyLib;
using UnityEngine;

namespace AIROG_Settlement
{
    // Small, independent Harmony patches — save/load hooks and the map-location label/highlight
    // tweak. Split out of SettlementPlugin.cs (the previous single-file bundle); the bigger UI
    // construction patches live in SettlementMainUIPatch.cs and SettlementMapButtonsPatch.cs.

    [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
    public static class Patch_SaveIO_WriteSaveFile
    {
        public static void Postfix()
        {
            if (SettlementPlugin.Instance == null) return;
            // Production moved to TurnHappenedEvent — this hook only persists state.
            SettlementPlugin.Instance.SaveSettlementData();
            SettlementPlugin.Instance.ScheduleUiUpdate();
        }
    }

    [HarmonyPatch(typeof(GameplayManager), "Start")]
    public static class Patch_GameplayManager_Start
    {
        public static void Postfix()
        {
            // -=/+= keeps the subscription single across scene reloads
            GameplayManager.TurnHappenedEvent -= SettlementPlugin.OnTurnHappened;
            GameplayManager.TurnHappenedEvent += SettlementPlugin.OnTurnHappened;
        }
    }

    [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
    public static class Patch_SaveIO_ReadSaveFile
    {
        public static void Postfix(string saveSubDir)
        {
            if (SettlementPlugin.Instance == null) return;
            SettlementPlugin.Instance.LoadSettlementData(saveSubDir);
        }
    }

    [HarmonyPatch(typeof(MapLocation), "UpdateGraphicalInfo")]
    public static class Patch_MapLocation_UpdateGraphicalInfo
    {
        public static void Postfix(MapLocation __instance)
        {
            Place p = __instance.GetPlace();
            if (p != null && SettlementPlugin.Instance != null && SettlementPlugin.Instance.IsSettlement(p))
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
    }
}
