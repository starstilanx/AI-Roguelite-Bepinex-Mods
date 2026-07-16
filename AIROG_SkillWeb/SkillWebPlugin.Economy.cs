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

            if (Data.economyLedger.ContainsKey(key))
            {
                // Already awarded
                return;
            }

            // Apply multiplier configuration
            int finalAmount = Mathf.RoundToInt(amount * SkillConfig.ResonanceMultiplier);

            Data.resonance += finalAmount;
            Data.resonanceEarnedTotal += finalAmount;
            Data.economyLedger[key] = finalAmount;

            Logger.LogInfo($"[SkillWeb] Awarded {finalAmount} Resonance (base {amount}) for {reason}. Key: {key}");
            
            // Recompute stats and save
            SyncBonuses();
        }

        /// <summary>
        /// Attempts to purchase a locked node with Resonance.
        /// </summary>
        public bool TryBuyNode(WebNode node)
        {
            if (Data == null || node == null) return false;

            int cost = WebGraph.GetUnlockCost(node);
            if (Data.resonance < cost)
            {
                Logger.LogWarning($"[SkillWeb] Cannot afford node {node.name} (cost: {cost}, held: {Data.resonance})");
                return false;
            }

            if (!WebGraph.CanUnlock(node, Data))
            {
                Logger.LogWarning($"[SkillWeb] Adjacency check failed for node {node.name}");
                return false;
            }

            Data.resonance -= cost;
            node.unlocked = true;
            node.tier = 1;
            NodeIconGen.EnsureIconAsync(node);

            Logger.LogInfo($"[SkillWeb] Unlocked node {node.name} for {cost} Resonance.");
            // SyncBonuses → WebGrower.EnsureFrontierIgnited grows the frontier if this unlock reached
            // the outermost formed ring (the deepest ring is the unformed preview, so reaching it is
            // always reaching deepestGeneratedRing - 1 — the old direct == check here never fired).
            SyncBonuses();
            return true;
        }

        /// <summary>
        /// Attempts to upgrade an unlocked node's mastery tier.
        /// </summary>
        public bool TryUpgradeNode(WebNode node)
        {
            if (Data == null || node == null) return false;

            int cost = WebGraph.GetUpgradeCost(node);
            if (Data.resonance < cost) return false;

            if (!WebGraph.CanUpgrade(node, SkillConfig)) return false;

            Data.resonance -= cost;
            node.tier++;

            Logger.LogInfo($"[SkillWeb] Upgraded node {node.name} to Tier {node.tier}.");
            SyncBonuses();
            return true;
        }

        /// <summary>
        /// Connectivity-aware refund checker. Returns true if removing this node leaves
        /// all other unlocked nodes connected to the Origin.
        /// </summary>
        public bool CanRefund(WebNode node)
        {
            if (Data == null || node == null || !node.unlocked || node.type == WebNodeType.Anchor || node.ring == 0)
            {
                return false;
            }

            // Temporarily lock node
            node.unlocked = false;

            // Find all other unlocked nodes that need to remain connected
            var remainingUnlocked = Data.nodes.FindAll(n => (n.unlocked || n.ring == 0) && n.id != node.id);

            var reached = new HashSet<string>();
            var queue = new Queue<string>();

            queue.Enqueue("origin");
            reached.Add("origin");

            while (queue.Count > 0)
            {
                var currId = queue.Dequeue();
                var currNode = Data.GetNode(currId);
                if (currNode == null) continue;

                foreach (var edgeId in currNode.edges)
                {
                    var neighbor = Data.GetNode(edgeId);
                    // Path only through unlocked/active neighbors (including active anchors)
                    if (neighbor != null && neighbor.unlocked && !reached.Contains(neighbor.id))
                    {
                        reached.Add(neighbor.id);
                        queue.Enqueue(neighbor.id);
                    }
                }
            }

            // Restore unlock state
            node.unlocked = true;

            // The refund is valid if BFS reached all remaining unlocked nodes
            return reached.Count == remainingUnlocked.Count;
        }

        /// <summary>
        /// Attempts to refund a purchased node.
        /// Refunding leaf nodes returns full Resonance cost. Keystones return half.
        /// </summary>
        public bool TryRefundNode(WebNode node)
        {
            if (Data == null || node == null) return false;

            if (!CanRefund(node))
            {
                Logger.LogWarning($"[SkillWeb] Cannot refund node {node.name}: not a leaf node.");
                return false;
            }

            int buyCost = WebGraph.GetUnlockCost(node);
            int refundValue = buyCost;

            if (node.type == WebNodeType.Keystone)
            {
                refundValue = buyCost / 2; // refund at half cost
            }

            // Refund any upgrades cost
            if (node.tier > 1)
            {
                for (int t = 1; t < node.tier; t++)
                {
                    refundValue += t; // cost of Tier 2 is 1, Tier 3 is 2
                }
            }

            Data.resonance += refundValue;
            node.unlocked = false;
            node.tier = 0;

            Logger.LogInfo($"[SkillWeb] Refunded node {node.name} for {refundValue} Resonance.");
            SyncBonuses();
            return true;
        }
    }
}
