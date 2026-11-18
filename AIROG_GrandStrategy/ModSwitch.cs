using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AIROG_GrandStrategy
{
    /// <summary>
    /// The per-save master switch from GenContext's "Mods" checklist ("Grand Strategy"). The
    /// dominion layer lives entirely on WorldExpansion's simulation, so it is also off whenever
    /// "World State" is off. GenContext is a soft dependency: every touch of its types sits in
    /// its own non-inlined method, so a missing/old AIROG_GenContext.dll surfaces as a catchable
    /// exception at the call site - and "missing" reads as switched on.
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
                    Debug.LogWarning($"[GrandStrategy] GenContext mod switches unavailable ({e.GetType().Name}) - running as enabled.");
                    return true;
                }
            }
        }

        /// <summary>
        /// Calls <paramref name="onSwitchMayHaveChanged"/> when either switch is flipped or a
        /// different save's switches are loaded. The callback must only ever touch UI, never
        /// load, simulate or save: nothing guarantees GrandStrategyData already holds the new
        /// save's dominion when it runs.
        /// </summary>
        public static void Hook(Action onSwitchMayHaveChanged)
        {
            if (_unavailable) return;
            try { Subscribe(onSwitchMayHaveChanged); }
            catch (Exception e)
            {
                _unavailable = true;
                Debug.LogWarning($"[GrandStrategy] GenContext mod switches unavailable ({e.GetType().Name}) - running as enabled.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Query() =>
            AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.GrandStrategy)
            && AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.WorldExpansion);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe(Action callback)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.GrandStrategy
                    || key == AIROG_GenContext.ModToggles.Keys.WorldExpansion) callback();
            };
            AIROG_GenContext.ModToggles.Reloaded += callback;
        }
    }
}
