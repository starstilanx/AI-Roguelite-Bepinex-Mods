using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using TMPro;

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
                    // 160 = Char Desc
                    // 16 = Obvious Question Short (keep small)

                    if (currentMax == 220)
                    {
                        newMax = _maxTokensStory.Value;
                    }
                    else if (currentMax == 1090 || currentMax == 15000)
                    {
                        newMax = _maxTokensChat.Value;
                    }
                    else if (currentMax == 550)
                    {
                        newMax = _maxTokensEvent.Value;
                    }
                    // Optional: Map 160 to Chat or Story if desired, but 160 is specific for Char Desc.
                    // Leaving others alone to avoid breaking specific logic.

                    if (newMax != currentMax)
                    {
                        json["max_tokens"] = newMax;
                        contentStr = json.ToString(Newtonsoft.Json.Formatting.None);
                        if (Instance != null)
                        {
                            Instance.Logger.LogInfo($"[Harmony] Intercepted request. Replaced max_tokens {currentMax} -> {newMax}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Silent catch to avoid breaking the game loop if parsing fails
                /*
                if (Instance != null) {
                    Instance.Logger.LogError($"[Harmony] Error parsing JSON: {ex.Message}");
                }
                */
            }
        }

        private bool _uiInjected = false;
        private GameObject _tokenInputObj;

        private void Update()
        {
            // If we marked as injected, but the object is null (e.g. scene change destroyed it), reset flag
            if (_uiInjected && _tokenInputObj == null)
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

                // 1. Get references
                // We use ambienceVolumeSlider for positioning
                var sliderField = typeof(MainMenu).GetField("ambienceVolumeSlider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (sliderField == null)
                {
                    Logger.LogError("Could not find ambienceVolumeSlider field in MainMenu!");
                    _uiInjected = true;
                    return;
                }

                var referenceSlider = sliderField.GetValue(mainMenu) as UnityEngine.UI.Slider;
                if (referenceSlider == null)
                {
                    Logger.LogError("referenceSlider (ambience) is null or not a Slider!");
                    _uiInjected = true;
                    return;
                }

                // We use customerKeyTxtInput as a reference for the InputField look and feel
                var referenceInput = mainMenu.customerKeyTxtInput;
                if (referenceInput == null)
                {
                    Logger.LogError("customerKeyTxtInput is null on MainMenu!");
                    _uiInjected = true;
                    return;
                }

                // 2. Clone/Position the InputField
                // Ambience is Row 2, Right Col. We want Row 3, Right Col.
                float yOffset = -60f;

                _tokenInputObj = Instantiate(referenceInput.gameObject, referenceSlider.transform.parent);
                _tokenInputObj.name = "TokenCountInput";

                var inputRect = _tokenInputObj.GetComponent<RectTransform>();
                if (inputRect != null && referenceSlider.GetComponent<RectTransform>() != null)
                {
                    var refSliderRect = referenceSlider.GetComponent<RectTransform>();
                    
                    // Match position and size of the slider area if possible, 
                    // or just use the cloned input's defaults but anchored correctly.
                    inputRect.anchorMin = refSliderRect.anchorMin;
                    inputRect.anchorMax = refSliderRect.anchorMax;
                    inputRect.pivot = refSliderRect.pivot;
                    inputRect.anchoredPosition = refSliderRect.anchoredPosition + new Vector2(0, yOffset);
                    inputRect.sizeDelta = new Vector2(refSliderRect.sizeDelta.x, inputRect.sizeDelta.y);
                }

                // 3. Clone/Position the Label
                Transform referenceLabelTransform = null;
                foreach (Transform sibling in referenceSlider.transform.parent)
                {
                    var tmp = sibling.GetComponent<TMP_Text>();
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

                    // Strip potential localization scripts
                    var components = newTokenLabel.GetComponents<MonoBehaviour>();
                    foreach (var c in components)
                    {
                        if (c is TMP_Text || c is UnityEngine.UI.Text) continue;
                        DestroyImmediate(c);
                    }

                    // Set Text
                    var tmps = newTokenLabel.GetComponentsInChildren<TMP_Text>();
                    foreach (var t in tmps) t.text = "Max Tokens (Story/Chat)";

                    var legs = newTokenLabel.GetComponentsInChildren<UnityEngine.UI.Text>();
                    foreach (var l in legs) l.text = "Max Tokens (Story/Chat)";
                }

                // 4. Configure the InputField
                var newInput = _tokenInputObj.GetComponent<TMP_InputField>();
                
                // CRITICAL: Clear all persistent and non-persistent listeners by assigning fresh event objects.
                // Cloned objects retain their inspector-set (persistent) listeners, which in this case 
                // link back to the MainMenu's key-handling logic.
                newInput.onValueChanged = new TMP_InputField.OnChangeEvent();
                newInput.onEndEdit = new TMP_InputField.SubmitEvent();
                newInput.onSelect = new TMP_InputField.SelectionEvent();
                newInput.onDeselect = new TMP_InputField.SelectionEvent();

                // Strip any scripts that aren't part of Unity/TMPro to avoid side effects
                var behaviours = _tokenInputObj.GetComponents<MonoBehaviour>();
                foreach (var b in behaviours)
                {
                    if (b == null || b is TMP_InputField) continue;
                    var type = b.GetType();
                    if (!type.Namespace.StartsWith("UnityEngine") && !type.Namespace.StartsWith("TMPro"))
                    {
                        Logger.LogInfo($"Removing script {type.Name} from cloned input field.");
                        DestroyImmediate(b);
                    }
                }

                newInput.contentType = TMP_InputField.ContentType.IntegerNumber;
                newInput.characterLimit = 5;
                newInput.text = _maxTokensChat.Value.ToString();

                newInput.onEndEdit.AddListener((val) =>
                {
                    if (int.TryParse(val, out int intVal))
                    {
                        intVal = Mathf.Clamp(intVal, 10, 32000); 
                        _maxTokensChat.Value = intVal;
                        _maxTokensStory.Value = intVal;
                        _maxTokensEvent.Value = intVal;
                        ApplyOverrides();
                        newInput.text = intVal.ToString();
                        Logger.LogInfo($"Token count updated to: {intVal}");
                    }
                    else
                    {
                        newInput.text = _maxTokensChat.Value.ToString();
                    }
                });

                _uiInjected = true;
                Logger.LogInfo("UI Injected with InputField Successfully!");
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

                SS.I.moddableOpenaiapiCommonParams[type]["max_tokens"] = tokens;
                Logger.LogInfo($"Set {type} max_tokens to {tokens}");
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

