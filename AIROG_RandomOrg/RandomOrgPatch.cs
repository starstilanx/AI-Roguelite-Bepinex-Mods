using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace AIROG_RandomOrg
{
    // -----------------------------------------------------------------------
    // Roll-scope tracking
    // The game funnels every dice/skill-check roll through
    // Utils.GetRollOutcome(GameplayManager, ...) and
    // Utils.GetRollOutcomeForAttrOnly(...). We mark a scope while either is on
    // the call stack; the Rand patches below only spend Random.org numbers
    // inside that scope (unless DiceRollsOnly is turned off). Everything else
    // (colors, voice picks, world gen, UI) keeps the vanilla System.Random,
    // so the daily API quota is spent on actual rolls only.
    // -----------------------------------------------------------------------

    public static class RollScope
    {
        [ThreadStatic] private static int _depth;

        public static bool Active => _depth > 0;

        public static void Enter() { _depth++; }

        public static void Exit() { if (_depth > 0) _depth--; }

        /// <summary>True when this Rand call is allowed to consume the buffer.</summary>
        public static bool AllowTrueRandom =>
            !RandomOrgPlugin.DiceRollsOnly.Value || Active;
    }

    // The game keeps appending trailing optional parameters to this method (07/18 added
    // `GameCharacter actor`, the 11/18 Perks v2 build added `PerkNode passivePerk`), and every time
    // an explicit Type[] goes stale the patch dies with "Undefined target method" - which, with
    // DiceRollsOnly on, silently turns the whole mod off for skill checks. So the overload is picked
    // by shape instead: the one whose first parameter is the GameplayManager (the other overload is
    // the pure (double rollNum, double[] bkpts) maths helper, which must NOT open a roll scope).
    [HarmonyPatch]
    public static class Patch_Utils_GetRollOutcome_Scope
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Utils)))
            {
                if (m.Name != "GetRollOutcome") continue;
                var ps = m.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType == typeof(GameplayManager)) return m;
            }
            return null;
        }

        [HarmonyPrefix]
        public static void Prefix() => RollScope.Enter();

        [HarmonyFinalizer]
        public static void Finalizer() => RollScope.Exit();
    }

    [HarmonyPatch(typeof(Utils), "GetRollOutcomeForAttrOnly", new Type[]
    {
        typeof(GameplayManager), typeof(SS.PlayerAttribute), typeof(long),
        typeof(long), typeof(SS.GameDifficulty), typeof(SS.RollOutcome), typeof(double)
    })]
    public static class Patch_Utils_GetRollOutcomeForAttrOnly_Scope
    {
        [HarmonyPrefix]
        public static void Prefix() => RollScope.Enter();

        [HarmonyFinalizer]
        public static void Finalizer() => RollScope.Exit();
    }

    // -----------------------------------------------------------------------
    // Dice-roll patches
    // These replace calls to Utils.RandDouble / RandInt / RandIntInclusive
    // with a value from the Random.org buffer when the mod is enabled and
    // (by default) a dice roll is in progress — see RollScope above.
    // If the buffer is empty the original System.Random path is used as fallback.
    // -----------------------------------------------------------------------

    [HarmonyPatch(typeof(Utils), "RandDouble", new Type[] { typeof(double) })]
    public static class Patch_Utils_RandDouble
    {
        [HarmonyPrefix]
        public static bool Prefix(double high, ref double __result)
        {
            if (!RandomOrgPlugin.Enabled.Value) return true;
            if (!RollScope.AllowTrueRandom) return true;
            if (!RandomOrgClient.TryGetRandom(out double val)) return true;

            __result = val * high;
            if (RandomOrgPlugin.ShowNotification.Value)
                RandomOrgPlugin.Log.LogInfo(
                    $"[RandomOrg] RandDouble({high:F2}) → {__result:F4}  (buf: {RandomOrgClient.BufferCount})");
            return false;
        }
    }

    [HarmonyPatch(typeof(Utils), "RandInt", new Type[] { typeof(int) })]
    public static class Patch_Utils_RandInt
    {
        [HarmonyPrefix]
        public static bool Prefix(int n, ref int __result)
        {
            if (!RandomOrgPlugin.Enabled.Value) return true;
            if (!RollScope.AllowTrueRandom) return true;
            // Vanilla Random.Next(0) is 0 (and a negative n throws); the mapping below would
            // answer -1. Leave degenerate ranges to the original.
            if (n <= 0) return true;
            if (!RandomOrgClient.TryGetRandom(out double val)) return true;

            __result = Math.Min((int)(val * n), n - 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(Utils), "RandIntInclusive", new Type[] { typeof(int), typeof(int) })]
    public static class Patch_Utils_RandIntInclusive
    {
        [HarmonyPrefix]
        public static bool Prefix(int low, int high, ref int __result)
        {
            if (!RandomOrgPlugin.Enabled.Value) return true;
            if (!RollScope.AllowTrueRandom) return true;
            // An empty range (high < low) would come back as high, below the caller's minimum;
            // vanilla returns low or throws. Leave it to the original.
            if (high < low) return true;
            if (!RandomOrgClient.TryGetRandom(out double val)) return true;

            __result = Math.Min(low + (int)(val * (high - low + 1)), high);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // GenContext Mods-menu injection
    // Intercepts NTextPromptModal.PresentSelf when it's opened as
    // "GenContext Mod Manager" and appends two RandomOrg toggle items.
    // The actual Harmony.Patch() call is made from RandomOrgPlugin.Awake()
    // using explicit method lookup so we aren't bound to a compile-time type.
    // -----------------------------------------------------------------------

    public static class RandomOrgUI
    {
        /// <summary>
        /// Prefix injected into NTextPromptModal.PresentSelf at runtime.
        /// Parameters use Harmony's positional naming: __instance = this,
        /// __0 = first param (the PromptArg list), __1 = second param (title).
        /// </summary>
        public static void PresentSelf_Prefix(object __instance, object __0, string __1)
        {
            if (__1 != "GenContext Mod Manager") return;

            // A prefix that throws stops PresentSelf, i.e. GenContext's whole Mods menu, from
            // opening. Losing these three rows is the worst that should happen.
            try
            {
                AppendToggles(__instance, __0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RandomOrg] Mods menu injection failed (non-fatal): {ex.Message}");
            }
        }

        private static void AppendToggles(object __instance, object __0)
        {
            var list = __0 as IList;
            if (list == null) return;

            Type toggleType = __instance.GetType().GetNestedType("TogglePromptArg");
            if (toggleType == null)
            {
                Debug.LogWarning("[RandomOrg] Could not find TogglePromptArg — skipping menu injection.");
                return;
            }

            // --- Enable toggle ---
            {
                bool enabled   = RandomOrgPlugin.Enabled.Value;
                string keyInfo = string.IsNullOrWhiteSpace(RandomOrgPlugin.ApiKey.Value)
                    ? "anonymous" : "API key";

                Action<bool> onToggle = val =>
                {
                    RandomOrgPlugin.Enabled.Value = val;
                    if (val && RandomOrgClient.BufferCount == 0)
                        RandomOrgClient.PrimeFetch();
                    RandomOrgPlugin.Log.LogInfo($"[RandomOrg] Enabled → {val}");
                };

                list.Add(Activator.CreateInstance(toggleType, new object[]
                {
                    $"True Randomness (Random.org) [{keyInfo}, buf: {RandomOrgClient.BufferCount}]",
                    enabled,
                    onToggle
                }));
            }

            // --- Dice-rolls-only toggle ---
            {
                bool enabled = RandomOrgPlugin.DiceRollsOnly.Value;
                Action<bool> onToggle = val =>
                {
                    RandomOrgPlugin.DiceRollsOnly.Value = val;
                    RandomOrgPlugin.Log.LogInfo($"[RandomOrg] DiceRollsOnly → {val}");
                };

                list.Add(Activator.CreateInstance(toggleType, new object[]
                {
                    "  └ Dice rolls only (saves daily API quota)",
                    enabled,
                    onToggle
                }));
            }

            // --- Log-on-roll toggle ---
            {
                bool enabled = RandomOrgPlugin.ShowNotification.Value;
                Action<bool> onToggle = val =>
                {
                    RandomOrgPlugin.ShowNotification.Value = val;
                    RandomOrgPlugin.Log.LogInfo($"[RandomOrg] ShowNotification → {val}");
                };

                list.Add(Activator.CreateInstance(toggleType, new object[]
                {
                    "  \u2514 Log each roll to BepInEx console",
                    enabled,
                    onToggle
                }));
            }
        }
    }
}
