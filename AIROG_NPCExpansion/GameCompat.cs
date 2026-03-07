using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_NPCExpansion
{
    /// <summary>
    /// Compatibility wrappers for game methods whose signatures differ between
    /// stable and alpha builds. All methods use runtime reflection so the mod
    /// compiles once and works on both branches.
    /// </summary>
    internal static class GameCompat
    {
        // ── GameLogView.LogText ───────────────────────────────────────────────────
        // Alpha:  Task<GameLogViewObj> LogText(string s, bool precedingNewline = true)
        // Stable: signature differs — may lack the bool param or have a different return

        private static MethodInfo _logTextStrBool;
        private static MethodInfo _logTextStr;
        private static MethodInfo _queueLogText;
        private static bool _logTextResolved;

        private static void ResolveLogText()
        {
            if (_logTextResolved) return;
            _logTextResolved = true;
            _logTextStrBool = typeof(GameLogView).GetMethod("LogText", new Type[] { typeof(string), typeof(bool) });
            _logTextStr     = typeof(GameLogView).GetMethod("LogText", new Type[] { typeof(string) });
            _queueLogText   = typeof(GameLogView).GetMethod("QueueLogText", new Type[] { typeof(string) });
        }

        /// <summary>Extension-style helper so call sites only need to rename the method.</summary>
        public static Task LogTextCompat(this GameLogView view, string text, bool precedingNewline = true)
        {
            ResolveLogText();
            try
            {
                if (_logTextStrBool != null)
                    return (Task)_logTextStrBool.Invoke(view, new object[] { text, precedingNewline });
                if (_logTextStr != null)
                    return (Task)_logTextStr.Invoke(view, new object[] { text });
                if (_queueLogText != null)
                {
                    _queueLogText.Invoke(view, new object[] { text });
                    return Task.CompletedTask;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GameCompat] LogTextCompat failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        // ── AIAsker.GenerateTxtNoTryStrStyle ─────────────────────────────────────
        // Alpha:  Task<string> GenerateTxtNoTryStrStyle(14 params, many optional)
        // Stable: same method name with fewer params
        // Strategy: find any overload whose first 3 required params match, fill the
        //           rest with their declared default values.

        private static MethodInfo _generateTxt;
        private static bool _generateTxtResolved;

        private static void ResolveGenerateTxt()
        {
            if (_generateTxtResolved) return;
            _generateTxtResolved = true;
            foreach (var m in typeof(AIAsker).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GenerateTxtNoTryStrStyle") continue;
                var ps = m.GetParameters();
                if (ps.Length >= 3
                    && ps[0].ParameterType == typeof(AIAsker.ChatGptPromptType)
                    && ps[1].ParameterType == typeof(string)
                    && ps[2].ParameterType == typeof(AIAsker.ChatGptPostprocessingType))
                {
                    _generateTxt = m;
                    break;
                }
            }
            if (_generateTxt == null)
                Debug.LogError("[GameCompat] Could not find GenerateTxtNoTryStrStyle.");
        }

        public static Task<string> GenerateTxt(
            AIAsker.ChatGptPromptType promptType,
            string prompt,
            AIAsker.ChatGptPostprocessingType postprocessingType = AIAsker.ChatGptPostprocessingType.NONE)
        {
            ResolveGenerateTxt();
            if (_generateTxt == null)
                return Task.FromResult<string>(null);

            var ps = _generateTxt.GetParameters();
            var args = new object[ps.Length];
            args[0] = promptType;
            args[1] = prompt;
            args[2] = postprocessingType;
            for (int i = 3; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : GetDefault(ps[i].ParameterType);

            return (Task<string>)_generateTxt.Invoke(null, args);
        }

        private static object GetDefault(Type t) =>
            t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}
