using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public static class WebGrower
    {
        private static bool _batchQueueRunning;
        private static readonly Queue<(string sectorId, int ring)> _refinementQueue = new Queue<(string sectorId, int ring)>();

        /// <summary>
        /// Initializes a new or migrated web. Creates the Origin, sets up sectors based on native perk trees
        /// (or fallback defaults), seeds initial rings (Rings 1-3) as fully formed, and Ring 4 as unformed.
        /// </summary>
        public static void InitializeWeb(SkillWebData data, SkillWebConfig config, Dictionary<string, string> nativeTrees)
        {
            data.nodes.Clear();
            data.sectors.Clear();

            // Create Origin Node (Ring 0)
            var origin = new WebNode
            {
                id = "origin",
                type = WebNodeType.Basic,
                name = "Origin",
                description = "The core of your mechanical constellation.",
                sectorId = null,
                ring = 0,
                unlocked = true,
                tier = 0,
                x = 0,
                y = 0
            };
            data.nodes.Add(origin);

            // Determine sectors to build
            int sectorCount = config.SectorsAtStart;
            if (nativeTrees != null && nativeTrees.Count > 0)
            {
                sectorCount = Math.Max(sectorCount, nativeTrees.Count);
            }

            var sectorList = new List<WebSector>();

            if (nativeTrees != null && nativeTrees.Count > 0)
            {
                int index = 0;
                string[] colors = { "#E8734A", "#4A9BE8", "#7AE84A", "#E8D44A", "#C44AE8", "#4AE8C8", "#E84A7A", "#E8A84A" };
                
                foreach (var kvp in nativeTrees)
                {
                    var sector = new WebSector
                    {
                        id = "sector_" + kvp.Key,
                        name = kvp.Value,
                        purpose = "Anchor star discipline grown from your native perks.",
                        colorHex = colors[index % colors.Length],
                        deepestGeneratedRing = 0,
                        anchorPerkTreeUuid = kvp.Key
                    };
                    sectorList.Add(sector);
                    index++;
                }
            }
            else
            {
                // Fallback default sectors
                string[] sectorNames = { "Vigor", "Agility", "Reason", "Subterfuge", "Presence" };
                string[] sectorPurposes = {
                    "Strength and physical endurance.",
                    "Speed, precision, and quick movement.",
                    "Magic, spellcraft, and ancient studies.",
                    "Stealth, poison, and poison traps.",
                    "Diplomacy, commanding voice, and bargaining."
                };
                string[] colors = { "#E8734A", "#4A9BE8", "#7AE84A", "#E8D44A", "#C44AE8" };

                for (int i = 0; i < Math.Min(sectorCount, 5); i++)
                {
                    var sector = new WebSector
                    {
                        id = "sector_fallback_" + i,
                        name = sectorNames[i],
                        purpose = sectorPurposes[i],
                        colorHex = colors[i],
                        deepestGeneratedRing = 0,
                        anchorPerkTreeUuid = null
                    };
                    sectorList.Add(sector);
                }
            }

            data.sectors = sectorList;

            // Align sector angles
            WebLayout.AlignSectors(data.sectors);

            // Generate seeded rings (Rings 1 to SeedRings) as fully formed
            foreach (var sector in data.sectors)
            {
                for (int r = 1; r <= config.SeedRings; r++)
                {
                    GenerateRingForSectorOffline(data, sector, r, isPlaceholder: false);
                }
                
                // Ring 4 (SeedRings + 1) as Unformed Preview
                GenerateRingForSectorOffline(data, sector, config.SeedRings + 1, isPlaceholder: true);
            }

            // Position all nodes deterministically
            RepositionAndRewire(data);

            // Trigger AI refinement queue for initial formed sectors if enabled
            if (config.UseAIGeneration)
            {
                EnqueueInitialRefinements(data);
            }
        }

        /// <summary>
        /// Converts the current unformed preview ring into fully formed nodes, and spawns the next unformed preview ring.
        /// </summary>
        public static void GrowSector(SkillWebData data, WebSector sector, SkillWebConfig config)
        {
            if (data == null || sector == null) return;
            
            int unformedRing = sector.deepestGeneratedRing; // current unformed ring to ignite
            
            // 1. Ignite the unformed ring: overwrite placeholder data with real offline stats
            var placeholderNodes = data.nodes.FindAll(n => n.sectorId == sector.id && n.ring == unformedRing && n.name == "Unformed Star");
            for (int i = 0; i < placeholderNodes.Count; i++)
            {
                var placeholder = placeholderNodes[i];
                var realOfflineNode = NodeGenOffline.GenerateNode(placeholder.id, placeholder.type, sector, unformedRing, i, data.layoutSeed);
                
                placeholder.name = realOfflineNode.name;
                placeholder.description = realOfflineNode.description;
                placeholder.stats = realOfflineNode.stats;
                placeholder.traits = realOfflineNode.traits;
                placeholder.keystoneRule = realOfflineNode.keystoneRule;
                placeholder.tier = realOfflineNode.tier;
                placeholder.aiRefined = false;
            }

            // 2. Generate the next Ring as the new unformed preview ring
            int nextUnformedRing = unformedRing + 1;
            GenerateRingForSectorOffline(data, sector, nextUnformedRing, isPlaceholder: true);
            
            RepositionAndRewire(data);

            // 3. Trigger AI refinement for the newly ignited ring in background
            if (config.UseAIGeneration)
            {
                EnqueueRefinement(sector.id, unformedRing);
            }
        }

        /// <summary>
        /// Ignites the frontier for every sector: whenever the outermost *formed* ring
        /// (deepestGeneratedRing - 1, since the deepest ring is always the unformed preview)
        /// contains a player-unlocked node, its unformed preview ring is grown into real nodes.
        /// This is the single source of truth for lazy growth — call it after any unlock and on
        /// load. Idempotent, and it self-heals saves whose frontier never advanced. Anchor nodes
        /// are ignored: growth is driven only by nodes the player actually purchased.
        /// </summary>
        public static void EnsureFrontierIgnited(SkillWebData data, SkillWebConfig config)
        {
            if (data == null) return;
            foreach (var sector in data.sectors)
            {
                // Guard against any pathological runaway; in practice this grows at most one ring.
                for (int guard = 0; guard < 32; guard++)
                {
                    int frontier = sector.deepestGeneratedRing - 1; // outermost formed ring
                    if (frontier < 1) break;

                    bool frontierReached = data.nodes.Exists(n =>
                        n.sectorId == sector.id && n.ring == frontier &&
                        n.unlocked && n.type != WebNodeType.Anchor && n.name != "Unformed Star");

                    if (!frontierReached) break;
                    GrowSector(data, sector, config);
                }
            }
        }

        /// <summary>
        /// Generates nodes for a specific sector and ring using the offline lexicon.
        /// </summary>
        private static void GenerateRingForSectorOffline(SkillWebData data, WebSector sector, int ring, bool isPlaceholder = false)
        {
            if (ring > sector.deepestGeneratedRing)
            {
                sector.deepestGeneratedRing = ring;
            }

            var recipe = GetRingRecipe(ring);
            for (int i = 0; i < recipe.Count; i++)
            {
                string nodeId = Guid.NewGuid().ToString();
                var node = NodeGenOffline.GenerateNode(nodeId, recipe[i], sector, ring, i, data.layoutSeed);
                
                if (isPlaceholder)
                {
                    node.name = "Unformed Star";
                    node.description = "A faint, unformed star at the edge of the constellation. Ignite adjacent stars to reveal it.";
                    node.stats.Clear();
                    node.traits.Clear();
                    node.keystoneRule = null;
                    node.tier = 0;
                    node.aiRefined = false;
                }

                data.nodes.Add(node);
            }
        }

        private static List<WebNodeType> GetRingRecipe(int ring)
        {
            var types = new List<WebNodeType>();
            if (ring == 1 || ring == 2)
            {
                types.Add(WebNodeType.Basic);
                types.Add(WebNodeType.Basic);
                types.Add(WebNodeType.Basic);
            }
            else if (ring == 3 || ring == 4)
            {
                types.Add(WebNodeType.Basic);
                types.Add(WebNodeType.Basic);
                types.Add(WebNodeType.Notable);
            }
            else if (ring == 5)
            {
                types.Add(WebNodeType.Keystone);
                types.Add(WebNodeType.Notable);
            }
            else // ring 6+
            {
                if ((ring - 5) % 3 == 0)
                {
                    types.Add(WebNodeType.Keystone);
                    types.Add(WebNodeType.Notable);
                }
                else
                {
                    types.Add(WebNodeType.Basic);
                    types.Add(WebNodeType.Basic);
                    types.Add(WebNodeType.Notable);
                }
            }
            return types;
        }

        // ── AI Refinement Queue ──────────────────────────────────────────────────

        public static void EnqueueInitialRefinements(SkillWebData data)
        {
            foreach (var sector in data.sectors)
            {
                // Refine rings 1 to deepest-1 (since deepest is the unformed preview ring)
                for (int r = 1; r < sector.deepestGeneratedRing; r++)
                {
                    EnqueueRefinement(sector.id, r);
                }
            }
        }

        public static void EnqueueRefinement(string sectorId, int ring)
        {
            lock (_refinementQueue)
            {
                if (!_refinementQueue.Contains((sectorId, ring)))
                {
                    _refinementQueue.Enqueue((sectorId, ring));
                }

                if (!_batchQueueRunning)
                {
                    _ = ProcessRefinementQueueAsync();
                }
            }
        }

        private static async Task ProcessRefinementQueueAsync()
        {
            _batchQueueRunning = true;
            try
            {
                while (true)
                {
                    (string sectorId, int ring) target;
                    lock (_refinementQueue)
                    {
                        if (_refinementQueue.Count == 0) break;
                        target = _refinementQueue.Dequeue();
                    }

                    var manager = SS.I?.hackyManager;
                    var plugin = SkillWebPlugin.Instance;
                    if (manager == null || plugin?.Data == null) continue;

                    var data = plugin.Data;
                    var sector = data.GetSector(target.sectorId);
                    if (sector == null) continue;

                    // Find all offline-generated nodes in this cell that are NOT placeholders
                    var offlineNodes = data.nodes.FindAll(n => n.sectorId == target.sectorId && n.ring == target.ring && !n.aiRefined && n.type != WebNodeType.Anchor && n.name != "Unformed Star");
                    if (offlineNodes.Count == 0) continue;

                    var batchTargets = offlineNodes.Select(n => (n.id, n.type)).ToList();
                    var existingSectorNodes = data.nodes.FindAll(n => n.sectorId == target.sectorId);

                    Debug.Log($"[SkillWeb] AI refining batch for sector {sector.name} Ring {target.ring} ({offlineNodes.Count} nodes)...");

                    var aiNodes = await NodeGenAI.GenerateRingBatchAsync(
                        manager, 
                        sector, 
                        target.ring, 
                        batchTargets, 
                        existingSectorNodes, 
                        data.layoutSeed);

                    if (aiNodes != null && aiNodes.Count > 0)
                    {
                        foreach (var aiNode in aiNodes)
                        {
                            var oldNode = data.GetNode(aiNode.id);
                            if (oldNode != null)
                            {
                                // Overwrite stats, name, description, traits, keystoneRule
                                oldNode.name = aiNode.name;
                                oldNode.description = aiNode.description;
                                oldNode.stats = aiNode.stats;
                                oldNode.traits = aiNode.traits;
                                oldNode.keystoneRule = aiNode.keystoneRule;
                                oldNode.aiRefined = true;
                            }
                        }

                        RepositionAndRewire(data);
                        plugin.SaveData();

                        // Refresh UI if it is active in the game
                        if (SkillWebUI.Instance != null && SkillWebUI.Instance.gameObject.activeSelf)
                        {
                            SkillWebUI.Instance.Refresh();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkillWeb] Refinement queue processor failed: " + ex.Message);
            }
            finally
            {
                _batchQueueRunning = false;
            }
        }

        // ── Reposition and Wireframe ──────────────────────────────────────────────

        public static void RepositionAndRewire(SkillWebData data)
        {
            WebLayout.AlignSectors(data.sectors);

            // Group nodes by cell (sector, ring) for neat layout spacing
            var cellGroups = new Dictionary<(string sectorId, int ring), List<WebNode>>();
            foreach (var node in data.nodes)
            {
                if (node.ring == 0) continue;
                var key = (node.sectorId, node.ring);
                if (!cellGroups.ContainsKey(key)) cellGroups[key] = new List<WebNode>();
                cellGroups[key].Add(node);
            }

            // Position nodes within their cell groups
            foreach (var kvp in cellGroups)
            {
                string sectorId = kvp.Key.sectorId;

                var sector = data.GetSector(sectorId);
                var nodesInCell = kvp.Value;

                // Sort by ID to ensure order is deterministic for indexing
                nodesInCell.Sort((a, b) => a.id.CompareTo(b.id));

                for (int i = 0; i < nodesInCell.Count; i++)
                {
                    WebLayout.PositionNode(nodesInCell[i], sector, i, nodesInCell.Count, data.layoutSeed);
                }
            }

            // Special position for origin node
            var origin = data.nodes.Find(n => n.ring == 0);
            if (origin != null)
            {
                origin.x = 0;
                origin.y = 0;
            }

            // Rebuild wireframe connections
            WebLayout.GenerateEdges(data);
        }
    }
}
