using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.IO;

namespace AIROG_Insight
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class InsightPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.insight";
        public const string PLUGIN_NAME = "Insight Mechanic";
        public const string PLUGIN_VERSION = "1.0.0";

        private void Awake()
        {
            var harmony = new Harmony(PLUGIN_GUID);
            
            var writeSaveMethod = AccessTools.Method(typeof(SaveIO), "WriteSaveFile");
            if (writeSaveMethod != null) harmony.Patch(writeSaveMethod, null, new HarmonyMethod(AccessTools.Method(typeof(InsightPlugin), nameof(Postfix_WriteSaveFile))));

            var loadGameMethod = AccessTools.Method(typeof(GameplayManager), "LoadGame");
            if (loadGameMethod != null) harmony.Patch(loadGameMethod, null, new HarmonyMethod(AccessTools.Method(typeof(InsightPlugin), nameof(Postfix_LoadGame))));
            
            // Generate some random insights just for testing when conversing with NPCs.
            var handleTurnMethod = AccessTools.Method(typeof(InteractionLogic), "HandleTurn");
            if (handleTurnMethod != null) harmony.Patch(handleTurnMethod, null, new HarmonyMethod(AccessTools.Method(typeof(InsightPlugin), nameof(Postfix_HandleTurn))));

            Logger.LogInfo("InsightPlugin Awake completed successfully.");
        }

        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                InsightData.Instance.Save(saveDir);
            }
        }

        public static void Postfix_LoadGame(GameplayManager __instance)
        {
            if (SS.I != null)
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                InsightData.Instance.Load(saveDir);
            }
        }

        public static void Postfix_HandleTurn(InteractionLogic __instance, GameCharacter characterToTalkTo)
        {
            if (characterToTalkTo != null)
            {
                if (!InsightData.Instance.NpcInsights.ContainsKey(characterToTalkTo.uuid))
                {
                    // A simple static insight for now to test the GenContext provider
                    InsightData.Instance.NpcInsights[characterToTalkTo.uuid] = "This person seems to be hiding a deep secret about their past.";
                    if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
                    {
                        InsightData.Instance.Save(Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg));
                    }
                }
            }
        }
    }
}
