using System;
using AIROG_Core;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AIROG_VertexAI
{
    /// <summary>
    /// Adds Google Vertex AI (express mode — a single API key, no GCP project or region)
    /// as both a text-generation and an image-generation backend.
    ///
    /// Text generation piggybacks on the game's OPENAI_API mode: the injected dropdown row
    /// reports itself as <see cref="SS.TextGenerationMode.OPENAI_API"/> so every
    /// mode-keyed lookup in the game (summaryceptionByMode, context char limits,
    /// IsOfficialServers, ...) keeps working, and a separate PlayerPref marks that our
    /// backend — not a local OpenAI-compatible server — should service the request.
    /// Image generation uses its own <see cref="SS.ImageGenerationMode"/> value 98,
    /// deliberately clear of AIROG_NanoBanana's 99 so both mods can be installed at once.
    /// </summary>
    [BepInPlugin(GUID, "AIROG Vertex AI", "1.0.1")]
    public class VertexAIPlugin : BaseModPlugin
    {
        public const string GUID = "com.airog.vertexai";

        public static VertexAIPlugin Instance;
        public static ManualLogSource Log;
        public static VertexCatalogue Catalogue;

        // --- Dropdown row labels (also the identity check used by every menu patch) ---
        public const string TEXT_OPTION_LABEL  = "Vertex AI (Google)";
        public const string IMAGE_OPTION_LABEL = "Vertex AI (Gemini Image)";

        /// <summary>Our slot in SS.ImageGenerationMode. 99 belongs to AIROG_NanoBanana.</summary>
        public const SS.ImageGenerationMode VERTEX_IMG_MODE = (SS.ImageGenerationMode)98;

        // --- PlayerPrefs keys ---
        public const string PREF_API_KEY     = "PREF_KEY_VERTEX_API_KEY";
        public const string PREF_TEXT_MODEL  = "PREF_KEY_VERTEX_TEXT_MODEL";
        public const string PREF_TEXT_ACTIVE = "PREF_KEY_VERTEX_TEXT_ACTIVE";
        public const string PREF_IMG_MODEL   = "PREF_KEY_VERTEX_IMG_MODEL";
        public const string PREF_IMG_SIZE    = "PREF_KEY_VERTEX_IMG_SIZE";
        public const string PREF_IMG_ACTIVE  = "PREF_KEY_VERTEX_IMG_ACTIVE";

        // The game's own image-gen mode pref, which we have to co-own to persist mode 98.
        public const string PREF_IMG_GEN_MODE = "PREF_KEY_IMAGE_GENERATION_MODE2";

        // PlayerPrefs is main-thread-only, but the API clients run on background AI tasks.
        // Everything they need is mirrored here and refreshed whenever the menu writes prefs.
        public static string CachedApiKey     { get; private set; } = "";
        public static string CachedTextModel  { get; private set; } = "";
        public static string CachedImageModel { get; private set; } = "";
        public static string CachedImageSize  { get; private set; } = "";
        public static bool   CachedTextActive { get; private set; }

        /// <summary>True when Vertex should service text generation instead of the OpenAI-compatible client.</summary>
        public static bool TextBackendActive => CachedTextActive;

        /// <summary>True when Vertex should service image generation.</summary>
        public static bool ImageBackendActive => SS.I != null && SS.I.imageGenerationMode == VERTEX_IMG_MODE;

        protected override void Awake()
        {
            base.Awake();
            Instance = this;
            Log = Logger;

            Catalogue = VertexCatalogue.LoadOrCreate(Paths.ConfigPath);
            RefreshCache();

            VertexTextPatches.Register(this);
            VertexImagePatches.Register(this);

            Logger.LogInfo($"[VertexAI] Loaded. Text backend active: {CachedTextActive}. " +
                           $"API key {(string.IsNullOrEmpty(CachedApiKey) ? "NOT set" : "set")}.");
        }

        /// <summary>Mirrors the PlayerPrefs the background clients need. Main thread only.</summary>
        public static void RefreshCache()
        {
            try
            {
                CachedApiKey     = PlayerPrefs.GetString(PREF_API_KEY, "");
                CachedTextActive = PlayerPrefs.GetInt(PREF_TEXT_ACTIVE, 0) == 1;

                string defaultTextModel = Catalogue.textModels.Count > 0 ? Catalogue.textModels[0].id : "gemini-3.5-flash";
                CachedTextModel = PlayerPrefs.GetString(PREF_TEXT_MODEL, defaultTextModel);
                if (string.IsNullOrEmpty(CachedTextModel)) CachedTextModel = defaultTextModel;

                string defaultImgModel = Catalogue.imageModels.Count > 0 ? Catalogue.imageModels[0].id : "gemini-3.1-flash-image";
                string defaultImgSize  = Catalogue.imageModels.Count > 0 ? (Catalogue.imageModels[0].size ?? "") : "";
                CachedImageModel = PlayerPrefs.GetString(PREF_IMG_MODEL, defaultImgModel);
                if (string.IsNullOrEmpty(CachedImageModel)) CachedImageModel = defaultImgModel;
                CachedImageSize = PlayerPrefs.GetString(PREF_IMG_SIZE, defaultImgSize);
            }
            catch (Exception ex)
            {
                Log.LogError($"[VertexAI] RefreshCache failed: {ex.Message}");
            }
        }

        /// <summary>Writes the shared express-mode key and refreshes the background cache.</summary>
        public static void SaveApiKey(string key)
        {
            PlayerPrefs.SetString(PREF_API_KEY, (key ?? "").Trim());
            PlayerPrefs.Save();
            RefreshCache();
        }

        /// <summary>
        /// Exposes BaseModPlugin.SafePatch to the patch registries in this assembly. Game
        /// builds rename and re-sign methods regularly, so a hook that no longer resolves
        /// logs a warning and leaves the rest of the mod working.
        /// </summary>
        internal bool TryPatch(Type targetType, string methodName, HarmonyMethod prefix = null,
                               HarmonyMethod postfix = null, Type[] argTypes = null)
        {
            return SafePatch(targetType, methodName, prefix, postfix, argTypes);
        }
    }
}
