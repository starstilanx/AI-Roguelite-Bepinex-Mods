// PatchAudit - headless Harmony patch audit for the AIROG mods.
//
// A clean `dotnet build` after a game update only proves the mods still COMPILE. Harmony targets
// are mostly strings ("GameplayManager", "DoConvoTextFieldSubmission"), patch parameters bind by
// NAME (a renamed game parameter silently stops binding), and `___privateField` injections bind to
// field names. None of that is checked by the compiler. Under HarmonyX a patch that fails to bind
// is not even an exception: it is one error line in BepInEx\LogOutput.log and the patch is simply
// absent in-game. This tool runs Harmony's own binding step outside the game and reports it.
//
// What it does, per mod DLL:
//   1. ATTRIBUTE PATCHES  - every type carrying Harmony attributes is expanded into
//      (original, patch) jobs with Harmony's own PatchClassProcessor, then bound (see BIND).
//   2. MANUAL PATCHES     - method bodies are scanned with Cecil and a small symbolic stack
//      machine, recovering `harmony.Patch(AccessTools.Method(typeof(T), "M"), prefix: ...)` and
//      `SafePatch(typeof(T), "M", ...)` call sites, which are then bound the same way.
//   3. REFLECTION TARGETS - AccessTools.Field/Property/Method and Type.GetMethod/GetField/
//      GetProperty calls with a constant type + name are resolved against the loaded assemblies.
//   4. ASYNC SKIP REVIEW  - bool-returning prefixes on Task-returning originals are listed: a
//      prefix that returns false there without assigning __result hands the caller a null Task
//      (the game's HarmonyFaultDetector names exactly this failure).
//   5. UNREGISTERED TYPES - annotated types that no PatchAll/CreateAndPatchAll call names.
//
// BIND = (a) a static check of every patch parameter against the original (name exists, type is
// assignable, ___field exists) and (b) HarmonyManipulator.Manipulate run against a Cecil copy of
// the original, with Harmony's error log captured. The patched method is deliberately NOT compiled
// or detoured: the desktop CLR refuses to JIT anything that calls Unity's InternalCall methods
// ("ECall methods must be packaged into a system module"), so that last step can only run in-game.
//
// What it does NOT prove: that a patch still does the right thing. It only proves it still binds.
//
// Usage: PatchAudit.exe <pluginsDir> [--libs <AIRL\libs>] [--core <BepInEx\core>] [--probe <dir>]... [--verbose]
// Exit code = number of hard failures (0 = everything bound).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace PatchAudit
{
    internal static class Program
    {
        static readonly List<string> ProbeDirs = new List<string>();
        static readonly Dictionary<string, Assembly> Resolved = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        static bool _verbose;
        static int _failures, _warnings, _bound, _reflectionOk;

        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: PatchAudit <pluginsDir> [--libs <dir>] [--core <dir>] [--verbose]");
                return -1;
            }
            string plugins = Path.GetFullPath(args[0]);
            string libs = null, core = null;
            var extraProbe = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--libs" && i + 1 < args.Length) libs = Path.GetFullPath(args[++i]);
                else if (args[i] == "--core" && i + 1 < args.Length) core = Path.GetFullPath(args[++i]);
                else if (args[i] == "--probe" && i + 1 < args.Length) extraProbe.Add(Path.GetFullPath(args[++i]));
                else if (args[i] == "--verbose") _verbose = true;
            }
            if (core != null) ProbeDirs.Add(core);
            if (libs != null) ProbeDirs.Add(libs);
            ProbeDirs.Add(plugins);
            // Assemblies the audited mods depend on but that are not themselves being audited
            // (auditing only SkillWeb still needs AIROG_GenContext.dll to load its types).
            ProbeDirs.AddRange(extraProbe);
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            return Run(plugins);
        }

        static Assembly OnResolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (Resolved.TryGetValue(name, out var hit)) return hit;
            foreach (var dir in ProbeDirs)
            {
                string path = Path.Combine(dir, name + ".dll");
                if (!File.Exists(path)) continue;
                try
                {
                    var asm = Assembly.LoadFrom(path);
                    Resolved[name] = asm;
                    return asm;
                }
                catch { }
            }
            Resolved[name] = null;
            return null;
        }

        // ---- Harmony internals (pinned to the HarmonyX build the game ships) ------------------
        static FieldInfo F_patchMethods, F_apInfo, F_apType;
        static MethodInfo M_getBulkMethods, M_getOriginalMethod, M_manipulate;
        static readonly List<string> HarmonyLog = new List<string>();

        // Kept out of Main so the JIT does not need 0Harmony before the resolver is installed.
        static int Run(string plugins)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var harmonyAsm = typeof(Harmony).Assembly;
            var procType = typeof(PatchClassProcessor);
            var apType = harmonyAsm.GetType("HarmonyLib.AttributePatch");
            F_patchMethods = procType.GetField("patchMethods", any);
            M_getBulkMethods = procType.GetMethod("GetBulkMethods", any);
            F_apInfo = apType?.GetField("info", any);
            F_apType = apType?.GetField("type", any);
            M_getOriginalMethod = harmonyAsm.GetType("HarmonyLib.PatchTools")?.GetMethod("GetOriginalMethod", any);
            M_manipulate = harmonyAsm.GetType("HarmonyLib.Public.Patching.HarmonyManipulator")?
                .GetMethods(any).FirstOrDefault(m => m.Name == "Manipulate" && m.GetParameters().Length == 3);
            if (F_patchMethods == null || M_getBulkMethods == null || F_apInfo == null || F_apType == null || M_getOriginalMethod == null || M_manipulate == null)
            {
                Console.WriteLine("PatchAudit: this HarmonyX build does not have the internals the audit binds to " +
                                  "(PatchClassProcessor.patchMethods / GetBulkMethods, AttributePatch.info / type, " +
                                  "PatchTools.GetOriginalMethod, HarmonyManipulator.Manipulate). Update Program.cs.");
                return -2;
            }

            HarmonyLib.Tools.Logger.ChannelFilter = HarmonyLib.Tools.Logger.LogChannel.Warn | HarmonyLib.Tools.Logger.LogChannel.Error;
            HarmonyLib.Tools.Logger.MessageReceived += (s, e) => HarmonyLog.Add(e.LogChannel + ": " + e.Message);

            var dlls = Directory.GetFiles(plugins, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine($"PatchAudit: {dlls.Count} assemblies in {plugins}");
            Console.WriteLine();

            // Load everything first so cross-mod references (GenContext, Core) resolve to these builds.
            var loaded = new List<Assembly>();
            foreach (var dll in dlls)
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    Resolved[asm.GetName().Name] = asm;
                    loaded.Add(asm);
                }
                catch (Exception ex) { Fail("LOAD", Path.GetFileName(dll) + ": " + ex.Message); }
            }

            foreach (var asm in loaded) AuditAssembly(asm);

            if (AsyncReview.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== ASYNC SKIP REVIEW (bool prefixes on Task-returning game methods)");
                foreach (var l in AsyncReview.OrderBy(x => x)) Console.WriteLine(l);
            }

            Console.WriteLine();
            Console.WriteLine($"SUMMARY: {_bound} patches bound, {_reflectionOk} reflection targets resolved, {_failures} FAILURES, {_warnings} warnings.");
            return _failures;
        }

        static void AuditAssembly(Assembly asm)
        {
            string mod = asm.GetName().Name;
            Console.WriteLine($"=== {mod} {asm.GetName().Version}");

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                foreach (var msg in ex.LoaderExceptions.Select(l => l.Message).Distinct())
                    Fail("TYPELOAD", msg);
            }

            var harmony = new Harmony("audit." + mod);
            var annotated = new List<Type>();

            // ---- 1. attribute patches -------------------------------------------------------
            foreach (var type in types)
            {
                if (!HasHarmonyAttributes(type)) continue;
                annotated.Add(type);
                AuditAttributeType(harmony, mod, type);
            }

            // ---- 2 + 3. manual patches + reflection targets (IL scan) -------------------------
            var registered = new HashSet<string>();
            bool assemblyWide = false;
            try
            {
                ScanIl(asm, mod, registered, ref assemblyWide);
            }
            catch (Exception ex)
            {
                Warn(mod, "ILSCAN", "scan aborted: " + ex.Message);
            }

            // ---- 5. annotated types nobody registers ----------------------------------------
            if (!assemblyWide)
            {
                foreach (var t in annotated)
                {
                    if (registered.Contains(t.FullName)) continue;
                    Warn(mod, "UNREGISTERED", $"{t.FullName} has Harmony attributes but no PatchAll/CreateAndPatchAll names it (and the mod never patches assembly-wide)");
                }
            }
        }

        static bool HasHarmonyAttributes(Type type)
        {
            try
            {
                if (type.GetCustomAttributes(typeof(HarmonyAttribute), true).Length > 0) return true;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                return type.GetMethods(all).Any(m => m.GetCustomAttributes(typeof(HarmonyAttribute), true).Length > 0);
            }
            catch { return false; }
        }

        // =====================================================================================
        // attribute patches
        // =====================================================================================

        static void AuditAttributeType(Harmony harmony, string mod, Type type)
        {
            object proc;
            IEnumerable patchMethods;
            try
            {
                proc = harmony.CreateClassProcessor(type, true);
                patchMethods = F_patchMethods.GetValue(proc) as IEnumerable;
            }
            catch (Exception ex)
            {
                Fail("ATTR", $"{type.FullName}: {Flatten(ex)}");
                return;
            }
            if (patchMethods == null) return;

            List<MethodBase> bulk = null;
            try { bulk = M_getBulkMethods.Invoke(proc, null) as List<MethodBase>; }
            catch (TargetInvocationException ex)
            {
                // TargetMethod()/TargetMethods() is the mod's own code and may need the live game.
                Warn(mod, "TARGETMETHOD", $"{type.FullName}: TargetMethod(s) could not run off-game ({Flatten(ex.InnerException)}) - check by hand");
                return;
            }

            var jobs = new Dictionary<MethodBase, List<KeyValuePair<string, MethodInfo>>>();
            int seen = 0;
            foreach (var ap in patchMethods)
            {
                var info = F_apInfo.GetValue(ap) as HarmonyMethod;
                string kind = F_apType.GetValue(ap)?.ToString();
                if (info?.method == null || kind == null) continue;
                seen++;

                IEnumerable<MethodBase> originals;
                if (bulk != null && bulk.Count > 0) originals = bulk;
                else
                {
                    MethodBase o;
                    try { o = M_getOriginalMethod.Invoke(null, new object[] { info }) as MethodBase; }
                    catch (TargetInvocationException ex)
                    {
                        Fail("ATTR", $"{type.FullName}.{info.method.Name}: {Flatten(ex.InnerException)}{Hint(info)}");
                        continue;
                    }
                    if (o == null)
                    {
                        Fail("ATTR", $"{type.FullName}.{info.method.Name}: target not found -> {DescribeTarget(info)}{Hint(info)}");
                        continue;
                    }
                    originals = new[] { o };
                }
                foreach (var o in originals)
                {
                    if (!jobs.TryGetValue(o, out var list)) jobs[o] = list = new List<KeyValuePair<string, MethodInfo>>();
                    list.Add(new KeyValuePair<string, MethodInfo>(kind, info.method));
                }
            }
            if (seen == 0) Warn(mod, "NOOP", $"{type.FullName}: carries Harmony attributes but declares no prefix/postfix/transpiler/finalizer");

            foreach (var job in jobs) Bind(mod, "attr", job.Key, job.Value);
        }

        static string DescribeTarget(HarmonyMethod info)
        {
            string args = info.argumentTypes == null ? "" : "(" + string.Join(", ", info.argumentTypes.Select(t => t?.Name ?? "?")) + ")";
            string kind = info.methodType.HasValue && info.methodType.Value != MethodType.Normal ? $" [{info.methodType.Value}]" : "";
            return $"{info.declaringType?.Name ?? "<no type>"}.{info.methodName ?? "<no name>"}{args}{kind}";
        }

        /// <summary>When a target is missing, say what IS there: overloads, or the base type that really declares it.</summary>
        static string Hint(HarmonyMethod info)
        {
            try
            {
                if (info.declaringType == null || info.methodName == null) return "";
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                var same = info.declaringType.GetMethods(all).Where(m => m.Name == info.methodName).ToList();
                if (same.Count == 0) return "   [no method of that name exists on the type any more]";
                var declared = same.Where(m => m.DeclaringType == info.declaringType).ToList();
                if (declared.Count == 0)
                    return $"   [declared on base type {same[0].DeclaringType.Name}: an attribute patch must name the declaring type]";
                return "   [existing overloads: " + string.Join(" | ", declared.Select(m => "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")")) + "]";
            }
            catch { return ""; }
        }

        // =====================================================================================
        // BIND
        // =====================================================================================

        static readonly List<string> AsyncReview = new List<string>();
        static readonly HashSet<string> BoundKeys = new HashSet<string>();

        static readonly HashSet<string> SpecialNames = new HashSet<string>
        {
            "__state", "__args", "__originalMethod", "__runOriginal", "__exception", "__resultRef", "__instructions"
        };

        static void Bind(string mod, string origin, MethodBase original, List<KeyValuePair<string, MethodInfo>> patches)
        {
            string target = $"{original.DeclaringType?.Name}.{original.Name}";
            string key = mod + "|" + original.DeclaringType?.FullName + "|" + original + "|" + string.Join(",", patches.Select(p => p.Key + ":" + p.Value.DeclaringType?.FullName + "." + p.Value.Name));
            if (!BoundKeys.Add(key)) return;

            var problems = new List<string>();

            // (a) static parameter check - clearer messages than Harmony's, and it also catches
            //     type mismatches, which Harmony does not check at all (they become invalid IL).
            foreach (var p in patches)
            {
                if (p.Key == "Transpiler" || p.Key == "ILManipulator" || p.Key == "ReversePatch") continue;
                foreach (var problem in StaticParamCheck(original, p.Value))
                    problems.Add($"{p.Value.DeclaringType?.Name}.{p.Value.Name}: {problem}");

                if (p.Key == "Prefix" && p.Value.ReturnType == typeof(bool) && original is MethodInfo omi && typeof(Task).IsAssignableFrom(omi.ReturnType))
                {
                    bool setsResult = p.Value.GetParameters().Any(x => x.Name == "__result" && x.ParameterType.IsByRef);
                    AsyncReview.Add($"  {(setsResult ? "review" : "DANGER")}  {target} (returns {omi.ReturnType.Name}) <- {p.Value.DeclaringType?.FullName}.{p.Value.Name}" +
                                    (setsResult ? "  [has ref __result - confirm every `return false` path assigns a non-null Task]"
                                                : "  [NO ref __result - any `return false` hands the caller a null Task]"));
                    if (!setsResult) _warnings++;
                }
            }

            // (b) Harmony's own binding, against a Cecil copy of the original.
            try
            {
                var info = new PatchInfo();
                foreach (var p in patches)
                {
                    string add = p.Key == "Prefix" ? "AddPrefixes" : p.Key == "Postfix" ? "AddPostfixes" : p.Key == "Transpiler" ? "AddTranspilers"
                               : p.Key == "Finalizer" ? "AddFinalizers" : p.Key == "ILManipulator" ? "AddILManipulators" : null;
                    if (add == null) continue;
                    typeof(PatchInfo).GetMethod(add, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(info, new object[] { "audit", new[] { new HarmonyMethod(p.Value) } });
                }

                if (original.GetMethodBody() == null)
                {
                    problems.Add("original has no IL body (abstract/extern) - Harmony cannot patch it");
                }
                else
                {
                    HarmonyLog.Clear();
                    // The original's IL is read from disk with Cecil rather than through
                    // DynamicMethodDefinition(MethodBase): that route resolves every token in the
                    // body by reflection, and the desktop CLR's mscorlib lacks members the game's
                    // Mono mscorlib has (CollectionExtensions.GetValueOrDefault, String.Split(char)).
                    using (var module = ModuleDefinition.ReadModule(original.Module.Assembly.Location, CecilParams()))
                    {
                        if (!(module.LookupToken(original.MetadataToken) is MethodDefinition def) || !def.HasBody)
                            problems.Add("could not locate the original's IL on disk");
                        else
                            M_manipulate.Invoke(null, new object[] { original, info, new ILContext(def) });
                    }
                    foreach (var line in HarmonyLog.Where(l => l.StartsWith("Error")))
                    {
                        string first = FirstLine(line);
                        if (!problems.Any(x => first.Contains(x))) problems.Add("Harmony: " + first);
                    }
                }
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                problems.Add("Harmony binding threw: " + Flatten(inner));
            }

            string label = $"{target} <- {string.Join(", ", patches.Select(p => p.Value.DeclaringType?.Name + "." + p.Value.Name + "[" + p.Key + "]"))}";
            NoCallersCheck(mod, original, label);
            if (problems.Count == 0)
            {
                _bound += patches.Count;
                if (_verbose) Console.WriteLine($"    ok   [{origin}] {label}");
            }
            else
            {
                foreach (var problem in problems.Distinct()) Fail("BIND", $"{label}: {problem}");
            }
        }

        // =====================================================================================
        // dead-hook check
        // =====================================================================================
        //
        // A patch can bind perfectly to a method the game no longer calls. That is how
        // NPCExpansion's whole interaction system stayed dead for four months: the 07/28 build
        // stopped calling GameplayManager.ProcessInteractionInfoNoTryStr (it calls
        // ProcessTurnNoTryStr directly) but left the old method in place, so the postfix on it
        // compiled, bound, and never ran. This flags a patched method that nothing in its own
        // assembly references.
        //
        // It cannot see calls Unity makes itself, so it stays quiet for virtual methods, Unity
        // messages, and "On..." handlers on MonoBehaviours (those are wired to UI events in the
        // scene files, not in code). Anything it does report is a warning to check by hand.

        static readonly Dictionary<string, HashSet<string>> CalledByAssembly = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> UnityMessages = new HashSet<string>
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI",
            "OnApplicationQuit", "OnApplicationFocus", "OnApplicationPause", "OnValidate", "Reset",
            "OnTransformParentChanged", "OnRectTransformDimensionsChange", "OnCanvasGroupChanged",
        };

        static HashSet<string> CalledNames(string assemblyPath)
        {
            if (CalledByAssembly.TryGetValue(assemblyPath, out var set)) return set;
            set = new HashSet<string>();
            try
            {
                using (var module = ModuleDefinition.ReadModule(assemblyPath, CecilParams()))
                {
                    foreach (var type in AllTypes(module.Types))
                        foreach (var method in type.Methods)
                        {
                            if (!method.HasBody) continue;
                            string self = method.DeclaringType.FullName + "::" + method.Name;
                            foreach (var ins in method.Body.Instructions)
                            {
                                if (!(ins.Operand is MethodReference mr)) continue;
                                var dt = mr.DeclaringType is GenericInstanceType git ? git.ElementType : mr.DeclaringType;
                                string key = dt.FullName + "::" + mr.Name;
                                if (key != self) set.Add(key); // recursion alone does not keep a method alive
                            }
                        }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  note: could not index calls in {Path.GetFileName(assemblyPath)} ({ex.Message}); dead-hook check skipped for it.");
                set = null;
            }
            CalledByAssembly[assemblyPath] = set;
            return set;
        }

        static bool InspectorBindable(MethodBase m)
        {
            if (!m.IsPublic || !(m is MethodInfo mi) || mi.ReturnType != typeof(void)) return false;
            var ps = m.GetParameters();
            if (ps.Length == 0) return true;
            if (ps.Length > 1) return false;
            var t = ps[0].ParameterType;
            if (t == typeof(int) || t == typeof(float) || t == typeof(string) || t == typeof(bool)) return true;
            for (var b = t; b != null; b = b.BaseType)
                if (b.FullName == "UnityEngine.Object") return true;
            return false;
        }

        static void NoCallersCheck(string mod, MethodBase original, string label)
        {
            try
            {
                if (original.IsConstructor || original.IsVirtual || original.IsAbstract || original.DeclaringType == null) return;
                string asmPath = original.Module.Assembly.Location;
                if (!string.Equals(Path.GetFileNameWithoutExtension(asmPath), "Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return;

                bool isBehaviour = false;
                for (var t = original.DeclaringType; t != null; t = t.BaseType)
                    if (t.FullName == "UnityEngine.MonoBehaviour") { isBehaviour = true; break; }
                if (isBehaviour && (UnityMessages.Contains(original.Name) || original.Name.StartsWith("On", StringComparison.Ordinal))) return;

                var called = CalledNames(asmPath);
                if (called == null) return;
                string key = original.DeclaringType.FullName.Replace('+', '/') + "::" + original.Name;
                if (called.Contains(key)) return;

                // A public void method with no argument (or one int/float/string/bool/UnityEngine.Object)
                // on a MonoBehaviour is exactly what a Button or other UnityEvent can be wired to in
                // the scene, e.g. MainMenu.Options. Having no caller in code is normal for those.
                if (isBehaviour && InspectorBindable(original))
                {
                    if (_verbose) Console.WriteLine($"    note [SCENE-WIRED?] {original.DeclaringType.Name}.{original.Name} has no caller in code (normal for a scene-wired UI handler)");
                    return;
                }

                // An async method's body lives in a state machine; the method itself is still the
                // thing callers reference, so no special case is needed beyond the lookup above.
                Warn(mod, "NOCALLERS", $"{label}: nothing in the game's code calls {original.DeclaringType.Name}.{original.Name} any more - " +
                                       "the patch binds but may never run (unless Unity or a scene-wired UI event invokes it)");
            }
            catch { }
        }

        static ReaderParameters CecilParams()
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (var dir in ProbeDirs) resolver.AddSearchDirectory(dir);
            return new ReaderParameters { ReadingMode = ReadingMode.Deferred, AssemblyResolver = resolver };
        }

        static IEnumerable<string> StaticParamCheck(MethodBase original, MethodInfo patch)
        {
            var origParams = original.GetParameters();
            Type retType = original is MethodInfo mi ? mi.ReturnType : typeof(void);

            foreach (var p in patch.GetParameters())
            {
                string name = p.Name;
                Type pt = p.ParameterType;
                Type elem = pt.IsByRef ? pt.GetElementType() : pt;

                if (name == "__instance")
                {
                    if (original.IsStatic) yield return "__instance requested but the original is static (it will always be null)";
                    else if (!Compatible(elem, original.DeclaringType)) yield return $"__instance is {elem.Name} but the original lives on {original.DeclaringType.Name}";
                    continue;
                }
                if (name == "__result")
                {
                    if (retType == typeof(void)) yield return "__result requested but the original returns void";
                    else if (!Compatible(elem, retType)) yield return $"__result is {Pretty(elem)} but the original returns {Pretty(retType)}";
                    continue;
                }
                if (SpecialNames.Contains(name)) continue;

                if (name.StartsWith("___"))
                {
                    string fname = name.Substring(3);
                    if (int.TryParse(fname, out _)) continue;
                    var field = AccessTools.Field(original.DeclaringType, fname);
                    if (field == null) yield return $"injected field \"{fname}\" no longer exists on {original.DeclaringType.Name}";
                    else if (!Compatible(elem, field.FieldType)) yield return $"injected field \"{fname}\" is {Pretty(field.FieldType)} but the patch declares {Pretty(elem)}";
                    continue;
                }
                if (name.StartsWith("__") && int.TryParse(name.Substring(2), out int idx))
                {
                    if (idx >= origParams.Length) yield return $"positional argument {name} is out of range ({origParams.Length} parameters)";
                    continue;
                }

                var op = origParams.FirstOrDefault(x => x.Name == name);
                if (op == null)
                {
                    yield return $"parameter \"{name}\" does not exist on the original; it has ({string.Join(", ", origParams.Select(x => x.ParameterType.Name + " " + x.Name))})";
                    continue;
                }
                Type oelem = op.ParameterType.IsByRef ? op.ParameterType.GetElementType() : op.ParameterType;
                if (!Compatible(elem, oelem)) yield return $"parameter \"{name}\" is {Pretty(oelem)} on the original but the patch declares {Pretty(elem)}";
            }
        }

        static bool Compatible(Type patchSide, Type originalSide)
        {
            if (patchSide == null || originalSide == null) return true;
            if (patchSide == typeof(object)) return true;
            if (patchSide.IsGenericParameter || originalSide.IsGenericParameter) return true;
            return patchSide.IsAssignableFrom(originalSide);
        }

        static string Pretty(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.Name;
            return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">";
        }

        // =====================================================================================
        // IL scan
        // =====================================================================================

        abstract class Sym { }
        sealed class SUnknown : Sym { public static readonly SUnknown I = new SUnknown(); }
        sealed class SNull : Sym { public static readonly SNull I = new SNull(); }
        sealed class SType : Sym { public TypeReference T; public string ByName; }
        sealed class SStr : Sym { public string S; }
        sealed class SInt : Sym { public int V; }
        sealed class SArr : Sym { public List<Sym> Items = new List<Sym>(); }
        sealed class SMember : Sym { public string Kind; public Sym Type; public string Name; public Sym ArgTypes; public bool Soft; }
        sealed class SHarmony : Sym { public Sym Method; }

        static void ScanIl(Assembly runtimeAsm, string mod, HashSet<string> registered, ref bool assemblyWide)
        {
            using (var module = ModuleDefinition.ReadModule(runtimeAsm.Location))
            {
                foreach (var type in AllTypes(module.Types))
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        ScanMethod(method, mod, registered, ref assemblyWide);
                    }
            }
        }

        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
        {
            foreach (var t in roots)
            {
                yield return t;
                foreach (var n in AllTypes(t.NestedTypes)) yield return n;
            }
        }

        static void ScanMethod(MethodDefinition method, string mod, HashSet<string> registered, ref bool assemblyWide)
        {
            var body = method.Body;
            var targets = new HashSet<Instruction>();
            foreach (var ins in body.Instructions)
            {
                if (ins.Operand is Instruction one) targets.Add(one);
                else if (ins.Operand is Instruction[] many) foreach (var t in many) targets.Add(t);
            }
            foreach (var h in body.ExceptionHandlers)
            {
                if (h.HandlerStart != null) targets.Add(h.HandlerStart);
                if (h.FilterStart != null) targets.Add(h.FilterStart);
            }

            var stack = new List<Sym>();
            var locals = new Dictionary<int, Sym>();
            string where = method.DeclaringType.FullName.Replace('/', '+') + "." + method.Name;

            Sym Pop() { if (stack.Count == 0) return SUnknown.I; var s = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); return s; }

            foreach (var ins in body.Instructions)
            {
                if (targets.Contains(ins)) stack.Clear();
                var op = ins.OpCode;

                switch (op.Code)
                {
                    case Code.Ldtoken:
                        stack.Add(ins.Operand is TypeReference tr ? (Sym)new SType { T = tr } : SUnknown.I);
                        continue;
                    case Code.Ldstr:
                        stack.Add(new SStr { S = (string)ins.Operand });
                        continue;
                    case Code.Ldnull:
                        stack.Add(SNull.I);
                        continue;
                    case Code.Dup:
                        {
                            var top = Pop(); stack.Add(top); stack.Add(top);
                            continue;
                        }
                    case Code.Newarr:
                        Pop(); stack.Add(new SArr());
                        continue;
                    case Code.Stelem_Ref:
                    case Code.Stelem_Any:
                        {
                            var val = Pop(); var idx = Pop(); var arr = Pop();
                            if (arr is SArr sa && idx is SInt si)
                            {
                                while (sa.Items.Count <= si.V) sa.Items.Add(SUnknown.I);
                                sa.Items[si.V] = val;
                            }
                            continue;
                        }
                }

                int? ldc = LdcValue(ins);
                if (ldc.HasValue) { stack.Add(new SInt { V = ldc.Value }); continue; }

                int? stloc = LocalIndex(ins, store: true);
                if (stloc.HasValue) { locals[stloc.Value] = Pop(); continue; }
                int? ldloc = LocalIndex(ins, store: false);
                if (ldloc.HasValue) { stack.Add(locals.TryGetValue(ldloc.Value, out var lv) ? lv : SUnknown.I); continue; }

                if (op.Code == Code.Call || op.Code == Code.Callvirt || op.Code == Code.Newobj)
                {
                    var mr = (MethodReference)ins.Operand;
                    int n = mr.Parameters.Count;
                    var callArgs = new Sym[n];
                    for (int i = n - 1; i >= 0; i--) callArgs[i] = Pop();
                    Sym self = (mr.HasThis && op.Code != Code.Newobj) ? Pop() : null;
                    Sym result = InterpretCall(mr, self, callArgs, op.Code == Code.Newobj, mod, where, registered, ref assemblyWide);
                    bool pushes = op.Code == Code.Newobj || mr.ReturnType.FullName != "System.Void";
                    if (pushes) stack.Add(result ?? SUnknown.I);
                    continue;
                }

                // Everything else: keep the stack depth honest, values unknown.
                int pops = PopCount(ins, stack.Count);
                for (int i = 0; i < pops; i++) Pop();
                int pushes2 = PushCount(ins);
                for (int i = 0; i < pushes2; i++) stack.Add(SUnknown.I);

                if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Return || op.FlowControl == FlowControl.Throw)
                    stack.Clear();
            }
        }

        static Sym InterpretCall(MethodReference mr, Sym self, Sym[] a, bool isCtor, string mod, string where, HashSet<string> registered, ref bool assemblyWide)
        {
            string decl = mr.DeclaringType.FullName;
            string name = mr.Name;

            if (decl == "System.Type" && name == "GetTypeFromHandle") return a[0];

            // ---- AccessTools -----------------------------------------------------------------
            if (decl == "HarmonyLib.AccessTools")
            {
                switch (name)
                {
                    case "TypeByName":
                        if (a.Length > 0 && a[0] is SStr tn) return CheckTypeByName(tn.S, mod, where);
                        return SUnknown.I;
                    case "Method":
                    case "DeclaredMethod":
                        {
                            SMember m = null;
                            if (a.Length >= 2 && mr.Parameters[0].ParameterType.FullName == "System.Type" && a[1] is SStr ms)
                                m = new SMember { Kind = "Method", Type = a[0], Name = ms.S, ArgTypes = a.Length > 2 ? a[2] : null };
                            else if (a.Length >= 1 && mr.Parameters[0].ParameterType.FullName == "System.String" && a[0] is SStr colon && colon.S.Contains(":"))
                            {
                                var parts = colon.S.Split(':');
                                m = new SMember { Kind = "Method", Type = new SType { ByName = parts[0] }, Name = parts[1], ArgTypes = a.Length > 1 ? a[1] : null };
                            }
                            if (m == null) return SUnknown.I;
                            CheckMember(m, mod, where);
                            return m;
                        }
                    case "Constructor":
                    case "DeclaredConstructor":
                        {
                            var m = new SMember { Kind = "Ctor", Type = a[0], ArgTypes = a.Length > 1 ? a[1] : null };
                            CheckMember(m, mod, where);
                            return m;
                        }
                    case "Field":
                    case "DeclaredField":
                    case "Property":
                    case "DeclaredProperty":
                    case "PropertyGetter":
                    case "DeclaredPropertyGetter":
                    case "PropertySetter":
                    case "DeclaredPropertySetter":
                        if (a.Length >= 2 && a[1] is SStr fs && mr.Parameters[0].ParameterType.FullName == "System.Type")
                        {
                            string kind = name.Contains("Field") ? "Field" : name.Contains("Getter") ? "Getter" : name.Contains("Setter") ? "Setter" : "Property";
                            var m = new SMember { Kind = kind, Type = a[0], Name = fs.S };
                            CheckMember(m, mod, where);
                            return m;
                        }
                        return SUnknown.I;
                    case "FieldRefAccess":
                    case "StaticFieldRefAccess":
                        if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count >= 1 && a.Length >= 1 && a[a.Length - 1] is SStr rs)
                        {
                            Sym t = (a.Length == 2 && a[0] is SType) ? a[0] : new SType { T = gim.GenericArguments[0] };
                            CheckMember(new SMember { Kind = "Field", Type = t, Name = rs.S }, mod, where);
                        }
                        return SUnknown.I;
                }
                return SUnknown.I;
            }

            // ---- Type.GetType("Namespace.Type, Assembly") - how the mods reach each other softly ----
            if (decl == "System.Type" && name == "GetType" && !mr.HasThis && a.Length >= 1 && a[0] is SStr spec)
                return CheckTypeSpec(spec.S, mod, where);

            // ---- System.Type reflection ---------------------------------------------------------
            if (decl == "System.Type" && self is SType && a.Length >= 1 && a[0] is SStr rn)
            {
                string kind = name == "GetMethod" ? "Method" : name == "GetField" ? "Field" : name == "GetProperty" ? "Property" : null;
                if (kind != null)
                {
                    // Raw reflection is how the mods probe for members that only exist on some game
                    // builds (and null-check the result), so a miss here is a warning, not a failure.
                    Sym argTypes = null;
                    if (kind == "Method")
                        for (int i = 1; i < a.Length; i++)
                            if (mr.Parameters[i].ParameterType.FullName == "System.Type[]") argTypes = a[i];
                    var m = new SMember { Kind = kind, Type = self, Name = rn.S, ArgTypes = argTypes, Soft = true };
                    CheckMember(m, mod, where);
                    return m;
                }
            }

            // ---- HarmonyMethod ctor -------------------------------------------------------------
            if (decl == "HarmonyLib.HarmonyMethod" && isCtor)
            {
                if (a.Length >= 2 && mr.Parameters[0].ParameterType.FullName == "System.Type" && a[1] is SStr hs)
                    return new SHarmony { Method = new SMember { Kind = "Method", Type = a[0], Name = hs.S, ArgTypes = a.Length > 2 ? a[2] : null } };
                if (a.Length >= 1) return new SHarmony { Method = a[0] };
                return SUnknown.I;
            }

            // ---- registration bookkeeping -----------------------------------------------------
            if (decl == "HarmonyLib.Harmony" && (name == "PatchAll" || name == "CreateAndPatchAll"))
            {
                if (a.Length >= 1 && a[0] is SType st && st.T != null) registered.Add(st.T.FullName.Replace('/', '+'));
                else assemblyWide = true;   // PatchAll(), PatchAll(Assembly), or a type we cannot see
                return SUnknown.I;
            }
            if (decl == "HarmonyLib.Harmony" && name == "CreateClassProcessor")
            {
                if (a.Length >= 1 && a[0] is SType st && st.T != null) registered.Add(st.T.FullName.Replace('/', '+'));
                return SUnknown.I;
            }

            // ---- manual patch call sites --------------------------------------------------------
            if (decl == "HarmonyLib.Harmony" && name == "Patch" && a.Length >= 1)
            {
                BindManual(mod, where, a[0],
                    a.Length > 1 ? a[1] : null, a.Length > 2 ? a[2] : null, a.Length > 3 ? a[3] : null, a.Length > 4 ? a[4] : null);
                return SUnknown.I;
            }

            // Any helper shaped like SafePatch(Type, string, HarmonyMethod...) / SafePatchCtor(Type, Type[], ...).
            if (mr.Parameters.Any(p => p.ParameterType.FullName == "HarmonyLib.HarmonyMethod") &&
                mr.Parameters.Count >= 2 && mr.Parameters[0].ParameterType.FullName == "System.Type")
            {
                Sym original = null;
                if (mr.Parameters[1].ParameterType.FullName == "System.String")
                {
                    if (a[0] is SType && a[1] is SStr ps)
                    {
                        Sym argTypes = null;
                        for (int i = 2; i < a.Length; i++)
                            if (mr.Parameters[i].ParameterType.FullName == "System.Type[]") argTypes = a[i];
                        original = new SMember { Kind = "Method", Type = a[0], Name = ps.S, ArgTypes = argTypes };
                    }
                }
                else if (mr.Parameters[1].ParameterType.FullName == "System.Type[]")
                {
                    if (a[0] is SType) original = new SMember { Kind = "Ctor", Type = a[0], ArgTypes = a[1] };
                }

                if (original == null)
                {
                    // Forwarding wrapper (args are the wrapper's own parameters) - its callers are scanned instead.
                    return SUnknown.I;
                }

                Sym prefix = null, postfix = null, transpiler = null, finalizer = null;
                var def = SafeResolve(mr);
                int hmIndex = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (mr.Parameters[i].ParameterType.FullName != "HarmonyLib.HarmonyMethod") continue;
                    string pname = (def != null && i < def.Parameters.Count ? def.Parameters[i].Name : "").ToLowerInvariant();
                    if (pname.Contains("post")) postfix = a[i];
                    else if (pname.Contains("trans")) transpiler = a[i];
                    else if (pname.Contains("final")) finalizer = a[i];
                    else if (pname.Contains("pre")) prefix = a[i];
                    else if (hmIndex == 0) prefix = a[i];
                    else if (hmIndex == 1) postfix = a[i];
                    hmIndex++;
                }
                BindManual(mod, where, original, prefix, postfix, transpiler, finalizer);
                return SUnknown.I;
            }

            return SUnknown.I;
        }

        static MethodDefinition SafeResolve(MethodReference mr)
        {
            try { return mr.Resolve(); } catch { return null; }
        }

        // -------------------------------------------------------------------------------------
        // symbolic -> runtime
        // -------------------------------------------------------------------------------------

        static readonly HashSet<string> Reported = new HashSet<string>();

        static Sym CheckTypeByName(string typeName, string mod, string where)
        {
            var t = AccessTools.TypeByName(typeName);
            HarmonyLog.Clear();
            if (t == null)
            {
                // Soft-dependency probes (another mod that is not installed) are expected to miss,
                // so this is a warning; a GAME type that went missing shows up here too.
                Warn(mod, "TYPEBYNAME", $"{where}: AccessTools.TypeByName(\"{typeName}\") resolves to nothing");
                return SUnknown.I;
            }
            _reflectionOk++;
            return new SType { ByName = typeName };
        }

        /// <summary>
        /// Resolves an assembly-qualified type string. A missing ASSEMBLY is a soft dependency that
        /// is simply not installed (silent); an assembly that is present but has no such TYPE is a
        /// stale name - a renamed class or a wrong namespace - and the lookup can never succeed.
        /// </summary>
        static Sym CheckTypeSpec(string spec, string mod, string where)
        {
            var parts = spec.Split(new[] { ',' }, 2);
            string typeName = parts[0].Trim();
            string asmName = parts.Length > 1 ? parts[1].Split(',')[0].Trim() : null;
            if (string.IsNullOrEmpty(asmName)) return CheckTypeByName(typeName, mod, where);

            Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == asmName);
            if (asm == null) { try { asm = Assembly.Load(asmName); } catch { } }
            if (asm == null) return SUnknown.I; // that mod is not part of this audit

            Type t = null;
            try { t = asm.GetType(typeName); } catch { }
            if (t == null)
            {
                Warn(mod, "TYPESPEC", $"{where}: Type.GetType(\"{spec}\") - {asmName} is present but has no type {typeName} (renamed, or wrong namespace?)");
                return SUnknown.I;
            }
            _reflectionOk++;
            SpecTypes[typeName] = t;
            return new SType { ByName = typeName };
        }

        static readonly Dictionary<string, Type> SpecTypes = new Dictionary<string, Type>();

        static Type ToType(Sym s)
        {
            if (!(s is SType st)) return null;
            if (st.ByName != null) return SpecTypes.TryGetValue(st.ByName, out var known) ? known : AccessTools.TypeByName(st.ByName);
            return ToType(st.T);
        }

        static Type ToType(TypeReference tr)
        {
            if (tr == null) return null;
            try
            {
                if (tr is ByReferenceType br) return ToType(br.ElementType)?.MakeByRefType();
                if (tr is ArrayType at) return ToType(at.ElementType)?.MakeArrayType();
                if (tr is GenericInstanceType git)
                {
                    var def = ToType(git.ElementType);
                    var targs = git.GenericArguments.Select(ToType).ToArray();
                    if (def == null || targs.Any(x => x == null)) return null;
                    return def.MakeGenericType(targs);
                }
                if (tr.IsGenericParameter) return null;
                string full = tr.FullName.Replace('/', '+');
                string scope = tr.Scope is AssemblyNameReference anr ? anr.Name : (tr.Scope is ModuleDefinition md ? md.Assembly.Name.Name : null);
                if (scope != null)
                {
                    Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == scope);
                    if (asm == null) { try { asm = Assembly.Load(scope); } catch { } }
                    var hit = asm?.GetType(full);
                    if (hit != null) return hit;
                }
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var hit = asm.GetType(full);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        static Type[] ToTypes(Sym s, out bool known)
        {
            known = true;
            if (s == null || s is SNull) return null;
            if (s is SArr arr)
            {
                var list = new List<Type>();
                foreach (var item in arr.Items)
                {
                    var t = ToType(item);
                    if (t == null) { known = false; return null; }
                    list.Add(t);
                }
                return list.ToArray();
            }
            known = false;
            return null;
        }

        /// <summary>Resolves a symbolic member, reporting a failure once if it does not exist. Null = not statically knowable.</summary>
        static MemberInfo CheckMember(SMember m, string mod, string where)
        {
            if (m == null) return null;
            Type type = ToType(m.Type);
            if (type == null)
            {
                // A by-name type that is absent is a soft dependency probe; a typeof() that is absent is real.
                if (m.Type is SType st && st.T != null && st.ByName == null && !st.T.IsGenericParameter)
                    FailOnce(mod, "REFLECT", $"{where}: type {st.T.FullName} could not be loaded");
                return null;
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            string label = $"{type.Name}.{m.Name ?? ".ctor"}";
            try
            {
                MemberInfo found = null;
                switch (m.Kind)
                {
                    case "Method":
                        {
                            var argTypes = ToTypes(m.ArgTypes, out bool known);
                            if (!known && m.ArgTypes != null && !(m.ArgTypes is SNull))
                            {
                                // Explicit signature we could not decode: settle for "some overload exists".
                                found = type.GetMethods(all).FirstOrDefault(x => x.Name == m.Name);
                            }
                            else found = argTypes != null ? AccessTools.Method(type, m.Name, argTypes) : AccessTools.Method(type, m.Name);
                            break;
                        }
                    case "Ctor":
                        {
                            var argTypes = ToTypes(m.ArgTypes, out bool known);
                            if (!known) found = type.GetConstructors(all).FirstOrDefault();
                            else found = AccessTools.Constructor(type, argTypes);
                            break;
                        }
                    case "Field": found = AccessTools.Field(type, m.Name); break;
                    case "Property": found = AccessTools.Property(type, m.Name); break;
                    case "Getter": found = AccessTools.PropertyGetter(type, m.Name); break;
                    case "Setter": found = AccessTools.PropertySetter(type, m.Name); break;
                }
                HarmonyLog.Clear();

                if (found == null)
                {
                    string miss = $"{where}: {m.Kind} {label}{ArgList(m)} not found{MemberHint(type, m)}";
                    if (m.Soft) Warn(mod, "PROBE", miss);
                    else FailOnce(mod, "REFLECT", miss);
                    return null;
                }
                if (Reported.Add("ok|" + mod + "|" + m.Kind + "|" + label + ArgList(m))) _reflectionOk++;
                return found;
            }
            catch (AmbiguousMatchException)
            {
                FailOnce(mod, "REFLECT", $"{where}: {m.Kind} {label} is AMBIGUOUS (overloads exist; needs explicit argument types){MemberHint(type, m)}");
                return null;
            }
            catch (Exception ex)
            {
                FailOnce(mod, "REFLECT", $"{where}: {m.Kind} {label}: {ex.Message}");
                return null;
            }
        }

        static string MemberHint(Type type, SMember m)
        {
            try
            {
                if (m.Kind != "Method" || m.Name == null) return "";
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                var same = type.GetMethods(all).Where(x => x.Name == m.Name).ToList();
                if (same.Count == 0) return "";
                return "   [existing overloads: " + string.Join(" | ", same.Select(x => "(" + string.Join(", ", x.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")")) + "]";
            }
            catch { return ""; }
        }

        static string ArgList(SMember m)
        {
            if (!(m.ArgTypes is SArr arr)) return "";
            return "(" + string.Join(", ", arr.Items.Select(i => i is SType st ? (st.T?.Name ?? st.ByName) : "?")) + ")";
        }

        static void BindManual(string mod, string where, Sym original, Sym prefix, Sym postfix, Sym transpiler, Sym finalizer)
        {
            if (!(original is SMember om))
            {
                // BaseModPlugin.SafePatch itself and other forwarding helpers land here: their callers are scanned instead.
                if (!where.Contains("SafePatch"))
                    Warn(mod, "DYNAMIC", $"{where}: Harmony.Patch target is computed at runtime - check by hand");
                return;
            }
            var target = CheckMember(om, mod, where) as MethodBase;
            if (target == null) return; // already reported (or soft dependency)

            var patches = new List<KeyValuePair<string, MethodInfo>>();
            void Add(Sym s, string kind)
            {
                if (s == null || s is SNull) return;
                if (!(s is SHarmony sh) || !(sh.Method is SMember pm))
                {
                    Warn(mod, "DYNAMIC", $"{where}: {kind.ToLowerInvariant()} for {target.DeclaringType.Name}.{target.Name} is built at runtime - check by hand");
                    return;
                }
                if (CheckMember(pm, mod, where) is MethodInfo mi) patches.Add(new KeyValuePair<string, MethodInfo>(kind, mi));
            }
            Add(prefix, "Prefix");
            Add(postfix, "Postfix");
            Add(transpiler, "Transpiler");
            Add(finalizer, "Finalizer");
            if (patches.Count == 0) return;

            Bind(mod, "manual", target, patches);
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        static void Fail(string kind, string msg)
        {
            _failures++;
            Console.WriteLine($"  FAIL [{kind}] {msg}");
        }

        static void FailOnce(string mod, string kind, string msg)
        {
            if (Reported.Add(mod + "|" + kind + "|" + msg)) Fail(kind, msg);
        }

        static void Warn(string mod, string kind, string msg)
        {
            if (!Reported.Add(mod + "|" + kind + "|" + msg)) return;
            _warnings++;
            Console.WriteLine($"  warn [{kind}] {msg}");
        }

        static string FirstLine(string s)
        {
            if (s == null) return "";
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return (nl < 0 ? s : s.Substring(0, nl)).Trim();
        }

        static string Flatten(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                string m = (e.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (m.Length > 0 && !parts.Contains(m)) parts.Add(m);
            }
            return string.Join("  <=  ", parts);
        }

        static int? LdcValue(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_S: return (sbyte)ins.Operand;
                case Code.Ldc_I4: return (int)ins.Operand;
            }
            return null;
        }

        static int? LocalIndex(Instruction ins, bool store)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Stloc_0: return store ? 0 : (int?)null;
                case Code.Stloc_1: return store ? 1 : (int?)null;
                case Code.Stloc_2: return store ? 2 : (int?)null;
                case Code.Stloc_3: return store ? 3 : (int?)null;
                case Code.Stloc:
                case Code.Stloc_S: return store ? ((VariableDefinition)ins.Operand).Index : (int?)null;
                case Code.Ldloc_0: return store ? (int?)null : 0;
                case Code.Ldloc_1: return store ? (int?)null : 1;
                case Code.Ldloc_2: return store ? (int?)null : 2;
                case Code.Ldloc_3: return store ? (int?)null : 3;
                case Code.Ldloc:
                case Code.Ldloc_S: return store ? (int?)null : ((VariableDefinition)ins.Operand).Index;
            }
            return null;
        }

        static int PopCount(Instruction ins, int depth)
        {
            switch (ins.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref: return 3;
                case StackBehaviour.PopAll: return depth;
                case StackBehaviour.Varpop:
                    if (ins.OpCode.Code == Code.Ret) return depth;
                    if (ins.Operand is MethodReference mr) return mr.Parameters.Count + (mr.HasThis ? 1 : 0); // calli etc.
                    return 0;
            }
            return 0;
        }

        static int PushCount(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    return ins.Operand is MethodReference mr && mr.ReturnType.FullName != "System.Void" ? 1 : 0;
                default: return 1;
            }
        }
    }
}
