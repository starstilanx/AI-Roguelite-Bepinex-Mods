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
        public const string PLUGIN_VERSION = "1.0.6";

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
            _maxTokensStory = Config.Bind("General", "MaxTokens_Story", 220,
                "Raise the token limit of story-completion requests (specialized pipeline) to at least this. " +
                "Never lowers what the game itself asks for.");
            _maxTokensChat = Config.Bind("General", "MaxTokens_Chat", 1090,
                "Raise the token limit of general / unified-turn / world-generation requests to at least this. " +
                "Never lowers what the game itself asks for.");
            _maxTokensEvent = Config.Bind("General", "MaxTokens_Event", 550,
                "Raise the token limit of event-check requests to at least this. " +
                "Never lowers what the game itself asks for.");

            // apply Harmony patches
            Harmony.CreateAndPatchAll(typeof(TokenModifierPlugin));
        }

        /// <summary>
        /// Maps the limit the game put in a request to the limit that should be sent.
        ///
        /// The request body does not say which kind of call it is, so the kind is inferred from
        /// the value OpenaiApiClient hard-codes for it (11/18 build):
        ///   16 / 160   short answers, character descriptions - left alone
        ///   260        story completion (220 in old builds)
        ///   1000       event checks (550 in old builds)
        ///   3000       general, 5000 unified turn, 15000 grid/world generation
        ///
        /// A setting only ever RAISES the game's value. The settings' defaults date from when the
        /// game asked for 220/1090/550; it has since moved to 260/3000-5000/1000, and replacing
        /// outright meant an untouched config cut every unified turn from 5000 tokens to 1090
        /// (truncated JSON). It also means a zero or negative setting can never reach the API.
        /// </summary>
        internal static int ResolveMaxTokens(int gameMax, int story, int evt, int chat)
        {
            int wanted;
            if (gameMax >= 200 && gameMax <= 400) wanted = story;
            else if (gameMax >= 450 && gameMax <= 1050) wanted = evt;
            else if (gameMax > 1050) wanted = chat;
            else return gameMax;

            return System.Math.Max(gameMax, wanted);
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
                    int newMax = ResolveMaxTokens(currentMax, _maxTokensStory.Value, _maxTokensEvent.Value, _maxTokensChat.Value);

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

            // Wait for MainMenu context. MainMenu registers itself here on Start and GameplayManager
            // clears it when a save starts, so this stays a cheap check for the whole play session
            // where FindObjectOfType scanned every loaded object every frame.
            var mainMenu = SS.I != null ? SS.I.hackyMenu : null;
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
                    // The game's own scripts - the ones this is meant to remove - live in the
                    // global namespace, where Type.Namespace is null.
                    string ns = type.Namespace ?? "";
                    if (!ns.StartsWith("UnityEngine") && !ns.StartsWith("TMPro"))
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

        // No per-prompt-type override is written into SS.I.moddableOpenaiapiCommonParams any more.
        // Those entries replaced the game's value before the request reached the prefix above,
        // which then saw only the configured number: it could no longer tell what the game had
        // asked for (so could not avoid lowering it), and re-sorted the configured number into
        // whichever range it happened to fall in - MaxTokens_Story = 1000 was sent as
        // MaxTokens_Chat. Every request passes through the prefix, so it is the single place
        // the limit is adjusted.
    }
}

