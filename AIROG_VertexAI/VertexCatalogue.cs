using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIROG_VertexAI
{
    /// <summary>One selectable entry in the text-model dropdown.</summary>
    public class VertexTextModel
    {
        /// <summary>Text shown in the dropdown.</summary>
        public string label;
        /// <summary>Vertex publisher model id, e.g. "gemini-3.5-flash".</summary>
        public string id;
        /// <summary>
        /// Thinking control. "minimal"/"low"/"high" emit thinkingConfig.thinkingLevel
        /// (Gemini 3.x); a bare integer emits thinkingConfig.thinkingBudget (Gemini 2.5);
        /// "default" or empty omits thinkingConfig entirely. Null = auto-detect from id.
        /// </summary>
        public string thinking;
    }

    /// <summary>One selectable entry in the image-model dropdown.</summary>
    public class VertexImageModel
    {
        public string label;
        public string id;
        /// <summary>imageConfig.imageSize — "512", "1K", "2K", "4K". Empty = model default (1K).</summary>
        public string size;
    }

    /// <summary>
    /// The model lists plus the request knobs, loaded from
    /// BepInEx/config/airog_vertexai_models.json so the lists can be edited without a
    /// rebuild. Google churns model ids fast (Imagen was fully deprecated in 2026, the
    /// Gemini 2.5 line retires Oct 2026), so the built-in defaults are a starting point,
    /// not a fixed contract.
    /// </summary>
    public class VertexCatalogue
    {
        /// <summary>API version segment of the express-mode URL. "v1" or "v1beta1".</summary>
        public string apiVersion = "v1";

        /// <summary>
        /// safetySettings threshold applied to every harm category. "OFF" fully disables
        /// filtering on Gemini 2.0+; "BLOCK_NONE" is the older equivalent; "" omits
        /// safetySettings so the account defaults apply.
        /// </summary>
        public string safetyThreshold = "OFF";

        /// <summary>Max simultaneous Vertex requests. 1 mirrors the game's own OpenAI-API path.</summary>
        public int maxConcurrentRequests = 1;

        /// <summary>Extra keys merged into generationConfig on every text request.</summary>
        public JObject extraGenerationConfig;

        public List<VertexTextModel> textModels = new List<VertexTextModel>();
        public List<VertexImageModel> imageModels = new List<VertexImageModel>();

        public const string FILE_NAME = "airog_vertexai_models.json";

        // Current as of 2026-08-15. Gemini 2.5 entries are kept because saves may still
        // reference them, but they shut down Oct 16 2026.
        private static List<VertexTextModel> DefaultTextModels() => new List<VertexTextModel>
        {
            new VertexTextModel { label = "Gemini 3.7 Flash",              id = "gemini-3.7-flash" },
            new VertexTextModel { label = "Gemini 3.5 Flash",              id = "gemini-3.5-flash" },
            new VertexTextModel { label = "Gemini 3.5 Flash-Lite",         id = "gemini-3.5-flash-lite" },
            new VertexTextModel { label = "Gemini 3.1 Pro (preview)",      id = "gemini-3.1-pro-preview" },
            new VertexTextModel { label = "Gemini 3.1 Flash-Lite",         id = "gemini-3.1-flash-lite" },
            new VertexTextModel { label = "Gemini 3 Flash",                id = "gemini-3-flash" },
            new VertexTextModel { label = "Gemini 2.5 Pro (retires Oct 2026)",        id = "gemini-2.5-pro" },
            new VertexTextModel { label = "Gemini 2.5 Flash (retires Oct 2026)",      id = "gemini-2.5-flash" },
            new VertexTextModel { label = "Gemini 2.5 Flash-Lite (retires Oct 2026)", id = "gemini-2.5-flash-lite" },
        };

        // All imagen-* models are deprecated and shutting down from Aug 17 2026, so image
        // generation routes through the Gemini image models instead.
        private static List<VertexImageModel> DefaultImageModels() => new List<VertexImageModel>
        {
            new VertexImageModel { label = "Gemini 3 Pro Image · 2K",        id = "gemini-3-pro-image",         size = "2K" },
            new VertexImageModel { label = "Gemini 3 Pro Image · 4K",        id = "gemini-3-pro-image",         size = "4K" },
            new VertexImageModel { label = "Gemini 3.1 Flash Image · 1K",    id = "gemini-3.1-flash-image",     size = "1K" },
            new VertexImageModel { label = "Gemini 3.1 Flash Image · 2K",    id = "gemini-3.1-flash-image",     size = "2K" },
            new VertexImageModel { label = "Gemini 3.1 Flash-Lite Image",    id = "gemini-3.1-flash-lite-image", size = "1K" },
            new VertexImageModel { label = "Gemini 2.5 Flash Image",         id = "gemini-2.5-flash-image",     size = "" },
        };

        public static VertexCatalogue Defaults()
        {
            return new VertexCatalogue
            {
                textModels = DefaultTextModels(),
                imageModels = DefaultImageModels(),
            };
        }

        /// <summary>
        /// Reads the override file if present, otherwise writes the defaults out so the
        /// user has something to edit. Any failure falls back to the built-in defaults.
        /// </summary>
        public static VertexCatalogue LoadOrCreate(string configDir)
        {
            string path = Path.Combine(configDir, FILE_NAME);
            try
            {
                if (File.Exists(path))
                {
                    VertexCatalogue loaded = JsonConvert.DeserializeObject<VertexCatalogue>(File.ReadAllText(path));
                    if (loaded == null) throw new Exception("file deserialized to null");
                    if (loaded.textModels == null || loaded.textModels.Count == 0)
                        loaded.textModels = DefaultTextModels();
                    if (loaded.imageModels == null || loaded.imageModels.Count == 0)
                        loaded.imageModels = DefaultImageModels();
                    if (string.IsNullOrEmpty(loaded.apiVersion)) loaded.apiVersion = "v1";
                    if (loaded.maxConcurrentRequests < 1) loaded.maxConcurrentRequests = 1;
                    VertexAIPlugin.Log.LogInfo($"[VertexAI] Loaded model catalogue from {path} " +
                                               $"({loaded.textModels.Count} text, {loaded.imageModels.Count} image).");
                    return loaded;
                }

                VertexCatalogue defaults = Defaults();
                Directory.CreateDirectory(configDir);
                File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                VertexAIPlugin.Log.LogInfo($"[VertexAI] Wrote default model catalogue to {path} — edit it to add or remove models.");
                return defaults;
            }
            catch (Exception ex)
            {
                VertexAIPlugin.Log.LogError($"[VertexAI] Could not load {path}, using built-in defaults: {ex.Message}");
                return Defaults();
            }
        }

        public List<string> TextModelLabels()
        {
            var labels = new List<string>();
            foreach (VertexTextModel m in textModels) labels.Add(m.label);
            return labels;
        }

        public List<string> ImageModelLabels()
        {
            var labels = new List<string>();
            foreach (VertexImageModel m in imageModels) labels.Add(m.label);
            return labels;
        }

        /// <summary>Index of the entry matching this model id, or 0 when unknown.</summary>
        public int IndexOfTextModel(string id)
        {
            for (int i = 0; i < textModels.Count; i++)
                if (textModels[i].id == id) return i;
            return 0;
        }

        /// <summary>Index of the entry matching this id+size pair, or 0 when unknown.</summary>
        public int IndexOfImageModel(string id, string size)
        {
            for (int i = 0; i < imageModels.Count; i++)
                if (imageModels[i].id == id && (imageModels[i].size ?? "") == (size ?? "")) return i;
            for (int i = 0; i < imageModels.Count; i++)
                if (imageModels[i].id == id) return i;
            return 0;
        }

        public VertexTextModel TextModelById(string id)
        {
            foreach (VertexTextModel m in textModels)
                if (m.id == id) return m;
            // Unknown id (hand-edited config, or a saved pref pointing at a removed entry):
            // still usable, just without a thinking override.
            return new VertexTextModel { label = id, id = id };
        }
    }
}
