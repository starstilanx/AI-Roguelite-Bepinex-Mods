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
        public const string PLUGIN_VERSION = "4.4.0";

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

            var harmony = new Harmony(PLUGIN_GUID);
            harmony.PatchAll(typeof(NPCExpansionPlugin));

            // NPC action menu injection is now done via Prefix_PresentNpcMenu + Prefix_showMenu
            // (patching GetActions directly was fragile due to AccessTools overload resolution failures).

            // Patch GameCharacter.GenerateImportantData so that when the player uses the
            // native "Generate details" button we bootstrap our extended NPCData too.
            var genImportantMethod = AccessTools.Method(typeof(GameCharacter), "GenerateImportantData");
            if (genImportantMethod != null)
            {
                harmony.Patch(genImportantMethod,
                    postfix: new HarmonyMethod(typeof(NPCExpansionPlugin),
                        nameof(Postfix_GenerateImportantData)));
                Logger.LogInfo("[NPCExpansion] Patched GameCharacter.GenerateImportantData.");
            }
            else
            {
                Logger.LogWarning("[NPCExpansion] Could not find GameCharacter.GenerateImportantData — native profile generation will not seed NPCData.");
            }

            // NOTE: NPC-taught techniques used to be appended here via a postfix on
            // PlayableCharacterData.GetPlayerStatusStrToAppendNoSpace. That bypassed the
            // shared token budget and the provider toggle, so it now goes through
            // AIROG_GenContext's NPCProvider like every other piece of our context.

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

        /// <summary>
        /// Called after the game's native GenerateImportantData completes.
        /// Seeds our NPCData with the natively generated personality and background so
        /// all our systems (bark, secrets, arcs, etc.) recognise the NPC as profiled,
        /// then kicks off extended attribute/skill/ability generation in the background.
        /// </summary>
        public static async void Postfix_GenerateImportantData(GameCharacter __instance)
        {
            try
            {
                var npc = __instance;
                if (npc == null || npc.importantData == null) return;
                if (string.IsNullOrEmpty(npc.importantData.personality)) return;

                var data = NPCData.Load(npc.uuid) ?? NPCData.CreateDefault(npc.GetPrettyName());

                // Seed our fields from native data so HasProfile() returns true immediately
                if (string.IsNullOrEmpty(data.Personality))
                    data.Personality = npc.importantData.personality;
                if (string.IsNullOrEmpty(data.Scenario) && !string.IsNullOrEmpty(npc.importantData.background))
                    data.Scenario = npc.importantData.background;
                if (string.IsNullOrEmpty(data.Description) && !string.IsNullOrEmpty(npc.importantData.visualDescription))
                    data.Description = npc.importantData.visualDescription;

                NPCData.Save(npc.uuid, data);
                Debug.Log($"[NPCExpansion] Seeded NPCData from native importantData for {npc.GetPrettyName()}.");

                bool needsExtended = data.Attributes == null || data.Attributes.Count == 0
                                  || data.Attributes.Values.All(v => v == 10);
                if (needsExtended)
                {
                    string context = (npc.manager as GameplayManager)?.GetContextForQuickActions() ?? "";
                    await NPCGenerator.GenerateLore(npc, context);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCExpansion] Postfix_GenerateImportantData failed: {ex.Message}");
            }
        }

        public static void RunAutonomyTest(GameplayManager manager)
        {
            var nearbyNpcs = manager.currentPlace?.npcs?.ToList();
            if (nearbyNpcs != null)
            {
                Debug.Log($"[AIROG_NPCExpansion] Manual autonomy test triggered for {nearbyNpcs.Count} NPCs.");
                foreach (var npc in nearbyNpcs)
                {
                    _ = NPCAutonomy.Process(npc, manager);
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
        // First line of defence: NeedsRestock() is patched to always return false for non-merchants.
        // NOTE: This may not fire if NeedsRestock() is JIT-inlined into TurnHappened; the
        // Prefix/Postfix static-dictionary approach below acts as a belt-and-suspenders safety net.
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

        // Static dictionary used to pass saved items from Prefix to Postfix.
        // Using a static field is more reliable than Harmony's __state mechanism when Prefix and
        // Postfix are registered as separate attribute-patched methods (Harmony may not correctly
        // thread __state across separately-registered patches).
        private static readonly Dictionary<string, List<GameItem>> _restockSavedItems =
            new Dictionary<string, List<GameItem>>();

        // Before each TurnHappened: snapshot items that must survive a potential restock wipe.
        //   • Merchants    → save only player-placed items (tracked by UUID in NPCData.PlayerPlacedItemUuids).
        //                    This covers both Give-button items (itemState=INVENTORY) and Trade-sold items
        //                    (itemState=MERCHANT), while excluding generated stock from being re-added after restock.
        //   • Non-merchants → save ALL items (guards against NeedsRestock inlining wiping their inventory).
        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPrefix]
        public static void Prefix_TurnHappened(GameCharacter __instance)
        {
            if (string.IsNullOrEmpty(__instance.uuid)) return;
            _restockSavedItems.Remove(__instance.uuid);
            if (__instance.items == null || __instance.items.Count == 0) return;

            List<GameItem> toSave;
            if (__instance.isMerchant)
            {
                var data = NPCData.Load(__instance.uuid);
                var playerUuids = data?.PlayerPlacedItemUuids ?? new HashSet<string>();
                toSave = __instance.items.Where(i => !string.IsNullOrEmpty(i.uuid) && playerUuids.Contains(i.uuid)).ToList();
            }
            else
            {
                toSave = __instance.items.ToList();
            }

            if (toSave.Count > 0)
                _restockSavedItems[__instance.uuid] = toSave;
        }

        // After each TurnHappened: restore any items that were wiped by the restock logic.
        // If no wipe occurred (the common case), all items are still present and nothing is added.
        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPostfix]
        public static void Postfix_TurnHappened(GameCharacter __instance)
        {
            if (string.IsNullOrEmpty(__instance.uuid)) return;
            if (!_restockSavedItems.TryGetValue(__instance.uuid, out var saved)) return;
            _restockSavedItems.Remove(__instance.uuid);
            if (saved == null || saved.Count == 0) return;
            if (__instance.items == null) __instance.items = new List<GameItem>();

            int restored = 0;
            foreach (var item in saved)
            {
                if (!__instance.items.Contains(item))
                {
                    item.itemState = GameItem.ItemState.INVENTORY;
                    __instance.items.Add(item);
                    restored++;
                }
            }

            if (restored > 0)
            {
                // For non-merchants whose items were wiped by an inlined NeedsRestock call,
                // set state to FINISHED so "Trade" doesn't trigger spurious GenerateItems().
                if (!__instance.isMerchant)
                    __instance.merchantGenerationState = GameCharacter.MerchantGenerationState.FINISHED;

                string ctx = __instance.isMerchant ? "merchant" : "non-merchant NPC";
                Debug.Log($"[AIROG_NPCExpansion] Preserved {restored} item(s) for {ctx} {__instance.GetPrettyName()} across restock.");
            }
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
                ScenarioUpdater.SaveState(saveDir);
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
                ScenarioUpdater.LoadState(saveDir);
                NPCUI.RefreshAll();

                // Sync all loaded affinities into game sentiment values
                foreach (var kvp in NPCData.LoreCache)
                    SyncAffinityToGame(kvp.Key, kvp.Value);
            }
        }

        // The 07/18 build removed InventoryAndAbilitySelectionPrompter.SellItem; the sell path now
        // routes through GameplayManager.SellItemToMerchant(item, merchant), which hands us the
        // merchant GameCharacter directly (no MerchantInventory traversal needed).
        // Patched on DoSellItemToMerchant rather than SellItemToMerchant: the host's MP command
        // handler (MpCommands.cs) calls DoSellItemToMerchant directly for a non-host client's
        // sale, bypassing SellItemToMerchant entirely. DoSellItemToMerchant is the common
        // downstream of both paths, so patching it here covers host-local and relayed sales alike.
        [HarmonyPatch(typeof(GameplayManager), "DoSellItemToMerchant")]
        [HarmonyPostfix]
        public static void Postfix_SellItem(GameItem item, GameCharacter merchant)
        {
            if (item == null || merchant == null) return;

            var npc = merchant;
            var data = NPCData.Load(npc.uuid);
            if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());

            data.ChangeAffinity(3, $"Sold {item.GetPrettyName()} to them.");
            if (!string.IsNullOrEmpty(item.uuid))
                data.PlayerPlacedItemUuids.Add(item.uuid);
            NPCData.Save(npc.uuid, data);
            SyncAffinityToGame(npc.uuid, data);
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
                    var manager = SS.I?.hackyManager;
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
                    var manager = SS.I?.hackyManager;
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

        // --- NPC Action Menu Injection ---
        // PresentNpcMenu calls GetActions(npc) then dropdownMenu.showMenu().
        // We capture the NPC here and inject our items inside Prefix_showMenu.
        private static GameCharacter _pendingNpcForMenu;

        [HarmonyPatch(typeof(GameplayManager), "PresentNpcMenu")]
        [HarmonyPrefix]
        public static void Prefix_PresentNpcMenu(GameCharacter npc)
        {
            _pendingNpcForMenu = npc;
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

            // 4. Default to bottom bar/dropdown selection.
            // The dropdown is 1-based: slot 0 is "[OPEN-ENDED]", NPCs start at 1.
            // Utils.GetTargetedChar handles the offset (returns null for open-ended).
            if (manager.npcConvoSelectorDropdown != null)
            {
                var chars = manager.GetCharsForNpcConvoSelectorDropdown();
                if (chars != null)
                    return Utils.GetTargetedChar(manager.npcConvoSelectorDropdown.value, chars);
            }
            return null;
        }

        [HarmonyPatch(typeof(DropdownMenu), "showMenu")]
        [HarmonyPrefix]
        public static void Prefix_showMenu(List<DropdownMenuItem> menuItems, DropdownMenu __instance)
        {
            // --- NPC action menu injection ---
            var npc = _pendingNpcForMenu;
            _pendingNpcForMenu = null;
            if (npc != null)
            {
                var manager = __instance.manager ?? SS.I?.hackyManager;
                var npcData = NPCData.Load(npc.uuid);
                bool hasLore = NPCData.HasProfile(npc, npcData);
                bool isAlive = npc.corpseState == GameCharacter.CorpseState.NONE;

                menuItems.Add(new DropdownMenuItem("<color=#ffff00>Examine</color>", () =>
                {
                    NPCExamineUI.OpenFor(npc, manager);
                    return Task.CompletedTask;
                }));

                // Determine if extended NPCData stats/skills have been generated yet
                bool hasNativeProfile = npc.importantData != null && !string.IsNullOrEmpty(npc.importantData.personality);
                bool hasExtendedData  = npcData != null && npcData.Attributes != null
                                        && npcData.Attributes.Values.Any(v => v != 10);
                string profileLabel;
                if (hasExtendedData)
                    profileLabel = "<color=#ff9900>Edit Extended Profile</color>";
                else if (hasNativeProfile)
                    profileLabel = "<color=#ff9900>Generate Extended Stats</color>";
                else
                    profileLabel = "<color=#ff9900>Generate Profile</color>";

                menuItems.Add(new DropdownMenuItem(profileLabel, async () =>
                {
                    if (hasExtendedData)
                        NPCUI.ShowLoreEditor(npc, manager);
                    else
                        await NPCGenerator.GenerateLore(npc, manager.GetContextForQuickActions());
                }));

                if (isAlive && !npc.IsEnemyType())
                {
                    menuItems.Add(new DropdownMenuItem("<color=#00ccff>Inventory & Gear</color>", () =>
                    {
                        NPCEquipmentUI.OpenFor(npc, manager);
                        return Task.CompletedTask;
                    }));

                    if (hasLore)
                    {
                        menuItems.Add(new DropdownMenuItem("<color=#ffd700>Accept Quest</color>", async () =>
                        {
                            await QuestManager.GenerateQuest(npc, npcData, manager);
                        }));
                    }

                    menuItems.Add(new DropdownMenuItem("<color=#aaaaff>Quest Log</color>", () =>
                    {
                        QuestUI.Open(manager);
                        return Task.CompletedTask;
                    }));

                    if (hasLore)
                    {
                        var arcActions = RelationshipArcSystem.GetAvailableArcActions(npc, npcData, manager);
                        foreach (var arcAction in arcActions)
                            menuItems.Add(new DropdownMenuItem(arcAction.str, arcAction.a));
                    }
                }

                if (NPCData.LoreCache.Values.Any(d => d != null && d.IsDeceased))
                {
                    menuItems.Add(new DropdownMenuItem("<color=#ff8888>Hall of Fallen</color>", () =>
                    {
                        HallOfFallenUI.Open(manager);
                        return Task.CompletedTask;
                    }));
                }
                return; // NPC menu handled — skip item-give logic
            }

            // --- Item give injection ---
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

            var itemManager = __instance.manager ?? SS.I?.hackyManager;
            var currentNpc = GetCurrentlySelectedNpc(itemManager);

            if (currentNpc != null && !currentNpc.IsEnemyType())
            {
                string giveText = $"Give to {currentNpc.GetPrettyName()}";
                if (!menuItems.Any(m => m.menuItemText.Contains("Give to") || m.menuItemText.Contains("<color=#00ff00>")))
                {
                    var itemToGive = _lastClickedItemSlot.item;
                    menuItems.Add(new DropdownMenuItem("<color=#00ff00>" + giveText + "</color>", async () =>
                    {
                        await NPCEquipmentUI.GiveItemToNPC(itemToGive, currentNpc, itemManager);
                    }));
                }
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
                var manager = SS.I?.hackyManager;
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
            manager.inventory.RemoveItemIfExists(item.uuid);
            
            // 2. Update Item Entity Data
            item.itemState = GameItem.ItemState.INVENTORY;
            item.parentEnt = npc; 
            item.SetParentEnt(npc);
            
            // 3. Add to NPC
            if (npc.items == null) npc.items = new List<GameItem>();
            npc.items.Add(item);

            // Track as player-placed so it survives merchant restock wipes
            if (!string.IsNullOrEmpty(item.uuid))
            {
                var npcData = NPCData.Load(npc.uuid) ?? NPCData.CreateDefault(npc.GetPrettyName());
                npcData.PlayerPlacedItemUuids.Add(item.uuid);
                NPCData.Save(npc.uuid, npcData);
            }

            // 4. Force UI Refresh
            manager.inventory.RefreshInvDisplay();
            
            Debug.Log($"[AIROG_NPCExpansion] Transfer complete. NPC now has {npc.items.Count} items.");
        }
    }
}
