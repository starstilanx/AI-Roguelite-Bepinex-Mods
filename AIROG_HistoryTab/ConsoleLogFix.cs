using HarmonyLib;
using UnityEngine;
using System;

namespace AIROG_HistoryTab
{
    public static class ConsoleLogFix
    {
        public static void Patch(Harmony harmony, bool enable)
        {
            if (!enable)
            {
                Debug.Log("[ConsoleLogFix] Log truncation disabled by config. Skipping patches.");
                return;
            }
            try
            {
                // 1. Wrap the BepInEx Console Log Listener in a try-catch to prevent crash propagation
                // (Note: we do NOT patch Debug.Log globally — that truncates the in-game console log.
                //  The finalizer below is sufficient to swallow encoding crashes on the BepInEx side.)
                var consoleListenerType = AccessTools.TypeByName("BepInEx.Logging.ConsoleLogListener");
                if (consoleListenerType != null)
                {
                    var logEventMethod = AccessTools.Method(consoleListenerType, "LogEvent");
                    if (logEventMethod != null)
                    {
                        harmony.Patch(original: logEventMethod, finalizer: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Finalizer_LogEvent))));
                        Debug.Log("[ConsoleLogFix] Successfully patched BepInEx.Logging.ConsoleLogListener.LogEvent with a Finalizer.");
                    }
                }

                // 2. Patch Utils truncation methods to be surrogate-aware
                var truncateBeginningMethod = AccessTools.Method(typeof(Utils), nameof(Utils.TruncateBeginning));
                if (truncateBeginningMethod != null) harmony.Patch(original: truncateBeginningMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_TruncateBeginning))));

                var simplerTruncateMethod = AccessTools.Method(typeof(Utils), nameof(Utils.SimplerTruncateWithPreferenceForEnders));
                if (simplerTruncateMethod != null) harmony.Patch(original: simplerTruncateMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_SimplerTruncate))));

                Debug.Log("[ConsoleLogFix] Successfully applied all truncation and crash-prevention patches.");
            }
            catch (Exception e)
            {
                Debug.LogError("[ConsoleLogFix] Failed to apply patches: " + e);
            }
        }

        public static bool Prefix_TruncateBeginning(string str, int maxLength, ref string __result)
        {
            if (str == null || str.Length <= maxLength)
            {
                __result = str;
                return false; // Skip original
            }
            // UnityEngine.Debug.Log("truncateBeginning triggered"); // Omitted to avoid spam or recursion if patched
            int start = str.Length - (int)((float)maxLength * 0.9f);
            // If we are at a low surrogate, move forward to the next full character to avoid split character
            if (start < str.Length && char.IsLowSurrogate(str[start]))
            {
                start++;
            }
            __result = str.Substring(start);
            return false; // Skip original
        }

        public static bool Prefix_SimplerTruncate(string str, int maxChars, ref string __result)
        {
            if (str == null || maxChars >= str.Length)
            {
                __result = str;
                return false;
            }
            int num = maxChars - 1;
            // Handle split surrogate at truncation point
            if (num > 0 && char.IsHighSurrogate(str[num]))
            {
                num--;
            }

            int startNum = num;
            while (num > 0 && !AIAsker.SENTENCE_ENDERS_FOR_SIMPLE_TRUNCATE.Contains(str[num]))
            {
                num--;
            }
            if (num == 0)
            {
                num = startNum;
                while (num > 0 && str[num] != ' ')
                {
                    num--;
                }
                if (num == 0)
                {
                    int finalLen = maxChars;
                    if (finalLen > 0 && char.IsHighSurrogate(str[finalLen - 1])) finalLen--;
                    __result = str.Substring(0, finalLen) + "...";
                    return false;
                }
                __result = str.Substring(0, num) + "...";
                return false;
            }
            __result = str.Substring(0, num + 1);
            return false;
        }

        // Finalizer can catch exceptions and suppress them by returning null
        public static Exception Finalizer_LogEvent(Exception __exception)
        {
            if (__exception != null)
            {
                // We cannot use Debug.Log here as it would cause infinite recursion!
                return null; // Swallow the exception
            }
            return null;
        }

    }
}
