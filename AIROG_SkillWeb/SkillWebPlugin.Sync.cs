using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public partial class SkillWebPlugin
    {
        private static string GetAnchorId(string perkUuid) => "anchor:" + perkUuid;
        private static string GetPerkUuidFromAnchor(string anchorId) => anchorId.Replace("anchor:", "");

        /// <summary>
        /// Reads the current actor's native perk trees, imports anchors, updates active/learned state,
        /// recalculates Resonance bonuses, and rebuilds the CachedStats block.
        /// </summary>
        public void SyncBonuses()
        {
            if (Data == null) LoadSaveData();
            if (Data == null) return;

            var actor = CurrentActor();
            var pdata = actor?.playableData;
            if (pdata?.perkTrees == null) return;

            // Step 1: Scan and identify all native trees and learned/active perks
            var nativeTrees = new Dictionary<string, string>();
            var learnedPerks = new Dictionary<string, PerkNode>();
            var activePerkUuids = new HashSet<string>();

            foreach (var pt in pdata.perkTrees)
            {
                if (pt?.rootPerkNode == null) continue;
                nativeTrees[pt.uuid] = pt.GetPrettyName();

                foreach (var pn in pt.GetAllPerkNodes())
                {
                    if (pn == null) continue;
                    if (pn.isLearned)
                    {
                        learnedPerks[pn.uuid] = pn;
                        if (pn.isActivated)
                        {
                            activePerkUuids.Add(pn.uuid);
                        }
                    }
                }
            }

            // Step 2: Initialize web structure if missing or migrated
            if (Data.sectors.Count == 0 || Data.nodes.Count == 0)
            {
                Logger.LogInfo("[SkillWeb] First-time web initialization...");
                WebGrower.InitializeWeb(Data, SkillConfig, nativeTrees);
            }
            else
            {
                // Ensure every native tree has a sector
                bool sectorAdded = false;
                foreach (var kvp in nativeTrees)
                {
                    string sectorId = "sector_" + kvp.Key;
                    if (Data.GetSector(sectorId) == null)
                    {
                        var newSector = new WebSector
                        {
                            id = sectorId,
                            name = kvp.Value,
                            purpose = "Anchor star discipline grown from your native perks.",
                            colorHex = "#4A9BE8",
                            deepestGeneratedRing = 0,
                            anchorPerkTreeUuid = kvp.Key
                        };
                        Data.sectors.Add(newSector);
                        
                        // Seed rings 1-3
                        for (int r = 1; r <= SkillConfig.SeedRings; r++)
                        {
                            WebGrower.GrowSector(Data, newSector, SkillConfig);
                        }
                        sectorAdded = true;
                    }
                }

                if (sectorAdded)
                {
                    WebGrower.RepositionAndRewire(Data);
                }
            }

            // Grow any sector whose outermost formed ring the player has reached. This is the single
            // source of truth for lazy frontier growth (runs on every unlock and on load), and it
            // self-heals saves whose frontier was previously stuck behind the unformed preview ring.
            WebGrower.EnsureFrontierIgnited(Data, SkillConfig);

            // Step 3: Anchor Star import and state sync
            bool nodesAddedOrModified = false;

            // Reset all Anchor nodes to dormant by default
            foreach (var node in Data.nodes)
            {
                if (node.type == WebNodeType.Anchor)
                {
                    node.unlocked = false; // dim / dormant
                }
            }

            // Sync learned perks to Anchor nodes
            foreach (var pt in pdata.perkTrees)
            {
                if (pt?.rootPerkNode == null) continue;
                string sectorId = "sector_" + pt.uuid;

                foreach (var pn in pt.GetAllPerkNodes())
                {
                    if (pn == null || !pn.isLearned) continue;

                    string anchorNodeId = GetAnchorId(pn.uuid);
                    var anchorNode = Data.GetNode(anchorNodeId);

                    int depth = GetPerkDepth(pn);
                    int ring = 1 + depth;

                    // Ensure the anchor node exists
                    if (anchorNode == null)
                    {
                        anchorNode = new WebNode
                        {
                            id = anchorNodeId,
                            type = WebNodeType.Anchor,
                            name = pn.GetPrettyName(),
                            description = pn.GetPotentiallyNullDescription() ?? "",
                            sectorId = sectorId,
                            ring = ring,
                            unlocked = true,
                            tier = 0,
                            aiRefined = false
                        };
                        Data.nodes.Add(anchorNode);
                        nodesAddedOrModified = true;
                        NodeIconGen.EnsureIconAsync(anchorNode);
                    }
                    else
                    {
                        anchorNode.unlocked = true; // active seed
                    }

                    // Heuristic or AI Perk bonus import
                    var pb = Data.perkBonuses.ContainsKey(pn.uuid) ? Data.perkBonuses[pn.uuid] : null;
                    if (pb == null)
                    {
                        pb = new PerkBonus { perkUuid = pn.uuid, perkName = pn.GetPrettyName() };
                        pb.stats = PerkStatDeriver.Heuristic(pn.GetPrettyName(), pn.GetPotentiallyNullDescription() ?? "", SkillConfig.HeuristicBudget);
                        pb.derived = true;
                        Data.perkBonuses[pn.uuid] = pb;

                        if (SkillConfig.UseAIGeneration)
                        {
                            _refineQueue.Enqueue(pn);
                        }
                    }

                    // Copy stats from PerkBonus bridge into Anchor WebNode
                    anchorNode.stats.Clear();
                    foreach (var kvp in pb.stats)
                    {
                        anchorNode.stats[kvp.Key] = kvp.Value;
                    }
                }
            }

            // Grant 1 ⟡ Resonance point for each unique learned perk imported
            foreach (var kvp in learnedPerks)
            {
                string key = $"anchor_import:{kvp.Key}";
                if (!Data.economyLedger.ContainsKey(key))
                {
                    AwardResonance(key, 1, $"importing perk '{kvp.Value.GetPrettyName()}'");
                }
            }

            if (nodesAddedOrModified)
            {
                WebGrower.RepositionAndRewire(Data);
            }

            // Step 4: Rebuild attribute CachedStats with Radiance modifiers
            RecalculateStats(activePerkUuids);

            // Step 5: Reconcile usable abilities from unlocked Keystone/Confluence nodes
            SyncAbilities();

            SaveData();

            // Run async AI derivation queue if items exist
            if (_refineQueue.Count > 0 && !_aiRefinementRunning)
            {
                _ = RefineViaAIAsync();
            }
        }

        private void RecalculateStats(HashSet<string> activePerkUuids)
        {
            Data.CachedStats.Clear();

            Func<string, bool> isAnchorActive = (nodeId) =>
            {
                if (nodeId == null || !nodeId.StartsWith("anchor:")) return false;
                string uuid = GetPerkUuidFromAnchor(nodeId);
                return activePerkUuids.Contains(uuid);
            };

            foreach (var node in Data.nodes)
            {
                // Skip locked web nodes (unlocked basic/notable are unlocked, ring 0 origin is unlocked, anchors are unlocked if learned)
                if (!node.unlocked && node.ring > 0) continue;

                float mult = 1.0f;

                if (node.type == WebNodeType.Anchor)
                {
                    // Scale Anchor star stats by ActiveBonusMultiplier if it's active
                    if (isAnchorActive(node.id))
                    {
                        mult = SkillConfig.ActiveBonusMultiplier;
                    }
                }
                else
                {
                    // Scale normal node stats if adjacent to at least one active Anchor Star
                    bool adjacentToActiveAnchor = false;
                    foreach (var neighborId in node.edges)
                    {
                        if (isAnchorActive(neighborId))
                        {
                            adjacentToActiveAnchor = true;
                            break;
                        }
                    }

                    if (adjacentToActiveAnchor)
                    {
                        mult = SkillConfig.ActiveBonusMultiplier;
                    }

                    // Apply mastery tier multiplier
                    if (node.tier > 0)
                    {
                        // Tier 1: x1.0, Tier 2: x1.5, Tier 3: x2.0
                        mult *= (1f + (node.tier - 1) * 0.5f);
                    }
                }

                foreach (var kvp in node.stats)
                {
                    if (!Enum.TryParse(kvp.Key, true, out SS.PlayerAttribute attr) || attr == SS.PlayerAttribute.Unknown) continue;
                    if (!Data.CachedStats.ContainsKey(attr)) Data.CachedStats[attr] = 0f;
                    Data.CachedStats[attr] += kvp.Value * mult;
                }
            }

            // Clamp CachedStats limits
            var keys = new List<SS.PlayerAttribute>(Data.CachedStats.Keys);
            foreach (var k in keys)
            {
                Data.CachedStats[k] = Mathf.Clamp(Data.CachedStats[k], -SkillConfig.MaxBonusPerAttribute, SkillConfig.MaxBonusPerAttribute);
            }
        }

        private int GetPerkDepth(PerkNode pn)
        {
            int depth = 0;
            var curr = pn.parent;
            while (curr != null)
            {
                depth++;
                curr = curr.parent;
            }
            return depth;
        }
    }
}
