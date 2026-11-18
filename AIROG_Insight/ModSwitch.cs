using System.Runtime.CompilerServices;

namespace AIROG_Insight
{
    /// <summary>
    /// The per-save on/off switch for this mod, as set in the GenContext "Mods" checklist.
    ///
    /// Insight must keep working when AIROG_GenContext.dll is absent (or is an older build without
    /// ModToggles). So nothing outside this class names a GenContext type, the method that does is
    /// NoInlining (the type is resolved when THAT method is JIT-compiled, so the failure surfaces
    /// at the guarded call, not in the caller), and any failure means "enabled".
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _resolved, _available;

        /// <summary>False only when the player switched Insight off for the current save.</summary>
        public static bool Enabled
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    try { _available = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.airog.gencontext"); }
                    catch { _available = false; }
                }
                if (!_available) return true;
                try { return Query(); }
                catch { _available = false; return true; }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Query()
            => AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.Insight);
    }
}
