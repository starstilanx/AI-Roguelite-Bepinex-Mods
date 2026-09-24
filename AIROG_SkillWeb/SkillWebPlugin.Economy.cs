using System;
using System.Collections.Generic;
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

            // Inside a sync the caller saves at the end; outside one, sync (which saves) now.
            SyncBonuses();
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
    }
}
