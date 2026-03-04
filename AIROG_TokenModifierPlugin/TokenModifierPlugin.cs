using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace AIROG_TokenModifierPlugin
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class TokenModifierPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.tokenmodifier";
        public const string PLUGIN_NAME = "Token Modifier";
        public const string PLUGIN_VERSION = "1.0.5";

        public static TokenModifierPlugin Instance { get; private set; }

        private static ConfigEntry<int> _maxTokensStory;
        private static ConfigEntry<int> _maxTokensChat;
        private static ConfigEntry<int> _maxTokensEvent;

        private void Awake()
        {
            Instance = this;
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            // Bind configuration
            _maxTokensStory = Config.Bind("General", "MaxTokens_Story", 220, "Max tokens for story completion");
            _maxTokensChat = Config.Bind("General", "MaxTokens_Chat", 1090, "Max tokens for general chat/questions");
            _maxTokensEvent = Config.Bind("General", "MaxTokens_Event", 550, "Max tokens for event checks");


            // Apply overrides initially (still good to do for dictionary-based lookups)
            ApplyOverrides();

            // apply Harmony patches
            Harmony.CreateAndPatchAll(typeof(TokenModifierPlugin));
        }

        [HarmonyPatch(typeof(MyHttpClient), "DoHttpRequest")]
        [HarmonyPrefix]
        public static void Prefix_DoHttpRequest(ref string contentStr, bool contentIsJson)
        {
            if (!contentIsJson || string.IsNullOrEmpty(contentStr)) return;

            try
            {
                // We only care if it looks like an OpenAI request body
                if (!contentStr.Contains("max_tokens")) return;

                JObject json = JObject.Parse(contentStr);
                if (json["max_tokens"] != null)
                {
                    int currentMax = json["max_tokens"].Value<int>();
                    int newMax = currentMax;

                    // Map hardcoded values to our config
                    // 220 = Story Completer
                    // 1090 = General Question (Chat) default
                    // 550 = Event Checks
                    // 15000 = High Cost Grd (Treat as Chat for now? Or ignore. Let's map to Chat if user wants control)

                    if (currentMax == 220)
                    {
                        newMax = _maxTokensStory.Value;
                    }
                    else if (currentMax == 1090)
                    {
                        newMax = _maxTokensChat.Value;
                    }
                    else if (currentMax == 550)
                    {
                        newMax = _maxTokensEvent.Value;
                    }
                    
                    // SAFETY VALVE: If the requested tokens are high (like 15000 for grids), 
                    // and exceed our current override, we allow it to prevent truncation/crashes.
                    if (currentMax > newMax && currentMax >= 2000)
                    {
                        newMax = currentMax;
                    }

                    // --- SECURITY CHECK FOR SAPPHIRE SERVICE ---
                    if (IsSapphireModeActive())
                    {
                        int officialLimit = GetOfficialLimit(currentMax);
                        if (newMax > officialLimit)
                        {
                            if (Instance != null) {
                                Instance.Logger.LogWarning($"[Security] Attempted to set max_tokens to {newMax} for Sapphire service, but the official limit is {officialLimit}. Capping at {officialLimit}.");
                            }
                            newMax = officialLimit;
                        }
                    }

                    if (newMax != currentMax)
                    {
                        json["max_tokens"] = newMax;
                        contentStr = json.ToString(Formatting.None);
                        if (Instance != null) Instance.Logger.LogInfo($"Intercepted request. Replaced max_tokens {currentMax} -> {newMax}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Silent catch to avoid breaking the game loop if parsing fails
            }
        }

        private static bool IsSapphireModeActive()
        {
            if (SS.I == null) return false;
            // The official sapphire service uses CLOUD_AI_CHATGPT text generation mode
            return SS.I.textGenerationMode == SS.TextGenerationMode.CLOUD_AI_CHATGPT;
        }

        private static int GetOfficialLimit(int originalValue)
        {
            // Official limits based on the game's hardcoded values
            return originalValue switch
            {
                220 => 220,    // Story Completer
                1090 => 1090,  // General Question (Chat)
                550 => 550,    // Event Checks
                160 => 160,    // Char Desc
                16 => 16,      // Short Questions
                15000 => 15000, // High Cost GRD
                _ => originalValue // Keep other values as is if they don't match known types
            };
        }

        private bool _uiInjected = false;
        private GameObject _tokenSliderObj;

        private void Update()
        {
            // If we marked as injected, but the object is null (e.g. scene change destroyed it), reset flag
            if (_uiInjected && _tokenSliderObj == null)
            {
                _uiInjected = false;
            }

            if (_uiInjected) return;

            // Wait for MainMenu context
            var mainMenu = FindObjectOfType<MainMenu>();
            if (mainMenu != null)
            {
                InjectUI(mainMenu);
            }
        }

        private void InjectUI(MainMenu mainMenu)
        {
            try
            {
                Logger.LogInfo("Attempting to inject UI...");

                // Use reflection to get the ambience slider reference
                // We typically use 'musicVolumeSlider' or 'ambienceVolumeSlider'.
                // Found 'ambienceVolumeSlider' in Decompilation.
                var fieldInfo = typeof(MainMenu).GetField("ambienceVolumeSlider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fieldInfo == null)
                {
                    Logger.LogError("Could not find ambienceVolumeSlider field in MainMenu!");
                    _uiInjected = true;
                    return;
                }

                var referenceSlider = fieldInfo.GetValue(mainMenu) as UnityEngine.UI.Slider;
                if (referenceSlider == null)
                {
                    Logger.LogError("referenceSlider (ambience) is null or not a Slider!");
                    _uiInjected = true; 
                    return;
                }

                // 1. Clone/Position the Slider
                // Ambience is Row 2, Right Col. We want Row 3, Right Col.
                // So -60 offset from Ambience should be correct (assuming 60px row height).
                float yOffset = -60f;

                _tokenSliderObj = Instantiate(referenceSlider.gameObject, referenceSlider.transform.parent);
                _tokenSliderObj.name = "TokenCountSlider";

                var sliderRect = _tokenSliderObj.GetComponent<RectTransform>();
                if (sliderRect != null)
                {
                    sliderRect.anchoredPosition += new Vector2(0, yOffset);
                }

                // 2. Clone/Position the Label
                // Search for sibling label with text "Ambience"
                Transform referenceLabelTransform = null;
                foreach (Transform sibling in referenceSlider.transform.parent)
                {
                    var tmp = sibling.GetComponent<TMPro.TMP_Text>();
                    if (tmp != null && (tmp.text.Contains("Ambience") || tmp.text.Contains("Volume")))
                    {
                        referenceLabelTransform = sibling;
                        break;
                    }
                    var legacyText = sibling.GetComponent<UnityEngine.UI.Text>();
                    if (legacyText != null && (legacyText.text.Contains("Ambience") || legacyText.text.Contains("Volume")))
                    {
                        referenceLabelTransform = sibling;
                        break;
                    }
                }

                if (referenceLabelTransform != null)
                {
                    var newTokenLabel = Instantiate(referenceLabelTransform.gameObject, referenceLabelTransform.parent);
                    newTokenLabel.name = "TokenCountLabel";
                    var labelRect = newTokenLabel.GetComponent<RectTransform>();
                    if (labelRect != null)
                    {
                        labelRect.anchoredPosition += new Vector2(0, yOffset);
                    }
                    
                    // Strip localization/other scripts that might override text
                    var components = newTokenLabel.GetComponents<MonoBehaviour>();
                    foreach (var c in components)
                    {
                        // Don't destroy the text component itself, but destroy potential localization scripts
                        if (c is TMPro.TMP_Text || c is UnityEngine.UI.Text) continue;
                        DestroyImmediate(c); 
                    }

                    // Set Text
                    var tmps = newTokenLabel.GetComponentsInChildren<TMPro.TMP_Text>();
                    foreach (var t in tmps) t.text = "Token Count";
                    
                    var legs = newTokenLabel.GetComponentsInChildren<UnityEngine.UI.Text>();
                    foreach (var l in legs) l.text = "Token Count";
                }
                else
                {
                    Logger.LogWarning("Could not find Ambience Volume label to clone.");
                }

                // 3. Configure the Slider
                var newSlider = _tokenSliderObj.GetComponent<UnityEngine.UI.Slider>();
                
                newSlider.minValue = 100;
                newSlider.maxValue = 4000;
                newSlider.value = _maxTokensChat.Value;

                newSlider.onValueChanged.RemoveAllListeners();
                newSlider.onValueChanged.AddListener((val) =>
                {
                    int intVal = Mathf.RoundToInt(val);
                    _maxTokensChat.Value = intVal;
                    _maxTokensStory.Value = intVal;
                    _maxTokensEvent.Value = intVal;
                    ApplyOverrides(); 
                });

                _uiInjected = true;
                Logger.LogInfo("UI Injected and Positioned Successfully!");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to inject UI: {ex}");
                _uiInjected = true;
            }
        }

        private void UpdateLabel(GameObject sliderObj, int val)
        {
            // Deprecated: We now clone the main label properly. 
            // Leaving empty to satisfy any lingering calls or remove if unused.
        }

            // If the original game uses a specific label update method, we might miss it.
            // But modifying the children text is a good best-effort.
            private void ApplyOverrides()
        {
            if (SS.I == null)
            {
                Logger.LogError("SS.I is null. Cannot apply overrides yet.");
                return;
            }

            Logger.LogInfo("Applying Token Count Overrides...");

            // Helper to set max_tokens for a specific prompt type
            void SetMaxTokens(AIAsker.ChatGptPromptType type, int tokens)
            {
                if (!SS.I.moddableOpenaiapiCommonParams.ContainsKey(type) || SS.I.moddableOpenaiapiCommonParams[type] == null)
                {
                    SS.I.moddableOpenaiapiCommonParams[type] = new JObject();
                }

                // Security check for Sapphire service
                if (IsSapphireModeActive())
                {
                    int defaultLimit = type switch
                    {
                        AIAsker.ChatGptPromptType.STORY_COMPLETER => 220,
                        AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER => 1090,
                        AIAsker.ChatGptPromptType.EVENT_CHECKS_ANSWERER => 550,
                        _ => 1090
                    };

                    if (tokens > defaultLimit)
                    {
                        tokens = defaultLimit;
                        Logger.LogWarning($"[Security] Capped {type} max_tokens to {defaultLimit} for Sapphire service.");
                    }
                }

                // We NO LONGER set max_tokens here, to avoid overriding high-cost modes (like GRD) 
                // that might be misclassified as Chat or Story early on.
                // The override is now handled exclusively in Prefix_DoHttpRequest.
                // SS.I.moddableOpenaiapiCommonParams[type]["max_tokens"] = tokens;
                // Logger.LogInfo($"Set {type} max_tokens to {tokens}");
            }

            // Apply to specific types
            SetMaxTokens(AIAsker.ChatGptPromptType.STORY_COMPLETER, _maxTokensStory.Value);
            SetMaxTokens(AIAsker.ChatGptPromptType.GENERAL_QUESTION_ANSWERER, _maxTokensChat.Value);
            SetMaxTokens(AIAsker.ChatGptPromptType.EVENT_CHECKS_ANSWERER, _maxTokensEvent.Value);

            // Also apply to NONE as a fallback/base if needed, though specific types usually override
            // SetMaxTokens(AIAsker.ChatGptPromptType.NONE, _maxTokensChat.Value);

            Logger.LogInfo("Overrides Applied Successfully.");
        
        }
    }
}

