using System;
using System.Runtime.CompilerServices;

namespace AIROG_Settlement
{
    /// <summary>
    /// The per-save master switch from GenContext's "Mods" checklist ("Settlement Info").
    /// GenContext is a soft dependency (this mod also ships on its own): every touch of its types
    /// sits in its own non-inlined method, so a missing/old AIROG_GenContext.dll surfaces as a
    /// catchable exception at the call site - and "missing" reads as switched on.
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _unavailable;

        public static bool Enabled
        {
            get
            {
                if (_unavailable) return true;
                try { return Query(); }
                catch (Exception e)
                {
                    _unavailable = true;
                    SettlementPlugin.Log?.LogWarning($"GenContext mod switches unavailable ({e.GetType().Name}) - running as enabled.");
                    return true;
                }
            }
        }

        /// <summary>
        /// Calls <paramref name="onSwitchMayHaveChanged"/> when this mod's switch is flipped or a
        /// different save's switches are loaded. The callback must only ever touch UI, never
        /// load, simulate or save: nothing guarantees the plugin already holds the new save's
        /// settlement when it runs.
        /// </summary>
        public static void Hook(Action onSwitchMayHaveChanged)
        {
            if (_unavailable) return;
            try { Subscribe(onSwitchMayHaveChanged); }
            catch (Exception e)
            {
                _unavailable = true;
                SettlementPlugin.Log?.LogWarning($"GenContext mod switches unavailable ({e.GetType().Name}) - running as enabled.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Query() =>
            AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.Settlement);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe(Action callback)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.Settlement) callback();
            };
            AIROG_GenContext.ModToggles.Reloaded += callback;
        }
    }
}
