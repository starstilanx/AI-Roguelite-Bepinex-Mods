using System;
using System.Runtime.CompilerServices;

namespace AIROG_Chronicle
{
    /// <summary>
    /// The per-save on/off switch for this mod, as set in the GenContext "Mods" checklist.
    ///
    /// GenContext is a SOFT dependency: Chronicle must load and run when AIROG_GenContext.dll is
    /// absent (or is an older build without ModToggles). So nothing outside this class and the
    /// provider names a GenContext type, every method that does is NoInlining (the type is
    /// resolved when THAT method is JIT-compiled, so the failure surfaces at the guarded call,
    /// not in the caller), and any failure means "enabled".
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _resolved, _available, _hooked;

        /// <summary>False only when the player switched Chronicle off for the current save.</summary>
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
        /// Calls <paramref name="onChange"/> whenever this mod's switch flips or another save's
        /// switches are loaded. Safe to call repeatedly; subscribes once.
        /// The callback must only touch UI: the "another save loaded" notification arrives in the
        /// middle of that save's load, while this mod's in-memory state still belongs to the
        /// previous save.
        /// </summary>
        public static void Hook(Action onChange)
        {
            if (_hooked || onChange == null || !Resolve()) return;
            try { Subscribe(onChange); _hooked = true; }
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
            => AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.Chronicle);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe(Action onChange)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.Chronicle) onChange();
            };
            AIROG_GenContext.ModToggles.Reloaded += onChange;
        }
    }
}
