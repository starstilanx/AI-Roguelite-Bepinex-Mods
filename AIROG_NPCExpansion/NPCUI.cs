using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AIROG_NPCExpansion
{
    public static class NPCUI
    {
        private static bool _uiInjected = false;
        private static GameObject _generateButtonObj;
        private static NpcActionsHandler _handler;
        private static string _lastLoggedNpcUuid;
        
        private static GameObject _dropdownObj;
        private static TMP_Dropdown _dropdown;
        private static List<GameCharacter> _availableChars;

        public static void Init()
        {
            _uiInjected = false;
            _lastLoggedNpcUuid = null;
        }

        private static GameCharacter _lastMonitoredNpc;
        private static int _lastMonitoredIdx = -1;

        public static void Update()
        {
             // 1. Maintain button existence
            if (_uiInjected && _generateButtonObj == null)
            {
                _uiInjected = false;
            }

            // 2. Monitor bottom bar selection for state changes
            if (_gameplayManager != null && _gameplayManager.npcConvoSelectorDropdown != null && _gameplayManager.currentPlace != null)
            {
                var currentNpc = GetSelectedNPC(_gameplayManager);
                int currentIdx = _gameplayManager.npcConvoSelectorDropdown.value;

                if (currentNpc != _lastMonitoredNpc || currentIdx != _lastMonitoredIdx)
                {
                    _lastMonitoredNpc = currentNpc;
                    _lastMonitoredIdx = currentIdx;
                    if (currentNpc != null)
                    {
                        TryUpdateTextForBottomBar(_gameplayManager);
                    }
                }
            }
        }

        public static void RefreshAll()
        {
            _lastMonitoredNpc = null; // Force Refresh
            if (_handler != null) RefreshUI();
            if (_gameplayManager != null) TryUpdateTextForBottomBar(_gameplayManager);
        }

        public static void TryInject(NpcActionsHandler handler)
        {
            _handler = handler;

            // 1. Inject Generate Button
            if (_generateButtonObj == null)
            {
                var refButton = handler.barterButton ?? handler.interactButton;
                if (refButton != null)
                {
                    _generateButtonObj = UnityEngine.Object.Instantiate(refButton.gameObject, refButton.transform.parent);
                    _generateButtonObj.name = "GenerateLoreButton";
                    
                    // Cleanup old listeners
                    var btn = _generateButtonObj.GetComponent<Button>();
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(OnGenerateClicked);

                    _generateButtonObj.transform.SetAsLastSibling();
                    _uiInjected = true;
                }
            }

            // 2. Inject Dropdown
            if (_dropdownObj == null && handler.manager.npcConvoSelectorDropdown != null)
            {
                _dropdownObj = UnityEngine.Object.Instantiate(handler.manager.npcConvoSelectorDropdown.gameObject, handler.npcActionsSidebar);
                _dropdownObj.name = "NPCSelectionDropdown";
                _dropdownObj.SetActive(true); // Ensure it's active
                _dropdownObj.transform.localScale = Vector3.one; // Reset scale

                
                // Adjust position/offset if necessary. 
                // Typically instantiate keeps localPosition, which might be off if parent changed.
                // Resetting anchoring/position might be needed, but difficult without inspecting hierarchy.
                // For now, let's rely on LayoutGroup if it exists, or manual placement.
                
                _dropdown = _dropdownObj.GetComponent<TMP_Dropdown>();
                
                // Position: Try LastSibling to ensure it renders on top of background
                _dropdownObj.transform.SetAsLastSibling(); 
                
                // Add Listener
                _dropdown.onValueChanged.RemoveAllListeners();
                _dropdown.onValueChanged.AddListener(OnDropdownChanged);
            }

            // 3. Refresh State
            RefreshUI();
        }

        private static void RefreshUI()
        {
            if (_handler == null || _handler.currentNpc == null) return;

            // Update Generate Button Text
            var data = NPCData.Load(_handler.currentNpc.uuid);
            var tmp = _generateButtonObj?.GetComponentInChildren<TMP_Text>();
            var img = _generateButtonObj?.GetComponent<Image>();
            
            if (tmp != null) 
            {
                tmp.text = (data != null) ? "Edit Lore" : "Generate Lore";
            }
            if (img != null)
            {
                img.color = (data != null) ? new Color(0f, 0.7f, 1f) : new Color(1f, 0.5f, 0f);
            }

            // Update Dropdown Options
            if (_dropdown != null)
            {
                _availableChars = _handler.manager.GetCharsForNpcConvoSelectorDropdown();
                _dropdown.ClearOptions();
                
                var options = new List<string>();
                int selectedIndex = 0;

                for (int i = 0; i < _availableChars.Count; i++)
                {
                    var c = _availableChars[i];
                    options.Add(c.GetPrettyName());
                    if (c == _handler.currentNpc) selectedIndex = i;
                }
                
                _dropdown.AddOptions(options);
                _dropdown.SetValueWithoutNotify(selectedIndex);
            }
        }

        private static void OnDropdownChanged(int index)
        {
            if (_availableChars == null || index < 0 || index >= _availableChars.Count) return;
            var selected = _availableChars[index];
            
            if (selected != _handler.currentNpc)
            {
                Debug.Log($"[AIROG_NPCExpansion] Switching to NPC: {selected.GetPrettyName()}");
                _handler.UpdateCurrentNpc(selected);
            }
        }

        private static async void OnGenerateClicked()
        {
            if (_handler == null || _handler.currentNpc == null) return;
            if (_generateButtonObj == null) return;

            var tmp = _generateButtonObj.GetComponentInChildren<TMP_Text>();
            string originalText = "Generate Lore";
            if (tmp != null) tmp.text = "Generating...";

            Debug.Log($"[AIROG_NPCExpansion] Generate clicked for {_handler.currentNpc.GetPrettyName()}");
            
            // Get context if possible
            string context = "";
            if (_handler.manager != null)
            {
               context = _handler.manager.GetContextForQuickActions();
            }

            // Trigger Generation
            bool success = await NPCGenerator.GenerateLore(_handler.currentNpc, context);
            
            if (success)
            {
                var data = NPCData.Load(_handler.currentNpc.uuid);
                Debug.Log($"[AIROG_NPCExpansion] Saved new lore for {_handler.currentNpc.GetPrettyName()}");
                
                if (tmp != null) tmp.text = "Done!";
                NPCUI.TryUpdateText(_handler); // Auto-update text on completion
                await System.Threading.Tasks.Task.Delay(2000);
                if (tmp != null) tmp.text = originalText;
            }
            else
            {
                if (tmp != null) tmp.text = "Failed";
                await System.Threading.Tasks.Task.Delay(2000);
                if (tmp != null) tmp.text = originalText;
            }
        }

        public static void TryUpdateText(NpcActionsHandler handler)
        {
            if (handler == null || handler.currentNpc == null || handler.conversationText == null) return;

            var data = NPCData.Load(handler.currentNpc.uuid);
            if (data != null && !string.IsNullOrEmpty(data.FirstMessage))
            {
                string relColor = GetRelationshipColor(data.Affinity);
                string relInfo = $"<color={relColor}>[{data.RelationshipStatus} ({data.Affinity}/100)]</color>";
                
                string currentText = handler.conversationText.text;
                // Avoid duplicating
                if (string.IsNullOrEmpty(currentText) || !currentText.Contains(data.FirstMessage))
                {
                     if (string.IsNullOrEmpty(currentText))
                         handler.conversationText.text = $"{data.Name} {relInfo}: {data.FirstMessage}";
                     else
                         handler.conversationText.text = $"{data.Name} {relInfo}: {data.FirstMessage}\n\n{currentText}";
                }
            }
        }
        // Bottom Bar Injection
        private static GameObject _bottomGenerateBtnObj;
        private static GameplayManager _gameplayManager;
        private static Sprite _loreButtonSprite;

        private static Sprite LoadLoreButtonSprite()
        {
            string fileName = "LoreButton.png";
            string[] searchPaths = new string[]
            {
                Path.Combine(Application.streamingAssetsPath, "NPCExpansion", fileName),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Assets", fileName),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), fileName)
            };
            foreach (string path in searchPaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    Texture2D tex = new Texture2D(2, 2);
                    if (tex.LoadImage(data))
                        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
                catch (Exception ex) { Debug.LogWarning($"[NPCUI] LoreButton.png load error: {ex.Message}"); }
            }
            Debug.LogWarning("[NPCUI] LoreButton.png not found.");
            return null;
        }

        public static void TryInjectBottomBar(GameplayManager manager)
        {
            if (_bottomGenerateBtnObj != null) 
            {
               if (!_bottomGenerateBtnObj.activeSelf) _bottomGenerateBtnObj.SetActive(true);
               return; 
            }

            _gameplayManager = manager;
            if (manager.npcConvoSelectorDropdown == null) return;

            if (_bottomGenerateBtnObj != null) 
            {
               if (!_bottomGenerateBtnObj.activeSelf) _bottomGenerateBtnObj.SetActive(true);
               // Even if already injected, ensure we refresh state for current NPC
               TryUpdateTextForBottomBar(manager);
               return; 
            }

            _gameplayManager = manager;
            if (manager.npcConvoSelectorDropdown == null) return;

            // Load sprite asset once
            if (_loreButtonSprite == null)
                _loreButtonSprite = LoadLoreButtonSprite();

            // Create FRESH button to avoid inheriting existing scripts/events
            _bottomGenerateBtnObj = new GameObject("GenerateLoreButtonBottom", typeof(RectTransform));
            _bottomGenerateBtnObj.transform.SetParent(manager.npcConvoSelectorDropdown.transform, false);

            // Visuals — sprite image (no text; "Lore" is baked into the asset)
            var img = _bottomGenerateBtnObj.AddComponent<Image>();
            if (_loreButtonSprite != null)
            {
                img.sprite = _loreButtonSprite;
                img.type = Image.Type.Simple;
                img.preserveAspect = true;
                img.color = Color.white;
            }
            else
            {
                // Fallback: plain orange square if asset missing
                img.color = new Color(1f, 0.5f, 0f);
            }

            // Interaction
            var btn = _bottomGenerateBtnObj.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => OnBottomGenerateClicked(manager));

            // Disable Navigation to prevent 'Enter' from triggering Submit on Focus Change
            var nav = new Navigation();
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;

            // Layout Handling - Floating, ignores layout groups
            var le = _bottomGenerateBtnObj.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            // Position: bottom-left of the dropdown, so it sits next to the NPC name — unobtrusive
            var rect = _bottomGenerateBtnObj.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0f, 1f); // Top-left pivot
                rect.anchoredPosition = new Vector2(2f, -1f); // 2px inset from bottom-left corner of dropdown
                rect.sizeDelta = new Vector2(80, 30); // Larger; preserves stone-tablet aspect ratio
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
            }
            
            Debug.Log($"[AIROG_NPCExpansion] Bottom Bar Injected (Fresh Object).");
            
            // Initial update to reflect current NPC state
            TryUpdateTextForBottomBar(manager);
        }

        // Helper for Layout Rebuild if needed, or use Unity's
        private static class LayoutT {
            public static void Rebuild(RectTransform rect) {
                if(rect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            }
        }


        
        private static GameCharacter GetSelectedNPC(GameplayManager manager)
        {
             if (manager == null || manager.npcConvoSelectorDropdown == null || manager.currentPlace == null) return null;
             var chars = manager.GetCharsForNpcConvoSelectorDropdown();
             int idx = manager.npcConvoSelectorDropdown.value - 1; // dropdown is 1-based: 0=[OPEN-ENDED], 1=first NPC
             if (chars == null || idx < 0 || idx >= chars.Count) return null;
             return chars[idx];
        }

        public static void TryUpdateTextForBottomBar(GameplayManager manager)
        {
            if (manager.npcConvoSelectorDropdown == null) return;
            
            GameCharacter npc = GetSelectedNPC(manager);
            if (npc != null)
            {
                // Update Button State (New vs Edit)
                UpdateBottomButtonState(npc, manager);

                // Inject Intro Text if available
                var data = NPCData.Load(npc.uuid);
                if (data != null)
                {
                    if (_lastLoggedNpcUuid != npc.uuid)
                    {
                        string relColor = GetRelationshipColor(data.Affinity);
                        _ = manager.gameLogView.LogTextCompat($"<color=yellow>[AI Lore]</color> Relationship with {npc.GetPrettyName()}: <color={relColor}>{data.RelationshipStatus} ({data.Affinity})</color>");
                        
                        if (!string.IsNullOrEmpty(data.FirstMessage))
                        {
                            _ = manager.gameLogView.LogTextCompat($"<color=white>\"{data.FirstMessage}\"</color>");
                        }
                        _lastLoggedNpcUuid = npc.uuid;
                    }
                }
            }
        }

        private static string GetRelationshipColor(int affinity)
        {
            if (affinity >= 80) return "#FFD700"; // Gold
            if (affinity >= 50) return "#00FF00"; // Green
            if (affinity >= 20) return "#ADFF2F"; // GreenYellow
            if (affinity > -20) return "#FFFFFF"; // White
            if (affinity > -50) return "#FFA500"; // Orange
            if (affinity > -80) return "#FF4500"; // OrangeRed
            return "#FF0000"; // Red
        }

        private static void UpdateBottomButtonState(GameCharacter npc, GameplayManager manager = null)
        {
            if (_bottomGenerateBtnObj == null) return;

            var mgr = manager ?? _gameplayManager;
            var data = NPCData.Load(npc.uuid);
            bool hasLore = (data != null);

            var img = _bottomGenerateBtnObj.GetComponent<Image>();
            var btn = _bottomGenerateBtnObj.GetComponent<Button>();

            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                if (hasLore)
                {
                    // EDIT MODE — cyan tint to signal "has lore, click to edit"
                    if (img != null) img.color = new Color(0.55f, 0.9f, 1f);
                    btn.onClick.AddListener(() => ShowLoreEditor(npc, mgr));
                }
                else
                {
                    // GENERATE MODE — neutral white tint (asset's own colors show through)
                    if (img != null) img.color = Color.white;
                    btn.onClick.AddListener(() => OnBottomGenerateClicked(mgr));
                }
            }
        }


        public static void ShowLoreEditor(GameCharacter npc, GameplayManager manager)
        {
            if (manager == null)
            {
                Debug.LogError("[AIROG_NPCExpansion] ShowLoreEditor: GameplayManager is null!");
                return;
            }

            Debug.Log("[AIROG_NPCExpansion] ShowLoreEditor started.");

            try
            {
                var modal = manager.NTextPromptModal();
                if (modal == null)
                {
                    Debug.LogError("[AIROG_NPCExpansion] NTextPromptModal returned null.");
                    return;
                }

                Debug.Log("[AIROG_NPCExpansion] Retrieved NTextPromptModal instance.");

                // Reload NPC to ensure we have the latest
                var allChars = manager.GetCharsForNpcConvoSelectorDropdown();
                if (allChars != null)
                {
                    foreach (var c in allChars)
                    {
                        if (c != null && c.uuid == npc.uuid)
                        {
                            npc = c;
                            break;
                        }
                    }
                }
                
                var data = NPCData.Load(npc.uuid) ?? new NPCData { Name = npc.GetPrettyName() };

                // Use reflection to get types to avoid TypeLoadException
                Type modalType = modal.GetType();
                // NTextPromptModal is the outer class. PromptArg and TextPromptArg are nested.
                Type promptArgType = modalType.GetNestedType("PromptArg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                Type textPromptArgType = modalType.GetNestedType("TextPromptArg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                Type togglePromptArgType = modalType.GetNestedType("TogglePromptArg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (promptArgType == null || textPromptArgType == null || togglePromptArgType == null)
                {
                    Debug.LogError("[AIROG_NPCExpansion] Could not find nested types PromptArg, TextPromptArg, or TogglePromptArg via reflection.");
                    return;
                }

                Debug.Log("[AIROG_NPCExpansion] Found nested types via reflection.");

                // Create generic List<PromptArg>
                Type listType = typeof(List<>).MakeGenericType(promptArgType);
                System.Collections.IList list = (System.Collections.IList)Activator.CreateInstance(listType);

                // Helper to add
                // Constructor: public TextPromptArg(string desc, string text, string suggestion, bool isBig, Action<string> confirmAction)
                Action<string, string, Action<string>> addText = (desc, txt, act) => {
                     object arg = Activator.CreateInstance(textPromptArgType, new object[] { 
                        desc, 
                        txt ?? "", 
                        null, 
                        true, 
                        act 
                     });
                     list.Add(arg);
                };

                addText("Personality", data.Personality, (s) => data.Personality = s);
                addText("Scenario", data.Scenario, (s) => data.Scenario = s);
                addText("First Message", data.FirstMessage, (s) => data.FirstMessage = s);
                addText("Message Examples", data.MessageExamples, (s) => data.MessageExamples = s);
                addText("System Prompt", data.SystemPrompt, (s) => data.SystemPrompt = s);
                addText("Creator Notes", data.CreatorNotes, (s) => data.CreatorNotes = s);
                addText("Post-History Instructions", data.PostHistoryInstructions, (s) => data.PostHistoryInstructions = s);
                addText("Alternate Greetings (newline separated)", string.Join("\n", data.AlternateGreetings ?? new List<string>()), (s) => {
                    data.AlternateGreetings = s.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                });
                addText("Extensions (JSON format)", JsonConvert.SerializeObject(data.Extensions ?? new Dictionary<string, string>(), Formatting.Indented), (s) => {
                    try {
                        data.Extensions = JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
                    } catch { /* ignore invalid json */ }
                });

                // Autonomy Settings
                Action<string, bool, Action<bool>> addToggle = (desc, val, act) => {
                      object arg = Activator.CreateInstance(togglePromptArgType, new object[] { 
                         desc, 
                         val,
                         act 
                      });
                      list.Add(arg);
                 };

                addToggle("Allow Auto-Equip", data.AllowAutoEquip, (b) => data.AllowAutoEquip = b);
                addToggle("Allow Self-Preservation (Healing)", data.AllowSelfPreservation, (b) => data.AllowSelfPreservation = b);
                addToggle("Allow Economic Activity (Selling surplus)", data.AllowEconomicActivity, (b) => data.AllowEconomicActivity = b);

                Debug.Log("[AIROG_NPCExpansion] Populated prompt arguments.");

                // Find PresentSelf method
                System.Reflection.MethodInfo presentMethod = null;
                foreach (var m in modalType.GetMethods())
                {
                    if (m.Name == "PresentSelf")
                    {
                        var paras = m.GetParameters();
                        if (paras.Length >= 1 && paras[0].ParameterType.IsGenericType && paras[0].ParameterType.GetGenericTypeDefinition() == typeof(List<>))
                        {
                            presentMethod = m;
                            break;
                        }
                    }
                }

                if (presentMethod == null)
                {
                    Debug.LogError("[AIROG_NPCExpansion] FAILED to find PresentSelf method via reflection.");
                    return;
                }

                Debug.Log($"[AIROG_NPCExpansion] Invoking {presentMethod.Name} with {presentMethod.GetParameters().Length} parameters.");

                // Parameters: List<PromptArg> promptArgs, string mainTitle = null, Action postProcessAction = null, Action cancelAction = null, Func<bool> validateAction = null, bool playSoundOnConfirm = true, bool disableCancel = false
                object[] args = new object[presentMethod.GetParameters().Length];
                args[0] = list;
                if (args.Length > 1) args[1] = "Edit NPC Lore";
                if (args.Length > 2) args[2] = new Action(() => {
                         NPCData.Save(npc.uuid, data);
                         Debug.Log($"[AIROG_NPCExpansion] Saved edited lore for {npc.GetPrettyName()}");
                         UpdateBottomButtonState(npc, manager); 
                     });
                // Fill remaining with nulls or defaults
                for (int i = 3; i < args.Length; i++)
                {
                    var p = presentMethod.GetParameters()[i];
                    if (p.ParameterType == typeof(bool)) args[i] = (i == 5); // playSoundOnConfirm default true
                    else args[i] = null;
                }

                presentMethod.Invoke(modal, args);

                Debug.Log("[AIROG_NPCExpansion] PresentSelf invoked successfully.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIROG_NPCExpansion] Error in ShowLoreEditor: {e}");
                if (e.InnerException != null)
                {
                    Debug.LogError($"[AIROG_NPCExpansion] Inner Exception: {e.InnerException}");
                }
            }
        }

        private static async void OnBottomGenerateClicked(GameplayManager manager)
        {
            if (manager == null) return;
            var npc = GetSelectedNPC(manager);
            if (npc == null) return;

            // Pulse the button dim-yellow to signal "working"
            var img = _bottomGenerateBtnObj?.GetComponent<Image>();
            if (img != null) img.color = new Color(1f, 0.85f, 0.3f);

            var context = manager.GetContextForQuickActions();
            bool success = await NPCGenerator.GenerateLore(npc, context);

            if (success)
                TryUpdateTextForBottomBar(manager); // Will call UpdateBottomButtonState → cyan tint
            else if (img != null)
                img.color = new Color(1f, 0.35f, 0.35f); // Red tint on failure; next NPC switch resets it
        }
    }
}
