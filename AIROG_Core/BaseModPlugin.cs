using System;
using System.Linq;
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
            // Worked out before the try. The type can be null (a TypeByName lookup that stopped
            // resolving after a game update), and reading targetType.Name inside the catch then
            // threw a second time - out of the method that exists to not throw.
            string target = TypeLabel(targetType) + "." + methodName;
            try
            {
                if (targetType == null)
                {
                    Logger.LogWarning($"[SafePatch] {target}: the target type was not found — skipping patch.");
                    return false;
                }

                MethodBase method = null;
                bool ambiguous = false;
                try
                {
                    method = argTypes != null
                        ? AccessTools.Method(targetType, methodName, argTypes)
                        : AccessTools.Method(targetType, methodName);
                }
                catch (AmbiguousMatchException)
                {
                    ambiguous = true;
                }

                // Only a lookup by bare name can be ambiguous.
                int overloads = argTypes == null ? CountOverloads(targetType, methodName) : 1;

                if (method == null)
                {
                    if (ambiguous || overloads > 1)
                        Logger.LogError($"[SafePatch] {target} is overloaded and no argTypes were given to choose between {DescribeOverloads(targetType, methodName)} — patch skipped. Pass argTypes for the overload you mean.");
                    else
                        Logger.LogWarning($"[SafePatch] {target} not found — skipping patch.");
                    return false;
                }

                // Harmony does not always refuse an overloaded name: when one of the overloads
                // takes no arguments it quietly settles on that one. Say which, in case it is
                // not the one that was meant.
                if (overloads > 1)
                    Logger.LogWarning($"[SafePatch] {target} is overloaded ({DescribeOverloads(targetType, methodName)}) and no argTypes were given; patching {DescribeParameters(method)}. Pass argTypes to make the choice explicit.");

                HarmonyInstance.Patch(method, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SafePatch] Failed to patch {target}: {ex}");
                return false;
            }
        }

        /// <summary>Same as SafePatch, but for constructors (AccessTools.Method can't resolve those).</summary>
        protected bool SafePatchCtor(Type targetType, Type[] paramTypes, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            string target = TypeLabel(targetType) + " constructor";
            try
            {
                if (targetType == null)
                {
                    Logger.LogWarning($"[SafePatch] {target}: the target type was not found — skipping patch.");
                    return false;
                }

                ConstructorInfo ctor = AccessTools.Constructor(targetType, paramTypes);
                if (ctor == null)
                {
                    Logger.LogWarning($"[SafePatch] {target}({string.Join(", ", Array.ConvertAll(paramTypes ?? Type.EmptyTypes, t => t.Name))}) not found — skipping patch.");
                    return false;
                }

                HarmonyInstance.Patch(ctor, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[SafePatch] Failed to patch {target}: {ex}");
                return false;
            }
        }

        private static string TypeLabel(Type type) => type != null ? type.Name : "<missing type>";

        private static int CountOverloads(Type type, string methodName)
            => type.GetMethods(AccessTools.all).Count(m => m.Name == methodName);

        private static string DescribeParameters(MethodBase method)
            => "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";

        private static string DescribeOverloads(Type type, string methodName)
            => string.Join(" / ", type.GetMethods(AccessTools.all).Where(m => m.Name == methodName).Select(DescribeParameters));

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
