using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public partial class SkillWebPlugin
    {
        /// <summary>
        /// Safely awards Resonance to the player with ledger-based idempotency checks.
        /// </summary>
        public void AwardResonance(string key, int amount, string reason)
        {
            if (Data == null || string.IsNullOrEmpty(key)) return;
            if (Data.economyLedger.ContainsKey(key)) return;

            int finalAmount = Mathf.RoundToInt(amount * SkillConfig.ResonanceMultiplier);

            Data.resonance += finalAmount;
            Data.resonanceEarnedTotal += finalAmount;
            Data.economyLedger[key] = finalAmount;

            Logger.LogInfo($"[SkillWeb] Awarded {finalAmount} Resonance (base {amount}) for {reason}. Key: {key}");

            // Inside a sync the caller saves at the end; outside one, sync (which saves) now -
            // unless a batch of awards is being applied, which syncs once when it is done.
            if (_deferSync) _syncPending = true;
            else SyncBonuses();
        }

        /// <summary>Allocates a single node. See <see cref="TryBuyNodes"/>.</summary>
        public bool TryBuyNode(WebNode node) => node != null && TryBuyNodes(new List<WebNode> { node });

        /// <summary>
        /// Allocates nodes in order, as queued in the UI: each must be reachable from what is
        /// already allocated plus the nodes before it, and the total must be affordable. All or nothing.
        /// </summary>
        public bool TryBuyNodes(List<WebNode> nodes)
        {
            if (Data == null || nodes == null || nodes.Count == 0) return false;

            var taken = new HashSet<string>();
            int total = 0;
            foreach (var node in nodes)
            {
                if (!WebGraph.CanUnlock(node, Data, taken))
                {
                    Logger.LogWarning($"[SkillWeb] Cannot allocate {node?.name}: not reachable.");
                    return false;
                }
                taken.Add(node.id);
                total += WebGraph.GetUnlockCost(node);
            }
            if (Data.resonance < total)
            {
                Logger.LogWarning($"[SkillWeb] Cannot afford {nodes.Count} node(s) (cost: {total}, held: {Data.resonance})");
                return false;
            }

            Data.resonance -= total;
            foreach (var node in nodes)
            {
                node.unlocked = true;
                node.tier = 1;
                NodeIconGen.EnsureIconAsync(node);
            }

            Logger.LogInfo($"[SkillWeb] Allocated {nodes.Count} node(s) for {total} Resonance.");
            // SyncBonuses charts any ring these allocations reached and recomputes stats/abilities.
            SyncBonuses();
            return true;
        }

        /// <summary>Attempts to upgrade an allocated node's mastery tier.</summary>
        public bool TryUpgradeNode(WebNode node)
        {
            if (Data == null || node == null) return false;
            if (!WebGraph.CanUpgrade(node, SkillConfig)) return false;

            int cost = WebGraph.GetUpgradeCost(node);
            if (Data.resonance < cost) return false;

            Data.resonance -= cost;
            node.tier++;

            Logger.LogInfo($"[SkillWeb] Upgraded node {node.name} to Tier {node.tier}.");
            SyncBonuses();
            return true;
        }

        /// <summary>Picks (or changes) a Mastery's effect. Free; the Mastery must be allocated.</summary>
        public bool TryChooseMastery(WebNode node, int option)
        {
            if (Data == null || node == null || node.type != WebNodeType.Mastery || !node.unlocked) return false;
            if (node.masteryOptions == null || option < 0 || option >= node.masteryOptions.Count) return false;
            node.masteryChoice = option;
            SaveData();
            return true;
        }

        /// <summary>
        /// A node can be refunded when every other allocated node stays connected to the Heart
        /// without it, and no allocated Mastery would be left with an empty cluster.
        /// </summary>
        public bool CanRefund(WebNode node)
        {
            if (Data == null || node == null || !node.unlocked || node.type == WebNodeType.Anchor || node.ring == 0)
                return false;

            var keep = new HashSet<string>();
            foreach (var n in Data.nodes)
                if (n.unlocked && n.ring > 0 && n.id != node.id) keep.Add(n.id);
            return WebGraph.AllConnected(Data, keep);
        }

        /// <summary>Resonance returned by a refund: everything spent, except Keystones return half their price.</summary>
        public static int RefundValue(WebNode node)
        {
            if (node == null) return 0;
            int value = WebGraph.TotalSpent(node);
            if (node.type == WebNodeType.Keystone) value -= WebGraph.GetUnlockCost(node) - WebGraph.GetUnlockCost(node) / 2;
            return value;
        }

        /// <summary>Attempts to refund an allocated node.</summary>
        public bool TryRefundNode(WebNode node)
        {
            if (Data == null || node == null) return false;

            if (!CanRefund(node))
            {
                Logger.LogWarning($"[SkillWeb] Cannot refund node {node.name}: other allocations depend on it.");
                return false;
            }

            int refundValue = RefundValue(node);
            Data.resonance += refundValue;
            node.unlocked = false;
            node.tier = 0;
            if (node.type == WebNodeType.Mastery) node.masteryChoice = -1;

            Logger.LogInfo($"[SkillWeb] Refunded node {node.name} for {refundValue} Resonance.");
            SyncBonuses();
            return true;
        }

        // ── Whole-web operations ────────────────────────────────────────────────

        /// <summary>Resonance currently invested in the web: what a full refund returns.</summary>
        public int TotalInvested()
        {
            if (Data == null) return 0;
            int total = 0;
            foreach (var n in Data.nodes)
                if (IsAllocated(n)) total += WebGraph.TotalSpent(n);
            return total;
        }

        private static bool IsAllocated(WebNode n)
            => n != null && n.unlocked && n.ring > 0 && n.type != WebNodeType.Anchor;

        /// <summary>
        /// Un-allocates every node and returns ALL the Resonance spent on them - price and tier
        /// upgrades, with no Keystone penalty (a single-node refund returns only half a Keystone's
        /// price; clearing the whole board is a fresh start, not a way to dodge that). The web
        /// itself, its charted rings and everything earned are untouched. Returns the amount
        /// refunded, or -1 if there is no web.
        /// </summary>
        public int RefundEverything()
        {
            if (Data == null || !ModSwitch.Enabled) return -1;

            int refund = ClearAllocations();
            Data.resonance += refund;

            Logger.LogInfo($"[SkillWeb] Refunded the whole web: {refund} Resonance returned.");
            SyncBonuses(); // takes back granted perks, recomputes attributes, saves
            return refund;
        }

        /// <summary>
        /// Throws the current web away and generates a new one from a fresh seed, using the current
        /// config (so a changed WebSize / SectorsAtStart / SeedRings takes effect), after refunding
        /// everything in full. Earned Resonance, the ledger, turn count and native-perk bonuses
        /// carry over. The old file is kept beside the save as SkillWeb.json.regen.bak.
        /// Returns the amount refunded, or -1 if it could not run.
        /// </summary>
        public int RegenerateWeb()
        {
            // SyncBonuses does nothing while the mod is switched off, which would leave the web empty.
            if (Data == null || !ModSwitch.Enabled) return -1;

            // SyncBonuses is what builds the replacement, and it needs the owner's perk trees.
            // Without them we would be left holding an empty web.
            if (WebOwner()?.playableData?.perkTrees == null)
            {
                Logger.LogWarning("[SkillWeb] Cannot regenerate the web: the player character is not loaded yet.");
                return -1;
            }

            try
            {
                string path = GetSavePath();
                if (File.Exists(path)) File.Copy(path, path + ".regen.bak", overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[SkillWeb] Could not back up the web before regenerating: {ex.Message}");
            }

            int refund = ClearAllocations();
            Data.resonance += refund;

            // Take the granted perks back while their nodes still exist to be matched against.
            SyncAbilities();

            // Icons are cached on disk by node id, and the new web reuses the same ids ("n12") for
            // entirely different nodes. Left in place, every new node would wear an old node's icon.
            NodeIconGen.ClearRuntimeState();
            NodeIconGen.DeleteAllIcons();
            WebGrower.ClearRefinementQueue();
            LastGrownSectors.Clear();

            Data.layoutSeed = SkillWebData.NewSeed();
            Data.iconsPerWeb = true; // the new web's icons live under its own seed folder only
            Data.nodes.Clear();
            Data.sectors.Clear();
            Data.maxRing = 0;
            Data.worldRadius = 0f;
            Data.nextNodeIndex = 0;
            Data.InvalidateIndex();

            Logger.LogInfo($"[SkillWeb] Regenerating the web (new seed {Data.layoutSeed}); {refund} Resonance refunded.");
            SyncBonuses(); // empty structure -> InitializeWeb, bind perk trees, recompute, save
            LastGrownSectors.Clear(); // a brand-new web has not "grown"; do not toast it

            if (Data.nodes.Count == 0)
            {
                Logger.LogError("[SkillWeb] Regeneration produced no web. The previous one is in SkillWeb.json.regen.bak.");
                return -1;
            }
            return refund;
        }

        /// <summary>Un-allocates every node. Returns the Resonance that was invested; the caller credits it.</summary>
        private int ClearAllocations()
        {
            int refund = 0;
            foreach (var n in Data.nodes)
            {
                if (!IsAllocated(n)) continue;
                refund += WebGraph.TotalSpent(n);
                n.unlocked = false;
                n.tier = 0;
                if (n.type == WebNodeType.Mastery) n.masteryChoice = -1;
            }
            return refund;
        }
    }
}
