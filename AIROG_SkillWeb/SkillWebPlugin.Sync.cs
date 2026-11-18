using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIROG_SkillWeb
{
    public partial class SkillWebPlugin
    {
        /// <summary>Set while SyncBonuses runs, so Resonance awarded mid-sync doesn't recurse into another sync.</summary>
        private bool _syncing;

        /// <summary>Sectors that charted a new ring during the most recent sync; the UI reports and clears it.</summary>
        public List<WebSector> LastGrownSectors { get; } = new List<WebSector>();

        /// <summary>
        /// Reads the current actor's native perk trees, (re)builds the web if needed, binds new perk
        /// trees to sectors, charts newly reached rings, feeds learned perks into their sector's
        /// Anchor Star, and rebuilds CachedStats and granted abilities.
        /// </summary>
        public void SyncBonuses()
        {
            if (_syncing) return;
            if (!ModSwitch.Enabled) return; // switched off for this save: the stored web is left exactly as it is
            // Also reload when Data is a previous save's web (see DataIsForActiveSave).
            if (Data == null || !DataIsForActiveSave()) LoadSaveData();
            if (Data == null) return;

            var actor = WebOwner();
            var pdata = actor?.playableData;
            if (pdata?.perkTrees == null) return;

            _syncing = true;
            try
            {
                // Step 1: scan native trees and learned/engaged perks.
                var nativeTrees = new Dictionary<string, string>();
                var learnedByTree = new Dictionary<string, List<PerkNode>>();
                var activePerkUuids = new HashSet<string>();

                foreach (var pt in pdata.perkTrees)
                {
                    if (pt?.rootPerkNode == null) continue;
                    nativeTrees[pt.uuid] = pt.GetPrettyName();
                    var learned = new List<PerkNode>();
                    foreach (var pn in pt.GetAllPerkNodes())
                    {
                        if (pn == null || !pn.isLearned) continue;
                        learned.Add(pn);
                        // Under Perks v2 isActivated means "enabled passive" - the game refuses to
                        // activate an ACTIVE-type perk - and enabled passives are a limited choice
                        // (maxActivePerks), which is what the multiplier rewards. Active-type perks
                        // deliberately do not earn it: "is on the 16-slot hotbar" would be nearly
                        // free, and the game does not refresh anything when the hotbar changes, so
                        // the bonus would sit stale until some unrelated sync.
                        if (pn.isActivated) activePerkUuids.Add(pn.uuid);
                    }
                    learnedByTree[pt.uuid] = learned;
                }

                // Step 2: structure. Older schemas were migrated to an empty web (with a refund) on load.
                if (Data.sectors.Count == 0 || Data.nodes.Count == 0)
                {
                    Logger.LogInfo("[SkillWeb] Generating a new web...");
                    WebGrower.InitializeWeb(Data, SkillConfig, nativeTrees);
                }
                // Also after creation: a character with more trees than sector slots binds the extras here.
                WebGrower.BindNativeTrees(Data, nativeTrees);

                foreach (var s in WebGrower.EnsureFrontierCharted(Data, SkillConfig))
                    if (!LastGrownSectors.Contains(s)) LastGrownSectors.Add(s);

                // Step 3: learned perks -> PerkBonus bridge -> their sector's Anchor Star.
                foreach (var node in Data.nodes)
                {
                    if (!node.IsGate) continue;
                    node.stats.Clear();
                    node.feedingPerks.Clear();
                    node.hasActivePerk = false;
                }

                foreach (var sector in Data.sectors)
                {
                    var gate = Data.GetNode("gate:" + sector.id);
                    if (gate == null) continue;

                    foreach (var treeUuid in sector.perkTreeUuids)
                    {
                        if (!learnedByTree.TryGetValue(treeUuid, out var learned)) continue;
                        foreach (var pn in learned)
                        {
                            var pb = EnsurePerkBonus(pn);
                            bool active = activePerkUuids.Contains(pn.uuid);
                            // Perks v2 perks have ranks 1-3 bought with perk points (0 = granted by
                            // the story, not yet invested in). Each rank past the first adds
                            // PerkRankBonusStep of the perk's base bonus to its Anchor Star.
                            int rank = Mathf.Clamp(pn.rank, 0, PerksV2.MAX_RANK);
                            float mult = (active ? SkillConfig.ActiveBonusMultiplier : 1f)
                                       * (1f + Mathf.Max(0, rank - 1) * Mathf.Max(0f, SkillConfig.PerkRankBonusStep));
                            foreach (var kv in pb.stats)
                                gate.stats[kv.Key] = (gate.stats.TryGetValue(kv.Key, out float v) ? v : 0f) + kv.Value * mult;
                            gate.feedingPerks.Add(new FeedingPerk { perkUuid = pn.uuid, name = pn.GetPrettyName(), active = active, rank = rank, stats = pb.stats });
                            if (active) gate.hasActivePerk = true;
                        }
                    }

                    if (gate.feedingPerks.Count > 0) NodeIconGen.EnsureIconAsync(gate);
                }

                // 1 Resonance for each unique learned perk imported.
                foreach (var list in learnedByTree.Values)
                {
                    foreach (var pn in list)
                    {
                        string key = $"anchor_import:{pn.uuid}";
                        if (!Data.economyLedger.ContainsKey(key))
                            AwardResonance(key, 1, $"importing perk '{pn.GetPrettyName()}'");
                    }
                }

                // Step 4: attributes.
                RecalculateStats();

                // Step 5: usable abilities from allocated Keystones/Confluences.
                SyncAbilities();

                SaveData();
            }
            finally
            {
                _syncing = false;
            }

            if (_refineQueue.Count > 0 && !_aiRefinementRunning)
                _ = RefineViaAIAsync();
        }

        private PerkBonus EnsurePerkBonus(PerkNode pn)
        {
            if (Data.perkBonuses.TryGetValue(pn.uuid, out var pb) && pb != null)
            {
                // Still only heuristic (the request was interrupted or failed in an earlier session): try once more.
                if (!pb.aiRefined && SkillConfig.UseAIGeneration && _refineRequested.Add(pn.uuid)) _refineQueue.Enqueue(pn);
                return pb;
            }
            pb = new PerkBonus { perkUuid = pn.uuid, perkName = pn.GetPrettyName() };
            pb.stats = PerkStatDeriver.Heuristic(pn.GetPrettyName(), pn.GetPotentiallyNullDescription() ?? "", SkillConfig.HeuristicBudget);
            pb.derived = true;
            Data.perkBonuses[pn.uuid] = pb;
            if (SkillConfig.UseAIGeneration && _refineRequested.Add(pn.uuid)) _refineQueue.Enqueue(pn);
            return pb;
        }

        /// <summary>After the AI rewrites node content: stats may have changed, nothing structural did.</summary>
        public void RecalculateAfterContentChange()
        {
            if (Data == null) return;
            RecalculateStats();
            // A Keystone or Confluence that already granted its perk may just have been renamed:
            // the perk on the hotbar follows its node (unless the player renamed it themselves).
            if (!_syncing) SyncAbilities();
            SaveData();
        }

        /// <summary>
        /// Sums every allocated node's stats into CachedStats. Gates already carry their perks'
        /// stats (active perks pre-multiplied); nodes touching a gate with an active perk get the
        /// active multiplier too, and tiered nodes scale x1 / x1.5 / x2 with their tier.
        /// </summary>
        private void RecalculateStats()
        {
            Data.CachedStats.Clear();

            foreach (var node in Data.nodes)
            {
                if (!node.unlocked && node.ring > 0) continue;
                if (node.stats == null || node.stats.Count == 0) continue;

                float mult = 1f;
                if (!node.IsGate)
                {
                    foreach (var nid in node.edges)
                    {
                        var nb = Data.GetNode(nid);
                        if (nb != null && nb.IsGate && nb.hasActivePerk) { mult = SkillConfig.ActiveBonusMultiplier; break; }
                    }
                    if (node.IsTiered && node.tier > 0) mult *= 1f + (node.tier - 1) * 0.5f;
                }

                foreach (var kvp in node.stats)
                {
                    if (!Enum.TryParse(kvp.Key, true, out SS.PlayerAttribute attr) || attr == SS.PlayerAttribute.Unknown) continue;
                    Data.CachedStats[attr] = (Data.CachedStats.TryGetValue(attr, out float cur) ? cur : 0f) + kvp.Value * mult;
                }
            }

            var keys = new List<SS.PlayerAttribute>(Data.CachedStats.Keys);
            foreach (var k in keys)
                Data.CachedStats[k] = Mathf.Clamp(Data.CachedStats[k], -SkillConfig.MaxBonusPerAttribute, SkillConfig.MaxBonusPerAttribute);
        }
    }
}
