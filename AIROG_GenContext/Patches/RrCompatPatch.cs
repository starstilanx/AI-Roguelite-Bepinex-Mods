using System;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace AIROG_GenContext.Patches
{
    /// <summary>
    /// Reactive Realms Compatibility: when the RR workshop preset is active it
    /// replaces the UNIFIED preamble with a story-only preamble, causing the AI
    /// to return plain narrative text instead of the JSON the game expects.
    /// This patch catches the resulting AiOutputException and wraps the plain
    /// text in a minimal {"story":"..."} JSON so the game can continue normally.
    /// Enable via the "RR Compat Mode" toggle in the GenContext Mod Manager.
    /// </summary>
    [HarmonyPatch(typeof(Utils), nameof(Utils.GetAiJsonSubstr))]
    public static class Patch_GetAiJsonSubstr
    {
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception, ref string __result, string s)
        {
            if (__exception is AiOutputException && ContextManager.GetGlobalSetting("RRCompat"))
            {
                // Serialize the raw string so all special chars are properly escaped
                string storyJson = JsonConvert.SerializeObject(s);
                __result = $"{{\"story\":{storyJson}}}";
                Debug.Log("[GenContext] RR Compat: wrapped plain-text AI response as UNIFIED JSON story.");
                return null; // suppress the exception
            }
            return __exception;
        }
    }
}
