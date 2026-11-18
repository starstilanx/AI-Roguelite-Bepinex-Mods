using System;
using System.Runtime.CompilerServices;

namespace AIROG_Reverie
{
    /// <summary>
    /// The per-save on/off switch for this mod, as set in the GenContext "Mods" checklist.
    ///
    /// GenContext is a SOFT dependency, so nothing outside this class and the provider names a
    /// GenContext type, every method that does is NoInlining (the type is resolved when THAT
    /// method is JIT-compiled, so the failure surfaces at the guarded call, not in the caller),
    /// and any failure means "enabled".
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _resolved, _available, _hooked;

        /// <summary>False only when the player switched Reverie off for the current save.</summary>
        public static bool Enabled
        {
            get
            {
                if (!Resolve()) return true;
                try { return Query(); }
                catch { _available = false; return true; }
            }
        }

        /// <summary>
        /// Calls <paramref name="onFlipped"/> when the player flips this mod's switch during play.
        /// Deliberately NOT hooked to "another save's switches were loaded": that notification
        /// arrives mid-load, while the in-memory dream state still belongs to the previous save.
        /// </summary>
        public static void HookFlipped(Action<bool> onFlipped)
        {
            if (_hooked || onFlipped == null || !Resolve()) return;
            try { Subscribe(onFlipped); _hooked = true; }
            catch { _available = false; }
        }

        private static bool Resolve()
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _available = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.airog.gencontext"); }
                catch { _available = false; }
            }
            return _available;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Query()
            => AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.Reverie);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe(Action<bool> onFlipped)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.Reverie) onFlipped(enabled);
            };
        }
    }
}
