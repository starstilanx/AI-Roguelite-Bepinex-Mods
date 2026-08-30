using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Polar layout for the constellation. Node positions are packed rather than jittered:
    /// every ring's radius is derived from how much arc its fullest cell actually needs, so
    /// nodes can never be squeezed on top of each other no matter how many Anchor stars a
    /// perk tree imports into one (sector, ring) cell.
    /// </summary>
    public static class WebLayout
    {
        /// <summary>Bumped whenever the packing math changes, so old saves get re-laid-out once on load.</summary>
        public const int LayoutVersion = 2;

        // ── Packing constants (all in UI units, matching SkillWebUI node sprites) ────
        /// <summary>Radius of ring 1 when nothing forces it further out.</summary>
        public const float FirstRingRadius = 320f;
        /// <summary>Minimum clearance between the outer edge of one ring band and the inner edge of the next.</summary>
        public const float RingGap = 190f;
        /// <summary>Radial distance between stagger lanes inside a single cell.</summary>
        public const float LaneStep = 170f;
        /// <summary>Empty arc reserved around each node (half on either side) — also keeps labels apart.</summary>
        public const float NodePad = 40f;
        /// <summary>Empty arc kept between two neighbouring sectors' cells on the same ring.</summary>
        public const float CellGutter = 100f;
        /// <summary>Deterministic radial wobble, kept small enough that the gaps above absorb it.</summary>
        public const float RadialJitter = 8f;
        /// <summary>Cells wider than one arc row get staggered across up to this many lanes.</summary>
        public const int MaxLanes = 3;

        /// <summary>
        /// Node footprint, mirroring the sprite sizes SkillWebUI.DrawNode uses. Keep the two in sync.
        /// </summary>
        public static float NodeDiameter(WebNode node)
        {
            if (node == null) return 80f;
            switch (node.type)
            {
                case WebNodeType.Keystone:   return 130f;
                case WebNodeType.Anchor:     return 110f;
                case WebNodeType.Confluence: return 110f;
                case WebNodeType.Notable:    return 100f;
                default:                     return 80f;
            }
        }

        /// <summary>Arc a node consumes on its lane, including its share of the padding.</summary>
        private static float NodeArc(WebNode node) => NodeDiameter(node) + NodePad;

        /// <summary>Half-thickness of a cell's lane stack.</summary>
        private static float LaneExtent(int lanes) => (lanes - 1) * LaneStep * 0.5f;

        /// <summary>
        /// Recalculates center angles and spans for all sectors.
        /// </summary>
        public static void AlignSectors(List<WebSector> sectors)
        {
            if (sectors == null || sectors.Count == 0) return;
            float span = (2f * Mathf.PI) / sectors.Count;
            for (int i = 0; i < sectors.Count; i++)
            {
                sectors[i].angleCenter = i * span;
                sectors[i].angleSpan = span;
            }
        }

        /// <summary>
        /// Positions every node in the web. Rings are laid out from the inside out; each ring picks
        /// the lane count and radius that make its widest cell fit inside its sector slice without
        /// touching either the neighbouring sectors or the previous ring.
        /// </summary>
        public static void LayoutNodes(SkillWebData data)
        {
            if (data == null || data.nodes == null) return;

            AlignSectors(data.sectors);

            // Origin sits dead center.
            float originHalf = 40f;
            foreach (var node in data.nodes)
            {
                if (node.ring != 0) continue;
                node.angle = 0f;
                node.radius = 0f;
                node.x = 0f;
                node.y = 0f;
                originHalf = Mathf.Max(originHalf, NodeDiameter(node) * 0.5f);
            }

            // Bucket nodes into (sector, ring) cells.
            var cells = new Dictionary<string, List<WebNode>>();
            var sectorIdsSeen = new HashSet<string>();
            int maxRing = 0;
            foreach (var node in data.nodes)
            {
                if (node.ring <= 0) continue;
                if (node.ring > maxRing) maxRing = node.ring;
                string sectorId = node.sectorId ?? "";
                sectorIdsSeen.Add(sectorId);
                string key = sectorId + "|" + node.ring;
                if (!cells.TryGetValue(key, out var list))
                {
                    list = new List<WebNode>();
                    cells[key] = list;
                }
                list.Add(node);
            }

            // Sector ids that show up on nodes but no longer match any live sector (e.g. the
            // sector was deleted, or a hand-edited save left a dangling id) need to fall back to
            // the same orphan bucket as a null/empty sectorId, not be silently left unpositioned.
            var validSectorIds = new HashSet<string>();
            if (data.sectors != null)
                foreach (var s in data.sectors) validSectorIds.Add(s.id);
            var staleSectorIds = new List<string>();
            foreach (var sid in sectorIdsSeen)
                if (!string.IsNullOrEmpty(sid) && !validSectorIds.Contains(sid))
                    staleSectorIds.Add(sid);

            float prevOuterEdge = originHalf;

            for (int ring = 1; ring <= maxRing; ring++)
            {
                // Collect this ring's cells (one per sector, plus one for orphaned nodes).
                var ringCells = new List<(WebSector sector, List<WebNode> nodes)>();

                if (data.sectors != null)
                {
                    foreach (var sector in data.sectors)
                    {
                        if (!cells.TryGetValue(sector.id + "|" + ring, out var list) || list.Count == 0) continue;
                        list.Sort(CompareNodes);
                        ringCells.Add((sector, list));
                    }
                }

                // Nodes whose sector was deleted (null/empty sectorId, or one that no longer
                // matches any current sector) still need somewhere sane to live.
                List<WebNode> orphans = null;
                if (cells.TryGetValue("|" + ring, out var nullSectorOrphans))
                    orphans = new List<WebNode>(nullSectorOrphans);
                foreach (var staleSectorId in staleSectorIds)
                {
                    if (!cells.TryGetValue(staleSectorId + "|" + ring, out var stale)) continue;
                    if (orphans == null) orphans = new List<WebNode>();
                    orphans.AddRange(stale);
                }
                if (orphans != null && orphans.Count > 0)
                {
                    orphans.Sort(CompareNodes);
                    ringCells.Add((null, orphans));
                }

                if (ringCells.Count == 0) continue;

                // Pick the lane count that puts this ring's outer edge closest in.
                int bestLanes = 1;
                float bestRadius = 0f;
                float bestOuter = float.MaxValue;

                for (int lanes = 1; lanes <= MaxLanes; lanes++)
                {
                    float extent = LaneExtent(lanes);
                    float radius = Mathf.Max(
                        prevOuterEdge + RingGap + extent + RadialJitter,
                        ring == 1 ? FirstRingRadius : 0f);

                    foreach (var cell in ringCells)
                    {
                        float span = (cell.sector != null && cell.sector.angleSpan > 0.01f)
                            ? cell.sector.angleSpan
                            : 2f * Mathf.PI;

                        // The innermost lane has the shortest arc, so it drives the requirement.
                        float required = ((WidestRowArc(cell.nodes, lanes) + CellGutter) / span) + extent + RadialJitter;
                        if (required > radius) radius = required;
                    }

                    float outer = radius + extent + RadialJitter;
                    if (outer < bestOuter)
                    {
                        bestOuter = outer;
                        bestRadius = radius;
                        bestLanes = lanes;
                    }
                }

                // Place each cell's nodes along their lanes.
                foreach (var cell in ringCells)
                {
                    float angleCenter = cell.sector != null ? cell.sector.angleCenter : 0f;

                    for (int lane = 0; lane < bestLanes; lane++)
                    {
                        float laneRadius = bestRadius + (lane - (bestLanes - 1) * 0.5f) * LaneStep;
                        if (laneRadius < 1f) laneRadius = 1f;

                        float rowArc = 0f;
                        for (int i = lane; i < cell.nodes.Count; i += bestLanes) rowArc += NodeArc(cell.nodes[i]);
                        if (rowArc <= 0f) continue;

                        // Walk the row outward from the sector's center line.
                        float cursor = angleCenter - (rowArc * 0.5f) / laneRadius;
                        for (int i = lane; i < cell.nodes.Count; i += bestLanes)
                        {
                            var node = cell.nodes[i];
                            float halfArc = (NodeArc(node) * 0.5f) / laneRadius;

                            cursor += halfArc;
                            node.angle = NormalizeAngle(cursor);
                            cursor += halfArc;

                            node.radius = laneRadius + StableJitter(node.id, data.layoutSeed) * RadialJitter;
                            node.x = node.radius * Mathf.Cos(node.angle);
                            node.y = node.radius * Mathf.Sin(node.angle);
                        }
                    }
                }

                prevOuterEdge = bestRadius + LaneExtent(bestLanes) + RadialJitter;
            }

            data.layoutVersion = LayoutVersion;
        }

        /// <summary>
        /// Arc consumed by the fullest lane when a cell is dealt round-robin across <paramref name="lanes"/> lanes.
        /// </summary>
        private static float WidestRowArc(List<WebNode> nodes, int lanes)
        {
            float widest = 0f;
            for (int lane = 0; lane < lanes; lane++)
            {
                float arc = 0f;
                for (int i = lane; i < nodes.Count; i += lanes) arc += NodeArc(nodes[i]);
                if (arc > widest) widest = arc;
            }
            return widest;
        }

        /// <summary>
        /// Deterministic -1..1 wobble. Uses an FNV-1a hash rather than string.GetHashCode so the
        /// same save lays out identically on every machine and every run.
        /// </summary>
        private static float StableJitter(string id, long seed)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (id != null)
                {
                    for (int i = 0; i < id.Length; i++)
                    {
                        h ^= id[i];
                        h *= 16777619u;
                    }
                }
                h ^= (uint)seed;
                h *= 16777619u;
                h ^= (uint)(seed >> 32);
                h *= 16777619u;
                return ((h % 2001u) / 1000f) - 1f;
            }
        }

        /// <summary>Stable ordering inside a cell: Anchors first (they mirror the native tree), then by id.</summary>
        private static int CompareNodes(WebNode a, WebNode b)
        {
            bool anchorA = a.type == WebNodeType.Anchor;
            bool anchorB = b.type == WebNodeType.Anchor;
            if (anchorA != anchorB) return anchorA ? -1 : 1;
            return string.CompareOrdinal(a.id, b.id);
        }

        /// <summary>
        /// Recalculates and regenerates all node edges (connections) deterministically based on coordinates.
        /// </summary>
        public static void GenerateEdges(SkillWebData data)
        {
            if (data == null) return;

            // Clear existing edges
            foreach (var node in data.nodes)
            {
                node.edges.Clear();
            }

            var origin = data.nodes.Find(n => n.ring == 0);

            // Find max ring in the entire web
            int maxRing = 0;
            foreach (var node in data.nodes)
            {
                if (node.ring > maxRing) maxRing = node.ring;
            }

            // Connect Ring 0 (Origin) to all Ring 1 nodes
            if (origin != null)
            {
                var ring1 = data.nodes.FindAll(n => n.ring == 1);
                foreach (var r1 in ring1)
                {
                    Connect(origin, r1);
                }
            }

            // Inward-outward radial connections
            for (int r = 2; r <= maxRing; r++)
            {
                foreach (var sector in data.sectors)
                {
                    var currentNodes = data.nodes.FindAll(n => n.ring == r && n.sectorId == sector.id);
                    var prevNodes = data.nodes.FindAll(n => n.ring == r - 1 && n.sectorId == sector.id);

                    if (prevNodes.Count == 0) continue;

                    foreach (var node in currentNodes)
                    {
                        if (node.type == WebNodeType.Keystone)
                        {
                            // Keystones always connect to exactly one closest node in the previous ring (creates a choke-point)
                            var bestParent = FindClosest(node, prevNodes);
                            if (bestParent != null) Connect(node, bestParent);
                        }
                        else
                        {
                            // Basic/Notables connect to 1-2 parent nodes
                            var sortedParents = new List<WebNode>(prevNodes);
                            sortedParents.Sort((a, b) => AngularDistance(node.angle, a.angle).CompareTo(AngularDistance(node.angle, b.angle)));

                            if (sortedParents.Count > 0)
                            {
                                Connect(node, sortedParents[0]);
                            }
                            if (sortedParents.Count > 1 && AngularDistance(node.angle, sortedParents[1].angle) < (sector.angleSpan * 0.4f))
                            {
                                Connect(node, sortedParents[1]);
                            }
                        }
                    }
                }
            }

            // Lateral (same-ring) connections to knit the web together (excluding Keystones)
            for (int r = 1; r <= maxRing; r++)
            {
                var ringNodes = data.nodes.FindAll(n => n.ring == r && n.type != WebNodeType.Keystone);
                if (ringNodes.Count < 2) continue;

                // Sort ring nodes by angle around the circle
                ringNodes.Sort((a, b) => a.angle.CompareTo(b.angle));

                for (int i = 0; i < ringNodes.Count; i++)
                {
                    var n1 = ringNodes[i];
                    var n2 = ringNodes[(i + 1) % ringNodes.Count];

                    float diff = AngularDistance(n1.angle, n2.angle);
                    // Lateral link threshold (depends on sector count)
                    float threshold = (2f * Mathf.PI / Math.Max(3, data.sectors.Count)) * 0.7f;
                    // Lane-staggering means "same ring" no longer implies "same radius" — without this,
                    // two angularly-close nodes in different lanes get a long diagonal edge cutting
                    // across the lane gap instead of a short, tidy cross-link.
                    float radialDiff = Mathf.Abs(n1.radius - n2.radius);
                    if (diff < threshold && radialDiff < LaneStep * 0.75f)
                    {
                        Connect(n1, n2);
                    }
                }
            }
        }

        private static void Connect(WebNode a, WebNode b)
        {
            if (a == null || b == null) return;
            if (!a.edges.Contains(b.id)) a.edges.Add(b.id);
            if (!b.edges.Contains(a.id)) b.edges.Add(a.id);
        }

        private static WebNode FindClosest(WebNode target, List<WebNode> candidates)
        {
            WebNode best = null;
            float minDist = float.MaxValue;
            foreach (var c in candidates)
            {
                float dist = AngularDistance(target.angle, c.angle);
                if (dist < minDist)
                {
                    minDist = dist;
                    best = c;
                }
            }
            return best;
        }

        public static float AngularDistance(float a1, float a2)
        {
            float diff = Mathf.Abs(a1 - a2);
            if (diff > Mathf.PI) diff = (2f * Mathf.PI) - diff;
            return diff;
        }

        public static float NormalizeAngle(float angle)
        {
            while (angle < 0f) angle += 2f * Mathf.PI;
            while (angle >= 2f * Mathf.PI) angle -= 2f * Mathf.PI;
            return angle;
        }
    }
}
