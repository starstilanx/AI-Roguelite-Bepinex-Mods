using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;

namespace AIROG_OpenAIImage
{
    /// <summary>
    /// Bootstrap + configuration for the OpenAI-compatible image backend. The Harmony patches
    /// live in OpenAIImageMenuPatches.cs (Options UI) and OpenAIImageGenerationPatches.cs
    /// (AIAsker interception); the HTTP call lives in OpenAIImageClient.cs.
    /// </summary>
    [BepInPlugin("com.airog.openaiimage", "OpenAI Image", "2.0.0")]
    public class OpenAIImagePlugin : BaseUnityPlugin
    {
        public static OpenAIImagePlugin Instance;
        public static BepInEx.Logging.ManualLogSource Log;

        // Custom image-gen mode. 98 belongs to AIROG_VertexAI and 99 to AIROG_NanoBanana, so this
        // backend sits on 97. Check the claimed-value registry before adding another backend.
        public const SS.ImageGenerationMode OPENAI_IMG_MODE = (SS.ImageGenerationMode)97;

        public const string DROPDOWN_LABEL = "OpenAI-Compatible API";

        public const string PREF_KEY_API_KEY = "PREF_KEY_OPENAI_IMG_GEN_API_KEY";
        public const string PREF_KEY_MODEL   = "PREF_KEY_OPENAI_IMG_GEN_MODEL";
        public const string PREF_KEY_ACTIVE  = "PREF_KEY_OPENAI_IMG_ACTIVE";

        // Suppresses ApplyUiState during the Options() body, so the spurious
        // OnImageGenerationDropdownChanged the game fires internally (before our row is
        // injected) can't read "not selected" and clear the saved pref.
        internal static bool _optionsOpenInProgress = false;

        private ConfigEntry<string> _cfgApiKey;
        private ConfigEntry<string> _cfgBaseUrl;
        private ConfigEntry<string> _cfgModels;
        private ConfigEntry<string> _cfgModeration;
        private ConfigEntry<string> _cfgOutputFormat;
        private ConfigEntry<string> _cfgQuality;
        private ConfigEntry<string> _cfgSize;

        private string[] _models = { "gpt-image-1" };

        /// <summary>Model IDs offered in the preset dropdown, parsed from the Models config entry.</summary>
        public string[] Models => _models;

        /// <summary>PlayerPrefs key wins (set in-game); the config entry is the fallback.</summary>
        public string ApiKey
        {
            get
            {
                string pref = PlayerPrefs.GetString(PREF_KEY_API_KEY, "");
                return !string.IsNullOrEmpty(pref) ? pref : _cfgApiKey.Value;
            }
        }

        public string ActiveModel
        {
            get
            {
                string saved = PlayerPrefs.GetString(PREF_KEY_MODEL, "");
                return !string.IsNullOrEmpty(saved) ? saved : _models[0];
            }
        }

        public string BaseUrl      => _cfgBaseUrl.Value;
        public string Moderation   => (_cfgModeration.Value   ?? "").Trim();
        public string OutputFormat => (_cfgOutputFormat.Value ?? "").Trim().ToLowerInvariant();
        public string Quality      => (_cfgQuality.Value      ?? "").Trim().ToLowerInvariant();
        public string SizeOverride => (_cfgSize.Value         ?? "").Trim();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _cfgApiKey = Config.Bind("General", "OpenAIApiKey", "",
                "API key for the image endpoint (also settable in-game via Options).");
            _cfgBaseUrl = Config.Bind("General", "OpenAIBaseUrl", "https://api.openai.com/v1",
                "Base URL of the OpenAI-compatible image API (e.g. https://api.venice.ai/api/v1).");
            _cfgModels = Config.Bind("General", "Models", "gpt-image-1,dall-e-3,dall-e-2",
                "Comma-separated model IDs to offer in the in-game preset dropdown. " +
                "Edit this to match whatever your endpoint serves.");

            _cfgQuality = Config.Bind("Tuning", "Quality", "",
                "Image quality. gpt-image-*: low/medium/high/auto. dall-e-3: standard/hd. " +
                "Leave empty for the API default.");
            _cfgSize = Config.Bind("Tuning", "ImageSize", "",
                "Force one size for every image (e.g. 1024x1024). Leave empty to pick a size " +
                "per entity type from the sizes your model supports.");

            _cfgModeration = Config.Bind("Compatible", "Moderation", "",
                "gpt-image-* and some third-party servers only: 'auto' or 'low'. " +
                "Ignored for dall-e-* models, which reject it. Leave empty to omit.");
            _cfgOutputFormat = Config.Bind("Compatible", "OutputFormat", "",
                "gpt-image-* and some third-party servers only: png or jpeg. " +
                "webp is NOT supported — the game cannot decode it. Leave empty for the default.");

            RebuildModelList();
            _cfgModels.SettingChanged += (_, __) => RebuildModelList();

            var harmony = new Harmony("com.airog.openaiimage");
            harmony.PatchAll();
            Logger.LogInfo($"OpenAIImage loaded (mode {(int)OPENAI_IMG_MODE}) — {_models.Length} model(s) configured.");
        }

        private void RebuildModelList()
        {
            string[] parsed = (_cfgModels.Value ?? "")
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToArray();

            _models = parsed.Length > 0 ? parsed : new[] { "gpt-image-1" };
        }
    }
}
