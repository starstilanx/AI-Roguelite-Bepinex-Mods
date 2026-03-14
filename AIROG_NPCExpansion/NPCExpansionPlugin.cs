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
using DelaunayVoronoi;

namespace AIROG_NPCExpansion
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [HarmonyPatch]
    public class NPCExpansionPlugin : BaseUnityPlugin
    {
        public const string PLUGIN_GUID = "com.airog.npcexpansion";
        public const string PLUGIN_NAME = "NPC Expansion";
        public const string PLUGIN_VERSION = "3.0.0";

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
            QuestUI.Init();
            HallOfFallenUI.Init();
        }

        private void Update()
        {
            NPCUI.Update();
        }

        // ─── Faction-Sentiment Bridge ──────────────────────────────────────────────
        /// <summary>
        /// Synchronises NPCData.Affinity into GameCharacter.sentimentV2 so that our
        /// relationship system has real in-game mechanical weight.
        /// Called after every affinity change + save.
        /// </summary>
        public static void SyncAffinityToGame(string uuid, NPCData data)
        {
            try
            {
                if (SS.I?.uuidToGameEntityMap == null || data == null) return;
                if (SS.I.uuidToGameEntityMap.TryGetValue(uuid, out var ent) && ent is GameCharacter gc)
                    gc.sentimentV2 = (data.Affinity / 100f) * 15f; // -100→-15, 0→0, 100→+15
            }
            catch { /* Non-critical; GameCharacter may not be loaded yet */ }
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
                // Scenario Update Hook
                GameplayManager.TurnHappenedEvent -= ScenarioUpdater.OnTurnHappened;
                GameplayManager.TurnHappenedEvent += ScenarioUpdater.OnTurnHappened;
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "UpdateNpcConvoSelectorDropdown")]
        [HarmonyPostfix]
        public static void Postfix_UpdateNpcConvoSelectorDropdown(GameplayManager __instance)
        {
            if (__instance != null)
                NPCUI.TryUpdateTextForBottomBar(__instance);
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

        // Prevents the merchant restock cycle from firing on non-merchant NPCs.
        // TurnHappened() wipes npc.items and resets merchantGenerationState=NOT_STARTED for ALL
        // characters every 2-8 turns (no isMerchant guard in the base game). For non-merchants this
        // destroys player-gifted gear and causes GenerateItems() to fire if the player opens trade.
        [HarmonyPatch(typeof(GameCharacter), "NeedsRestock")]
        [HarmonyPrefix]
        public static bool Prefix_NeedsRestock(GameCharacter __instance, ref bool __result)
        {
            if (!__instance.isMerchant)
            {
                __result = false;
                return false; // skip original — non-merchants never restock
            }
            return true; // actual merchants: run original logic
        }

        // Safety net for actual merchant NPCs that have received player-gifted items (INVENTORY state).
        // When a merchant restocks, we preserve those items so they survive the new stock generation.
        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPrefix]
        public static void Prefix_TurnHappened(GameCharacter __instance, out List<GameItem> __state)
        {
            __state = null;
            if (!__instance.isMerchant) return; // non-merchants are guarded by Prefix_NeedsRestock
            if (__instance.items == null) return;

            var giftedItems = __instance.items.Where(i => i.itemState == GameItem.ItemState.INVENTORY).ToList();
            if (giftedItems.Count > 0)
                __state = giftedItems;
        }

        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPostfix]
        public static void Postfix_TurnHappened(GameCharacter __instance, List<GameItem> __state)
        {
            if (__state == null || __state.Count == 0) return;

            int restored = 0;
            foreach (var item in __state)
            {
                if (!__instance.items.Contains(item))
                {
                    item.itemState = GameItem.ItemState.INVENTORY;
                    __instance.items.Add(item);
                    restored++;
                }
            }

            if (restored > 0)
                Debug.Log($"[AIROG_NPCExpansion] Preserved {restored} gifted item(s) for merchant {__instance.GetPrettyName()} across restock.");
        }



        // ---- Nemesis System: Trigger on player death ----
        [HarmonyPatch(typeof(GameplayManager), "DeadLogic")]
        [HarmonyPrefix]
        public static void Prefix_DeadLogic(GameplayManager __instance)
        {
            try
            {
                // Check master switch — soft-dep on GenContext; default ON if unavailable
                bool nemesisEnabled = true;
                try { nemesisEnabled = AIROG_GenContext.ContextManager.GetGlobalSetting("NemesisSystem"); }
                catch { /* GenContext not present, use default */ }
                if (!nemesisEnabled) return;

                // Only promote living enemy characters
                var killer = __instance.enemyActionsHandler?.currentEnemy;
                if (killer == null || killer.corpseState != GameCharacter.CorpseState.NONE) return;

                NemesisManager.PromoteKiller(killer, __instance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Nemesis] Promotion failed: {ex.Message}");
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
                QuestManager.SaveQuests();
                NPCDeathTracker.SaveMemorial();
                NPCTeachingSystem.SavePlayerSkills();
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
                QuestManager.LoadQuests(saveDir);
                NPCTeachingSystem.LoadPlayerSkills(saveDir);
                NPCUI.RefreshAll();

                // Sync all loaded affinities into game sentiment values
                foreach (var kvp in NPCData.LoreCache)
                    SyncAffinityToGame(kvp.Key, kvp.Value);
            }
        }

        // ─── Inject NPC-taught skills into the player status string the AI sees ──
        [HarmonyPatch(typeof(PcGameEntity), "GetPlayerStatusStrToAppendNoSpace")]
        [HarmonyPostfix]
        public static void Postfix_GetPlayerStatusStrToAppendNoSpace(ref string __result)
        {
            string taught = NPCTeachingSystem.BuildTaughtSkillsContext();
            if (!string.IsNullOrEmpty(taught))
                __result += "\n" + taught;
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
                SyncAffinityToGame(npc.uuid, data);
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
                int oldAffinity = data.Affinity; // capture before ChangeAffinity
                int affinityDelta = 0;

                if (interType == InteracterInfo.InteracterType.OFFER_ITEM)
                {
                    var item = interactionInfo.interacterInfo.item;
                    affinityDelta = 10;
                    data.ChangeAffinity(affinityDelta, $"Received {item?.GetPrettyName() ?? "an item"} as a gift.");
                }
                else if (interType == InteracterInfo.InteracterType.TXT_INPUT || interType == InteracterInfo.InteracterType.INTERACT)
                {
                    affinityDelta = 1;
                    data.ChangeAffinity(affinityDelta, "Spent time talking to them.");
                }
                else if (interType == InteracterInfo.InteracterType.ATTACK_WITH_ITEM || interType == InteracterInfo.InteracterType.ABILITY)
                {
                    affinityDelta = -10;
                    data.ChangeAffinity(affinityDelta, $"Was attacked by the player with {interactionInfo.interacterInfo.item?.GetPrettyName() ?? "something"}.");
                }

                NPCData.Save(npc.uuid, data);
                SyncAffinityToGame(npc.uuid, data);

                // Social Ripple + Gossip + Arc Advancement + Secret Auto-Reveal
                if (affinityDelta != 0)
                {
                    var manager = GameObject.FindObjectOfType<GameplayManager>();
                    if (manager != null)
                    {
                        SocialRippleSystem.Process(npc.uuid, npc.GetPrettyName(), affinityDelta, manager);
                        WorldGossipSystem.SeedPlayerGossip(npc.uuid, npc.GetPrettyName(), affinityDelta);
                        RelationshipArcSystem.CheckArcAdvancement(npc, data, manager, oldAffinity);
                        NPCSecretSystem.CheckAutoReveal(npc, data, manager);
                    }
                }

                // Death Detection: if the NPC just died (corpseState changed to non-NONE)
                if (npc.corpseState != GameCharacter.CorpseState.NONE && !data.IsDeceased)
                {
                    var manager = GameObject.FindObjectOfType<GameplayManager>();
                    if (manager != null)
                    {
                        string killerName = interType == InteracterInfo.InteracterType.ATTACK_WITH_ITEM ||
                                            interType == InteracterInfo.InteracterType.ABILITY
                            ? "the player"
                            : "unknown causes";
                        NPCDeathTracker.OnNpcDied(npc, killerName, ScenarioUpdater.GlobalTurn, manager, data);
                    }
                }
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
        public static bool Prefix_HandleEnemyInjury(EnemyActionsHandler __instance, GameCharacter ene, long damage, ref bool __result)
        {
            if (ene == null) 
            {
                __result = false;
                return false;
            }

            // Calculate armor reduction from NPC equipment
            var data = NPCData.Load(ene.uuid);
            if (data != null && data.EquippedUuids != null && ene.items != null)
            {
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

            // Apply damage (replicating original HandleEnemyInjury logic)
            bool died = ene.DeltaHealth(-damage);
            
            // Update health bar if this is the current enemy
            if (__instance.currentEnemy == ene)
            {
                // Update health bar graphics via reflection (private method)
                try
                {
                    var updateMethod = typeof(EnemyActionsHandler).GetMethod("UpdateHealthBarGraphic", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    updateMethod?.Invoke(__instance, null);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AIROG_NPCExpansion] Failed to update health bar: {ex.Message}");
                }
            }

            __result = died;
            return false; // Skip original method since we handled it
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
            if (npc == null || npc.corpseState != GameCharacter.CorpseState.NONE) return;

            var npcData = NPCData.Load(npc.uuid);
            bool hasLore = npcData != null && !string.IsNullOrEmpty(npcData.Personality);

            // Examine — available for all characters including enemies
            __result.Add(new StrToAction("<color=#ffff00>Examine</color>", () =>
            {
                NPCExamineUI.OpenFor(npc, __instance);
                return Task.CompletedTask;
            }));

            // Generate / Edit Profile — available for all characters including enemies
            string profileLabel = hasLore ? "<color=#ff9900>Edit Profile</color>" : "<color=#ff9900>Generate Profile</color>";
            __result.Add(new StrToAction(profileLabel, async () =>
            {
                if (hasLore)
                    NPCUI.ShowLoreEditor(npc, __instance);
                else
                    await NPCGenerator.GenerateLore(npc, __instance.GetContextForQuickActions());
            }));

            // Everything below is friendly-NPC-only
            if (npc.IsEnemyType()) return;

            __result.Add(new StrToAction("<color=#00ccff>Inventory & Gear</color>", () =>
            {
                NPCEquipmentUI.OpenFor(npc, __instance);
                return Task.CompletedTask;
            }));

            if (npcData != null && !string.IsNullOrEmpty(npcData.Personality))
            {
                __result.Add(new StrToAction("<color=#ffd700>Accept Quest</color>", async () =>
                {
                    await QuestManager.GenerateQuest(npc, npcData, __instance);
                }));
            }

            __result.Add(new StrToAction("<color=#aaaaff>Quest Log</color>", () =>
            {
                QuestUI.Open(__instance);
                return Task.CompletedTask;
            }));

            // Relationship Arc actions (Ask Secret, Teach Me — threshold-gated)
            if (npcData != null && !string.IsNullOrEmpty(npcData.Personality))
            {
                var arcActions = RelationshipArcSystem.GetAvailableArcActions(npc, npcData, __instance);
                foreach (var arcAction in arcActions)
                    __result.Add(arcAction);
            }

            if (NPCData.LoreCache.Values.Any(d => d != null && d.IsDeceased))
            {
                __result.Add(new StrToAction("<color=#ff8888>Hall of Fallen</color>", () =>
                {
                    HallOfFallenUI.Open(__instance);
                    return Task.CompletedTask;
                }));
            }
        }

        // REMOVED: Postfix_GetDetailsForPromptStr logic moved to AIROG_GenContext per architecture guidelines.

        // ─── Quest Completion Observer ─────────────────────────────────────────────
        // Observes story results (STORY_COMPLETER / UNIFIED) to auto-detect quest completion.
        // Does NOT modify the result — read-only observation.
        [HarmonyPatch(typeof(AIAsker), nameof(AIAsker.GenerateTxtNoTryStrStyle))]
        [HarmonyPostfix]
        public static void Postfix_StoryCompletionObserver(
            System.Threading.Tasks.Task<string> __result,
            AIAsker.ChatGptPromptType chatGptPromptType)
        {
            if (chatGptPromptType != AIAsker.ChatGptPromptType.STORY_COMPLETER &&
                chatGptPromptType != AIAsker.ChatGptPromptType.UNIFIED) return;
            if (!QuestManager.HasActiveQuests) return;
            _ = ObserveStoryForQuests(__result);
        }

        private static async System.Threading.Tasks.Task ObserveStoryForQuests(
            System.Threading.Tasks.Task<string> resultTask)
        {
            try
            {
                string text = await resultTask;
                if (string.IsNullOrEmpty(text)) return;
                var manager = GameObject.FindObjectOfType<GameplayManager>();
                if (manager != null) _ = QuestManager.CheckCompletion(text, manager);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCExpansion] Quest observer error: {ex.Message}");
            }
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


        public static void TransferItemToNpc(GameItem item, GameCharacter npc, GameplayManager manager)
        {
            if (item == null || npc == null || manager == null) return;

            Debug.Log($"[AIROG_NPCExpansion] Transferring {item.GetPrettyName()} to {npc.GetPrettyName()} directly.");

            // 1. Remove from Player/World Inventory context
            // Using REMOVE_BUT_KEEP_IN_CTX ensures it's not deleted from memory, just from the list
            manager.inventory.RemoveItemIfExists(item.uuid, SS.DelMode.REMOVE_BUT_KEEP_IN_CTX);
            
            // 2. Update Item Entity Data
            item.itemState = GameItem.ItemState.INVENTORY;
            item.parentEnt = npc; 
            item.SetParentEnt(npc);
            
            // 3. Add to NPC
            if (npc.items == null) npc.items = new List<GameItem>();
            npc.items.Add(item);

            // 4. Force UI Refresh
            manager.inventory.RefreshInvDisplay();
            
            Debug.Log($"[AIROG_NPCExpansion] Transfer complete. NPC now has {npc.items.Count} items.");
        }
    }
}
