using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIROG_SkillWeb
{
    [Serializable]
    public class SkillNode
    {
        public string id;
        public string name;
        public string description;
        public Vector2 position;
        public List<string> connectedIds = new List<string>();

        /// <summary>Stat name → base bonus amount (applied * tier when unlocked).</summary>
        public Dictionary<string, float> statModifiers = new Dictionary<string, float>();

        public bool isUnlocked = false;

        /// <summary>0 = locked, 1/2/3 = mastery tiers.</summary>
        public int tier = 0;

        public string imageUuid;
        public string treeId;
        public List<string> narrativeTraits = new List<string>();

        /// <summary>Milestone nodes are generated at level milestones and have richer effects.</summary>
        public bool isMilestone = false;

        public SkillNode() { }

        public SkillNode(string id, string name, string description, Vector2 position)
        {
            this.id          = id;
            this.name        = name;
            this.description = description;
            this.position    = position;
        }
    }

    [Serializable]
    public class SkillTree
    {
        public string id;
        public string name;
        public string purpose;
        public string colorHex = "#FFFFFF";
        public int nodesUnlocked = 0;

        public SkillTree() { }

        public SkillTree(string id, string name, string purpose)
        {
            this.id      = id;
            this.name    = name;
            this.purpose = purpose;
        }
    }

    public class SkillWebData
    {
        public List<SkillNode> nodes = new List<SkillNode>();
        public List<SkillTree> trees = new List<SkillTree>();

        public float nodeRadius = 50f;
        public int   skillPoints = 0;
        public int   totalPointsEarned = 0;
        public int   totalNodesUnlocked = 0;
        public int   lastKnownLevel = 1;

        /// <summary>Active narrative traits toggled on by the player.</summary>
        public HashSet<string> activeAffixes = new HashSet<string>();

        /// <summary>Accumulated stat bonuses from all unlocked nodes. Not serialized — rebuilt on load.</summary>
        [JsonIgnore]
        public Dictionary<SS.PlayerAttribute, float> CachedStats = new Dictionary<SS.PlayerAttribute, float>();

        // ── Persistence ─────────────────────────────────────────────────────────

        public static SkillWebData Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    var settings = new JsonSerializerSettings();
                    settings.Converters.Add(new Vector2Converter());
                    var data = JsonConvert.DeserializeObject<SkillWebData>(File.ReadAllText(path), settings);
                    if (data != null) return data;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkillWeb] Load error: {ex.Message}");
                }
            }
            return new SkillWebData();
        }

        public void Save(string path)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
                settings.Converters.Add(new Vector2Converter());
                File.WriteAllText(path, JsonConvert.SerializeObject(this, settings));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkillWeb] Save error: {ex.Message}");
            }
        }

        // ── Graph helpers ────────────────────────────────────────────────────────

        public bool CheckCollision(Vector2 pos, string excludeId = null)
        {
            foreach (var n in nodes)
            {
                if (n.id == excludeId) continue;
                if (Vector2.Distance(n.position, pos) < nodeRadius * 2.2f) return true;
            }
            return false;
        }

        /// <summary>Adds a node only if the position is collision-free. Returns false on collision.</summary>
        public bool TryAddNode(SkillNode node)
        {
            if (CheckCollision(node.position, node.id))
            {
                Debug.LogWarning($"[SkillWeb] Collision — could not place '{node.name}' at {node.position}");
                return false;
            }
            nodes.Add(node);
            return true;
        }

        public void AddConnection(string id1, string id2)
        {
            var n1 = nodes.Find(n => n.id == id1);
            var n2 = nodes.Find(n => n.id == id2);
            if (n1 != null && !n1.connectedIds.Contains(id2)) n1.connectedIds.Add(id2);
            if (n2 != null && !n2.connectedIds.Contains(id1)) n2.connectedIds.Add(id1);
        }

        public SkillTree GetTree(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return trees.Find(t => t.id == id);
        }

        // ── Unlock / upgrade logic ───────────────────────────────────────────────

        /// <summary>
        /// True if <paramref name="node"/> is locked but adjacent to at least one unlocked node,
        /// OR it is an isolated root node (no connections yet).
        /// </summary>
        public bool CanUnlock(SkillNode node)
        {
            if (node.isUnlocked) return false;
            if (node.connectedIds.Count == 0) return true;  // floating root — always reachable
            foreach (var id in node.connectedIds)
            {
                var neighbor = nodes.Find(n => n.id == id);
                if (neighbor != null && neighbor.isUnlocked) return true;
            }
            return false;
        }

        /// <summary>True if node is unlocked and below tier 3.</summary>
        public bool CanUpgrade(SkillNode node) => node.isUnlocked && node.tier < 3;

        /// <summary>
        /// Returns the point cost to unlock (if locked) or upgrade (if already unlocked).
        /// Unlock = NodeCost. Upgrade T1→T2 = UpgradeCost×1, T2→T3 = UpgradeCost×2.
        /// </summary>
        public int UnlockCost(SkillNode node)
        {
            if (!node.isUnlocked) return SkillWebPlugin.Instance.SkillConfig.NodeCost;
            return SkillWebPlugin.Instance.SkillConfig.UpgradeCost * node.tier;
        }

        /// <summary>Spends points and unlocks the node. Returns false if not unlockable or insufficient points.</summary>
        public bool TryUnlock(SkillNode node)
        {
            if (!CanUnlock(node)) return false;
            int cost = UnlockCost(node);
            if (skillPoints < cost) return false;

            skillPoints -= cost;
            node.isUnlocked = true;
            node.tier = 1;
            totalNodesUnlocked++;
            var tree = GetTree(node.treeId);
            if (tree != null) tree.nodesUnlocked++;
            RecalculateStats();
            return true;
        }

        /// <summary>Spends points and upgrades the node to the next tier. Returns false if not upgradeable or insufficient points.</summary>
        public bool TryUpgrade(SkillNode node)
        {
            if (!CanUpgrade(node)) return false;
            int cost = UnlockCost(node);
            if (skillPoints < cost) return false;

            skillPoints -= cost;
            node.tier++;
            RecalculateStats();
            return true;
        }

        // ── Stat accumulation ────────────────────────────────────────────────────

        public void RecalculateStats()
        {
            if (CachedStats == null) CachedStats = new Dictionary<SS.PlayerAttribute, float>();
            CachedStats.Clear();

            foreach (var node in nodes)
            {
                if (!node.isUnlocked || node.statModifiers == null) continue;
                float tierMult = node.tier; // T1 = 1×, T2 = 2×, T3 = 3×
                foreach (var kvp in node.statModifiers)
                {
                    if (Enum.TryParse(kvp.Key, true, out SS.PlayerAttribute attr))
                    {
                        if (!CachedStats.ContainsKey(attr)) CachedStats[attr] = 0;
                        CachedStats[attr] += kvp.Value * tierMult;
                    }
                }
            }
        }

        // ── AI context ───────────────────────────────────────────────────────────

        /// <summary>Returns a formatted block of all active narrative traits for AI context injection.</summary>
        public string GetActiveTraitsBlock()
        {
            var sb = new StringBuilder();
            foreach (var node in nodes)
            {
                if (!node.isUnlocked || node.narrativeTraits == null) continue;
                foreach (var trait in node.narrativeTraits)
                    sb.AppendLine($"  - {trait} (from {node.name}, Tier {node.tier})");
            }
            return sb.ToString().Trim();
        }
    }

    // ── JSON converter for UnityEngine.Vector2 ──────────────────────────────────

    public class Vector2Converter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var v = (Vector2)value;
            new JObject { ["x"] = v.x, ["y"] = v.y }.WriteTo(writer);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var jo = JObject.Load(reader);
            return new Vector2(jo["x"].Value<float>(), jo["y"].Value<float>());
        }

        public override bool CanConvert(Type objectType) => objectType == typeof(Vector2);
    }
}
