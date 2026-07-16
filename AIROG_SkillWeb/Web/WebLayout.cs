using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public static class WebLayout
    {
        // Radial distances per ring
        public const float RingRadiusStep = 180f;

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
        /// Positions a node deterministically in polar and Cartesian space based on its sector, ring, and index.
        /// </summary>
        public static void PositionNode(WebNode node, WebSector sector, int nodeIndex, int totalNodesInCell, long layoutSeed)
        {
            if (node.ring == 0)
            {
                node.angle = 0f;
                node.radius = 0f;
                node.x = 0f;
                node.y = 0f;
                return;
            }

            // Unique deterministic seed combining layout seed, sector, ring, and node index
            int sectorHash = sector != null ? sector.id.GetHashCode() : 0;
            int combinedSeed = (int)(layoutSeed ^ (sectorHash * 397) ^ (node.ring * 17) ^ (nodeIndex * 7));
            var rng = new System.Random(combinedSeed);

            // Compute base radius and jitter it slightly
            float baseRadius = node.ring * RingRadiusStep;
            // Add a radial offset (slightly larger for Notables and Keystones)
            float radialOffset = 0f;
            if (node.type == WebNodeType.Notable) radialOffset = 15f;
            else if (node.type == WebNodeType.Keystone) radialOffset = 30f;
            
            float radiusJitter = ((float)rng.NextDouble() - 0.5f) * 20f;
            node.radius = baseRadius + radialOffset + radiusJitter;

            // Spacing inside the sector slice
            float angleCenter = sector != null ? sector.angleCenter : 0f;
            float angleSpan = sector != null ? sector.angleSpan : 2f * Mathf.PI;

            // Calculate even distribution angle within sector slice
            float t = (totalNodesInCell > 1) ? (float)nodeIndex / (totalNodesInCell - 1) : 0.5f;
            if (totalNodesInCell == 1) t = 0.5f;

            // Map t from 0..1 to angle range [center - span/3, center + span/3] to leave boundary space
            float minAngle = angleCenter - (angleSpan * 0.35f);
            float maxAngle = angleCenter + (angleSpan * 0.35f);
            float baseAngle = Mathf.Lerp(minAngle, maxAngle, t);

            // Seeded angle jitter
            float angleJitterMax = (totalNodesInCell > 1) ? (angleSpan * 0.1f) : (angleSpan * 0.05f);
            float angleJitter = ((float)rng.NextDouble() - 0.5f) * angleJitterMax;

            node.angle = NormalizeAngle(baseAngle + angleJitter);

            // Calculate Cartesian coordinates
            node.x = node.radius * Mathf.Cos(node.angle);
            node.y = node.radius * Mathf.Sin(node.angle);
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
                    if (diff < threshold)
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
