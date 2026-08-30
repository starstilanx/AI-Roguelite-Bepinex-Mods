using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Generates a small transparent icon per unlocked Notable/Keystone/Anchor/Confluence node
    /// (Basic nodes stay icon-less — there are too many to justify a call each). Prefers
    /// AIROG_NanoBanana (better Gemini Imagen quality) if it's installed and the player has
    /// configured an API key for it, reached only via reflection so SkillWeb has no hard
    /// dependency on that mod. Otherwise routes through the player's configured image backend
    /// (SS.I.imageGenerationMode — local A1111/ComfyUI, Stable Horde, NovelAI, Wombo), the
    /// same dispatch the base game does in AIAsker.getGeneratedSprite. Only Sapphire/free-tier
    /// players hit the game's cloud endpoint, which needs no API key.
    /// </summary>
    public static class NodeIconGen
    {
        static string IconDir => Path.Combine(Path.GetDirectoryName(SkillWebPlugin.GetSavePath()), "icons");

        // A single generation holds the backend's image semaphore for up to a minute (ComfyUI/A1111),
        // but SkillWebUI.Refresh() — which re-triggers EnsureIconAsync for every visible node — fires
        // dozens of times before any gen finishes (each icon completion itself calls Refresh). Without
        // a guard, the same handful of nodes get re-queued on every Refresh, ballooning the backend
        // queue into an endless re-generation of the same icons and starving the game's own image gens.
        // These two sets de-dupe: _inFlight blocks a node that's already generating; _failedUntil backs
        // off a node whose gen failed so a permanently-failing node doesn't re-queue on every Refresh.
        // Concurrent collections: a continuation resuming off Unity's main thread (some native backend
        // clients aren't guaranteed to hop back) must not be able to corrupt these against a concurrent
        // EnsureIconAsync call on the main thread.
        static readonly ConcurrentDictionary<string, byte> _inFlight = new ConcurrentDictionary<string, byte>();
        static readonly ConcurrentDictionary<string, float> _failedUntil = new ConcurrentDictionary<string, float>();
        const float FailCooldownSeconds = 300f;

        /// <summary>
        /// Clears in-flight/cooldown bookkeeping. Node ids for Anchors are deterministic
        /// ("anchor:"+perkUuid), so without this a failure on one save's icon would leave a
        /// same-perk node on a different save silently cooling down too. Call on every load.
        /// </summary>
        public static void ClearRuntimeState()
        {
            _inFlight.Clear();
            _failedUntil.Clear();
        }

        /// <summary>Node ids can contain ':' (Anchors are "anchor:&lt;perkUuid&gt;"), which is illegal in a Windows filename.</summary>
        static string SafeFileName(string nodeId) => nodeId.Replace(':', '_');

        public static string GetIconPath(WebNode node) => Path.Combine(IconDir, SafeFileName(node.id) + ".png");

        public static bool HasIcon(WebNode node) => File.Exists(GetIconPath(node));

        /// <summary>Fire-and-forget: generates (if missing and eligible) an icon for this node.</summary>
        public static void EnsureIconAsync(WebNode node)
        {
            if (node == null || node.type == WebNodeType.Basic || HasIcon(node)) return;

            // Already generating this node, or it failed recently — don't pile another request onto
            // the backend queue. Refresh() calls this for every visible node many times per second.
            if (_inFlight.ContainsKey(node.id)) return;
            if (_failedUntil.TryGetValue(node.id, out float until) && Time.realtimeSinceStartup < until) return;

            _inFlight[node.id] = 0;
            _ = GenerateAsync(node);
        }

        static async Task GenerateAsync(WebNode node)
        {
            bool succeeded = false;
            bool skippedDisabled = false;
            try
            {
                Directory.CreateDirectory(IconDir);
                string prompt = BuildPrompt(node);
                string pathNoExt = Path.Combine(IconDir, SafeFileName(node.id));

                bool ok = await TryNanoBanana(prompt, pathNoExt);
                if (!ok)
                {
                    if (SS.I.imageGenerationMode == SS.ImageGenerationMode.DISABLED)
                    {
                        // Player turned image generation off — don't warn every refresh, and don't
                        // treat this as a generation failure either: no attempt was actually made,
                        // so re-enabling a backend should let this node retry immediately.
                        skippedDisabled = true;
                        return;
                    }
                    ok = await TryNativeBackend(prompt, pathNoExt);
                }

                if (!ok)
                {
                    Debug.LogWarning($"[SkillWeb] Icon generation failed for node '{node.name}'.");
                    return;
                }

                succeeded = true;
                if (SkillWebUI.Instance != null && SkillWebUI.Instance.gameObject.activeSelf)
                    SkillWebUI.Instance.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] NodeIconGen exception: " + ex.Message);
            }
            finally
            {
                // Guarded: if this continuation resumed off Unity's main thread, Time.realtimeSinceStartup
                // itself can throw here — letting that escape a finally block would fault this fire-and-forget
                // Task as an unobserved exception and silently skip setting the cooldown below.
                try
                {
                    _inFlight.TryRemove(node.id, out _);
                    if (succeeded || skippedDisabled) _failedUntil.TryRemove(node.id, out _);
                    else _failedUntil[node.id] = Time.realtimeSinceStartup + FailCooldownSeconds;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[SkillWeb] NodeIconGen cleanup exception: " + ex.Message);
                }
            }
        }

        static string BuildPrompt(WebNode node)
        {
            string kind = node.type switch
            {
                WebNodeType.Keystone => "a powerful mythic rune-sigil",
                WebNodeType.Anchor => "a glowing anchor-star emblem",
                WebNodeType.Confluence => "a fused dual-toned emblem",
                _ => "a small passive-skill emblem",
            };
            return $"A single centered icon: {kind} representing '{node.name}' ({node.description}). " +
                   "Flat vector game-icon style, high contrast, no text, no lettering, isolated on a plain solid white background, no shadow.";
        }

        // ── Preferred: AIROG_NanoBanana, reached only via reflection (soft dependency) ──────────

        static async Task<bool> TryNanoBanana(string prompt, string pathNoExt)
        {
            try
            {
                var pluginType = Type.GetType("AIROG_NanoBanana.NanoBananaPlugin, AIROG_NanoBanana");
                if (pluginType == null) return false; // mod not installed

                object instance = pluginType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) return false; // mod installed but not loaded

                string apiKey = pluginType.GetProperty("GeminiApiKey")?.GetValue(instance) as string;
                string model = pluginType.GetProperty("GeminiModel")?.GetValue(instance) as string;
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model)) return false; // no key configured

                return await RequestGeminiImage(apiKey, model, prompt, pathNoExt);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SkillWeb] NanoBanana icon path failed, falling back: " + ex.Message);
                return false;
            }
        }

        static async Task<bool> RequestGeminiImage(string apiKey, string model, string prompt, string pathNoExt)
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            var body = new JObject
            {
                ["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray { "IMAGE" },
                    ["thinkingConfig"] = new JObject { ["thinkingLevel"] = "minimal" },
                },
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JArray { new JObject { ["text"] = prompt } },
                    },
                },
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Add("x-goog-api-key", apiKey);
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

                using (var resp = await AIROG_Core.SingletonHttpClient.Instance.SendAsync(req))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) return false;

                    var parts = JObject.Parse(text)["candidates"]?[0]?["content"]?["parts"];
                    if (parts == null) return false;

                    foreach (var part in parts)
                    {
                        if (part["thought"]?.Value<bool>() == true) continue;
                        string base64 = part["inlineData"]?["data"]?.ToString();
                        if (string.IsNullOrEmpty(base64)) continue;

                        File.WriteAllBytes(pathNoExt + ".png", Convert.FromBase64String(base64));

                        // Gemini can't return transparency and often ignores the "white background"
                        // instruction entirely, inventing a solid colour instead — so we don't try to
                        // key it here. SkillWebUI.KnockOutBackground removes whatever background the
                        // model actually produced (any colour) when the icon is loaded for display.
                        return true;
                    }
                    return false;
                }
            }
        }

        // ── Fallback: whichever image backend the player configured in game settings ───────────

        /// <summary>
        /// Mirrors the base game's provider dispatch (AIAsker.getGeneratedSprite). Every client
        /// tolerates a null GameEntity/ImgGenInfo with an explicit output path — it's the same
        /// shape the game uses for video keyframes. Sapphire players are kept on the free cloud
        /// endpoint so mod icons never consume paid generations.
        /// </summary>
        static async Task<bool> TryNativeBackend(string prompt, string pathNoExt)
        {
            try
            {
                // Steps/negative-prompt come from the player's own item-image settings; icons
                // are square, so use the smaller configured dimension for both sides.
                int size = 512, iter = 20;
                string negPr = null;
                var settings = SS.I.settingsPojo?.GetEntImgSettings(SettingsPojo.EntImgType.ITEM);
                if (settings != null)
                {
                    size = Math.Max(64, Math.Min(settings.x, settings.y));
                    iter = settings.iter;
                    negPr = settings.negPr;
                }

                GameEntity.ImgGenState state;
                switch (SS.I.imageGenerationMode)
                {
                    case SS.ImageGenerationMode.LOCAL_STABLE_DIFFUSION:
                        state = await PipeClient.I.generateImageViaStableDiffusion2(
                            iter, size, size, null, null, prompt, negPr, pathNoExt);
                        break;
                    case SS.ImageGenerationMode.COMFYUI_API:
                        state = await ComfyuiApiClient.I.generateImageViaComfyUi(
                            iter, size, size, null, null, prompt, negPr, pathNoExt);
                        break;
                    case SS.ImageGenerationMode.STABLE_HORDE:
                        state = await StableHordeClient.I.GenerateImage(
                            null, null, prompt, size, size, iter, pathNoExt);
                        break;
                    case SS.ImageGenerationMode.NOVELAI:
                        state = await NovelaiClient.I.GenerateImageV2(
                            null, null, prompt, negPr, size, size, iter, SS.I.novelAiImgGenModel, pathNoExt);
                        break;
                    case SS.ImageGenerationMode.WOMBO:
                        state = await WomboClient.I.GenerateImage(
                            null, null, Utils.TruncateToCharLimitUpToLastWord(prompt, 350), pathNoExt);
                        break;
                    default: // SAPPHIRE, AIRL_FREE
                        state = await FirebaseClient.I.GenerateImageFree(
                            gameEntity: null, imgGenInfo: null, prompt: prompt, negPrompt: null,
                            spAspectRatio: 1f, removeBg: true, imgPathNoExt: pathNoExt);
                        break;
                }
                return state == GameEntity.ImgGenState.FINISHED;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SkillWeb] Native image-gen fallback failed: " + ex.Message);
                return false;
            }
        }
    }
}
