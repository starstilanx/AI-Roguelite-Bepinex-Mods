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

        /// <summary>
        /// Resolves the GameplayManager a UI window should use (falling back to the hacky
        /// singleton) and validates it has a usable canvas. Callers should check this BEFORE
        /// touching any of their own state (e.g. the NPC currently being shown) so a failed
        /// resolve leaves an already-open window showing its previous, still-correct content
        /// instead of a mismatched NPC/manager pairing.
        /// </summary>
        public static bool TryResolveManager(GameplayManager manager, string uiName, out GameplayManager resolved)
        {
            resolved = manager ?? SS.I?.hackyManager;
            if (resolved == null || resolved.canvasTransform == null)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Cannot open {uiName}: no valid GameplayManager/canvas available.");
                resolved = null;
                return false;
            }
            return true;
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
            _gameplayManager = handler.manager;

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
        // Gameplay manager reference — set when the NPC panel is opened
        private static GameplayManager _gameplayManager;

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
            if (npc == null) return;

            var data = NPCData.Load(npc.uuid);
            if (data != null && _lastLoggedNpcUuid != npc.uuid)
            {
                string relColor = GetRelationshipColor(data.Affinity);
                _ = manager.gameLogView.LogTextCompat($"<color=yellow>[AI Lore]</color> Relationship with {npc.GetPrettyName()}: <color={relColor}>{data.RelationshipStatus} ({data.Affinity})</color>");
                if (!string.IsNullOrEmpty(data.FirstMessage))
                    _ = manager.gameLogView.LogTextCompat($"<color=white>\"{data.FirstMessage}\"</color>");
                _lastLoggedNpcUuid = npc.uuid;
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


    }
}
