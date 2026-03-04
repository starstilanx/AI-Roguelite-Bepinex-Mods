using HarmonyLib;
using UnityEngine;
using System;

namespace AIROG_HistoryTab
{
    public static class ConsoleLogFix
    {
        private const int MAX_LOG_LENGTH = 1000;

        public static void Patch(Harmony harmony, bool enable)
        {
            if (!enable)
            {
                Debug.Log("[ConsoleLogFix] Log truncation disabled by config. Skipping patches.");
                return;
            }
            try
            {
                // 1. Aggressively truncate all log calls
                var loggerLogMethod = AccessTools.Method(typeof(UnityEngine.Logger), "Log", new Type[] { typeof(LogType), typeof(object) });
                if (loggerLogMethod != null) harmony.Patch(original: loggerLogMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_LoggerLog))));

                var loggerLogCtxMethod = AccessTools.Method(typeof(UnityEngine.Logger), "Log", new Type[] { typeof(LogType), typeof(object), typeof(UnityEngine.Object) });
                if (loggerLogCtxMethod != null) harmony.Patch(original: loggerLogCtxMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_LoggerLog))));

                var logMethod = AccessTools.Method(typeof(Debug), "Log", new Type[] { typeof(object) });
                if (logMethod != null) harmony.Patch(original: logMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_Log))));

                var logErrorMethod = AccessTools.Method(typeof(Debug), "LogError", new Type[] { typeof(object) });
                if (logErrorMethod != null) harmony.Patch(original: logErrorMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_Log))));

                var logWarningMethod = AccessTools.Method(typeof(Debug), "LogWarning", new Type[] { typeof(object) });
                if (logWarningMethod != null) harmony.Patch(original: logWarningMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleLogFix), nameof(Prefix_Log))));

                // 2. Wrap the BepInEx Console Log Listener in a try-catch to prevent crash propagation
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

                // 3. Patch Utils truncation methods to be surrogate-aware
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

        public static void Prefix_Log(ref object message) => Truncate(ref message);

        public static void Prefix_LoggerLog(LogType logType, ref object message) => Truncate(ref message);

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

        private static void Truncate(ref object message)
        {
            if (message is string s && s.Length > MAX_LOG_LENGTH)
            {
                int len = MAX_LOG_LENGTH;
                // If we are at the end of a high surrogate, back up one to avoid splitting a pair
                if (len > 0 && char.IsHighSurrogate(s[len - 1]))
                {
                    len--;
                }
                message = s.Substring(0, len) + "... [MOD TRUNCATED]";
            }
        }
    }
}
