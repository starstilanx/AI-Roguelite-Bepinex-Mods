using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AIROG_RandomOrg
{
    [BepInPlugin("com.airog.randomorg", "AIROG True Randomness (Random.org)", "1.0.0")]
    // Soft-dep: if GenContext is present we inject into its Mods menu; not required.
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    public class RandomOrgPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // ---- BepInEx config entries ----------------------------------------
        public static ConfigEntry<bool>   Enabled;
        public static ConfigEntry<string> ApiKey;
        public static ConfigEntry<bool>   ShowNotification;
        public static ConfigEntry<int>    BufferSize;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind(
                "General", "Enabled", false,
                "Enable true randomness from Random.org for all dice rolls. Requires internet.");

            ApiKey = Config.Bind(
                "General", "ApiKey", "",
                "Your Random.org API key (optional). Leave blank for the free anonymous interface " +
                "(~1 million bits/day). Get a free key at https://api.random.org/");

            ShowNotification = Config.Bind(
                "General", "ShowNotification", true,
                "Log each roll result to the BepInEx console when true randomness is active.");

            BufferSize = Config.Bind(
                "General", "BufferSize", 1000,
                "How many integers to pre-fetch per Random.org request. " +
                "Higher = fewer network calls. Valid range: 1–10000.");

            // --- Harmony patches ------------------------------------------------
            _harmony = new Harmony("com.airog.randomorg");

            // Dice-roll replacements (attribute-based, found by PatchAll)
            _harmony.PatchAll(typeof(Patch_Utils_RandDouble));
            _harmony.PatchAll(typeof(Patch_Utils_RandInt));
            _harmony.PatchAll(typeof(Patch_Utils_RandIntInclusive));

            // GenContext Mods-menu injection (runtime method lookup)
            TryPatchGenContextMenu();

            // Pre-fetch buffer if enabled at startup
            if (Enabled.Value)
                RandomOrgClient.PrimeFetch();

            Log.LogInfo($"[RandomOrg] Loaded. True randomness is " +
                        $"{(Enabled.Value ? "ENABLED" : "disabled")}. " +
                        $"Mode: {(string.IsNullOrWhiteSpace(ApiKey.Value) ? "anonymous" : "API key")}.");
        }

        private void TryPatchGenContextMenu()
        {
            try
            {
                var modalType = AccessTools.TypeByName("NTextPromptModal");
                if (modalType == null)
                {
                    Log.LogInfo("[RandomOrg] NTextPromptModal not found — Mods menu injection skipped.");
                    return;
                }

                MethodInfo presentSelf = null;
                foreach (var m in modalType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name == "PresentSelf" && m.GetParameters().Length >= 2)
                    {
                        presentSelf = m;
                        break;
                    }
                }

                if (presentSelf == null)
                {
                    Log.LogWarning("[RandomOrg] PresentSelf not found — Mods menu injection skipped.");
                    return;
                }

                var prefix = typeof(RandomOrgUI).GetMethod("PresentSelf_Prefix",
                    BindingFlags.Public | BindingFlags.Static);

                _harmony.Patch(presentSelf, prefix: new HarmonyMethod(prefix));
                Log.LogInfo("[RandomOrg] Injected settings into GenContext Mods menu.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[RandomOrg] Mods menu injection failed (non-fatal): {ex.Message}");
            }
        }
    }
}
