using HarmonyLib;
using UnityEngine;
using System.Linq;

namespace AIROG_GenContext.Integration
{
    public static class SettlementIntegration
    {
        public const string PROVIDER_NAME = "Settlement Info";

        public static void ApplyState()
        {
            bool enabled = ContextManager.IsProviderEnabled(PROVIDER_NAME);
            ApplyState(enabled);
        }

        public static void ApplyState(bool enabled)
        {
            // 1. HUD Button
            // Usually located in MainLayouts.buttonsHolderHolder -> "SettlementButton"
            // We can look up by name globally or via MainLayouts if available, but Find is safest for loose coupling
            
            // Try finding by precise path if possible or by global name search (expensive but safe for UI)
            // "SettlementButton" is likely unique
            var btn = GameObject.Find("SettlementButton");
            if (btn != null)
            {
                btn.SetActive(enabled);
            }
            else
            {
                // Fallback: It might be under buttonsHolderHolder and disabled, so Find won't see it if we don't look carefully.
                // However, GameObject.Find only finds active objects?
                // Actually, GameObject.Find only finds ACTIVE objects. 
                // If we disabled it, we can't find it back to enable it!
                // We need a persistent reference or look through Root objects.
                
                // Better approach: Look into MainLayouts instance if it exists
                var layouts = UnityEngine.Object.FindObjectOfType<MainLayouts>();
                if (layouts != null && layouts.buttonsHolderHolder != null)
                {
                    var found = layouts.buttonsHolderHolder.Find("SettlementButton");
                    if (found != null) found.gameObject.SetActive(enabled);
                }
            }

            // 2. Modal
            var modal = GameObject.Find("SettlementModal");
             if (modal != null)
            {
                 // If we disable the modal entirely, the SettlementMod might lose reference or re-create it?
                 // SettlementPlugin logic: checks if SettlementModalObj is null, if so errors?
                 // If we just SetActive(false), it mimics closing it.
                 // But if we want to "Disable" the mod, we should probably hide the button. 
                 // Hiding the modal is default state anyway.
                 // We don't need to force hide the modal unless it's open.
                 // But if the mod is "Disabled", the modal should definitively be hidden/inactive.
                 if (!enabled) modal.SetActive(false);
            }
            else
            {
                 // Try finding inactive via parent
                 var layouts = UnityEngine.Object.FindObjectOfType<MainLayouts>();
                 if (layouts != null && layouts.mainHolder != null)
                 {
                     var found = layouts.mainHolder.Find("SettlementModal");
                     if (found != null && !enabled) found.gameObject.SetActive(false);
                 }
            }
        }

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts_InitCommonAnchs_Integration
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                // Run after SettlementMod's patch (which is also Postfix). 
                // Harmony ordering isn't guaranteed without priority, but typically load order applies.
                // We'll trust that running this will check the state.
                ApplyState();
            }
        }
    }
}
