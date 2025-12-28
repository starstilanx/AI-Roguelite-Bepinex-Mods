using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using HarmonyLib;
using System.IO;
using System.Collections.Generic;
using UnityEngine.EventSystems;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System;

namespace AIROG_NPCExpansion
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [HarmonyPatch]
    public class NPCExpansionPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.npcexpansion";
        public const string PLUGIN_NAME = "NPC Expansion";
        public const string PLUGIN_VERSION = "1.0.1";

        public static NPCExpansionPlugin Instance { get; private set; }
        public static string NPCDataPath => Path.Combine(Paths.PluginPath, "AIROG_NPCExpansion", "NPCData");

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Plugin {PLUGIN_GUID} is loaded!");

            if (!Directory.Exists(NPCDataPath))
            {
                Directory.CreateDirectory(NPCDataPath);
            }

            Harmony.CreateAndPatchAll(typeof(NPCExpansionPlugin));
            
            // Initialize UI logic
            NPCUI.Init();
            NPCEquipmentUI.Init();
            NPCExamineUI.Init();
        }

        private void Update()
        {
            NPCUI.Update();
        }

        public static void RunAutonomyTest(GameplayManager manager)
        {
            var nearbyNpcs = manager.currentPlace?.npcs?.ToList();
            if (nearbyNpcs != null)
            {
                Debug.Log($"[AIROG_NPCExpansion] Manual autonomy test triggered for {nearbyNpcs.Count} NPCs.");
                foreach (var npc in nearbyNpcs)
                {
                    NPCAutonomy.Process(npc, manager);
                }
            }
        }

        [HarmonyPatch(typeof(NpcActionsHandler), "UpdateCurrentNpc")]
        [HarmonyPostfix]
        public static void Postfix_UpdateCurrentNpc(NpcActionsHandler __instance, GameCharacter npc)
        {
            if (__instance != null && npc != null)
            {
                NPCUI.TryInject(__instance);
                NPCUI.TryUpdateText(__instance);
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "Start")]
        [HarmonyPostfix]
        public static void Postfix_GameplayManager_Start(GameplayManager __instance)
        {
            if (__instance != null)
            {
                NPCUI.TryInjectBottomBar(__instance);

                // Scenario Update Hook
                GameplayManager.TurnHappenedEvent -= ScenarioUpdater.OnTurnHappened;
                GameplayManager.TurnHappenedEvent += ScenarioUpdater.OnTurnHappened;
            }
        }
        
        // Also ensure we inject if dropdown functionality is used/updated
        [HarmonyPatch(typeof(GameplayManager), "UpdateNpcConvoSelectorDropdown")]
        [HarmonyPostfix]
        public static void Postfix_UpdateNpcConvoSelectorDropdown(GameplayManager __instance)
        {
             if (__instance != null)
             {
                 NPCUI.TryInjectBottomBar(__instance);
             }
        }

        // Hook into dropdown change to maybe update text if needed
        [HarmonyPatch(typeof(GameplayManager), "OnNpcConvoSelectorDropdownValueChanged")]
        [HarmonyPostfix]
        public static void Postfix_OnNpcConvoSelectorDropdownValueChanged(GameplayManager __instance)
        {
             // This method runs when user selects new NPC in the bottom bar.
             // We can check the selected NPC and inject text.
             if (__instance != null)
             {
                 NPCUI.TryUpdateTextForBottomBar(__instance);
             }
        }

        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPrefix]
        public static void Prefix_TurnHappened(GameCharacter __instance, out List<GameItem> __state)
        {
            __state = null;
            if (__instance.isMerchant && __instance.NeedsRestock())
            {
                // Save personal items (those not marked as MERCHANT stock)
                __state = __instance.items.Where(i => i.itemState != GameItem.ItemState.MERCHANT).ToList();
            }
        }

        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPostfix]
        public static void Postfix_TurnHappened(GameCharacter __instance, List<GameItem> __state)
        {
            if (__state != null && __state.Count > 0)
            {
                foreach (var item in __state)
                {
                    if (!__instance.items.Contains(item))
                    {
                        __instance.items.Add(item);
                        // Also ensure personal gear has correct state
                        if (item.itemState == GameItem.ItemState.MERCHANT) 
                            item.itemState = GameItem.ItemState.INVENTORY;
                    }
                }
                Debug.Log($"[AIROG_NPCExpansion] Restored {__state.Count} personal items for {__instance.GetPrettyName()} after merchant restock.");
            }
        }

        [HarmonyPatch(typeof(SaveIO), "WriteSaveFile")]
        [HarmonyPostfix]
        public static void Postfix_WriteSaveFile(GameplayManager manager, bool clean)
        {
            if (SS.I != null && !string.IsNullOrEmpty(SS.I.saveSubDirAsArg))
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                NPCData.SaveSessionLore(saveDir);
            }
        }

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        [HarmonyPostfix]
        public static void Postfix_ReadSaveFile(string saveSubDir, GameSaveData __result)
        {
            if (__result != null && SS.I != null)
            {
                string saveDir = Path.Combine(SS.I.saveTopLvlDir, saveSubDir);
                NPCData.LoadSessionLore(saveDir);
                NPCUI.RefreshAll();
            }
        }

        [HarmonyPatch(typeof(InventoryAndAbilitySelectionPrompter), "SellItem")]
        [HarmonyPostfix]
        public static void Postfix_SellItem(GameItem item, long goldAmount, MerchantInventory ___merchantInventory)
        {
            if (___merchantInventory != null && ___merchantInventory.currentMerchant != null)
            {
                var npc = ___merchantInventory.currentMerchant;
                var data = NPCData.Load(npc.uuid);
                if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());

                data.ChangeAffinity(3, $"Sold {item.GetPrettyName()} to them.");
                NPCData.Save(npc.uuid, data);
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "ProcessInteractionInfoNoTryStr")]
        [HarmonyPostfix]
        public static void Postfix_ProcessInteractionInfoNoTryStr(InteractionInfo interactionInfo)
        {
            if (interactionInfo == null || interactionInfo.interacteeInfo == null || interactionInfo.interacterInfo == null) return;

            if (interactionInfo.interacteeInfo.interacteeEntity is GameCharacter npc)
            {
                var data = NPCData.Load(npc.uuid);
                if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());

                var interType = interactionInfo.interacterInfo.interacterType;
                
                if (interType == InteracterInfo.InteracterType.OFFER_ITEM)
                {
                    var item = interactionInfo.interacterInfo.item;
                    data.ChangeAffinity(10, $"Received {item?.GetPrettyName() ?? "an item"} as a gift.");
                }
                else if (interType == InteracterInfo.InteracterType.TXT_INPUT || interType == InteracterInfo.InteracterType.INTERACT)
                {
                    data.ChangeAffinity(1, "Spent time talking to them.");
                }
                else if (interType == InteracterInfo.InteracterType.ATTACK_WITH_ITEM || interType == InteracterInfo.InteracterType.ABILITY)
                {
                    data.ChangeAffinity(-10, $"Was attacked by the player with {interactionInfo.interacterInfo.item?.GetPrettyName() ?? "something"}.");
                }

                NPCData.Save(npc.uuid, data);
            }
        }

        // --- Gear System Helper ---
        private static ItemSlot _lastClickedItemSlot;

        [HarmonyPatch(typeof(GameplayManager), "OnItemSlotClicked")]
        [HarmonyPrefix]
        public static void Prefix_OnItemSlotClicked(ItemSlot itemSlot)
        {
            _lastClickedItemSlot = itemSlot;
        }

        private static GameCharacter GetCurrentlySelectedNpc(GameplayManager manager)
        {
            if (manager == null) return null;

            // 1. Check if we are talking to an NPC
            if (manager.npcActionsHandler != null && manager.npcActionsHandler.currentNpc != null)
            {
                return manager.npcActionsHandler.currentNpc;
            }

            // 2. Check if we are in combat with an NPC (who might be neutral or we are giving them something before/after fight)
            if (manager.enemyActionsHandler != null && manager.enemyActionsHandler.currentEnemy != null)
            {
                return manager.enemyActionsHandler.currentEnemy;
            }

            // 3. Check if Gear UI is open
            if (NPCEquipmentUI.Instance != null && NPCEquipmentUI.Instance._window != null && NPCEquipmentUI.Instance._window.activeSelf)
            {
                if (NPCEquipmentUI.Instance._currentNpc != null) 
                    return NPCEquipmentUI.Instance._currentNpc;
            }

            // 4. Default to bottom bar/dropdown selection
            if (manager.npcConvoSelectorDropdown != null)
            {
                var chars = manager.GetCharsForNpcConvoSelectorDropdown();
                int idx = manager.npcConvoSelectorDropdown.value;
                if (chars != null && idx >= 0 && idx < chars.Count)
                {
                    return chars[idx];
                }
            }
            return null;
        }

        [HarmonyPatch(typeof(DropdownMenu), "showMenu")]
        [HarmonyPrefix]
        public static void Prefix_showMenu(List<DropdownMenuItem> menuItems, DropdownMenu __instance)
        {
            if (_lastClickedItemSlot == null || _lastClickedItemSlot.item == null) 
            {
                _lastClickedItemSlot = null;
                return;
            }
            if (_lastClickedItemSlot.item.itemState != GameItem.ItemState.INVENTORY) 
            {
                _lastClickedItemSlot = null;
                return;
            }

            var manager = __instance.manager;
            var currentNpc = GetCurrentlySelectedNpc(manager);
            
            if (currentNpc != null && !currentNpc.IsEnemyType())
            {
                string giveText = $"Give to {currentNpc.GetPrettyName()}";
                // Colorized specific check to avoid missing it if base game adds a similar but different one
                if (!menuItems.Any(m => m.menuItemText.Contains("Give to") || m.menuItemText.Contains("<color=#00ff00>")))
                {
                    var itemToGive = _lastClickedItemSlot.item;
                    menuItems.Add(new DropdownMenuItem("<color=#00ff00>" + giveText + "</color>", async () =>
                    {
                        await NPCEquipmentUI.GiveItemToNPC(itemToGive, currentNpc, manager);
                    }));
                    Debug.Log($"[AIROG_NPCExpansion] Injected 'Give to {currentNpc.GetPrettyName()}' choice for {itemToGive.GetPrettyName()}.");
                }
            }
            else
            {
                 Debug.Log($"[AIROG_NPCExpansion] Skipping 'Give to' injection. currentNpc: {(currentNpc?.GetPrettyName() ?? "null")}, IsEnemy: {currentNpc?.IsEnemyType()}");
            }
            
            _lastClickedItemSlot = null;
        }

        // --- Mechanical Effects for Gear ---

        [HarmonyPatch(typeof(EnemyActionsHandler), "HandleEnemyInjury", typeof(GameCharacter), typeof(long))]
        [HarmonyPrefix]
        public static void Prefix_HandleEnemyInjury(GameCharacter ene, ref long damage)
        {
            if (ene == null) return;
            var data = NPCData.Load(ene.uuid);
            if (data == null) return;

            double reduction = 0;
            foreach (var kvp in data.EquippedUuids)
            {
                if (kvp.Key == "WEAPON1" || kvp.Key == "WEAPON2") continue;
                var item = ene.items.Find(i => i.uuid == kvp.Value);
                if (item != null && item.IsArmorType())
                {
                     reduction += Utils.GetDmgProtForItem(item, ene.level);
                }
            }

            if (reduction > 0)
            {
                long oldDmg = damage;
                damage = (long)(damage * (1.0 - Math.Min(0.8, reduction)));
                if (damage < oldDmg)
                {
                    Debug.Log($"[AIROG_NPCExpansion] Armor reduced damage to {ene.GetPrettyName()}: {oldDmg} -> {damage} ({reduction:P0} reduction)");
                }
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "GetPlayerBarDmg")]
        [HarmonyPostfix]
        public static void Postfix_GetPlayerBarDmg(CauseOfEvent causeOfEvent, ref long __result, GameplayManager __instance)
        {
            if (causeOfEvent != null && causeOfEvent.currentEntity is GameCharacter npc)
            {
                var data = NPCData.Load(npc.uuid);
                if (data != null && data.EquippedUuids.TryGetValue("WEAPON1", out string weaponUuid))
                {
                    var weapon = npc.items.Find(i => i.uuid == weaponUuid);
                    if (weapon != null)
                    {
                        var difficulty = __instance.GetDifficulty();
                        long weaponDmg = Utils.CalculatePlayerDamage(npc.level, new CauseOfEvent(weapon), npc.level, difficulty);
                        long baseDmg = Utils.CalculatePlayerDamage(npc.level, new CauseOfEvent(npc), npc.level, difficulty);
                        
                        if (weaponDmg > baseDmg)
                        {
                            double ratio = (double)weaponDmg / baseDmg;
                            long oldDmg = __result;
                            __result = (long)(__result * ratio);
                            Debug.Log($"[AIROG_NPCExpansion] Weapon increased damage from {npc.GetPrettyName()}: {oldDmg} -> {__result} ({ratio:F2}x)");
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "GetActions", typeof(GameCharacter))]
        [HarmonyPostfix]
        public static void Postfix_GetActions(GameCharacter npc, ref List<StrToAction> __result, GameplayManager __instance)
        {
            if (npc == null || npc.IsEnemyType() || npc.corpseState != GameCharacter.CorpseState.NONE) return;

            __result.Add(new StrToAction("<color=#00ccff>Inventory & Gear</color>", () =>
            {
                NPCEquipmentUI.OpenFor(npc, __instance);
                return Task.CompletedTask;
            }));

            __result.Add(new StrToAction("<color=#ffff00>Examine</color>", () =>
            {
                NPCExamineUI.OpenFor(npc, __instance);
                return Task.CompletedTask;
            }));
        }

        [HarmonyPatch(typeof(GameCharacter), "GetDetailsForPromptStr")]
        [HarmonyPostfix]
        public static void Postfix_GetDetailsForPromptStr(GameCharacter __instance, ref string __result)
        {
            var data = NPCData.Load(__instance.uuid);
            if (data == null) return;

            string relationshipInfo = $"\n\n[RELATIONSHIP STATUS: {data.RelationshipStatus} (Affinity: {data.Affinity}/100)]";
            if (data.InteractionHistory.Count > 0)
            {
                relationshipInfo += "\nRecent Interactions:\n- " + string.Join("\n- ", data.InteractionHistory);
            }
            
            if (data.EquippedUuids.Count > 0)
            {
                relationshipInfo += "\nEquipped Items:";
                foreach (var kvp in data.EquippedUuids)
                {
                    var item = __instance.items.Find(i => i.uuid == kvp.Value);
                    if (item != null) relationshipInfo += $"\n- {kvp.Key}: {item.GetPrettyName()}";
                }
            }

            string statInfo = "\n\n[NPC STATS & ABILITIES]";
            statInfo += "\nAttributes: " + string.Join(", ", data.Attributes.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            if (data.Skills.Count > 0)
                statInfo += "\nSkills: " + string.Join(", ", data.Skills.Select(kvp => $"{kvp.Key} (Lvl {kvp.Value.level})"));
            if (data.Abilities.Count > 0)
                statInfo += "\nAbilities: " + string.Join(", ", data.Abilities);

            string personalityInfo = "";
            if (!string.IsNullOrEmpty(data.Personality))
                personalityInfo += $"\nPersonality: {data.Personality}";
            if (!string.IsNullOrEmpty(data.Scenario))
                personalityInfo += $"\nCurrent Situation: {data.Scenario}";

            string systemInstruction = "\n\n[CHARACTER GUIDELINES: Act according to your stats and abilities. If you have high Intellect, be articulate. If you have combat abilities, use them in your descriptions. Your reactions to the player should be influenced by your Affinity and personality.]";

            __result += relationshipInfo + statInfo + personalityInfo + systemInstruction;
        }

        [HarmonyPatch(typeof(GameplayManager), "GetCharsForNpcConvoSelectorDropdown")]
        [HarmonyPrefix]
        public static bool Prefix_GetCharsForNpcConvoSelectorDropdown(GameplayManager __instance, ref List<GameCharacter> __result)
        {
            if (__instance.currentPlace == null)
            {
                // If currentPlace is null, return just followers (if any) or an empty list
                if (__instance.playerCharacter?.pcGameEntity?.followers != null)
                {
                    __result = __instance.playerCharacter.pcGameEntity.followers.ToList();
                }
                else
                {
                    __result = new List<GameCharacter>();
                }
                return false; // Skip original method
            }
            return true; // Run original method
        }


        [HarmonyPatch(typeof(GameplayManager), "AddSavePointForUndo")]
        [HarmonyPostfix]
        public static void Postfix_AddSavePointForUndo(GameplayManager __instance)
        {
            CheckAndProcessPendingGift(__instance);
        }

        [HarmonyPatch(typeof(GameplayManager), "AddRetryPointAfterUndoPoint")]
        [HarmonyPostfix]
        public static void Postfix_AddRetryPointAfterUndoPoint(GameplayManager __instance)
        {
            CheckAndProcessPendingGift(__instance);
        }

        private static void CheckAndProcessPendingGift(GameplayManager manager)
        {
            if (NPCEquipmentUI.PendingGiftItem != null && NPCEquipmentUI.PendingGiftNpc != null)
            {
                var item = NPCEquipmentUI.PendingGiftItem;
                var npc = NPCEquipmentUI.PendingGiftNpc;

                Debug.Log($"[AIROG_NPCExpansion] Processing pending gift of {item.GetPrettyName()} to {npc.GetPrettyName()} AFTER snapshot.");

                manager.inventory.RemoveItemIfExists(item.uuid, SS.DelMode.REMOVE_BUT_KEEP_IN_CTX);
                item.itemState = GameItem.ItemState.INVENTORY;
                item.parentEnt = npc; // Essential for serialization
                item.SetParentEnt(npc); // Essential for entity logic
                npc.items.Add(item);

                manager.inventory.RefreshInvDisplay();

                NPCEquipmentUI.PendingGiftItem = null;
                NPCEquipmentUI.PendingGiftNpc = null;
            }
        }
    }
}
