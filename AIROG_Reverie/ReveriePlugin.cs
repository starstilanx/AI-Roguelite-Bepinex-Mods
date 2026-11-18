using System.Linq;
using BepInEx;
using HarmonyLib;

namespace AIROG_Reverie
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    public class ReveriePlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.airog.reverie";
        public const string PLUGIN_NAME    = "AIROG Reverie";
        public const string PLUGIN_VERSION = "1.0.1";

        public static ReveriePlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[Reverie] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_DoConvoTextFieldSubmission));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_GenerateTxtNoTryStrStyle));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_GenerateTxtFromTxtAndImg));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_ReadSaveFile));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_WriteSaveFile));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_DoNewGame));
            Harmony.CreateAndPatchAll(typeof(Patch_ConsoleCommands));

            ReverieManager.Init();

            // Register with GenContext if present (soft dependency — inert without it)
            try
            {
                RegisterProvider();
                Logger.LogInfo("[Reverie] ReverieProvider registered with GenContext.");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"[Reverie] GenContext not available — the dream layer is disabled. ({ex.Message})");
            }
        }

        // Own method so a missing GenContext assembly fails here, inside the caller's try, and
        // not while Awake itself is being compiled.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RegisterProvider()
            => AIROG_GenContext.ContextManager.RegisterProvider(new ReverieProvider());

        // ---- Console commands ----

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        public static class Patch_ConsoleCommands
        {
            [HarmonyPrefix]
            public static bool Prefix(string txt, GameplayManager __instance)
            {
                // Every mod's console commands hang off this one prefix chain: never throw out of it.
                try
                {
                    return !Handle((txt ?? "").ToUpperInvariant().Trim());
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError($"[Reverie] Console command error: {ex.Message}");
                    return true;
                }
            }

            /// <summary>True if the command was one of ours.</summary>
            private static bool Handle(string cmd)
            {
                if (cmd == "REVERIE_TEST")
                {
                    // Force a dream to begin immediately (bypasses roll + cooldown)
                    if (!ReverieManager.Active)
                        // Starting one anyway would print the falling-asleep line for a dream nobody narrates
                        MessageModal.I.ShowModal("Reverie is switched off for this save (Mods checklist).", false, true);
                    else if (ReverieManager.State.Phase == DreamPhase.Dreaming)
                        MessageModal.I.ShowModal("Already dreaming.", false, true);
                    else
                        ReverieManager.BeginDream();
                    return true;
                }

                if (cmd == "REVERIE_WAKE")
                {
                    if (ReverieManager.State.Phase == DreamPhase.Dreaming && ReverieManager.State.CurrentDream != null)
                    {
                        var d = ReverieManager.State.CurrentDream;
                        ReverieManager.Wake(
                            d.Progress >= ReverieManager.TRIUMPH_THRESHOLD ? WakeOutcome.Triumph : WakeOutcome.Neutral,
                            null);
                    }
                    else
                        MessageModal.I.ShowModal("Not dreaming.", false, true);
                    return true;
                }

                if (cmd == "REVERIE_STATUS")
                {
                    var st = ReverieManager.State;
                    string dreamLine = st.Phase == DreamPhase.Dreaming && st.CurrentDream != null
                        ? $"DREAMING — \"{st.CurrentDream.Theme}\"\n" +
                          $"  Lucidity {st.CurrentDream.Lucidity}/{ReverieManager.MAX_LUCIDITY}, " +
                          $"progress {st.CurrentDream.Progress}/100, " +
                          $"{st.CurrentDream.DreamTurnsRemaining} dream-turns left"
                        : "Awake";
                    string omens = ReverieManager.LiveOmens().Count > 0
                        ? string.Join("\n", ReverieManager.LiveOmens().Select(o => $"  \"{o.Text}\" (until turn {o.ExpiresTurn})"))
                        : "  none";
                    var haunting = ReverieManager.LiveHaunting();
                    MessageModal.I.ShowModal(
                        $"Turn {st.GlobalTurn} — {dreamLine}\n" +
                        $"Live omens:\n{omens}\n" +
                        $"Haunting: {(haunting != null ? haunting.Text : "none")}\n" +
                        $"Dreams: {st.TotalDreams} ({st.TotalTriumphs} triumphs, {st.TotalNightmares} nightmares)",
                        false, true);
                    return true;
                }

                return false;
            }
        }
    }
}
