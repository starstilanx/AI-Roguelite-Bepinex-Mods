using BepInEx;
using HarmonyLib;

namespace AIROG_Chronicle
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    public class ChroniclePlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.airog.chronicle";
        public const string PLUGIN_NAME    = "AIROG Chronicle";
        public const string PLUGIN_VERSION = "1.0.0";

        public static ChroniclePlugin Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[Chronicle] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            // Apply all Harmony patches
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_GenerateTxtNoTryStrStyle));
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_ReadSaveFile));
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_WriteSaveFile));
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_DoNewGame));
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_GainXp));
            Harmony.CreateAndPatchAll(typeof(ChronicleUI.Patch_MainLayouts));

            // Initialize manager (subscribes to TurnHappenedEvent)
            ChronicleManager.Init();

            // Register with GenContext if present (soft dependency — works without it)
            try
            {
                AIROG_GenContext.ContextManager.RegisterProvider(new ChronicleProvider());
                Logger.LogInfo("[Chronicle] ChronicleProvider registered with GenContext.");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"[Chronicle] GenContext not available — context injection disabled. ({ex.Message})");
            }
        }
    }
}
