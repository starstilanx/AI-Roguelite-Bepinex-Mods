using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AIROG_Chronicle
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency("com.airog.gencontext", BepInDependency.DependencyFlags.SoftDependency)]
    public class ChroniclePlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID    = "com.airog.chronicle";
        public const string PLUGIN_NAME    = "AIROG Chronicle";
        public const string PLUGIN_VERSION = "1.2.0";

        public static ChroniclePlugin Instance { get; private set; }

        private static ConfigEntry<int> _recentChapters, _chaptersPerAct, _maxActs, _maxRecalled, _maxRecapChars, _maxTokens, _fallbackTurns;
        private static ConfigEntry<bool> _fallbackBeats;
        private static ConfigEntry<string> _tagBlacklist;

        /// <summary>Current config values (the defaults if the plugin has not started yet).</summary>
        public static ChronicleSettings Settings()
        {
            var s = new ChronicleSettings();
            if (_recentChapters == null) return s;
            s.RecentChapters    = _recentChapters.Value;
            s.ChaptersPerAct    = _chaptersPerAct.Value;
            s.MaxActsInPrompt   = _maxActs.Value;
            s.MaxRecalled       = _maxRecalled.Value;
            s.MaxRecapChars     = _maxRecapChars.Value;
            s.MaxTokens         = _maxTokens.Value;
            s.TagBlacklist      = _tagBlacklist.Value ?? "";
            s.FallbackBeats     = _fallbackBeats.Value;
            s.FallbackBeatTurns = _fallbackTurns.Value;
            return s;
        }

        private void BindConfig()
        {
            _recentChapters = Config.Bind("Prompt", "RecentChaptersVerbatim", 5, new ConfigDescription(
                "How many of the newest closed chapters always have their recap sent to the AI. " +
                "Older chapters leave the prompt, and only come back condensed into an act summary or when one of their tags shows up in the scene.",
                new AcceptableValueRange<int>(0, 30)));
            _chaptersPerAct = Config.Bind("Prompt", "ChaptersPerAct", 5, new ConfigDescription(
                "Once this many chapters have aged out of the recent window they are condensed, by one background AI call, into a short act summary. " +
                "0 turns acts off (old chapters are then simply dropped from the prompt, apart from tag recalls).",
                new AcceptableValueRange<int>(0, 30)));
            _maxActs = Config.Bind("Prompt", "MaxActSummariesInPrompt", 4, new ConfigDescription(
                "Only the newest this-many act summaries are sent to the AI. Older acts stay in the save and in the Chronicle panel.",
                new AcceptableValueRange<int>(0, 20)));
            _maxRecalled = Config.Bind("Prompt", "MaxRecalledChaptersPerTurn", 2, new ConfigDescription(
                "Each closed chapter has a few tags (names of people, places and things central to it). When a tag of an older chapter appears in the current scene, " +
                "that chapter's recap is sent again for that turn. This is the most chapters recalled per turn; chapters with a milestone are preferred, then the most recent. 0 turns recall off.",
                new AcceptableValueRange<int>(0, 10)));
            _tagBlacklist = Config.Bind("Prompt", "TagBlacklist", "",
                "Comma-separated tags that never trigger a chapter recall. Use it for names that are in nearly every turn, such as your home town or a constant companion. " +
                "The player character's name is always treated as blacklisted on top of this list. Tags shorter than 3 characters never match either.");
            _maxRecapChars = Config.Bind("Prompt", "MaxCharactersPerEntry", 400, new ConfigDescription(
                "Longest chapter recap, act summary or beat sent to the AI, in characters. Longer ones are cut at a word boundary (the full text stays in the save).",
                new AcceptableValueRange<int>(80, 2000)));

            _maxTokens = Config.Bind("Prompt", "MaxTokens", 1200, new ConfigDescription(
                "Hard ceiling on everything Chronicle adds to one prompt, in tokens (about 4 characters each). If the entries above add up to more, " +
                "the oldest material is left out first: act summaries, then recalled chapters, then the older recent recaps. GenContext's own shared budget still applies on top.",
                new AcceptableValueRange<int>(150, 8000)));

            _fallbackBeats = Config.Bind("Beats", "FallbackBeats", true,
                "Chronicle normally asks the story AI to append a hidden one-line beat to each response. Some setups rarely do (most often the specialized pipeline, " +
                "whose short reply limit cuts the hidden block off). With this on, Chronicle writes the missing beat itself with one small background AI call. Turn off to never spend that call.");
            _fallbackTurns = Config.Bind("Beats", "FallbackBeatAfterTurns", 3, new ConfigDescription(
                "How many story turns in a row may pass without a beat before Chronicle writes one itself from those turns' story text. 1 means every turn without a beat.",
                new AcceptableValueRange<int>(1, 50)));
        }

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"[Chronicle] {PLUGIN_GUID} v{PLUGIN_VERSION} loaded.");

            BindConfig();

            // Apply all Harmony patches
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_GenerateTxtNoTryStrStyle));
            Harmony.CreateAndPatchAll(typeof(ChronicleResponseInterceptor.Patch_GenerateTxtFromTxtAndImg));
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
                RegisterProvider();
                Logger.LogInfo("[Chronicle] ChronicleProvider registered with GenContext.");
            }
            catch (System.Exception ex)
            {
                Logger.LogWarning($"[Chronicle] GenContext not available — context injection disabled. ({ex.Message})");
            }
        }

        // Own method so a missing GenContext assembly fails here, inside the caller's try, and
        // not while Awake itself is being compiled.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RegisterProvider()
            => AIROG_GenContext.ContextManager.RegisterProvider(new ChronicleProvider());
    }
}
