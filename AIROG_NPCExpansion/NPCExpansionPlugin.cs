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
        public const string PLUGIN_VERSION = "4.6.0";

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

            // Only raises a flag: the switch can report a change from inside another mod's
            // IsEnabled() call, mid-load, where touching UI or mod state would be unsafe.
            ModSwitch.Subscribe(() => _switchChanged = true);
        }

        private static volatile bool _switchChanged;

        private void Update()
        {
            if (_switchChanged)
            {
                _switchChanged = false;
                // UI only. Data for the new save is loaded by Postfix_ReadSaveFile, never from here.
                if (!ModSwitch.Enabled) CloseAllPanels();
            }
            if (ModSwitch.Enabled) NPCUI.Update();
        }

        private static void CloseAllPanels()
        {
            try
            {
                NPCExamineUI.ForceClose();
                NPCEquipmentUI.Hide();
                QuestUI.Hide();
                HallOfFallenUI.Hide();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPCExpansion] Closing panels failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Drops everything held in memory for the previous game. Runs as each game scene starts,
        /// before the game reads a save, so a NEW game (which never goes through ReadSaveFile) and a
        /// co-op join do not inherit the last save's NPCs, quests, taught skills or turn counter.
        /// </summary>
        private static void ResetSessionState()
        {
            NPCData.ResetSession();
            QuestManager.AllQuests.Clear();
            NPCTeachingSystem.PlayerTaughtSkills.Clear();
            ScenarioUpdater.ResetSession();
            _restockSavedItems.Clear();
            _pendingNpcForMenu = null;
            _lastClickedItemSlot = null;
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
        public static async void Postfix_GenerateImportantData(GameCharacter __instance, Task __result)
        {
            try
            {
                if (!ModSwitch.Enabled) return;
                // The new-game menu generates character details too; there is no game to track yet.
                if (SS.I?.hackyManager == null) return;
                // GenerateImportantData is async: a postfix runs when it first yields, which is before
                // the AI has answered and before importantData is assigned. Wait for the real result.
                string saveAtStart = SS.I?.saveSubDirAsArg;
                if (__result != null) await __result;
                if (!string.Equals(saveAtStart, SS.I?.saveSubDirAsArg, StringComparison.Ordinal)) return;

                var npc = __instance;
                if (npc == null || npc.importantData == null || npc.IsMainPlayer()) return;
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
                    string context = SS.I?.hackyManager?.GetContextForQuickActions() ?? "";
                    // The game has just written the personality and background: keep them.
                    await NPCGenerator.GenerateLore(npc, context, keepProfileText: true);
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

        // (A postfix on NpcActionsHandler.UpdateCurrentNpc used to live here, injecting the old
        // per-NPC panel through NPCUI.TryInject. The game still has that method but nothing calls
        // it any more - the bottom-bar talk selector replaced the old NPC view - so the patch bound
        // and never ran. Removed; tools\patch-audit.ps1 reports such hooks as [NOCALLERS].)

        // The game also receives every Esc press (GameplayManager.EscKey): it closes whichever of
        // its own windows is open and otherwise opens the settings menu. It does not know the
        // dossier, so the press that closed the dossier (or just left one of its text boxes) also
        // popped the game menu. Skipped while the dossier is open or handled Esc this frame; a
        // game modal on top keeps its normal handling. EscKey is a plain void method.
        [HarmonyPatch(typeof(GameplayManager), "EscKey")]
        [HarmonyPrefix]
        public static bool Prefix_EscKey()
        {
            try
            {
                if (MessageModal.I != null && MessageModal.I.IsShowing()) return true;
                if (ModalWindow.I != null && ModalWindow.I.IsShowing()) return true;
                if (NTextPromptModal.I != null && NTextPromptModal.I.IsShowing()) return true;

                bool ours = (NPCExamineUI.Instance != null && NPCExamineUI.Instance.IsOpen)
                         || NPCExamineUI.EscConsumedFrame == Time.frameCount;
                return !ours;
            }
            catch { return true; }
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

                // Start is async and this runs at its first yield, which is before it reads the save
                // (see GameplayManager.StartGame), so a load refills what is cleared here.
                try { ResetSessionState(); }
                catch (Exception ex) { Debug.LogWarning($"[NPCExpansion] Session reset failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(GameplayManager), "UpdateNpcConvoSelectorDropdown")]
        [HarmonyPostfix]
        public static void Postfix_UpdateNpcConvoSelectorDropdown(GameplayManager __instance)
        {
            try
            {
                if (__instance != null && ModSwitch.Enabled)
                    NPCUI.TryUpdateTextForBottomBar(__instance);
            }
            catch (Exception ex) { Debug.LogWarning($"[NPCExpansion] Bottom-bar update failed: {ex.Message}"); }
        }

        // Hook into dropdown change to maybe update text if needed
        [HarmonyPatch(typeof(GameplayManager), "OnNpcConvoSelectorDropdownValueChanged")]
        [HarmonyPostfix]
        public static void Postfix_OnNpcConvoSelectorDropdownValueChanged(GameplayManager __instance)
        {
             // This method runs when user selects new NPC in the bottom bar.
             // We can check the selected NPC and inject text.
             try
             {
                 if (__instance != null && ModSwitch.Enabled)
                     NPCUI.TryUpdateTextForBottomBar(__instance);
             }
             catch (Exception ex) { Debug.LogWarning($"[NPCExpansion] Bottom-bar update failed: {ex.Message}"); }
        }

        // Prevents the merchant restock cycle from firing on non-merchant NPCs.
        // First line of defence: NeedsRestock() is patched to always return false for non-merchants.
        // NOTE: This may not fire if NeedsRestock() is JIT-inlined into TurnHappened; the
        // Prefix/Postfix static-dictionary approach below acts as a belt-and-suspenders safety net.
        [HarmonyPatch(typeof(GameCharacter), "NeedsRestock")]
        [HarmonyPrefix]
        public static bool Prefix_NeedsRestock(GameCharacter __instance, ref bool __result)
        {
            if (!ModSwitch.Enabled) return true;
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
            // Runs for every character on every turn, inside the game's turn event: must not throw.
            try
            {
                if (string.IsNullOrEmpty(__instance.uuid)) return;
                _restockSavedItems.Remove(__instance.uuid);
                if (!ModSwitch.Enabled) return;
                if (__instance.items == null || __instance.items.Count == 0) return;

                List<GameItem> toSave;
                if (__instance.isMerchant)
                {
                    var data = NPCData.Load(__instance.uuid);
                    var playerUuids = data?.PlayerPlacedItemUuids ?? new HashSet<string>();
                    toSave = __instance.items.Where(i => i != null && !string.IsNullOrEmpty(i.uuid) && playerUuids.Contains(i.uuid)).ToList();
                }
                else
                {
                    toSave = __instance.items.ToList();
                }

                if (toSave.Count > 0)
                    _restockSavedItems[__instance.uuid] = toSave;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Restock snapshot failed: {ex.Message}");
            }
        }

        // After each TurnHappened: restore any items that were wiped by the restock logic.
        // If no wipe occurred (the common case), all items are still present and nothing is added.
        [HarmonyPatch(typeof(GameCharacter), "TurnHappened")]
        [HarmonyPostfix]
        public static void Postfix_TurnHappened(GameCharacter __instance)
        {
            try
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
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Restock restore failed: {ex.Message}");
            }
        }



        // ---- Nemesis System: Trigger on player death ----
        [HarmonyPatch(typeof(GameplayManager), "DeadLogic")]
        [HarmonyPrefix]
        public static void Prefix_DeadLogic(GameplayManager __instance)
        {
            try
            {
                if (!ModSwitch.Enabled) return;
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
        // No injected args: WriteSaveFile's first parameter is MmCtxGetter since the 07/28 build,
        // so declaring it as GameplayManager was an unchecked type mismatch.
        public static void Postfix_WriteSaveFile()
        {
            // Not gated by the per-save switch: a switched-off mod still keeps its files current.
            try
            {
                // Everything held in memory belongs to the save the session started on; never write
                // it into a folder the game has since been pointed at.
                if (SS.I != null && NPCData.CacheBelongsToCurrentSave)
                {
                    string saveDir = Path.Combine(SS.I.saveTopLvlDir, SS.I.saveSubDirAsArg);
                    NPCData.SaveSessionLore(saveDir);
                    QuestManager.SaveQuests();
                    NPCDeathTracker.SaveMemorial();
                    NPCTeachingSystem.SavePlayerSkills();
                    ScenarioUpdater.SaveState(saveDir);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Saving mod data failed: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(SaveIO), "ReadSaveFile")]
        [HarmonyPostfix]
        public static void Postfix_ReadSaveFile(string saveSubDir, GameSaveData __result)
        {
            try
            {
                if (__result == null || SS.I == null) return;
                // The main menu also calls ReadSaveFile, to rebuild a missing preview for ANY save
                // in the list. Only the save actually being loaded may replace our state.
                if (!string.Equals(saveSubDir, SS.I.saveSubDirAsArg, StringComparison.Ordinal)) return;

                string saveDir = Path.Combine(SS.I.saveTopLvlDir, saveSubDir);
                NPCData.LoadSessionLore(saveDir);
                // No-op unless the load just migrated old entries; then GenContext, which reads the
                // file rather than our memory, sees the new shape from the first prompt on.
                NPCData.FlushSessionLore();
                QuestManager.LoadQuests(saveDir);
                NPCTeachingSystem.LoadPlayerSkills(saveDir);
                ScenarioUpdater.LoadState(saveDir);
                NPCUI.RefreshAll();

                // Sync all loaded affinities into game sentiment values
                foreach (var kvp in NPCData.LoreCache)
                    SyncAffinityToGame(kvp.Key, kvp.Value);
            }
            catch (Exception ex)
            {
                // Must not escape: this runs inside the game's own load.
                Debug.LogWarning($"[AIROG_NPCExpansion] Loading mod data failed: {ex.Message}");
            }
        }

        // The 07/18 build removed InventoryAndAbilitySelectionPrompter.SellItem; the sell path now
        // routes through GameplayManager.SellItemToMerchant(item, merchant), which hands us the
        // merchant GameCharacter directly (no MerchantInventory traversal needed).
        // Patched on DoSellItemToMerchant rather than SellItemToMerchant: the host's MP command
        // handler (MpCommands.cs) calls DoSellItemToMerchant directly for a non-host client's
        // sale, bypassing SellItemToMerchant entirely. DoSellItemToMerchant is the common
        // downstream of both paths, so patching it here covers host-local and relayed sales alike.
        // Items already on the merchant when a sale starts. Since the 11/18 build items stack, so the
        // thing that lands on the merchant can be a split-off copy with a new uuid, not the `item`
        // argument (which, for a partial sale, is the stack the player keeps).
        private static HashSet<GameItem> _merchantItemsBeforeSale;

        [HarmonyPatch(typeof(GameplayManager), "DoSellItemToMerchant")]
        [HarmonyPrefix]
        public static void Prefix_SellItem(GameCharacter merchant)
        {
            _merchantItemsBeforeSale = null;
            try
            {
                if (ModSwitch.Enabled && merchant?.items != null)
                    _merchantItemsBeforeSale = new HashSet<GameItem>(merchant.items);
            }
            catch { /* tracking only */ }
        }

        [HarmonyPatch(typeof(GameplayManager), "DoSellItemToMerchant")]
        [HarmonyPostfix]
        public static void Postfix_SellItem(GameItem item, GameCharacter merchant)
        {
            var before = _merchantItemsBeforeSale;
            _merchantItemsBeforeSale = null;
            try
            {
                if (!ModSwitch.Enabled) return;
                if (item == null || merchant == null) return;

                var npc = merchant;
                var data = NPCData.Load(npc.uuid);
                if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());

                int oldAffinity = data.Affinity;
                data.ChangeAffinity(3, $"Sold {item.GetPrettyName()} to them.");
                if (before != null && npc.items != null)
                {
                    foreach (var sold in npc.items)
                        if (sold != null && !before.Contains(sold) && !string.IsNullOrEmpty(sold.uuid))
                            data.PlayerPlacedItemUuids.Add(sold.uuid);
                }
                NPCData.Save(npc.uuid, data);
                SyncAffinityToGame(npc.uuid, data);

                var manager = SS.I?.hackyManager;
                if (manager != null) RelationshipArcSystem.CheckArcAdvancement(npc, data, manager, oldAffinity);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Sale tracking failed: {ex.Message}");
            }
        }

        // Every player action on an NPC (talk, gift, attack) feeds affinity, interaction history,
        // arc milestones, ripples, gossip and death tracking from here.
        //
        // This used to be a postfix on ProcessInteractionInfoNoTryStr. Since the 07/28 multiplayer
        // build the game calls ProcessTurnNoTryStr directly and the old method is a wrapper nothing
        // calls, so none of the above ever happened and the dossier's Bonds tab stayed empty.
        [HarmonyPatch(typeof(GameplayManager), "ProcessTurnNoTryStr")]
        [HarmonyPostfix]
        public static void Postfix_ProcessTurnNoTryStr(List<TurnAction> batch, Task<GameEventResult> __result)
        {
            try
            {
                if (!ModSwitch.Enabled || batch == null) return;

                // The method is async, so this runs at its first yield: before the turn resolves.
                // Health is read now so the handler can tell afterwards whether the NPC was hurt.
                var pending = new List<(InteractionInfo info, GameCharacter npc, long hpBefore, bool wasHostile)>();
                foreach (var action in batch)
                {
                    var info = action?.info;
                    if (info?.interacterInfo == null) continue;
                    // Player characters (your own, or another player's in co-op) are not NPCs.
                    if (info.interacteeInfo?.interacteeEntity is GameCharacter npc && !npc.IsMainPlayer() && !npc.IsMpOwnedPc())
                        pending.Add((info, npc, npc.GetHealth(), npc.IsEnemyType()));
                }
                _ = ApplyInteractionsWhenTurnEnds(pending, __result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Interaction tracking failed: {ex.Message}");
            }
        }

        private static async Task ApplyInteractionsWhenTurnEnds(
            List<(InteractionInfo info, GameCharacter npc, long hpBefore, bool wasHostile)> pending, Task<GameEventResult> turn)
        {
            try
            {
                string saveAtStart = SS.I?.saveSubDirAsArg;
                // A null result is an action the game blocked as implausible: nothing happened.
                GameEventResult result = turn != null ? await turn : null;
                if (result == null) return;
                var manager = SS.I?.hackyManager;
                if (manager == null || !ModSwitch.Enabled) return;
                if (!string.Equals(saveAtStart, SS.I.saveSubDirAsArg, StringComparison.Ordinal)) return;

                foreach (var (info, npc, hpBefore, wasHostile) in pending)
                    ApplyInteraction(info, npc, hpBefore, wasHostile, manager);
                ApplyStoryOpinions(result, manager);
                NPCData.FlushSessionLore();

                // Quest completion is judged on the finished story text of the turn. (It used to read
                // the raw AI reply from a patch on AIAsker.GenerateTxtNoTryStrStyle: that is JSON in the
                // unified flow, and turns sent with a scene image go through a different method entirely.)
                string story = result.storyTurn?.resultingStoryStr;
                if (!string.IsNullOrWhiteSpace(story) && QuestManager.HasActiveQuests)
                    _ = QuestManager.CheckCompletion(story, manager);
            }
            catch (OperationCanceledException) { /* the player cancelled the turn */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AIROG_NPCExpansion] Interaction tracking failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The storyteller's own verdicts on how a turn changed an NPC's opinion of the player (the
        /// "npc_sentiments" entries behind the game's "sentiment +5" lines) also move our affinity.
        /// Without this only actions aimed at an NPC through the talk dropdown or a menu counted, and
        /// someone who plays in free text never saw the relationship arc move at all.
        /// </summary>
        private static void ApplyStoryOpinions(GameEventResult result, GameplayManager manager)
        {
            if (result.npcsOpinionChanges == null) return;
            foreach (var change in result.npcsOpinionChanges)
            {
                var npc = change.Key?.apiVal;
                int delta = change.Value.Item1;
                var toward = change.Value.Item2; // null = toward the acting player
                if (npc == null || delta == 0 || string.IsNullOrEmpty(npc.uuid)) continue;
                if (npc.IsMainPlayer() || npc.IsMpOwnedPc()) continue;
                if (toward != null && !toward.IsMainPlayer()) continue; // our affinity is "toward you" only

                var data = NPCData.Load(npc.uuid) ?? NPCData.CreateDefault(npc.GetPrettyName());
                int oldAffinity = data.Affinity;
                data.ChangeAffinity(delta, delta > 0
                    ? $"Thought better of you after what happened (+{delta})."
                    : $"Thought worse of you after what happened ({delta}).");
                NPCData.Save(npc.uuid, data);
                RelationshipArcSystem.CheckArcAdvancement(npc, data, manager, oldAffinity);
                if (npc.corpseState == GameCharacter.CorpseState.NONE)
                    NPCSecretSystem.CheckAutoReveal(npc, data, manager);
            }
        }

        private static void ApplyInteraction(InteractionInfo interactionInfo, GameCharacter npc, long hpBefore, bool wasHostile, GameplayManager manager)
        {
            var data = NPCData.Load(npc.uuid);
            if (data == null) data = NPCData.CreateDefault(npc.GetPrettyName());

            var interType = interactionInfo.interacterInfo.interacterType;
            int oldAffinity = data.Affinity; // capture before ChangeAffinity
            int affinityDelta = 0;
            bool died = npc.corpseState != GameCharacter.CorpseState.NONE;
            bool hurt = died || npc.GetHealth() < hpBefore;
            bool attacked = false;

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
            // An ability is not always an attack (heals and buffs use the same action), so it only
            // counts against the player when it actually hurt the NPC.
            else if (interType == InteracterInfo.InteracterType.ATTACK_WITH_ITEM
                     || (interType == InteracterInfo.InteracterType.ABILITY && hurt))
            {
                attacked = true;
                affinityDelta = -10;
                string weapon = interactionInfo.interacterInfo.item?.GetPrettyName()
                                ?? interactionInfo.interacterInfo.ability?.GetPrettyName() ?? "something";
                data.ChangeAffinity(affinityDelta, $"Was attacked by the player with {weapon}.");
            }

            if (affinityDelta != 0)
            {
                NPCData.Save(npc.uuid, data);
                SyncAffinityToGame(npc.uuid, data);

                // Social Ripple + Gossip + Arc Advancement + Secret Auto-Reveal
                SocialRippleSystem.Process(npc.uuid, npc.GetPrettyName(), affinityDelta, manager);
                // Fighting a hostile is not a scandal: without this every attack on a monster became
                // "Player acted against <monster>" and cost affinity with each NPC the rumour reached.
                // (Judged on how they stood BEFORE the turn: a friend you attack turns hostile during it.)
                if (!wasHostile)
                    WorldGossipSystem.SeedPlayerGossip(npc.uuid, npc.GetPrettyName(), affinityDelta);
                RelationshipArcSystem.CheckArcAdvancement(npc, data, manager, oldAffinity);
                if (!died) NPCSecretSystem.CheckAutoReveal(npc, data, manager);
            }

            // Death Detection: the NPC did not survive this turn
            if (died && !data.IsDeceased)
                NPCDeathTracker.OnNpcDied(npc, attacked ? "the player" : "unknown causes", ScenarioUpdater.GlobalTurn, manager, data);
        }

        // --- NPC Action Menu Injection ---
        // PresentNpcMenu calls GetActions(npc) then dropdownMenu.showMenu().
        // We capture the NPC here and inject our items inside Prefix_showMenu.
        private static GameCharacter _pendingNpcForMenu;

        [HarmonyPatch(typeof(GameplayManager), "PresentNpcMenu")]
        [HarmonyPrefix]
        public static void Prefix_PresentNpcMenu(GameCharacter npc)
        {
            // The game uses this same menu for the player's own portrait and, in co-op, for other
            // players' characters. Those are not NPCs: no dossier, quests or gear for them.
            _pendingNpcForMenu = null;
            try
            {
                if (ModSwitch.Enabled && npc != null && !npc.IsMainPlayer() && !npc.IsMpOwnedPc())
                    _pendingNpcForMenu = npc;
            }
            catch { /* menu opens without our entries */ }
        }

        // PresentNpcMenu opens no menu at all when the character has no actions (clicking your own
        // portrait, or any character while input is locked). Without this the pending NPC survived
        // and its Dossier/Quest entries were appended to the next unrelated menu (an item, a chest).
        [HarmonyPatch(typeof(GameplayManager), "PresentNpcMenu")]
        [HarmonyPostfix]
        public static void Postfix_PresentNpcMenu()
        {
            _pendingNpcForMenu = null;
        }

        // --- Gear System Helper ---
        private static ItemSlot _lastClickedItemSlot;

        [HarmonyPatch(typeof(GameplayManager), "OnItemSlotClicked")]
        [HarmonyPrefix]
        public static bool Prefix_OnItemSlotClicked(ItemSlot itemSlot)
        {
            _lastClickedItemSlot = null;
            try
            {
                // Slots in our own gear window are clones of the game's inventory slot, so a click on
                // one also reaches this handler, which would open the PLAYER's equip/use/drop menu for
                // an item the NPC is holding. The window handles its own clicks.
                var gear = NPCEquipmentUI.Instance;
                if (itemSlot != null && gear != null && gear._window != null && itemSlot.transform.IsChildOf(gear._window.transform))
                    return false;

                if (ModSwitch.Enabled) _lastClickedItemSlot = itemSlot;
            }
            catch { /* fall through to the game's handler */ }
            return true;
        }

        // The item menu, when there is one, opens inside OnItemSlotClicked. A click that opens none
        // (right click, merchant stock) must not leave its slot behind for a later menu to pick up.
        [HarmonyPatch(typeof(GameplayManager), "OnItemSlotClicked")]
        [HarmonyPostfix]
        public static void Postfix_OnItemSlotClicked()
        {
            _lastClickedItemSlot = null;
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
            if (!ModSwitch.Enabled || menuItems == null)
            {
                _lastClickedItemSlot = null;
                return;
            }
            if (npc != null)
            {
                var manager = __instance.manager ?? SS.I?.hackyManager;
                var npcData = NPCData.Load(npc.uuid);
                bool hasLore = NPCData.HasProfile(npc, npcData);
                bool isAlive = npc.corpseState == GameCharacter.CorpseState.NONE;

                menuItems.Add(new DropdownMenuItem("<color=#ffff00>Dossier</color>", () =>
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
                        NPCExamineUI.OpenFor(npc, manager, NPCExamineUI.Tab.Profile, edit: true);
                    else
                    {
                        // Fills in stats, traits and goals around whatever personality/background
                        // already exists; only the dossier's Regenerate rewrites those two.
                        await NPCGenerator.GenerateLore(npc, manager?.GetContextForQuickActions(), keepProfileText: true);
                    }
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
                            // npcData is null for an NPC the game profiled itself; the quest prompt needs an entry.
                            await QuestManager.GenerateQuest(npc, NPCData.LoadOrCreate(npc), manager);
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

            // Not for player characters or playable party members: those have a real inventory the
            // game moves items into itself, and our gift lands in the separate NPC gear list.
            if (currentNpc != null && !currentNpc.IsEnemyType()
                && !currentNpc.IsMainPlayer() && !currentNpc.IsMpOwnedPc() && !currentNpc.IsPlayable())
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
            if (!ModSwitch.Enabled) return true; // no gear effects: let the game's own method run

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
            if (!ModSwitch.Enabled) return;
            if (causeOfEvent != null && causeOfEvent.currentEntity is GameCharacter npc)
            {
                var data = NPCData.Load(npc.uuid);
                if (data != null && data.EquippedUuids != null && npc.items != null && data.EquippedUuids.TryGetValue("WEAPON1", out string weaponUuid))
                {
                    var weapon = npc.items.Find(i => i != null && i.uuid == weaponUuid);
                    if (weapon != null)
                    {
                        var difficulty = __instance.GetDifficulty();
                        long weaponDmg = Utils.CalculatePlayerDamage(npc.level, new CauseOfEvent(weapon), npc.level, difficulty);
                        long baseDmg = Utils.CalculatePlayerDamage(npc.level, new CauseOfEvent(npc), npc.level, difficulty);
                        
                        if (baseDmg > 0 && weaponDmg > baseDmg)
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

        [HarmonyPatch(typeof(GameplayManager), "GetCharsForNpcConvoSelectorDropdown")]
        [HarmonyPrefix]
        public static bool Prefix_GetCharsForNpcConvoSelectorDropdown(GameplayManager __instance, ref List<GameCharacter> __result)
        {
            if (__instance.currentPlace == null)
            {
                // If currentPlace is null, return just followers (if any) or an empty list.
                // scenarioState.followers is the live list; playerCharacter.pcGameEntity is the
                // legacy player object and is null on current saves.
                __result = __instance.scenarioState?.followers?.ToList() ?? new List<GameCharacter>();
                return false; // Skip original method
            }
            return true; // Run original method
        }


        public static void TransferItemToNpc(GameItem item, GameCharacter npc, GameplayManager manager)
        {
            if (item == null || npc == null || manager == null) return;

            // The gift turn that ran just before this can consume the item or merge it into another
            // stack (stacks are new in the 11/18 build); either way the object has been torn down.
            if (string.IsNullOrEmpty(item.uuid) || SS.I?.uuidToGameEntityMap == null || !SS.I.uuidToGameEntityMap.ContainsKey(item.uuid))
            {
                Debug.Log("[AIROG_NPCExpansion] Item no longer exists after the gift turn; nothing left to hand over.");
                return;
            }

            Debug.Log($"[AIROG_NPCExpansion] Transferring {item.GetPrettyName()} to {npc.GetPrettyName()} directly.");

            // 1. Take it away from whoever holds it now. inventory.RemoveItemIfExists only removes the
            // inventory panel's tile; the item stayed in the owner's real inventory (invItems /
            // equippedItems) and came back on the next load, so every gift duplicated the item.
            // DetachFromOwner is what the game's own transfers use; it also refreshes the panels.
            item.DetachFromOwner();

            // 2. Update Item Entity Data
            item.itemState = GameItem.ItemState.INVENTORY;
            item.parentEnt = npc;
            item.SetParentEnt(npc);

            // 3. Add to NPC
            if (npc.items == null) npc.items = new List<GameItem>();
            if (!npc.items.Contains(item)) npc.items.Add(item);

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

    /// <summary>
    /// The per-save master switch: the "NPC Expansion" row of GenContext's Mods checklist.
    /// GenContext is a soft dependency, so every touch of its types lives in its own
    /// non-inlined method behind a try/catch; when the assembly is missing the mod is simply on.
    /// </summary>
    internal static class ModSwitch
    {
        private static bool _unavailable;

        public static bool Enabled
        {
            get
            {
                if (_unavailable) return true;
                try { return Query(); }
                catch { _unavailable = true; return true; }
            }
        }

        /// <summary>Calls <paramref name="onChanged"/> when this mod's switch flips or another save's switches load. May be raised mid-load and off the main thread: only set a flag in it.</summary>
        public static void Subscribe(Action onChanged)
        {
            if (_unavailable) return;
            try { DoSubscribe(onChanged); }
            catch { _unavailable = true; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool Query() =>
            AIROG_GenContext.ModToggles.IsEnabled(AIROG_GenContext.ModToggles.Keys.NPCExpansion);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DoSubscribe(Action onChanged)
        {
            AIROG_GenContext.ModToggles.Changed += (key, enabled) =>
            {
                if (key == AIROG_GenContext.ModToggles.Keys.NPCExpansion) onChanged();
            };
            AIROG_GenContext.ModToggles.Reloaded += onChanged;
        }
    }
}
