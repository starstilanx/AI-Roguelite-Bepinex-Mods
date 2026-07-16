using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace AIROG_Core
{
    /// <summary>
    /// Base class for AIROG BepInEx plugins. Provides a shared Harmony instance and
    /// SafePatch/SafeRun helpers so that one broken hook (e.g. a game update renaming
    /// or changing the signature of a patched method) cannot take down unrelated
    /// features registered in the same plugin's Awake().
    /// </summary>
    public abstract class BaseModPlugin : BaseUnityPlugin
    {
        protected Harmony HarmonyInstance { get; private set; }

        protected virtual void Awake()
        {
            HarmonyInstance = new Harmony(Info.Metadata.GUID);
        }

        /// <summary>
        /// Resolves and patches an instance/static method by name. Logs and returns false
        /// instead of throwing if the method can't be found or the patch fails to apply.
        /// </summary>
        protected bool SafePatch(Type targetType, string methodName, HarmonyMethod prefix = null, HarmonyMethod postfix = null, Type[] argTypes = null)
        {
            try
            {
                MethodBase method = argTypes != null
                    ? AccessTools.Method(targetType, methodName, argTypes)
                    : AccessTools.Method(targetType, methodName);

                if (method == null)
                {
                    Logger.LogWarning($"[SafePatch] {targetType.Name}.{methodName} not found — skipping patch.");
                    return false;
                }

                HarmonyInstance.Patch(method, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SafePatch] Failed to patch {targetType.Name}.{methodName}: {ex}");
                return false;
            }
        }

        /// <summary>Same as SafePatch, but for constructors (AccessTools.Method can't resolve those).</summary>
        protected bool SafePatchCtor(Type targetType, Type[] paramTypes, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            try
            {
                ConstructorInfo ctor = AccessTools.Constructor(targetType, paramTypes);
                if (ctor == null)
                {
                    Logger.LogWarning($"[SafePatch] {targetType.Name} constructor({string.Join(", ", Array.ConvertAll(paramTypes, t => t.Name))}) not found — skipping patch.");
                    return false;
                }

                HarmonyInstance.Patch(ctor, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SafePatch] Failed to patch {targetType.Name} constructor: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Runs a block of setup code (e.g. an unrelated feature bundled into this plugin)
        /// in isolation, so a failure in it can't prevent sibling blocks from running.
        /// </summary>
        protected bool SafeRun(string label, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SafeRun] '{label}' failed: {ex}");
                return false;
            }
        }
    }
}
