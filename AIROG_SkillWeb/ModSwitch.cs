using System;
using System.Runtime.CompilerServices;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// The per-save on/off switch for this mod, as set in the GenContext "Mods" checklist.
    ///
    /// GenContext is a SOFT dependency: SkillWeb must load and run when AIROG_GenContext.dll is
    /// absent (or is an older build without ModToggles). So nothing outside this class names a
    /// GenContext type, every method that does is NoInlining (the type is resolved when THAT
    /// method is JIT-compiled, so the failure surfaces at the guarded call, not in the caller),
    /// and any failure means "enabled".
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _resolved, _available, _hooked;

        /// <summary>False only when the player switched Skill Web off for the current save.</summary>
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
        /// <paramref name="onFlipped"/> runs when the player flips this mod's switch mid-session.
        /// <paramref name="onSaveSwitched"/> runs when a different save's switches have just been
        /// loaded - which can happen in the middle of that save's load, before this mod has read
        /// its own file for it, so it must only touch UI, never mod data.
        /// Safe to call repeatedly; subscribes once.
        /// </summary>
        public static void Hook(Action onFlipped, Action onSaveSwitched)
        {
            if (_hooked || onFlipped == null || onSaveSwitched == null || !Resolve()) return;
            try { Subscribe(onFlipped, onSaveSwitched); _hooked = true; }
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
            => AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.SkillWeb);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Subscribe(Action onFlipped, Action onSaveSwitched)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.SkillWeb) onFlipped();
            };
            AIROG_GenContext.ModToggles.Reloaded += onSaveSwitched;
        }
    }
}
