using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace AIROG_SkillWeb
{
    // Serialized by ordinal: only ever APPEND new members, never reorder.
    public enum WebNodeType
    {
        Basic,
        Notable,
        Keystone,
        Anchor,
        Confluence,
        /// <summary>Pick-one-of-three trait hub; unlocked by any allocated node in its cluster, not by edges.</summary>
        Mastery,
        /// <summary>Small attribute node strung along the paths between clusters (PoE's "+10 Strength" nodes).</summary>
        Travel
    }

    [Serializable]
    public class WebNode
    {
        public string id;                       // "origin", "gate:<sectorId>", or "n<index>"
        public WebNodeType type;
        public string name;                     // procedural name
        public string description;              // lore description
        public string sectorId;                 // owning sector/discipline id
        public string sectorId2;                // Confluence only: the second sector it joins
        public int ring;                        // band index (0 = Heart / sector gates, 1..N outward)
        public int clusterId = -1;              // generator cluster; Masteries unlock off it
        public float angle;                     // polar angle in radians
        public float radius;                    // distance from center
        public float x;                         // Cartesian X (UI units, y up)
        public float y;                         // Cartesian Y
        public List<string> edges = new List<string>(); // adjacent node IDs (undirected)
        public Dictionary<string, float> stats = new Dictionary<string, float>(); // attribute -> bonus
        public List<string> traits = new List<string>(); // non-numerical narrative traits
        public string keystoneRule;             // rule directive for keystones
        public List<string> masteryOptions;     // Mastery only: the three traits on offer
        public int masteryChoice = -1;          // Mastery only: chosen option index, -1 = none yet
        public bool unlocked;
        public int tier;                        // 0 locked, 1..3 mastery tier
        public bool aiRefined;                  // true if AI refined
        public string originHook;               // provenance info e.g. "chronicle:<beat>"

        // ── Usable ability grant (Keystone / Confluence nodes) ────────────────────
        public string abilityName;              // name of the granted ability; falls back to the node name
        public string grantedAbilityUuid;      // stable uuid of the minted usable GameAbility
        public string grantedAbilityDesc;       // AI-generated ability flavor/rules text (cached)
        public int abilityCooldownRemaining;    // persisted cooldown so it survives save/load

        // ── Runtime-only (rebuilt by SyncBonuses) ─────────────────────────────────
        /// <summary>Gate only: learned native perks feeding this Anchor Star, for the UI.</summary>
        [JsonIgnore] public List<FeedingPerk> feedingPerks = new List<FeedingPerk>();
        /// <summary>Gate only: at least one feeding perk is currently activated.</summary>
        [JsonIgnore] public bool hasActivePerk;

        [JsonIgnore]
        public Vector2 Position => new Vector2(x, y);

        [JsonIgnore]
        public bool IsGate => type == WebNodeType.Anchor && id != null && id.StartsWith("gate:");

        [JsonIgnore]
        public bool IsOrigin => id == "origin";

        /// <summary>Nodes whose stats scale with mastery tier upgrades.</summary>
        [JsonIgnore]
        public bool IsTiered => type == WebNodeType.Basic || type == WebNodeType.Notable
                             || type == WebNodeType.Confluence || type == WebNodeType.Travel;
    }

    public class FeedingPerk
    {
        public string perkUuid;
        public string name;
        public bool active;
        public Dictionary<string, float> stats;
    }

    [Serializable]
    public class WebSector
    {
        public string id;
        public string name;
        public string purpose;
        public string colorHex;
        public string attr;                     // primary attribute
        public string altAttr;                  // secondary attribute
        public float angleCenter;               // radians, UI convention (y up)
        public float angleSpan;
        public int chartedRing;                 // rings 1..chartedRing are revealed
        public int deepestGeneratedRing;        // legacy (schema 4), unused
        public string anchorPerkTreeUuid;       // legacy (schema 4): single bound tree
        public List<string> perkTreeUuids = new List<string>(); // native PerkTrees feeding this sector's gate
    }

    public struct PerkSnapshot
    {
        public string uuid;
        public bool isActivated;
    }

    [Serializable]
    public class PerkBonus
    {
        public string perkUuid;
        public string perkName;
        public Dictionary<string, float> stats = new Dictionary<string, float>();
        public bool derived = false;
        public bool aiRefined = false;
    }

    public class SkillWebData
    {
        public const int CurrentSchema = 5;

        public int schemaVersion = CurrentSchema;
        public long layoutSeed;
        /// <summary>Which generator revision produced the stored structure.</summary>
        public int layoutVersion;
        /// <summary>Outermost ring the generator built (every sector shares it).</summary>
        public int maxRing;
        /// <summary>Radius of the outermost generated content, UI units.</summary>
        public float worldRadius;
        public int nextNodeIndex;
        public List<WebNode> nodes = new List<WebNode>();
        public List<WebSector> sectors = new List<WebSector>();
        public int resonance;
        public int resonanceEarnedTotal;
        public int turnsSurvived;
        public Dictionary<string, long> economyLedger = new Dictionary<string, long>();
        public Dictionary<string, PerkBonus> perkBonuses = new Dictionary<string, PerkBonus>();

        [JsonIgnore]
        public Dictionary<SS.PlayerAttribute, float> CachedStats = new Dictionary<SS.PlayerAttribute, float>();

        [JsonIgnore] private Dictionary<string, WebNode> _index;
        [JsonIgnore] private int _indexedCount = -1;

        // ── Persistence ─────────────────────────────────────────────────────────

        public static SkillWebData Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var data = JsonConvert.DeserializeObject<SkillWebData>(json);
                    if (data != null)
                    {
                        if (data.nodes == null) data.nodes = new List<WebNode>();
                        if (data.sectors == null) data.sectors = new List<WebSector>();
                        if (data.economyLedger == null) data.economyLedger = new Dictionary<string, long>();
                        if (data.perkBonuses == null) data.perkBonuses = new Dictionary<string, PerkBonus>();
                        foreach (var s in data.sectors)
                            if (s.perkTreeUuids == null) s.perkTreeUuids = new List<string>();

                        if (data.schemaVersion < CurrentSchema)
                        {
                            // Keep the old web: migration discards it, and a rollback to v4 needs it back.
                            try { File.Copy(path, path + ".schema" + data.schemaVersion + ".bak", overwrite: true); }
                            catch (Exception bex) { Debug.LogWarning($"[SkillWeb] Could not back up old web: {bex.Message}"); }
                            MigrateToCurrent(data);
                        }
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkillWeb] Load error: {ex.Message}");
                }
            }

            var newData = new SkillWebData();
            newData.layoutSeed = NewSeed();
            return newData;
        }

        public static long NewSeed()
        {
            var r = new System.Random();
            return ((long)r.Next() << 32) | (uint)r.Next();
        }

        /// <summary>
        /// Older webs (the ring-by-ring constellation) can't be mapped onto the procedural web, so
        /// the structure is discarded and regenerated. Everything the player spent on it comes back
        /// as Resonance; the ledger, perk bonuses and turn count carry over untouched.
        /// </summary>
        static void MigrateToCurrent(SkillWebData data)
        {
            int refund = 0;
            if (data.schemaVersion >= 4)
            {
                foreach (var n in data.nodes)
                {
                    if (!n.unlocked || n.ring == 0 || n.type == WebNodeType.Anchor) continue;
                    // v4 prices: Basic 1, Notable/Confluence 2, Keystone 3; tier upgrades 1 then 2.
                    refund += n.type == WebNodeType.Keystone ? 3
                            : (n.type == WebNodeType.Notable || n.type == WebNodeType.Confluence) ? 2 : 1;
                    for (int t = 1; t < n.tier; t++) refund += t;
                }
            }
            Debug.Log($"[SkillWeb] Migrating save from schema {data.schemaVersion} to {CurrentSchema}: web regenerated, {refund} Resonance refunded.");
            data.resonance += refund;
            data.layoutSeed = NewSeed();
            data.nodes.Clear();
            data.sectors.Clear();
            data.maxRing = 0;
            data.nextNodeIndex = 0;
            data.schemaVersion = CurrentSchema;
            data.InvalidateIndex();
        }

        public void Save(string path)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    // A vast web is ~1,200 nodes; indenting roughly doubles the file for no benefit.
                    Formatting = Formatting.None,
                    NullValueHandling = NullValueHandling.Ignore,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(this, settings));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] Save error: {ex.Message}");
            }
        }

        // ── Queries ─────────────────────────────────────────────────────────────

        /// <summary>O(1) node lookup. The index self-heals whenever the node list grows or shrinks.</summary>
        public WebNode GetNode(string id)
        {
            if (id == null) return null;
            if (_index == null || _indexedCount != nodes.Count) RebuildIndex();
            if (_index.TryGetValue(id, out var node)) return node;
            // The list may have been edited in place without a count change; fall back once.
            node = nodes.Find(n => n.id == id);
            if (node != null) RebuildIndex();
            return node;
        }

        public void InvalidateIndex() { _index = null; _indexedCount = -1; }

        void RebuildIndex()
        {
            _index = new Dictionary<string, WebNode>(nodes.Count);
            foreach (var n in nodes)
                if (n?.id != null) _index[n.id] = n;
            _indexedCount = nodes.Count;
        }

        public WebSector GetSector(string id)
        {
            return id == null ? null : sectors.Find(s => s.id == id);
        }

        /// <summary>Revealed on the map. Confluences are charted once either side reaches their ring.</summary>
        public bool IsCharted(WebNode node)
        {
            if (node == null) return false;
            if (node.ring <= 0) return true;
            var a = GetSector(node.sectorId);
            if (a != null && node.ring <= a.chartedRing) return true;
            if (node.type == WebNodeType.Confluence)
            {
                var b = GetSector(node.sectorId2);
                if (b != null && node.ring <= b.chartedRing) return true;
            }
            return a == null && node.type != WebNodeType.Confluence;
        }
    }
}
