using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace AIROG_NanoBanana
{
    [BepInPlugin("com.airog.nanobanana", "NanoBanana", "1.0.0")]
    public class NanoBananaPlugin : BaseUnityPlugin
    {
        public static NanoBananaPlugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;
        // Suppresses ApplyUiState during Options() body to avoid the spurious
        // OnImageGenerationDropdownChanged(0) call the game fires internally.
        internal static bool _optionsOpenInProgress = false;

        public const string PREF_KEY_GEMINI_API_KEY   = "PREF_KEY_GEMINI_IMG_GEN_API_KEY";
        public const string PREF_KEY_GEMINI_MODEL      = "PREF_KEY_GEMINI_IMG_GEN_MODEL";
        public const string PREF_KEY_GEMINI_RESOLUTION = "PREF_KEY_GEMINI_IMG_GEN_RESOLUTION";

        // Preset table: (display name, model ID, output resolution — empty = model default)
        internal static readonly string[] PRESET_NAMES  = { "Gemini 2.5 Flash", "Gemini 3.1 Flash · 1K", "Gemini 3.1 Flash · 2K", "Gemini 3.1 Flash · 4K" };
        internal static readonly string[] PRESET_MODELS = { "gemini-2.5-flash-image", "gemini-3.1-flash-image-preview", "gemini-3.1-flash-image-preview", "gemini-3.1-flash-image-preview" };
        internal static readonly string[] PRESET_RES    = { "", "1K", "2K", "4K" };

        public string GeminiApiKey    => PlayerPrefs.HasKey(PREF_KEY_GEMINI_API_KEY)
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_API_KEY)
            : Config.Bind("General", "GeminiApiKey", "", "API Key for Gemini Image Generation").Value;
        public string GeminiModel      => PlayerPrefs.HasKey(PREF_KEY_GEMINI_MODEL)
            ? PlayerPrefs.GetString(PREF_KEY_GEMINI_MODEL)
            : Config.Bind("General", "GeminiModel", "gemini-2.5-flash-image", "Model ID").Value;
        public string GeminiResolution => PlayerPrefs.GetString(PREF_KEY_GEMINI_RESOLUTION, "");

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            var harmony = new Harmony("com.maxloh.nanobanana");
            harmony.PatchAll();
            Logger.LogInfo("NanoBanana loaded! Ready to generate some nano bananas.");
        }
    }
}
