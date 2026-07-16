using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public static class WebGraph
    {
        /// <summary>
        /// Cost of purchasing a node in Resonance points (⟡).
        /// </summary>
        public static int GetUnlockCost(WebNode node)
        {
            if (node.type == WebNodeType.Anchor) return 0; // Anchors unlock via native perks, cannot be bought
            switch (node.type)
            {
                case WebNodeType.Basic: return 1;
                case WebNodeType.Notable:
                case WebNodeType.Confluence: return 2;
                case WebNodeType.Keystone: return 3;
                default: return 1;
            }
        }

        /// <summary>
        /// Cost of upgrading a node's mastery tier (Basic/Notable only).
        /// </summary>
        public static int GetUpgradeCost(WebNode node)
        {
            if (node.type == WebNodeType.Keystone || node.type == WebNodeType.Anchor) return 0;
            if (node.tier == 1) return 1; // Tier 1 -> Tier 2 costs 1
            if (node.tier == 2) return 2; // Tier 2 -> Tier 3 costs 2
            return 0;
        }

        /// <summary>
        /// Checks if a node is buyable with Resonance.
        /// Admissible if locked, not an Anchor, and connected to at least one unlocked/active node.
        /// </summary>
        public static bool CanUnlock(WebNode node, SkillWebData data)
        {
            if (node == null || node.unlocked || node.type == WebNodeType.Anchor || node.ring == 0) return false;
            if (node.name == "Unformed Star") return false;

            foreach (var edgeId in node.edges)
            {
                var neighbor = data.GetNode(edgeId);
                if (neighbor != null && (neighbor.unlocked || neighbor.ring == 0))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a node is upgradeable.
        /// </summary>
        public static bool CanUpgrade(WebNode node, SkillWebConfig config)
        {
            if (node == null || !node.unlocked || !config.MasteryTiersEnabled) return false;
            if (node.type == WebNodeType.Keystone || node.type == WebNodeType.Anchor) return false;
            return node.tier < 3;
        }

        /// <summary>
        /// Finds the cheapest Resonance path from any unlocked node to the target locked node.
        /// Uses Dijkstra's algorithm.
        /// Returns (path, totalCost) or (null, 0) if no path exists.
        /// </summary>
        public static (List<WebNode> path, int cost) FindCheapestPath(WebNode target, SkillWebData data)
        {
            if (target == null || target.unlocked || target.type == WebNodeType.Anchor)
            {
                return (null, 0);
            }

            // Dijkstra setup
            var dist = new Dictionary<string, int>();
            var prev = new Dictionary<string, string>();
            var queue = new SortedSet<(int cost, string id)>(Comparer<(int cost, string id)>.Create((a, b) =>
            {
                int cmp = a.cost.CompareTo(b.cost);
                if (cmp != 0) return cmp;
                return a.id.CompareTo(b.id);
            }));

            // Initialize unlocked nodes with distance 0, others with infinity
            foreach (var node in data.nodes)
            {
                if (node.unlocked || node.ring == 0)
                {
                    dist[node.id] = 0;
                    queue.Add((0, node.id));
                }
                else
                {
                    dist[node.id] = int.MaxValue;
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Min;
                queue.Remove(current);

                int currCost = current.cost;
                string currId = current.id;

                if (currId == target.id) break;
                if (currCost == int.MaxValue) break;

                var currNode = data.GetNode(currId);
                if (currNode == null) continue;

                foreach (var edgeId in currNode.edges)
                {
                    var neighbor = data.GetNode(edgeId);
                    // Cannot path through locked Anchors
                    if (neighbor == null || (neighbor.type == WebNodeType.Anchor && !neighbor.unlocked)) continue;

                    int edgeCost = neighbor.unlocked ? 0 : GetUnlockCost(neighbor);
                    int alt = currCost + edgeCost;

                    if (alt < dist[neighbor.id])
                    {
                        queue.Remove((dist[neighbor.id], neighbor.id));
                        dist[neighbor.id] = alt;
                        prev[neighbor.id] = currId;
                        queue.Add((alt, neighbor.id));
                    }
                }
            }

            if (dist[target.id] == int.MaxValue)
            {
                return (null, 0);
            }

            // Reconstruct path
            var path = new List<WebNode>();
            string trace = target.id;
            while (prev.ContainsKey(trace))
            {
                var n = data.GetNode(trace);
                if (n != null && !n.unlocked)
                {
                    path.Add(n);
                }
                trace = prev[trace];
            }
            path.Reverse();

            return (path, dist[target.id]);
        }
    }
}
