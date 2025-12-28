using HarmonyLib;
using UnityEngine;
using System;

namespace AIROG_HistoryTab
{
    public static class ConsoleLogFix
    {
        private const int MAX_LOG_LENGTH = 1000;

        public static void Patch(Harmony harmony)
        {
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

                Debug.Log("[ConsoleLogFix] Successfully applied all truncation and crash-prevention patches.");
            }
            catch (Exception e)
            {
                Debug.LogError("[ConsoleLogFix] Failed to apply patches: " + e);
            }
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
                message = s.Substring(0, MAX_LOG_LENGTH) + "... [MOD TRUNCATED]";
            }
        }
    }
}
