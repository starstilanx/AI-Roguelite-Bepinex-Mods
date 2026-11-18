using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace AIROG_GenContext
{
    public static class GenContextUI
    {
        private static GameObject _configButtonObj;
        private static GameObject _dmButtonObj;
        private static bool _switchEventsHooked;

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts_InitCommonAnchs
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance)
            {
                // InitCommonAnchs runs inside the game's startup; a failure here must not stop it.
                try { InjectButton(__instance); }
                catch (Exception ex) { Debug.LogError($"[GenContext] Button injection failed: {ex}"); }
            }
        }

        /// <summary>
        /// Shows the DM button only while DM Notes is on (global setting and this save's switch),
        /// and closes the panel when it goes off. UI only - safe as a ModToggles event handler.
        /// </summary>
        internal static void ApplyDmNotesState()
        {
            bool active = DMNotes.DmNotesManager.IsActive;
            if (_dmButtonObj != null) _dmButtonObj.SetActive(active);
            if (!active) DMNotes.DmNotesPanel.Close();
        }

        private static void InjectButton(MainLayouts layout)
        {
            if (layout == null || layout.buttonsHolderHolder == null) return;

            // Don't inject twice in the same scene
            if (layout.buttonsHolderHolder.Find("GenContextConfigBtn") != null) return;

            _configButtonObj = new GameObject("GenContextConfigBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            _configButtonObj.transform.SetParent(layout.buttonsHolderHolder, false);

            var img = _configButtonObj.GetComponent<Image>();

            Sprite btnSprite = LoadSprite("ModButton.png");
            if (btnSprite != null)
            {
                img.sprite = btnSprite;
                img.color = Color.white;
                img.preserveAspect = true;
            }
            else
            {
                img.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);

                var textObj = new GameObject("Text", typeof(RectTransform));
                textObj.transform.SetParent(_configButtonObj.transform, false);
                var txt = textObj.AddComponent<TextMeshProUGUI>();
                txt.text = "Mods";
                txt.fontSize = 12;
                txt.color = Color.white;
                txt.alignment = TextAlignmentOptions.Center;
                txt.enableWordWrapping = false;
                txt.enableAutoSizing = true;

                var textRect = textObj.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
            }

            // LayoutElement so HorizontalLayoutGroup sizes the button correctly
            LayoutElement le = _configButtonObj.AddComponent<LayoutElement>();
            le.preferredWidth = 60;
            le.preferredHeight = 60;
            le.minWidth = 60;
            le.minHeight = 60;

            var btn = _configButtonObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => OpenConfigMenu());

            _configButtonObj.transform.SetAsLastSibling();
            Debug.Log("[GenContext] Injected Config Button.");

            // DM Notes toggle button
            if (layout.buttonsHolderHolder.Find("DmNotesToggleBtn") == null)
            {
                var dmBtnObj = new GameObject("DmNotesToggleBtn", typeof(RectTransform), typeof(Image), typeof(Button));
                dmBtnObj.transform.SetParent(layout.buttonsHolderHolder, false);

                var dmImg = dmBtnObj.GetComponent<Image>();
                Sprite dmSprite = LoadSprite("DMNotesButton.png");
                if (dmSprite != null)
                {
                    dmImg.sprite = dmSprite;
                    dmImg.color = Color.white;
                    dmImg.preserveAspect = true;
                }
                else
                {
                    dmImg.color = new Color(0.1f, 0.15f, 0.3f, 0.9f);
                    var dmLabel = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                    dmLabel.transform.SetParent(dmBtnObj.transform, false);
                    var dmTxt = dmLabel.GetComponent<TextMeshProUGUI>();
                    dmTxt.text = "DM"; dmTxt.fontSize = 13; dmTxt.fontStyle = FontStyles.Bold;
                    dmTxt.color = new Color(0.95f, 0.85f, 0.5f);
                    dmTxt.alignment = TextAlignmentOptions.Center;
                    var dmLabelRect = dmLabel.GetComponent<RectTransform>();
                    dmLabelRect.anchorMin = Vector2.zero; dmLabelRect.anchorMax = Vector2.one;
                    dmLabelRect.offsetMin = dmLabelRect.offsetMax = Vector2.zero;
                }

                var dmLe = dmBtnObj.AddComponent<LayoutElement>();
                dmLe.preferredWidth = 60; dmLe.minWidth = 60;
                dmLe.preferredHeight = 60; dmLe.minHeight = 60;

                var dmBtn = dmBtnObj.GetComponent<Button>();
                dmBtn.targetGraphic = dmImg;
                dmBtn.onClick.AddListener(() => DMNotes.DmNotesPanel.Toggle());
                dmBtnObj.transform.SetAsLastSibling();
                _dmButtonObj = dmBtnObj;
                Debug.Log("[GenContext] Injected DM Notes button.");
            }

            if (!_switchEventsHooked)
            {
                _switchEventsHooked = true;
                ModToggles.Changed += (key, enabled) => { if (key == ModToggles.Keys.DmNotes) ApplyDmNotesState(); };
                ModToggles.Reloaded += ApplyDmNotesState;
            }
            ApplyDmNotesState();
        }

        private static Sprite LoadSprite(string filename)
        {
            try
            {
                string path = System.IO.Path.Combine(Application.streamingAssetsPath, "GenContext", filename);
                if (System.IO.File.Exists(path))
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    Texture2D tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                    {
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                }
                else
                {
                    Debug.LogWarning($"[GenContext] Button icon not found at {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] Failed to load button icon: {ex}");
            }
            return null;
        }

        private static void OpenConfigMenu()
        {
            Debug.Log("[GenContext] Opening Config Menu");
            var modal = NTextPromptModal.I;
            if (modal == null) return;

            try
            {
                Type modalType = modal.GetType();
                Type toggleType = modalType.GetNestedType("TogglePromptArg");

                if (toggleType == null)
                {
                    Debug.LogError("[GenContext] Could not find TogglePromptArg type!");
                    return;
                }

                Type promptArgType = modalType.GetNestedType("PromptArg");
                Type listType = typeof(List<>).MakeGenericType(promptArgType);
                IList argList = (IList)Activator.CreateInstance(listType);

                // Mod master switches, stored per save (see ModToggles). One per context provider,
                // plus any mod that registered a switch without having a provider.
                var switchNames = new List<string>();
                foreach (var provider in ContextManager.GetProviders())
                    if (!switchNames.Contains(provider.Name)) switchNames.Add(provider.Name);
                foreach (var extra in ModToggles.ExtraKeys())
                    if (!switchNames.Contains(extra)) switchNames.Add(extra);

                foreach (var switchName in switchNames)
                {
                    bool isEnabled = ModToggles.IsEnabled(switchName);
                    string name = switchName;

                    Action<bool> onToggle = (val) => {
                        // The modal calls every toggle's callback on Confirm, changed or not. Only
                        // write real changes: an unconditional write would pin every mod's current
                        // value into this save and stop it following the global default.
                        if (ModToggles.IsEnabled(name) == val) return;
                        ModToggles.SetEnabled(name, val);
                        Debug.Log($"[GenContext] Toggled {name} to {val} for this save");

                        if (name == Integration.SettlementIntegration.PROVIDER_NAME)
                            Integration.SettlementIntegration.ApplyState(val);
                    };

                    argList.Add(Activator.CreateInstance(toggleType, new object[] { name, isEnabled, onToggle }));
                }

                // Runs after the switches above (callbacks fire in list order), so it copies the
                // values the player just confirmed.
                {
                    Action<bool> onToggle = (val) => {
                        if (!val) return;
                        ModToggles.SaveCurrentAsDefaults();
                        Debug.Log("[GenContext] Saved this save's mod switches as the default for other saves.");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "  └ Also use the switches above as the default for my other saves", false, onToggle }));
                }

                // Video Gen Fix
                {
                    bool isEnabled = GenContextPlugin.EnableVideoGenFix.Value;
                    Action<bool> onToggle = (val) => {
                        GenContextPlugin.EnableVideoGenFix.Value = val;
                        Debug.Log($"[GenContext] Toggled Video Gen Fix to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "Fix Video Gen Crash", isEnabled, onToggle }));
                }

                // Nemesis System
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("NemesisSystem");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("NemesisSystem", val);
                        Debug.Log($"[GenContext] Toggled Nemesis System to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "Nemesis System (Master Switch)", isEnabled, onToggle }));
                }

                // Nemesis Looting
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("NemesisLooting");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("NemesisLooting", val);
                        Debug.Log($"[GenContext] Toggled Nemesis Looting to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "  \u2514 Loot Player on Death", isEnabled, onToggle }));
                }

                // Disable Truncation
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("DisableTruncation");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("DisableTruncation", val);
                        Debug.Log($"[GenContext] Toggled Disable Truncation to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "Disable Context Truncation (Unlimited Tokens)", isEnabled, onToggle }));
                }

                // DM Notes
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("DMNotes");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("DMNotes", val);
                        Debug.Log($"[GenContext] Toggled DM Notes to {val}");
                        ApplyDmNotesState();
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "DM Notes (AI Director Layer)", isEnabled, onToggle }));
                }

                // RR Compat Mode
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("RRCompat");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("RRCompat", val);
                        Debug.Log($"[GenContext] Toggled RR Compat Mode to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "RR Compat Mode (Reactive Realms + UNIFIED)", isEnabled, onToggle }));
                }

                // Find PresentSelf by name (handles any number of parameters)
                MethodInfo presentMethod = null;
                foreach (var m in modalType.GetMethods())
                {
                    if (m.Name == "PresentSelf" && m.GetParameters().Length >= 1)
                    {
                        presentMethod = m;
                        break;
                    }
                }

                if (presentMethod != null)
                {
                    var pParams = presentMethod.GetParameters();
                    object[] args = new object[pParams.Length];
                    args[0] = argList;
                    // NOTE: AIROG_RandomOrg recognises this modal by this exact title to append its
                    // own toggles - do not reword it without updating RandomOrgPlugin.
                    if (pParams.Length > 1) args[1] = "GenContext Mod Manager";

                    // Fill remaining params: use default values for value types, null for reference types
                    for (int i = 2; i < pParams.Length; i++)
                    {
                        if (pParams[i].HasDefaultValue)
                            args[i] = pParams[i].DefaultValue;
                        else if (pParams[i].ParameterType.IsValueType)
                            args[i] = Activator.CreateInstance(pParams[i].ParameterType);
                        // else leave null for Action, Func<bool>, etc.
                    }

                    presentMethod.Invoke(modal, args);
                }
                else
                {
                    Debug.LogError("[GenContext] PresentSelf method not found!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GenContext] Error showing menu: {ex}");
            }
        }
    }
}
