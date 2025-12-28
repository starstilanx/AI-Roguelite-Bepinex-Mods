using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AIROG_LoopBeGone
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class LoopBeGonePlugin : BaseUnityPlugin
    {
        public const string GUID = "com.airog.loopbegone";
        public const string NAME = "AIROG LoopBeGone";
        public const string VERSION = "1.0.0";

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> SeverityThreshold;
        public static ConfigEntry<bool> DebugMode;

        private void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable loop detection.");
            SeverityThreshold = Config.Bind("General", "SeverityThreshold", 0.7f, "The threshold at which a loop is considered significant (0.0 to 1.0).");
            DebugMode = Config.Bind("Debug", "DebugMode", false, "Append <LOOP_DETECTED> to repetitive AI output for testing.");

            if (!Enabled.Value) return;

            var harmony = new Harmony(GUID);
            harmony.PatchAll();

            Logger.LogInfo($"{NAME} {VERSION} loaded.");
        }
    }
}
