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

        [HarmonyPatch(typeof(MainLayouts), "InitCommonAnchs")]
        public static class Patch_MainLayouts_InitCommonAnchs
        {
            [HarmonyPostfix]
            public static void Postfix(MainLayouts __instance)
            {
                InjectButton(__instance);
            }
        }

        private static void InjectButton(MainLayouts layout)
        {
            if (layout == null || layout.buttonsHolderHolder == null) return;

            // Don't inject twice in the same scene
            if (layout.buttonsHolderHolder.Find("GenContextConfigBtn") != null) return;

            _configButtonObj = new GameObject("GenContextConfigBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            _configButtonObj.transform.SetParent(layout.buttonsHolderHolder, false);

            var img = _configButtonObj.GetComponent<Image>();

            Sprite btnSprite = LoadSprite();
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
                dmBtnObj.GetComponent<Image>().color = new Color(0.1f, 0.15f, 0.3f, 0.9f);

                var dmLe = dmBtnObj.AddComponent<LayoutElement>();
                dmLe.preferredWidth = 60; dmLe.minWidth = 60;
                dmLe.preferredHeight = 60; dmLe.minHeight = 60;

                var dmLabel = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                dmLabel.transform.SetParent(dmBtnObj.transform, false);
                var dmTxt = dmLabel.GetComponent<TextMeshProUGUI>();
                dmTxt.text = "DM"; dmTxt.fontSize = 13; dmTxt.fontStyle = FontStyles.Bold;
                dmTxt.color = new Color(0.95f, 0.85f, 0.5f);
                dmTxt.alignment = TextAlignmentOptions.Center;
                var dmLabelRect = dmLabel.GetComponent<RectTransform>();
                dmLabelRect.anchorMin = Vector2.zero; dmLabelRect.anchorMax = Vector2.one;
                dmLabelRect.offsetMin = dmLabelRect.offsetMax = Vector2.zero;

                dmBtnObj.GetComponent<Button>().onClick.AddListener(() => DMNotes.DmNotesPanel.Toggle());
                dmBtnObj.transform.SetAsLastSibling();
                Debug.Log("[GenContext] Injected DM Notes button.");
            }
        }

        private static Sprite LoadSprite()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.streamingAssetsPath, "GenContext", "ModButton.png");
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
            var modal = MainMenu.NTextPromptModal();
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

                // Context provider toggles
                var providers = ContextManager.GetProviders();
                foreach (var provider in providers)
                {
                    bool isEnabled = ContextManager.IsProviderEnabled(provider.Name);
                    string name = provider.Name;

                    Action<bool> onToggle = (val) => {
                        ContextManager.SetProviderEnabled(name, val);
                        Debug.Log($"[GenContext] Toggled {name} to {val}");

                        if (name == Integration.SettlementIntegration.PROVIDER_NAME)
                            Integration.SettlementIntegration.ApplyState(val);
                    };

                    argList.Add(Activator.CreateInstance(toggleType, new object[] { name, isEnabled, onToggle }));
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

                // Skill Web
                {
                    bool isEnabled = ContextManager.GetGlobalSetting("AffixAsStatusEffect");
                    Action<bool> onToggle = (val) => {
                        ContextManager.SetGlobalSetting("AffixAsStatusEffect", val);
                        Debug.Log($"[GenContext] Toggled AffixAsStatusEffect to {val}");
                    };
                    argList.Add(Activator.CreateInstance(toggleType, new object[] { "Skill Web: Affixes as Status Effects", isEnabled, onToggle }));
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
