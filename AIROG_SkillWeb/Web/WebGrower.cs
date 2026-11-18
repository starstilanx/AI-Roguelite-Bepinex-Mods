using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AIROG_SkillWeb
{
    /// <summary>
    /// Owns the web's lifecycle: creation (sectors + WebGenerator), binding native perk trees to
    /// sectors, charting rings as the player reaches them, and the background AI refinement queue
    /// that rewrites each newly charted ring's Notables/Keystones/Masteries/Confluences in the
    /// voice of the current world.
    /// </summary>
    public static class WebGrower
    {
        private static bool _batchQueueRunning;
        private static readonly Queue<(string sectorId, int ring)> _refinementQueue = new Queue<(string sectorId, int ring)>();

        /// <summary>Largest AI batch; a vast web's outer ring can hold 15+ refinable nodes.</summary>
        private const int MaxBatch = 10;

        // ── Creation ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a fresh web. Sector count is fixed here for the life of the character: one per
        /// native perk tree, topped up with themed fallback sectors to <see cref="SkillWebConfig.SectorsAtStart"/>,
        /// capped at 8. Perk trees that appear later bind onto existing sectors (see BindNativeTrees),
        /// so the structure never has to be re-laid-out.
        /// </summary>
        public static void InitializeWeb(SkillWebData data, SkillWebConfig config, Dictionary<string, string> nativeTrees)
        {
            data.nodes.Clear();
            data.sectors.Clear();
            data.InvalidateIndex();
            // A web built from here on keeps its icons in its own seed folder (see NodeIconGen).
            // Safe to set for a first-time web as well: it has no icons anywhere yet.
            data.iconsPerWeb = true;

            int treeCount = nativeTrees?.Count ?? 0;
            int count = Mathf.Clamp(Math.Max(treeCount, config.SectorsAtStart), 3, 8);
            int rings = WebGenerator.RingsFor(config.WebSize);
            int charted = Mathf.Clamp(config.SeedRings, 1, rings);

            var usedAttrs = new Dictionary<string, int>();
            foreach (var a in ThemeLexicon.Attributes) usedAttrs[a] = 0;

            if (nativeTrees != null)
            {
                foreach (var kvp in nativeTrees)
                {
                    if (data.sectors.Count >= count) break;
                    var (attr, alt) = NodeGenOffline.PickAttributes(kvp.Value);
                    usedAttrs[attr]++;
                    data.sectors.Add(new WebSector
                    {
                        id = "sector_" + kvp.Key,
                        name = kvp.Value,
                        purpose = "Anchor star discipline grown from your native perks.",
                        attr = attr,
                        altAttr = alt,
                        chartedRing = charted,
                        perkTreeUuids = new List<string> { kvp.Key },
                    });
                }
            }

            // Top up with fallback themes, preferring attributes the perk trees didn't cover.
            var pool = new List<ThemeLexicon.FallbackSector>(ThemeLexicon.FallbackSectors);
            int fi = 0;
            while (data.sectors.Count < count && pool.Count > 0)
            {
                var pick = pool.OrderBy(f => usedAttrs[f.attr]).ThenBy(f => Array.IndexOf(ThemeLexicon.FallbackSectors, f)).First();
                pool.Remove(pick);
                usedAttrs[pick.attr]++;
                data.sectors.Add(new WebSector
                {
                    id = "sector_fallback_" + fi++,
                    name = pick.name,
                    purpose = pick.purpose,
                    attr = pick.attr,
                    altAttr = pick.alt,
                    chartedRing = charted,
                });
            }

            for (int i = 0; i < data.sectors.Count; i++)
                data.sectors[i].colorHex = ThemeLexicon.SectorColors[i % ThemeLexicon.SectorColors.Length];

            WebGenerator.Generate(data, rings);
            Debug.Log($"[SkillWeb] Generated web: {data.nodes.Count} nodes, {data.sectors.Count} sectors, {rings} rings (seed {data.layoutSeed}).");

            if (config.UseAIGeneration) EnqueueInitialRefinements(data);
        }

        /// <summary>
        /// Attaches native perk trees that no sector feeds from yet. A tree takes the first
        /// sector with no tree (which is renamed after it), else the sector whose attribute
        /// matches its theme, else the sector with the fewest trees. Returns true if anything changed.
        /// </summary>
        public static bool BindNativeTrees(SkillWebData data, Dictionary<string, string> nativeTrees)
        {
            if (data == null || nativeTrees == null || data.sectors.Count == 0) return false;
            bool changed = false;

            foreach (var kvp in nativeTrees)
            {
                if (data.sectors.Any(s => s.perkTreeUuids.Contains(kvp.Key))) continue;

                var (attr, _) = NodeGenOffline.PickAttributes(kvp.Value);
                var target = data.sectors.FirstOrDefault(s => s.perkTreeUuids.Count == 0)
                          ?? data.sectors.FirstOrDefault(s => s.attr == attr)
                          ?? data.sectors.OrderBy(s => s.perkTreeUuids.Count).First();

                if (target.perkTreeUuids.Count == 0)
                {
                    target.name = kvp.Value;
                    target.purpose = "Anchor star discipline grown from your native perks.";
                    var gate = data.GetNode("gate:" + target.id);
                    if (gate != null)
                    {
                        gate.name = target.name;
                        gate.description = "The Anchor Star of " + target.name + ". Perks you learn in its native perk tree feed it.";
                    }
                }
                target.perkTreeUuids.Add(kvp.Key);
                changed = true;
                Debug.Log($"[SkillWeb] Bound native perk tree '{kvp.Value}' to sector '{target.name}'.");
            }
            return changed;
        }

        // ── Charting (lazy growth) ───────────────────────────────────────────────

        /// <summary>
        /// Reveals the next ring of every sector whose outermost charted ring holds an allocated
        /// (or pending) node, repeatedly, and queues AI refinement for each newly charted ring.
        /// Idempotent; call after any allocation and on load. Returns the sectors that grew.
        /// </summary>
        public static List<WebSector> EnsureFrontierCharted(SkillWebData data, SkillWebConfig config, ICollection<string> pending = null)
        {
            var grown = new List<WebSector>();
            if (data == null) return grown;

            foreach (var sector in data.sectors)
            {
                while (sector.chartedRing < data.maxRing)
                {
                    int frontier = sector.chartedRing;
                    bool reached = data.nodes.Exists(n =>
                        n.ring == frontier && n.ring > 0 &&
                        (n.sectorId == sector.id || n.sectorId2 == sector.id) &&
                        (n.unlocked || (pending != null && pending.Contains(n.id))));
                    if (!reached) break;

                    sector.chartedRing++;
                    if (!grown.Contains(sector)) grown.Add(sector);
                    if (config.UseAIGeneration) EnqueueRefinement(sector.id, sector.chartedRing);
                }
            }
            return grown;
        }

        // ── AI Refinement Queue ──────────────────────────────────────────────────

        static bool Refinable(WebNode n) =>
            n.type == WebNodeType.Notable || n.type == WebNodeType.Keystone ||
            n.type == WebNodeType.Mastery || n.type == WebNodeType.Confluence;

        public static void EnqueueInitialRefinements(SkillWebData data)
        {
            foreach (var sector in data.sectors)
                for (int r = 1; r <= sector.chartedRing; r++)
                    EnqueueRefinement(sector.id, r);
        }

        /// <summary>
        /// Drops queued (sector, ring) refinements. They are keyed by sector id only, so after a
        /// different save loads or the web is regenerated they would point at whatever sector
        /// happens to reuse that id.
        /// </summary>
        public static void ClearRefinementQueue()
        {
            lock (_refinementQueue) _refinementQueue.Clear();
        }

        public static void EnqueueRefinement(string sectorId, int ring)
        {
            lock (_refinementQueue)
            {
                if (!_refinementQueue.Contains((sectorId, ring)))
                    _refinementQueue.Enqueue((sectorId, ring));

                if (!_batchQueueRunning)
                    _ = ProcessRefinementQueueAsync();
            }
        }

        private static async Task ProcessRefinementQueueAsync()
        {
            _batchQueueRunning = true;
            int failedInARow = 0;
            try
            {
                while (true)
                {
                    // Unfinished rings are re-queued on every load, so a backend that is down or
                    // returning rubbish must not be sent the whole web again each time: after two
                    // failed requests in a row, give up until the next session.
                    if (failedInARow >= 2)
                    {
                        Debug.LogWarning("[SkillWeb] AI refinement is failing; the remaining rings keep their generated text for now.");
                        ClearRefinementQueue();
                        break;
                    }

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

                    var pendingNodes = data.nodes.FindAll(n =>
                        n.sectorId == target.sectorId && n.ring == target.ring && !n.aiRefined && Refinable(n));
                    if (pendingNodes.Count == 0) continue;

                    for (int start = 0; start < pendingNodes.Count; start += MaxBatch)
                    {
                        var batch = pendingNodes.GetRange(start, Math.Min(MaxBatch, pendingNodes.Count - start));
                        Debug.Log($"[SkillWeb] AI refining {sector.name} ring {target.ring} ({batch.Count} nodes)...");

                        var infos = await NodeGenAI.RefineBatchAsync(manager, data, sector, target.ring, batch);
                        if (infos == null || infos.Count == 0)
                        {
                            if (++failedInARow >= 2) break;
                            continue;
                        }
                        failedInARow = 0;

                        // The player may have loaded another save while the request was in flight.
                        if (!ReferenceEquals(plugin.Data, data)) break;
                        // ...or regenerated this one: same data object, brand-new nodes that reuse
                        // the old ids. Writing the reply onto them would mislabel the new web.
                        if (batch.Exists(b => data.GetNode(b.id) != b)) break;

                        foreach (var info in infos)
                        {
                            var node = data.GetNode(info.id);
                            if (node == null) continue;
                            node.name = info.name;
                            node.description = info.description;
                            node.stats = info.stats;
                            node.traits = info.traits;
                            node.keystoneRule = info.keystoneRule;
                            if (!string.IsNullOrEmpty(info.abilityName)) node.abilityName = info.abilityName;
                            if (info.masteryOptions != null && info.masteryOptions.Count > 0) node.masteryOptions = info.masteryOptions;
                            node.aiRefined = true;
                        }

                        // Refinement only rewrites content, never structure, so no re-layout.
                        plugin.RecalculateAfterContentChange();
                        if (SkillWebUI.Instance != null && SkillWebUI.Instance.IsOpen)
                            SkillWebUI.Instance.Refresh();
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
    }
}
