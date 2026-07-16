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
        public const string PLUGIN_VERSION = "1.0.0";

        public static ReveriePlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[Reverie] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_DoConvoTextFieldSubmission));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_GenerateTxtNoTryStrStyle));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_ReadSaveFile));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_WriteSaveFile));
            Harmony.CreateAndPatchAll(typeof(ReverieInterceptor.Patch_DoNewGame));
            Harmony.CreateAndPatchAll(typeof(Patch_ConsoleCommands));

            ReverieManager.Init();

            // Register with GenContext if present (soft dependency — inert without it)
            try
            {
                AIROG_GenContext.ContextManager.RegisterProvider(new ReverieProvider());
                Logger.LogInfo("[Reverie] ReverieProvider registered with GenContext.");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"[Reverie] GenContext not available — the dream layer is disabled. ({ex.Message})");
            }
        }

        // ---- Console commands ----

        [HarmonyPatch(typeof(GameplayManager), "ProcessConsoleCommand")]
        public static class Patch_ConsoleCommands
        {
            [HarmonyPrefix]
            public static bool Prefix(string txt, GameplayManager __instance)
            {
                string cmd = txt.ToUpperInvariant().Trim();

                if (cmd == "REVERIE_TEST")
                {
                    // Force a dream to begin immediately (bypasses roll + cooldown)
                    if (ReverieManager.State.Phase == DreamPhase.Dreaming)
                    MessageModal.I.ShowModal("Already dreaming.", false, true);
                    else
                        ReverieManager.BeginDream();
                    return false;
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
                    return false;
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
                    return false;
                }

                return true;
            }
        }
    }
}
