using System;
using System.Collections.Generic;

namespace AIROG_SkillWeb
{
    public static class WebGraph
    {
        /// <summary>Cost of allocating a node, in Resonance.</summary>
        public static int GetUnlockCost(WebNode node)
        {
            if (node == null) return 0;
            switch (node.type)
            {
                case WebNodeType.Anchor: return 0; // gates are fed by native perks, never bought
                case WebNodeType.Travel:
                case WebNodeType.Basic: return 1;
                case WebNodeType.Mastery: return 2;
                case WebNodeType.Notable: return 3;
                case WebNodeType.Confluence: return 4;
                case WebNodeType.Keystone: return 5;
                default: return 1;
            }
        }

        /// <summary>Cost of the next mastery-tier upgrade: tier 1 to 2 costs 2, tier 2 to 3 costs 3.</summary>
        public static int GetUpgradeCost(WebNode node)
        {
            if (node == null || !node.IsTiered) return 0;
            if (node.tier == 1) return 2;
            if (node.tier == 2) return 3;
            return 0;
        }

        /// <summary>Everything spent on a node so far: its price plus any tier upgrades.</summary>
        public static int TotalSpent(WebNode node)
        {
            if (node == null) return 0;
            int spent = GetUnlockCost(node);
            if (node.tier >= 2) spent += 2;
            if (node.tier >= 3) spent += 3;
            return spent;
        }

        /// <summary>Allocated, or free by construction (the Heart and every sector gate), or pending.</summary>
        public static bool IsTaken(WebNode node, ICollection<string> pending = null)
        {
            if (node == null) return false;
            return node.unlocked || node.ring == 0 || (pending != null && pending.Contains(node.id));
        }

        /// <summary>
        /// Whether a locked node can be allocated now: charted, not a gate, and touching an
        /// allocated (or pending) node. Masteries instead need any allocated node in their cluster.
        /// </summary>
        public static bool CanUnlock(WebNode node, SkillWebData data, ICollection<string> pending = null)
        {
            if (node == null || data == null || IsTaken(node, pending)) return false;
            if (node.type == WebNodeType.Anchor || node.ring == 0) return false;
            if (!data.IsCharted(node)) return false;

            if (node.type == WebNodeType.Mastery)
            {
                if (node.clusterId < 0) return false;
                foreach (var n in data.nodes)
                    if (n.clusterId == node.clusterId && n.type != WebNodeType.Mastery && IsTaken(n, pending))
                        return true;
                return false;
            }

            foreach (var edgeId in node.edges)
                if (IsTaken(data.GetNode(edgeId), pending)) return true;
            return false;
        }

        public static bool CanUpgrade(WebNode node, SkillWebConfig config)
        {
            if (node == null || !node.unlocked || !config.MasteryTiersEnabled) return false;
            return node.IsTiered && node.tier < 3;
        }

        /// <summary>
        /// Cheapest chain of locked nodes that would reach <paramref name="target"/> from what is
        /// already allocated (or pending), via charted nodes only. Returns (null, 0) when the
        /// target is unreachable or is a Mastery (those aren't reached along edges).
        /// </summary>
        public static (List<WebNode> path, int cost) FindCheapestPath(WebNode target, SkillWebData data, ICollection<string> pending = null)
        {
            if (target == null || data == null || IsTaken(target, pending)) return (null, 0);
            if (target.type == WebNodeType.Anchor || target.type == WebNodeType.Mastery || !data.IsCharted(target)) return (null, 0);

            var dist = new Dictionary<string, int>();
            var prev = new Dictionary<string, string>();
            var queue = new SortedSet<(int cost, string id)>(Comparer<(int cost, string id)>.Create((a, b) =>
            {
                int cmp = a.cost.CompareTo(b.cost);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.id, b.id);
            }));

            foreach (var node in data.nodes)
            {
                if (IsTaken(node, pending)) { dist[node.id] = 0; queue.Add((0, node.id)); }
            }

            while (queue.Count > 0)
            {
                var current = queue.Min;
                queue.Remove(current);
                if (current.id == target.id) break;

                var currNode = data.GetNode(current.id);
                if (currNode == null) continue;

                foreach (var edgeId in currNode.edges)
                {
                    var nb = data.GetNode(edgeId);
                    if (nb == null || nb.type == WebNodeType.Mastery || nb.type == WebNodeType.Anchor) continue;
                    if (!IsTaken(nb, pending) && !data.IsCharted(nb)) continue;

                    int alt = current.cost + (IsTaken(nb, pending) ? 0 : GetUnlockCost(nb));
                    if (!dist.TryGetValue(nb.id, out int old) || alt < old)
                    {
                        if (dist.ContainsKey(nb.id)) queue.Remove((old, nb.id));
                        dist[nb.id] = alt;
                        prev[nb.id] = current.id;
                        queue.Add((alt, nb.id));
                    }
                }
            }

            if (!dist.TryGetValue(target.id, out int total)) return (null, 0);

            var path = new List<WebNode>();
            string trace = target.id;
            while (trace != null)
            {
                var n = data.GetNode(trace);
                if (n == null || IsTaken(n, pending)) break;
                path.Add(n);
                prev.TryGetValue(trace, out trace);
            }
            path.Reverse();
            return (path, total);
        }

        /// <summary>
        /// True if every node in <paramref name="keep"/> is still connected to the allocated web
        /// through allocated/kept nodes (Masteries: through their cluster). Used to decide whether
        /// a node can be refunded or un-pended without stranding anything beyond it.
        /// </summary>
        public static bool AllConnected(SkillWebData data, HashSet<string> keep)
        {
            var reached = new HashSet<string>();
            var queue = new Queue<WebNode>();
            foreach (var n in data.nodes)
            {
                if (n.ring == 0) { reached.Add(n.id); queue.Enqueue(n); }
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var eid in cur.edges)
                {
                    if (reached.Contains(eid) || !keep.Contains(eid)) continue;
                    var nb = data.GetNode(eid);
                    if (nb == null || nb.type == WebNodeType.Mastery) continue;
                    reached.Add(eid);
                    queue.Enqueue(nb);
                }
            }

            var liveClusters = new HashSet<int>();
            foreach (var id in reached)
            {
                var n = data.GetNode(id);
                if (n != null && n.clusterId >= 0) liveClusters.Add(n.clusterId);
            }

            foreach (var id in keep)
            {
                if (reached.Contains(id)) continue;
                var n = data.GetNode(id);
                if (n == null) continue;
                if (n.type == WebNodeType.Mastery && liveClusters.Contains(n.clusterId)) continue;
                return false;
            }
            return true;
        }
    }
}
