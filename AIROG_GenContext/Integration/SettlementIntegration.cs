using HarmonyLib;
using UnityEngine;
using System.Linq;

namespace AIROG_GenContext.Integration
{
    /// <summary>
    /// Hides/shows the Settlement mod's HUD button (and closes its panel and event popup) to
    /// match the "Settlement Info" switch. Current Settlement builds do this themselves from
    /// ModToggles; this stays as the fallback for an older AIROG_Settlement.dll, which knows
    /// nothing about the switch. Loose coupling on purpose: objects are found by name.
    /// </summary>
    public static class SettlementIntegration
    {
        public const string PROVIDER_NAME = "Settlement Info";

        private static bool _hooked;

        public static void ApplyState()
        {
            bool enabled = ContextManager.IsProviderEnabled(PROVIDER_NAME);
            ApplyState(enabled);
        }

        public static void ApplyState(bool enabled)
        {
            try
            {
                ApplyState(enabled, null);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[GenContext] Could not apply the Settlement switch to its UI: {ex.Message}");
            }
        }

        private static void ApplyState(bool enabled, MainLayouts layouts)
        {
            // Always go through MainLayouts: Transform.Find also returns INACTIVE children, which
            // GameObject.Find does not - so a button we hid earlier can be found again to show it.
            if (layouts == null) layouts = SS.I?.hackyManager?.mainLayouts;
            if (layouts == null) layouts = UnityEngine.Object.FindObjectOfType<MainLayouts>();
            if (layouts == null) return; // main menu, or the HUD is not built yet

            // 1. HUD button
            if (layouts.buttonsHolderHolder != null)
            {
                var button = layouts.buttonsHolderHolder.Find("SettlementButton");
                if (button != null && button.gameObject.activeSelf != enabled) button.gameObject.SetActive(enabled);
            }

            // 2. Panel and event popup: closed when the mod is off (never opened from here)
            if (!enabled && layouts.mainHolder != null)
            {
                var modal = layouts.mainHolder.Find("SettlementModal");
                if (modal != null) modal.gameObject.SetActive(false);
                var popup = layouts.mainHolder.Find("SettlementEventPopup");
                if (popup != null) popup.gameObject.SetActive(false);
            }
        }

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts_InitCommonAnchs_Integration
        {
            // Priority.Last: this has to run AFTER the Settlement mod's own InitCommonAnchs
            // postfix, which is what creates the button. GenContext loads (and so registers its
            // patches) first, and at equal priority Harmony runs postfixes in registration order -
            // so this used to look for a button that did not exist yet, and the button came back
            // on every load no matter what the checklist said.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(MainLayouts __instance)
            {
                // InitCommonAnchs is called from GameplayManager.StartGame; never throw into it.
                try
                {
                    HookSwitchEvents();
                    ApplyState(ContextManager.IsProviderEnabled(PROVIDER_NAME), __instance);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[GenContext] Settlement integration failed: {ex.Message}");
                }
            }
        }

        private static void HookSwitchEvents()
        {
            if (_hooked) return;
            _hooked = true;
            // UI only in both handlers (see the note on ModToggles.Reloaded).
            ModToggles.Changed += (key, enabled) => { if (key == PROVIDER_NAME) ApplyState(enabled); };
            ModToggles.Reloaded += ApplyState;
        }
    }
}
